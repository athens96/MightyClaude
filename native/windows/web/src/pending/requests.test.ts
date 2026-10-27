import { describe, expect, it } from 'vitest';
import type { LogEntry, MightyGraphRun, RunSession } from '../store/models';
import {
  StartedGrace, applyAnswer, failRequest, newPendingRequest, pendingRun, pendingRunId, reconcile, requestState, shownInConversation, shownInMighty, type PendingRequest,
} from './requests';

const at = Date.parse('2026-10-27T10:00:00Z');
const entry = (id: string, kind: LogEntry['kind'], text: string, time = at - 60_000): LogEntry => ({ id, kind, text, timestamp: new Date(time).toISOString() });
const run = (id: string, input: string): MightyGraphRun => ({ id, input, status: 'running', rootEntries: [], agents: [], resultEntries: [] });

function pane(logs: LogEntry[], graphRuns: MightyGraphRun[] = []): RunSession {
  return {
    id: 'pane', workspaceId: 'w', title: 't', kind: 'claude', status: 'idle', model: 'm', provider: 'claude',
    settings: { effort: 'high', permissionMode: 'default', maxTurns: null, maxBudgetUsd: null }, logs, createdAt: '', draft: '', graphRuns,
  };
}

/** The pane read a moment after the answer. */
const settle = (list: readonly PendingRequest[], session: RunSession | undefined) => reconcile(list, session, at + 100);

const before = pane([entry('u1', 'user', 'first'), entry('a1', 'assistant', 'answer')], [run('r1', 'first')]);
const sent = (): PendingRequest => newPendingRequest(before, 'c1', 'second request', at);

describe('pending requests', () => {
  it('remembers the pane as it was at Enter', () => {
    const request = sent();
    expect(request).toMatchObject({ clientId: 'c1', text: 'second request', at, phase: 'sending', lastUserEntryId: 'u1', knownRunIds: ['r1'] });
    expect(requestState(request)).toEqual({ kind: 'pending', since: at });
  });

  it('is replaced once the host brings the entry and run the answer named', () => {
    const started = applyAnswer([sent()], 'c1', 'started', { entryId: 'u2', runId: 'r2' }, '', at + 50);
    expect(started[0]).toMatchObject({ phase: 'started', entryId: 'u2', runId: 'r2' });
    // Only the entry so far: the diagram still waits for the run, so the request stays; the conversation draws the entry.
    const entryOnly = pane([...before.logs, entry('u2', 'user', 'second request', at)], before.graphRuns);
    expect(settle(started, entryOnly)).toBe(started);
    expect(shownInConversation(started, entryOnly)).toEqual([]);
    expect(shownInMighty(started, entryOnly)).toBe(started[0]);
    const both = pane(entryOnly.logs, [...before.graphRuns!, run('r2', 'second request')]);
    expect(settle(started, both)).toEqual([]);
    expect(shownInMighty(started, both)).toBeUndefined();
  });

  it('waits for the entry alone when the answer named no run (a shell, a remote workspace)', () => {
    const started = applyAnswer([sent()], 'c1', 'started', { entryId: 'u2', runId: null }, '', at + 50);
    expect(settle(started, pane([...before.logs, entry('u2', 'user', 'other words', at)]))).toEqual([]);
  });

  it('matches a delta that came before the answer by its words, and a start without ids by any new request', () => {
    const waiting = [sent()];
    expect(settle(waiting, before)).toBe(waiting);
    // Someone else's request (another words) does not take its place while no answer has come.
    expect(settle(waiting, pane([...before.logs, entry('u9', 'user', 'unrelated', at)]))).toBe(waiting);
    expect(settle(waiting, pane([...before.logs, entry('u2', 'user', 'second request', at)]))).toEqual([]);
    expect(settle(waiting, pane(before.logs, [...before.graphRuns!, run('r2', 'second request')]))).toEqual([]);
    // Started with held items: no ids, so any new request entry is it.
    const started = applyAnswer(waiting, 'c1', 'started', {}, '', at + 50);
    expect(settle(started, pane([...before.logs, entry('u3', 'user', 'held item first', at)]))).toEqual([]);
  });

  it('reads new entries by time when the last one it knew was trimmed away', () => {
    const request = sent();
    const trimmed = pane([entry('a1', 'assistant', 'answer'), entry('u2', 'user', 'second request', at + 10)]);
    expect(settle([request], trimmed)).toEqual([]);
    const old = pane([entry('a1', 'assistant', 'answer'), entry('u0', 'user', 'second request', at - 60_000)]);
    expect(settle([request], old)).toHaveLength(1);
  });

  it('drops the block for queued, worktree, answered, builtin, steered and nothing sent', () => {
    for (const outcome of ['queued', 'worktree', 'answered', 'builtin', 'steered', 'none', 'unknown', null]) {
      expect(applyAnswer([sent()], 'c1', outcome, {}, '', at + 10)).toEqual([]);
    }
  });

  it('turns failed in place for a start that did not happen, and keeps it until the next send', () => {
    for (const outcome of ['notStarted', 'blocked']) {
      const failed = applyAnswer([sent()], 'c1', outcome, {}, 'no CLI', at + 40);
      expect(failed[0]).toMatchObject({ phase: 'failed', message: 'no CLI', endedAt: at + 40 });
      expect(requestState(failed[0]!)).toEqual({ kind: 'failed', message: 'no CLI', since: at, at: at + 40 });
      expect(settle(failed, pane([...before.logs, entry('u2', 'user', 'second request', at)]))).toBe(failed);
      expect(shownInConversation(failed, before)).toBe(failed);
    }
    expect(failRequest([sent()], 'c1', 'bridge gone', at + 5)[0]).toMatchObject({ phase: 'failed', message: 'bridge gone' });
  });

  it('leaves the requests alone for an answer it does not know', () => {
    const list = [sent()];
    expect(applyAnswer(list, 'other', 'queued', {}, '', at)).toBe(list);
    expect(failRequest(list, 'other', 'x', at)).toBe(list);
  });

  it('stands in for the run in the diagram, apart from every host id', () => {
    const request = sent();
    expect(pendingRunId(request)).toBe('pending-c1');
    expect(pendingRun(request, 'claude')).toMatchObject({ id: 'pending-c1', input: 'second request', status: 'starting', agents: [], provider: 'claude' });
    const failed = applyAnswer([request], 'c1', 'blocked', {}, 'why', at + 1)[0]!;
    expect(pendingRun(failed, 'claude')).toMatchObject({ status: 'error', finalOutput: 'why' });
  });

  it('lets a started request go once its blocks are long overdue, so 전송 중 never stands for good', () => {
    const started = applyAnswer([sent()], 'c1', 'started', { entryId: 'never', runId: 'never-run' }, '', at + 50);
    expect(reconcile(started, before, at + 50 + StartedGrace)).toBe(started);
    expect(reconcile(started, before, at + 51 + StartedGrace)).toEqual([]);
    // A request still waiting for its answer is not timed out here: the send itself ends it.
    const waiting = [sent()];
    expect(reconcile(waiting, before, at + 10 * StartedGrace)).toBe(waiting);
  });

  it('forgets every request of a pane that is gone', () => {
    expect(settle([sent()], undefined)).toEqual([]);
  });
});
