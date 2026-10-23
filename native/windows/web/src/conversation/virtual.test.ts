import { describe, expect, it } from 'vitest';
import type { LogEntry } from '../store/models';
import { HeightCache, estimateHeight, layout, nearBottom, visibleRange } from './virtual';

const entry = (id: string, text = 'x'): LogEntry => ({ id, kind: 'assistant', text, timestamp: '' });
const entries = (count: number) => Array.from({ length: count }, (_, index) => entry('e' + index));

describe('layout / visibleRange', () => {
  it('adds the heights up', () => {
    expect(Array.from(layout(entries(3), () => 10))).toEqual([0, 10, 20, 30]);
  });

  it('draws only what meets the viewport and its overscan', () => {
    const offsets = layout(entries(5000), () => 100);
    expect(visibleRange(offsets, 0, 500, 0)).toEqual([0, 5]);
    expect(visibleRange(offsets, 1050, 500, 0)).toEqual([10, 16]);
    expect(visibleRange(offsets, 1000, 500, 200)).toEqual([8, 17]);
  });

  it('clamps a scroll past the end to the last screen', () => {
    const offsets = layout(entries(100), () => 100);
    expect(visibleRange(offsets, Number.MAX_SAFE_INTEGER, 500, 0)).toEqual([95, 100]);
  });

  it('draws at least one entry, none for an empty list', () => {
    expect(visibleRange(layout([entry('a')], () => 5000), 2000, 100, 0)).toEqual([0, 1]);
    expect(visibleRange(layout([], () => 1), 0, 100)).toEqual([0, 0]);
  });
});

describe('HeightCache', () => {
  it('keeps heights per width and tells when one changed', () => {
    const cache = new HeightCache();
    expect(cache.set('a', 300, 40)).toBe(true);
    expect(cache.set('a', 300, 40)).toBe(false);
    expect(cache.get('a', 300)).toBe(40);
    expect(cache.get('a', 500)).toBeUndefined();
  });

  it('forgets the oldest width past four', () => {
    const cache = new HeightCache();
    for (const width of [1, 2, 3, 4, 5]) cache.set('a', width, width);
    expect(cache.get('a', 1)).toBeUndefined();
    expect(cache.get('a', 5)).toBe(5);
  });
});

describe('estimateHeight / nearBottom', () => {
  it('grows with the text', () => {
    expect(estimateHeight(entry('a', 'x'.repeat(900)))).toBeGreaterThan(estimateHeight(entry('b', 'x')));
  });

  it('follows within 32 px of the bottom', () => {
    expect(nearBottom(1000, 668, 300)).toBe(true);
    expect(nearBottom(1000, 667, 300)).toBe(false);
  });
});
