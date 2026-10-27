import { describe, expect, it } from 'vitest';
import fixture from '@tokens/pending.json';
import { eased, isNearBottom, submitScroll, submitViewFor, type SubmitView } from './submitScroll';

interface ScrollCase { name: string; input: { view: SubmitView; own: boolean; chosenBoard: 'newest' | 'older'; nearBottom: boolean }; expect: { scroll: string; resetBoard: boolean } }

const cases = fixture.scrollTargets as ScrollCase[];

describe('submit scroll (pending.json scrollRule)', () => {
  it('has a case for every view', () => {
    expect(new Set(cases.map((entry) => entry.input.view))).toEqual(new Set(['basic', 'diagram', 'timeline', 'pipeline']));
  });

  for (const entry of cases) {
    it(entry.name, () => {
      const { view, own, chosenBoard, nearBottom } = entry.input;
      expect(submitScroll(view, own, chosenBoard === 'newest', nearBottom)).toEqual(entry.expect);
    });
  }

  it('resets the board only for an own submit, and only in the pipeline', () => {
    for (const view of ['basic', 'diagram', 'timeline'] as const) {
      for (const own of [true, false]) for (const near of [true, false]) expect(submitScroll(view, own, false, near).resetBoard).toBe(false);
    }
    expect(submitScroll('pipeline', true, false, false).resetBoard).toBe(true);
    expect(submitScroll('pipeline', false, false, true).resetBoard).toBe(false);
  });

  it('reads the pane view as MightyTimeline.Mode does', () => {
    expect(submitViewFor(false, 'timeline')).toBe('basic');
    expect(submitViewFor(true, 'timeline')).toBe('timeline');
    expect(submitViewFor(true, 'structure')).toBe('pipeline');
    expect(submitViewFor(true, 'diagram')).toBe('diagram');
    expect(submitViewFor(true, undefined)).toBe('diagram');
    expect(submitViewFor(true, 'unknown')).toBe('diagram');
  });

  it('counts 32 px from the bottom as near it', () => {
    expect(isNearBottom(1000, 668, 300)).toBe(true);
    expect(isNearBottom(1000, 667, 300)).toBe(false);
  });

  it('moves along the standard easing from start to end', () => {
    expect(eased(100, 500, 0)).toBe(100);
    expect(eased(100, 500, 1)).toBe(500);
    const half = eased(100, 500, 0.5);
    expect(half).toBeGreaterThan(300);
    expect(half).toBeLessThan(500);
  });
});
