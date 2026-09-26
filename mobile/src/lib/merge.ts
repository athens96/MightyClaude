import type {
  MobileSessionDetail,
  MobileSessionSummary,
  MobileState,
  MobileWorkspace,
} from '@/api/types';

/** Initial long-poll backoff delay, in milliseconds. */
export const MIN_BACKOFF_MS = 1000;
/** Maximum long-poll backoff delay, in milliseconds. */
export const MAX_BACKOFF_MS = 10_000;

/** Exponential backoff: 1s → 2s → 4s → 8s → 10s (capped). */
export function nextBackoff(current: number | undefined): number {
  if (current === undefined || current < MIN_BACKOFF_MS) return MIN_BACKOFF_MS;
  return Math.min(MAX_BACKOFF_MS, current * 2);
}

/**
 * Applies a long-poll result. The server may answer with the same revision after a
 * `wait` timeout, and out-of-order responses must never roll state backwards. A
 * `fresh` answer (see `isFreshAnswer`) is the host's whole current state and replaces
 * what we hold whatever its revision, so a host that started counting again is heard.
 */
export function mergeState(
  previous: MobileState | undefined,
  incoming: MobileState,
  fresh = false,
): MobileState {
  if (!previous || fresh) return incoming;
  if (incoming.revision < previous.revision) return previous;
  if (incoming.revision === previous.revision) return previous;
  return incoming;
}

/**
 * Applies a session long-poll result. Entries are replaced wholesale because a
 * streaming entry keeps its id while its text grows. `fresh` as for `mergeState`.
 */
export function mergeSessionDetail(
  previous: MobileSessionDetail | undefined,
  incoming: MobileSessionDetail,
  fresh = false,
): MobileSessionDetail {
  if (!previous || fresh) return incoming;
  if (incoming.revision < previous.revision) return previous;
  if (incoming.revision === previous.revision) return previous;
  return incoming;
}

export interface WorkspaceGroup {
  workspace: MobileWorkspace;
  sessions: MobileSessionSummary[];
}

const STATUS_ORDER: Record<MobileSessionSummary['status'], number> = {
  running: 0,
  error: 1,
  idle: 2,
  completed: 3,
  stopped: 4,
};

function compareSessions(a: MobileSessionSummary, b: MobileSessionSummary): number {
  if (a.pendingPermissions !== b.pendingPermissions) {
    return b.pendingPermissions - a.pendingPermissions;
  }
  const byStatus = STATUS_ORDER[a.status] - STATUS_ORDER[b.status];
  if (byStatus !== 0) return byStatus;
  return b.updatedAt.localeCompare(a.updatedAt);
}

/** Groups sessions under their workspace; sessions with no known workspace are dropped. */
export function groupSessionsByWorkspace(state: MobileState): WorkspaceGroup[] {
  const groups = new Map<string, WorkspaceGroup>();
  for (const workspace of state.workspaces) {
    groups.set(workspace.id, { workspace, sessions: [] });
  }
  for (const session of state.sessions) {
    groups.get(session.workspaceId)?.sessions.push(session);
  }
  for (const group of groups.values()) {
    group.sessions.sort(compareSessions);
  }
  return [...groups.values()];
}

/** Total pending permission + question count across all sessions. */
export function countAttention(state: MobileState): number {
  return state.sessions.reduce(
    (total, session) => total + session.pendingPermissions + session.pendingQuestions,
    0,
  );
}
