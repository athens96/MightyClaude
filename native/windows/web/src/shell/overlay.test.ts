import { describe, expect, it } from 'vitest';
import { emptyState, reduce } from '../store/store';
import { overlapsAny } from './overlay';

describe('page overlays over a browser slot (src/shell/overlay.ts)', () => {
  it('the store counts the overlays open, never below none', () => {
    let state = reduce(emptyState, { kind: 'overlay', open: true });
    state = reduce(state, { kind: 'overlay', open: true });
    expect(state.overlays).toBe(2);
    state = reduce(reduce(reduce(state, { kind: 'overlay', open: false }), { kind: 'overlay', open: false }), { kind: 'overlay', open: false });
    expect(state.overlays).toBe(0);
  });
  it('an overlay hides the slot only where it covers part of it', () => {
    const slot = { left: 100, top: 100, right: 500, bottom: 400 };
    expect(overlapsAny(slot, [{ left: 480, top: 380, right: 700, bottom: 600 }])).toBe(true);
    expect(overlapsAny(slot, [{ left: 0, top: 0, right: 100, bottom: 100 }])).toBe(false);
    expect(overlapsAny(slot, [{ left: 600, top: 120, right: 800, bottom: 300 }])).toBe(false);
    expect(overlapsAny(slot, [{ left: 200, top: 200, right: 200, bottom: 300 }])).toBe(false);
    expect(overlapsAny({ left: 0, top: 0, right: 0, bottom: 0 }, [{ left: -10, top: -10, right: 10, bottom: 10 }])).toBe(false);
  });
});
