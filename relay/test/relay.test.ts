import { createHash, createHmac } from 'node:crypto';

import { WebSocket } from 'ws';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startRelay, type RelayHandle } from '../src/server.ts';

interface CloseInfo {
  code: number;
  reason: string;
}

interface FrameInfo {
  data: Buffer;
  binary: boolean;
}

const silent = (): void => {};

const sha256Hex = (value: string): string => createHash('sha256').update(value, 'utf8').digest('hex');

/** A host secret and the serverId derived from it, as the Mac computes them. */
function identity(label: string): { serverId: string; hostToken: string } {
  const hostToken = sha256Hex(`host-secret:${label}`);
  return { serverId: sha256Hex(hostToken), hostToken };
}

let relay: RelayHandle;
const open: WebSocket[] = [];

function url(handle: RelayHandle, query: Record<string, string>): string {
  return `ws://127.0.0.1:${handle.port}/ws?${new URLSearchParams(query).toString()}`;
}

function socket(handle: RelayHandle, query: Record<string, string>): WebSocket {
  const ws = new WebSocket(url(handle, query));
  ws.binaryType = 'nodebuffer';
  open.push(ws);
  return ws;
}

function opened(ws: WebSocket): Promise<void> {
  return new Promise((resolve, reject) => {
    ws.once('open', () => resolve());
    ws.once('error', reject);
  });
}

function closed(ws: WebSocket): Promise<CloseInfo> {
  return new Promise((resolve) => {
    ws.once('close', (code: number, reason: Buffer) =>
      resolve({ code, reason: reason.toString('utf8') }),
    );
  });
}

/** The close code, or 'open' if the socket is still up after `ms`. */
function closeCode(ws: WebSocket, ms = 1000): Promise<number | 'open'> {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve('open'), ms);
    ws.once('close', (code: number) => {
      clearTimeout(timer);
      resolve(code);
    });
  });
}

function nextFrame(ws: WebSocket): Promise<FrameInfo> {
  return new Promise((resolve) => {
    ws.once('message', (data: Buffer, binary: boolean) => resolve({ data, binary }));
  });
}

function nextJson(ws: WebSocket): Promise<Record<string, unknown>> {
  return nextFrame(ws).then(
    (frame) => JSON.parse(frame.data.toString('utf8')) as Record<string, unknown>,
  );
}

function collect(ws: WebSocket, count: number): Promise<FrameInfo[]> {
  return new Promise((resolve) => {
    const frames: FrameInfo[] = [];
    const onMessage = (data: Buffer, binary: boolean): void => {
      frames.push({ data, binary });
      if (frames.length === count) {
        ws.off('message', onMessage);
        resolve(frames);
      }
    };
    ws.on('message', onMessage);
  });
}

/** control + client + host data sockets, fully paired. */
async function pair(
  label: string,
  connectionId: string,
): Promise<{ control: WebSocket; client: WebSocket; host: WebSocket }> {
  const { serverId, hostToken } = identity(label);
  const control = socket(relay, { serverId, role: 'server', v: '1', hostToken });
  await opened(control);
  const client = socket(relay, { serverId, role: 'client', connectionId, v: '1' });
  await opened(client);
  await nextJson(control);
  const host = socket(relay, { serverId, role: 'server', connectionId, v: '1', hostToken });
  await opened(host);
  return { control, client, host };
}

beforeAll(async () => {
  relay = await startRelay({
    host: '127.0.0.1',
    port: 0,
    attachTimeoutMs: 5000,
    logger: silent,
  });
});

afterAll(async () => {
  for (const ws of open) ws.terminate();
  await relay.close();
});

describe('http surface', () => {
  it('answers GET /healthz with 200 ok', async () => {
    const res = await fetch(`http://127.0.0.1:${relay.port}/healthz`);
    expect(res.status).toBe(200);
    expect(await res.text()).toBe('ok');
  });

  it('404s other http paths', async () => {
    const res = await fetch(`http://127.0.0.1:${relay.port}/nope`);
    expect(res.status).toBe(404);
  });

  it('refuses websocket upgrades outside /ws', async () => {
    const ws = new WebSocket(`ws://127.0.0.1:${relay.port}/other`);
    open.push(ws);
    const error = await new Promise<Error>((resolve) => ws.once('error', resolve));
    expect(error.message).toMatch(/404/);
  });
});

describe('query validation', () => {
  it('closes 4400 on a bad version', async () => {
    const ws = socket(relay, { serverId: 'v-bad', role: 'server', v: '2' });
    expect((await closed(ws)).code).toBe(4400);
  });

  it('closes 4400 on a bad serverId', async () => {
    const ws = socket(relay, { serverId: '_nope', role: 'server', v: '1' });
    expect((await closed(ws)).code).toBe(4400);
  });

  it('closes 4400 when a client omits connectionId', async () => {
    const ws = socket(relay, { serverId: 'no-cid', role: 'client', v: '1' });
    expect((await closed(ws)).code).toBe(4400);
  });
});

describe('pairing', () => {
  it('pipes text and binary frames both ways', async () => {
    const { client, host } = await pair('pipe-1', 'conn-00000001');

    client.send('client-to-host');
    const a = await nextFrame(host);
    expect(a.binary).toBe(false);
    expect(a.data.toString('utf8')).toBe('client-to-host');

    host.send('host-to-client');
    const b = await nextFrame(client);
    expect(b.binary).toBe(false);
    expect(b.data.toString('utf8')).toBe('host-to-client');

    client.send(Buffer.from([1, 2, 3, 255]));
    const c = await nextFrame(host);
    expect(c.binary).toBe(true);
    expect([...c.data]).toEqual([1, 2, 3, 255]);

    host.send(Buffer.from([9, 8]));
    const d = await nextFrame(client);
    expect(d.binary).toBe(true);
    expect([...d.data]).toEqual([9, 8]);
  });

  it('buffers client frames and flushes them in order', async () => {
    const { serverId, hostToken } = identity('buffer-1');
    const connectionId = 'conn-00000002';
    const control = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const client = socket(relay, { serverId, role: 'client', connectionId, v: '1' });
    await opened(client);
    expect(await nextJson(control)).toEqual({ type: 'connected', connectionId });

    client.send('one');
    client.send('two');
    client.send(Buffer.from([3]));
    await new Promise((resolve) => setTimeout(resolve, 30));

    const host = socket(relay, { serverId, role: 'server', connectionId, v: '1', hostToken });
    const frames = collect(host, 3);
    await opened(host);
    const received = await frames;
    expect(received.map((f) => f.data.toString('utf8'))).toEqual(['one', 'two', '']);
    expect(received.map((f) => f.binary)).toEqual([false, false, true]);
  });

  it('closes a client 4404 when no host control socket exists', async () => {
    const ws = socket(relay, {
      serverId: 'ghost-host',
      role: 'client',
      connectionId: 'conn-00000003',
      v: '1',
    });
    const info = await closed(ws);
    expect(info.code).toBe(4404);
  });

  it('closes a host data socket 4404 for an unknown connectionId', async () => {
    const { serverId, hostToken } = identity('unknown-1');
    const control = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const host = socket(relay, { serverId, role: 'server', connectionId: 'conn-00000004', v: '1', hostToken });
    expect((await closed(host)).code).toBe(4404);
  });

  it('closes the client 4504 when the host never attaches', async () => {
    process.env.RELAY_ATTACH_TIMEOUT_MS = '250';
    const short = await startRelay({ host: '127.0.0.1', port: 0, logger: silent });
    try {
      expect(short.config.attachTimeoutMs).toBe(250);
      const { serverId, hostToken } = identity('slow-host');
      const connectionId = 'conn-00000005';
      const control = new WebSocket(url(short, { serverId, role: 'server', v: '1', hostToken }));
      open.push(control);
      await opened(control);
      const client = new WebSocket(url(short, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(client);
      await opened(client);
      expect((await closed(client)).code).toBe(4504);
    } finally {
      delete process.env.RELAY_ATTACH_TIMEOUT_MS;
      await short.close();
    }
  });
});

describe('control socket', () => {
  it('closes the previous control socket with 4409 and inherits pending state', async () => {
    const { serverId, hostToken } = identity('dup-control');
    const connectionId = 'conn-00000006';
    const first = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(first);
    const client = socket(relay, { serverId, role: 'client', connectionId, v: '1' });
    await opened(client);
    expect(await nextJson(first)).toEqual({ type: 'connected', connectionId });

    const second = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(second);
    expect((await closed(first)).code).toBe(4409);

    const host = socket(relay, { serverId, role: 'server', connectionId, v: '1', hostToken });
    await opened(host);
    client.send('after-takeover');
    expect((await nextFrame(host)).data.toString('utf8')).toBe('after-takeover');
  });

  it('mirrors the close code and notifies disconnected', async () => {
    const connectionId = 'conn-00000007';
    const { control, client, host } = await pair('close-mirror', connectionId);
    const hostClosed = closed(host);
    const notice = nextJson(control);

    client.close(4321, 'bye');

    const info = await hostClosed;
    expect(info.code).toBe(4321);
    expect(info.reason).toBe('bye');
    expect(await notice).toEqual({ type: 'disconnected', connectionId });
  });

  it('closes data sockets with 4410 when the control socket goes away', async () => {
    const { control, client, host } = await pair('host-offline', 'conn-00000008');
    const clientClosed = closed(client);
    const hostClosed = closed(host);
    control.close(1000, 'done');
    expect((await clientClosed).code).toBe(4410);
    expect((await hostClosed).code).toBe(4410);
  });

  it('accepts a reconnect from the same host secret and closes the old control socket 4409', async () => {
    const { serverId, hostToken } = identity('same-secret');
    const first = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(first);
    const second = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(second);
    expect((await closed(first)).code).toBe(4409);
  });
});

describe('host ownership', () => {
  const stranger = identity('stranger').hostToken;

  it('refuses a control socket for an unclaimed serverId without the matching token', async () => {
    const { serverId, hostToken } = identity('squat-idle');

    const absent = socket(relay, { serverId, role: 'server', v: '1' });
    expect(await closeCode(absent)).toBe(4401);
    const wrong = socket(relay, { serverId, role: 'server', v: '1', hostToken: stranger });
    expect(await closeCode(wrong)).toBe(4401);
    // A token is only good for the serverId it hashes to.
    const borrowed = socket(relay, { serverId: sha256Hex('someone-else'), role: 'server', v: '1', hostToken });
    expect(await closeCode(borrowed)).toBe(4401);

    // Nothing was registered: a phone finds no host until the owner arrives.
    const early = socket(relay, { serverId, role: 'client', connectionId: 'conn-squat-01', v: '1' });
    expect(await closeCode(early)).toBe(4404);

    const owner = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(owner);
    const phone = socket(relay, { serverId, role: 'client', connectionId: 'conn-squat-02', v: '1' });
    await opened(phone);
    expect(await nextJson(owner)).toEqual({ type: 'connected', connectionId: 'conn-squat-02' });
  });

  it('refuses a control socket without the matching token right after the host left', async () => {
    const { serverId, hostToken } = identity('squat-after');
    const owner = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(owner);
    const ownerClosed = closed(owner);
    owner.close(1000, 'sleep');
    await ownerClosed;

    const absent = socket(relay, { serverId, role: 'server', v: '1' });
    expect(await closeCode(absent)).toBe(4401);
    const wrong = socket(relay, { serverId, role: 'server', v: '1', hostToken: stranger });
    expect(await closeCode(wrong)).toBe(4401);

    const back = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(back);
    expect(await closeCode(back, 200)).toBe('open');
  });

  it('refuses a host data socket without the matching token and still pairs the real one', async () => {
    const { serverId, hostToken } = identity('data-owner');
    const connectionId = 'conn-owner-01';
    const control = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const client = socket(relay, { serverId, role: 'client', connectionId, v: '1' });
    await opened(client);
    expect(await nextJson(control)).toEqual({ type: 'connected', connectionId });
    client.send('for-the-host');

    const absent = socket(relay, { serverId, role: 'server', connectionId, v: '1' });
    expect(await closeCode(absent)).toBe(4401);
    const wrong = socket(relay, { serverId, role: 'server', connectionId, v: '1', hostToken: stranger });
    expect(await closeCode(wrong)).toBe(4401);

    const host = socket(relay, { serverId, role: 'server', connectionId, v: '1', hostToken });
    const first = nextFrame(host);
    await opened(host);
    expect((await first).data.toString('utf8')).toBe('for-the-host');
    host.send('for-the-phone');
    expect((await nextFrame(client)).data.toString('utf8')).toBe('for-the-phone');
  });

  it('closes 4400 on a malformed host token', async () => {
    const { serverId } = identity('malformed');
    const ws = socket(relay, { serverId, role: 'server', v: '1', hostToken: 'not-hex' });
    expect((await closed(ws)).code).toBe(4400);
  });
});

describe('turn credentials', () => {
  const TURN_SECRET = 'test-turn-secret-abc123';

  it('returns turn-unavailable when no secret is configured', async () => {
    const { serverId, hostToken } = identity('turn-noconf');
    const ctrl = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(ctrl);

    ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
    const resp = await nextJson(ctrl);
    expect(resp['type']).toBe('turn-unavailable');
  });

  it('mints valid HMAC-SHA1 TURN credentials for an authenticated host', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
      turnHost: 'turn.example.com',
      turnPort: 3478,
      turnCredentialTtlSecs: 3600,
    });
    try {
      const { serverId, hostToken } = identity('turn-mint');
      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);

      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const resp = await nextJson(ctrl);

      expect(resp['type']).toBe('turn-credentials');
      expect(typeof resp['username']).toBe('string');
      expect(typeof resp['password']).toBe('string');
      expect(resp['ttl']).toBe(3600);
      expect(Array.isArray(resp['uris'])).toBe(true);
      expect((resp['uris'] as string[])[0]).toContain('turn.example.com');

      // Verify HMAC-SHA1 is correct (coturn REST API format).
      const username = resp['username'] as string;
      const expected = createHmac('sha1', TURN_SECRET).update(username, 'utf8').digest('base64');
      expect(resp['password']).toBe(expected);

      // Username must start with an expiry timestamp.
      const expiry = Number(username.split(':')[0]);
      expect(expiry).toBeGreaterThan(Date.now() / 1000);

      // The raw secret must not appear anywhere in the response.
      const raw = JSON.stringify(resp);
      expect(raw).not.toContain(TURN_SECRET);
    } finally {
      await turnRelay.close();
    }
  });

  it('the TURN response is sent only to the host control socket, never to client data sockets', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
    });
    try {
      const { serverId, hostToken } = identity('turn-isolation');
      const connectionId = 'conn-turn-iso-01';

      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);

      const cli = new WebSocket(url(turnRelay, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(cli);
      await opened(cli);
      // Drain the 'connected' notice.
      await nextJson(ctrl);

      // Collect any frames the client receives while TURN is being requested.
      const clientFrames: unknown[] = [];
      cli.on('message', (data: Buffer) => {
        try {
          clientFrames.push(JSON.parse(data.toString('utf8')));
        } catch {
          clientFrames.push(data);
        }
      });

      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const resp = await nextJson(ctrl);
      expect(resp['type']).toBe('turn-credentials');

      // Give the relay a tick to propagate anything to the client.
      await new Promise((r) => setTimeout(r, 30));
      expect(clientFrames).toHaveLength(0);
    } finally {
      await turnRelay.close();
    }
  });

  it('rate-limits per serverId', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
      turnMaxPerServerId: 2,
      turnMaxPerIp: 100,
      turnRateWindowMs: 60_000,
    });
    try {
      const { serverId, hostToken } = identity('turn-rate-sid');
      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);

      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const r1 = await nextJson(ctrl);
      expect(r1['type']).toBe('turn-credentials');

      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const r2 = await nextJson(ctrl);
      expect(r2['type']).toBe('turn-credentials');

      // Third request exceeds the per-serverId limit.
      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const r3 = await nextJson(ctrl);
      expect(r3['type']).toBe('turn-rate-limited');
      expect(typeof r3['retryAfterSecs']).toBe('number');
    } finally {
      await turnRelay.close();
    }
  });

  it('rate-limits per IP', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
      turnMaxPerServerId: 100,
      turnMaxPerIp: 2,
      turnRateWindowMs: 60_000,
    });
    try {
      // Use two different serverIds so per-serverId limit is not hit.
      const a = identity('turn-rate-ip-a');
      const b = identity('turn-rate-ip-b');
      const c = identity('turn-rate-ip-c');

      const ctrlA = new WebSocket(url(turnRelay, { serverId: a.serverId, role: 'server', v: '1', hostToken: a.hostToken }));
      open.push(ctrlA);
      await opened(ctrlA);
      ctrlA.send(JSON.stringify({ type: 'turn-credentials-request' }));
      expect((await nextJson(ctrlA))['type']).toBe('turn-credentials');

      const ctrlB = new WebSocket(url(turnRelay, { serverId: b.serverId, role: 'server', v: '1', hostToken: b.hostToken }));
      open.push(ctrlB);
      await opened(ctrlB);
      ctrlB.send(JSON.stringify({ type: 'turn-credentials-request' }));
      expect((await nextJson(ctrlB))['type']).toBe('turn-credentials');

      // Third request from the same IP (127.0.0.1) hits the per-IP limit.
      const ctrlC = new WebSocket(url(turnRelay, { serverId: c.serverId, role: 'server', v: '1', hostToken: c.hostToken }));
      open.push(ctrlC);
      await opened(ctrlC);
      ctrlC.send(JSON.stringify({ type: 'turn-credentials-request' }));
      expect((await nextJson(ctrlC))['type']).toBe('turn-rate-limited');
    } finally {
      await turnRelay.close();
    }
  });

  it('ignores unrecognised messages on the control socket (relay stays blind)', async () => {
    const { serverId, hostToken } = identity('turn-blind');
    const ctrl = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(ctrl);

    // Send a binary E2EE-like payload — relay must not crash or respond.
    ctrl.send(Buffer.from([0x00, 0x01, 0x02, 0x03]));
    ctrl.send(JSON.stringify({ type: 'unknown-relay-message', data: 'ignored' }));

    // Control socket must still be open.
    await new Promise((r) => setTimeout(r, 30));
    expect(ctrl.readyState).toBe(ctrl.OPEN);
  });

  it('defaults the credential lifetime to ~1 h', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
    });
    try {
      const { serverId, hostToken } = identity('turn-ttl-default');
      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);

      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const resp = await nextJson(ctrl);

      expect(resp['ttl']).toBe(3600);
      const expiry = Number((resp['username'] as string).split(':')[0]);
      const nowSecs = Math.floor(Date.now() / 1000);
      // ~1 h ahead: allow a few seconds of slack for test execution time.
      expect(expiry).toBeGreaterThanOrEqual(nowSecs + 3590);
      expect(expiry).toBeLessThanOrEqual(nowSecs + 3600);
    } finally {
      await turnRelay.close();
    }
  });

  it('never mints for a client data socket — the request is forwarded as opaque payload', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
    });
    try {
      const { serverId, hostToken } = identity('turn-client-cannot-mint');
      const connectionId = 'conn-turn-cli-01';

      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);
      const cli = new WebSocket(url(turnRelay, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(cli);
      await opened(cli);
      expect((await nextJson(ctrl))['type']).toBe('connected');

      const clientFrames: Buffer[] = [];
      cli.on('message', (data: Buffer) => clientFrames.push(data));

      // A phone asking the relay directly for credentials must get nothing back.
      const request = JSON.stringify({ type: 'turn-credentials-request' });
      cli.send(request);
      await new Promise((r) => setTimeout(r, 50));
      expect(clientFrames).toHaveLength(0);

      // Instead the bytes were queued for the Mac as ordinary opaque payload.
      const hst = new WebSocket(
        url(turnRelay, { serverId, role: 'server', connectionId, v: '1', hostToken }),
      );
      open.push(hst);
      const first = nextFrame(hst);
      await opened(hst);
      expect((await first).data.toString('utf8')).toBe(request);
    } finally {
      await turnRelay.close();
    }
  });

  it('never mints for a host data socket — the request is forwarded as opaque payload', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
    });
    try {
      const { serverId, hostToken } = identity('turn-hostdata-cannot-mint');
      const connectionId = 'conn-turn-hd-01';

      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);
      const cli = new WebSocket(url(turnRelay, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(cli);
      await opened(cli);
      expect((await nextJson(ctrl))['type']).toBe('connected');
      const hst = new WebSocket(
        url(turnRelay, { serverId, role: 'server', connectionId, v: '1', hostToken }),
      );
      open.push(hst);
      await opened(hst);

      const hostFrames: Buffer[] = [];
      hst.on('message', (data: Buffer) => hostFrames.push(data));

      // Only the control socket mints; a data socket's request is just payload.
      const request = JSON.stringify({ type: 'turn-credentials-request' });
      const toClient = nextFrame(cli);
      hst.send(request);
      expect((await toClient).data.toString('utf8')).toBe(request);
      expect(hostFrames).toHaveLength(0);
    } finally {
      await turnRelay.close();
    }
  });

  it('refuses a control socket with the wrong host token, so it can never mint', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
    });
    try {
      const { serverId } = identity('turn-unauth');
      const wrongToken = identity('turn-unauth-stranger').hostToken;
      const impostor = new WebSocket(
        url(turnRelay, { serverId, role: 'server', v: '1', hostToken: wrongToken }),
      );
      open.push(impostor);
      expect((await closed(impostor)).code).toBe(4401);
    } finally {
      await turnRelay.close();
    }
  });

  it('keeps the coturn secret out of every client-bound frame', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
      turnMaxPerServerId: 1,
      turnRateWindowMs: 60_000,
    });
    try {
      const { serverId, hostToken } = identity('turn-secret-hygiene');
      const connectionId = 'conn-turn-hyg-01';

      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);
      const cli = new WebSocket(url(turnRelay, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(cli);
      await opened(cli);
      expect((await nextJson(ctrl))['type']).toBe('connected');
      const hst = new WebSocket(
        url(turnRelay, { serverId, role: 'server', connectionId, v: '1', hostToken }),
      );
      open.push(hst);
      await opened(hst);

      const clientFrames: Buffer[] = [];
      cli.on('message', (data: Buffer) => clientFrames.push(data));

      // Mint once, then trip the rate limit, so both relay→host replies are exercised.
      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const minted = await nextJson(ctrl);
      expect(minted['type']).toBe('turn-credentials');
      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const limited = await nextJson(ctrl);
      expect(limited['type']).toBe('turn-rate-limited');

      // The Mac forwards the credentials to the phone inside E2EE; the relay itself
      // must never put the secret on the wire, in either reply or any client frame.
      expect(JSON.stringify(minted)).not.toContain(TURN_SECRET);
      expect(JSON.stringify(limited)).not.toContain(TURN_SECRET);
      hst.send('host-to-phone');
      await nextFrame(cli);
      await new Promise((r) => setTimeout(r, 30));
      expect(clientFrames.length).toBeGreaterThan(0);
      for (const frame of clientFrames) {
        expect(frame.toString('utf8')).not.toContain(TURN_SECRET);
      }
    } finally {
      await turnRelay.close();
    }
  });

  it('lets a host mint again once the rate window has passed', async () => {
    const turnRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      turnSecret: TURN_SECRET,
      turnMaxPerServerId: 1,
      turnMaxPerIp: 100,
      turnRateWindowMs: 80,
    });
    try {
      const { serverId, hostToken } = identity('turn-window-expiry');
      const ctrl = new WebSocket(url(turnRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);

      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      expect((await nextJson(ctrl))['type']).toBe('turn-credentials');
      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      const limited = await nextJson(ctrl);
      expect(limited['type']).toBe('turn-rate-limited');
      expect(limited['retryAfterSecs']).toBeGreaterThanOrEqual(1);

      // Renewal before expiry needs the window to roll over, not a restart.
      await new Promise((r) => setTimeout(r, 140));
      ctrl.send(JSON.stringify({ type: 'turn-credentials-request' }));
      expect((await nextJson(ctrl))['type']).toBe('turn-credentials');
    } finally {
      await turnRelay.close();
    }
  });

  it('passes screen-share signalling frames through byte-for-byte at realistic sizes', async () => {
    const { client, host } = await pair('turn-signalling', 'conn-turn-sig-01');

    // Mock E2EE envelopes: 12B nonce + ciphertext. Sizes mirror the screen-share
    // signalling types — a small ICE candidate and a full SDP offer.
    const envelope = (size: number, seed: number): Buffer => {
      const buf = Buffer.alloc(size);
      for (let i = 0; i < size; i += 1) buf[i] = (i * 31 + seed) & 0xff;
      return buf;
    };
    const iceCandidate = envelope(96, 7);
    const sdpOffer = envelope(8192, 19);

    // Both directions, in order, unaltered — the relay reads none of it.
    client.send(iceCandidate);
    client.send(sdpOffer);
    const upstream = await collect(host, 2);
    expect(upstream.map((f) => f.binary)).toEqual([true, true]);
    expect([...upstream[0]!.data]).toEqual([...iceCandidate]);
    expect([...upstream[1]!.data]).toEqual([...sdpOffer]);

    host.send(sdpOffer);
    host.send(iceCandidate);
    const downstream = await collect(client, 2);
    expect([...downstream[0]!.data]).toEqual([...sdpOffer]);
    expect([...downstream[1]!.data]).toEqual([...iceCandidate]);
  });

  it('carries a signalling frame at the 1 MiB binary frame limit', async () => {
    const { client, host } = await pair('turn-sig-max', 'conn-turn-sig-02');
    const atLimit = Buffer.alloc(1024 * 1024, 0xa5);

    client.send(atLimit);
    const received = await nextFrame(host);
    expect(received.binary).toBe(true);
    expect(received.data.length).toBe(atLimit.length);
    expect(received.data.equals(atLimit)).toBe(true);
  });

  it('E2EE data socket frames pass through byte-for-byte (relay is blind to content)', async () => {
    // Simulate E2EE encrypted frames: binary blobs the relay must not read or alter.
    const { client, host } = await pair('turn-e2ee', 'conn-turn-e2ee-01');

    // 28-byte mock E2EE frame: 12B nonce + 16B ciphertext (just random bytes).
    const e2eeFrame = Buffer.from([
      0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
      0xde, 0xad, 0xbe, 0xef, 0xca, 0xfe, 0xba, 0xbe, 0x01, 0x23, 0x45, 0x67,
      0x89, 0xab, 0xcd, 0xef,
    ]);

    client.send(e2eeFrame);
    const fromClient = await nextFrame(host);
    expect(fromClient.binary).toBe(true);
    expect([...fromClient.data]).toEqual([...e2eeFrame]);

    host.send(e2eeFrame);
    const fromHost = await nextFrame(client);
    expect(fromHost.binary).toBe(true);
    expect([...fromHost.data]).toEqual([...e2eeFrame]);
  });
});

describe('limits', () => {
  it('rejects the 33rd concurrent connection with 4429', async () => {
    const { serverId, hostToken } = identity('cap-1');
    const control = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(control);

    const clients = Array.from({ length: 32 }, (_unused, index) =>
      socket(relay, {
        serverId,
        role: 'client',
        connectionId: `conn-cap-${String(index).padStart(4, '0')}`,
        v: '1',
      }),
    );
    await Promise.all(clients.map(opened));

    const extra = socket(relay, {
      serverId,
      role: 'client',
      connectionId: 'conn-cap-overflow',
      v: '1',
    });
    expect((await closed(extra)).code).toBe(4429);
  });

  it('closes 4413 when the client overruns the buffer before the host attaches', async () => {
    const { serverId, hostToken } = identity('overflow-1');
    const connectionId = 'conn-00000009';
    const control = socket(relay, { serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const client = socket(relay, { serverId, role: 'client', connectionId, v: '1' });
    await opened(client);
    await nextJson(control);

    for (let i = 0; i < 65; i += 1) client.send(`frame-${i}`);
    expect((await closed(client)).code).toBe(4413);
  });

  it('closes 4507 when forwarding would exceed maxSocketBufferedBytes (host→client)', async () => {
    const tinyRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      maxSocketBufferedBytes: 0,
    });
    try {
      const { serverId, hostToken } = identity('sockbuf-hc');
      const connectionId = 'conn-sockbuf-01';
      const ctrl = new WebSocket(url(tinyRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);
      const cli = new WebSocket(url(tinyRelay, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(cli);
      await opened(cli);
      await nextJson(ctrl);
      const hst = new WebSocket(url(tinyRelay, { serverId, role: 'server', connectionId, v: '1', hostToken }));
      open.push(hst);
      await opened(hst);

      const cliClosed = closed(cli);
      const hstClosed = closed(hst);
      hst.send('payload');
      expect((await cliClosed).code).toBe(4507);
      expect((await hstClosed).code).toBe(4507);
    } finally {
      await tinyRelay.close();
    }
  });

  it('closes 4507 when forwarding would exceed maxSocketBufferedBytes (client→host)', async () => {
    const tinyRelay = await startRelay({
      host: '127.0.0.1',
      port: 0,
      logger: silent,
      maxSocketBufferedBytes: 0,
    });
    try {
      const { serverId, hostToken } = identity('sockbuf-ch');
      const connectionId = 'conn-sockbuf-02';
      const ctrl = new WebSocket(url(tinyRelay, { serverId, role: 'server', v: '1', hostToken }));
      open.push(ctrl);
      await opened(ctrl);
      const cli = new WebSocket(url(tinyRelay, { serverId, role: 'client', connectionId, v: '1' }));
      open.push(cli);
      await opened(cli);
      await nextJson(ctrl);
      const hst = new WebSocket(url(tinyRelay, { serverId, role: 'server', connectionId, v: '1', hostToken }));
      open.push(hst);
      await opened(hst);

      const cliClosed = closed(cli);
      const hstClosed = closed(hst);
      cli.send('payload');
      expect((await cliClosed).code).toBe(4507);
      expect((await hstClosed).code).toBe(4507);
    } finally {
      await tinyRelay.close();
    }
  });
});
