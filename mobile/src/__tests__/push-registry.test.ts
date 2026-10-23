import { ApiError, createClient, type MobileClient, type RelayChannel } from '@/api/client';
import type { HostInfo, OkResponse } from '@/api/types';
import type { SecretStore } from '@/lib/host-secrets';
import {
  ensurePushHandle,
  findHostByHandle,
  forgetPushHandle,
  readPushHandle,
  relayHttpBase,
  syncHostPush,
  PUSH_HANDLE_CHECK_MS,
  PUSH_RELAY_TIMEOUT_MS,
  type PushFetch,
} from '@/lib/push/registry';

function memoryStore(): SecretStore & { items: Map<string, string> } {
  const items = new Map<string, string>();
  return {
    items,
    getItemAsync: async (key) => items.get(key) ?? null,
    setItemAsync: async (key, value) => void items.set(key, value),
    deleteItemAsync: async (key) => void items.delete(key),
  };
}

interface Call { url: string; method: string; body?: string; auth?: string }

function relay(answers: Array<{ status: number; body?: unknown }>, calls: Call[]): PushFetch {
  return async (url, init) => {
    const call: Call = { url, method: init.method };
    if (init.body !== undefined) call.body = init.body;
    const auth = init.headers['authorization'];
    if (auth !== undefined) call.auth = auth;
    calls.push(call);
    const next = answers.shift() ?? { status: 500 };
    return { status: next.status, json: async () => next.body };
  };
}

const HANDLE = 'h'.repeat(43);
const SECRET = 's'.repeat(43);

describe('relayHttpBase', () => {
  it('follows the pairing relay scheme', () => {
    expect(relayHttpBase('wss://relay.example.invalid')).toBe('https://relay.example.invalid');
    expect(relayHttpBase('ws://127.0.0.1:8787/ws')).toBe('http://127.0.0.1:8787');
    expect(relayHttpBase('ftp://relay.example.invalid')).toBeNull();
  });
});

describe('push handles', () => {
  it('registers once, then refreshes only when the FCM token changes', async () => {
    const store = memoryStore();
    const calls: Call[] = [];
    const fetch = relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }, { status: 204 }], calls);
    const now = () => 5_000;
    const first = await ensurePushHandle({ store, fetch, now }, 'host-a', 'wss://relay.example.invalid', 'token-1');
    expect(first).toEqual({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'token-1', checkedAt: 5_000 });
    expect(calls[0]).toEqual({ url: 'https://relay.example.invalid/push/v1/handles', method: 'POST', body: JSON.stringify({ fcmToken: 'token-1' }) });
    expect(await ensurePushHandle({ store, fetch, now }, 'host-a', 'wss://relay.example.invalid', 'token-1')).toEqual(first);
    expect(calls).toHaveLength(1);
    await ensurePushHandle({ store, fetch, now }, 'host-a', 'wss://relay.example.invalid', 'token-2');
    expect(calls[1]).toEqual({ url: `https://relay.example.invalid/push/v1/handles/${HANDLE}`, method: 'PUT', body: JSON.stringify({ fcmToken: 'token-2' }), auth: `Bearer ${SECRET}` });
    expect((await readPushHandle(store, 'host-a'))?.fcmToken).toBe('token-2');
  });

  it('registers again when the relay forgot the handle', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'old' }));
    const calls: Call[] = [];
    const other = 'o'.repeat(43);
    const fetch = relay([{ status: 404 }, { status: 201, body: { handle: other, secret: SECRET } }], calls);
    expect((await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'new'))?.handle).toBe(other);
    expect(calls.map((c) => c.method)).toEqual(['PUT', 'POST']);
  });

  it('drops the handle on the old relay and registers on the new one when the pairing relay changed', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://old.example.invalid', fcmToken: 't' }));
    const calls: Call[] = [];
    const other = 'o'.repeat(43);
    const fetch = relay([{ status: 204 }, { status: 201, body: { handle: other, secret: SECRET } }], calls);
    const made = await ensurePushHandle({ store, fetch }, 'host-a', 'wss://new.example.invalid', 't');
    expect(made).toEqual({ handle: other, secret: SECRET, relayUrl: 'wss://new.example.invalid', fcmToken: 't', checkedAt: expect.any(Number) });
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      `DELETE https://old.example.invalid/push/v1/handles/${HANDLE}`,
      'POST https://new.example.invalid/push/v1/handles',
    ]);
  });

  it('keeps nothing when the relay answers with a malformed handle', async () => {
    const store = memoryStore();
    const fetch = relay([{ status: 201, body: { handle: 'short', secret: SECRET } }], []);
    expect(await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 't')).toBeNull();
    expect(store.items.size).toBe(0);
  });

  it('forgets a handle on the relay and on the phone', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 't' }));
    const calls: Call[] = [];
    await forgetPushHandle({ store, fetch: relay([{ status: 204 }], calls) }, 'host-a');
    expect(calls[0]).toEqual({ url: `https://relay.example.invalid/push/v1/handles/${HANDLE}`, method: 'DELETE', auth: `Bearer ${SECRET}` });
    expect(await readPushHandle(store, 'host-a')).toBeNull();
  });

  it('finds the pairing a push came for by its handle', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-b', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://r', fcmToken: 't' }));
    expect(await findHostByHandle(store, ['host-a', 'host-b'], HANDLE)).toBe('host-b');
    expect(await findHostByHandle(store, ['host-a', 'host-b'], 'x'.repeat(43))).toBeNull();
  });
});

describe('an unchanged handle', () => {
  const DAY = PUSH_HANDLE_CHECK_MS;
  const saved = (checkedAt?: number) =>
    JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'token', ...(checkedAt === undefined ? {} : { checkedAt }) });

  it('is not asked about again within a day', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', saved(1_000));
    const calls: Call[] = [];
    const record = await ensurePushHandle({ store, fetch: relay([], calls), now: () => 1_000 + DAY - 1 }, 'host-a', 'wss://relay.example.invalid', 'token');
    expect(record?.handle).toBe(HANDLE);
    expect(calls).toHaveLength(0);
  });

  it('is confirmed with the relay once a day, and a confirmed handle is kept', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', saved(1_000));
    const calls: Call[] = [];
    const record = await ensurePushHandle({ store, fetch: relay([{ status: 204 }], calls), now: () => 1_000 + DAY }, 'host-a', 'wss://relay.example.invalid', 'token');
    expect(record).toEqual({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'token', checkedAt: 1_000 + DAY });
    expect(calls).toEqual([{ url: `https://relay.example.invalid/push/v1/handles/${HANDLE}`, method: 'PUT', body: JSON.stringify({ fcmToken: 'token' }), auth: `Bearer ${SECRET}` }]);
    expect((await readPushHandle(store, 'host-a'))?.checkedAt).toBe(1_000 + DAY);
  });

  it('saved before the check was kept is confirmed on the next sync', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', saved());
    expect((await readPushHandle(store, 'host-a'))?.checkedAt).toBe(0);
    const calls: Call[] = [];
    await ensurePushHandle({ store, fetch: relay([{ status: 204 }], calls), now: () => 10 }, 'host-a', 'wss://relay.example.invalid', 'token');
    expect(calls.map((c) => c.method)).toEqual(['PUT']);
  });

  it('is kept when the relay is busy or unreachable, and asked about again next time', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', saved(1));
    expect((await ensurePushHandle({ store, fetch: relay([{ status: 503 }], []), now: () => 1 + DAY }, 'host-a', 'wss://relay.example.invalid', 'token'))?.handle).toBe(HANDLE);
    const offline: PushFetch = async () => { throw new Error('offline'); };
    expect((await ensurePushHandle({ store, fetch: offline, now: () => 1 + DAY }, 'host-a', 'wss://relay.example.invalid', 'token'))?.handle).toBe(HANDLE);
    expect((await readPushHandle(store, 'host-a'))?.checkedAt).toBe(1);
  });

  it('the relay lost is replaced, and the host gets the new handle', async () => {
    const info = (): HostInfo => ({ protocol: 1, hostId: 'h', hostName: 'H', appVersion: '1', platform: 'darwin', capabilities: ['push'] } as HostInfo);
    const ok: OkResponse = { protocol: 1, ok: true } as OkResponse;
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', saved(1_000));
    const calls: Call[] = [];
    const other = 'o'.repeat(43);
    const fetch = relay([{ status: 404 }, { status: 201, body: { handle: other, secret: SECRET } }], calls);
    const registered: string[] = [];
    const client: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(),
      registerPush: async (input) => { registered.push(input.handle); return ok; },
    };
    const result = await syncHostPush({ store, fetch, client, now: () => 1_000 + 2 * DAY }, { id: 'host-a', relayUrl: 'wss://relay.example.invalid' }, 'token', ['completed']);
    expect(result).toBe('registered');
    expect(calls.map((c) => c.method)).toEqual(['PUT', 'POST']);
    expect(registered).toEqual([other]);
    expect((await readPushHandle(store, 'host-a'))?.handle).toBe(other);
  });
});

describe('syncHostPush', () => {
  const info = (capabilities: string[]): HostInfo => ({ protocol: 1, hostId: 'h', hostName: 'H', appVersion: '1', platform: 'darwin', capabilities } as HostInfo);
  const ok: OkResponse = { protocol: 1, ok: true } as OkResponse;

  it('gives the host the handle and the kinds', async () => {
    const store = memoryStore();
    const registered: unknown[] = [];
    const client: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(['push']),
      registerPush: async (input) => { registered.push(input); return ok; },
    };
    const fetch = relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }], []);
    expect(await syncHostPush({ store, fetch, client }, { id: 'host-a', relayUrl: 'wss://relay.example.invalid' }, 'token', ['permission', 'completed'])).toBe('registered');
    expect(registered).toEqual([{ handle: HANDLE, kinds: ['permission', 'completed'] }]);
  });

  it('leaves a host without push alone, and a legacy pairing is unsupported', async () => {
    const store = memoryStore();
    const none: Pick<MobileClient, 'info' | 'registerPush'> = { info: async () => info([]), registerPush: async () => ok };
    const noCalls: Call[] = [];
    expect(await syncHostPush({ store, fetch: relay([], noCalls), client: none }, { id: 'a', relayUrl: 'wss://r' }, 't', ['completed'])).toBe('unsupported');
    expect(noCalls).toHaveLength(0);
    expect(store.items.size).toBe(0);
    const legacy: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(['push']),
      registerPush: async () => { throw new ApiError(409, 'needs token'); },
    };
    expect(await syncHostPush({ store, fetch: relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }], []), client: legacy }, { id: 'a', relayUrl: 'wss://r' }, 't', ['completed'])).toBe('unsupported');
  });
});

describe('one pairing at a time', () => {
  const info = (): HostInfo => ({ protocol: 1, hostId: 'h', hostName: 'H', appVersion: '1', platform: 'darwin', capabilities: ['push'] } as HostInfo);
  const ok: OkResponse = { protocol: 1, ok: true } as OkResponse;

  it('two syncs for one pairing share one relay handle', async () => {
    const store = memoryStore();
    const calls: Call[] = [];
    const fetch = relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }, { status: 201, body: { handle: 'o'.repeat(43), secret: SECRET } }], calls);
    const registered: string[] = [];
    const client: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(),
      registerPush: async (input) => { registered.push(input.handle); return ok; },
    };
    const host = { id: 'host-a', relayUrl: 'wss://relay.example.invalid' };
    const results = await Promise.all([
      syncHostPush({ store, fetch, client }, host, 'token', ['completed']),
      syncHostPush({ store, fetch, client }, host, 'token', ['completed']),
    ]);
    expect(results).toEqual(['registered', 'registered']);
    expect(calls.filter((c) => c.method === 'POST')).toHaveLength(1);
    expect(registered).toEqual([HANDLE, HANDLE]);
  });

  it('a removal during a sync leaves no record behind and deletes the new handle on the relay', async () => {
    const store = memoryStore();
    const calls: Call[] = [];
    let answerPost: (value: { status: number; json(): Promise<unknown> }) => void = () => undefined;
    const fetch: PushFetch = (url, init) => {
      calls.push({ url, method: init.method });
      if (init.method === 'POST') return new Promise((resolve) => { answerPost = resolve; });
      return Promise.resolve({ status: 204, json: async () => undefined });
    };
    const client: Pick<MobileClient, 'info' | 'registerPush'> = { info: async () => info(), registerPush: async () => ok };
    const sync = syncHostPush({ store, fetch, client }, { id: 'host-a', relayUrl: 'wss://relay.example.invalid' }, 'token', ['completed']);
    await new Promise((resolve) => setImmediate(resolve));
    const forget = forgetPushHandle({ store, fetch }, 'host-a');
    answerPost({ status: 201, json: async () => ({ handle: HANDLE, secret: SECRET }) });
    await sync;
    await forget;
    expect(await readPushHandle(store, 'host-a')).toBeNull();
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      'POST https://relay.example.invalid/push/v1/handles',
      `DELETE https://relay.example.invalid/push/v1/handles/${HANDLE}`,
    ]);
  });
});

describe('a relay that never answers', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('gives up after the timeout instead of hanging', async () => {
    const store = memoryStore();
    const fetch: PushFetch = (_url, init) =>
      new Promise((_resolve, reject) => {
        init.signal?.addEventListener('abort', () => reject(new Error('aborted')));
      });
    const pending = ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token');
    await jest.advanceTimersByTimeAsync(PUSH_RELAY_TIMEOUT_MS);
    await expect(pending).resolves.toBeNull();
    expect(store.items.size).toBe(0);
  });
});

describe('push routes on the host', () => {
  it('posts register, unregister and viewing to the m1 push routes', async () => {
    const calls: Array<{ method: string; path: string; body?: unknown }> = [];
    const channel: RelayChannel = {
      request: (method, path, body) => {
        calls.push({ method, path, body });
        return Promise.resolve({ status: 200, body: { protocol: 1, ok: true } });
      },
      onNotify: () => () => undefined,
    };
    const client = createClient(channel);
    await client.registerPush({ handle: HANDLE, kinds: ['permission'] });
    await client.unregisterPush();
    await client.reportViewing('pane-1');
    await client.reportViewing(null);
    expect(calls).toEqual([
      { method: 'POST', path: '/m1/push/register', body: { handle: HANDLE, kinds: ['permission'] } },
      { method: 'POST', path: '/m1/push/unregister', body: {} },
      { method: 'POST', path: '/m1/push/viewing', body: { sessionId: 'pane-1' } },
      { method: 'POST', path: '/m1/push/viewing', body: { sessionId: null } },
    ]);
  });
});
