// The conversation's windowing: which entries stand near the viewport, from heights measured once per entry and width
// (an entry not drawn yet is estimated). Pure, so the arithmetic is tested without a browser.
import type { LogEntry } from '../store/models';

/** How far past the viewport entries are still drawn, above and below. */
export const overscanPx = 800;
/** Within this of the bottom the view follows new content (spec §2). */
export const followSlackPx = 32;

/** Measured heights by entry id, one table per width; a width not used for a while is forgotten. */
export class HeightCache {
  private readonly widths = new Map<number, Map<string, number>>();

  get(id: string, width: number): number | undefined {
    return this.widths.get(width)?.get(id);
  }

  /** Records a height; gives back whether it differs from the one known. */
  set(id: string, width: number, height: number): boolean {
    let table = this.widths.get(width);
    if (!table) {
      table = new Map();
      this.widths.set(width, table);
      if (this.widths.size > 4) this.widths.delete(this.widths.keys().next().value as number);
    }
    if (table.get(id) === height) return false;
    table.set(id, height);
    return true;
  }
}

/** A first guess for an entry not measured yet: a line or two per 90 characters, a card's padding. */
export function estimateHeight(entry: LogEntry): number {
  if (entry.activity) return 34;
  if (entry.kind === 'image') return 140;
  const lines = entry.text.split('\n').reduce((count, line) => count + Math.max(1, Math.ceil(line.length / 90)), 0);
  return Math.min(4000, 28 + lines * 20 + (entry.kind === 'assistant' ? 30 : 0));
}

/** Each entry's top: offsets[i] is where entry i starts, offsets[n] the whole height. */
export function layout(entries: readonly LogEntry[], height: (entry: LogEntry) => number): Float64Array {
  const offsets = new Float64Array(entries.length + 1);
  for (let index = 0; index < entries.length; index++) offsets[index + 1] = offsets[index]! + height(entries[index]!);
  return offsets;
}

/** The entries [start, end) that meet the viewport grown by `overscan` on both sides. */
export function visibleRange(offsets: Float64Array, scrollTop: number, viewport: number, overscan = overscanPx): [number, number] {
  const count = offsets.length - 1;
  if (count <= 0) return [0, 0];
  const total = offsets[count]!;
  const top = Math.max(0, Math.min(scrollTop, Math.max(0, total - viewport)) - overscan);
  const bottom = Math.min(total, Math.max(scrollTop, 0) + viewport + overscan);
  // The first entry ending below `top`, then the first starting at or past `bottom`.
  let low = 0, high = count - 1;
  while (low < high) {
    const middle = (low + high) >> 1;
    if (offsets[middle + 1]! > top) high = middle;
    else low = middle + 1;
  }
  const start = low;
  low = start;
  high = count;
  while (low < high) {
    const middle = (low + high) >> 1;
    if (offsets[middle]! >= bottom) high = middle;
    else low = middle + 1;
  }
  return [start, Math.max(start + 1, low)];
}

/** Whether a scroller stands within the follow slack of its bottom. */
export function nearBottom(scrollHeight: number, scrollTop: number, clientHeight: number): boolean {
  return scrollHeight - scrollTop - clientHeight <= followSlackPx;
}
