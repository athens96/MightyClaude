import { useCallback, useRef, useState } from 'react';
import { useFocusEffect } from 'expo-router';
import { ApiError, describeError, isAbortError } from '@/api/client';
import { nextBackoff } from '@/lib/merge';

export interface LongPollOptions<T> {
  /** Poll only while true; also gates on screen focus. */
  enabled: boolean;
  fetchPage: (since: number | undefined, signal: AbortSignal) => Promise<T>;
  revisionOf: (data: T) => number;
  onData: (data: T) => void;
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
 * in-flight request on blur and backing off exponentially on transport errors.
 */
export function useLongPoll<T>(options: LongPollOptions<T>): LongPollHandle {
  const { enabled, fetchPage, revisionOf, onData } = options;
  const [error, setError] = useState<string | undefined>(undefined);
  const [needsRepair, setNeedsRepair] = useState(false);
  const [loading, setLoading] = useState(false);
  const [generation, setGeneration] = useState(0);

  const latest = useRef({ fetchPage, revisionOf, onData });
  latest.current = { fetchPage, revisionOf, onData };

  const refresh = useCallback(() => {
    setGeneration((value) => value + 1);
  }, []);

  useFocusEffect(
    useCallback(() => {
      if (!enabled) {
        setLoading(false);
        return undefined;
      }

      let cancelled = false;
      let controller: AbortController | undefined;
      let timer: ReturnType<typeof setTimeout> | undefined;

      const sleep = (ms: number) =>
        new Promise<void>((resolve) => {
          timer = setTimeout(resolve, ms);
        });

      setLoading(true);
      setNeedsRepair(false);

      void (async () => {
        let since: number | undefined;
        let backoff: number | undefined;

        while (!cancelled) {
          controller = new AbortController();
          try {
            const data = await latest.current.fetchPage(since, controller.signal);
            if (cancelled) return;
            since = latest.current.revisionOf(data);
            latest.current.onData(data);
            backoff = undefined;
            setError(undefined);
            setLoading(false);
          } catch (caught) {
            if (cancelled || isAbortError(caught)) return;
            setLoading(false);
            setError(describeError(caught));
            if (caught instanceof ApiError && caught.needsRepair) {
              setNeedsRepair(true);
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
        if (timer) clearTimeout(timer);
        controller?.abort();
      };
      // Callbacks are read through `latest`, so identity changes do not restart the loop.
    }, [enabled, generation]),
  );

  return { error, loading, needsRepair, refresh };
}
