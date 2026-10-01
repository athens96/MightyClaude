import { useEffect, useState } from 'react';

/**
 * The phone's clock, re-read every `intervalMs` while `enabled`: for a running card's
 * elapsed time (every second) and for "n분 전" labels (every minute), which otherwise
 * only change when the host sends something new.
 */
export function useNow(intervalMs: number, enabled = true): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return undefined;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [enabled, intervalMs]);
  return now;
}
