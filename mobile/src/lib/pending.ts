// The natural-submit rules the phone shares with the Mac (MightyCore PendingDisplay.swift), Windows Core and the Windows page
// (native/windows/web/src/pending/display.ts), read from native/contracts/fixtures/pending.json by pending.test.ts: when a
// pending action's loading shows (A1), and what a host's submit answer says (B5). Pure; times are milliseconds on one clock.
import { motion } from '@/theme';

/** Where one user action is: nothing asked yet, waiting for its answer, answered, or failed with the message it shows in place. */
export type PendingState =
  | { kind: 'idle' }
  | { kind: 'pending'; since: number }
  | { kind: 'done'; since: number; at: number }
  | { kind: 'failed'; message: string; since: number; at?: number };

/** What the pending area shows now: nothing, the shared loading, or the failure's message. */
export type PendingIndicator = { kind: 'hidden' } | { kind: 'loading' } | { kind: 'failed'; message: string };

export const IDLE: PendingState = { kind: 'idle' };

/**
 * The display rule (pending.json `displayRule`). Loading shows once the action has lasted `showAfter`; an action over by then
 * never shows it; once shown it stays while pending and until `showAfter + minVisible` (inclusive), then hides at once. A failure
 * follows that rule until it ends, then shows at once and stays. `endedAt` null: still pending, or for a failure a failure now.
 */
export function pendingVisible(
  startedAt: number,
  endedAt: number | null,
  now: number,
  failed = false,
  showAfter: number = motion.pendingShowAfter,
  minVisible: number = motion.pendingMinVisible,
): boolean {
  const shownAt = startedAt + showAfter;
  if (failed) return now >= (endedAt ?? now) || now >= shownAt;
  if (now < shownAt) return false;
  if (endedAt === null) return true;
  if (endedAt <= shownAt) return false;
  return now < endedAt || now <= shownAt + minVisible;
}

/** What `state` shows at `now`: a failure shows loading until it ends (if the rule shows it by then) and its message from then on. */
export function pendingIndicator(
  state: PendingState,
  now: number,
  showAfter: number = motion.pendingShowAfter,
  minVisible: number = motion.pendingMinVisible,
): PendingIndicator {
  switch (state.kind) {
    case 'idle':
      return { kind: 'hidden' };
    case 'pending':
      return pendingVisible(state.since, null, now, false, showAfter, minVisible) ? { kind: 'loading' } : { kind: 'hidden' };
    case 'done':
      return pendingVisible(state.since, state.at, now, false, showAfter, minVisible) ? { kind: 'loading' } : { kind: 'hidden' };
    case 'failed': {
      const ended = state.at ?? now;
      if (now >= ended) return { kind: 'failed', message: state.message };
      return pendingVisible(state.since, ended, now, true, showAfter, minVisible) ? { kind: 'loading' } : { kind: 'hidden' };
    }
  }
}

/**
 * The next moment after `now` at which `state`'s indicator may change (Mac PendingSchedule): when loading may show, when the
 * action ended, and just after the shortest showing; null when nothing more changes. The hook waits for it, never frame by frame.
 */
export function nextPendingChange(
  state: PendingState,
  now: number,
  showAfter: number = motion.pendingShowAfter,
  minVisible: number = motion.pendingMinVisible,
): number | null {
  let moments: number[] = [];
  switch (state.kind) {
    case 'idle':
      return null;
    case 'pending':
      moments = [state.since + showAfter];
      break;
    // The hold is inclusive: drawn again just after it ends.
    case 'done':
      moments = [state.since + showAfter, state.at, state.since + showAfter + minVisible + 1];
      break;
    case 'failed':
      moments = [state.since + showAfter, ...(state.at !== undefined ? [state.at] : [])];
      break;
  }
  const later = moments.filter((moment) => moment > now);
  return later.length === 0 ? null : Math.min(...later);
}

/** What happened to a submit: started, queued or steered (pending.json `submitRule`). */
export type SubmitOutcome = 'started' | 'queued' | 'steered';

/** A host's submit answer as the phone reads it, old hosts and new. */
export interface ParsedSubmit {
  accepted: boolean;
  entryId: string | null;
  runId: string | null;
  outcome: SubmitOutcome | null;
}

const OUTCOMES: ReadonlySet<string> = new Set<SubmitOutcome>(['started', 'queued', 'steered']);
const outcomeWord = (value: unknown): SubmitOutcome | null =>
  typeof value === 'string' && OUTCOMES.has(value) ? (value as SubmitOutcome) : null;
const idOf = (value: unknown): string | null => (typeof value === 'string' && value.length > 0 ? value : null);

/**
 * `/m1/sessions/{id}/submit` and `guided`, old and new: the body's `outcome` when it is one of the three words, else its
 * `accepted` word (old hosts); accepted when there is an outcome or `accepted` is the boolean true. Ids count only as non-empty
 * strings; anything else in the body (protocol, draft, state) is ignored.
 */
export function parseSubmitResponse(raw: unknown): ParsedSubmit {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    return { accepted: false, entryId: null, runId: null, outcome: null };
  }
  const body = raw as Record<string, unknown>;
  const outcome = outcomeWord(body.outcome) ?? outcomeWord(body.accepted);
  return { accepted: outcome !== null || body.accepted === true, entryId: idOf(body.entryId), runId: idOf(body.runId), outcome };
}
