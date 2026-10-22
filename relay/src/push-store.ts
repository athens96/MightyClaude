import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

import { SERVER_ID_PATTERN } from './protocol.ts';

/** 32 random bytes, base64url without padding. */
export const HANDLE_PATTERN = /^[A-Za-z0-9_-]{43}$/;
/** FCM registration tokens: printable ASCII, no spaces. */
export const FCM_TOKEN_PATTERN = /^[\x21-\x7e]{20,4096}$/;

const DAY_MS = 86_400_000;

export function newPushSecret(): string {
  return randomBytes(32).toString('base64url');
}

function sha256Hex(value: string): string {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

export interface PushHandleRecord {
  readonly handle: string;
  /** SHA-256 of the phone's handle secret; the secret itself is never stored. */
  readonly secretHash: string;
  fcmToken: string;
  /** The host the handle was first used by; null until then. */
  serverId: string | null;
  readonly createdAt: number;
  usedAt: number;
}

export type BindResult =
  | { readonly kind: 'ok'; readonly fcmToken: string }
  | { readonly kind: 'unknown' }
  | { readonly kind: 'foreign' };

export interface PushStoreOptions {
  /** File on the server volume; '' keeps handles in memory only (tests, local runs). */
  readonly path: string;
  readonly maxHandles: number;
  /** A handle neither refreshed nor used for this long is dropped. */
  readonly idleMs: number;
  readonly log: (line: string) => void;
}

function validRecord(value: unknown): value is PushHandleRecord {
  if (typeof value !== 'object' || value === null) return false;
  const r = value as Record<string, unknown>;
  return (
    typeof r['handle'] === 'string' && HANDLE_PATTERN.test(r['handle']) &&
    typeof r['secretHash'] === 'string' && /^[0-9a-f]{64}$/.test(r['secretHash']) &&
    typeof r['fcmToken'] === 'string' && FCM_TOKEN_PATTERN.test(r['fcmToken']) &&
    (r['serverId'] === null || (typeof r['serverId'] === 'string' && SERVER_ID_PATTERN.test(r['serverId']))) &&
    Number.isFinite(r['createdAt']) && Number.isFinite(r['usedAt'])
  );
}

/**
 * Push handles → FCM tokens. A handle is what a phone hands its hosts; the token
 * never leaves the relay. A handle answers only the first host that used it.
 */
export class PushHandleStore {
  readonly #records = new Map<string, PushHandleRecord>();
  /** The usedAt value last written to disk, per handle; keeps bind's daily write from being starved by its own updates. */
  readonly #persistedUsedAt = new Map<string, number>();
  readonly #options: PushStoreOptions;
  #writing: Promise<void> = Promise.resolve();

  private constructor(options: PushStoreOptions) {
    this.#options = options;
  }

  static async open(options: PushStoreOptions, now: number): Promise<PushHandleStore> {
    const store = new PushHandleStore(options);
    if (options.path !== '') await store.#load(now);
    return store;
  }

  get size(): number {
    return this.#records.size;
  }

  async register(fcmToken: string, now: number): Promise<{ handle: string; secret: string } | null> {
    if (!FCM_TOKEN_PATTERN.test(fcmToken)) return null;
    this.#dropIdle(now);
    if (this.#records.size >= this.#options.maxHandles) return null;
    const handle = newPushSecret();
    const secret = newPushSecret();
    this.#records.set(handle, { handle, secretHash: sha256Hex(secret), fcmToken, serverId: null, createdAt: now, usedAt: now });
    await this.#save();
    return { handle, secret };
  }

  async refresh(handle: string, secret: string, fcmToken: string, now: number): Promise<boolean> {
    const record = this.#owned(handle, secret);
    if (record === null || !FCM_TOKEN_PATTERN.test(fcmToken)) return false;
    record.fcmToken = fcmToken;
    record.usedAt = now;
    await this.#save();
    return true;
  }

  async remove(handle: string, secret: string): Promise<boolean> {
    if (this.#owned(handle, secret) === null) return false;
    this.#records.delete(handle);
    await this.#save();
    return true;
  }

  async bind(handle: string, serverId: string, now: number): Promise<BindResult> {
    const record = this.#records.get(handle);
    if (record === undefined) return { kind: 'unknown' };
    if (record.serverId !== null && record.serverId !== serverId) return { kind: 'foreign' };
    const firstUse = record.serverId === null;
    const stale = now - (this.#persistedUsedAt.get(handle) ?? record.usedAt) > DAY_MS;
    record.serverId = serverId;
    record.usedAt = now;
    // usedAt reaches the disk at most daily per handle (measured from the last write); it only feeds the idle sweep.
    if (firstUse || stale) await this.#save();
    return { kind: 'ok', fcmToken: record.fcmToken };
  }

  async drop(handle: string): Promise<void> {
    if (!this.#records.delete(handle)) return;
    await this.#save();
  }

  async flush(): Promise<void> {
    await this.#writing;
  }

  #owned(handle: string, secret: string): PushHandleRecord | null {
    const record = this.#records.get(handle);
    if (record === undefined || !HANDLE_PATTERN.test(secret)) return null;
    const stored = Buffer.from(record.secretHash, 'hex');
    const given = Buffer.from(sha256Hex(secret), 'hex');
    return stored.length === given.length && timingSafeEqual(stored, given) ? record : null;
  }

  #dropIdle(now: number): void {
    for (const [handle, record] of this.#records) {
      if (now - record.usedAt > this.#options.idleMs) this.#records.delete(handle);
    }
  }

  async #load(now: number): Promise<void> {
    const path = this.#options.path;
    let text: string;
    try {
      text = await readFile(path, 'utf8');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
      throw error;
    }
    try {
      const parsed = JSON.parse(text) as { v?: unknown; handles?: unknown };
      if (parsed.v !== 1 || !Array.isArray(parsed.handles)) throw new Error('shape');
      for (const record of parsed.handles) {
        if (validRecord(record)) this.#records.set(record.handle, { ...record });
      }
    } catch {
      await rename(path, `${path}.unreadable-${now}`);
      this.#options.log('[push] handle store unreadable; moved aside, starting empty');
      return;
    }
    this.#dropIdle(now);
  }

  #save(): Promise<void> {
    const path = this.#options.path;
    if (path === '') return Promise.resolve();
    const snapshot = JSON.stringify({ v: 1, handles: [...this.#records.values()] });
    const written = new Map([...this.#records.values()].map((r) => [r.handle, r.usedAt] as const));
    this.#writing = this.#writing
      .then(async () => {
        await mkdir(dirname(path), { recursive: true, mode: 0o700 });
        const temporary = `${path}.tmp`;
        await writeFile(temporary, snapshot, { mode: 0o600 });
        await rename(temporary, path);
        this.#persistedUsedAt.clear();
        for (const [handle, usedAt] of written) this.#persistedUsedAt.set(handle, usedAt);
      })
      .catch((error: unknown) => {
        // Memory still answers; the next write tries again.
        this.#options.log(`[push] handle store write failed: ${(error as NodeJS.ErrnoException).code ?? 'error'}`);
      });
    return this.#writing;
  }
}
