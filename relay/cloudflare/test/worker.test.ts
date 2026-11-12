/**
 * The Worker relay against the Node relay's wire (../../test/relay.test.ts, push.test.ts), run in
 * `wrangler dev` (local workerd): the same sockets, close codes and push answers.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { WebSocket } from 'ws';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

const PORT = 8799 + Math.floor(Math.random() * 200);
const BASE = `127.0.0.1:${PORT}`;
const MAX_CONNECTIONS = 3;
const MAX_BUFFERED = 4;

interface FrameInfo {
  data: Buffer;
  binary: boolean;
}

const sha256Hex = (value: string): string => createHash('sha256').update(value, 'utf8').digest('hex');

function identity(label: string): { serverId: string; hostToken: string } {
  const hostToken = sha256Hex(`host-secret:${label}`);
  return { serverId: sha256Hex(hostToken), hostToken };
}

let worker: ChildProcess;
let state: string;
const open: WebSocket[] = [];

function socket(query: Record<string, string>, path = '/ws'): WebSocket {
  const ws = new WebSocket(`ws://${BASE}${path}?${new URLSearchParams(query).toString()}`);
  ws.binaryType = 'nodebuffer';
  ws.on('error', () => {});
  open.push(ws);
  return ws;
}

function opened(ws: WebSocket): Promise<void> {
  return new Promise((resolve, reject) => {
    ws.once('open', () => resolve());
    ws.once('error', reject);
  });
}

function closed(ws: WebSocket): Promise<{ code: number; reason: string }> {
  return new Promise((resolve) => {
    ws.once('close', (code: number, reason: Buffer) => resolve({ code, reason: reason.toString('utf8') }));
  });
}

function nextFrame(ws: WebSocket): Promise<FrameInfo> {
  return new Promise((resolve) => ws.once('message', (data: Buffer, binary: boolean) => resolve({ data, binary })));
}

async function nextJson(ws: WebSocket): Promise<Record<string, unknown>> {
  return JSON.parse((await nextFrame(ws)).data.toString('utf8')) as Record<string, unknown>;
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

async function pair(label: string, connectionId: string): Promise<{ control: WebSocket; client: WebSocket; host: WebSocket }> {
  const { serverId, hostToken } = identity(label);
  const control = socket({ serverId, role: 'server', v: '1', hostToken });
  await opened(control);
  const client = socket({ serverId, role: 'client', connectionId, v: '1' });
  const connected = nextJson(control);
  await opened(client);
  await connected;
  const host = socket({ serverId, role: 'server', connectionId, v: '1', hostToken });
  await opened(host);
  return { control, client, host };
}

async function http(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`http://${BASE}${path}`, init);
}

beforeAll(async () => {
  state = mkdtempSync(join(tmpdir(), 'mighty-relay-cf-'));
  worker = spawn(
    'npx',
    [
      'wrangler', 'dev', '--ip', '127.0.0.1', '--port', String(PORT), '--persist-to', state, '--show-interactive-dev-session=false',
      '--var', 'RELAY_ATTACH_TIMEOUT_MS:800', '--var', `RELAY_MAX_CONNECTIONS:${MAX_CONNECTIONS}`, '--var', `RELAY_MAX_BUFFERED_FRAMES:${MAX_BUFFERED}`,
    ],
    { cwd: join(import.meta.dirname, '..'), env: { ...process.env, WRANGLER_SEND_METRICS: 'false', CI: '1' }, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  let output = '';
  worker.stdout?.on('data', (chunk: Buffer) => { output += chunk.toString(); });
  worker.stderr?.on('data', (chunk: Buffer) => { output += chunk.toString(); });
  const deadline = Date.now() + 90_000;
  for (;;) {
    try {
      if ((await http('/healthz')).status === 200) break;
    } catch {
      // not up yet
    }
    if (Date.now() > deadline || worker.exitCode !== null) throw new Error(`wrangler dev did not start:\n${output}`);
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
}, 120_000);

afterAll(() => {
  for (const ws of open) ws.terminate();
  worker?.kill('SIGTERM');
  if (state) rmSync(state, { recursive: true, force: true });
});

describe('http surface', () => {
  it('answers GET /healthz with 200 ok and 404s other paths', async () => {
    const health = await http('/healthz');
    expect(health.status).toBe(200);
    expect(await health.text()).toBe('ok');
    expect((await http('/nope')).status).toBe(404);
  });

  it('refuses websocket upgrades outside /ws', async () => {
    const ws = socket({ v: '1' }, '/other');
    await expect(opened(ws)).rejects.toThrow();
  });
});

describe('query validation', () => {
  it('closes 4400 on a bad version, a bad serverId and a client without connectionId', async () => {
    expect((await closed(socket({ v: '2', serverId: 'abc', role: 'server' }))).code).toBe(4400);
    expect((await closed(socket({ v: '1', serverId: '!bad', role: 'server' }))).code).toBe(4400);
    expect((await closed(socket({ v: '1', serverId: 'abc', role: 'client' }))).code).toBe(4400);
  });
});

describe('pairing', () => {
  it('pipes text and binary frames both ways', async () => {
    const { client, host } = await pair('pipe-1', 'conn-00000001');
    client.send('client-to-host');
    const a = await nextFrame(host);
    expect([a.binary, a.data.toString('utf8')]).toEqual([false, 'client-to-host']);
    host.send('host-to-client');
    const b = await nextFrame(client);
    expect([b.binary, b.data.toString('utf8')]).toEqual([false, 'host-to-client']);
    client.send(Buffer.from([1, 2, 3, 255]));
    const c = await nextFrame(host);
    expect([c.binary, [...c.data]]).toEqual([true, [1, 2, 3, 255]]);
    host.send(Buffer.from([9, 8]));
    const d = await nextFrame(client);
    expect([d.binary, [...d.data]]).toEqual([true, [9, 8]]);
  });

  it('buffers client frames and flushes them in order', async () => {
    const { serverId, hostToken } = identity('buffer-1');
    const connectionId = 'conn-00000002';
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const client = socket({ serverId, role: 'client', connectionId, v: '1' });
    const connected = nextJson(control);
    await opened(client);
    expect(await connected).toEqual({ type: 'connected', connectionId });
    client.send('one');
    client.send('two');
    client.send(Buffer.from([3]));
    await new Promise((resolve) => setTimeout(resolve, 100));
    const host = socket({ serverId, role: 'server', connectionId, v: '1', hostToken });
    const frames = collect(host, 3);
    await opened(host);
    const received = await frames;
    expect(received.map((f) => f.data.toString('utf8'))).toEqual(['one', 'two', '\u0003']);
    expect(received.map((f) => f.binary)).toEqual([false, false, true]);
  });

  it('closes a client 4404 when no host control socket exists', async () => {
    expect((await closed(socket({ serverId: 'ghost-host', role: 'client', connectionId: 'conn-00000003', v: '1' }))).code).toBe(4404);
  });

  it('closes a host data socket 4404 for an unknown connectionId', async () => {
    const { serverId, hostToken } = identity('unknown-1');
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    expect((await closed(socket({ serverId, role: 'server', connectionId: 'conn-00000004', v: '1', hostToken }))).code).toBe(4404);
  });

  it('closes the client 4504 when the host never attaches and tells the control socket', async () => {
    const { serverId, hostToken } = identity('slow-host');
    const connectionId = 'conn-00000005';
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const notices = collect(control, 2);
    const client = socket({ serverId, role: 'client', connectionId, v: '1' });
    await opened(client);
    expect((await closed(client)).code).toBe(4504);
    expect((await notices).map((f) => JSON.parse(f.data.toString('utf8')) as unknown)).toEqual([
      { type: 'connected', connectionId },
      { type: 'disconnected', connectionId },
    ]);
  });

  it('carries a frame at the 1 MiB limit', async () => {
    const { client, host } = await pair('big-frame', 'conn-00000009');
    const big = Buffer.alloc(1024 * 1024, 7);
    client.send(big);
    const got = await nextFrame(host);
    expect(got.data.byteLength).toBe(big.byteLength);
  });
});

describe('control socket', () => {
  it('closes the previous control socket with 4409 and the new one inherits pending state', async () => {
    const { serverId, hostToken } = identity('dup-control');
    const connectionId = 'conn-00000006';
    const first = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(first);
    const client = socket({ serverId, role: 'client', connectionId, v: '1' });
    const connected = nextJson(first);
    await opened(client);
    expect(await connected).toEqual({ type: 'connected', connectionId });
    const second = socket({ serverId, role: 'server', v: '1', hostToken });
    const firstClosed = closed(first);
    await opened(second);
    // The client is still waiting for its host half (800 ms here), now under the new control socket.
    const host = socket({ serverId, role: 'server', connectionId, v: '1', hostToken });
    await opened(host);
    client.send('after-takeover');
    expect((await nextFrame(host)).data.toString('utf8')).toBe('after-takeover');
    expect((await firstClosed).code).toBe(4409);
  });

  it('mirrors the close code and notifies disconnected', async () => {
    const connectionId = 'conn-00000007';
    const { control, client, host } = await pair('close-mirror', connectionId);
    const hostClosed = closed(host);
    const notice = nextJson(control);
    client.close(4321, 'bye');
    expect(await hostClosed).toEqual({ code: 4321, reason: 'bye' });
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

  it('answers a TURN request turn-unavailable and ignores anything else', async () => {
    const { serverId, hostToken } = identity('turn-off');
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    control.send('not json');
    control.send(JSON.stringify({ type: 'something-else' }));
    control.send(JSON.stringify({ type: 'turn-credentials-request' }));
    expect(await nextJson(control)).toEqual({ type: 'turn-unavailable' });
  });
});

describe('host ownership', () => {
  it('refuses a control socket without the matching token', async () => {
    const { serverId } = identity('owned');
    const other = identity('intruder');
    expect((await closed(socket({ serverId, role: 'server', v: '1' }))).code).toBe(4401);
    expect((await closed(socket({ serverId, role: 'server', v: '1', hostToken: other.hostToken }))).code).toBe(4401);
  });

  it('refuses a host data socket without the matching token and still pairs the real one', async () => {
    const { serverId, hostToken } = identity('owned-data');
    const connectionId = 'conn-00000010';
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const client = socket({ serverId, role: 'client', connectionId, v: '1' });
    const connected = nextJson(control);
    await opened(client);
    await connected;
    const fake = socket({ serverId, role: 'server', connectionId, v: '1', hostToken: identity('x').hostToken });
    expect((await closed(fake)).code).toBe(4401);
    const host = socket({ serverId, role: 'server', connectionId, v: '1', hostToken });
    await opened(host);
    client.send('real');
    expect((await nextFrame(host)).data.toString('utf8')).toBe('real');
  });
});

describe('limits', () => {
  it(`rejects connection ${MAX_CONNECTIONS + 1} with 4429`, async () => {
    const { serverId, hostToken } = identity('crowd');
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    for (let i = 0; i < MAX_CONNECTIONS; i++) {
      const client = socket({ serverId, role: 'client', connectionId: `crowd-0000000${i}`, v: '1' });
      await opened(client);
      const host = socket({ serverId, role: 'server', connectionId: `crowd-0000000${i}`, v: '1', hostToken });
      await opened(host);
    }
    expect((await closed(socket({ serverId, role: 'client', connectionId: 'crowd-overflow', v: '1' }))).code).toBe(4429);
  });

  it('closes 4413 when the client overruns the buffer before the host attaches', async () => {
    const { serverId, hostToken } = identity('overrun');
    const control = socket({ serverId, role: 'server', v: '1', hostToken });
    await opened(control);
    const client = socket({ serverId, role: 'client', connectionId: 'conn-00000011', v: '1' });
    await opened(client);
    const done = closed(client);
    for (let i = 0; i <= MAX_BUFFERED; i++) client.send(`f${i}`);
    expect((await done).code).toBe(4413);
  });
});

describe('push', () => {
  const token = 'fcm-token-'.padEnd(40, 'x');

  it('registers, refreshes and deletes a handle with its secret', async () => {
    const made = await http('/push/v1/handles', { method: 'POST', body: JSON.stringify({ fcmToken: token }), headers: { 'content-type': 'application/json' } });
    expect(made.status).toBe(201);
    const { handle, secret } = (await made.json()) as { handle: string; secret: string };
    expect(handle).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(secret).toMatch(/^[A-Za-z0-9_-]{43}$/);
    const put = (auth: string) => http(`/push/v1/handles/${handle}`, { method: 'PUT', body: JSON.stringify({ fcmToken: `${token}2` }), headers: { authorization: `Bearer ${auth}` } });
    expect((await put(secret)).status).toBe(204);
    expect((await put('A'.repeat(43))).status).toBe(404);
    expect((await http(`/push/v1/handles/${handle}`, { method: 'DELETE', headers: { authorization: `Bearer ${secret}` } })).status).toBe(204);
    expect((await put(secret)).status).toBe(404);
  });

  it('refuses bad bodies and unknown routes', async () => {
    expect((await http('/push/v1/handles', { method: 'POST', body: 'nope' })).status).toBe(400);
    expect((await http('/push/v1/handles', { method: 'POST', body: JSON.stringify({ fcmToken: 'short' }) })).status).toBe(400);
    expect((await http('/push/v1/handles', { method: 'POST', body: JSON.stringify({ fcmToken: 'x'.repeat(9000) }) })).status).toBe(413);
    expect((await http('/push/v1/other', { method: 'POST', body: '{}' })).status).toBe(404);
  });

  it('checks the host token before anything else and answers 503 without an FCM key', async () => {
    const made = await http('/push/v1/handles', { method: 'POST', body: JSON.stringify({ fcmToken: token }) });
    const { handle } = (await made.json()) as { handle: string };
    const { serverId, hostToken } = identity('push-host');
    const send = (auth: string | null) =>
      http('/push/v1/send', {
        method: 'POST',
        body: JSON.stringify({ serverId, handle, payload: 'p'.repeat(40) }),
        headers: auth === null ? {} : { authorization: `Bearer ${auth}` },
      });
    expect((await send(null)).status).toBe(401);
    expect((await send(identity('other').hostToken)).status).toBe(401);
    const unavailable = await send(hostToken);
    expect(unavailable.status).toBe(503);
    expect(await unavailable.json()).toEqual({ error: 'push-unavailable' });
  });
});
