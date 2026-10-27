// The requests the phone draws before the host has them (natural submit B1 on the phone): on send the composer empties and the
// session screen adds one with the sent words, the shown view draws it at once with 전송 중, and it gives way to the host's own
// entry, run or board as the long poll brings them. The rules are the Windows page's (native/windows/web/src/pending/requests.ts),
// adapted to a polled detail: entries, the Mighty runs and the 파이프라인 structure come in one answer. Pure; the screen keeps them.
import type { LogEntry, MobileStructure } from '@/api/types';
import { pendingVisible, type ParsedSubmit, type PendingState } from '@/lib/pending';
import { motion } from '@/theme';

/** One sent request on its way. */
export interface PendingRequest {
  /** The phone's own id for it; the host never sees it. */
  clientId: string;
  /** The words the block shows (trimmed, as sent). */
  text: string;
  /** When send was pressed (Date.now()). */
  at: number;
  /**
   * sending: no answer yet; started: the host started it and its entry comes with a poll; queued: the host queued it, and it is
   * only watched (nothing is drawn) until its entry comes, matched by its words, so the views move to it when it runs (B2);
   * arrived: its entry and run are in the pane and the 파이프라인 waits for its board; failed: it did not start (the message shows
   * in place until the next send).
   */
  phase: 'sending' | 'started' | 'queued' | 'arrived' | 'failed';
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
  /** The host's user entry it arrived as (the transcript draws the stand-in in its place while it holds, `doneAt`). */
  arrivedEntryId?: string;
  /**
   * When the shown view's own data had it and the stand-in would have gone, had its 전송 중 not shown yet: once shown it stays
   * its shortest time (pending.json `displayRule`, pendingVisible(at, doneAt, now)), drawn in place of the host's own row, which
   * the view holds back meanwhile, so nothing shows twice.
   */
  doneAt?: number;
  /** A queued one: when its words were first missing from the pane's queue without its entry having come. */
  leftQueueAt?: number;
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
  /** The words of the pane's queued requests, when the host sent its queue. */
  queued?: readonly string[];
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

/**
 * How long a queued request is still watched once its words left the pane's queue (it ran, 다음 실행 took it, or it was removed)
 * and its entry has not come.
 */
export const QUEUE_LEFT_GRACE = 15_000;

/** The moment a holding request's stand-in may go: just after its shortest showing (the hold is inclusive). */
export function holdEnd(request: PendingRequest): number {
  return request.at + motion.pendingShowAfter + motion.pendingMinVisible + 1;
}

/** The next moment a request may go by time alone, so a quiet pane is settled then too; null: none. */
export function nextDeadline(requests: readonly PendingRequest[]): number | null {
  let next: number | null = null;
  for (const request of requests) {
    const at =
      request.phase === 'started' && request.answeredAt !== undefined
        ? request.answeredAt + STARTED_GRACE + 1
        : request.phase === 'arrived' && request.doneAt !== undefined
          ? holdEnd(request)
          : request.phase === 'arrived' && request.arrivedAt !== undefined
            ? request.arrivedAt + ARRIVED_GRACE + 1
            : request.phase === 'queued' && request.leftQueueAt !== undefined
              ? request.leftQueueAt + QUEUE_LEFT_GRACE + 1
              : null;
    if (at !== null && (next === null || at < next)) next = at;
  }
  return next;
}

/**
 * An arrived request whose shown view's data has it: it goes, unless its 전송 중 has shown and must stay its shortest time
 * (pendingVisible from the moment it would have gone, `doneAt`); then it stays, in place of the host's row, until `holdEnd`.
 * One drawn nowhere goes at once.
 */
function release(request: PendingRequest, now: number): PendingRequest | null {
  if (!request.drawn) return null;
  const doneAt = request.doneAt ?? now;
  if (!pendingVisible(request.at, doneAt, now)) return null;
  return request.doneAt === undefined ? { ...request, doneAt } : request;
}

/** The host's user entry `request` arrived as: the one the answer named, else the first new one with its words, else (a start answered without ids) the first new one. */
function arrivedEntry(request: PendingRequest, entries: readonly LogEntry[]): string | undefined {
  if (request.entryId !== undefined) return request.entryId;
  const fresh = userEntriesSince(request, entries);
  return (fresh.find((entry) => sameWords(entry.text, request.text)) ?? (request.phase === 'started' ? fresh[0] : undefined))?.id;
}

/**
 * The requests still waiting once the pane is `snapshot` at `now`, and whether one of them landed: its entry (and in the Mighty
 * view its run) came, so the views move to it again (B2: follow the stand-in at once and again when the real one lands). A
 * failed one stays until the next send. One that landed first turns `arrived` and goes when `holds` says the shown view's own
 * data has it (the 파이프라인 waits for its board), or once its 전송 중 has had its shortest showing (`release`). A queued one
 * lands when an entry with its words comes (it now runs: an own request), and is forgotten QUEUE_LEFT_GRACE after its words left
 * the queue without one. STARTED_GRACE and ARRIVED_GRACE end a wait whose data never comes; that is not a landing.
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
      if (request.doneAt !== undefined || now - (request.arrivedAt ?? now) > ARRIVED_GRACE || holds(request)) next = release(request, now);
    } else if (request.phase === 'started' && request.answeredAt !== undefined && now - request.answeredAt > STARTED_GRACE) {
      next = null;
    } else if (request.phase !== 'failed') {
      const runs = snapshot.runs ? runsFor(request, snapshot.runs) : [];
      if (entryArrived(request, snapshot.entries) && (!snapshot.runs || runs.length > 0)) {
        landed = true;
        const arrivedEntryId = arrivedEntry(request, snapshot.entries);
        const arrived: PendingRequest = {
          ...request,
          phase: 'arrived',
          arrivedAt: now,
          arrivedRunIds: runs,
          ...(arrivedEntryId !== undefined ? { arrivedEntryId } : {}),
        };
        next = holds(arrived) ? release(arrived, now) : arrived;
      } else if (request.phase === 'queued' && snapshot.queued) {
        const queued = snapshot.queued.some((text) => sameWords(text, request.text));
        if (queued) next = request.leftQueueAt === undefined ? request : { ...request, leftQueueAt: undefined };
        else if (request.leftQueueAt === undefined) next = { ...request, leftQueueAt: now };
        else if (now - request.leftQueueAt > QUEUE_LEFT_GRACE) next = null;
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
 * matched by words); queued: no block (the queue flashes), only watched by its words until it runs, which counts as own then
 * (pending.json `scrollRule`); steered or anything else: dropped.
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
  if (answer.outcome === 'queued') {
    next = {
      ...request,
      phase: 'queued',
      drawn: false,
      entryId: undefined,
      runId: undefined,
      answeredAt: now,
      arrivedAt: undefined,
      arrivedRunIds: undefined,
      arrivedEntryId: undefined,
      doneAt: undefined,
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

/**
 * The holding requests the transcript draws in place of the host's own entry (by that entry's id) until their shortest showing
 * is over (`doneAt`); the entry itself is not drawn meanwhile.
 */
export function heldInLog(requests: readonly PendingRequest[]): ReadonlyMap<string, PendingRequest> {
  const held = new Map<string, PendingRequest>();
  for (const request of requests) {
    if (request.phase === 'arrived' && request.doneAt !== undefined && request.arrivedEntryId !== undefined) held.set(request.arrivedEntryId, request);
  }
  return held;
}

/** The host's runs a view holds back while `shown` (its stand-in) holds in their place; none otherwise. */
export function heldRuns(shown: PendingRequest | undefined): readonly string[] {
  return shown?.phase === 'arrived' && shown.doneAt !== undefined ? (shown.arrivedRunIds ?? []) : [];
}

/**
 * The request the block list draws (the newest drawn one) while its run is not among the host's, or while it holds in its
 * run's place (`heldRuns`).
 */
export function shownInBlocks(requests: readonly PendingRequest[], runs: readonly { id: string; input: string }[]): PendingRequest | undefined {
  const request = newestDrawn(requests);
  if (!request) return undefined;
  return request.phase === 'failed' || request.doneAt !== undefined || runsFor(request, runs).length === 0 ? request : undefined;
}

/** `structure` without the requests of `runIds` (a holding stand-in's, `heldRuns`); the same object when there are none. */
export function structureWithout(structure: MobileStructure, runIds: readonly string[]): MobileStructure {
  if (runIds.length === 0) return structure;
  const held = new Set(runIds);
  return structure.shape === 'free'
    ? { ...structure, rows: structure.rows.filter((row) => !held.has(row.item.runId)) }
    : { ...structure, columns: structure.columns.map((column) => ({ ...column, items: column.items.filter((item) => !held.has(item.runId)) })) };
}

/**
 * The request the 파이프라인 draws (the newest drawn one) while no board holds its run: the run it arrived with, or for a
 * started one the run the answer named.
 */
export function shownInStructure(requests: readonly PendingRequest[], boardRuns: ReadonlySet<string> | undefined): PendingRequest | undefined {
  const request = newestDrawn(requests);
  if (!request) return undefined;
  if (request.phase === 'failed' || request.doneAt !== undefined || !boardRuns) return request;
  // The board may come before the entry does: a run the answer named, or the run it arrived
  // with, already on a board is shown there.
  const runs = request.phase === 'arrived' ? (request.arrivedRunIds ?? []) : request.runId !== undefined ? [request.runId] : [];
  return runs.some((id) => boardRuns.has(id)) ? undefined : request;
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
