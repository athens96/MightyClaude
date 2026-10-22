import { ApiError, type MobileClient } from '@/api/client';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';
import type { SecretStore } from '@/lib/host-secrets';
import type { PushKind } from '@/lib/push/message';

/** SecureStore key per pairing (storage id) for its relay push handle. */
export const PUSH_HANDLE_PREFIX = 'mightyclaude.push.v1.';
/** A handle and its secret alike: 32 random bytes, base64url without padding. */
const HANDLE = /^[A-Za-z0-9_-]{43}$/;

export interface PushHandleRecord {
  handle: string;
  /** Proves this phone to the relay when the token changes or the handle goes; never leaves the phone otherwise. */
  secret: string;
  relayUrl: string;
  fcmToken: string;
}

export type PushFetch = (
  url: string,
  init: { method: string; headers: Record<string, string>; body?: string },
) => Promise<{ status: number; json(): Promise<unknown> }>;

interface Deps {
  store: SecretStore;
  fetch: PushFetch;
}

/** `https://host[:port]` for a pairing's `wss://` relay (`http` for `ws`); null for anything else. */
export function relayHttpBase(relayUrl: string): string | null {
  let url: URL;
  try {
    url = new URL(relayUrl);
  } catch {
    return null;
  }
  const scheme =
    url.protocol === 'wss:' || url.protocol === 'https:'
      ? 'https:'
      : url.protocol === 'ws:' || url.protocol === 'http:'
        ? 'http:'
        : null;
  return scheme === null || url.host === '' ? null : `${scheme}//${url.host}`;
}

function parseRecord(text: string | null): PushHandleRecord | null {
  if (text === null) return null;
  try {
    const value = JSON.parse(text) as Partial<PushHandleRecord>;
    if (typeof value.handle !== 'string' || !HANDLE.test(value.handle)) return null;
    if (typeof value.secret !== 'string' || !HANDLE.test(value.secret)) return null;
    if (typeof value.relayUrl !== 'string' || typeof value.fcmToken !== 'string') return null;
    return {
      handle: value.handle,
      secret: value.secret,
      relayUrl: value.relayUrl,
      fcmToken: value.fcmToken,
    };
  } catch {
    return null;
  }
}

export async function readPushHandle(
  store: SecretStore,
  storageId: string,
): Promise<PushHandleRecord | null> {
  return parseRecord(await store.getItemAsync(PUSH_HANDLE_PREFIX + storageId));
}

async function save(
  store: SecretStore,
  storageId: string,
  record: PushHandleRecord,
): Promise<PushHandleRecord> {
  await store.setItemAsync(PUSH_HANDLE_PREFIX + storageId, JSON.stringify(record));
  return record;
}

async function drop(deps: Deps, record: PushHandleRecord): Promise<void> {
  const base = relayHttpBase(record.relayUrl);
  if (base === null) return;
  try {
    await deps.fetch(`${base}/push/v1/handles/${record.handle}`, {
      method: 'DELETE',
      headers: { authorization: `Bearer ${record.secret}` },
    });
  } catch {
    // The relay forgets idle handles on its own.
  }
}

/**
 * This pairing's handle for `fcmToken`, registering or refreshing it on the pairing's
 * relay as needed. A handle the relay forgot (404) is registered again; a handle from
 * another relay is deleted there first. Null when the relay refuses or answers oddly.
 */
export async function ensurePushHandle(
  deps: Deps,
  storageId: string,
  relayUrl: string,
  fcmToken: string,
): Promise<PushHandleRecord | null> {
  const base = relayHttpBase(relayUrl);
  if (base === null) return null;
  const existing = await readPushHandle(deps.store, storageId);
  if (existing !== null && existing.relayUrl === relayUrl) {
    if (existing.fcmToken === fcmToken) return existing;
    const res = await deps.fetch(`${base}/push/v1/handles/${existing.handle}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json', authorization: `Bearer ${existing.secret}` },
      body: JSON.stringify({ fcmToken }),
    });
    if (res.status === 204) return save(deps.store, storageId, { ...existing, fcmToken });
    if (res.status !== 404) return null;
  } else if (existing !== null) {
    await drop(deps, existing);
  }
  const res = await deps.fetch(`${base}/push/v1/handles`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ fcmToken }),
  });
  if (res.status !== 201) return null;
  const made = (await res.json()) as { handle?: unknown; secret?: unknown } | null;
  if (made === null || typeof made !== 'object') return null;
  if (typeof made.handle !== 'string' || !HANDLE.test(made.handle)) return null;
  if (typeof made.secret !== 'string' || !HANDLE.test(made.secret)) return null;
  return save(deps.store, storageId, {
    handle: made.handle,
    secret: made.secret,
    relayUrl,
    fcmToken,
  });
}

/** Deletes this pairing's handle on its relay (best effort) and on the phone. */
export async function forgetPushHandle(deps: Deps, storageId: string): Promise<void> {
  const existing = await readPushHandle(deps.store, storageId);
  if (existing !== null) await drop(deps, existing);
  await deps.store.deleteItemAsync(PUSH_HANDLE_PREFIX + storageId);
}

/** Which pairing a push was sent through: the `h` of an FCM message is that pairing's handle. */
export async function findHostByHandle(
  store: SecretStore,
  storageIds: readonly string[],
  handle: string,
): Promise<string | null> {
  for (const id of storageIds) {
    if ((await readPushHandle(store, id))?.handle === handle) return id;
  }
  return null;
}

/**
 * Gives one paired host this pairing's handle and the kinds this phone wants. A host
 * that does not advertise `"push"`, or refuses a legacy (token-less) pairing with 409,
 * is `unsupported`; anything else that goes wrong is `failed` and is tried again later.
 */
export async function syncHostPush(
  deps: Deps & { client: Pick<MobileClient, 'info' | 'registerPush'> },
  host: { id: string; relayUrl: string },
  fcmToken: string,
  kinds: PushKind[],
): Promise<'registered' | 'unsupported' | 'failed'> {
  try {
    if (!hasCapability(parseCapabilities(await deps.client.info()), 'push')) return 'unsupported';
    const record = await ensurePushHandle(deps, host.id, host.relayUrl, fcmToken);
    if (record === null) return 'failed';
    await deps.client.registerPush({ handle: record.handle, kinds });
    return 'registered';
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) return 'unsupported';
    return 'failed';
  }
}
