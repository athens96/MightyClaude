// The role view on the page (flow.ts, the fan-out of layout.ts), checked against the shared flow vectors the Windows Core asserts too
// (native/contracts/fixtures/flow-vectors.json, FlowVectorVerification.cs) and against the cases of the macOS FlowCardTests, FlowLinesTests,
// FlowAdvisorTests, FlowLogTests and FlowRolesTests that the page decides itself.
import { describe, expect, it } from 'vitest';
import vectors from '@contracts/fixtures/flow-vectors.json';
import ko from '@locales/ko.json';
import { translator } from '../i18n/locale';
import type { MightyGraphAgent, MightyGraphRun } from '../store/models';
import { makeLayout, nodeId } from './layout';
import { followTargetID } from './camera';
import { shownRun, shownRuns } from './notice';
import {
  AdvisorMinimumHeight, advisorCallers, advisorRoute, advisorShows, advisorVisible, cardFigures, delegationChildren, drawnNode, elapsed, estimatedWidth,
  flowAgents, flowOrder, foldsToCard, hasReturn, headerFit, isCompact, liveFlowIds, logTarget, TypeCeiling, TypeFloor,
} from './flow';
import type { FlowAdvisorCopy, FlowCardCopy, FlowRoleCopy } from './types';

const t = translator([ko as Record<string, string>]);
const agent = (id: string, extra: Partial<MightyGraphAgent> = {}): MightyGraphAgent => ({ id, title: id, input: '확인', status: 'completed', entries: [], ...extra });
const run = (id: string, agents: MightyGraphAgent[], extra: Partial<MightyGraphRun> = {}): MightyGraphRun =>
  ({ id, input: '요청 ' + id, status: 'completed', rootEntries: [], agents, resultEntries: [], provider: 'claude', ...extra });

describe('the shared flow vectors', () => {
  type Case = { name: string; running: boolean; opened: string[]; runs: MightyGraphRun[]; expect: { nodes: { id: string; kind: string; x: number; y: number; w: number; h: number; compact?: boolean }[]; edges: { source: string; target: string; joins: boolean }[]; advisorLinks: { band: string; target: string }[]; size: { w: number; h: number }; originX: number; originY: number } };
  for (const item of (vectors as unknown as { cases: Case[] }).cases) {
    it(item.name, () => {
      const opened = new Set(item.opened.flatMap((suffix) => item.runs.map((r) => nodeId(r.id, suffix))));
      const layout = makeLayout(item.runs, '', item.running, new Set(), { splitsExecution: true, fanOut: { opened } });
      expect(layout.nodes.map((n) => ({ id: n.id, kind: n.kind, compact: n.compact === true }))).toEqual(item.expect.nodes.map((n) => ({ id: n.id, kind: n.kind, compact: n.compact === true })));
      layout.nodes.forEach((n, index) => {
        const want = item.expect.nodes[index]!;
        for (const [got, wanted] of [[n.frame.x, want.x], [n.frame.y, want.y], [n.frame.w, want.w], [n.frame.h, want.h]] as const) expect(Math.abs(got - wanted)).toBeLessThanOrEqual(0.001);
      });
      expect(layout.edges).toEqual(item.expect.edges);
      expect(layout.advisorLinks).toEqual(item.expect.advisorLinks);
      expect(Math.abs(layout.size.w - item.expect.size.w)).toBeLessThanOrEqual(0.001);
      expect(Math.abs(layout.size.h - item.expect.size.h)).toBeLessThanOrEqual(0.001);
      expect(Math.abs(layout.originX - item.expect.originX)).toBeLessThanOrEqual(0.001);
    });
  }
});

describe('what a shown run carries for the flow alone (MightyGraphFanOutTests)', () => {
  it('keeps every other view as it was and gives the flow its background blocks where they were launched', () => {
    const request = run('r', [agent('fg'), agent('bg', { status: 'running', background: true }), agent('bg-child', { parentID: 'bg', status: 'running' }),
      agent('task', { status: 'running', background: true, kind: 'task' }), agent('task-child', { parentID: 'task' }), agent('late')]);
    const shown = shownRun(request);
    expect(shown.agents.map((a) => a.id)).toEqual(['fg', 'late']);
    expect(flowAgents(shown).map((a) => a.id)).toEqual(['fg', 'late', 'bg', 'bg-child', 'task', 'task-child']);
    expect(flowAgents(shown).map((_, index) => flowOrder(shown, index))).toEqual([0, 5, 1, 2, 3, 4]);
    expect(shownRun(shown)).toBe(shown);
    const quiet = run('q', [agent('a'), agent('b', { parentID: 'a' })]);
    expect(flowAgents(shownRun(quiet))).toBe(quiet.agents);
    expect(flowOrder(quiet, 1)).toBe(1);
    const notice = run('r:notice:1', [agent('copy', { background: true, sourceBlockId: 'bg' })], { noticeRun: true });
    expect(flowAgents(shownRun(notice)).map((a) => a.id)).toEqual(['copy']);
  });

  it('lays out nothing new without a fan-out', () => {
    const request = run('r', [agent('fg', { status: 'running' }), agent('bg', { status: 'running', background: true })], { status: 'running' });
    const plain = makeLayout([request], '', true, new Set(), { splitsExecution: true });
    expect(plain.nodes.some((n) => n.id === nodeId('r', 'agent:bg'))).toBe(false);
    expect(plain.nodes.find((n) => n.id === nodeId('r', 'agent:fg'))?.frame).toMatchObject({ w: 360, h: 240 });
    expect(plain.nodes.some((n) => n.compact)).toBe(false);
    expect(plain.advisorLinks).toEqual([]);
  });
});

describe('cards (FlowCardTests)', () => {
  it('folds a request\'s own background block at any time and a foreground sub-agent while it runs', () => {
    expect(foldsToCard(agent('fg', { status: 'running' }))).toBe(true);
    expect(foldsToCard(agent('fg'))).toBe(false);
    expect(foldsToCard(agent('bg', { background: true }))).toBe(true);
    expect(foldsToCard(agent('task', { kind: 'task', background: true }))).toBe(true);
    expect(foldsToCard(agent('copy', { background: true, sourceBlockId: 'bg' }))).toBe(false);
    expect(foldsToCard(agent('steer', { kind: 'steer', status: 'running' }))).toBe(false);
    expect(isCompact(agent('fg', { status: 'running' }), true)).toBe(false);
  });

  it('reads a card\'s time against the page\'s clock: since it started while it runs, how long it ran once it ended', () => {
    const base = 1_800_000_000_000;
    const card = (extra: Partial<FlowCardCopy>): FlowCardCopy => ({ title: 'x', status: 'running', stateWord: '실행 중', before: ['실행 중'], after: [], running: true, nested: 0, marks: [], ink: 'agentText', summary: '', isTask: false, activateHelp: '', ...extra });
    expect(cardFigures(t, card({ startedAt: base }), base + 138_000)).toBe('실행 중 · 2분 18초');
    expect(cardFigures(t, card({ status: 'completed', stateWord: '완료', before: ['완료', '종료 코드 0'], running: false, startedAt: base, endedAt: base + 840_000 }), base + 9_999_000)).toBe('완료 · 종료 코드 0 · 14분 0초');
    expect(cardFigures(t, card({ status: 'stopped', before: ['중단'], running: false, startedAt: base }), base)).toBe('중단');
    expect(cardFigures(t, card({ startedAt: base, after: ['도구 3', '토큰 12.3K'] }), base + 5_000)).toBe('실행 중 · 5초 · 도구 3 · 토큰 12.3K');
    expect(elapsed(t, base, base - 5_000)).toBe('0초');
    expect(elapsed(t, base, base + 3_720_000)).toBe('1시간 2분');
  });
});

describe('the delegate line and the way back (FlowLinesTests)', () => {
  it('names the blocks right under a block in launch order, never a notice run\'s copy', () => {
    const request = shownRun(run('r', [agent('scout', { status: 'running' }), agent('watch', { parentID: 'scout', kind: 'task', background: true, status: 'running' }),
      agent('later', { background: true, status: 'running' }), agent('steer', { kind: 'steer' })]));
    expect(delegationChildren('main', request).map((a) => a.id)).toEqual(['scout', 'later']);
    expect(delegationChildren('scout', request).map((a) => a.id)).toEqual(['watch']);
    expect(delegationChildren('main', run('r:notice:1', [agent('copy', { sourceBlockId: 'bg', background: true })], { noticeRun: true }))).toEqual([]);
    expect(hasReturn(request)).toBe(true);
    expect(hasReturn(run('plain', []))).toBe(false);
    expect(hasReturn(run('r:notice:1', [agent('copy', { sourceBlockId: 'bg', background: true })], { noticeRun: true }))).toBe(false);
  });

  it('keeps the return node above the composer until the result stands in its place (MightyGraphFollow)', () => {
    const request = run('r', [agent('fg', { status: 'running' })], { status: 'running' });
    const layout = makeLayout([request], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    expect(followTargetID(layout, [request])).toBe(nodeId('r', 'return'));
    const done = run('r', [agent('fg')], { finalOutput: '결과' });
    const ended = makeLayout([done], '', false, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    expect(ended.nodes.some((n) => n.kind === 'return')).toBe(false);
    expect(followTargetID(ended, [done])).toBe(nodeId('r', 'result'));
  });

  it('makes the edges into running cards, their line and the return node live', () => {
    const request = run('r', [agent('notes', { background: true }), agent('ci', { kind: 'task', background: true, status: 'running' })], { status: 'running' });
    const layout = makeLayout([request], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    const ci = nodeId('r', 'agent:ci'), notes = nodeId('r', 'agent:notes');
    const live = liveFlowIds(layout, new Set([ci]));
    expect(live.has(ci)).toBe(true);
    expect(live.has(notes)).toBe(false);
    expect(live.has(nodeId('r', 'delegate:main'))).toBe(true);
    expect(live.has(nodeId('r', 'return'))).toBe(true);
  });
});

describe('the advisor (FlowAdvisorTests)', () => {
  const band = (rows: number, extra: Partial<FlowAdvisorCopy> = {}): FlowAdvisorCopy => ({
    header: '조언자', consulting: false, rows: Array.from({ length: rows }, (_, i) => ({ id: 'c' + i, caller: '메인', ink: 'taskText', callerNodeId: 'n', when: '', line: '', state: 'done', help: '' })),
    earlier: 0, calls: rows, totals: '', hidesText: false, ...extra,
  });

  it('shows a band on a Claude run that consulted or was started with one on; a notice run only once it asked', () => {
    expect(advisorShows(run('r', [], { configuredAdvisor: 'opus' }))).toBe(true);
    expect(advisorShows(run('r', [], { advisor: { calls: [] } }))).toBe(true);
    expect(advisorShows(run('r', []))).toBe(false);
    expect(advisorShows(run('r:notice:1', [], { configuredAdvisor: 'opus', noticeRun: true }))).toBe(false);
    expect(advisorShows(run('r', [], { configuredAdvisor: 'opus', provider: 'codex' }))).toBe(false);
  });

  it('shows the newest rows that fit and says how many stand before them', () => {
    expect(advisorVisible(band(1), AdvisorMinimumHeight).rows.length).toBe(1);
    const tall = advisorVisible(band(3), 28 + 24 + 3 * 38);
    expect(tall).toEqual({ rows: band(3).rows, earlier: 0 });
    const tight = advisorVisible(band(5), AdvisorMinimumHeight);
    // 140 high: room for two rows, so the newest one under the line that counts the four before it.
    expect(tight.rows.map((r) => r.id)).toEqual(['c4']);
    expect(tight.earlier).toBe(4);
    const kept = advisorVisible(band(2, { earlier: 4, calls: 6 }), 400);
    expect(kept.rows.length).toBe(2);
    expect(kept.earlier).toBe(4);
  });

  it('links each block that asked once, through the card that hides it', () => {
    const request = shownRun(run('r', [agent('scout', { status: 'running' }), agent('deep', { parentID: 'scout', status: 'running' })], {
      advisor: { calls: [{ id: 'a', state: 'done' }, { id: 'b', caller: 'deep', state: 'consulting' }, { id: 'c', state: 'done' }] }, status: 'running',
    }));
    const node = (suffix: string) => nodeId('r', suffix);
    expect(advisorCallers(request, node)).toEqual([node('execution'), node('agent:deep')]);
    const drawn = new Set([node('execution'), node('agent:scout')]);
    expect(drawnNode(node('agent:deep'), request, node, (id) => drawn.has(id))).toBe(node('agent:scout'));
    expect(drawnNode(node('agent:ghost'), request, node, (id) => drawn.has(id))).toBe(null);
  });

  it('routes a link straight when nothing stands between, else along the gap above the block\'s row', () => {
    const band = { x: -252, y: 196, w: 248, h: 500 };
    expect(advisorRoute(band, { x: 24, y: 196, w: 500, h: 280 }, [])).toEqual([{ x: -4, y: 210 }, { x: 24, y: 210 }]);
    const around = advisorRoute(band, { x: 284, y: 544, w: 236, h: 86 }, [{ x: 24, y: 544, w: 236, h: 86 }]);
    expect(around[0]).toEqual({ x: -4, y: 538 });
    expect(around[around.length - 1]).toEqual({ x: 298, y: 544 });
  });
});

describe('the session log (FlowLogTests)', () => {
  it('goes to a row\'s block, else the nearest drawn block above it, else the request\'s 실행 block', () => {
    const runs = shownRuns([run('old', []), run('r', [agent('scout', { status: 'running' }), agent('deep', { parentID: 'scout', status: 'running' })], { status: 'running' })]);
    const node = (r: MightyGraphRun, suffix: string) => nodeId(r.id, suffix);
    const drawn = new Set([nodeId('r', 'execution'), nodeId('r', 'agent:scout')]);
    expect(logTarget(nodeId('r', 'agent:scout'), runs, node, (id) => drawn.has(id))).toBe(nodeId('r', 'agent:scout'));
    expect(logTarget(nodeId('r', 'agent:deep'), runs, node, (id) => drawn.has(id))).toBe(nodeId('r', 'agent:scout'));
    expect(logTarget(nodeId('r', 'advisor'), runs, node, (id) => drawn.has(id))).toBe(null);
  });
});

describe('the header\'s chips (FlowRolesTests)', () => {
  const who: FlowRoleCopy = { name: 'subagent', ink: 'agentText', type: 'Explore', typeChip: 'Explore', marks: [{ name: 'background', help: '' }], summary: '' };

  it('shows none at the block\'s least width, the marks and the type when wide, and gives way in order as it narrows', () => {
    expect(headerFit(300, 'Map the repository layout', '실행 중', '12.3K · Haiku 5.5', 1, who)).toEqual({ typeWidth: null, showsEffort: false, showsMarks: false });
    const wide = headerFit(520, 'Map the repository layout', '실행 중', '12.3K · Haiku 5.5', 1, who);
    expect(wide.showsMarks).toBe(true);
    expect(wide.typeWidth).toBeGreaterThanOrEqual(TypeFloor);
    expect(wide.typeWidth).toBeLessThanOrEqual(TypeCeiling);
    let seenMarksAlone = false;
    let previous = headerFit(300, 'Map the repository layout', '실행 중', '12.3K · Haiku 5.5', 1, who);
    for (let width = 300; width <= 700; width += 4) {
      const fit = headerFit(width, 'Map the repository layout', '실행 중', '12.3K · Haiku 5.5', 1, who);
      if (fit.showsMarks && fit.typeWidth === null) seenMarksAlone = true;
      if (previous.showsMarks) expect(fit.showsMarks).toBe(true);
      if (previous.typeWidth !== null) expect(fit.typeWidth ?? 0).toBeGreaterThanOrEqual(previous.typeWidth);
      if (fit.typeWidth !== null) expect(fit.showsMarks).toBe(true);
      previous = fit;
    }
    expect(seenMarksAlone).toBe(true);
    expect(previous.typeWidth).not.toBe(null);
  });

  it('gives the lead\'s effort room when there is some, and a wide letter a full em', () => {
    const lead: FlowRoleCopy = { name: 'lead', ink: 'taskText', effort: 'High', marks: [], summary: '' };
    expect(headerFit(300, '실행 · 요청 12', '턴 완료 · 백그라운드 2개 실행 중', '120.4K · Opus 5.5', 1, lead).showsEffort).toBe(false);
    expect(headerFit(500, '실행 · 요청 12', '실행 중', '120.4K · Opus 5.5', 1, lead).showsEffort).toBe(true);
    expect(headerFit(900, '실행', null, null, 1, { name: 'lead', ink: 'taskText', marks: [], summary: '' })).toEqual({ typeWidth: null, showsEffort: false, showsMarks: false });
    expect(estimatedWidth('실행 중', 10)).toBeGreaterThan(estimatedWidth('abc d', 10));
    expect(estimatedWidth('aaaa', 10, true)).toBeGreaterThan(estimatedWidth('aaaa', 10));
  });
});
