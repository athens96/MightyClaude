import * as SecureStore from 'expo-secure-store';
import { create } from 'zustand';
import { ApiError, createClient, describeError } from '@/api/client';
import { buildBaseUrl, pairingFingerprint, type PairingPayload } from '@/lib/pairing';

const INDEX_KEY = 'mightyclaude.hosts.index';
const SECRET_PREFIX = 'mightyclaude.hostkey.';

export interface PairedHost {
  /** Local, storage-safe identifier derived from host+port. */
  id: string;
  name: string;
  host: string;
  port: number;
  /** Host-reported identity captured at pairing time. */
  hostId: string;
  appVersion: string;
  platform: string;
  pairedAt: string;
}

export type Reachability = 'unknown' | 'checking' | 'online' | 'unauthorized' | 'offline';

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
  addHost: (payload: PairingPayload, info: { hostId: string; appVersion: string; platform: string }) => Promise<PairedHost>;
  removeHost: (id: string) => Promise<void>;
  refreshReachability: (id: string) => Promise<void>;
  refreshAll: () => Promise<void>;
}

/** SecureStore keys accept only alphanumerics, `.`, `-` and `_`. */
export function storageIdFor(host: string, port: number): string {
  return pairingFingerprint(host, port).replace(/[^A-Za-z0-9._-]/g, '_');
}

function parseIndex(raw: string | null): PairedHost[] {
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((entry): entry is PairedHost => {
      if (entry === null || typeof entry !== 'object') return false;
      const candidate = entry as Partial<PairedHost>;
      return typeof candidate.id === 'string' && typeof candidate.host === 'string' && typeof candidate.port === 'number';
    });
  } catch {
    return [];
  }
}

export const useHostsStore = create<HostsState>((set, get) => ({
  hosts: [],
  keys: {},
  status: {},
  loaded: false,

  load: async () => {
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
    const id = storageIdFor(payload.host, payload.port);
    const host: PairedHost = {
      id,
      name: payload.name,
      host: payload.host,
      port: payload.port,
      hostId: info.hostId,
      appVersion: info.appVersion,
      platform: info.platform,
      pairedAt: new Date().toISOString(),
    };
    const hosts = [...get().hosts.filter((entry) => entry.id !== id), host];
    await SecureStore.setItemAsync(`${SECRET_PREFIX}${id}`, payload.key);
    await SecureStore.setItemAsync(INDEX_KEY, JSON.stringify(hosts));
    set((prev) => ({
      hosts,
      keys: { ...prev.keys, [id]: payload.key },
      status: { ...prev.status, [id]: { reachability: 'online' } },
    }));
    return host;
  },

  removeHost: async (id) => {
    const hosts = get().hosts.filter((entry) => entry.id !== id);
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
    const key = get().keys[id];
    if (!host || key === undefined) return;
    set((prev) => ({ status: { ...prev.status, [id]: { reachability: 'checking' } } }));
    try {
      const info = await createClient({ host: host.host, port: host.port, key }).info();
      set((prev) => ({
        status: { ...prev.status, [id]: { reachability: 'online', detail: info.hostName } },
      }));
    } catch (error) {
      const reachability: Reachability =
        error instanceof ApiError && error.needsRepair ? 'unauthorized' : 'offline';
      set((prev) => ({
        status: { ...prev.status, [id]: { reachability, detail: describeError(error) } },
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
  return buildBaseUrl(host.host, host.port).replace('http://', '');
}
