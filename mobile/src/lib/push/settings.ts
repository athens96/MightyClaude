import * as SecureStore from 'expo-secure-store';

import type { SecretStore } from '@/lib/host-secrets';
import { PUSH_KINDS, type PushKind } from '@/lib/push/message';

/** SecureStore key for the phone's notification settings. */
export const PUSH_SETTINGS_KEY = 'mightyclaude.push.settings.v1';

export interface PushSettings {
  /** Which kinds of push the paired hosts send this phone (`/m1/push/register` `kinds`). */
  kinds: Record<PushKind, boolean>;
  /** Crashlytics collection; on by default, never with user content. */
  crashReports: boolean;
}

export const DEFAULT_PUSH_SETTINGS: PushSettings = {
  kinds: { completed: true, failed: true, permission: true, question: true, plan: true, signin: true },
  crashReports: true,
};

/** The saved settings; anything missing or unreadable is on. */
export function parsePushSettings(text: string | null): PushSettings {
  if (text === null) return DEFAULT_PUSH_SETTINGS;
  try {
    const value = JSON.parse(text) as { kinds?: Partial<Record<string, unknown>>; crashReports?: unknown } | null;
    if (value === null || typeof value !== 'object') return DEFAULT_PUSH_SETTINGS;
    const kinds = { ...DEFAULT_PUSH_SETTINGS.kinds };
    for (const kind of PUSH_KINDS) {
      const saved = value.kinds?.[kind];
      if (typeof saved === 'boolean') kinds[kind] = saved;
    }
    return { kinds, crashReports: typeof value.crashReports === 'boolean' ? value.crashReports : true };
  } catch {
    return DEFAULT_PUSH_SETTINGS;
  }
}

/** The kinds that are on, in `PUSH_KINDS` order. */
export function enabledKinds(settings: PushSettings): PushKind[] {
  return PUSH_KINDS.filter((kind) => settings.kinds[kind]);
}

export function createPushSettings(store: SecretStore) {
  let current = DEFAULT_PUSH_SETTINGS;
  /** A change made before the saved file was read wins over that file. */
  let changed = false;
  const listeners = new Set<() => void>();
  const tell = (): void => {
    for (const listener of listeners) listener();
  };
  const ready = Promise.resolve()
    .then(() => store.getItemAsync(PUSH_SETTINGS_KEY))
    .then((text) => {
      if (changed) return;
      const loaded = parsePushSettings(text);
      if (JSON.stringify(loaded) === JSON.stringify(current)) return;
      current = loaded;
      tell();
    })
    .catch(() => undefined);
  return {
    get: (): PushSettings => current,
    ready,
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    async set(next: PushSettings): Promise<void> {
      changed = true;
      current = next;
      tell();
      await store.setItemAsync(PUSH_SETTINGS_KEY, JSON.stringify(next)).catch(() => undefined);
    },
  };
}

/** The phone's one settings store, kept in SecureStore. */
export const pushSettings = createPushSettings(SecureStore);
