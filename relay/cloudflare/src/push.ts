/**
 * Push handles and sends (../src/push.ts and ../src/push-store.ts on Cloudflare, docs/relay.md
 * "푸시 알림"): one object for every handle, kept in its SQLite storage. The rate windows live
 * in memory as on the Node relay, so an eviction starts them over.
 */

import { DurableObject } from 'cloudflare:workers';

import { HOST_TOKEN_PATTERN, SERVER_ID_PATTERN } from '../../src/protocol.ts';
import { SlidingWindowLimiter, type RateDecision } from '../../src/rate.ts';
import type { PushSender } from '../../src/fcm-core.ts';
import { workerFcmSender } from './fcm.ts';
import { HUB_HEADERS } from './hub.ts';
import { newSecret, ownsServerIdAsync, sameDigest, sha256Hex } from './identity.ts';
import { pushIpKey } from './ip.ts';

/** Same patterns as ../src/push-store.ts and ../src/push.ts. */
export const HANDLE_PATTERN = /^[A-Za-z0-9_-]{43}$/;
export const FCM_TOKEN_PATTERN = /^[\x21-\x7e]{20,4096}$/;
export const PUSH_PAYLOAD_PATTERN = /^[A-Za-z0-9_-]{24,3072}$/;
const PUSH_PAYLOAD_MAX = 3072;
const BODY_LIMIT = 8 * 1024;
const DAY_MS = 86_400_000;
const RETRY_AFTER_MAX_SECS = 3600;
const HANDLE_PATH = /^\/push\/v1\/handles\/([A-Za-z0-9_-]{43})$/;

interface PushEnv {
  readonly FCM_SERVICE_ACCOUNT?: string;
  readonly PUSH_RATE_WINDOW_MS?: string;
  readonly PUSH_MAX_SEND_PER_SERVER_ID?: string;
  readonly PUSH_MAX_SEND_PER_IP?: string;
  readonly PUSH_MAX_SEND_GLOBAL?: string;
  readonly PUSH_MAX_HANDLE_OPS_PER_IP?: string;
  readonly PUSH_MAX_REGISTRATIONS_GLOBAL?: string;
  readonly PUSH_MAX_HANDLES?: string;
  readonly PUSH_HANDLE_IDLE_DAYS?: string;
}

function envInt(raw: string | undefined, fallback: number): number {
  const parsed = Number.parseInt(raw ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function reply(status: number, body?: unknown, retryAfterSecs?: number): Response {
  const headers: Record<string, string> = { 'cache-control': 'no-store' };
  if (retryAfterSecs !== undefined) headers['retry-after'] = String(retryAfterSecs);
  if (body === undefined) return new Response(null, { status, headers });
  headers['content-type'] = 'application/json; charset=utf-8';
  return new Response(JSON.stringify(body), { status, headers });
}

function failure(status: number, code: string, retryAfterSecs?: number): Response {
  return reply(status, { error: code }, retryAfterSecs);
}

function clampRetryAfter(secs: number): number {
  return Number.isFinite(secs) ? Math.min(RETRY_AFTER_MAX_SECS, Math.max(1, Math.ceil(secs))) : 60;
}

type Body = { readonly ok: true; readonly value: Record<string, unknown> } | { readonly ok: false; readonly status: 400 | 413 };

async function readBody(request: Request): Promise<Body> {
  const declared = Number(request.headers.get('content-length') ?? '0');
  if (declared > BODY_LIMIT) return { ok: false, status: 413 };
  const bytes = await request.arrayBuffer();
  if (bytes.byteLength > BODY_LIMIT) return { ok: false, status: 413 };
  try {
    const parsed: unknown = JSON.parse(new TextDecoder().decode(bytes));
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return { ok: false, status: 400 };
    return { ok: true, value: parsed as Record<string, unknown> };
  } catch {
    return { ok: false, status: 400 };
  }
}

function bearer(request: Request): string | null {
  const header = request.headers.get('authorization');
  if (header === null || !header.startsWith('Bearer ')) return null;
  return header.slice('Bearer '.length).trim();
}

interface HandleRow {
  readonly handle: string;
  readonly secret_hash: string;
  readonly fcm_token: string;
  readonly server_id: string | null;
  readonly used_at: number;
  [key: string]: SqlStorageValue;
}

export class PushStore extends DurableObject<PushEnv> {
  readonly #sql: SqlStorage;
  readonly #sender: PushSender | null;
  readonly #maxHandles: number;
  readonly #idleMs: number;
  readonly #handleOpsByIp: SlidingWindowLimiter;
  readonly #sendByServerId: SlidingWindowLimiter;
  readonly #sendByIp: SlidingWindowLimiter;
  readonly #sendGlobal: SlidingWindowLimiter;
  readonly #registrationsGlobal: SlidingWindowLimiter;

  constructor(ctx: DurableObjectState, env: PushEnv) {
    super(ctx, env);
    this.#sql = ctx.storage.sql;
    this.#sql.exec(`CREATE TABLE IF NOT EXISTS handles (
      handle TEXT PRIMARY KEY,
      secret_hash TEXT NOT NULL,
      fcm_token TEXT NOT NULL,
      server_id TEXT,
      created_at INTEGER NOT NULL,
      used_at INTEGER NOT NULL
    )`);
    this.#sender = workerFcmSender(env.FCM_SERVICE_ACCOUNT, (line) => console.log(line));
    this.#maxHandles = envInt(env.PUSH_MAX_HANDLES, 20_000);
    this.#idleMs = envInt(env.PUSH_HANDLE_IDLE_DAYS, 120) * DAY_MS;
    const window = envInt(env.PUSH_RATE_WINDOW_MS, 600_000);
    this.#handleOpsByIp = new SlidingWindowLimiter(window, envInt(env.PUSH_MAX_HANDLE_OPS_PER_IP, 30));
    this.#sendByServerId = new SlidingWindowLimiter(window, envInt(env.PUSH_MAX_SEND_PER_SERVER_ID, 120));
    this.#sendByIp = new SlidingWindowLimiter(window, envInt(env.PUSH_MAX_SEND_PER_IP, 240));
    this.#sendGlobal = new SlidingWindowLimiter(window, envInt(env.PUSH_MAX_SEND_GLOBAL, 3000));
    this.#registrationsGlobal = new SlidingWindowLimiter(window, envInt(env.PUSH_MAX_REGISTRATIONS_GLOBAL, 300));
  }

  override async fetch(request: Request): Promise<Response> {
    const path = new URL(request.url).pathname;
    const ip = pushIpKey(request.headers.get(HUB_HEADERS.ip) ?? '0.0.0.0');
    const now = Date.now();
    this.#sweep(now);
    if (request.method === 'POST' && path === '/push/v1/handles') return this.#register(request, ip, now);
    if (request.method === 'POST' && path === '/push/v1/send') return this.#send(request, ip, now);
    const managed = HANDLE_PATH.exec(path);
    if ((request.method === 'PUT' || request.method === 'DELETE') && managed !== null) return this.#manage(request, ip, managed[1] ?? '', now);
    return reply(404, { error: 'not-found' });
  }

  #sweep(now: number): void {
    for (const limiter of [this.#handleOpsByIp, this.#sendByServerId, this.#sendByIp, this.#sendGlobal, this.#registrationsGlobal]) limiter.sweep(now);
  }

  #take(checks: ReadonlyArray<readonly [SlidingWindowLimiter, string]>, now: number): RateDecision {
    for (const [limiter, key] of checks) {
      const decision = limiter.check(key, now);
      if (!decision.ok) return decision;
    }
    for (const [limiter, key] of checks) limiter.record(key, now);
    return { ok: true };
  }

  async #register(request: Request, ip: string, now: number): Promise<Response> {
    const body = await readBody(request);
    if (!body.ok) return failure(body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const token = body.value['fcmToken'];
    if (typeof token !== 'string' || !FCM_TOKEN_PATTERN.test(token)) return failure(400, 'bad-request');
    const rate = this.#take([[this.#handleOpsByIp, ip], [this.#registrationsGlobal, '*']], now);
    if (!rate.ok) return failure(429, 'rate-limited', rate.retryAfterSecs);
    this.#sql.exec('DELETE FROM handles WHERE used_at < ?', now - this.#idleMs);
    let count = this.#count();
    if (count >= this.#maxHandles) {
      console.log('[push] handle store full');
      // Room comes only from handles no host has used yet, least recently used first; bound ones stay.
      while (count >= this.#maxHandles) {
        const unused = this.#sql.exec<{ handle: string }>('SELECT handle FROM handles WHERE server_id IS NULL ORDER BY used_at ASC LIMIT 1').toArray()[0];
        if (unused === undefined) return failure(503, 'full');
        this.#sql.exec('DELETE FROM handles WHERE handle = ?', unused.handle);
        count -= 1;
      }
    }
    const handle = newSecret();
    const secret = newSecret();
    this.#sql.exec(
      'INSERT INTO handles (handle, secret_hash, fcm_token, server_id, created_at, used_at) VALUES (?, ?, ?, NULL, ?, ?)',
      handle, await sha256Hex(secret), token, now, now,
    );
    return reply(201, { handle, secret });
  }

  async #manage(request: Request, ip: string, handle: string, now: number): Promise<Response> {
    const secret = bearer(request) ?? '';
    const isPut = request.method === 'PUT';
    const body = isPut ? await readBody(request) : null;
    if (body !== null && !body.ok) return failure(body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const token = body?.ok === true ? body.value['fcmToken'] : undefined;
    if (isPut && (typeof token !== 'string' || !FCM_TOKEN_PATTERN.test(token))) return failure(400, 'bad-request');
    const rate = this.#take([[this.#handleOpsByIp, ip]], now);
    if (!rate.ok) return failure(429, 'rate-limited', rate.retryAfterSecs);
    const row = this.#row(handle);
    if (row === null || !HANDLE_PATTERN.test(secret) || !sameDigest(row.secret_hash, await sha256Hex(secret))) return failure(404, 'unknown-handle');
    if (isPut) this.#sql.exec('UPDATE handles SET fcm_token = ?, used_at = ? WHERE handle = ?', token as string, now, handle);
    else this.#sql.exec('DELETE FROM handles WHERE handle = ?', handle);
    return reply(204);
  }

  async #send(request: Request, ip: string, now: number): Promise<Response> {
    const body = await readBody(request);
    if (!body.ok) return failure(body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const { serverId, handle, payload } = body.value;
    const hostToken = bearer(request);
    if (typeof serverId !== 'string' || !SERVER_ID_PATTERN.test(serverId)) return failure(400, 'bad-request');
    if (hostToken === null || !HOST_TOKEN_PATTERN.test(hostToken) || !(await ownsServerIdAsync(serverId, hostToken))) return failure(401, 'unauthorized');
    if (typeof handle !== 'string' || !HANDLE_PATTERN.test(handle)) return failure(400, 'bad-request');
    if (typeof payload === 'string' && payload.length > PUSH_PAYLOAD_MAX) return failure(413, 'too-large');
    if (typeof payload !== 'string' || !PUSH_PAYLOAD_PATTERN.test(payload)) return failure(400, 'bad-request');
    if (this.#sender === null) return failure(503, 'push-unavailable');
    const rate = this.#take([[this.#sendByServerId, serverId], [this.#sendByIp, ip]], now);
    if (!rate.ok) return failure(429, 'rate-limited', rate.retryAfterSecs);
    const row = this.#row(handle);
    if (row === null) return failure(404, 'unknown-handle');
    if (row.server_id !== null && row.server_id !== serverId) return failure(403, 'foreign-handle');
    // usedAt only feeds the idle sweep: written on first use and then at most daily.
    if (row.server_id === null || now - row.used_at > DAY_MS) this.#sql.exec('UPDATE handles SET server_id = ?, used_at = ? WHERE handle = ?', serverId, now, handle);
    // Only a send that would reach FCM spends the shared budget, so guessed or foreign handles cannot use it up.
    const global = this.#take([[this.#sendGlobal, '*']], now);
    if (!global.ok) return failure(429, 'rate-limited', global.retryAfterSecs);
    const short = serverId.slice(0, 8);
    const result = await this.#sender.send(row.fcm_token, { v: '1', h: handle, p: payload });
    switch (result.kind) {
      case 'sent':
        console.log(`[${short}] push sent`);
        return reply(202, { ok: true });
      case 'invalid-token':
        this.#sql.exec('DELETE FROM handles WHERE handle = ?', handle);
        console.log(`[${short}] push token gone; handle dropped`);
        return failure(410, 'token-gone');
      case 'retry':
        return failure(502, 'fcm-failed', clampRetryAfter(result.retryAfterSecs));
      case 'failed':
        // The FCM error code (for example SENDER_ID_MISMATCH: a key from another Firebase project) names the setup problem.
        console.log(`[${short}] push failed: fcm ${result.status}${result.code === undefined ? '' : ` ${result.code}`}`);
        return reply(502, result.code === undefined ? { error: 'fcm-failed' } : { error: 'fcm-failed', fcmError: result.code }, 30);
    }
  }

  #row(handle: string): HandleRow | null {
    return this.#sql.exec<HandleRow>('SELECT handle, secret_hash, fcm_token, server_id, used_at FROM handles WHERE handle = ?', handle).toArray()[0] ?? null;
  }

  #count(): number {
    return Number(this.#sql.exec<{ n: number }>('SELECT COUNT(*) AS n FROM handles').one().n);
  }
}
