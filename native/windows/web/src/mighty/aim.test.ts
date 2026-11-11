// A reveal from another view (a pipeline card, a timeline row): where the diagram's aim lands (camera.ts aimLanding), and why the block it landed on
// is held in place afterwards (Diagram.tsx): the newest result is measured only once it is drawn, which moves every block under it.
import { describe, expect, it } from 'vitest';
import type { MightyGraphAgent, MightyGraphRun } from '../store/models';
import { aimLanding, cameraOffset } from './camera';
import { makeLayout, nodeId } from './layout';
import { pinnedAnchor, pinnedCamera } from './resize';

const agent = (id: string, status: string, extra: Partial<MightyGraphAgent> = {}): MightyGraphAgent => ({ id, title: id, input: 'do ' + id, status, entries: [], ...extra });
const run = (id: string, status: string, agents: MightyGraphAgent[]): MightyGraphRun => ({
  id, input: 'request ' + id, status, rootEntries: [], agents, resultEntries: [], provider: 'claude', ...(status === 'completed' ? { finalOutput: 'answer ' + id } : {}),
});
// The web smoke's fixture: finished requests with a completed reader, and a running one whose writer still runs (a card in the role view).
const runs = (writer: MightyGraphAgent[] = []) => [
  run('smoke-run-3', 'completed', [agent('reader', 'completed')]),
  run('smoke-run-4', 'running', [agent('reader', 'completed'), agent('writer', 'running'), ...writer]),
];
const layoutOf = (all: MightyGraphRun[], opened: ReadonlySet<string> = new Set(), fanOut = true) =>
  makeLayout(all, '', false, new Set(), { splitsExecution: true, fanOut: fanOut ? { opened } : null });
const id = (suffix: string) => nodeId('smoke-run-4', suffix);

describe('where an aim from another view lands', () => {
  it('lands on the block asked for', () => {
    const all = runs();
    const landing = aimLanding({ nodeId: id('request') }, layoutOf(all), all, true, new Set());
    expect(landing && 'land' in landing ? landing.land.id : null).toBe(id('request'));
  });

  it('opens a block drawn as a card, then lands on that block', () => {
    const all = runs();
    expect(aimLanding({ nodeId: id('agent:writer'), fallbackNodeId: id('request') }, layoutOf(all), all, true, new Set())).toEqual({ open: id('agent:writer') });
    const opened = new Set([id('agent:writer')]);
    const landing = aimLanding({ nodeId: id('agent:writer') }, layoutOf(all, opened), all, true, opened);
    expect(landing && 'land' in landing ? [landing.land.id, landing.land.compact === true] : null).toEqual([id('agent:writer'), false]);
  });

  it('opens the card that hides a block, one card at a time, until it lands on that block', () => {
    const all = runs([agent('child', 'completed', { parentID: 'writer' })]);
    const aim = { nodeId: id('agent:child'), fallbackNodeId: id('request') };
    // The running writer is a card that folds its child: the child stands behind it.
    expect(aimLanding(aim, layoutOf(all), all, true, new Set())).toEqual({ open: id('agent:writer') });
    // Opened, the writer draws its child, an ended sub-agent and so a card too: it opens next.
    const opened = new Set([id('agent:writer')]);
    expect(aimLanding(aim, layoutOf(all, opened), all, true, opened)).toEqual({ open: id('agent:child') });
    const both = new Set([id('agent:writer'), id('agent:child')]);
    const landing = aimLanding(aim, layoutOf(all, both), all, true, both);
    expect(landing && 'land' in landing ? landing.land.id : null).toBe(id('agent:child'));
  });

  it('falls back to the request without the role view, and to nothing when nothing is drawn', () => {
    const all = runs();
    const landing = aimLanding({ nodeId: id('agent:gone'), fallbackNodeId: id('request') }, layoutOf(all, new Set(), false), all, false, new Set());
    expect(landing && 'land' in landing ? landing.land.id : null).toBe(id('request'));
    expect(aimLanding({ nodeId: 'nothing', fallbackNodeId: 'none' }, layoutOf(all), all, true, new Set())).toBeNull();
  });
});

describe('the block an aim landed on is held where it landed', () => {
  it('stays on screen while the newest result above it is measured, which moves it up by the difference', () => {
    const all = runs();
    const viewport = { w: 900, h: 550 };
    const before = makeLayout(all, '', false, new Set(), { splitsExecution: true, fanOut: { opened: new Set() }, viewport, zoom: 1 });
    // The newest result is measured once drawn: shorter than the pane-high block it stood as.
    const after = makeLayout(all, '', false, new Set(), { splitsExecution: true, fanOut: { opened: new Set() }, viewport, zoom: 1, resultContentHeight: 140 });
    const frame = (layout: typeof before, suffix: string) => layout.nodes.find((n) => n.id === id(suffix))!.frame;
    const result = (layout: typeof before) => layout.nodes.find((n) => n.id === nodeId('smoke-run-3', 'result'))!.frame;
    const drop = result(before).h - result(after).h;
    expect(drop).toBeGreaterThan(200);
    expect(frame(before, 'request').y - frame(after, 'request').y).toBeCloseTo(drop);
    // The aim's camera, then the camera that keeps the block's top-left corner where it landed.
    const landed = cameraOffset(frame(before, 'request'), viewport, 1, false);
    const anchor = pinnedAnchor({ x: 0, y: 0 }, frame(before, 'request'), landed, 1);
    const held = pinnedCamera(anchor, { x: 0, y: 0 }, frame(after, 'request'), 1);
    expect(held.y + frame(after, 'request').y).toBeCloseTo(landed.y + frame(before, 'request').y);
    expect(held.x + frame(after, 'request').x).toBeCloseTo(landed.x + frame(before, 'request').x);
    // Without it the block would stand `drop` higher: past the viewport's top in a pane this tall.
    expect(landed.y + frame(after, 'request').y + frame(after, 'request').h).toBeLessThan(0);
  });
});
