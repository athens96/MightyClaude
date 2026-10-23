import { describe, expect, it } from 'vitest';
import { devSnapshot } from '../bridge/devHost';
import { applyListDelta, applySessionDelta, diffList, diffSession } from './delta';
import type { LogEntry, MightyGraphRun, RunSession } from './models';

const entry = (id: string, text = 'text ' + id): LogEntry => ({ id, kind: 'assistant', text, timestamp: '2026-10-24T00:00:00Z' });
const entries = (count: number, from = 0) => Array.from({ length: count }, (_, index) => entry('e' + (index + from)));
const run = (id: string, status = 'running'): MightyGraphRun => ({ id, input: 'ask', status, rootEntries: [], agents: [], resultEntries: [] });

/** The round trip the host and the page make: the delta from a to b, applied to a, must be b. */
function roundTrip<T extends { id: string }>(previous: T[], next: T[]) {
  const delta = diffList(previous, next);
  const applied = applyListDelta(previous, delta);
  expect(applied).toEqual(next);
  return { delta, applied };
}

describe('diffList / applyListDelta', () => {
  it('says nothing when nothing changed and keeps the array', () => {
    const list = entries(3);
    const delta = diffList(list, list.slice());
    expect(delta).toEqual({});
    expect(applyListDelta(list, delta)).toBe(list);
  });

  it('appends new records', () => {
    const list = entries(3);
    const { delta, applied } = roundTrip(list, [...list, entry('n1'), entry('n2')]);
    expect(delta).toEqual({ added: [entry('n1'), entry('n2')] });
    expect(applied[0]).toBe(list[0]);
  });

  it('replaces a record by id and keeps every other object', () => {
    const list = entries(4);
    const changed = entry('e2', 'streamed');
    const { delta, applied } = roundTrip(list, [list[0]!, list[1]!, changed, list[3]!]);
    expect(delta).toEqual({ replaced: [changed] });
    expect(applied[0]).toBe(list[0]);
    expect(applied[3]).toBe(list[3]);
    expect(applied[2]).toBe(changed);
  });

  it('drops the oldest records retention trimmed and appends in one delta', () => {
    const list = entries(5);
    const { delta } = roundTrip(list, [...list.slice(2), entry('n')]);
    expect(delta).toEqual({ drop: 2, added: [entry('n')] });
  });

  it('empties the list', () => {
    const { delta } = roundTrip(entries(3), []);
    expect(delta).toEqual({ drop: 3 });
  });

  it('removes a record inside the list through the order', () => {
    const list = entries(4);
    const { delta, applied } = roundTrip(list, [list[0]!, list[2]!, list[3]!]);
    expect(delta.order).toEqual(['e0', 'e2', 'e3']);
    expect(delta.replaced).toBeUndefined();
    expect(applied[1]).toBe(list[2]);
  });

  it('reorders through the order, with replaced and added records', () => {
    const list = entries(3);
    const changed = entry('e0', 'new words');
    const { delta } = roundTrip(list, [list[2]!, changed, entry('x'), list[1]!]);
    expect(delta.order).toEqual(['e2', 'e0', 'x', 'e1']);
    expect(delta.replaced).toEqual([changed]);
    expect(delta.added).toEqual([entry('x')]);
  });

  it('does not take a dropped record coming back for a new one', () => {
    const list = entries(3);
    const { delta } = roundTrip(list, [list[1]!, list[2]!, list[0]!]);
    expect(delta.order).toEqual(['e1', 'e2', 'e0']);
  });

  it('compares with the rule it is given', () => {
    const list = entries(2);
    const copy = list.map((record) => ({ ...record }));
    expect(diffList(list, copy)).toEqual({ replaced: copy });
    expect(diffList(list, copy, (a, b) => JSON.stringify(a) === JSON.stringify(b))).toEqual({});
  });

  it('costs one entry when the last of 5,000 streams', () => {
    const list = entries(5000);
    const streamed = entry('e4999', 'text e4999 and more words');
    const next = [...list.slice(0, -1), streamed];
    const delta = diffList(list, next);
    expect(delta).toEqual({ replaced: [streamed] });
    const size = JSON.stringify(delta).length;
    expect(size).toBeLessThan(200);
    // The same change on a 50-entry pane is the same size: the message does not grow with the conversation.
    const small = entries(50, 4950);
    expect(JSON.stringify(diffList(small, [...small.slice(0, -1), streamed])).length).toBe(size);
    const applied = applyListDelta(list, delta);
    expect(applied[4999]).toBe(streamed);
    expect(applied[0]).toBe(list[0]);
  });
});

describe('diffSession / applySessionDelta', () => {
  const base = (): RunSession => ({ ...devSnapshot().sessions[0]!, logs: entries(3) });

  it('sends a pane the page has not seen whole', () => {
    const session = base();
    expect(diffSession(undefined, session)).toEqual({ id: session.id, session });
  });

  it('sends nothing for the same record', () => {
    const session = base();
    expect(diffSession(session, session)).toBeNull();
    expect(diffSession(session, { ...session })).toBeNull();
  });

  it('sends only the fields that changed and the fields that went', () => {
    const before: RunSession = { ...base(), resumeId: 'r1' };
    const { resumeId: _gone, ...rest } = before;
    const after: RunSession = { ...rest, status: 'running' };
    const delta = diffSession(before, after)!;
    expect(delta).toEqual({ id: before.id, set: { status: 'running' }, unset: ['resumeId'] });
    const applied = applySessionDelta(before, delta)!;
    expect(applied).toEqual(after);
    expect(applied.logs).toBe(before.logs);
  });

  it('streams the logs as list changes, not the pane', () => {
    const before = base();
    const after: RunSession = { ...before, logs: [...before.logs.slice(0, -1), entry('e2', 'more'), entry('e3')] };
    const delta = diffSession(before, after)!;
    expect(delta).toEqual({ id: before.id, logs: { replaced: [entry('e2', 'more')], added: [entry('e3')] } });
    const applied = applySessionDelta(before, delta)!;
    expect(applied).toEqual(after);
    expect(applied.logs[0]).toBe(before.logs[0]);
  });

  it('streams graph runs by id and forgets the field when none are left', () => {
    const before: RunSession = { ...base(), graphRuns: [run('g1', 'completed'), run('g2')] };
    const finished = run('g2', 'completed');
    const after: RunSession = { ...before, graphRuns: [before.graphRuns![0]!, finished, run('g3')] };
    const delta = diffSession(before, after)!;
    expect(delta.graphRuns).toEqual({ replaced: [finished], added: [run('g3')] });
    const applied = applySessionDelta(before, delta)!;
    expect(applied.graphRuns![0]).toBe(before.graphRuns![0]);
    expect(applied).toEqual(after);
    const { graphRuns: _runs, ...none } = after;
    const cleared = applySessionDelta(applied, diffSession(applied, none)!)!;
    expect(cleared.graphRuns).toBeUndefined();
    expect(cleared).toEqual(none);
  });

  it('cannot patch a pane it does not have', () => {
    expect(applySessionDelta(undefined, { id: 'x', set: { title: 'y' } })).toBeUndefined();
  });
});
