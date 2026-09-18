import { ApiError, byteLength, createClient, describeError, needsRepair } from '@/api/client';
import { RelayError } from '@/api/relay/transport';
import type { MobileClient, RelayChannel } from '@/api/client';
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

describe('the m1 capability extension routes', () => {
  const ok = { status: 200, body: { protocol: 1, ok: true } };

  it('leaves submit alone unless a mode or attachments are asked for', async () => {
    const fake = fakeChannel({ status: 202, body: { protocol: 1, accepted: 'queued' } });
    const client = createClient(fake.channel);
    await client.submit('s1', 'hi');
    await client.submit('s1', 'hi', { mode: 'steer' });
    await client.submit('s1', 'hi', { mode: 'queue', attachments: ['u1', 'u2'] });
    await client.submit('s1', 'hi', { attachments: [] });
    expect(fake.calls.map((call) => call.body)).toEqual([
      { text: 'hi' },
      { text: 'hi', mode: 'steer' },
      { text: 'hi', mode: 'queue', attachments: ['u1', 'u2'] },
      { text: 'hi' },
    ]);
  });

  it('addresses the queue, pane, settings and command routes', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await client.removeQueued('s1', 'q/1');
    await client.runNext('s1');
    await client.rename('s1', '  새 이름  ');
    await client.close('s1');
    await client.updateSettings('s1', { model: 'opus', effort: undefined, permissionMode: 'ask' });
    await client.commands('s1');
    await client.runCommand('s1', 'usage');
    expect(fake.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'POST /m1/sessions/s1/queue/q%2F1/remove',
      'POST /m1/sessions/s1/queue/run-next',
      'POST /m1/sessions/s1/rename',
      'POST /m1/sessions/s1/close',
      'POST /m1/sessions/s1/settings',
      'GET /m1/sessions/s1/commands',
      'POST /m1/sessions/s1/command',
    ]);
    expect(fake.calls[2]?.body).toEqual({ title: '새 이름' });
    expect(fake.calls[4]?.body).toEqual({ model: 'opus', permissionMode: 'ask' });
    expect(fake.calls[6]?.body).toEqual({ action: 'usage' });
  });

  it('pages history with before and a clamped limit', async () => {
    const fake = fakeChannel({ status: 200, body: { protocol: 1, entries: [], hasMore: false } });
    const client = createClient(fake.channel);
    await client.entries('s1', { before: 'e 9' });
    await client.entries('s1', { before: 'e9', limit: 500 });
    await client.entries('s1', { before: 'e9', limit: 0 });
    expect(fake.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'GET /m1/sessions/s1/entries?before=e+9&limit=50',
      'GET /m1/sessions/s1/entries?before=e9&limit=100',
      'GET /m1/sessions/s1/entries?before=e9&limit=1',
    ]);
  });

  it('refuses a title outside 1~80 characters and an empty settings patch locally', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await expect(client.rename('s1', '   ')).rejects.toMatchObject({ status: 400 });
    await expect(client.rename('s1', 'x'.repeat(81))).rejects.toMatchObject({ status: 400 });
    await expect(client.updateSettings('s1', {})).rejects.toMatchObject({ status: 400 });
    await expect(client.updateSettings('s1', { model: undefined })).rejects.toMatchObject({
      status: 400,
    });
    expect(fake.calls).toHaveLength(0);
  });

  it('posts a guided skill with and without the composer text', async () => {
    const fake = fakeChannel({ status: 202, body: { protocol: 1, accepted: 'started' } });
    const client = createClient(fake.channel);
    await client.guided('s1', { style: 'ouroboros', skill: 'interview', text: '  로그인  ' });
    await client.guided('s1', { style: 'ouroboros', skill: 'seed' });
    await client.guided('s1', { style: 'paperthin', skill: ' re0 ', text: '' });
    expect(fake.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'POST /m1/sessions/s1/guided',
      'POST /m1/sessions/s1/guided',
      'POST /m1/sessions/s1/guided',
    ]);
    expect(fake.calls.map((call) => call.body)).toEqual([
      { style: 'ouroboros', skill: 'interview', text: '로그인' },
      { style: 'ouroboros', skill: 'seed' },
      { style: 'paperthin', skill: 're0' },
    ]);
  });

  it('refuses an empty skill and oversized guided text before touching the tunnel', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await expect(client.guided('s1', { style: 'paperthin', skill: '  ' })).rejects.toMatchObject({
      status: 400,
    });
    await expect(
      client.guided('s1', { style: 'paperthin', skill: 're0', text: 'x'.repeat(32 * 1024 + 1) }),
    ).rejects.toMatchObject({ status: 413 });
    expect(fake.calls).toHaveLength(0);
  });

  it('walks the upload routes: open, chunks by index, complete, cancel', async () => {
    const fake = fakeChannel({ status: 201, body: { protocol: 1, uploadId: 'u1', chunkSize: 8 } });
    const client = createClient(fake.channel);
    await client.createUpload('s1', { name: '사진.png', size: 20, mimeType: 'image/png' });
    await client.createUpload('s1', { name: 'plain.bin', size: 3 });
    await client.uploadChunk('u/1', 0, 'AAEC');
    await client.completeUpload('u1');
    await client.cancelUpload('u1');
    expect(fake.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'POST /m1/sessions/s1/uploads',
      'POST /m1/sessions/s1/uploads',
      'POST /m1/uploads/u%2F1/chunks/0',
      'POST /m1/uploads/u1/complete',
      'POST /m1/uploads/u1/cancel',
    ]);
    expect(fake.calls[0]?.body).toEqual({ name: '사진.png', size: 20, mimeType: 'image/png' });
    expect(fake.calls[1]?.body).toEqual({ name: 'plain.bin', size: 3 });
    expect(fake.calls[2]?.body).toEqual({ dataBase64: 'AAEC' });
  });

  it('maps every new route’s host error onto ApiError', async () => {
    const cases: Array<[number, string, (client: MobileClient) => Promise<unknown>]> = [
      [404, '없는 항목입니다', (client) => client.removeQueued('s1', 'q1')],
      [409, '실행 중입니다', (client) => client.runNext('s1')],
      [400, '이름이 올바르지 않습니다', (client) => client.rename('s1', '이름')],
      [404, '없는 실행 창입니다', (client) => client.close('s1')],
      [400, '잘못된 요청입니다', (client) => client.entries('s1', { before: 'e1' })],
      [409, '실행 중에는 바꿀 수 없습니다', (client) => client.updateSettings('s1', { model: 'x' })],
      [503, '지금은 쓸 수 없습니다', (client) => client.commands('s1')],
      [400, '모르는 명령입니다', (client) => client.runCommand('s1', 'help')],
      [413, '첨부가 너무 큽니다', (client) => client.submit('s1', 'hi', { attachments: ['u1'] })],
      [400, '모르는 스킬입니다', (client) => client.guided('s1', { style: 'ouroboros', skill: 'x' })],
      [409, '이 스타일의 창이 아닙니다', (client) => client.guided('s1', { style: 'paperthin', skill: 're0' })],
      [413, '한도를 넘었습니다', (client) => client.createUpload('s1', { name: 'a', size: 9 })],
      [400, '크기가 다릅니다', (client) => client.completeUpload('u1')],
      [404, '없는 업로드입니다', (client) => client.uploadChunk('u1', 0, 'AA==')],
    ];
    for (const [status, error, call] of cases) {
      const fake = fakeChannel({ status, body: { protocol: 1, error } });
      await expect(call(createClient(fake.channel))).rejects.toMatchObject({
        name: 'ApiError',
        status,
        message: error,
      });
    }
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
