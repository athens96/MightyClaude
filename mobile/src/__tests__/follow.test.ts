import { FOLLOW_THRESHOLD, PULL_REFRESH_DISTANCE, PULL_START_SLACK, blocksProgressKey, entriesProgressKey, followAfterScroll, isNearBottom, pullPhase } from '@/lib/follow';

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

describe('pulling past the newest content to refresh', () => {
  it('stays idle until the finger moves up', () => {
    expect(pullPhase(0)).toBe('idle');
    expect(pullPhase(-30)).toBe('idle');
  });

  it('is pulling on the way up and armed from the refresh distance on', () => {
    expect(pullPhase(1)).toBe('pulling');
    expect(pullPhase(PULL_REFRESH_DISTANCE - 1)).toBe('pulling');
    expect(pullPhase(PULL_REFRESH_DISTANCE)).toBe('armed');
    expect(pullPhase(PULL_REFRESH_DISTANCE * 3)).toBe('armed');
  });

  it('may only start right at the bottom', () => {
    expect(isNearBottom(at(600), PULL_START_SLACK)).toBe(true);
    expect(isNearBottom(at(597), PULL_START_SLACK)).toBe(true);
    expect(isNearBottom(at(590), PULL_START_SLACK)).toBe(false);
    expect(isNearBottom({ contentHeight: 200, viewportHeight: 400, offsetY: 0 }, PULL_START_SLACK)).toBe(true);
  });
});

describe('what counts as work moving on', () => {
  const block = (id: string, status = 'running', output = '') => ({ id, status, output });
  const run = (id: string, blocks: ReturnType<typeof block>[], status = 'running', result?: string) => ({ id, status, blocks, result });

  it('changes on a new block, a change to the newest block, and a settled result', () => {
    const base = blocksProgressKey([run('r1', [block('a')])]);
    expect(blocksProgressKey([run('r1', [block('a'), block('b')])])).not.toBe(base);
    expect(blocksProgressKey([run('r1', [block('a', 'running', 'more')])])).not.toBe(base);
    expect(blocksProgressKey([run('r1', [block('a')], 'done', 'answer')])).not.toBe(base);
    expect(blocksProgressKey([run('r1', [block('a')]), run('r2', [])])).not.toBe(base);
  });

  it('stays put when only older blocks change', () => {
    const before = blocksProgressKey([run('r1', [block('a'), block('b')])]);
    expect(blocksProgressKey([run('r1', [block('a', 'done', 'x'), block('b')])])).toBe(before);
    expect(blocksProgressKey([])).toBe('');
  });

  it('tracks the newest transcript entry the same way', () => {
    const base = entriesProgressKey([{ id: '1', text: 'a' }]);
    expect(entriesProgressKey([{ id: '1', text: 'a' }, { id: '2', text: 'b' }])).not.toBe(base);
    expect(entriesProgressKey([{ id: '1', text: 'ab' }])).not.toBe(base);
    expect(entriesProgressKey([])).toBe('');
  });

  it('ignores older history paged in above', () => {
    const base = entriesProgressKey([{ id: '5', text: 'e' }]);
    expect(entriesProgressKey([{ id: '3', text: 'c' }, { id: '4', text: 'd' }, { id: '5', text: 'e' }])).toBe(base);
  });
});
