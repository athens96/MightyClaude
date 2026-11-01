import { useEffect, useMemo, useState } from 'react';
import { create } from 'zustand';
import { acquireHostClient, type HostCredentials, type MobileClient } from '@/api/client';
import type { Capability, MobileCommand, MobileSessionDetail, MobileState } from '@/api/types';
import { KEY_SEPARATOR } from '@/lib/keys';
import type { HostIdentity } from '@/lib/provider-host';
import { mergeSessionDetail, mergeState } from '@/lib/merge';
import { workspaceMute } from '@/lib/push/workspace-mute';
import { hostLeaseOptions, useHostsStore } from '@/store/hosts';

function detailKey(hostId: string, sessionId: string): string {
  return `${hostId}${KEY_SEPARATOR}${sessionId}`;
}

/** Shared empty array: the selector must return a stable reference when nothing is cached. */
const NO_COMMANDS: MobileCommand[] = [];

interface LiveStore {
  states: Record<string, MobileState>;
  details: Record<string, MobileSessionDetail>;
  /**
   * When each held detail arrived (phone clock, ms). A detail is only replaced by a newer
   * revision, so this is when the host measured its `elapsedSeconds`.
   */
  detailReceivedAt: Record<string, number>;
  /** `/m1/info` capabilities, cached per host for as long as the app runs. */
  capabilities: Record<string, Capability[]>;
  /** `/m1/sessions/{id}/commands`, cached per session and refreshed on focus. */
  commands: Record<string, MobileCommand[]>;
  /** `fresh`: the host's whole current state, taken whatever its revision. */
  applyState: (hostId: string, incoming: MobileState, fresh?: boolean) => void;
  applyDetail: (
    hostId: string,
    sessionId: string,
    incoming: MobileSessionDetail,
    fresh?: boolean,
  ) => void;
  /** `/m1/info`'s platform and app version per host, cached with its capabilities. */
  hostIdentity: Record<string, HostIdentity>;
  setCapabilities: (hostId: string, capabilities: Capability[], identity?: HostIdentity) => void;
  setCommands: (hostId: string, sessionId: string, commands: MobileCommand[]) => void;
  clearHost: (hostId: string) => void;
}

export const useLiveStore = create<LiveStore>((set) => ({
  states: {},
  details: {},
  detailReceivedAt: {},
  capabilities: {},
  hostIdentity: {},
  commands: {},

  applyState: (hostId, incoming, fresh) => {
    let applied = undefined as MobileState | undefined;
    set((prev) => {
      const merged = mergeState(prev.states[hostId], incoming, fresh);
      if (merged === prev.states[hostId]) return prev;
      applied = merged;
      return { ...prev, states: { ...prev.states, [hostId]: merged } };
    });
    // Notifications place a push in its workspace from the latest state (workspace-mute.ts);
    // a pairing removed while its poll was in flight is not written back.
    if (applied && useHostsStore.getState().hosts.some((host) => host.id === hostId)) {
      void workspaceMute.learn(hostId, applied);
    }
  },

  applyDetail: (hostId, sessionId, incoming, fresh) =>
    set((prev) => {
      const key = detailKey(hostId, sessionId);
      const merged = mergeSessionDetail(prev.details[key], incoming, fresh);
      if (merged === prev.details[key]) return prev;
      return {
        ...prev,
        details: { ...prev.details, [key]: merged },
        detailReceivedAt: { ...prev.detailReceivedAt, [key]: Date.now() },
      };
    }),

  setCapabilities: (hostId, capabilities, identity) =>
    set((prev) => ({
      ...prev,
      capabilities: { ...prev.capabilities, [hostId]: capabilities },
      hostIdentity: identity ? { ...prev.hostIdentity, [hostId]: identity } : prev.hostIdentity,
    })),

  setCommands: (hostId, sessionId, commands) =>
    set((prev) => ({
      ...prev,
      commands: { ...prev.commands, [detailKey(hostId, sessionId)]: commands },
    })),

  clearHost: (hostId) =>
    set((prev) => {
      const states = { ...prev.states };
      const capabilities = { ...prev.capabilities };
      const hostIdentity = { ...prev.hostIdentity };
      delete states[hostId];
      delete capabilities[hostId];
      delete hostIdentity[hostId];
      const belongsToHost = ([key]: [string, unknown]) =>
        !key.startsWith(`${hostId}${KEY_SEPARATOR}`);
      const details = Object.fromEntries(Object.entries(prev.details).filter(belongsToHost));
      const detailReceivedAt = Object.fromEntries(
        Object.entries(prev.detailReceivedAt).filter(belongsToHost),
      );
      const commands = Object.fromEntries(Object.entries(prev.commands).filter(belongsToHost));
      return { ...prev, states, details, detailReceivedAt, capabilities, hostIdentity, commands };
    }),
}));

export function useHostState(hostId: string | undefined): MobileState | undefined {
  return useLiveStore((store) => (hostId ? store.states[hostId] : undefined));
}

export function useSessionDetail(
  hostId: string | undefined,
  sessionId: string | undefined,
): MobileSessionDetail | undefined {
  return useLiveStore((store) =>
    hostId && sessionId ? store.details[detailKey(hostId, sessionId)] : undefined,
  );
}

/** When the held detail of a session arrived, or undefined when none is held. */
export function useDetailReceivedAt(
  hostId: string | undefined,
  sessionId: string | undefined,
): number | undefined {
  return useLiveStore((store) =>
    hostId && sessionId ? store.detailReceivedAt[detailKey(hostId, sessionId)] : undefined,
  );
}

export function useSessionCommands(
  hostId: string | undefined,
  sessionId: string | undefined,
): MobileCommand[] {
  return useLiveStore((store) =>
    hostId && sessionId
      ? (store.commands[detailKey(hostId, sessionId)] ?? NO_COMMANDS)
      : NO_COMMANDS,
  );
}

/**
 * Leases the host's shared relay connection for as long as the screen is mounted.
 * Every screen on the same host reuses one encrypted tunnel.
 */
export function useHostClient(hostId: string | undefined): MobileClient | undefined {
  const serverId = useHostsStore(
    (state) => state.hosts.find((entry) => entry.id === hostId)?.serverId,
  );
  const relayUrl = useHostsStore(
    (state) => state.hosts.find((entry) => entry.id === hostId)?.relayUrl,
  );
  const hostPublicKeyB64 = useHostsStore(
    (state) => state.hosts.find((entry) => entry.id === hostId)?.hostPublicKeyB64,
  );
  const pairingKey = useHostsStore((state) => (hostId ? state.keys[hostId] : undefined));
  const deviceToken = useHostsStore((state) => (hostId ? state.tokens[hostId] : undefined));
  // A host that once answered `device-conflict` has its own id; everyone else shares one.
  const clientId = useHostsStore((state) =>
    hostId ? (state.clientIds[hostId] ?? state.clientId) : state.clientId,
  );
  const [client, setClient] = useState<MobileClient | undefined>(undefined);

  const credentials = useMemo(() => {
    if (!serverId || !relayUrl || !hostPublicKeyB64) return undefined;
    if (deviceToken === undefined && pairingKey === undefined) return undefined;
    const value: HostCredentials = { serverId, relayUrl, hostPublicKeyB64 };
    if (clientId) value.clientId = clientId;
    if (deviceToken) value.deviceToken = deviceToken;
    else if (pairingKey) value.pairingKey = pairingKey;
    return value;
  }, [serverId, relayUrl, hostPublicKeyB64, pairingKey, deviceToken, clientId]);

  useEffect(() => {
    if (!hostId || !credentials) {
      setClient(undefined);
      return undefined;
    }
    // The host hands the token out once and only lets one connection ask for it, so the
    // gate, the token sink and the `device-conflict` answer all come from the store.
    const lease = acquireHostClient(hostId, credentials, hostLeaseOptions(hostId));
    setClient(lease.client);
    return () => {
      lease.release();
      setClient(undefined);
    };
  }, [hostId, credentials]);

  return client;
}
