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
    const first = await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token-1');
    expect(first).toEqual({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'token-1' });
    expect(calls[0]).toEqual({ url: 'https://relay.example.invalid/push/v1/handles', method: 'POST', body: JSON.stringify({ fcmToken: 'token-1' }) });
    expect(await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token-1')).toEqual(first);
    expect(calls).toHaveLength(1);
    await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token-2');
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
    expect(made).toEqual({ handle: other, secret: SECRET, relayUrl: 'wss://new.example.invalid', fcmToken: 't' });
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
    expect(await syncHostPush({ store, fetch: relay([], []), client: none }, { id: 'a', relayUrl: 'wss://r' }, 't', ['completed'])).toBe('unsupported');
    const legacy: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(['push']),
      registerPush: async () => { throw new ApiError(409, 'needs token'); },
    };
    expect(await syncHostPush({ store, fetch: relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }], []), client: legacy }, { id: 'a', relayUrl: 'wss://r' }, 't', ['completed'])).toBe('unsupported');
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
