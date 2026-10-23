import { acquireHostClient, type MobileClient } from '@/api/client';
import type { HostSecrets } from '@/lib/host-secrets';
import { credentialsFor, hostLeaseOptions, useHostsStore } from '@/store/hosts';

/** The pairings, read without probing every host when nothing has read them yet (the app may be closed). */
export async function loadedHosts() {
  if (!useHostsStore.getState().loaded) await useHostsStore.getState().load({ probe: false });
  return useHostsStore.getState();
}

/**
 * A connection to one paired host that works with the app closed (a notification button's
 * headless task). `use` gets a signal that aborts when `timeoutMs` runs out, so a request in
 * flight stops with the call.
 */
export async function withHostClient<T>(
  storageId: string,
  use: (client: MobileClient, signal: AbortSignal) => Promise<T>,
  timeoutMs = 15_000,
): Promise<T> {
  const state = await loadedHosts();
  const host = state.hosts.find((entry) => entry.id === storageId);
  if (!host) throw new Error('unknown host');
  const secrets: HostSecrets = {};
  const key = state.keys[storageId];
  const token = state.tokens[storageId];
  if (key) secrets.pairingKey = key;
  if (token) secrets.deviceToken = token;
  const lease = acquireHostClient(
    storageId,
    credentialsFor(host, secrets, state.clientIds[storageId] ?? state.clientId),
    hostLeaseOptions(storageId),
  );
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      use(lease.client, controller.signal),
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => {
          controller.abort();
          reject(new Error('timeout'));
        }, timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    lease.release();
  }
}
