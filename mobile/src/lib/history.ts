import type { EntriesPage, LogEntry } from '@/api/types';

/**
 * History paging keeps two lists apart: the pages fetched with `entries?before=` and the
 * recent window the long poll replaces wholesale. They are combined only for display, so
 * an entry that arrives from the long poll while a page is loading neither duplicates nor
 * reorders what is already on screen.
 */

/**
 * Most entries the app keeps for one pane. The host itself remembers 400, so holding
 * more than that can only waste memory.
 */
export const MAX_RETAINED_ENTRIES = 400;

/** Drops repeats inside one page, keeping the first occurrence. */
function dedupe(entries: readonly LogEntry[]): LogEntry[] {
  const seen = new Set<string>();
  const result: LogEntry[] = [];
  for (const entry of entries) {
    if (seen.has(entry.id)) continue;
    seen.add(entry.id);
    result.push(entry);
  }
  return result;
}

/** The id to send as `before`, or undefined when there is nothing loaded yet. */
export function oldestEntryId(
  older: readonly LogEntry[],
  live: readonly LogEntry[],
): string | undefined {
  return older[0]?.id ?? live[0]?.id;
}

/**
 * Prepends one older page in front of the pages already loaded, skipping ids we hold
 * anywhere. Order within the page and within the existing list is preserved.
 */
export function prependOlderPage(
  older: readonly LogEntry[],
  live: readonly LogEntry[],
  page: readonly LogEntry[],
): LogEntry[] {
  const known = new Set<string>([...older, ...live].map((entry) => entry.id));
  const fresh = dedupe(page).filter((entry) => !known.has(entry.id));
  if (fresh.length === 0) return older as LogEntry[];
  return [...fresh, ...older];
}

/**
 * Catches the entries that just fell off the front of the live window. The host sends
 * only the newest 80 and the long poll replaces that window wholesale, so without this
 * the entries between the last loaded page and the new window would be lost from the
 * middle of the list and no `before=` page could bring them back.
 *
 * They are appended to the paged list, which keeps the whole list chronological: what
 * the window drops is newer than every page already loaded and older than the window.
 * The combined list is capped at `MAX_RETAINED_ENTRIES`, oldest first.
 */
export function retainDropped(
  older: readonly LogEntry[],
  previousLive: readonly LogEntry[],
  live: readonly LogEntry[],
): LogEntry[] {
  const liveIds = new Set(live.map((entry) => entry.id));
  const known = new Set(older.map((entry) => entry.id));
  const dropped: LogEntry[] = [];
  for (const entry of previousLive) {
    if (liveIds.has(entry.id) || known.has(entry.id)) continue;
    known.add(entry.id);
    dropped.push(entry);
  }
  const kept = dropped.length === 0 ? (older as LogEntry[]) : [...older, ...dropped];
  const excess = kept.length + live.length - MAX_RETAINED_ENTRIES;
  if (excess <= 0) return kept;
  return kept.slice(Math.min(excess, kept.length));
}

/**
 * What the list renders: loaded history first, then the live window. An id the long poll
 * has taken over is shown once, in the live window's position.
 */
export function combineEntries(
  older: readonly LogEntry[],
  live: readonly LogEntry[],
): LogEntry[] {
  if (older.length === 0) return live as LogEntry[];
  const liveIds = new Set(live.map((entry) => entry.id));
  return [...older.filter((entry) => !liveIds.has(entry.id)), ...live];
}

/**
 * True when no further page can arrive: either the host said so, or it answered with an
 * empty page because the `before` entry had already been evicted.
 */
export function isHistoryExhausted(page: Pick<EntriesPage, 'entries' | 'hasMore'>): boolean {
  return page.entries.length === 0 || !page.hasMore;
}
