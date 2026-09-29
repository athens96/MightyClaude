import type { HostStatus } from '@/store/hosts';

/** First re-probe of a host that just went unreachable, in milliseconds. */
export const RETRY_MIN_MS = 1_500;
/** The slowest the list asks again while a host stays unreachable. */
export const RETRY_MAX_MS = 30_000;

/** Hosts the list shows as unreachable: worth asking again, unlike a refused secret. */
export function unreachableHostIds(
  hosts: readonly { id: string }[],
  status: Readonly<Record<string, HostStatus | undefined>>,
): string[] {
  return hosts
    .map((host) => host.id)
    .filter((id) => {
      const reachability = status[id]?.reachability;
      return reachability === 'offline' || reachability === 'relay-offline';
    });
}

export interface OfflineRetryOptions {
  /** The hosts shown as unreachable right now. */
  unreachable: () => string[];
  /** Probes one host again, leaving its row as it is until the answer arrives. */
  refresh: (id: string) => Promise<void>;
  /** Calls `listener` whenever `unreachable` may answer differently; returns the unsubscribe. */
  subscribe: (listener: () => void) => () => void;
  minMs?: number;
  maxMs?: number;
}

/**
 * Keeps asking after the hosts the list shows as unreachable. The list's probe is
 * one-shot, so a Mac that dropped off for a moment — a network change, a sleep —
 * stayed "오프라인" until a pull or a trip to the background although it was back
 * seconds later. (A host's own screen already redials by itself: transport.ts.)
 *
 * Each host keeps its own backoff: probed `minMs` after it went down, then with the
 * delay doubling up to `maxMs`, and short again once it was back. No timer runs while
 * every host is reachable; `subscribe` wakes it when one goes down.
 * Returns the function that stops it.
 */
export function retryUnreachableHosts(options: OfflineRetryOptions): () => void {
  const minMs = options.minMs ?? RETRY_MIN_MS;
  const maxMs = options.maxMs ?? RETRY_MAX_MS;
  /** Per unreachable host: when it is probed next, and the wait after that probe. */
  const backoff = new Map<string, { due: number; delay: number }>();
  const probing = new Set<string>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let stopped = false;

  const schedule = () => {
    if (timer) clearTimeout(timer);
    timer = undefined;
    if (stopped) return;
    const down = new Set(options.unreachable());
    // A host that is back — or that someone else is checking — starts short next time.
    for (const id of backoff.keys()) if (!down.has(id)) backoff.delete(id);
    const now = Date.now();
    for (const id of down) if (!backoff.has(id)) backoff.set(id, { due: now + minMs, delay: minMs });
    let next = Infinity;
    for (const [id, entry] of backoff) if (!probing.has(id)) next = Math.min(next, entry.due);
    if (next !== Infinity) timer = setTimeout(probeDue, Math.max(0, next - now));
  };

  const probeDue = () => {
    timer = undefined;
    const now = Date.now();
    for (const [id, entry] of backoff) {
      if (probing.has(id) || entry.due > now) continue;
      probing.add(id);
      entry.delay = Math.min(maxMs, entry.delay * 2);
      void options
        .refresh(id)
        .catch(() => undefined)
        .finally(() => {
          probing.delete(id);
          const current = backoff.get(id);
          if (current) current.due = Date.now() + current.delay;
          schedule();
        });
    }
    schedule();
  };

  const unsubscribe = options.subscribe(schedule);
  schedule();
  return () => {
    stopped = true;
    unsubscribe();
    if (timer) clearTimeout(timer);
    timer = undefined;
  };
}
