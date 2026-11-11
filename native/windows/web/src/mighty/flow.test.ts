// The role view on the page (flow.ts, the fan-out of layout.ts), checked against the shared flow vectors the Windows Core asserts too
// (native/contracts/fixtures/flow-vectors.json, FlowVectorVerification.cs) and against the cases of the macOS FlowCardTests, FlowLinesTests,
// FlowStepsTests, FlowLogTests and FlowRolesTests that the page decides itself.
import { describe, expect, it } from 'vitest';
import vectors from '@contracts/fixtures/flow-vectors.json';
import ko from '@locales/ko.json';
import { translator } from '../i18n/locale';
import type { FlowStep, LogEntry, MightyGraphAgent, MightyGraphRun } from '../store/models';
import { makeLayout, nodeId } from './layout';
import { followTargetID } from './camera';
import { shownRun, shownRuns } from './notice';
import {
  cardFigures, CardHeight, delegationChildren, drawnNode, elapsed, estimatedWidth, flowAgents, flowOrder, foldsToCard, headerFit, isCompact, liveFlowIds,
  focusedRequest, launchedInStep, logTarget, progressRequest, requestSpans, SideGap, stableHash, StepCardHeight, stepEntries, stepIndex, stepSlot, stepSuffix, TypeCeiling,
  TypeFloor, WideCardWidth,
} from './flow';
import { edgePath, edgeRoute } from './Diagram';
import type { FlowCardCopy, FlowRoleCopy } from './types';

const t = translator([ko as Record<string, string>]);
const agent = (id: string, extra: Partial<MightyGraphAgent> = {}): MightyGraphAgent => ({ id, title: id, input: '확인', status: 'completed', entries: [], ...extra });
const run = (id: string, agents: MightyGraphAgent[], extra: Partial<MightyGraphRun> = {}): MightyGraphRun =>
  ({ id, input: '요청 ' + id, status: 'completed', rootEntries: [], agents, resultEntries: [], provider: 'claude', ...extra });

describe('the shared flow vectors', () => {
  type Case = { name: string; running: boolean; opened: string[]; runs: MightyGraphRun[]; expect: { nodes: { id: string; kind: string; x: number; y: number; w: number; h: number; compact?: boolean }[]; edges: { source: string; target: string; joins: boolean; side?: true }[]; size: { w: number; h: number }; originX: number; originY: number } };
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
    // Every other view keeps the 실행 block right under the request.
    expect(plain.edges).toContainEqual({ source: nodeId('r', 'request'), target: nodeId('r', 'execution'), joins: false });
  });
});

describe('cards (FlowCardTests)', () => {
  it('folds every sub-agent and command, running or ended, a notice run\'s copy too; never a steer', () => {
    expect(foldsToCard(agent('fg', { status: 'running' }))).toBe(true);
    expect(foldsToCard(agent('fg'))).toBe(true);
    expect(foldsToCard(agent('bg', { background: true }))).toBe(true);
    expect(foldsToCard(agent('task', { kind: 'task', background: true }))).toBe(true);
    expect(foldsToCard(agent('copy', { background: true, sourceBlockId: 'bg' }))).toBe(true);
    for (const kind of ['steer', 'compact', 'question']) expect(foldsToCard(agent(kind, { kind, status: 'running' }))).toBe(false);
    expect(isCompact(agent('fg', { status: 'running' }), true)).toBe(false);
    expect(isCompact(agent('fg'), false)).toBe(true);
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
  });

  it('stands the main session\'s card where the answer will come, under the row, until the result takes its place (MightyGraphFollow)', () => {
    const request = run('r', [agent('fg', { status: 'running' })], { status: 'running' });
    const layout = makeLayout([request], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    const work = layout.nodes.find((n) => n.id === nodeId('r', 'execution'))!;
    const card = layout.nodes.find((n) => n.id === nodeId('r', 'agent:fg'))!;
    expect(work).toMatchObject({ kind: 'work', compact: true, frame: { w: WideCardWidth, h: CardHeight } });
    expect(work.frame.y).toBe(card.frame.y + card.frame.h + 32);
    expect(layout.edges).toContainEqual({ source: card.id, target: work.id, joins: true });
    // The line hangs from the request; no return node.
    expect(layout.edges).toContainEqual({ source: nodeId('r', 'request'), target: nodeId('r', 'delegate:main'), joins: false });
    expect(layout.nodes.some((n) => n.kind === 'return')).toBe(false);
    expect(followTargetID(layout, [request])).toBe(work.id);
    const done = run('r', [agent('fg')], { finalOutput: '결과' });
    const ended = makeLayout([done], '', false, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    expect(ended.nodes.some((n) => n.kind === 'work')).toBe(false);
    expect(followTargetID(ended, [done])).toBe(nodeId('r', 'result'));
    // Opened, it is the 실행 block in that place.
    const opened = makeLayout([request], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set([work.id]) } });
    expect(opened.nodes.find((n) => n.id === work.id)).toMatchObject({ frame: { w: 500, h: 280 } });
    expect(opened.nodes.find((n) => n.id === work.id)?.compact).toBeUndefined();
  });

  it('makes the edges into running cards and their line live', () => {
    const request = run('r', [agent('notes', { background: true }), agent('ci', { kind: 'task', background: true, status: 'running' })], { status: 'running' });
    const layout = makeLayout([request], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    const ci = nodeId('r', 'agent:ci'), notes = nodeId('r', 'agent:notes');
    const live = liveFlowIds(layout, new Set([ci]));
    expect(live.has(ci)).toBe(true);
    expect(live.has(notes)).toBe(false);
    expect(live.has(nodeId('r', 'delegate:main'))).toBe(true);
  });
});

describe('a request\'s steps (FlowStepsTests)', () => {
  const at = (s: number) => new Date(Date.UTC(2027, 0, 15, 8, 0, 0) + s * 1000).toISOString();
  const ms = (s: number) => Date.parse(at(s));
  const tool = (id: string, seconds: number): LogEntry => ({ id, kind: 'system', text: id, timestamp: at(seconds) } as LogEntry);

  it('names a step by a hash of its key as the Mac does, never by its words', () => {
    expect(stepSuffix({ key: 'text:점검' })).toBe('step:818301147174f5bc');
    expect(stableHash('')).toBe('cbf29ce484222325');
  });

  it('gives a record to the step whose span holds it, the later where two meet, a start from its whole second', () => {
    const steps: FlowStep[] = [
      { key: 'a', content: 'a', status: 'completed', startedAt: at(10), endedAt: at(20) },
      { key: 'b', content: 'b', status: 'in_progress', startedAt: new Date(ms(20) + 400).toISOString() },
      { key: 'c', content: 'c', status: 'pending' },
    ];
    expect(stepIndex(ms(5), steps)).toBe(null);
    expect(stepIndex(ms(10), steps)).toBe(0);
    expect(stepIndex(ms(15), steps)).toBe(0);
    expect(stepIndex(ms(20), steps)).toBe(1);
    expect(stepIndex(ms(99), steps)).toBe(1);
    expect(stepSlot(agent('x', { figures: { startedAt: at(12) } }), steps)).toBe(1);
    expect(stepSlot(agent('x', { figures: { startedAt: at(1) } }), steps)).toBe(0);
    expect(stepSlot(agent('x'), steps)).toBe(0);
    expect(stepEntries(0, steps, [tool('e1', 11), tool('e2', 25), tool('e3', 3)]).map((e) => e.id)).toEqual(['e1']);
  });

  it('runs the spine through the steps that began, with what each launched beside it, and a card beside a step one line taller', () => {
    const request = run('s', [agent('early', { figures: { startedAt: at(-5) } }), agent('notes', { status: 'running', background: true, figures: { startedAt: at(12) } })], {
      status: 'running',
      steps: [{ key: 'a', content: '점검', status: 'completed', startedAt: at(0), endedAt: at(10) }, { key: 'b', content: '노트', status: 'in_progress', startedAt: at(10) },
        { key: 'c', content: 'CI', status: 'pending' }],
    });
    const layout = makeLayout([request], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    const find = (suffix: string) => layout.nodes.find((n) => n.id === nodeId('s', suffix))!;
    const main = find('request'), first = find(stepSuffix({ key: 'a' })), second = find(stepSuffix({ key: 'b' })), work = find('execution');
    expect(layout.nodes.some((n) => n.id === nodeId('s', stepSuffix({ key: 'c' })))).toBe(false);
    for (const card of [first, second, work]) expect(card).toMatchObject({ compact: true, frame: { w: WideCardWidth, h: CardHeight } });
    expect(first.frame.y).toBe(main.frame.y + main.frame.h + 32);
    expect(find('agent:early').frame).toMatchObject({ x: main.frame.x + main.frame.w + SideGap, y: main.frame.y });
    expect(find('agent:notes').frame).toMatchObject({ x: second.frame.x + second.frame.w + SideGap, y: second.frame.y });
    expect(layout.edges).toContainEqual({ source: second.id, target: nodeId('s', 'agent:notes'), joins: false, side: true });
    expect(layout.edges).toContainEqual({ source: second.id, target: work.id, joins: true });
    expect(layout.nodes.some((n) => n.kind === 'delegate')).toBe(false);
    // The plan is no block of the diagram (the progress panel shows it); a card launched in a step has the step's line.
    expect(layout.nodes.some((n) => n.kind === 'outline')).toBe(false);
    expect(find('agent:notes').frame).toMatchObject({ w: 236, h: StepCardHeight });
    expect(find('agent:early').frame).toMatchObject({ w: 236, h: CardHeight });
    expect(launchedInStep(request.agents[1]!, request, request.agents)).toBe(true);
    expect(launchedInStep(request.agents[0]!, request, request.agents)).toBe(false);
    for (const [i, a] of layout.nodes.entries()) for (const b of layout.nodes.slice(i + 1)) {
      expect(a.frame.x < b.frame.x + b.frame.w && b.frame.x < a.frame.x + a.frame.w && a.frame.y < b.frame.y + b.frame.h && b.frame.y < a.frame.y + a.frame.h, a.id + ' × ' + b.id).toBe(false);
    }
    // A notice run draws no steps.
    const notice = makeLayout([{ ...request, id: 's:notice:1', noticeRun: true }], '', true, new Set(), { splitsExecution: true, fanOut: { opened: new Set() } });
    expect(notice.nodes.some((n) => n.kind === 'step')).toBe(false);
  });

  it('reads the request in view: the one whose rows cross a third of the view, else the nearest, a notice run its request\'s', () => {
    const runs = [run('a', []), { ...run('a:notice:1', []), noticeRun: true }, run('b', [])];
    const nodes = [
      { id: nodeId('a', 'request'), frame: { y: 0, h: 140 } }, { id: nodeId('a', 'result'), frame: { y: 172, h: 200 } },
      { id: nodeId('a:notice:1', 'request'), frame: { y: 404, h: 140 } },
      { id: nodeId('b', 'request'), frame: { y: 600, h: 140 } }, { id: nodeId('b', 'execution'), frame: { y: 772, h: 86 } },
    ];
    const prefixes = runs.map((r, index) => ({ prefix: nodeId(r.id, ''), index })).sort((x, y) => y.prefix.length - x.prefix.length);
    const spans = requestSpans(nodes, runs, (id) => prefixes.find((p) => id.startsWith(p.prefix))?.index ?? null);
    expect(spans).toEqual([{ id: 'a', top: 0, bottom: 544 }, { id: 'b', top: 600, bottom: 858 }]);
    expect(focusedRequest(spans, 0, 600)).toBe('a');
    expect(focusedRequest(spans, 500, 600)).toBe('b');
    // Between two: the nearest.
    expect(focusedRequest(spans, 400, 540)).toBe('b');
    expect(focusedRequest([], 0, 600)).toBe(null);
  });

  it('shows a new request it was pinned to until the user scrolls, else the request in view, else the newest', () => {
    const has = (id: string) => id !== 'gone';
    expect(progressRequest('b', 'a', 'b', has)).toBe('b');
    expect(progressRequest(null, 'a', 'b', has)).toBe('a');
    expect(progressRequest(null, null, 'b', has)).toBe('b');
    expect(progressRequest('gone', 'a', 'b', has)).toBe('a');
    expect(progressRequest(null, null, null, has)).toBe(null);
  });

  it('routes a side edge out of the trailing edge into the leading edge, its arrow head pointing across', () => {
    const from = { x: 0, y: 0, w: 500, h: 86 }, to = { x: 544, y: 0, w: 236, h: 86 };
    const route = edgeRoute(from, to, false, true);
    expect(route[0]).toEqual({ x: 500, y: 43 });
    expect(route[route.length - 1]).toEqual({ x: 544, y: 43 });
    expect(route[1]!.x).toBe(522);
    expect(edgePath(route)).toContain('M538 47L544 43L538 39');
    // A downward edge keeps its arrow pointing down.
    expect(edgePath(edgeRoute({ x: 0, y: 0, w: 100, h: 100 }, { x: 0, y: 200, w: 100, h: 100 }, false))).toContain('M46 194L50 200L54 194');
  });
});

describe('the session log (FlowLogTests)', () => {
  it('goes to a row\'s block, else the nearest drawn block above it, else the request\'s 실행 block', () => {
    const runs = shownRuns([run('old', []), run('r', [agent('scout', { status: 'running' }), agent('deep', { parentID: 'scout', status: 'running' })], { status: 'running' })]);
    const node = (r: MightyGraphRun, suffix: string) => nodeId(r.id, suffix);
    const drawn = new Set([nodeId('r', 'execution'), nodeId('r', 'agent:scout')]);
    expect(logTarget(nodeId('r', 'agent:scout'), runs, node, (id) => drawn.has(id))).toBe(nodeId('r', 'agent:scout'));
    expect(logTarget(nodeId('r', 'agent:deep'), runs, node, (id) => drawn.has(id))).toBe(nodeId('r', 'agent:scout'));
    expect(logTarget(nodeId('r', 'agent:ghost'), runs, node, (id) => drawn.has(id))).toBe(null);
    // The main session's rows go to its result once the 실행 card is gone.
    const done = shownRuns([run('d', [], { finalOutput: '끝' })]);
    expect(logTarget(nodeId('d', 'execution'), done, node, (id) => id === nodeId('d', 'result'))).toBe(nodeId('d', 'result'));
    expect(drawnNode(nodeId('r', 'agent:deep'), runs[1]!, (suffix) => nodeId('r', suffix), (id) => drawn.has(id))).toBe(nodeId('r', 'agent:scout'));
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
