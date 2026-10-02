import {
  isAgentIOPane,
  type MobileSessionDetail,
  type MobileSessionSummary,
  type MobileState,
  type MobileUsage,
} from '@/api/types';
import { toneOf, type Tone } from '@/lib/status-tone';

/**
 * Everything the "현황" tab, the session cards and the "알림" tab count. All of it is read
 * from what the host already sends — the pane list in `MobileState` and, for a card, a
 * session detail the phone already holds — and nothing is estimated: a number the host
 * did not send is left out rather than made up.
 */

/** The three tiles: panes running now, requests waiting on the user, panes finished now. */
export interface DashboardStats {
  running: number;
  /**
   * Pending questions plus pending permission requests on panes the phone can open —
   * `countAttention` without an agent's own terminal and browser, the same count as the
   * "알림" tab's badge. It counts requests, not panes.
   */
  waiting: number;
  /** Panes whose status is `completed` right now — not a count of runs finished today. */
  done: number;
}

/** A pane the user opened, as opposed to the terminal or browser an agent opened for itself. */
function isUserPane(session: MobileSessionSummary): boolean {
  return !isAgentIOPane(session.kind);
}

export function attentionOf(session: MobileSessionSummary): number {
  return session.pendingQuestions + session.pendingPermissions;
}

export function countStats(state: MobileState | undefined): DashboardStats {
  const stats: DashboardStats = { running: 0, waiting: 0, done: 0 };
  if (!state) return stats;
  for (const session of state.sessions) {
    // An agent's own terminal or browser cannot be opened from the phone, so a request
    // there is not one the user can answer here — the "알림" badge skips it too.
    if (!isUserPane(session)) continue;
    stats.waiting += attentionOf(session);
    if (session.status === 'running') stats.running += 1;
    else if (session.status === 'completed') stats.done += 1;
  }
  return stats;
}

/**
 * The status a card draws: a pane holding a question or a permission request is waiting
 * on the user whatever its own status says, since that is what the user has to act on.
 */
export function displayStatus(session: MobileSessionSummary): string {
  return attentionOf(session) > 0 ? 'waiting' : session.status;
}

export function sessionTone(session: MobileSessionSummary): Tone {
  return toneOf(displayStatus(session));
}

// ------------------------------------------------------------------ the "알림" list

export type AttentionReason = 'question' | 'permission' | 'error' | 'finished';

const REASON_RANK: Record<AttentionReason, number> = {
  question: 0,
  permission: 1,
  error: 2,
  finished: 3,
};

export interface AttentionItem {
  /** `hostId` and session id together: the same pane id can exist on two hosts. */
  key: string;
  hostId: string;
  hostName: string;
  session: MobileSessionSummary;
  reason: AttentionReason;
  /** Pending questions plus permission requests while waiting; 1 for an error or a finished run. */
  count: number;
}

/** Why a pane is on the list, the most pressing reason first; undefined when it is not. */
export function attentionReason(session: MobileSessionSummary): AttentionReason | undefined {
  if (session.pendingQuestions > 0) return 'question';
  if (session.pendingPermissions > 0) return 'permission';
  if (session.status === 'error') return 'error';
  if (session.status === 'completed') return 'finished';
  return undefined;
}

/** Every request the pane holds when it is waiting on the user, else the one outcome. */
function countFor(session: MobileSessionSummary, reason: AttentionReason): number {
  return reason === 'question' || reason === 'permission' ? attentionOf(session) : 1;
}

/**
 * Every pane across the paired hosts that is waiting on the user or has an outcome to
 * look at, most pressing first and newest first within a reason. Only hosts still paired
 * are read, so a host removed a moment ago cannot leave rows behind; an agent's own
 * terminal or browser is left out, since the phone cannot open it.
 */
export function buildAttentionList(
  hosts: readonly { id: string; name: string }[],
  states: Readonly<Record<string, MobileState | undefined>>,
): AttentionItem[] {
  const items: AttentionItem[] = [];
  for (const host of hosts) {
    const state = states[host.id];
    if (!state) continue;
    const hostName = state.hostName || host.name;
    for (const session of state.sessions) {
      if (!isUserPane(session)) continue;
      const reason = attentionReason(session);
      if (!reason) continue;
      items.push({
        key: `${host.id}\u0000${session.id}`,
        hostId: host.id,
        hostName,
        session,
        reason,
        count: countFor(session, reason),
      });
    }
  }
  return items.sort(
    (a, b) =>
      REASON_RANK[a.reason] - REASON_RANK[b.reason] ||
      b.session.updatedAt.localeCompare(a.session.updatedAt),
  );
}

/** The tab badge: requests the user has to answer, not outcomes to look at. */
export function actionCount(items: readonly AttentionItem[]): number {
  return items.reduce(
    (total, item) =>
      item.reason === 'question' || item.reason === 'permission' ? total + item.count : total,
    0,
  );
}

// ------------------------------------------------------------------ a card's numbers

/** Context used, as the host reports it or worked out from its token counts. */
export function contextPercentOf(usage: MobileUsage | undefined): number | undefined {
  if (!usage) return undefined;
  const raw =
    usage.contextPercent ??
    (usage.contextUsedTokens !== undefined && usage.contextWindowTokens
      ? (usage.contextUsedTokens / usage.contextWindowTokens) * 100
      : undefined);
  if (raw === undefined || !Number.isFinite(raw)) return undefined;
  return Math.min(100, Math.max(0, raw));
}

export interface CardGlance {
  contextPercent?: number;
  elapsedSeconds?: number;
}

/**
 * The run time to show for a detail. The host measures `elapsedSeconds` when it answers,
 * and a quiet run (a long tool call) sends nothing new for minutes, so for a running pane
 * the host's reading is carried forward by the phone's clock from the moment that detail
 * arrived. Without that moment a running pane's reading is already old and is left out;
 * a pane that is not running has a stopped clock and its reading stands.
 */
export function liveElapsed(
  detail: Pick<MobileSessionDetail, 'elapsedSeconds' | 'session'>,
  receivedAt: number | undefined,
  now: number,
): number | undefined {
  const seconds = detail.elapsedSeconds;
  if (seconds === undefined || !Number.isFinite(seconds)) return undefined;
  if (detail.session.status !== 'running') return Math.max(0, seconds);
  if (receivedAt === undefined || !Number.isFinite(receivedAt)) return undefined;
  return Math.max(0, seconds + Math.max(0, now - receivedAt) / 1000);
}

/**
 * The context gauge and elapsed time a session card may show. The pane list carries
 * neither, so they come from a session detail the phone already holds — and only when
 * that detail is of the same revision as the list's entry. An older detail would show a
 * number that is no longer true, so the card shows none instead.
 */
export function freshGlance(
  summary: MobileSessionSummary,
  detail: MobileSessionDetail | undefined,
  receivedAt: number | undefined,
  now: number,
): CardGlance | undefined {
  if (!detail || detail.session.id !== summary.id || detail.session.revision !== summary.revision) {
    return undefined;
  }
  const glance: CardGlance = {};
  const percent = contextPercentOf(detail.usage);
  if (percent !== undefined) glance.contextPercent = percent;
  const elapsed = liveElapsed(detail, receivedAt, now);
  if (elapsed !== undefined) glance.elapsedSeconds = elapsed;
  return glance.contextPercent === undefined && glance.elapsedSeconds === undefined
    ? undefined
    : glance;
}

/** `2:14`, `1:02:09` — a stopwatch reading. */
export function formatClock(seconds: number): string {
  const total = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = String(total % 60).padStart(2, '0');
  if (hours > 0) return `${hours}:${String(minutes).padStart(2, '0')}:${rest}`;
  return `${minutes}:${rest}`;
}

export type AgeUnit = 'now' | 'minutes' | 'hours' | 'days';

/** How long ago `updatedAt` was, in the largest whole unit; undefined for a bad date. */
export function relativeAge(updatedAt: string, now: number): { unit: AgeUnit; count: number } | undefined {
  const at = Date.parse(updatedAt);
  if (!Number.isFinite(at)) return undefined;
  const minutes = Math.floor(Math.max(0, now - at) / 60_000);
  if (minutes < 1) return { unit: 'now', count: 0 };
  if (minutes < 60) return { unit: 'minutes', count: minutes };
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return { unit: 'hours', count: hours };
  return { unit: 'days', count: Math.floor(hours / 24) };
}

/** The host the "현황" tab shows: the one picked, or the first paired one. */
export function resolveSelectedHost(
  hosts: readonly { id: string }[],
  selectedId: string | undefined,
): string | undefined {
  if (selectedId && hosts.some((host) => host.id === selectedId)) return selectedId;
  return hosts[0]?.id;
}
