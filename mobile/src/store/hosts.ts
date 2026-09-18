import * as SecureStore from 'expo-secure-store';
import { create } from 'zustand';
import { closeHostClient, describeError, probeHost, type HostCredentials } from '@/api/client';
import { RelayError } from '@/api/relay/transport';
import { describeRelayTarget, type PairingPayload } from '@/lib/pairing';

const INDEX_KEY = 'mightyclaude.hosts.v2.index';
const SECRET_PREFIX = 'mightyclaude.hostkey.v2.';
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
  keys: Record<string, string>;
  status: Record<string, HostStatus>;
  loaded: boolean;
  load: () => Promise<void>;
  addHost: (payload: PairingPayload, info: { hostId: string; appVersion: string }) => Promise<PairedHost>;
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

export function credentialsFor(host: PairedHost, pairingKey: string): HostCredentials {
  return {
    serverId: host.serverId,
    relayUrl: host.relayUrl,
    hostPublicKeyB64: host.hostPublicKeyB64,
    pairingKey,
  };
}

function reachabilityFor(error: unknown): Reachability {
  if (error instanceof RelayError) {
    switch (error.failure) {
      case 'unpaired':
        return 'unauthorized';
      case 'relay-unreachable':
        return 'relay-offline';
      default:
        return 'offline';
    }
  }
  return 'offline';
}

export const useHostsStore = create<HostsState>((set, get) => ({
  hosts: [],
  keys: {},
  status: {},
  loaded: false,

  load: async () => {
    await SecureStore.deleteItemAsync(LEGACY_INDEX_KEY).catch(() => undefined);
    const hosts = parseIndex(await SecureStore.getItemAsync(INDEX_KEY));
    const keys: Record<string, string> = {};
    await Promise.all(
      hosts.map(async (host) => {
        const secret = await SecureStore.getItemAsync(`${SECRET_PREFIX}${host.id}`);
        if (secret) keys[host.id] = secret;
      }),
    );
    const usable = hosts.filter((host) => keys[host.id] !== undefined);
    set({ hosts: usable, keys, loaded: true });
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
    await SecureStore.setItemAsync(`${SECRET_PREFIX}${id}`, payload.pairingKey);
    await SecureStore.setItemAsync(INDEX_KEY, JSON.stringify(hosts));
    closeHostClient(id);
    set((prev) => ({
      hosts,
      keys: { ...prev.keys, [id]: payload.pairingKey },
      status: { ...prev.status, [id]: { reachability: 'online' } },
    }));
    return host;
  },

  removeHost: async (id) => {
    const hosts = get().hosts.filter((entry) => entry.id !== id);
    closeHostClient(id);
    await SecureStore.deleteItemAsync(`${SECRET_PREFIX}${id}`);
    await SecureStore.setItemAsync(INDEX_KEY, JSON.stringify(hosts));
    set((prev) => {
      const keys = { ...prev.keys };
      const status = { ...prev.status };
      delete keys[id];
      delete status[id];
      return { hosts, keys, status };
    });
  },

  refreshReachability: async (id) => {
    const host = get().hosts.find((entry) => entry.id === id);
    const pairingKey = get().keys[id];
    if (!host || pairingKey === undefined) return;
    set((prev) => ({ status: { ...prev.status, [id]: { reachability: 'checking' } } }));
    try {
      const info = await probeHost(credentialsFor(host, pairingKey));
      set((prev) => ({
        status: { ...prev.status, [id]: { reachability: 'online', detail: info.hostName } },
      }));
    } catch (error) {
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

export function hostAddress(host: PairedHost): string {
  return describeRelayTarget(host.relayUrl, host.serverId);
}
