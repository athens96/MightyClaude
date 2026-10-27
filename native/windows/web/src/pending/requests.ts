// The requests the page draws before the host has them (natural submit B1 on the Windows page): on Enter the composer adds
// one with the sent words, every view draws it at once with 전송 중, and it gives way to the host's own block as the delta
// brings it. Pure; the store keeps them per pane (store.ts `pending`).
import type { LogEntry, MightyGraphRun, RunSession } from '../store/models';
import type { PendingState } from './display';

/** One sent request on its way. */
export interface PendingRequest {
  /** The page's id for it, sent with `send` and echoed in the answer. */
  clientId: string;
  /** The words the block shows. */
  text: string;
  /** When Enter was pressed (Date.now()). */
  at: number;
  /**
   * sending: no answer yet; started: the host started it and its blocks come with a delta; arrived: its entry and run are in the
   * pane, and the shown Mighty view waits for its own data (the host's copy, groups or boards) to replace the stand-in; failed: it
   * did not start.
   */
  phase: 'sending' | 'started' | 'arrived' | 'failed';
  /** The failure's message (phase failed). */
  message?: string;
  /** When the failure came. */
  endedAt?: number;
  /** When the host answered that it started. */
  answeredAt?: number;
  /** When its entry and run were in the pane (phase arrived), and that run's ids. */
  arrivedAt?: number;
  arrivedRunIds?: readonly string[];
  /** The host's user entry and graph run for it, when the answer named them. */
  entryId?: string;
  runId?: string;
  /** The pane as it was at Enter: its last user entry and its graph runs, so a block that came later is told apart. */
  lastUserEntryId?: string;
  knownRunIds: readonly string[];
}

/** The send bridge's outcome words (MainWindow.WebShell.Composer.cs, SubmitReceipt.SendAnswer). */
export type SendOutcome = 'started' | 'notStarted' | 'queued' | 'worktree' | 'answered' | 'builtin' | 'blocked' | 'none' | 'steered';

/** A new request for `session`, sent now with `text`. */
export function newPendingRequest(session: RunSession, clientId: string, text: string, at: number): PendingRequest {
  const lastUser = lastUserEntry(session.logs);
  return { clientId, text, at, phase: 'sending', lastUserEntryId: lastUser?.id, knownRunIds: (session.graphRuns ?? []).map((run) => run.id) };
}

function lastUserEntry(logs: readonly LogEntry[]): LogEntry | undefined {
  for (let index = logs.length - 1; index >= 0; index--) if (logs[index]!.kind === 'user') return logs[index];
  return undefined;
}

/** The user entries that came after the request's Enter: after the last one the pane had then, else the ones timed after it. */
export function userEntriesSince(request: PendingRequest, logs: readonly LogEntry[]): LogEntry[] {
  const known = request.lastUserEntryId === undefined ? -1 : logs.findIndex((entry) => entry.id === request.lastUserEntryId);
  const after = known >= 0 ? logs.slice(known + 1) : logs.filter((entry) => Date.parse(entry.timestamp) >= request.at - ClockSlack);
  return after.filter((entry) => entry.kind === 'user');
}

/** The host and the page share the PC's clock; an entry's time is rounded and written a moment before the page reads it. */
const ClockSlack = 2000;

const sameWords = (a: string, b: string) => a.trim() === b.trim();

/** A pane that draws the Mighty diagram (MightyGraphViewModel.ShowsModeSwitch): its request also comes as a graph run. */
export const drawsDiagram = (session: RunSession) => session.kind === 'claude' && ['claude', 'codex', 'gemini'].includes(session.provider);

/** The graph runs that came after the request's Enter. */
export function runsSince(request: PendingRequest, runs: readonly MightyGraphRun[]): MightyGraphRun[] {
  const known = new Set(request.knownRunIds);
  return runs.filter((run) => !known.has(run.id));
}

/** Whether the host's user entry for `request` is in the pane: the one the answer named, or a new one with its words. */
export function entryArrived(request: PendingRequest, session: RunSession): boolean {
  if (request.entryId !== undefined) return session.logs.some((entry) => entry.id === request.entryId);
  const fresh = userEntriesSince(request, session.logs);
  // A start the host answered without ids (it went out with held items): any new request entry is it.
  return fresh.some((entry) => sameWords(entry.text, request.text)) || (request.phase === 'started' && fresh.length > 0);
}

/**
 * The host's graph runs for `request` in the pane: the ones it arrived with, the one the answer named, or the new ones with its
 * words (any new one for a start answered without ids).
 */
export function runsFor(request: PendingRequest, session: RunSession): readonly string[] {
  if (request.arrivedRunIds) return request.arrivedRunIds;
  const runs = session.graphRuns ?? [];
  if (request.runId !== undefined) return runs.some((run) => run.id === request.runId) ? [request.runId] : [];
  const fresh = runsSince(request, runs);
  const same = fresh.filter((run) => sameWords(run.input, request.text));
  return (same.length > 0 ? same : request.phase === 'started' ? fresh : []).map((run) => run.id);
}

/** Whether the host's graph run for `request` is in the pane. */
export const runArrived = (request: PendingRequest, session: RunSession) => runsFor(request, session).length > 0;

/**
 * How long a started request waits for its blocks after the answer. The host adds them before it answers, so they come with the
 * next delta; one that never comes (a block the host trimmed or replaced) must not leave 전송 중 standing.
 */
export const StartedGrace = 15_000;

/**
 * How long an arrived request waits for the shown Mighty view's own data (the host pushes it at most every 120 ms or so while the
 * view shows; a view that stopped showing pushes nothing).
 */
export const ArrivedGrace = 2_000;

/** The next moment a request may go by time alone (StartedGrace, ArrivedGrace), so a quiet pane is settled then too; null: none. */
export function nextDeadline(requests: readonly PendingRequest[]): number | null {
  let next: number | null = null;
  for (const request of requests) {
    const at = request.phase === 'started' && request.answeredAt !== undefined ? request.answeredAt + StartedGrace + 1
      : request.phase === 'arrived' && request.arrivedAt !== undefined ? request.arrivedAt + ArrivedGrace + 1 : null;
    if (at !== null && (next === null || at < next)) next = at;
  }
  return next;
}

/**
 * The requests still waiting once the pane is `session` at `now`. A failed one stays (it shows its message in place) until the
 * next send. One goes once its entry is in the pane and, on a pane that draws the diagram, its run too (the ids the answer named,
 * else a new one with its words). On a diagram pane it first turns `arrived` and goes when `holds` says the shown Mighty view's
 * own data has its run (always, when no Mighty view shows). StartedGrace and ArrivedGrace end a wait whose blocks never come.
 */
export function reconcile(requests: readonly PendingRequest[], session: RunSession | undefined, now: number, holds: (request: PendingRequest) => boolean = () => true): readonly PendingRequest[] {
  if (!session) return [];
  let changed = false;
  const kept: PendingRequest[] = [];
  for (const request of requests) {
    let next: PendingRequest | null = request;
    if (request.phase === 'arrived') {
      if (now - (request.arrivedAt ?? now) > ArrivedGrace || holds(request)) next = null;
    } else if (request.phase === 'started' && request.answeredAt !== undefined && now - request.answeredAt > StartedGrace) {
      next = null;
    } else if (request.phase !== 'failed') {
      const diagram = drawsDiagram(session);
      const runs = diagram ? runsFor(request, session) : [];
      if (entryArrived(request, session) && (!diagram || runs.length > 0)) {
        const arrived: PendingRequest = { ...request, phase: 'arrived', arrivedAt: now, arrivedRunIds: runs };
        next = !diagram || holds(arrived) ? null : arrived;
      }
    }
    if (next !== request) changed = true;
    if (next) kept.push(next);
  }
  return changed ? kept : requests;
}

/** What the send's answer does to its request; `message` is the failure's words (the caller's, from the host's state). */
export function applyAnswer(requests: readonly PendingRequest[], clientId: string, outcome: SendOutcome | string | null, ids: { entryId?: string | null; runId?: string | null }, message: string, now: number): readonly PendingRequest[] {
  const at = requests.findIndex((request) => request.clientId === clientId);
  if (at < 0) return requests;
  const request = requests[at]!;
  let next: PendingRequest | null;
  switch (outcome) {
    case 'started':
      // Kept until the host's entry or run comes (a delta may have brought it already: the store reconciles at once).
      next = { ...request, phase: 'started', entryId: ids.entryId ?? undefined, runId: ids.runId ?? undefined, answeredAt: now };
      break;
    case 'notStarted':
    case 'blocked':
      next = { ...request, phase: 'failed', message, endedAt: now };
      break;
    // queued: no block until it runs, the queue row flashes; worktree: the child pane shows its own; answered, builtin: no
    // request block; steered: it went into the running run; none: nothing was sent. Anything else is dropped too.
    default:
      next = null;
  }
  const copy = requests.slice();
  if (next) copy[at] = next; else copy.splice(at, 1);
  return copy;
}

/** A failure the send threw (the bridge's rejection): the request turns failed with its words. */
export function failRequest(requests: readonly PendingRequest[], clientId: string, message: string, now: number): readonly PendingRequest[] {
  const at = requests.findIndex((request) => request.clientId === clientId);
  if (at < 0) return requests;
  const copy = requests.slice();
  copy[at] = { ...requests[at]!, phase: 'failed', message, endedAt: now };
  return copy;
}

/** The pending state a request's block shows (display.ts): waiting since Enter, or failed with its message. */
export function requestState(request: PendingRequest): PendingState {
  return request.phase === 'failed' ? { kind: 'failed', message: request.message ?? '', since: request.at, at: request.endedAt } : { kind: 'pending', since: request.at };
}

/** The requests the conversation draws: not yet there as the host's user entry. */
export function shownInConversation(requests: readonly PendingRequest[] | undefined, session: RunSession): readonly PendingRequest[] {
  if (!requests || requests.length === 0) return none;
  const shown = requests.filter((request) => request.phase === 'failed' || (request.phase !== 'arrived' && !entryArrived(request, session)));
  return shown.length === requests.length ? requests : shown;
}

/**
 * The request the Mighty views draw (the newest), whatever its phase: each view stands it in until its own data holds the run
 * (`runsFor`), the diagram on the run's blocks until their copy is pushed, the timeline and the pipeline until a group or a board
 * has it.
 */
export function shownInMighty(requests: readonly PendingRequest[] | undefined): PendingRequest | undefined {
  return requests?.[requests.length - 1];
}

const none: readonly PendingRequest[] = [];

/** The graph-run id a pending request's block takes in the Mighty views, apart from every host id. */
export const pendingRunId = (request: PendingRequest) => 'pending-' + request.clientId;

/**
 * A graph run standing in for the request in the diagram until the host's comes: its words, nothing run yet (`starting`, the
 * status the Mac gives a request before its first event). A failed one ends in error with the host's message as its result.
 */
export function pendingRun(request: PendingRequest, provider: string): MightyGraphRun {
  const failed = request.phase === 'failed';
  return { id: pendingRunId(request), input: request.text, status: failed ? 'error' : 'starting', rootEntries: [], agents: [], resultEntries: [], provider, ...(failed ? { finalOutput: request.message ?? '' } : {}) };
}

/** A fresh id for a request, a Wire.Identifier (letters, digits, `._:-`, at most 128). */
export function newClientId(): string {
  const random = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function' ? crypto.randomUUID() : Math.random().toString(36).slice(2) + Date.now().toString(36);
  return 'page-' + random;
}
