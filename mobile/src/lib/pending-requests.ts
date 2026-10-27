// The requests the phone draws before the host has them (natural submit B1 on the phone): on send the composer empties and the
// session screen adds one with the sent words, the shown view draws it at once with 전송 중, and it gives way to the host's own
// entry, run or board as the long poll brings them. The rules are the Windows page's (native/windows/web/src/pending/requests.ts),
// adapted to a polled detail: entries, the Mighty runs and the 파이프라인 structure come in one answer. Pure; the screen keeps them.
import type { LogEntry } from '@/api/types';
import type { ParsedSubmit } from '@/lib/pending';
import type { PendingState } from '@/lib/pending';

/** One sent request on its way. */
export interface PendingRequest {
  /** The phone's own id for it; the host never sees it. */
  clientId: string;
  /** The words the block shows (trimmed, as sent). */
  text: string;
  /** When send was pressed (Date.now()). */
  at: number;
  /**
   * sending: no answer yet; started: the host started it and its entry comes with a poll; arrived: its entry and run are in the
   * pane and the 파이프라인 waits for its board; failed: it did not start (the message shows in place until the next send).
   */
  phase: 'sending' | 'started' | 'arrived' | 'failed';
  /**
   * Drawn as a bubble or block. A request with no stand-in (a slash command, 다음 실행) is still watched, so the views move to
   * its entry when it lands, but nothing is drawn for it.
   */
  drawn: boolean;
  message?: string;
  endedAt?: number;
  answeredAt?: number;
  arrivedAt?: number;
  arrivedRunIds?: readonly string[];
  /** The host's user entry and graph run for it, when the answer named them (only a request that started at once). */
  entryId?: string;
  runId?: string;
  /** The pane as it was at send: its last user entry and its Mighty runs, so what came later is told apart. */
  lastUserEntryId?: string;
  knownRunIds: readonly string[];
}

/** What the phone holds of the pane: the transcript, and the Mighty runs when the pane is in the Mighty view. */
export interface PaneSnapshot {
  entries: readonly LogEntry[];
  /** Absent outside the Mighty view (no runs are sent), so a request then waits for its entry alone. */
  runs?: readonly { id: string; input: string }[];
}

let sequence = 0;

/** A fresh id for a request. */
export function newClientId(now: number = Date.now()): string {
  sequence += 1;
  return `phone-${now.toString(36)}-${sequence.toString(36)}`;
}

function lastUserEntry(entries: readonly LogEntry[]): LogEntry | undefined {
  for (let index = entries.length - 1; index >= 0; index--) if (entries[index]!.kind === 'user') return entries[index];
  return undefined;
}

/** A new request for the pane as `snapshot` holds it, sent now with `text`. */
export function newPendingRequest(snapshot: PaneSnapshot, clientId: string, text: string, at: number, drawn = true): PendingRequest {
  return {
    clientId,
    text,
    at,
    phase: 'sending',
    drawn,
    lastUserEntryId: lastUserEntry(snapshot.entries)?.id,
    knownRunIds: (snapshot.runs ?? []).map((run) => run.id),
  };
}

/**
 * The phone's clock and the host's differ: an entry the host timed is only trusted to be newer than a send when it is within
 * this much of it. Used only when the entry the pane ended on at send is no longer in the window.
 */
export const CLOCK_SLACK = 60_000;

/** The user entries that came after the request's send: after the last one the pane had then. */
export function userEntriesSince(request: PendingRequest, entries: readonly LogEntry[]): LogEntry[] {
  let after: readonly LogEntry[];
  if (request.lastUserEntryId === undefined) after = entries;
  else {
    const known = entries.findIndex((entry) => entry.id === request.lastUserEntryId);
    after = known >= 0
      ? entries.slice(known + 1)
      : entries.filter((entry) => Date.parse(entry.timestamp) >= request.at - CLOCK_SLACK);
  }
  return after.filter((entry) => entry.kind === 'user');
}

/** The same words: equal once trimmed, with runs of white space read as one (the host cleans a run's input that way). */
export function sameWords(a: string, b: string): boolean {
  const flat = (value: string) => value.trim().replace(/\s+/g, ' ');
  return flat(a) === flat(b);
}

/** Whether the host's user entry for `request` is in the pane: the one the answer named, or a new one with its words. */
export function entryArrived(request: PendingRequest, entries: readonly LogEntry[]): boolean {
  if (request.entryId !== undefined) return entries.some((entry) => entry.id === request.entryId);
  const fresh = userEntriesSince(request, entries);
  // A start the host answered without ids (an old host, or it went out with held items): any new request entry is it.
  return fresh.some((entry) => sameWords(entry.text, request.text)) || (request.phase === 'started' && fresh.length > 0);
}

/**
 * The host's Mighty runs for `request`: the ones it arrived with, the one the answer named, or the new ones with its words (any
 * new one for a start answered without ids).
 */
export function runsFor(request: PendingRequest, runs: readonly { id: string; input: string }[]): readonly string[] {
  if (request.arrivedRunIds) return request.arrivedRunIds;
  if (request.runId !== undefined) return runs.some((run) => run.id === request.runId) ? [request.runId] : [];
  const known = new Set(request.knownRunIds);
  const fresh = runs.filter((run) => !known.has(run.id));
  const same = fresh.filter((run) => sameWords(run.input, request.text));
  return (same.length > 0 ? same : request.phase === 'started' ? fresh : []).map((run) => run.id);
}

/**
 * How long a started request waits for its entry after the answer. The host adds it before it answers, so it comes with the
 * next poll; one that never comes (a trimmed or replaced entry) must not leave 전송 중 standing.
 */
export const STARTED_GRACE = 15_000;

/** How long an arrived request waits for the 파이프라인's board (it comes in the same answer when the view asks for it). */
export const ARRIVED_GRACE = 2_000;

/** The next moment a request may go by time alone, so a quiet pane is settled then too; null: none. */
export function nextDeadline(requests: readonly PendingRequest[]): number | null {
  let next: number | null = null;
  for (const request of requests) {
    const at =
      request.phase === 'started' && request.answeredAt !== undefined
        ? request.answeredAt + STARTED_GRACE + 1
        : request.phase === 'arrived' && request.arrivedAt !== undefined
          ? request.arrivedAt + ARRIVED_GRACE + 1
          : null;
    if (at !== null && (next === null || at < next)) next = at;
  }
  return next;
}

/**
 * The requests still waiting once the pane is `snapshot` at `now`, and whether one of them landed: its entry (and in the Mighty
 * view its run) came, so the views move to it again (B2: follow the stand-in at once and again when the real one lands). A
 * failed one stays until the next send. One that landed first turns `arrived` and goes when `holds` says the shown view's own
 * data has it (the 파이프라인 waits for its board). STARTED_GRACE and ARRIVED_GRACE end a wait whose data never comes; that
 * is not a landing.
 */
export function settle(
  requests: readonly PendingRequest[],
  snapshot: PaneSnapshot,
  now: number,
  holds: (request: PendingRequest) => boolean = () => true,
): { requests: readonly PendingRequest[]; landed: boolean } {
  let changed = false;
  let landed = false;
  const kept: PendingRequest[] = [];
  for (const request of requests) {
    let next: PendingRequest | null = request;
    if (request.phase === 'arrived') {
      if (now - (request.arrivedAt ?? now) > ARRIVED_GRACE || holds(request)) next = null;
    } else if (request.phase === 'started' && request.answeredAt !== undefined && now - request.answeredAt > STARTED_GRACE) {
      next = null;
    } else if (request.phase !== 'failed') {
      const runs = snapshot.runs ? runsFor(request, snapshot.runs) : [];
      if (entryArrived(request, snapshot.entries) && (!snapshot.runs || runs.length > 0)) {
        landed = true;
        const arrived: PendingRequest = { ...request, phase: 'arrived', arrivedAt: now, arrivedRunIds: runs };
        next = holds(arrived) ? null : arrived;
      }
    }
    if (next !== request) changed = true;
    if (next) kept.push(next);
  }
  return { requests: changed ? kept : requests, landed };
}

/** `settle`'s requests alone. */
export function reconcile(
  requests: readonly PendingRequest[],
  snapshot: PaneSnapshot,
  now: number,
  holds: (request: PendingRequest) => boolean = () => true,
): readonly PendingRequest[] {
  return settle(requests, snapshot, now, holds).requests;
}

/**
 * What the host's answer does to its request. started: kept until its entry comes (an answer naming a run wins over one
 * matched by words); queued, steered or anything else: dropped (a queued one gets no block until it runs, the queue flashes).
 */
export function applyAnswer(requests: readonly PendingRequest[], clientId: string, answer: ParsedSubmit, now: number): readonly PendingRequest[] {
  const at = requests.findIndex((request) => request.clientId === clientId);
  if (at < 0) return requests;
  const request = requests[at]!;
  let next: PendingRequest | null = null;
  if (answer.outcome === 'started') {
    next =
      request.phase === 'arrived' && !answer.runId
        ? { ...request, entryId: answer.entryId ?? request.entryId, answeredAt: now }
        : {
            ...request,
            phase: 'started',
            entryId: answer.entryId ?? undefined,
            runId: answer.runId ?? undefined,
            answeredAt: now,
            arrivedAt: undefined,
            arrivedRunIds: undefined,
          };
  }
  const copy = requests.slice();
  if (next) copy[at] = next;
  else copy.splice(at, 1);
  return copy;
}

/** A send that failed (the host refused it, or the upload or the relay failed): the request turns failed with the message. */
export function failRequest(requests: readonly PendingRequest[], clientId: string, message: string, now: number): readonly PendingRequest[] {
  const at = requests.findIndex((request) => request.clientId === clientId);
  if (at < 0) return requests;
  const copy = requests.slice();
  copy[at] = { ...requests[at]!, phase: 'failed', message, endedAt: now };
  return copy;
}

/** Drops one request (a cancelled upload). */
export function dropRequest(requests: readonly PendingRequest[], clientId: string): readonly PendingRequest[] {
  const kept = requests.filter((request) => request.clientId !== clientId);
  return kept.length === requests.length ? requests : kept;
}

/** The requests before a new send: a failed one gives way to it. */
export function beforeSend(requests: readonly PendingRequest[]): readonly PendingRequest[] {
  const kept = requests.filter((request) => request.phase !== 'failed');
  return kept.length === requests.length ? requests : kept;
}

/** The pending state a request's block shows (lib/pending.ts): waiting since send, or failed with its message. */
export function requestState(request: PendingRequest): PendingState {
  return request.phase === 'failed'
    ? { kind: 'failed', message: request.message ?? '', since: request.at, ...(request.endedAt !== undefined ? { at: request.endedAt } : {}) }
    : { kind: 'pending', since: request.at };
}

/** The requests the transcript draws: drawn, and not yet there as the host's user entry. */
export function shownInLog(requests: readonly PendingRequest[], entries: readonly LogEntry[]): readonly PendingRequest[] {
  return requests.filter(
    (request) => request.drawn && (request.phase === 'failed' || (request.phase !== 'arrived' && !entryArrived(request, entries))),
  );
}

/** The request the block list draws (the newest drawn one) while its run is not among the host's. */
export function shownInBlocks(requests: readonly PendingRequest[], runs: readonly { id: string; input: string }[]): PendingRequest | undefined {
  const request = newestDrawn(requests);
  if (!request) return undefined;
  return request.phase === 'failed' || runsFor(request, runs).length === 0 ? request : undefined;
}

/** The request the 파이프라인 draws (the newest drawn one) while no board holds its run. */
export function shownInStructure(requests: readonly PendingRequest[], boardRuns: ReadonlySet<string> | undefined): PendingRequest | undefined {
  const request = newestDrawn(requests);
  if (!request) return undefined;
  if (request.phase === 'failed') return request;
  if (request.phase !== 'arrived') return request;
  return boardRuns && (request.arrivedRunIds ?? []).some((id) => boardRuns.has(id)) ? undefined : request;
}

function newestDrawn(requests: readonly PendingRequest[]): PendingRequest | undefined {
  for (let index = requests.length - 1; index >= 0; index--) if (requests[index]!.drawn) return requests[index];
  return undefined;
}

/**
 * The draft a failed send gives back: the sent words, only when the composer is still empty and nobody typed in it since (the
 * Mac's rule); otherwise nothing, and what is there stays.
 */
export function draftToRestore(sent: string, composer: string, editedSinceSend: boolean): string | undefined {
  return composer.length === 0 && !editedSinceSend && sent.length > 0 ? sent : undefined;
}

/** Whether a send gets a stand-in: not on a busy pane (it queues), not a slash command (the host may answer it itself). */
export function drawsStandIn(text: string, options: { running: boolean; mode?: string }): boolean {
  return !options.running && options.mode !== 'queue' && !text.trimStart().startsWith('/');
}
