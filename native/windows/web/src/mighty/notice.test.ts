// Notice runs on the page (notice.ts): the same rules as Core MightyGraphSupport and macOS BackgroundNoticeRunsTests.
import { describe, expect, it } from 'vitest';
import type { LogEntry, MightyGraphAgent, MightyGraphRun } from '../store/models';
import { followRunIndex, newestRequestIndex, newestRequestRun, quietNotice, reopenedWithReply, requestOrdinals, shownAgents, shownRuns } from './notice';
import { followTargetID } from './camera';
import { hasResult, latestResultID, makeLayout, nodeId } from './layout';
import { backgroundRowOpens } from './background';
import { StructureNavigation, newestBoardIndex } from './structureMotion';

const agent = (id: string, extra: Partial<MightyGraphAgent> = {}): MightyGraphAgent => ({ id, title: id, input: '', status: 'completed', entries: [], ...extra });
const run = (id: string, extra: Partial<MightyGraphRun> = {}): MightyGraphRun => ({ id, input: id, status: 'completed', rootEntries: [], agents: [], resultEntries: [], ...extra });
const result = (id: string, text: string): LogEntry[] => [{ id: id + '-result', kind: 'assistant', text, timestamp: '2026-10-28T00:00:00.000Z' }];

/** A request whose background command still runs, a notice run taken into its turn (no reply), and one with a reply. */
function runs(answered = true, requestStatus = 'completed'): MightyGraphRun[] {
  const task = agent('t', { title: 'Build', input: 'npm run build', status: 'running', kind: 'task', background: true });
  const copy = { ...task, status: 'completed', sourceBlockId: 't' };
  return [
    run('r', { status: requestStatus, agents: [agent('fg'), task], finalOutput: requestStatus === 'completed' ? 'Started.' : undefined, resultEntries: requestStatus === 'completed' ? result('r', 'Started.') : [] }),
    run('r:notice:1', { input: 'Build · 완료', agents: [{ ...copy, id: 'r:notice:1:t' }], noticeRun: true }),
    run('r:notice:2', { input: 'Build · 완료', status: answered ? 'completed' : 'running', agents: [{ ...copy, id: 'r:notice:2:t' }], noticeRun: true,
      finalOutput: answered ? 'The build passed.' : undefined, resultEntries: answered ? result('r:notice:2', 'The build passed.') : [] }),
  ];
}

describe('notice runs', () => {
  it('show background blocks only inside a notice run, with the blocks under them', () => {
    const agents = [agent('fg'), agent('bg', { kind: 'task', background: true }), agent('bg-child', { parentID: 'bg' }), agent('a', { background: true })];
    expect(shownAgents(run('r', { agents })).map((a) => a.id)).toEqual(['fg']);
    expect(shownAgents(run('r:notice:1', { agents, noticeRun: true })).map((a) => a.id)).toEqual(['fg', 'bg', 'bg-child', 'a']);
    const plain = [run('p', { agents: [agent('fg')] })];
    expect(shownRuns(plain)).toBe(plain);
  });

  it('name the newest request, the followed run and the request numbers', () => {
    const all = runs();
    expect(newestRequestIndex(all)).toBe(0);
    expect(newestRequestRun(all)?.id).toBe('r');
    expect([quietNotice(all[0]!), quietNotice(all[1]!), quietNotice(all[2]!)]).toEqual([false, true, false]);
    expect(followRunIndex(all)).toBe(2);
    expect(followRunIndex(runs(false))).toBe(2);
    expect(followRunIndex(all.slice(0, 2))).toBe(0);
    const next = [...all, run('n', { status: 'starting' })];
    expect(newestRequestIndex(next)).toBe(3);
    expect(followRunIndex(next)).toBe(3);
    expect(requestOrdinals(next)).toEqual([1, 1, 1, 2]);
    expect(requestOrdinals([all[1]!, all[0]!])).toEqual([1, 1]);
    expect(newestRequestIndex([all[1]!])).toBeNull();
  });

  it('give a request its result while its background work runs, and a quiet notice run none', () => {
    const all = runs();
    expect(hasResult(all[0]!)).toBe(true);
    const layout = makeLayout(all, '', false, new Set(), { splitsExecution: true });
    const ids = new Set(layout.nodes.map((n) => n.id));
    expect(ids.has(nodeId('r', 'result'))).toBe(true);
    expect(ids.has(nodeId('r', 'agent:t'))).toBe(false);
    expect(ids.has(nodeId('r:notice:1', 'result'))).toBe(false);
    expect(ids.has(nodeId('r:notice:1', 'agent:r:notice:1:t'))).toBe(true);
    expect(ids.has(nodeId('r:notice:2', 'result'))).toBe(true);
    expect(latestResultID(all)).toBe(nodeId('r:notice:2', 'result'));
    expect(latestResultID(all.slice(0, 2))).toBe(nodeId('r', 'result'));
  });

  it('let a following camera move on to an answered notice run but not to a quiet one', () => {
    const all = runs();
    expect(followTargetID(makeLayout(all, '', false, new Set(), { splitsExecution: true }), all)).toBe(nodeId('r:notice:2', 'result'));
    const running = runs(true, 'running').slice(0, 2);
    expect(followTargetID(makeLayout(running, '', true, new Set(), { splitsExecution: true }), running)).toBe(nodeId('r', 'agent:fg'));
  });

  it('keep a failed notice turn its card and a reopened one its earlier reply', () => {
    const failed = run('r:notice:1', { status: 'error', noticeRun: true });
    expect(quietNotice(failed)).toBe(false);
    const reopened = run('r:notice:1', { status: 'running', finalOutput: 'The build passed.', noticeRun: true });
    expect(reopenedWithReply(reopened) && hasResult(reopened)).toBe(true);
    expect(hasResult(run('r:notice:2', { status: 'running', noticeRun: true }))).toBe(false);
  });

  it('never move the pipeline on to a quiet board by itself', () => {
    const boards = [{ id: 'r', runIds: ['r'] }, { id: 'r:notice:1', runIds: ['r:notice:1'], quiet: true }];
    expect(newestBoardIndex(boards)).toBe(0);
    const navigation = new StructureNavigation();
    expect(navigation.index(boards)).toBe(0);
    expect(navigation.onNewest(boards)).toBe(true);
    navigation.step(1, boards);
    expect(navigation.index(boards)).toBe(1);
    expect(navigation.onNewest(boards)).toBe(false);
    navigation.showNewest(boards);
    expect(navigation.index(boards)).toBe(0);
  });
});

describe('background strip rows', () => {
  it('open when they have a command or output lines', () => {
    expect(backgroundRowOpens({ command: 'npm run build' })).toBe(true);
    expect(backgroundRowOpens({ tailLines: ['done'] })).toBe(true);
    expect(backgroundRowOpens({ tailLines: [] })).toBe(false);
    expect(backgroundRowOpens({})).toBe(false);
  });
});
