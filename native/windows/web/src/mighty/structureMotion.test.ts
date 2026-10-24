import { describe, expect, it } from 'vitest';
import {
  ColumnWatch, PageWatch, ScrollBottom, SettleGrace, StaleAfter, WheelTracker, axisOf, canMove, confined, isDrag, mayPan, panOrigin,
  scrollable, StructureNavigation, stepsSummary, taker, userMoving, wheelOrigin, type Page,
} from './structureMotion';

// Translated from MightyClaude.Core.Tests MightyStructureVerification.cs and MightyStructureBoardVerification.cs.
const xy = (x: number, y: number) => ({ x, y });
const page = (originY: number, documentHeight: number, newestBottom: number | null, visibleHeight = 600, newestId: string | null = 'b'): Page => ({ originY, visibleHeight, documentHeight, newestId, newestBottom });
const column = (id: string, origin: number, document: number): Page => page(origin, document, document - 8, 600, id);

describe('structure pan (MightyStructurePan)', () => {
  it('dragging empty space pans', () => {
    const min = xy(0, 0), max = xy(240, 1800);
    expect(panOrigin(xy(100, 900), xy(300, 200), xy(260, 270), min, max)).toEqual(xy(140, 830));
    expect(panOrigin(xy(20, 1750), xy(300, 300), xy(600, -500), min, max)).toEqual(xy(0, 1800));
    expect(panOrigin(xy(20, 1750), xy(300, 300), xy(-900, 4000), min, max)).toEqual(xy(240, 0));
    expect(panOrigin(xy(5, 5), xy(0, 0), xy(-80, -60), xy(10, Number.NaN), xy(0, 100))).toEqual(xy(5, 5));
    expect(scrollable(xy(0, 0), xy(0, 1000))).toBe(true);
    expect(scrollable(xy(0, 0), xy(0.2, 0.4))).toBe(false);
    expect(mayPan('page', false, true)).toBe(true);
    expect(['textOrControl', 'innerScroll', 'outside'].some((t) => mayPan(t as 'page', false, true))).toBe(false);
    expect(mayPan('page', true, true) || mayPan('page', false, false)).toBe(false);
    expect([isDrag(xy(100, 100), xy(100, 100)), isDrag(xy(100, 100), xy(102, 98)), isDrag(xy(100, 100), xy(103, 100)), isDrag(xy(100, 100), xy(100, 96))]).toEqual([false, false, true, true]);
  });
  it('a drag in a column moves the column up and down and the page sideways', () => {
    const p = confined(xy(0, 0), xy(240, 30), xy(100, 0), 'horizontal');
    const c = confined(xy(0, 0), xy(4, 1800), xy(0, 900), 'vertical');
    expect(p).toEqual({ minimum: xy(0, 0), maximum: xy(240, 0) });
    expect(c).toEqual({ minimum: xy(0, 0), maximum: xy(0, 1800) });
    expect(panOrigin(xy(100, 0), xy(300, 200), xy(260, 270), p.minimum, p.maximum)).toEqual(xy(140, 0));
    expect(panOrigin(xy(0, 900), xy(300, 200), xy(260, 270), c.minimum, c.maximum)).toEqual(xy(0, 830));
  });
});

describe('structure wheel (MightyStructureWheel)', () => {
  it('the wheel moves the origin and stays on its axis', () => {
    const min = xy(0, 0), max = xy(240, 1800), start = xy(100, 900);
    expect(wheelOrigin(start, xy(0, 120), null, min, max)).toEqual(xy(100, 780));
    expect(wheelOrigin(start, xy(30, -40), null, min, max)).toEqual(xy(70, 940));
    expect([axisOf(xy(-6, 12)), axisOf(xy(-12, 6)), axisOf(xy(0, 0))]).toEqual(['vertical', 'horizontal', null]);
    expect(wheelOrigin(start, xy(-6, 12), 'vertical', min, max)).toEqual(xy(100, 888));
    expect(wheelOrigin(xy(230, 1790), xy(-50, -100), null, min, max)).toEqual(max);
    const right = canMove(xy(0, 0), xy(-20, 0), 'horizontal', xy(0, 0), xy(400, 0));
    const left = canMove(xy(0, 0), xy(20, 0), 'horizontal', xy(0, 0), xy(400, 0));
    expect([right, left]).toEqual([true, false]);
    expect([taker([right, true]), taker([left, true]), taker([false, false]), taker([false])]).toEqual([0, 1, 1, 0]);
  });
  it('a gesture is decided once and is the user\'s until it settles', () => {
    let wheel = new WheelTracker();
    expect(wheel.feed('began', true, xy(0, 0), 10)).toBe('hold');
    expect(wheel.feed('changed', false, xy(-3, 12), 10.02)).toBe('choose');
    expect(wheel.axis).toBe('vertical');
    expect(wheel.feed('changed', false, xy(-8, 4), 10.04)).toBe('move');
    expect(wheel.feed('ended', false, xy(0, 0), 10.06)).toBe('hold');
    expect(wheel.feed('momentum', false, xy(0, 9), 10.1)).toBe('move');
    expect(wheel.feed('momentumEnded', false, xy(0, 0), 10.5)).toBe('hold');
    expect(wheel.feed('momentum', false, xy(0, 9), 11)).toBe('pass');
    wheel = new WheelTracker();
    expect(wheel.feed('step', true, xy(0, -30), 10)).toBe('choose');
    expect(wheel.feed('step', false, xy(0, -30), 10.1)).toBe('pass');
    wheel = new WheelTracker(); wheel.feed('step', true, xy(0, -30), 10);
    expect(wheel.userScrolling(10 + SettleGrace - 0.01)).toBe(true);
    expect(wheel.userScrolling(10 + SettleGrace + 0.01)).toBe(false);
    wheel = new WheelTracker(); wheel.feed('began', true, xy(0, 0), 10); wheel.feed('changed', true, xy(0, 12), 10.1);
    expect(wheel.userScrolling(10.1 + StaleAfter - 0.01)).toBe(true);
    expect(wheel.userScrolling(10.1 + StaleAfter + 0.01)).toBe(false);
    const idle = new WheelTracker();
    expect(userMoving(false, false, 5, idle, 5.2)).toBe(true);
    expect(userMoving(false, false, 5, idle, 5.3)).toBe(false);
  });
});

describe('structure follow (MightyStructureFollow)', () => {
  it('the page follows the newest card', () => {
    let watch = new PageWatch();
    watch.observe(page(2000, 3000, 2500), false);
    expect(watch.observe(page(2400, 3000, 2700), false)).toBeNull();
    expect(watch.following).toBe(true);
    expect(watch.observe(page(2400, 3000, 2960), false)).toBeNull();
    watch = new PageWatch(); watch.observe(page(1400, 2000, 1992), false);
    expect(watch.observe(page(1400, 2300, 2292), false)).toEqual(ScrollBottom);
    watch = new PageWatch(); watch.observe(page(1000, 3000, 1590), false);
    expect(watch.observe(page(1000, 3000, 1700), false)).toEqual({ card: 'b' });
    watch = new PageWatch(); watch.observe(page(1400, 2000, 1992), false);
    expect(watch.observe(page(300, 2000, 1992), false)).toBeNull();
    expect(watch.following).toBe(false);
    expect(watch.observe(page(300, 2400, 2392), false)).toBeNull();
    expect(watch.observe(page(300, 2400, 2392, 560), false)).toBeNull();
    expect(watch.observe(page(1800, 2400, 2392, 560), false)).toBeNull();
    expect(watch.following).toBe(true);
    watch = new PageWatch(); watch.observe(page(1400, 2000, 1992), false);
    expect(watch.observe(page(1200, 2300, 2292), true)).toBeNull();
    expect(watch.following).toBe(false);
    watch = new PageWatch(); watch.observe(page(1400, 2000, 1992), false); watch.observe(page(200, 2000, 1992), false);
    expect(watch.newRequest('c')).toEqual({ card: 'c' });
    expect(watch.observe(page(1500, 2400, 2392, 600, 'c'), false)).toEqual(ScrollBottom);
    watch = new PageWatch();
    expect(watch.appeared('b')).toEqual({ card: 'b' });
    watch.stop();
    expect(watch.appeared('b')).toBeNull();
    const empty = new PageWatch();
    expect(empty.observe(page(0, 40, null, 600, null), false)).toBeNull();
    expect(empty.following).toBe(true);
  });
  it('a user\'s move is judged when it happens', () => {
    const watch = new PageWatch();
    watch.observe(page(1523, 2100, 2091, 577), false);
    watch.userScrolled(-180);
    expect(watch.following).toBe(false);
    expect(watch.observe(page(1343, 2130, 2121, 577), false)).toBeNull();
    const fresh = new PageWatch();
    fresh.userScrolled(-300);
    expect(fresh.following).toBe(true);
  });
  it('each column follows its own end; only the newest board follows', () => {
    let watch = new ColumnWatch();
    expect(watch.appeared('b', true, 'execution')).toEqual({ card: 'execution' });
    expect(watch.observe('b', true, 'execution', column('execution', 1400, 2000), false)).toBeNull();
    expect(watch.observe('b', true, 'execution', column('execution', 1400, 2300), false)).toEqual(ScrollBottom);
    watch = new ColumnWatch();
    watch.observe('b', true, 'execution', column('execution', 1400, 2000), false);
    watch.observe('b', true, 'request', column('request', 900, 1500), false);
    watch.userScrolled('execution', -200);
    expect([watch.following('execution'), watch.following('request')]).toEqual([false, true]);
    watch = new ColumnWatch();
    watch.observe('new', true, 'execution', column('execution', 1400, 2000), false);
    expect(watch.appeared('old', false, 'execution')).toBeNull();
    expect(watch.following('execution')).toBe(false);
    expect(watch.observe('old', false, 'execution', column('execution', 0, 2000), false)).toBeNull();
    expect(watch.appeared('new', true, 'execution')).toEqual({ card: 'execution' });
    expect(watch.newRequest('new', 'plan')).toEqual({ card: 'plan' });
  });
});

describe('structure navigation (MightyStructureBoard.Navigation)', () => {
  const boards = (...ids: string[]) => ids.map((id) => ({ id, runIds: [id + '-run'] }));
  it('moves on with the newest board until the user picks another; a newer one is then unseen', () => {
    const nav = new StructureNavigation();
    expect(nav.index([])).toBeNull();
    expect(nav.index(boards('a', 'b'))).toBe(1);
    nav.step(-1, boards('a', 'b'));
    expect(nav.chosen).toBe('a');
    expect(nav.index(boards('a', 'b', 'c'))).toBe(0);
    expect(nav.hasUnseen(boards('a', 'b'))).toBe(false);
    expect(nav.hasUnseen(boards('a', 'b', 'c'))).toBe(true);
    nav.showNewest(boards('a', 'b', 'c'));
    expect(nav.chosen).toBeNull();
    expect(nav.onNewest(boards('a', 'b', 'c', 'd'))).toBe(true);
  });
  it('a picked board that is gone is found by a request it held, else the oldest', () => {
    const nav = new StructureNavigation();
    nav.select('b', boards('a', 'b', 'c'));
    expect(nav.index([{ id: 'b2', runIds: ['x'] }, { id: 'b3', runIds: ['b-run'] }, { id: 'c', runIds: [] }])).toBe(1);
    expect(nav.index(boards('x', 'y'))).toBe(0);
    nav.step(5, boards('a', 'b', 'c'));
    expect(nav.chosen).toBeNull();
  });
  it('the steps summary', () => {
    expect(stepsSummary([{ title: 'Request', step: 'done' }, { title: 'Run', step: 'current' }, { title: 'Result', step: 'pending' }])).toBe('Request ✓ → Run ● → Result ○');
  });
});
