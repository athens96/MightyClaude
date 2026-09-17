import { ApiError, NetworkError, byteLength, createClient } from '@/api/client';

interface Recorded {
  url: string;
  init: RequestInit;
}

function mockFetch(
  body: unknown,
  status = 200,
): { calls: Recorded[]; restore: () => void } {
  const calls: Recorded[] = [];
  const original = globalThis.fetch;
  const fake = jest.fn(async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    return {
      ok: status >= 200 && status < 300,
      status,
      text: async () => JSON.stringify(body),
    } as Response;
  });
  globalThis.fetch = fake as unknown as typeof fetch;
  return { calls, restore: () => (globalThis.fetch = original) };
}

const credentials = { host: '100.64.1.2', port: 43138, key: 'secret-key' };

describe('createClient', () => {
  it('builds urls against the host origin', () => {
    const client = createClient(credentials);
    expect(client.baseUrl).toBe('http://100.64.1.2:43138');
    expect(client.buildUrl('/m1/info')).toBe('http://100.64.1.2:43138/m1/info');
  });

  it('sends the bearer key and protocol header, and no Origin', () => {
    const headers = createClient(credentials).buildHeaders(true);
    expect(headers.Authorization).toBe('Bearer secret-key');
    expect(headers['x-mighty-mobile-version']).toBe('1');
    expect(headers['Content-Type']).toBe('application/json');
    expect(headers.Origin).toBeUndefined();
  });

  it('omits Content-Type for bodyless requests', () => {
    expect(createClient(credentials).buildHeaders(false)['Content-Type']).toBeUndefined();
  });

  it('clamps the long-poll wait and passes since', async () => {
    const mock = mockFetch({ protocol: 1, revision: 7, hostName: 'h', workspaces: [], sessions: [] });
    const client = createClient(credentials);
    await client.state({ since: 3, wait: 99 });
    await client.state({ since: 3, wait: -5 });
    await client.state();
    mock.restore();
    expect(mock.calls.map((call) => call.url)).toEqual([
      'http://100.64.1.2:43138/m1/state?since=3&wait=10',
      'http://100.64.1.2:43138/m1/state?since=3&wait=0',
      'http://100.64.1.2:43138/m1/state?wait=0',
    ]);
  });

  it('percent-encodes path segments', async () => {
    const mock = mockFetch({ protocol: 1, accepted: 'started' });
    await createClient(credentials).submit('a/b c', 'hi');
    mock.restore();
    expect(mock.calls[0]?.url).toBe('http://100.64.1.2:43138/m1/sessions/a%2Fb%20c/submit');
    expect(mock.calls[0]?.init.method).toBe('POST');
    expect(mock.calls[0]?.init.body).toBe(JSON.stringify({ text: 'hi' }));
  });

  it('posts permission and answer payloads verbatim', async () => {
    const mock = mockFetch({ protocol: 1, ok: true });
    const client = createClient(credentials);
    await client.respondPermission('s1', { requestId: 'r1', runId: 'run1', allow: true });
    await client.answer('s1', {
      requestId: 'r1',
      runId: 'run1',
      answers: { '어떻게 할까요?': { selectedOptions: ['계속'], customText: '빠르게' } },
    });
    mock.restore();
    expect(mock.calls[0]?.init.body).toBe(
      JSON.stringify({ requestId: 'r1', runId: 'run1', allow: true }),
    );
    expect(mock.calls[1]?.url).toBe('http://100.64.1.2:43138/m1/sessions/s1/answers');
    expect(JSON.parse(String(mock.calls[1]?.init.body))).toEqual({
      requestId: 'r1',
      runId: 'run1',
      answers: { '어떻게 할까요?': { selectedOptions: ['계속'], customText: '빠르게' } },
    });
  });

  it('creates workspace sessions', async () => {
    const mock = mockFetch({ protocol: 1, sessionId: 'new' }, 201);
    const result = await createClient(credentials).createSession('ws1', {
      kind: 'claude',
      provider: 'codex',
    });
    mock.restore();
    expect(mock.calls[0]?.url).toBe('http://100.64.1.2:43138/m1/workspaces/ws1/sessions');
    expect(result.sessionId).toBe('new');
  });

  it('turns error bodies into ApiError with the server message', async () => {
    const mock = mockFetch({ protocol: 1, error: '세션이 실행 중이 아닙니다' }, 409);
    await expect(createClient(credentials).stop('s1')).rejects.toMatchObject({
      name: 'ApiError',
      status: 409,
      message: '세션이 실행 중이 아닙니다',
    });
    mock.restore();
  });

  it('flags 401 as needing re-pairing', async () => {
    const mock = mockFetch({ protocol: 1, error: 'unauthorized' }, 401);
    const error = await createClient(credentials)
      .info()
      .catch((caught: unknown) => caught);
    mock.restore();
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).needsRepair).toBe(true);
  });

  it('rejects oversized submissions before hitting the network', async () => {
    const mock = mockFetch({ protocol: 1, accepted: 'started' });
    await expect(
      createClient(credentials).submit('s1', 'x'.repeat(32 * 1024 + 1)),
    ).rejects.toBeInstanceOf(ApiError);
    mock.restore();
    expect(mock.calls).toHaveLength(0);
  });

  it('wraps transport failures as NetworkError', async () => {
    const original = globalThis.fetch;
    globalThis.fetch = jest.fn(async () => {
      throw new TypeError('Network request failed');
    }) as unknown as typeof fetch;
    await expect(createClient(credentials).info()).rejects.toBeInstanceOf(NetworkError);
    globalThis.fetch = original;
  });
});

describe('byteLength', () => {
  it('counts utf-8 bytes', () => {
    expect(byteLength('abc')).toBe(3);
    expect(byteLength('한글')).toBe(6);
    expect(byteLength('😀')).toBe(4);
  });
});
