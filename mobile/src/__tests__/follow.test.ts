import { FOLLOW_THRESHOLD, followAfterScroll, isNearBottom } from '@/lib/follow';

const at = (offsetY: number) => ({ contentHeight: 1000, viewportHeight: 400, offsetY });

describe('following the newest content', () => {
  it('counts the last stretch above the bottom as the bottom', () => {
    expect(isNearBottom(at(600))).toBe(true);
    expect(isNearBottom(at(600 - FOLLOW_THRESHOLD + 1))).toBe(true);
    expect(isNearBottom(at(600 - FOLLOW_THRESHOLD))).toBe(false);
    // A list shorter than the screen is always at its bottom.
    expect(isNearBottom({ contentHeight: 200, viewportHeight: 400, offsetY: 0 })).toBe(true);
  });

  it('stops following when the user scrolls up and resumes when they come back', () => {
    let following = true;
    following = followAfterScroll(following, at(200), true);
    expect(following).toBe(false);
    following = followAfterScroll(following, at(590), true);
    expect(following).toBe(true);
  });

  it('never lets its own jumps or grown content change the decision', () => {
    // Content grew under a still viewport: far from the bottom, but nobody scrolled.
    expect(followAfterScroll(true, at(0), false)).toBe(true);
    // Reading higher up stays reading, even when a programmatic scroll lands at the end.
    expect(followAfterScroll(false, at(600), false)).toBe(false);
  });
});
