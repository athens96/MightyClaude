import { useEffect } from 'react';
import * as SecureStore from 'expo-secure-store';
import { create } from 'zustand';
import {
  closeHostClient,
  describeError,
  peekHostConnection,
  probeHost,
  type HostCredentials,
  type HostLeaseOptions,
} from '@/api/client';
import { RelayError, isFinalFailure, type AuthLease } from '@/api/relay/transport';
import type { DeviceAuthState } from '@/lib/device-token';
import {
  adoptDeviceToken,
  canAuthenticate,
  ensureClientId,
  forgetHost,
  forgetHostSecrets,
  mintHostClientId,
  readHostClientId,
  readHostSecrets,
  writeHostSecrets,
  type HostSecrets,
} from '@/lib/host-secrets';
import { createKeyedMutex } from '@/lib/keyed-mutex';
import { describeRelayTarget, type PairingPayload } from '@/lib/pairing';

const INDEX_KEY = 'mightyclaude.hosts.v2.index';
/** v1 (Tailscale) storage; dropped on first load of this build. */
const LEGACY_INDEX_KEY = 'mightyclaude.hosts.index';

export interface PairedHost {
  /** Storage-safe identifier derived from the relay `serverId`. */
  id: string;
  name: string;
  serverId: string;
  relayUrl: string;
  /** Host static X25519 public key (standard base64). */
  hostPublicKeyB64: string;
  /** Host-reported identity captured at pairing time. */
  hostId: string;
  appVersion: string;
  pairedAt: string;
}

export type Reachability =
  | 'unknown'
  | 'checking'
  | 'online'
  | 'unauthorized'
  | 'offline'
  | 'relay-offline';

export interface HostStatus {
  reachability: Reachability;
  detail?: string;
}

interface HostsState {
  hosts: PairedHost[];
  /** Pairing keys, by host id; a host that moved to a token has none. */
  keys: Record<string, string>;
  /** Device tokens, by host id. */
  tokens: Record<string, string>;
  /** This install's id, generated once and kept in the secure store. */
  clientId?: string;
  /** Per-host `clientId`, minted only where the shared one collided. */
  clientIds: Record<string, string>;
  status: Record<string, HostStatus>;
  loaded: boolean;
  load: () => Promise<void>;
  /** The install's id, generating it if this is the first time it is needed. */
  ensureClientId: () => Promise<string>;
  addHost: (
    payload: PairingPayload,
    info: { hostId: string; appVersion: string; deviceToken?: string },
  ) => Promise<PairedHost>;
  /** Stores the token a live tunnel was handed and drops that host's pairing key. */
  saveDeviceToken: (id: string, deviceToken: string) => Promise<void>;
  /** Makes this host its own `clientId` after it answered `device-conflict`. */
  mintClientIdFor: (id: string) => Promise<string | undefined>;
  /** Forgets a secret the host has refused for good; the entry stays, to be re-paired. */
  forgetSecrets: (id: string, detail?: string) => Promise<void>;
  removeHost: (id: string) => Promise<void>;
  refreshReachability: (id: string) => Promise<void>;
  refreshAll: () => Promise<void>;
}

/** SecureStore keys accept only alphanumerics, `.`, `-` and `_`. */
export function storageIdFor(serverId: string): string {
  return serverId.replace(/[^A-Za-z0-9._-]/g, '_');
}

/** Keeps only v2 entries; v1 hosts (host/port/key) are silently discarded. */
export function parseIndex(raw: string | null): PairedHost[] {
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((entry): entry is PairedHost => {
      if (entry === null || typeof entry !== 'object') return false;
      const candidate = entry as Partial<PairedHost>;
      return (
        typeof candidate.id === 'string' &&
        typeof candidate.serverId === 'string' &&
        typeof candidate.relayUrl === 'string' &&
        typeof candidate.hostPublicKeyB64 === 'string' &&
        candidate.serverId.length > 0 &&
        candidate.relayUrl.length > 0 &&
        candidate.hostPublicKeyB64.length > 0
      );
    });
  } catch {
    return [];
  }
}

export function credentialsFor(
  host: PairedHost,
  secrets: HostSecrets,
  clientId: string | undefined,
): HostCredentials {
  const credentials: HostCredentials = {
    serverId: host.serverId,
    relayUrl: host.relayUrl,
    hostPublicKeyB64: host.hostPublicKeyB64,
  };
  if (clientId) credentials.clientId = clientId;
  if (secrets.deviceToken) credentials.deviceToken = secrets.deviceToken;
  else if (secrets.pairingKey) credentials.pairingKey = secrets.pairingKey;
  return credentials;
}

/** Shown on a host whose stored secret is gone and that has to be paired again. */
const REPAIR_DETAIL = '다시 페어링해야 합니다';

/**
 * One pairing-key authentication at a time per host: the Mac answers a pairing key with
 * a device token exactly once, so a probe and a screen's tunnel racing each other would
 * mint two tokens and keep only the second (docs/relay.md "기기 토큰").
 */
const authGate = createKeyedMutex();
/** Keeps two `auth_ok`s from writing a host's token over one another. */
const writeGate = createKeyedMutex();

/**
 * Everything a connection to `id` needs from the store: the gate, the token sink and
 * the answer to `device-conflict`.
 */
export function hostLeaseOptions(id: string): HostLeaseOptions {
  return {
    onDeviceToken: (token) => useHostsStore.getState().saveDeviceToken(id, token),
    authorize: (state) => authorizeFor(id, state),
    mintClientId: () => useHostsStore.getState().mintClientIdFor(id),
  };
}

/**
 * Waits for this host's pairing key to be free, then answers with the freshest secrets:
 * a connection that queued behind the one which adopted a token authenticates with that
 * token instead of spending the key again. A host with nothing stored yet — a pairing in
 * progress — keeps the credentials it was dialled with.
 */
async function authorizeFor(id: string, state: DeviceAuthState): Promise<AuthLease> {
  const release = await authGate.acquire(id);
  const store = useHostsStore.getState();
  const stored = secretsOf(store, id);
  if (!canAuthenticate(stored)) return { auth: state, release };
  const auth: DeviceAuthState = {};
  const clientId = store.clientIds[id] ?? store.clientId ?? state.clientId;
  if (clientId) auth.clientId = clientId;
  if (stored.deviceToken) auth.deviceToken = stored.deviceToken;
  else if (stored.pairingKey) auth.pairingKey = stored.pairingKey;
  return { auth, release };
}

function reachabilityFor(error: unknown): Reachability {
  if (error instanceof RelayError) {
    if (isFinalFailure(error.failure)) return 'unauthorized';
    if (error.failure === 'relay-unreachable') return 'relay-offline';
    return 'offline';
  }
  return 'offline';
}

/** The secrets held for one host right now. */
function secretsOf(state: HostsState, id: string): HostSecrets {
  const secrets: HostSecrets = {};
  const pairingKey = state.keys[id];
  const deviceToken = state.tokens[id];
  if (pairingKey) secrets.pairingKey = pairingKey;
  if (deviceToken) secrets.deviceToken = deviceToken;
  return secrets;
}

export const useHostsStore = create<HostsState>((set, get) => ({
  hosts: [],
  keys: {},
  tokens: {},
  clientIds: {},
  status: {},
  loaded: false,

  ensureClientId: async () => {
    const existing = get().clientId;
    if (existing) return existing;
    const clientId = await ensureClientId(SecureStore);
    set({ clientId });
    return clientId;
  },

  load: async () => {
    await SecureStore.deleteItemAsync(LEGACY_INDEX_KEY).catch(() => undefined);
    const clientId = await ensureClientId(SecureStore);
    const hosts = parseIndex(await SecureStore.getItemAsync(INDEX_KEY));
    const keys: Record<string, string> = {};
    const tokens: Record<string, string> = {};
    const clientIds: Record<string, string> = {};
    await Promise.all(
      hosts.map(async (host) => {
        const secrets = await readHostSecrets(SecureStore, host.id);
        if (secrets.pairingKey) keys[host.id] = secrets.pairingKey;
        if (secrets.deviceToken) tokens[host.id] = secrets.deviceToken;
        const override = await readHostClientId(SecureStore, host.id);
        if (override) clientIds[host.id] = override;
      }),
    );
    // A host whose secret is gone — released from the Mac, or its key replaced — stays
    // on the list as "재페어링 필요" rather than disappearing without a word.
    const status: Record<string, HostStatus> = {};
    for (const host of hosts) {
      if (!canAuthenticate({ pairingKey: keys[host.id], deviceToken: tokens[host.id] })) {
        status[host.id] = { reachability: 'unauthorized', detail: REPAIR_DETAIL };
      }
    }
    set({ hosts, keys, tokens, clientIds, status, clientId, loaded: true });
    void get().refreshAll();
  },

  addHost: async (payload, info) => {
    const id = storageIdFor(payload.serverId);
    const host: PairedHost = {
      id,
      name: payload.name,
      serverId: payload.serverId,
      relayUrl: payload.relayUrl,
      hostPublicKeyB64: payload.hostPublicKeyB64,
      hostId: info.hostId,
      appVersion: info.appVersion,
      pairedAt: new Date().toISOString(),
    };
    const hosts = [...get().hosts.filter((entry) => entry.id !== id), host];
    // A host that issued a token during pairing never keeps the key it was paired with.
    const secrets: HostSecrets = info.deviceToken
      ? { deviceToken: info.deviceToken }
      : { pairingKey: payload.pairingKey };
    await writeHostSecrets(SecureStore, id, secrets);
    await SecureStore.setItemAsync(INDEX_KEY, JSON.stringify(hosts));
    closeHostClient(id);
    set((prev) => {
      const keys = { ...prev.keys };
      const tokens = { ...prev.tokens };
      if (secrets.deviceToken) {
        tokens[id] = secrets.deviceToken;
        delete keys[id];
      } else if (secrets.pairingKey) {
        keys[id] = secrets.pairingKey;
        delete tokens[id];
      }
      return { hosts, keys, tokens, status: { ...prev.status, [id]: { reachability: 'online' } } };
    });
    return host;
  },

  // Serialised per host: two tunnels that authenticated at once would otherwise write
  // their tokens over one another, leaving the store holding the one the Mac forgot.
  saveDeviceToken: (id, deviceToken) =>
    writeGate.run(id, async () => {
      if (get().tokens[id] === deviceToken) return;
      await adoptDeviceToken(SecureStore, id, deviceToken);
      set((prev) => {
        const keys = { ...prev.keys };
        delete keys[id];
        return { keys, tokens: { ...prev.tokens, [id]: deviceToken } };
      });
    }),

  mintClientIdFor: (id) =>
    writeGate.run(id, async () => {
      const clientId = await mintHostClientId(SecureStore, id);
      set((prev) => ({ clientIds: { ...prev.clientIds, [id]: clientId } }));
      return clientId;
    }),

  forgetSecrets: async (id, detail) => {
    const state = get();
    if (!canAuthenticate(secretsOf(state, id))) return;
    closeHostClient(id);
    // The host's own `clientId` survives: it is not a secret, and pairing again under
    // the same id is what that Mac expects to see.
    await forgetHostSecrets(SecureStore, id);
    set((prev) => {
      const keys = { ...prev.keys };
      const tokens = { ...prev.tokens };
      delete keys[id];
      delete tokens[id];
      return {
        keys,
        tokens,
        status: {
          ...prev.status,
          [id]: { reachability: 'unauthorized', detail: detail ?? REPAIR_DETAIL },
        },
      };
    });
  },

  removeHost: async (id) => {
    const hosts = get().hosts.filter((entry) => entry.id !== id);
    closeHostClient(id);
    await forgetHost(SecureStore, id);
    await SecureStore.setItemAsync(INDEX_KEY, JSON.stringify(hosts));
    set((prev) => {
      const keys = { ...prev.keys };
      const tokens = { ...prev.tokens };
      const clientIds = { ...prev.clientIds };
      const status = { ...prev.status };
      delete keys[id];
      delete tokens[id];
      delete clientIds[id];
      delete status[id];
      return { hosts, keys, tokens, clientIds, status };
    });
  },

  refreshReachability: async (id) => {
    const state = get();
    const host = state.hosts.find((entry) => entry.id === id);
    const secrets = secretsOf(state, id);
    if (!host) return;
    if (!canAuthenticate(secrets)) {
      set((prev) => ({
        status: {
          ...prev.status,
          [id]: { reachability: 'unauthorized', detail: REPAIR_DETAIL },
        },
      }));
      return;
    }
    // A screen already holds an authenticated tunnel to this host: that is the answer,
    // and dialling a second socket would only be a second `auth` for the same key.
    const pooled = peekHostConnection(id);
    if (pooled?.state === 'ready' && pooled.info) {
      set((prev) => ({
        status: { ...prev.status, [id]: { reachability: 'online', detail: pooled.info?.hostName } },
      }));
      return;
    }
    set((prev) => ({ status: { ...prev.status, [id]: { reachability: 'checking' } } }));
    try {
      const info = await probeHost(
        credentialsFor(host, secrets, state.clientIds[id] ?? state.clientId),
        hostLeaseOptions(id),
      );
      // A probe can be the connection that registers this device.
      if (info.deviceToken) await get().saveDeviceToken(id, info.deviceToken);
      set((prev) => ({
        status: { ...prev.status, [id]: { reachability: 'online', detail: info.hostName } },
      }));
    } catch (error) {
      // The host has refused this secret for good: drop it so nothing presents it again,
      // and so the next launch knows this entry has to be paired anew.
      if (error instanceof RelayError && error.needsRepair) {
        await get().forgetSecrets(id, describeError(error));
        return;
      }
      set((prev) => ({
        status: {
          ...prev.status,
          [id]: { reachability: reachabilityFor(error), detail: describeError(error) },
        },
      }));
    }
  },

  refreshAll: async () => {
    await Promise.all(get().hosts.map((host) => get().refreshReachability(host.id)));
  },
}));

export function useHost(id: string | undefined): PairedHost | undefined {
  return useHostsStore((state) => state.hosts.find((entry) => entry.id === id));
}

/**
 * Drops the stored secret as soon as a screen's poll reports that the host refused it
 * (a rejected pairing key, or this device released from the Mac). Doing it here keeps
 * the rule in one place for every screen that polls.
 */
export function useForgetRefusedSecret(
  hostId: string | undefined,
  refused: boolean,
  detail: string | undefined,
): void {
  const forgetSecrets = useHostsStore((state) => state.forgetSecrets);
  useEffect(() => {
    if (refused && hostId) void forgetSecrets(hostId, detail);
  }, [detail, forgetSecrets, hostId, refused]);
}

export function hostAddress(host: PairedHost): string {
  return describeRelayTarget(host.relayUrl, host.serverId);
}
