import { mkdtemp, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { HANDLE_PATTERN, PushHandleStore, type PushStoreOptions } from '../src/push-store.ts';
import { SlidingWindowLimiter } from '../src/rate.ts';

const TOKEN_A = 'fcm-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const TOKEN_B = 'fcm-token-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const SERVER_1 = 'a'.repeat(64);
const SERVER_2 = 'b'.repeat(64);
const DAY = 86_400_000;

function options(path: string, overrides: Partial<PushStoreOptions> = {}): PushStoreOptions {
  return { path, maxHandles: 100, idleMs: 120 * DAY, log: () => {}, ...overrides };
}

describe('SlidingWindowLimiter', () => {
  it('allows up to the limit inside the window and names the wait', () => {
    const limiter = new SlidingWindowLimiter(10_000, 2);
    expect(limiter.check('k', 0)).toEqual({ ok: true });
    limiter.record('k', 0);
    limiter.record('k', 1_000);
    expect(limiter.check('k', 2_000)).toEqual({ ok: false, retryAfterSecs: 8 });
    expect(limiter.check('k', 10_000)).toEqual({ ok: true });
  });

  it('forgets keys whose events aged out', () => {
    const limiter = new SlidingWindowLimiter(1_000, 1);
    limiter.record('a', 0);
    limiter.sweep(5_000);
    expect(limiter.size).toBe(0);
  });
});

describe('PushHandleStore', () => {
  it('registers an unguessable handle and binds it to the first serverId', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    const made = await store.register(TOKEN_A, 0);
    expect(made).not.toBeNull();
    expect(made!.handle).toMatch(HANDLE_PATTERN);
    expect(made!.secret).toMatch(HANDLE_PATTERN);
    expect(made!.secret).not.toBe(made!.handle);
    expect(await store.bind(made!.handle, SERVER_1, 1)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
    expect(await store.bind(made!.handle, SERVER_2, 2)).toEqual({ kind: 'foreign' });
    expect(await store.bind(made!.handle, SERVER_1, 3)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
    expect(await store.bind('x'.repeat(43), SERVER_1, 4)).toEqual({ kind: 'unknown' });
  });

  it('refreshes and removes only with the handle secret', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    const made = (await store.register(TOKEN_A, 0))!;
    expect(await store.refresh(made.handle, 'y'.repeat(43), TOKEN_B, 1)).toBe(false);
    expect(await store.refresh(made.handle, made.secret, TOKEN_B, 1)).toBe(true);
    expect(await store.bind(made.handle, SERVER_1, 2)).toEqual({ kind: 'ok', fcmToken: TOKEN_B });
    expect(await store.remove(made.handle, 'y'.repeat(43))).toBe(false);
    expect(await store.remove(made.handle, made.secret)).toBe(true);
    expect(await store.bind(made.handle, SERVER_1, 3)).toEqual({ kind: 'unknown' });
  });

  it('drops a handle FCM called gone', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    const made = (await store.register(TOKEN_A, 0))!;
    await store.drop(made.handle);
    expect(store.size).toBe(0);
  });

  it('refuses a new handle when full, after dropping idle ones', async () => {
    const store = await PushHandleStore.open(options('', { maxHandles: 1, idleMs: DAY }), 0);
    expect(await store.register(TOKEN_A, 0)).not.toBeNull();
    expect(await store.register(TOKEN_B, 1)).toBeNull();
    expect(await store.register(TOKEN_B, 2 * DAY)).not.toBeNull();
    expect(store.size).toBe(1);
  });

  it('keeps handles across a restart in an owner-only file', async () => {
    const folder = await mkdtemp(join(tmpdir(), 'push-store-'));
    const path = join(folder, 'data', 'push-handles.json');
    const first = await PushHandleStore.open(options(path), 0);
    const made = (await first.register(TOKEN_A, 0))!;
    await first.bind(made.handle, SERVER_1, 1);
    await first.flush();
    expect((await stat(path)).mode & 0o777).toBe(0o600);
    const text = await readFile(path, 'utf8');
    expect(text).not.toContain(made.secret);
    const second = await PushHandleStore.open(options(path), 2);
    expect(await second.bind(made.handle, SERVER_2, 3)).toEqual({ kind: 'foreign' });
    expect(await second.refresh(made.handle, made.secret, TOKEN_B, 4)).toBe(true);
  });

  it('keeps a handle that is used daily alive across a restart past the idle limit', async () => {
    const folder = await mkdtemp(join(tmpdir(), 'push-store-'));
    const path = join(folder, 'push-handles.json');
    const idleMs = 120 * DAY;
    const first = await PushHandleStore.open(options(path, { idleMs }), 0);
    const made = (await first.register(TOKEN_A, 0))!;
    for (let day = 1; day <= 130; day += 1) {
      expect(await first.bind(made.handle, SERVER_1, day * DAY)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
      await first.flush();
    }
    const second = await PushHandleStore.open(options(path, { idleMs }), 130 * DAY + 1);
    expect(await second.bind(made.handle, SERVER_1, 130 * DAY + 2)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
  });

  it('refuses a malformed FCM token on register and refresh', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    expect(await store.register('too short', 0)).toBeNull();
    expect(store.size).toBe(0);
    const made = (await store.register(TOKEN_A, 0))!;
    expect(await store.refresh(made.handle, made.secret, 'has a space in it, not valid at all', 1)).toBe(false);
    expect(await store.bind(made.handle, SERVER_1, 2)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
  });

  it('moves an unreadable file aside and starts empty', async () => {
    const folder = await mkdtemp(join(tmpdir(), 'push-store-'));
    const path = join(folder, 'push-handles.json');
    await writeFile(path, '{not json');
    const lines: string[] = [];
    const store = await PushHandleStore.open(options(path, { log: (line) => lines.push(line) }), 7);
    expect(store.size).toBe(0);
    expect(await readdir(folder)).toContain('push-handles.json.unreadable-7');
    expect(lines).toEqual(['[push] handle store unreadable; moved aside, starting empty']);
  });
});
