import type { LogEntry } from '@/api/types';
import {
  MAX_RETAINED_ENTRIES,
  combineEntries,
  isHistoryExhausted,
  oldestEntryId,
  prependOlderPage,
  retainDropped,
} from '@/lib/history';

function entry(id: string, text = id): LogEntry {
  return { id, kind: 'assistant', text, timestamp: '2026-01-01T00:00:00.000Z' };
}

/** `window(1, 4)` → the entries e1, e2, e3, e4. */
function window(from: number, to: number): LogEntry[] {
  return Array.from({ length: to - from + 1 }, (_, index) => entry(`e${from + index}`));
}

function ids(entries: readonly LogEntry[]): string[] {
  return entries.map((item) => item.id);
}

describe('oldestEntryId', () => {
  it('prefers the oldest loaded page, then the live window', () => {
    expect(oldestEntryId([entry('a'), entry('b')], [entry('c')])).toBe('a');
    expect(oldestEntryId([], [entry('c'), entry('d')])).toBe('c');
    expect(oldestEntryId([], [])).toBeUndefined();
  });
});

describe('prependOlderPage', () => {
  it('puts a page in front of the pages already loaded, keeping order', () => {
    const older = [entry('c')];
    const merged = prependOlderPage(older, [entry('d')], [entry('a'), entry('b')]);
    expect(merged.map((item) => item.id)).toEqual(['a', 'b', 'c']);
  });

  it('drops ids already held, wherever they are held', () => {
    const older = [entry('c')];
    const live = [entry('d')];
    const merged = prependOlderPage(older, live, [entry('b'), entry('c'), entry('d')]);
    expect(merged.map((item) => item.id)).toEqual(['b', 'c']);
  });

  it('drops repeats inside one page without reordering it', () => {
    const merged = prependOlderPage([], [], [entry('a'), entry('b'), entry('a')]);
    expect(merged.map((item) => item.id)).toEqual(['a', 'b']);
  });

  it('keeps the same array when the page adds nothing', () => {
    const older = [entry('c')];
    expect(prependOlderPage(older, [entry('d')], [entry('c')])).toBe(older);
    expect(prependOlderPage(older, [], [])).toBe(older);
  });

  it('never duplicates an entry the long poll took over while the page loaded', () => {
    // 'b' was in the page we asked for and also arrived in the live window meanwhile.
    const merged = prependOlderPage([], [entry('b'), entry('c')], [entry('a'), entry('b')]);
    expect(merged.map((item) => item.id)).toEqual(['a']);
    expect(combineEntries(merged, [entry('b'), entry('c')]).map((item) => item.id)).toEqual([
      'a',
      'b',
      'c',
    ]);
  });
});

describe('retainDropped', () => {
  it('keeps what slid off the front of the live window when nothing was paged in', () => {
    // The host sends the newest entries only; e1..e3 leave the window for good.
    const older = retainDropped([], window(1, 8), window(4, 11));
    expect(ids(older)).toEqual(['e1', 'e2', 'e3']);
    expect(ids(combineEntries(older, window(4, 11)))).toEqual(ids(window(1, 11)));
  });

  it('appends behind the pages already loaded, so the list stays chronological', () => {
    const paged = window(1, 4);
    const older = retainDropped(paged, window(5, 9), window(7, 12));
    expect(ids(older)).toEqual(['e1', 'e2', 'e3', 'e4', 'e5', 'e6']);
    expect(ids(combineEntries(older, window(7, 12)))).toEqual(ids(window(1, 12)));
  });

  it('keeps retaining across several moves of the window', () => {
    let older = retainDropped([], window(1, 5), window(3, 7));
    older = retainDropped(older, window(3, 7), window(6, 10));
    expect(ids(older)).toEqual(['e1', 'e2', 'e3', 'e4', 'e5']);
  });

  it('never repeats an entry the live window still shows or a page already holds', () => {
    expect(retainDropped([], window(1, 5), window(1, 5))).toEqual([]);
    const paged = window(1, 3);
    expect(retainDropped(paged, window(1, 5), window(4, 8))).toBe(paged);
  });

  it('keeps the same array when the window dropped nothing', () => {
    const paged = window(1, 3);
    expect(retainDropped(paged, window(4, 6), window(4, 8))).toBe(paged);
  });

  it('ignores repeats inside the window it is replacing', () => {
    const previous = [entry('e1'), entry('e1'), entry('e2')];
    expect(ids(retainDropped([], previous, [entry('e3')]))).toEqual(['e1', 'e2']);
  });

  it('caps the combined list, dropping the oldest first', () => {
    const paged = window(1, MAX_RETAINED_ENTRIES - 5);
    const previous = window(MAX_RETAINED_ENTRIES - 4, MAX_RETAINED_ENTRIES + 5);
    const live = window(MAX_RETAINED_ENTRIES + 6, MAX_RETAINED_ENTRIES + 10);
    const older = retainDropped(paged, previous, live);
    expect(older).toHaveLength(MAX_RETAINED_ENTRIES - live.length);
    expect(older[0]?.id).toBe('e11');
    expect(older[older.length - 1]?.id).toBe(`e${MAX_RETAINED_ENTRIES + 5}`);
  });
});

describe('combineEntries', () => {
  it('shows loaded history first and the live window last', () => {
    expect(combineEntries([entry('a')], [entry('b')]).map((item) => item.id)).toEqual(['a', 'b']);
  });

  it('shows an id the live window owns only once, in the live position', () => {
    const combined = combineEntries([entry('a'), entry('b')], [entry('b', '갱신됨'), entry('c')]);
    expect(combined.map((item) => item.id)).toEqual(['a', 'b', 'c']);
    expect(combined[1]?.text).toBe('갱신됨');
  });

  it('returns the live window untouched when nothing older is loaded', () => {
    const live = [entry('a')];
    expect(combineEntries([], live)).toBe(live);
  });
});

describe('isHistoryExhausted', () => {
  it('stops on hasMore false', () => {
    expect(isHistoryExhausted({ entries: [entry('a')], hasMore: false })).toBe(true);
    expect(isHistoryExhausted({ entries: [entry('a')], hasMore: true })).toBe(false);
  });

  it('stops on an empty page, which is what an evicted `before` answers', () => {
    expect(isHistoryExhausted({ entries: [], hasMore: true })).toBe(true);
    expect(isHistoryExhausted({ entries: [], hasMore: false })).toBe(true);
  });
});
