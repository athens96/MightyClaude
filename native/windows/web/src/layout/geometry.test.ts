import { describe, expect, it } from 'vitest';
import { clampRatio, dropZone, layoutGroups, layoutMinimum } from './geometry';
import type { PaneLayoutNode } from '../store/models';

const tabs = (id: string, ids: string[]): PaneLayoutNode => ({ id, kind: 'tabs', sessionIds: ids, selectedSessionId: ids[0], ratio: 0.5, children: [] });

describe('layout geometry (PaneLayout.cs)', () => {
  it('drop zone: the top strip merges, the nearest edge within 24% splits', () => {
    expect(dropZone(5, 10, 400, 300)).toBe('center');
    expect(dropZone(20, 150, 400, 300)).toBe('left');
    expect(dropZone(390, 150, 400, 300)).toBe('right');
    expect(dropZone(200, 290, 400, 300)).toBe('bottom');
    expect(dropZone(200, 60, 400, 300)).toBe('top');
    expect(dropZone(200, 150, 400, 300)).toBe('center');
    expect(dropZone(10, 100, 0, 300)).toBe('center');
  });
  it('ratio is kept within 0.15 to 0.85', () => {
    expect(clampRatio(0.02)).toBe(0.15);
    expect(clampRatio(0.9)).toBe(0.85);
    expect(clampRatio(Number.NaN)).toBe(0.5);
  });
  it('minimum sums along the split axis with the divider', () => {
    const split: PaneLayoutNode = { id: 's', kind: 'split', axis: 'horizontal', ratio: 0.5, sessionIds: [], children: [tabs('a', ['1']), tabs('b', ['2'])] };
    expect(layoutMinimum(split)).toEqual({ width: 315 * 2 + 6, height: 290 });
    expect(layoutGroups(split).map((g) => g.id)).toEqual(['a', 'b']);
  });
});
