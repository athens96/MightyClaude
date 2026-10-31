import { File, Paths } from 'expo-file-system';
import * as SecureStore from 'expo-secure-store';

import type { MobileState } from '@/api/types';
import type { SecretStore } from '@/lib/host-secrets';
import type { PushMessage } from '@/lib/push/message';

/**
 * Per-workspace notification switches, kept on this phone only (docs/mobile-remote.md "푸시 알림").
 * Every workspace is on until it is muted; the host is never told, so each phone paired with
 * the same host has its own choice and push registration is unaffected.
 *
 * A push names its workspace only by display name, so the phone keeps what it last saw of each
 * pairing — which workspace each session is in, and the workspaces' names — and resolves a
 * push from that: its session first, then (while what it saw is recent) a name only one
 * workspace has. A push it cannot place is shown.
 *
 * Storage: the muted ids are a small, rarely written list per pairing in SecureStore, next to
 * the other push settings. The session map grows with the host's panes and is rewritten as
 * they come and go, and SecureStore warns past 2 KB a value, so it is a JSON file per pairing
 * in the app's private document directory. Neither is a secret. Anything unreadable counts as
 * nothing muted and nothing known, so a push is shown rather than lost.
 */

/** SecureStore key prefix for a pairing's muted workspace ids. */
export const MUTED_PREFIX = 'mightyclaude.push.muted.v1.';
/** The host clips a push's workspace name to this many Unicode scalars (MobilePush.swift `clip`). */
export const WORKSPACE_NAME_LIMIT = 80;
/** At most this many sessions and workspaces are kept per pairing. */
export const ROUTE_SESSION_CAP = 1_000;
export const ROUTE_WORKSPACE_CAP = 200;
/**
 * Names older than this place no push: a workspace of the same name may have been added on the
 * host since, and its pushes must not fall under the muted one's name. Sessions still place.
 */
export const ROUTE_NAME_MAX_AGE_MS = 24 * 60 * 60 * 1000;
/** Unchanged routes are written again this often, so a phone that keeps polling keeps its names recent. */
export const ROUTE_REFRESH_MS = 60 * 60 * 1000;

/** What a pairing's last state said: session id → workspace id, and each workspace's clipped name. */
export interface PushRoutes {
  sessions: Record<string, string>;
  workspaces: { id: string; name: string }[];
  /** When the phone learned them (ms since the epoch); 0 when unknown. */
  learnedAt: number;
}

/** The name as the host writes it into a push. */
export function clipWorkspaceName(name: string): string {
  return [...name].slice(0, WORKSPACE_NAME_LIMIT).join('');
}

const byKey = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/**
 * A pairing's routes from its state. A host state is always the whole list (`mergeState` takes
 * it as it comes), so the routes are replaced, never merged: a closed session or a removed
 * workspace drops out with it. Sorted, so a reorder on the host writes nothing.
 */
export function routesFromState(state: MobileState, learnedAt: number): PushRoutes {
  const sessions = Object.fromEntries(
    state.sessions
      .slice(0, ROUTE_SESSION_CAP)
      .map((session) => [session.id, session.workspaceId] as const)
      .sort(([a], [b]) => byKey(a, b)),
  );
  const workspaces = state.workspaces
    .slice(0, ROUTE_WORKSPACE_CAP)
    .map((workspace) => ({ id: workspace.id, name: clipWorkspaceName(workspace.name) }))
    .sort((a, b) => byKey(a.id, b.id));
  return { sessions, workspaces, learnedAt };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Saved routes, or null for anything missing or damaged. A file without its time is old. */
export function parseRoutes(text: string | null): PushRoutes | null {
  if (text === null) return null;
  try {
    const value: unknown = JSON.parse(text);
    if (!isRecord(value) || !isRecord(value['sessions']) || !Array.isArray(value['workspaces'])) return null;
    const sessions = Object.fromEntries(
      Object.entries(value['sessions']).filter((entry): entry is [string, string] => typeof entry[1] === 'string'),
    );
    const workspaces = (value['workspaces'] as unknown[]).filter(
      (entry): entry is { id: string; name: string } =>
        isRecord(entry) && typeof entry['id'] === 'string' && typeof entry['name'] === 'string',
    );
    const learnedAt = typeof value['learnedAt'] === 'number' && Number.isFinite(value['learnedAt']) ? value['learnedAt'] : 0;
    return { sessions, workspaces: workspaces.map(({ id, name }) => ({ id, name })), learnedAt };
  } catch {
    return null;
  }
}

/** Saved muted ids; anything missing or damaged is none. */
export function parseMuted(text: string | null): string[] {
  if (text === null) return [];
  try {
    const value: unknown = JSON.parse(text);
    return Array.isArray(value) ? [...new Set(value.filter((id): id is string => typeof id === 'string'))] : [];
  } catch {
    return [];
  }
}

/**
 * The workspace a push is about: its session's workspace when the session is known, else — while
 * the routes are at most `ROUTE_NAME_MAX_AGE_MS` old — the one workspace whose name is the push's.
 * Null when neither tells (the push is then shown).
 */
export function resolvePushWorkspace(
  routes: PushRoutes | null,
  message: Pick<PushMessage, 'sessionId' | 'workspace'>,
  now: number,
): string | null {
  if (routes === null) return null;
  if (Object.prototype.hasOwnProperty.call(routes.sessions, message.sessionId)) {
    return routes.sessions[message.sessionId] ?? null;
  }
  if (message.workspace === '') return null;
  const age = now - routes.learnedAt;
  if (age < 0 || age > ROUTE_NAME_MAX_AGE_MS) return null;
  const named = routes.workspaces.filter((workspace) => workspace.name === message.workspace);
  return named.length === 1 ? (named[0]?.id ?? null) : null;
}

/** Where a pairing's routes are kept. */
export interface RouteFiles {
  read(storageId: string): Promise<string | null>;
  write(storageId: string, text: string): Promise<void>;
  remove(storageId: string): Promise<void>;
}

const NONE: readonly string[] = Object.freeze([]);

export function createWorkspaceMute({
  store,
  files,
  now = Date.now,
}: {
  store: SecretStore;
  files: RouteFiles;
  now?: () => number;
}) {
  const muted = new Map<string, readonly string[]>();
  /** Pairings whose muted list could not be read: read again before a change, never overwritten unread. */
  const unread = new Set<string>();
  /** One read of a pairing's muted list at a time; everyone asking meanwhile shares it. */
  const reads = new Map<string, Promise<readonly string[]>>();
  /** Bumped by every change, so a read begun before it never puts the old list back. */
  const generation = new Map<string, number>();
  const routes = new Map<string, PushRoutes>();
  /** The routes last written (or read) per pairing, without their time, and when; unchanged ones wait. */
  const saved = new Map<string, { content: string; at: number }>();
  /** Removed pairings: a state still in flight for one must not write its routes back. */
  const forgotten = new Set<string>();
  const queues = new Map<string, Promise<unknown>>();
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

  /** One pairing's writes run one after another. */
  function serially<T>(storageId: string, run: () => Promise<T>): Promise<T> {
    const done = (queues.get(storageId) ?? Promise.resolve()).then(run);
    queues.set(storageId, done.catch(() => undefined));
    return done;
  }

  function readMuted(storageId: string): Promise<readonly string[]> {
    const inFlight = reads.get(storageId);
    if (inFlight !== undefined) return inFlight;
    const startedAt = generation.get(storageId) ?? 0;
    const read = (async () => {
      try {
        const list = parseMuted(await store.getItemAsync(MUTED_PREFIX + storageId));
        // A change made while this read was out is newer than what it found.
        if ((generation.get(storageId) ?? 0) !== startedAt) return muted.get(storageId) ?? NONE;
        unread.delete(storageId);
        const before = muted.get(storageId);
        if (before === undefined || JSON.stringify(before) !== JSON.stringify(list)) {
          muted.set(storageId, list);
          tell();
        }
        return muted.get(storageId) ?? NONE;
      } catch {
        unread.add(storageId);
        return muted.get(storageId) ?? NONE;
      } finally {
        reads.delete(storageId);
      }
    })();
    reads.set(storageId, read);
    return read;
  }

  /** A pairing's muted ids, read once (and again while unreadable). */
  function load(storageId: string): Promise<readonly string[]> {
    const known = muted.get(storageId);
    return known !== undefined && !unread.has(storageId) ? Promise.resolve(known) : readMuted(storageId);
  }

  async function routesFor(storageId: string): Promise<PushRoutes | null> {
    const known = routes.get(storageId);
    if (known !== undefined) return known;
    try {
      const parsed = parseRoutes(await files.read(storageId));
      if (parsed !== null && !routes.has(storageId)) {
        routes.set(storageId, parsed);
        saved.set(storageId, { content: JSON.stringify({ ...parsed, learnedAt: 0 }), at: parsed.learnedAt });
      }
      return routes.get(storageId) ?? parsed;
    } catch {
      return null;
    }
  }

  return {
    load,
    /** The muted ids held now (empty until loaded); the same array until they change. */
    mutedNow: (storageId: string): readonly string[] => muted.get(storageId) ?? NONE,
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },

    /**
     * Mutes or unmutes one workspace of one pairing; nothing is sent to the host. False when the
     * choice could not be saved (the saved list is unreadable, or the write failed): it is then
     * not kept either, so the bell goes on showing what is saved.
     */
    setMuted(storageId: string, workspaceId: string, mute: boolean): Promise<boolean> {
      return serially(storageId, async () => {
        const current = await load(storageId);
        if (unread.has(storageId)) return false;
        if (current.includes(workspaceId) === mute) return true;
        const next = mute ? [...current, workspaceId] : current.filter((id) => id !== workspaceId);
        generation.set(storageId, (generation.get(storageId) ?? 0) + 1);
        muted.set(storageId, next);
        tell();
        const key = MUTED_PREFIX + storageId;
        try {
          await (next.length === 0 ? store.deleteItemAsync(key) : store.setItemAsync(key, JSON.stringify(next)));
          return true;
        } catch {
          if (muted.get(storageId) === next) {
            muted.set(storageId, current);
            tell();
          }
          return false;
        }
      });
    },

    /** Keeps what a pairing's latest state says about its sessions and workspaces. Never throws. */
    learn(storageId: string, state: MobileState): Promise<void> {
      if (forgotten.has(storageId)) return Promise.resolve();
      let text: string;
      let entry: { content: string; at: number };
      try {
        const at = now();
        const next = routesFromState(state, at);
        routes.set(storageId, next);
        entry = { content: JSON.stringify({ ...next, learnedAt: 0 }), at };
        text = JSON.stringify(next);
      } catch {
        return Promise.resolve();
      }
      const last = saved.get(storageId);
      if (last !== undefined && last.content === entry.content && entry.at - last.at < ROUTE_REFRESH_MS) {
        return Promise.resolve();
      }
      saved.set(storageId, entry);
      return serially(storageId, async () => {
        if (!forgotten.has(storageId)) await files.write(storageId, text);
      }).catch(() => {
        // Written again with the next state.
        if (saved.get(storageId) === entry) saved.delete(storageId);
      });
    },

    /** False when the push's workspace is muted on this pairing; true for anything unknown or unreadable. */
    async shouldShow(storageId: string, message: Pick<PushMessage, 'sessionId' | 'workspace'>): Promise<boolean> {
      try {
        const list = await load(storageId);
        if (list.length === 0) return true;
        const workspaceId = resolvePushWorkspace(await routesFor(storageId), message, now());
        return workspaceId === null || !list.includes(workspaceId);
      } catch {
        return true;
      }
    },

    /** Drops everything kept for a pairing that is removed; nothing is learned for it until it is paired again. */
    forget(storageId: string): Promise<void> {
      forgotten.add(storageId);
      return serially(storageId, async () => {
        muted.delete(storageId);
        unread.delete(storageId);
        routes.delete(storageId);
        saved.delete(storageId);
        generation.set(storageId, (generation.get(storageId) ?? 0) + 1);
        tell();
        await store.deleteItemAsync(MUTED_PREFIX + storageId).catch(() => undefined);
        await files.remove(storageId).catch(() => undefined);
      });
    },

    /** A pairing made (again) under this id learns from its states again. */
    resume(storageId: string): void {
      forgotten.delete(storageId);
    },
  };
}

function routeFile(storageId: string, suffix = ''): File {
  return new File(Paths.document, `push-routes.v1.${storageId}.json${suffix}`);
}

/** The routes files, one per pairing in the app's document directory. */
export const documentRouteFiles: RouteFiles = {
  read: async (storageId) => {
    const file = routeFile(storageId);
    return file.exists ? file.text() : null;
  },
  // Written beside it and moved over it, so a crash mid-write leaves the old file whole.
  write: async (storageId, text) => {
    const temp = routeFile(storageId, '.tmp');
    if (temp.exists) temp.delete();
    temp.create();
    temp.write(text);
    temp.moveSync(routeFile(storageId), { overwrite: true });
  },
  remove: async (storageId) => {
    for (const file of [routeFile(storageId), routeFile(storageId, '.tmp')]) {
      if (file.exists) file.delete();
    }
  },
};

/** The phone's one store of muted workspaces. */
export const workspaceMute = createWorkspaceMute({ store: SecureStore, files: documentRouteFiles });
