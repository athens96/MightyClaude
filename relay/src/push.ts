import { createPrivateKey } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { isIP } from 'node:net';

import type { RelayConfig } from './config.ts';
import { FcmHttpV1Sender, parseServiceAccount, type PushSender } from './fcm.ts';
import type { RelayLogger } from './hub.ts';
import { HOST_TOKEN_PATTERN, ownsServerId, SERVER_ID_PATTERN } from './protocol.ts';
import { FCM_TOKEN_PATTERN, HANDLE_PATTERN, PushHandleStore } from './push-store.ts';
import { SlidingWindowLimiter, type RateDecision } from './rate.ts';

/** A sealed push (docs/relay.md "푸시 알림"): base64url, at most 3072 characters. */
export const PUSH_PAYLOAD_PATTERN = /^[A-Za-z0-9_-]{24,3072}$/;
const PUSH_PAYLOAD_MAX = 3072;
const BODY_LIMIT = 8 * 1024;
const DAY_MS = 86_400_000;
/** A Retry-After FCM sends on is kept between 1 s and an hour. */
const RETRY_AFTER_MAX_SECS = 3600;
const HANDLE_PATH = /^\/push\/v1\/handles\/([A-Za-z0-9_-]{43})$/;

type Body = { readonly ok: true; readonly value: Record<string, unknown> } | { readonly ok: false; readonly status: 400 | 413 };

async function readBody(req: IncomingMessage): Promise<Body> {
  const declared = Number(req.headers['content-length'] ?? '0');
  let size = 0;
  let tooLarge = declared > BODY_LIMIT;
  const chunks: Buffer[] = [];
  for await (const chunk of req as AsyncIterable<Buffer>) {
    size += chunk.length;
    if (size > BODY_LIMIT) tooLarge = true;
    // Keep draining so the socket stays usable for the reply.
    if (!tooLarge) chunks.push(chunk);
  }
  if (tooLarge) return { ok: false, status: 413 };
  try {
    const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return { ok: false, status: 400 };
    return { ok: true, value: parsed as Record<string, unknown> };
  } catch {
    return { ok: false, status: 400 };
  }
}

function bearer(req: IncomingMessage): string | null {
  const header = req.headers['authorization'];
  if (typeof header !== 'string' || !header.startsWith('Bearer ')) return null;
  return header.slice('Bearer '.length).trim();
}

function reply(res: ServerResponse, status: number, body?: unknown, retryAfterSecs?: number): void {
  const headers: Record<string, string> = { 'cache-control': 'no-store' };
  if (retryAfterSecs !== undefined) headers['retry-after'] = String(retryAfterSecs);
  if (body === undefined) {
    res.writeHead(status, headers);
    res.end();
    return;
  }
  headers['content-type'] = 'application/json; charset=utf-8';
  res.writeHead(status, headers);
  res.end(JSON.stringify(body));
}

function failure(res: ServerResponse, status: number, code: string, retryAfterSecs?: number): void {
  reply(res, status, { error: code }, retryAfterSecs);
}

function clampRetryAfter(secs: number): number {
  return Number.isFinite(secs) ? Math.min(RETRY_AFTER_MAX_SECS, Math.max(1, Math.ceil(secs))) : 60;
}

/** The eight 16-bit groups of a valid IPv6 address (zone already removed), or null. */
function ipv6Groups(ip: string): number[] | null {
  let text = ip;
  const lastColon = text.lastIndexOf(':');
  const tail = text.slice(lastColon + 1);
  if (tail.includes('.')) {
    const octets = tail.split('.').map(Number);
    if (octets.length !== 4 || octets.some((o) => !Number.isInteger(o) || o < 0 || o > 255)) return null;
    const [a = 0, b = 0, c = 0, d = 0] = octets;
    text = `${text.slice(0, lastColon + 1)}${((a << 8) | b).toString(16)}:${((c << 8) | d).toString(16)}`;
  }
  const halves = text.split('::');
  if (halves.length > 2) return null;
  const parse = (part: string): number[] => (part === '' ? [] : part.split(':').map((g) => Number.parseInt(g, 16)));
  const head = parse(halves[0] ?? '');
  const rest = halves.length === 2 ? parse(halves[1] ?? '') : [];
  const fill = halves.length === 2 ? 8 - head.length - rest.length : 0;
  if (fill < 0) return null;
  const groups = [...head, ...new Array<number>(fill).fill(0), ...rest];
  return groups.length === 8 && groups.every((g) => Number.isInteger(g) && g >= 0 && g <= 0xffff) ? groups : null;
}

/**
 * The key push limits count a source under: IPv4 as is, IPv6 by its /64 (one subscriber
 * usually holds a whole /64), and an IPv4-mapped IPv6 address as its IPv4 address.
 */
export function pushIpKey(ip: string): string {
  const bare = ip.split('%')[0] ?? ip;
  if (isIP(bare) !== 6) return ip;
  const groups = ipv6Groups(bare);
  if (groups === null) return ip;
  const [g0, g1, g2, g3, g4, g5, g6 = 0, g7 = 0] = groups;
  if (g0 === 0 && g1 === 0 && g2 === 0 && g3 === 0 && g4 === 0 && g5 === 0xffff) {
    return `${g6 >> 8}.${g6 & 0xff}.${g7 >> 8}.${g7 & 0xff}`;
  }
  return `${groups.slice(0, 4).map((g) => g.toString(16)).join(':')}::/64`;
}

function shortId(serverId: string): string {
  return serverId.slice(0, 8);
}

/** Phone handle registration and host push sends (docs/relay.md "푸시 알림"). */
export class PushRoutes {
  readonly #store: PushHandleStore;
  readonly #sender: PushSender | null;
  readonly #log: RelayLogger;
  readonly #now: () => number;
  readonly #handleOpsByIp: SlidingWindowLimiter;
  readonly #sendByServerId: SlidingWindowLimiter;
  readonly #sendByIp: SlidingWindowLimiter;
  readonly #sendGlobal: SlidingWindowLimiter;
  readonly #registrationsGlobal: SlidingWindowLimiter;
  readonly #sweeper: NodeJS.Timeout;

  constructor(config: RelayConfig, store: PushHandleStore, sender: PushSender | null, log: RelayLogger, now: () => number) {
    this.#store = store;
    this.#sender = sender;
    this.#log = log;
    this.#now = now;
    const window = config.pushRateWindowMs;
    this.#handleOpsByIp = new SlidingWindowLimiter(window, config.pushMaxHandleOpsPerIp);
    this.#sendByServerId = new SlidingWindowLimiter(window, config.pushMaxSendPerServerId);
    this.#sendByIp = new SlidingWindowLimiter(window, config.pushMaxSendPerIp);
    this.#sendGlobal = new SlidingWindowLimiter(window, config.pushMaxSendGlobal);
    this.#registrationsGlobal = new SlidingWindowLimiter(window, config.pushMaxRegistrationsGlobal);
    this.#sweeper = setInterval(() => {
      const at = this.#now();
      const limiters = [this.#handleOpsByIp, this.#sendByServerId, this.#sendByIp, this.#sendGlobal, this.#registrationsGlobal];
      for (const limiter of limiters) limiter.sweep(at);
    }, window);
    this.#sweeper.unref?.();
  }

  /** Handles `/push/v1/*`; false for every other path. */
  async handle(req: IncomingMessage, res: ServerResponse, sourceIp: string): Promise<boolean> {
    const path = new URL(req.url ?? '/', 'http://relay.invalid').pathname;
    if (!path.startsWith('/push/v1/')) return false;
    const ip = pushIpKey(sourceIp);
    if (req.method === 'POST' && path === '/push/v1/handles') await this.#register(req, res, ip);
    else if (req.method === 'POST' && path === '/push/v1/send') await this.#send(req, res, ip);
    else if ((req.method === 'PUT' || req.method === 'DELETE') && HANDLE_PATH.test(path)) await this.#manage(req, res, ip, HANDLE_PATH.exec(path)?.[1] ?? '');
    else {
      req.resume();
      reply(res, 404, { error: 'not-found' });
    }
    return true;
  }

  async close(): Promise<void> {
    clearInterval(this.#sweeper);
    await this.#store.flush();
  }

  #take(checks: ReadonlyArray<readonly [SlidingWindowLimiter, string]>): RateDecision {
    const now = this.#now();
    for (const [limiter, key] of checks) {
      const decision = limiter.check(key, now);
      if (!decision.ok) return decision;
    }
    for (const [limiter, key] of checks) limiter.record(key, now);
    return { ok: true };
  }

  async #register(req: IncomingMessage, res: ServerResponse, ip: string): Promise<void> {
    const body = await readBody(req);
    if (!body.ok) return failure(res, body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const token = body.value['fcmToken'];
    if (typeof token !== 'string' || !FCM_TOKEN_PATTERN.test(token)) return failure(res, 400, 'bad-request');
    const rate = this.#take([[this.#handleOpsByIp, ip], [this.#registrationsGlobal, '*']]);
    if (!rate.ok) return failure(res, 429, 'rate-limited', rate.retryAfterSecs);
    const made = await this.#store.register(token, this.#now());
    if (made === null) return failure(res, 503, 'full');
    reply(res, 201, made);
  }

  async #manage(req: IncomingMessage, res: ServerResponse, ip: string, handle: string): Promise<void> {
    const secret = bearer(req) ?? '';
    const body = req.method === 'PUT' ? await readBody(req) : null;
    if (req.method !== 'PUT') req.resume();
    if (body !== null && !body.ok) return failure(res, body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const token = body?.ok === true ? body.value['fcmToken'] : undefined;
    if (req.method === 'PUT' && (typeof token !== 'string' || !FCM_TOKEN_PATTERN.test(token))) return failure(res, 400, 'bad-request');
    const rate = this.#take([[this.#handleOpsByIp, ip]]);
    if (!rate.ok) return failure(res, 429, 'rate-limited', rate.retryAfterSecs);
    const done = req.method === 'PUT'
      ? await this.#store.refresh(handle, secret, token as string, this.#now())
      : await this.#store.remove(handle, secret);
    if (!done) return failure(res, 404, 'unknown-handle');
    reply(res, 204);
  }

  async #send(req: IncomingMessage, res: ServerResponse, ip: string): Promise<void> {
    const body = await readBody(req);
    if (!body.ok) return failure(res, body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const { serverId, handle, payload } = body.value;
    const hostToken = bearer(req);
    if (typeof serverId !== 'string' || !SERVER_ID_PATTERN.test(serverId)) return failure(res, 400, 'bad-request');
    if (hostToken === null || !HOST_TOKEN_PATTERN.test(hostToken) || !ownsServerId(serverId, hostToken)) return failure(res, 401, 'unauthorized');
    if (typeof handle !== 'string' || !HANDLE_PATTERN.test(handle)) return failure(res, 400, 'bad-request');
    if (typeof payload === 'string' && payload.length > PUSH_PAYLOAD_MAX) return failure(res, 413, 'too-large');
    if (typeof payload !== 'string' || !PUSH_PAYLOAD_PATTERN.test(payload)) return failure(res, 400, 'bad-request');
    if (this.#sender === null) return failure(res, 503, 'push-unavailable');
    const rate = this.#take([[this.#sendByServerId, serverId], [this.#sendByIp, ip]]);
    if (!rate.ok) return failure(res, 429, 'rate-limited', rate.retryAfterSecs);
    const bound = await this.#store.bind(handle, serverId, this.#now());
    if (bound.kind === 'unknown') return failure(res, 404, 'unknown-handle');
    if (bound.kind === 'foreign') return failure(res, 403, 'foreign-handle');
    // Only a send that would reach FCM spends the shared budget, so guessed or foreign handles cannot use it up.
    const global = this.#take([[this.#sendGlobal, '*']]);
    if (!global.ok) return failure(res, 429, 'rate-limited', global.retryAfterSecs);
    const result = await this.#sender.send(bound.fcmToken, { v: '1', h: handle, p: payload });
    switch (result.kind) {
      case 'sent':
        this.#log(`[${shortId(serverId)}] push sent`);
        return reply(res, 202, { ok: true });
      case 'invalid-token':
        await this.#store.drop(handle);
        this.#log(`[${shortId(serverId)}] push token gone; handle dropped`);
        return failure(res, 410, 'token-gone');
      case 'retry':
        return failure(res, 502, 'fcm-failed', clampRetryAfter(result.retryAfterSecs));
      case 'failed':
        // The FCM error code (for example SENDER_ID_MISMATCH: a key from another Firebase project) names the setup problem.
        this.#log(`[${shortId(serverId)}] push failed: fcm ${result.status}${result.code === undefined ? '' : ` ${result.code}`}`);
        return reply(res, 502, result.code === undefined ? { error: 'fcm-failed' } : { error: 'fcm-failed', fcmError: result.code }, 30);
    }
  }
}

/** The store and the sender the config names; a sender that cannot be read or whose key does not parse leaves sending off, not the relay. */
export async function createPushRoutes(
  config: RelayConfig,
  log: RelayLogger,
  sender?: PushSender | null,
  now: () => number = Date.now,
): Promise<PushRoutes> {
  const store = await PushHandleStore.open(
    { path: config.pushStorePath, maxHandles: config.pushMaxHandles, idleMs: config.pushHandleIdleDays * DAY_MS, log },
    now(),
  );
  let resolved: PushSender | null = null;
  if (sender !== undefined) resolved = sender;
  else if (config.fcmServiceAccountFile !== '') {
    try {
      const account = parseServiceAccount(await readFile(config.fcmServiceAccountFile, 'utf8'));
      // A key that does not parse fails here, once, instead of on every send.
      createPrivateKey(account.privateKey);
      resolved = new FcmHttpV1Sender(account, undefined, undefined, log);
      log('[push] FCM sending on');
    } catch {
      log('[push] FCM disabled: service account unreadable');
    }
  }
  return new PushRoutes(config, store, resolved, log, now);
}
