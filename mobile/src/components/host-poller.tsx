import { useCallback, useEffect } from 'react';
import { peekHostConnection } from '@/api/client';
import type { RelayState } from '@/api/relay/transport';
import type { MobileState } from '@/api/types';
import { useLongPoll } from '@/hooks/use-long-poll';
import { t } from '@/lib/i18n';
import { registerHostRefresh, useDashboardStore } from '@/store/dashboard';
import { useForgetRefusedSecret, useHostsStore } from '@/store/hosts';
import { useHostClient, useLiveStore } from '@/store/live';

/**
 * Keeps one host's pane list in the live store while the tabs are on screen — the same
 * long poll the host screen runs, so "현황", "세션" and "알림" all read a current list.
 * It stops when a session or another stack screen covers the tabs (the poll follows
 * screen focus) and starts over with one full read when they come back. Draws nothing.
 */
export function HostPoller({ hostId }: { hostId: string }) {
  const client = useHostClient(hostId);
  const applyState = useLiveStore((store) => store.applyState);
  const setPoll = useDashboardStore((store) => store.setPoll);

  const fetchPage = useCallback(
    (since: number | undefined, signal: AbortSignal) => {
      if (!client) return Promise.reject(new Error(t('phone.workspaces.hostNotFound.title')));
      return client.state({ since, wait: since === undefined ? 0 : 10, signal });
    },
    [client],
  );

  const onData = useCallback(
    (data: MobileState, fresh: boolean) => {
      applyState(hostId, data, fresh);
      // A host that answers is reachable: let its live dot say so now rather than at the
      // next reachability retry. Only with this poll's tunnel ready and introduced, so the
      // check reads that tunnel instead of dialling a second socket.
      const { status, refreshReachability } = useHostsStore.getState();
      const pooled = peekHostConnection(hostId);
      if (status[hostId]?.reachability !== 'online' && pooled?.state === 'ready' && pooled.info) {
        void refreshReachability(hostId, { quiet: true });
      }
    },
    [applyState, hostId],
  );

  const subscribe = useCallback(
    (onChange: (revision: number) => void) => {
      if (!client) return () => undefined;
      return client.onNotify((event) => {
        if (event.scope === 'state') onChange(event.revision);
      });
    },
    [client],
  );

  const watchLink = useCallback(
    (listener: (state: RelayState) => void) => client?.onStateChange(listener) ?? (() => undefined),
    [client],
  );

  const poll = useLongPoll<MobileState>({
    enabled: Boolean(client),
    fetchPage,
    revisionOf: (data) => data.revision,
    onData,
    subscribe,
    watchLink,
  });

  // A refused secret is never presented again: it is dropped the moment the host says so.
  useForgetRefusedSecret(hostId, poll.needsRepair, poll.error);

  const { loading, error, needsRepair, refresh } = poll;
  useEffect(() => {
    const status: { loading: boolean; error?: string; needsRepair: boolean } = { loading, needsRepair };
    if (error !== undefined) status.error = error;
    setPoll(hostId, status);
  }, [error, hostId, loading, needsRepair, setPoll]);
  useEffect(() => () => setPoll(hostId, undefined), [hostId, setPoll]);
  useEffect(() => registerHostRefresh(hostId, refresh), [hostId, refresh]);

  return null;
}
