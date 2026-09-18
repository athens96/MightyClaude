import { generateClientId } from '@/lib/device-token';

/**
 * Where a host's secrets live in the secure store, and the rules for moving one from a
 * pairing key to a device token. Kept apart from the store so the rules can be tested
 * against an in-memory secret store instead of the keychain.
 */

/** Pairing keys, unchanged from the first relay build. */
export const PAIRING_KEY_PREFIX = 'mightyclaude.hostkey.v2.';
/** Device tokens, one per paired host. */
export const DEVICE_TOKEN_PREFIX = 'mightyclaude.devicetoken.v1.';
/** This install's `clientId`, shared by every host. */
export const CLIENT_ID_KEY = 'mightyclaude.clientId.v1';
/**
 * Per-host override, minted only when a host answers `device-conflict`: that Mac
 * already has a token under this install's id, so this phone needs a different one
 * there. Every other host keeps using the shared id.
 */
export const HOST_CLIENT_ID_PREFIX = 'mightyclaude.clientId.v1.host.';

/** The part of `expo-secure-store` these helpers use. */
export interface SecretStore {
  getItemAsync(key: string): Promise<string | null>;
  setItemAsync(key: string, value: string): Promise<void>;
  deleteItemAsync(key: string): Promise<void>;
}

/** The install's `clientId`, generating and storing one the first time it is asked for. */
export async function ensureClientId(
  store: SecretStore,
  make: () => string = generateClientId,
): Promise<string> {
  const existing = await store.getItemAsync(CLIENT_ID_KEY);
  if (existing && existing.length > 0) return existing;
  const created = make();
  await store.setItemAsync(CLIENT_ID_KEY, created);
  return created;
}

/** The host-specific `clientId`, if one was ever minted for this Mac. */
export async function readHostClientId(
  store: SecretStore,
  hostId: string,
): Promise<string | undefined> {
  const value = await store.getItemAsync(`${HOST_CLIENT_ID_PREFIX}${hostId}`);
  return value && value.length > 0 ? value : undefined;
}

/**
 * Makes and stores a fresh `clientId` for one host, replacing any earlier override. The
 * answer to `device-conflict`: the shared id is taken on that Mac, so this phone
 * introduces itself there under a new one.
 */
export async function mintHostClientId(
  store: SecretStore,
  hostId: string,
  make: () => string = generateClientId,
): Promise<string> {
  const created = make();
  await store.setItemAsync(`${HOST_CLIENT_ID_PREFIX}${hostId}`, created);
  return created;
}

export interface HostSecrets {
  pairingKey?: string;
  deviceToken?: string;
}

/** True when the host can still be reached: it has a key, a token, or both. */
export function canAuthenticate(secrets: HostSecrets): boolean {
  return Boolean(secrets.pairingKey ?? secrets.deviceToken);
}

export async function readHostSecrets(
  store: SecretStore,
  hostId: string,
): Promise<HostSecrets> {
  const [pairingKey, deviceToken] = await Promise.all([
    store.getItemAsync(`${PAIRING_KEY_PREFIX}${hostId}`),
    store.getItemAsync(`${DEVICE_TOKEN_PREFIX}${hostId}`),
  ]);
  const secrets: HostSecrets = {};
  if (pairingKey) secrets.pairingKey = pairingKey;
  if (deviceToken) secrets.deviceToken = deviceToken;
  return secrets;
}

/**
 * Stores what a fresh pairing produced. A host that answered with a token never keeps
 * its pairing key: the Mac replaces that key as soon as it releases any device, so a
 * stored copy could only ever be stale.
 */
export async function writeHostSecrets(
  store: SecretStore,
  hostId: string,
  secrets: HostSecrets,
): Promise<void> {
  if (secrets.deviceToken) {
    await store.setItemAsync(`${DEVICE_TOKEN_PREFIX}${hostId}`, secrets.deviceToken);
    await store.deleteItemAsync(`${PAIRING_KEY_PREFIX}${hostId}`);
    return;
  }
  await store.deleteItemAsync(`${DEVICE_TOKEN_PREFIX}${hostId}`);
  if (secrets.pairingKey) {
    await store.setItemAsync(`${PAIRING_KEY_PREFIX}${hostId}`, secrets.pairingKey);
  } else {
    await store.deleteItemAsync(`${PAIRING_KEY_PREFIX}${hostId}`);
  }
}

/** The migration: an already paired host is handed a token on its next connect. */
export async function adoptDeviceToken(
  store: SecretStore,
  hostId: string,
  deviceToken: string,
): Promise<void> {
  await writeHostSecrets(store, hostId, { deviceToken });
}

/**
 * Drops the two secrets, keeping the host's `clientId`. Used when the Mac refuses what
 * we hold: the entry stays on the list to be paired again, and pairing again under the
 * same id is exactly what that Mac expects.
 */
export async function forgetHostSecrets(store: SecretStore, hostId: string): Promise<void> {
  await store.deleteItemAsync(`${PAIRING_KEY_PREFIX}${hostId}`);
  await store.deleteItemAsync(`${DEVICE_TOKEN_PREFIX}${hostId}`);
}

/** Everything this host ever stored, including its `clientId` override. */
export async function forgetHost(store: SecretStore, hostId: string): Promise<void> {
  await forgetHostSecrets(store, hostId);
  await store.deleteItemAsync(`${HOST_CLIENT_ID_PREFIX}${hostId}`);
}
