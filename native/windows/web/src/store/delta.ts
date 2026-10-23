// A pane's change as the host sends it (WebBridge.cs, WebDelta.cs): its scalar fields only when they changed, and its two long
// lists (`logs`, `graphRuns`) as what happened to them by id, so a streamed line costs the line, not the conversation.
// `diffList` / `diffSession` are the host's rules in TypeScript: the dev host uses them, and the tests pin them.
import type { LogEntry, MightyGraphRun, RunSession } from './models';

/**
 * What happened to a list of records with ids. Without `order`: the first `drop` records went (retention trims the front),
 * the records in `replaced` took the place of the ones with their id, and `added` came after the rest. With `order` (any
 * other change: a removal inside, a reordering): the list is `order`'s ids, read from `replaced`, `added` and the old records.
 */
export interface ListDelta<T> {
  drop?: number;
  replaced?: T[];
  added?: T[];
  order?: string[];
}

/**
 * One pane in a delta. `session` is the whole record (a pane the page has not been sent yet); otherwise `set` holds the fields
 * whose value changed, `unset` the ones that are gone (the host omits nulls), and `logs` / `graphRuns` their list changes.
 */
export interface SessionDelta {
  id: string;
  session?: RunSession;
  set?: Partial<RunSession>;
  unset?: string[];
  logs?: ListDelta<LogEntry>;
  graphRuns?: ListDelta<MightyGraphRun>;
}

interface Identified {
  id: string;
}

/** Whether a list delta says nothing. */
export function emptyListDelta<T>(delta: ListDelta<T>): boolean {
  return !delta.drop && !delta.replaced?.length && !delta.added?.length && !delta.order;
}

/**
 * The change from `previous` to `next`. Records are compared with `same` (the host: by reference, then by value). The usual
 * changes (the last entry streamed, entries appended, the oldest trimmed) need no `order`, so their size is what changed.
 */
export function diffList<T extends Identified>(previous: readonly T[], next: readonly T[], same: (a: T, b: T) => boolean = Object.is): ListDelta<T> {
  const first = next[0];
  let drop = first === undefined ? previous.length : previous.findIndex((record) => record.id === first.id);
  if (drop < 0) drop = previous.length;
  const kept = previous.length - drop;
  let simple = next.length >= kept;
  for (let index = 0; simple && index < kept; index++) simple = next[index]!.id === previous[drop + index]!.id;
  if (simple && drop > 0 && next.length > kept) {
    // An added record must be new: one of the dropped ones coming back is a reordering.
    const dropped = new Set(previous.slice(0, drop).map((record) => record.id));
    simple = next.slice(kept).every((record) => !dropped.has(record.id));
  }
  const delta: ListDelta<T> = {};
  if (simple) {
    const replaced: T[] = [];
    for (let index = 0; index < kept; index++) if (!same(previous[drop + index]!, next[index]!)) replaced.push(next[index]!);
    if (drop > 0) delta.drop = drop;
    if (replaced.length > 0) delta.replaced = replaced;
    if (next.length > kept) delta.added = next.slice(kept);
    return delta;
  }
  const old = new Map(previous.map((record) => [record.id, record]));
  const replaced: T[] = [], added: T[] = [];
  for (const record of next) {
    const before = old.get(record.id);
    if (before === undefined) added.push(record);
    else if (!same(before, record)) replaced.push(record);
  }
  if (replaced.length > 0) delta.replaced = replaced;
  if (added.length > 0) delta.added = added;
  delta.order = next.map((record) => record.id);
  return delta;
}

/** The list after a delta; every record it did not name keeps its object, so a view memoized on one does not draw again. */
export function applyListDelta<T extends Identified>(previous: readonly T[], delta: ListDelta<T>): T[] {
  if (emptyListDelta(delta)) return previous as T[];
  if (delta.order) {
    const byId = new Map(previous.map((record) => [record.id, record]));
    for (const record of delta.replaced ?? []) byId.set(record.id, record);
    for (const record of delta.added ?? []) byId.set(record.id, record);
    return delta.order.map((id) => byId.get(id)).filter((record): record is T => record !== undefined);
  }
  const next = delta.drop ? previous.slice(delta.drop) : previous.slice();
  for (const record of delta.replaced ?? []) {
    // A streamed record is nearly always the last: look from the end.
    for (let index = next.length - 1; index >= 0; index--) {
      if (next[index]!.id === record.id) {
        next[index] = record;
        break;
      }
    }
  }
  if (delta.added) for (const record of delta.added) next.push(record);
  return next;
}

/** The two list fields; every other field is compared as its JSON. */
const listFields = new Set(['logs', 'graphRuns']);

/** The host's rule for one pane (WebDelta.cs), for the dev host and the tests: null when nothing changed. */
export function diffSession(previous: RunSession | undefined, next: RunSession, same?: (a: Identified, b: Identified) => boolean): SessionDelta | null {
  if (!previous) return { id: next.id, session: next };
  if (previous === next) return null;
  const delta: SessionDelta = { id: next.id };
  const set: Record<string, unknown> = {};
  const unset: string[] = [];
  const before = previous as unknown as Record<string, unknown>, after = next as unknown as Record<string, unknown>;
  for (const [key, value] of Object.entries(after)) {
    if (listFields.has(key) || value === undefined) continue;
    if (JSON.stringify(value) !== JSON.stringify(before[key])) set[key] = value;
  }
  for (const key of Object.keys(before)) if (!listFields.has(key) && before[key] !== undefined && after[key] === undefined) unset.push(key);
  if (Object.keys(set).length > 0) delta.set = set as Partial<RunSession>;
  if (unset.length > 0) delta.unset = unset;
  const logs = diffList(previous.logs, next.logs, same);
  if (!emptyListDelta(logs)) delta.logs = logs;
  const runs = diffList(previous.graphRuns ?? [], next.graphRuns ?? [], same);
  if (!emptyListDelta(runs)) delta.graphRuns = runs;
  return delta.set || delta.unset || delta.logs || delta.graphRuns ? delta : null;
}

/** A pane after its delta (`previous` undefined: the page did not have it, so only a whole record can make it). */
export function applySessionDelta(previous: RunSession | undefined, delta: SessionDelta): RunSession | undefined {
  if (delta.session) return delta.session;
  if (!previous) return undefined;
  let next: RunSession = delta.set ? { ...previous, ...delta.set } : { ...previous };
  if (delta.unset) {
    const record = next as unknown as Record<string, unknown>;
    for (const key of delta.unset) if (!listFields.has(key) && key !== 'id') delete record[key];
  }
  if (delta.logs) next = { ...next, logs: applyListDelta(previous.logs, delta.logs) };
  if (delta.graphRuns) {
    const runs = applyListDelta(previous.graphRuns ?? [], delta.graphRuns);
    next = { ...next, graphRuns: runs };
    if (runs.length === 0) delete next.graphRuns;
  }
  return next;
}
