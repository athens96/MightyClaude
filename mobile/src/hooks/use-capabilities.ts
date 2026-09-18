import { useEffect } from 'react';
import type { MobileClient } from '@/api/client';
import type { Capability } from '@/api/types';
import { parseCapabilities } from '@/lib/capabilities';
import { useLiveStore } from '@/store/live';

/** Stable reference for "nothing cached yet", so the hook never churns renders. */
const NONE: Capability[] = [];

/**
 * Asks `/m1/info` once per host and caches the answer. Until it arrives — and on a host
 * that never answers, or one older than the extension — the list stays empty and every
 * new feature stays hidden, so the app looks exactly like it did before.
 */
export function useCapabilities(
  hostId: string | undefined,
  client: MobileClient | undefined,
): Capability[] {
  const cached = useLiveStore((store) => (hostId ? store.capabilities[hostId] : undefined));
  const setCapabilities = useLiveStore((store) => store.setCapabilities);

  useEffect(() => {
    if (!client || !hostId || cached !== undefined) return undefined;
    const controller = new AbortController();
    void client
      .info(controller.signal)
      .then((info) => {
        if (!controller.signal.aborted) setCapabilities(hostId, parseCapabilities(info));
      })
      // A failed probe stays uncached so the next visit to the screen tries again.
      .catch(() => undefined);
    return () => controller.abort();
  }, [cached, client, hostId, setCapabilities]);

  return cached ?? NONE;
}
