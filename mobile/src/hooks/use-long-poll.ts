import { useCallback, useRef, useState } from 'react';
import { useFocusEffect } from 'expo-router';
import { describeError, isAbortError, needsRepair } from '@/api/client';
import { appForeground } from '@/api/relay/foreground';
import type { RelayState } from '@/api/relay/transport';
import { nextBackoff } from '@/lib/merge';
import { isFreshAnswer, relayCameBack } from '@/lib/resync';

export interface LongPollOptions<T> {
  /** Poll only while true; also gates on screen focus. */
  enabled: boolean;
  fetchPage: (since: number | undefined, signal: AbortSignal) => Promise<T>;
  revisionOf: (data: T) => number;
  /** `fresh`: the host's whole current state rather than a step after ours (`isFreshAnswer`). */
  onData: (data: T, fresh: boolean) => void;
  /**
   * Optional push channel (the relay's `notify`). Calling back with a revision newer
   * than the one we are waiting on re-issues the request immediately instead of
   * waiting out the long poll.
   */
  subscribe?: (onChange: (revision: number) => void) => () => void;
  /** The tunnel's state (`MobileClient.onStateChange`); a reconnect starts the poll over. */
  watchLink?: (listener: (state: RelayState) => void) => () => void;
}

export interface LongPollHandle {
  error?: string;
  /** True until the first response of the current focus cycle arrives. */
  loading: boolean;
  needsRepair: boolean;
  refresh: () => void;
}

/**
 * Drives a `since`/`wait` long-poll loop while the screen is focused, aborting the
 * in-flight request on blur and backing off exponentially on transport errors. It
 * starts over — one immediate, full read — when the app returns to the foreground and
 * when a lost tunnel is back, since nothing is pushed for what changed in between.
 */
export function useLongPoll<T>(options: LongPollOptions<T>): LongPollHandle {
  const { enabled, fetchPage, revisionOf, onData, subscribe, watchLink } = options;
  const [error, setError] = useState<string | undefined>(undefined);
  const [repairNeeded, setRepairNeeded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [generation, setGeneration] = useState(0);

  const latest = useRef({ fetchPage, revisionOf, onData, subscribe });
  latest.current = { fetchPage, revisionOf, onData, subscribe };

  const refresh = useCallback(() => {
    setGeneration((value) => value + 1);
  }, []);

  useFocusEffect(
    useCallback(() => {
      if (!enabled) return undefined;
      const unsubscribeForeground = appForeground.subscribe(refresh);
      let link: RelayState | undefined;
      const unsubscribeLink = watchLink?.((state) => {
        const cameBack = relayCameBack(link, state);
        link = state;
        if (cameBack) refresh();
      });
      return () => {
        unsubscribeForeground();
        unsubscribeLink?.();
      };
    }, [enabled, refresh, watchLink]),
  );

  useFocusEffect(
    useCallback(() => {
      if (!enabled) {
        setLoading(false);
        return undefined;
      }

      let cancelled = false;
      let restarting = false;
      let controller: AbortController | undefined;
      let timer: ReturnType<typeof setTimeout> | undefined;
      const cursor: { since: number | undefined } = { since: undefined };

      const sleep = (ms: number) =>
        new Promise<void>((resolve) => {
          timer = setTimeout(resolve, ms);
        });

      setLoading(true);
      setRepairNeeded(false);

      // Abandon the waiting request and ask again; the host answers the old one at
      // its own pace and we drop that stale response by revision.
      const unsubscribe = latest.current.subscribe?.((revision) => {
        if (cancelled) return;
        if (cursor.since !== undefined && revision <= cursor.since) return;
        restarting = true;
        controller?.abort();
      });

      void (async () => {
        let backoff: number | undefined;

        while (!cancelled) {
          controller = new AbortController();
          try {
            const data = await latest.current.fetchPage(cursor.since, controller.signal);
            if (cancelled) return;
            const revision = latest.current.revisionOf(data);
            const fresh = isFreshAnswer(cursor.since, revision);
            cursor.since = revision;
            latest.current.onData(data, fresh);
            backoff = undefined;
            setError(undefined);
            setLoading(false);
          } catch (caught) {
            if (cancelled) return;
            if (isAbortError(caught)) {
              if (!restarting) return;
              restarting = false;
              continue;
            }
            setLoading(false);
            setError(describeError(caught));
            if (needsRepair(caught)) {
              setRepairNeeded(true);
              return;
            }
            backoff = nextBackoff(backoff);
            await sleep(backoff);
            if (cancelled) return;
          }
        }
      })();

      return () => {
        cancelled = true;
        unsubscribe?.();
        if (timer) clearTimeout(timer);
        controller?.abort();
      };
      // Callbacks are read through `latest`, so identity changes do not restart the loop.
    }, [enabled, generation]),
  );

  return { error, loading, needsRepair: repairNeeded, refresh };
}
