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
  /** The saved file could not be read: it is read again before a change, and never overwritten unread. */
  let readFailed = false;
  const listeners = new Set<() => void>();
  const tell = (): void => {
    for (const listener of listeners) {
      try {
        listener();
      } catch {
        // One broken listener must not keep the others from hearing.
      }
    }
  };
  const read = async (): Promise<PushSettings | null> => {
    try {
      const text = await store.getItemAsync(PUSH_SETTINGS_KEY);
      readFailed = false;
      return parsePushSettings(text);
    } catch {
      readFailed = true;
      return null;
    }
  };
  const ready = read().then((loaded) => {
    if (loaded === null || JSON.stringify(loaded) === JSON.stringify(current)) return;
    current = loaded;
    tell();
  });
  /** Changes run one after another, each on the value the one before it left. */
  let queue: Promise<void> = ready;

  function update(change: (now: PushSettings) => PushSettings): Promise<void> {
    const run = async (): Promise<void> => {
      if (readFailed) {
        const loaded = await read();
        if (loaded !== null) current = loaded;
      }
      current = change(current);
      const saved = readFailed
        ? Promise.resolve()
        : store.setItemAsync(PUSH_SETTINGS_KEY, JSON.stringify(current)).catch(() => undefined);
      tell();
      await saved;
    };
    const done = queue.then(run);
    queue = done.catch(() => undefined);
    return done;
  }

  return {
    get: (): PushSettings => current,
    ready,
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    /** Applies `change` to the latest settings once the changes before it are done. */
    update,
    set: (next: PushSettings): Promise<void> => update(() => next),
  };
}

/** The phone's one settings store, kept in SecureStore. */
export const pushSettings = createPushSettings(SecureStore);
