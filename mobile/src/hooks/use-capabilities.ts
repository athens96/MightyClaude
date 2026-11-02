import { useEffect } from 'react';
import type { MobileClient } from '@/api/client';
import type { Capability } from '@/api/types';
import { parseCapabilities } from '@/lib/capabilities';
import { hostRunsAntigravity } from '@/lib/provider-host';
import { useLiveStore } from '@/store/live';

/** Stable reference for "nothing cached yet", so the hook never churns renders. */
const NONE: Capability[] = [];

/**
 * Asks `/m1/info` once per host and caches the answer, and asks again whenever the tunnel comes back after
 * being down: a host updated while the app kept running (a PC app restart drops the tunnel) then shows what it
 * now offers instead of the list it had when the app first met it. Until it arrives — and on a host
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
        if (!controller.signal.aborted) {
          setCapabilities(hostId, parseCapabilities(info), { platform: info.platform, appVersion: info.appVersion });
        }
      })
      // A failed probe stays uncached so the next visit to the screen tries again.
      .catch(() => undefined);
    return () => controller.abort();
  }, [cached, client, hostId, setCapabilities]);

  useEffect(() => {
    if (!client || !hostId) return undefined;
    let controller: AbortController | undefined;
    let down = false;
    const unsubscribe = client.onStateChange((state) => {
      if (state !== 'ready') {
        down = true;
        return;
      }
      if (!down) return;
      down = false;
      controller?.abort();
      const asking = new AbortController();
      controller = asking;
      void client
        .info(asking.signal)
        .then((info) => {
          if (asking.signal.aborted) return;
          const next = parseCapabilities(info);
          const known = useLiveStore.getState().capabilities[hostId];
          if (known && known.length === next.length && known.every((name, index) => name === next[index])) return;
          setCapabilities(hostId, next, { platform: info.platform, appVersion: info.appVersion });
        })
        .catch(() => undefined);
    });
    return () => {
      unsubscribe();
      controller?.abort();
    };
  }, [client, hostId, setCapabilities]);

  return cached ?? NONE;
}

/**
 * Whether the host runs Antigravity for its `gemini` panes (`hostRunsAntigravity`), from
 * the `/m1/info` answer `useCapabilities` caches; true until that answer arrives.
 */
export function useRunsAntigravity(hostId: string | undefined): boolean {
  return useLiveStore((store) => hostRunsAntigravity(hostId ? store.hostIdentity[hostId] : undefined));
}
