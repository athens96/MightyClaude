// The Windows-only layout mode (splitsExecution, which the shared vectors do not lay out) and the rules around the
// layout: sizing and drags, follow, zoom and wheel, dot grid, activity outline, saved sizes. Expected values are the C#
// rules worked by hand.
import { describe, expect, it } from 'vitest';
import type { MightyGraphRun } from '../store/models';
import { PendingNodeID, imagesHeight, makeLayout, nodeId, resultCap, resultDrag, resultFitSize, resultSize, resultViewportLimit, runOf, suffixOf } from './layout';
import {
  FollowWatch, activityPhase, barHeight, cameraTween, dashFitting, dashForCard, dotFirst, dotRadius, dotsShown, followTargetID, isAtNewest, outlineOf,
  perimeter, runsChanged, wheelPan, wheelScrollTarget, zoomAnchored, zoomIn, zoomLabel, zoomOut,
} from './camera';
import { normalizeForRuns, setBlockSize, splittingExecution } from './prefs';

const run = (id: string, status: string, agents: MightyGraphRun['agents'] = []): MightyGraphRun =>
  ({ id, input: 'Request', status, rootEntries: [], agents, resultEntries: [] });
const agent = (id: string, status: string, parentID?: string): MightyGraphRun['agents'][number] =>
  ({ id, title: id, input: 'Work', status, entries: [], ...(parentID ? { parentID } : {}) });
const frame = (layout: ReturnType<typeof makeLayout>, id: string) => layout.nodes.find((n) => n.id === id)?.frame;

describe('layout with the 실행 block (splitsExecution)', () => {
  it('stacks request, 실행 and result, then the draft', () => {
    const layout = makeLayout([run('r1', 'completed')], '', false, new Set(), { splitsExecution: true });
    expect(layout.nodes.map((n) => n.kind)).toEqual(['request', 'work', 'result', 'draft']);
    expect(frame(layout, nodeId('r1', 'request'))).toEqual({ x: 24, y: 24, w: 500, h: 140 });
    expect(frame(layout, nodeId('r1', 'execution'))).toEqual({ x: 24, y: 196, w: 500, h: 280 });
    expect(frame(layout, nodeId('r1', 'result'))).toEqual({ x: 24, y: 508, w: 500, h: 200 });
    expect(frame(layout, PendingNodeID)).toEqual({ x: 24, y: 740, w: 500, h: 140 });
    expect(layout.edges).toEqual([
      { source: nodeId('r1', 'request'), target: nodeId('r1', 'execution'), joins: false },
      { source: nodeId('r1', 'execution'), target: nodeId('r1', 'result'), joins: true },
      { source: nodeId('r1', 'result'), target: PendingNodeID, joins: true },
    ]);
  });

  it('hangs agents off the 실행 block, expanded sizes and a running run without result or draft', () => {
    const r = run('r2', 'running', [agent('a', 'running'), agent('b', 'completed', 'a')]);
    const id = nodeId('r2', 'request');
    const layout = makeLayout([r], '', true, new Set([id]), { splitsExecution: true });
    expect(layout.nodes.map((n) => n.kind)).toEqual(['request', 'work', 'agent', 'agent']);
    expect(frame(layout, id)?.h).toBe(280);
    expect(layout.edges[1]).toEqual({ source: nodeId('r2', 'execution'), target: nodeId('r2', 'agent:a'), joins: false });
    expect(frame(layout, nodeId('r2', 'agent:a'))).toEqual({ x: 94, y: 24 + 280 + 32 + 280 + 32, w: 360, h: 240 });
  });

  it('hangs images and executions beside the 실행 block, never moving it', () => {
    const layout = makeLayout([run('r3', 'completed')], '', false, new Set(), {
      splitsExecution: true, galleries: [{ runID: 'r3', step: 'request', count: 7 }], executions: [{ runID: 'r3', key: 'k' }],
    });
    const work = frame(layout, nodeId('r3', 'execution'))!;
    expect(frame(layout, nodeId('r3', 'images:request'))).toEqual({ x: 532, y: work.y, w: 304, h: imagesHeight(7, false) });
    expect(imagesHeight(7, false)).toBe(38 + 24 + 2 * 88 + 8);
    expect(frame(layout, nodeId('r3', 'ouroboros-execution:k'))).toEqual({ x: 844, y: work.y, w: 380, h: 290 });
  });

  it('uses saved sizes and the newest result fits the window', () => {
    const id = nodeId('r4', 'agent:a');
    const layout = makeLayout([run('r4', 'completed', [agent('a', 'completed')])], '', false, new Set(), {
      splitsExecution: true, blockSizes: { [id]: { width: 9999, height: 10 } }, viewport: { w: 1000, h: 700 }, zoom: 1,
    });
    expect(frame(layout, id)).toMatchObject({ w: 1400, h: 140 });
    expect(frame(layout, nodeId('r4', 'result'))).toMatchObject({ w: 952, h: 652 });
    expect(layout.fittedResultID).toBe(nodeId('r4', 'result'));
  });
});

describe('sizing and drags', () => {
  it('fits, limits and caps the newest result', () => {
    expect(resultFitSize({ w: 300, h: 100 }, false)).toEqual({ w: 500, h: 200 });
    expect(resultViewportLimit({ w: 1048, h: 748 }, 2, true)).toEqual({ w: 300, h: 350 });
    expect(resultCap({ w: 900, h: 900 }, { w: 800, h: 1000 })).toEqual({ w: 800, h: 900 });
    expect(resultSize({ w: 800, h: 600 }, 50.2)).toEqual({ w: 800, h: 140 });
    expect(resultSize({ w: 800, h: 600 }, null)).toEqual({ w: 800, h: 600 });
  });
  it('saves a released drag side by side', () => {
    const limit = { w: 800, h: 600 };
    expect(resultDrag({ w: 900, h: 300 }, true, false, 'live', null, limit, null).live).toEqual({ width: 800, height: 300 });
    expect(resultDrag({ w: 900, h: 300 }, true, false, 'finished', { width: 1000, height: 700 }, limit, null).save).toEqual({ width: 1000, height: 700 });
    expect(resultDrag({ w: 500, h: 300 }, true, true, 'finished', null, limit, { w: 700, h: 500 }).save).toEqual({ width: 500, height: 300 });
    expect(resultDrag({ w: 500, h: 300 }, true, true, 'cancelled', null, limit, null)).toEqual({ live: null, save: null });
  });
  it('reads node ids back', () => {
    const id = nodeId('런-1', 'agent:x');
    expect(id.startsWith('5:')).toBe(true);
    expect(suffixOf(id)).toBe('agent:x');
    expect(runOf(id)).toBe('런-1');
  });
});

describe('follow', () => {
  const runs = [run('f1', 'completed'), run('f2', 'running', [agent('a', 'running')])];
  const layout = makeLayout(runs, '', true, new Set(), { splitsExecution: true });
  it('targets the newest run’s lowest flow block', () => {
    expect(followTargetID(layout, runs)).toBe(nodeId('f2', 'agent:a'));
    expect(followTargetID(makeLayout([], '', false, new Set()), [])).toBe(PendingNodeID);
  });
  it('keeps the block where the user left it, within the slack', () => {
    const target = { x: 0, y: 0, w: 100, h: 500 };
    expect(isAtNewest(target, { x: 0, y: 0 }, { w: 800, h: 520 }, 1)).toBe(true);
    expect(isAtNewest(target, { x: 0, y: 0 }, { w: 800, h: 440 }, 1)).toBe(false);
    const watch = new FollowWatch();
    expect(watch.observe('a', target, { x: 5, y: 0 }, { w: 800, h: 600 }, 1)).toEqual({ x: 5, y: 84 });
    expect(watch.observe('a', { ...target, h: 520 }, { x: 5, y: 84 }, { w: 800, h: 600 }, 1)).toEqual({ x: 5, y: 64 });
    watch.moved('a', target, { x: 0, y: -1000 }, { w: 800, h: 600 }, 1);
    expect(watch.following).toBe(false);
  });
  it('reads run-id changes', () => {
    expect(runsChanged(null, [], true)).toBe('first');
    expect(runsChanged(['a'], ['a', 'b'], false)).toBe('follow');
    expect(runsChanged(['a', 'b'], ['b'], false)).toBe('trim');
    expect(runsChanged(['a', 'b'], ['b'], true)).toBe('none');
  });
});

describe('zoom, wheel, dots, activity', () => {
  it('zooms in steps and keeps the centre', () => {
    expect(zoomIn(1.45)).toBe(1.5);
    expect(zoomOut(0.5)).toBe(0.5);
    expect(zoomIn(1)).toBe(1.1);
    expect(zoomLabel(1.1)).toBe('110%');
    expect(zoomAnchored({ x: 0, y: 0 }, { w: 200, h: 100 }, 1, 2)).toEqual({ x: -100, y: -50 });
  });
  it('routes the wheel', () => {
    expect(wheelPan(120, true, false)).toEqual({ x: -120, y: 0 });
    expect(wheelPan(-120, false, true)).toEqual({ x: -120, y: 0 });
    expect(wheelPan(-120, false, false)).toEqual({ x: 0, y: -120 });
    expect(wheelScrollTarget('a', 'a', false)).toBe('a');
    expect(wheelScrollTarget('a', 'b', false)).toBeNull();
    expect(wheelScrollTarget(null, 'b', true)).toBe('b');
  });
  it('lays the dot grid', () => {
    expect(dotsShown(0.3)).toBe(false);
    expect(dotsShown(0.5)).toBe(true);
    expect(dotRadius(0.5)).toBe(0.6);
    expect(dotFirst(-5, 18)).toBe(13);
  });
  it('fits the marching dashes to the card', () => {
    expect(outlineOf('running', true)).toBe('marching');
    expect(outlineOf('running', false)).toBe('solid');
    expect(outlineOf('waiting', true)).toBe('waiting');
    expect(outlineOf('completed', true)).toBe('none');
    expect(activityPhase(2400, true)).toBeCloseTo(0.5);
    expect(activityPhase(2400, false)).toBe(0);
    const p = perimeter(498, 238, 11);
    const dash = dashForCard(500, 240);
    expect((p / (dash[0]! + dash[1]!)) % 1).toBeCloseTo(0, 6);
    expect(dashFitting(0)).toEqual([9, 7]);
    expect(barHeight(0, 0)).toBe(8);
  });
});

describe('saved sizes (GraphBlockPreferences)', () => {
  it('sets, clears and normalizes', () => {
    const id = nodeId('p', 'request');
    const set = setBlockSize(null, id, { width: 100, height: 5000 });
    expect(set).toEqual({ [id]: { width: 300, height: 1200 } });
    expect(setBlockSize(set, id, null)).toBeNull();
    expect(splittingExecution(set)).toEqual({ [nodeId('p', 'execution')]: { width: 300, height: 1200 } });
    const record = nodeId('p', 'plan-record:x');
    expect(setBlockSize(null, record, { width: 300, height: 50 })).toEqual({ [record]: { width: 300, height: 104 } });
    expect(normalizeForRuns({ ...set, [nodeId('gone', 'request')]: { width: 400, height: 400 } }, [run('p', 'running')])).toEqual(set);
  });
});

describe('own-send camera move (natural submit B2)', () => {
  it('starts where the camera was and ends on the request, easing out', () => {
    const from = { x: 10, y: -400 }, to = { x: 10, y: -1200 };
    expect(cameraTween(from, to, 0)).toEqual(from);
    expect(cameraTween(from, to, 1)).toEqual(to);
    expect(cameraTween(from, to, 2)).toEqual(to);
    expect(cameraTween(from, to, -1)).toEqual(from);
    const half = cameraTween(from, to, 0.5);
    expect(half.x).toBe(10);
    // Past halfway by half the time: the standard easing decelerates.
    expect(half.y).toBeLessThan(-800);
    expect(half.y).toBeGreaterThan(-1200);
  });
});

