import { describe, expect, it } from 'vitest';
import type { MightyGraphRun } from '../store/models';
import { applyMighty } from './types';
import { shareRuns } from './share';

const run = (id: string, status: string, text: string): MightyGraphRun => JSON.parse(JSON.stringify({
  id, input: 'go', status, resultEntries: [],
  rootEntries: [{ id: id + '-e', kind: 'assistant', text: 'root', timestamp: 't' }],
  agents: [
    { id: 'a', title: 'A', input: 'x', status: 'completed', entries: [{ id: 'a1', kind: 'assistant', text: 'done', timestamp: 't' }] },
    { id: 'b', title: 'B', input: 'y', status, entries: [{ id: 'b1', kind: 'assistant', text, timestamp: 't' }] },
  ],
}));

describe('run sharing', () => {
  it('keeps every unchanged list and agent of a replaced run', () => {
    const first = shareRuns([], [run('r0', 'completed', 'x'), run('r1', 'running', 'one')]);
    const next = shareRuns(first, [run('r0', 'completed', 'x'), run('r1', 'running', 'one two')]);
    expect(next[0]).toBe(first[0]);
    expect(next[1]).not.toBe(first[1]);
    expect(next[1]!.rootEntries).toBe(first[1]!.rootEntries);
    expect(next[1]!.agents[0]).toBe(first[1]!.agents[0]);
    expect(next[1]!.agents[1]).not.toBe(first[1]!.agents[1]);
    expect(shareRuns(next, next.map((r) => JSON.parse(JSON.stringify(r))))).toBe(next);
  });
});

describe('mighty pushes', () => {
  it('replaces blocks by id and keeps the others', () => {
    const block = (title: string) => ({ kind: 'work', title, state: '', help: '', indicator: 'none', outline: 'none', status: '', tone: 'idle', fromRecord: false });
    const a = applyMighty(undefined, { kind: 'mighty', paneId: 'p', blocks: { x: block('x'), y: block('y') } });
    const b = applyMighty(a, { kind: 'mighty', paneId: 'p', blocks: { y: block('y2') } });
    expect(b.blocks.x).toBe(a.blocks.x);
    expect(b.blocks.y?.title).toBe('y2');
    expect(applyMighty(b, { kind: 'mighty', paneId: 'p', removed: ['x'] }).blocks.x).toBeUndefined();
  });
});
