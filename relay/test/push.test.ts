import { createHash } from 'node:crypto';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import type { PushSendResult, PushSender } from '../src/fcm.ts';
import { startRelay, type RelayHandle } from '../src/server.ts';

const sha256Hex = (value: string): string => createHash('sha256').update(value, 'utf8').digest('hex');

function identity(label: string): { serverId: string; hostToken: string } {
  const hostToken = sha256Hex(`host-secret:${label}`);
  return { serverId: sha256Hex(hostToken), hostToken };
}

class FakeSender implements PushSender {
  readonly sent: Array<{ token: string; data: Readonly<Record<string, string>> }> = [];
  next: PushSendResult = { kind: 'sent' };
  async send(token: string, data: Readonly<Record<string, string>>): Promise<PushSendResult> {
    this.sent.push({ token, data });
    return this.next;
  }
}

const FCM_TOKEN = 'fcm-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const PAYLOAD = 'A'.repeat(100);
const relays: RelayHandle[] = [];

afterEach(async () => {
  for (const relay of relays.splice(0)) await relay.close();
});

async function relay(overrides: Parameters<typeof startRelay>[0] = {}): Promise<{ base: string; sender: FakeSender }> {
  const sender = new FakeSender();
  const handle = await startRelay({ host: '127.0.0.1', port: 0, logger: () => {}, pushSender: sender, ...overrides });
  relays.push(handle);
  return { base: `http://127.0.0.1:${handle.port}`, sender };
}

function call(base: string, method: string, path: string, body?: unknown, bearer?: string): Promise<Response> {
  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (bearer !== undefined) headers['authorization'] = `Bearer ${bearer}`;
  return fetch(`${base}${path}`, { method, headers, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
}

async function register(base: string): Promise<{ handle: string; secret: string }> {
  const res = await call(base, 'POST', '/push/v1/handles', { fcmToken: FCM_TOKEN });
  expect(res.status).toBe(201);
  return (await res.json()) as { handle: string; secret: string };
}

function send(base: string, who: { serverId: string; hostToken: string }, handle: string, payload = PAYLOAD): Promise<Response> {
  return call(base, 'POST', '/push/v1/send', { serverId: who.serverId, handle, payload }, who.hostToken);
}

describe('push handles', () => {
  it('hands out a handle and a secret for a token', async () => {
    const { base } = await relay();
    const made = await register(base);
    expect(made.handle).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(made.secret).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it('refuses a malformed token or body', async () => {
    const { base } = await relay();
    expect((await call(base, 'POST', '/push/v1/handles', { fcmToken: 'short' })).status).toBe(400);
    expect((await call(base, 'POST', '/push/v1/handles', 'nope')).status).toBe(400);
  });

  it('refreshes and deletes only with the secret', async () => {
    const { base, sender } = await relay();
    const host = identity('refresh');
    const made = await register(base);
    const NEW = 'fcm-token-bbbbbbbbbbbbbbbbbbbbbbbbbbbb';
    expect((await call(base, 'PUT', `/push/v1/handles/${made.handle}`, { fcmToken: NEW }, 'x'.repeat(43))).status).toBe(404);
    expect((await call(base, 'PUT', `/push/v1/handles/${made.handle}`, { fcmToken: NEW }, made.secret)).status).toBe(204);
    expect((await send(base, host, made.handle)).status).toBe(202);
    expect(sender.sent.at(-1)?.token).toBe(NEW);
    expect((await call(base, 'DELETE', `/push/v1/handles/${made.handle}`, undefined, made.secret)).status).toBe(204);
    expect((await send(base, host, made.handle)).status).toBe(404);
  });

  it('limits handle operations per IP', async () => {
    const { base } = await relay({ pushMaxHandleOpsPerIp: 2 });
    await register(base);
    await register(base);
    const res = await call(base, 'POST', '/push/v1/handles', { fcmToken: FCM_TOKEN });
    expect(res.status).toBe(429);
    expect(Number(res.headers.get('retry-after'))).toBeGreaterThan(0);
  });
});

describe('push send', () => {
  it('passes the sealed payload through untouched to FCM', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    const res = await send(base, identity('host-a'), made.handle);
    expect(res.status).toBe(202);
    expect(await res.json()).toEqual({ ok: true });
    expect(sender.sent).toEqual([{ token: FCM_TOKEN, data: { v: '1', h: made.handle, p: PAYLOAD } }]);
  });

  it('refuses a host token that does not hash to the serverId', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    const real = identity('host-a');
    const res = await call(base, 'POST', '/push/v1/send', { serverId: real.serverId, handle: made.handle, payload: PAYLOAD }, identity('other').hostToken);
    expect(res.status).toBe(401);
    expect((await call(base, 'POST', '/push/v1/send', { serverId: real.serverId, handle: made.handle, payload: PAYLOAD })).status).toBe(401);
    expect(sender.sent).toHaveLength(0);
  });

  it('keeps a handle to the first host that used it', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    expect((await send(base, identity('first'), made.handle)).status).toBe(202);
    const res = await send(base, identity('second'), made.handle);
    expect(res.status).toBe(403);
    expect(await res.json()).toEqual({ error: 'foreign-handle' });
    expect(sender.sent).toHaveLength(1);
  });

  it('forgets a handle whose token FCM calls gone and says 410', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    sender.next = { kind: 'invalid-token' };
    expect((await send(base, identity('host-a'), made.handle)).status).toBe(410);
    expect((await send(base, identity('host-a'), made.handle)).status).toBe(404);
  });

  it('answers 502 with Retry-After when FCM is busy', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    sender.next = { kind: 'retry', retryAfterSecs: 12 };
    const res = await send(base, identity('host-a'), made.handle);
    expect(res.status).toBe(502);
    expect(res.headers.get('retry-after')).toBe('12');
  });

  it('keeps a Retry-After from FCM to at most an hour', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    sender.next = { kind: 'retry', retryAfterSecs: 999_999 };
    const res = await send(base, identity('host-a'), made.handle);
    expect(res.status).toBe(502);
    expect(res.headers.get('retry-after')).toBe('3600');
  });

  it('turns sending off at startup when the service account key does not parse', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'relay-push-'));
    try {
      const file = join(dir, 'account.json');
      const fake = '-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----\n';
      await writeFile(file, JSON.stringify({ project_id: 'demo-project', client_email: 'sender@example.invalid', private_key: fake }));
      const lines: string[] = [];
      const handle = await startRelay({ host: '127.0.0.1', port: 0, logger: (line) => lines.push(line), fcmServiceAccountFile: file });
      relays.push(handle);
      const base = `http://127.0.0.1:${handle.port}`;
      const made = await register(base);
      expect((await send(base, identity('host-a'), made.handle)).status).toBe(503);
      expect(lines).toContain('[push] FCM disabled: service account unreadable');
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });

  it('refuses an oversized or malformed payload', async () => {
    const { base } = await relay();
    const made = await register(base);
    expect((await send(base, identity('host-a'), made.handle, 'A'.repeat(3073))).status).toBe(413);
    expect((await send(base, identity('host-a'), made.handle, 'not base64url!')).status).toBe(400);
  });

  it('rate-limits per serverId', async () => {
    const { base } = await relay({ pushMaxSendPerServerId: 2 });
    const made = await register(base);
    const host = identity('busy');
    expect((await send(base, host, made.handle)).status).toBe(202);
    expect((await send(base, host, made.handle)).status).toBe(202);
    const res = await send(base, host, made.handle);
    expect(res.status).toBe(429);
    expect(Number(res.headers.get('retry-after'))).toBeGreaterThan(0);
  });

  it('says 503 when no FCM sender is configured', async () => {
    const { base } = await relay({ pushSender: null });
    const made = await register(base);
    expect((await send(base, identity('host-a'), made.handle)).status).toBe(503);
  });

  it('keeps answering /healthz and 404 elsewhere', async () => {
    const { base } = await relay();
    expect((await fetch(`${base}/healthz`)).status).toBe(200);
    expect((await fetch(`${base}/push/v1/nope`)).status).toBe(404);
  });
});
