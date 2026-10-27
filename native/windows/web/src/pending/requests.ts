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
  /** sending: no answer yet; started: the host started it and its block comes with a delta; failed: it did not start. */
  phase: 'sending' | 'started' | 'failed';
  /** The failure's message (phase failed). */
  message?: string;
  /** When the failure came. */
  endedAt?: number;
  /** When the host answered that it started. */
  answeredAt?: number;
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

const sameWords = (a: string, b: string) => a.trim() === b.trim() || (b.trim().length > 0 && a.trim().startsWith(b.trim()));

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

/** Whether the host's graph run for `request` is in the pane: the one the answer named, or a new one with its words. */
export function runArrived(request: PendingRequest, session: RunSession): boolean {
  const runs = session.graphRuns ?? [];
  if (request.runId !== undefined) return runs.some((run) => run.id === request.runId);
  const fresh = runsSince(request, runs);
  return fresh.some((run) => sameWords(run.input, request.text)) || (request.phase === 'started' && fresh.length > 0);
}

/**
 * How long a started request waits for its blocks after the answer. The host adds them before it answers, so they come with the
 * next delta; one that never comes (a block the host trimmed or replaced) must not leave 전송 중 standing.
 */
export const StartedGrace = 15_000;

/**
 * The requests still waiting once the pane is `session` at `now`: a failed one stays (it shows its message in place) until the
 * next send; one whose blocks the host brought goes. When the answer named both ids, both must have come; a pane that draws no
 * diagram (a shell, a remote workspace) has no run id and waits for its entry alone.
 */
export function reconcile(requests: readonly PendingRequest[], session: RunSession | undefined, now: number): readonly PendingRequest[] {
  if (!session) return [];
  const kept = requests.filter((request) => {
    if (request.phase === 'failed') return true;
    if (request.phase === 'started' && request.answeredAt !== undefined && now - request.answeredAt > StartedGrace) return false;
    if (request.entryId !== undefined || request.runId !== undefined) {
      const entry = request.entryId === undefined || session.logs.some((e) => e.id === request.entryId);
      const run = request.runId === undefined || (session.graphRuns ?? []).some((r) => r.id === request.runId);
      return !(entry && run);
    }
    return !entryArrived(request, session) && !runArrived(request, session);
  });
  return kept.length === requests.length ? requests : kept;
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
  const shown = requests.filter((request) => request.phase === 'failed' || !entryArrived(request, session));
  return shown.length === requests.length ? requests : shown;
}

/** The request the Mighty views draw (the newest): not yet there as the host's graph run. */
export function shownInMighty(requests: readonly PendingRequest[] | undefined, session: RunSession): PendingRequest | undefined {
  const last = requests?.[requests.length - 1];
  if (!last) return undefined;
  return last.phase === 'failed' || !runArrived(last, session) ? last : undefined;
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
