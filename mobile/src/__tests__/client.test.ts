import { ApiError, byteLength, createClient, describeError, needsRepair } from '@/api/client';
import { RelayError } from '@/api/relay/transport';
import type { RelayChannel } from '@/api/client';
import type { RelayNotification } from '@/api/relay/transport';

interface Recorded {
  method: 'GET' | 'POST';
  path: string;
  body?: unknown;
}

function fakeChannel(response: { status: number; body: unknown }) {
  const calls: Recorded[] = [];
  const listeners = new Set<(event: RelayNotification) => void>();
  const channel: RelayChannel = {
    request: (method, path, body) => {
      calls.push({ method, path, body });
      return Promise.resolve(response);
    },
    onNotify: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
  return { channel, calls, emit: (event: RelayNotification) => listeners.forEach((l) => l(event)) };
}

const okState = { status: 200, body: { protocol: 1, revision: 7, hostName: 'h', workspaces: [], sessions: [] } };

describe('createClient over a relay channel', () => {
  it('clamps the long-poll wait and passes since inside the tunnelled path', async () => {
    const fake = fakeChannel(okState);
    const client = createClient(fake.channel);
    await client.state({ since: 3, wait: 99 });
    await client.state({ since: 3, wait: -5 });
    await client.state();
    expect(fake.calls.map((call) => call.path)).toEqual([
      '/m1/state?since=3&wait=10',
      '/m1/state?since=3&wait=0',
      '/m1/state?wait=0',
    ]);
    expect(fake.calls.every((call) => call.method === 'GET')).toBe(true);
  });

  it('percent-encodes path segments and sends bodies verbatim', async () => {
    const fake = fakeChannel({ status: 200, body: { protocol: 1, accepted: 'started' } });
    const client = createClient(fake.channel);
    await client.submit('a/b c', 'hi');
    await client.answer('s1', {
      requestId: 'r1',
      runId: 'run1',
      answers: { '어떻게 할까요?': { selectedOptions: ['계속'], customText: '빠르게' } },
    });
    expect(fake.calls[0]).toEqual({
      method: 'POST',
      path: '/m1/sessions/a%2Fb%20c/submit',
      body: { text: 'hi' },
    });
    expect(fake.calls[1]?.path).toBe('/m1/sessions/s1/answers');
    expect(fake.calls[1]?.body).toEqual({
      requestId: 'r1',
      runId: 'run1',
      answers: { '어떻게 할까요?': { selectedOptions: ['계속'], customText: '빠르게' } },
    });
  });

  it('routes the remaining m1 routes unchanged', async () => {
    const fake = fakeChannel({ status: 200, body: { protocol: 1, ok: true } });
    const client = createClient(fake.channel);
    await client.info();
    await client.session('s1', { since: 2, wait: 10 });
    await client.stop('s1');
    await client.respondPermission('s1', { requestId: 'r', runId: 'run', allow: true });
    await client.createSession('ws1', { kind: 'claude', provider: 'codex' });
    expect(fake.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'GET /m1/info',
      'GET /m1/sessions/s1?since=2&wait=10',
      'POST /m1/sessions/s1/stop',
      'POST /m1/sessions/s1/permission',
      'POST /m1/workspaces/ws1/sessions',
    ]);
  });

  it('turns non-2xx statuses into ApiError with the host message', async () => {
    const fake = fakeChannel({ status: 409, body: { protocol: 1, error: '세션이 실행 중이 아닙니다' } });
    await expect(createClient(fake.channel).stop('s1')).rejects.toMatchObject({
      name: 'ApiError',
      status: 409,
      message: '세션이 실행 중이 아닙니다',
    });
  });

  it('flags 401 as needing re-pairing', async () => {
    const fake = fakeChannel({ status: 401, body: { protocol: 1, error: 'unauthorized' } });
    const error = await createClient(fake.channel)
      .info()
      .catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(ApiError);
    expect(needsRepair(error)).toBe(true);
    expect(describeError(error)).toBe('재페어링 필요');
  });

  it('rejects oversized submissions before touching the tunnel', async () => {
    const fake = fakeChannel(okState);
    await expect(
      createClient(fake.channel).submit('s1', 'x'.repeat(32 * 1024 + 1)),
    ).rejects.toBeInstanceOf(ApiError);
    expect(fake.calls).toHaveLength(0);
  });

  it('honours an abort signal without cancelling the host-side work', async () => {
    const fake = fakeChannel(okState);
    const controller = new AbortController();
    controller.abort();
    await expect(createClient(fake.channel).state({ signal: controller.signal })).rejects.toThrow(
      /취소/,
    );
  });

  it('forwards notify events to subscribers', () => {
    const fake = fakeChannel(okState);
    const seen: RelayNotification[] = [];
    const off = createClient(fake.channel).onNotify((event) => seen.push(event));
    fake.emit({ scope: 'session:s1', revision: 9 });
    off();
    fake.emit({ scope: 'session:s1', revision: 10 });
    expect(seen).toEqual([{ scope: 'session:s1', revision: 9 }]);
  });
});

describe('describeError / needsRepair', () => {
  it('maps relay failures to Korean status text', () => {
    expect(describeError(new RelayError('host-offline'))).toBe('호스트 오프라인');
    expect(describeError(new RelayError('relay-unreachable'))).toBe('릴레이 연결 안 됨');
    expect(describeError(new RelayError('unpaired'))).toBe('재페어링 필요');
    expect(needsRepair(new RelayError('unpaired'))).toBe(true);
    expect(needsRepair(new RelayError('host-offline'))).toBe(false);
  });
});

describe('byteLength', () => {
  it('counts utf-8 bytes', () => {
    expect(byteLength('abc')).toBe(3);
    expect(byteLength('한글')).toBe(6);
    expect(byteLength('😀')).toBe(4);
  });
});
