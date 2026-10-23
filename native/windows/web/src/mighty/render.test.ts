// What a streamed graph_run costs the diagram: a block draws again only when one of its props changes (Block is memoized),
// so the blocks whose frame or parts differ between two passes are the blocks that draw.
import { describe, expect, it } from 'vitest';
import type { MightyGraphRun } from '../store/models';
import { blockExtras } from './extras';
import { makeLayout, type Layout } from './layout';
import { shareRuns } from './share';
import type { MightyInputs } from './types';

const at = '2026-10-24T00:00:00Z';
function fixture(): MightyGraphRun[] {
  const finished = (i: number): MightyGraphRun => ({
    id: 'run-' + i, input: 'Request ' + i, status: 'completed', finalOutput: 'Answer ' + i,
    rootEntries: [{ id: `r${i}-u`, kind: 'user', text: 'Request', timestamp: at }, { id: `r${i}-a`, kind: 'assistant', text: 'Working', timestamp: at }],
    resultEntries: [{ id: `r${i}-res`, kind: 'assistant', text: 'Answer ' + i, timestamp: at }],
    agents: [{ id: 'a', title: 'Reader', input: 'Read', status: 'completed', entries: [{ id: `r${i}-a1`, kind: 'assistant', text: 'read', timestamp: at }] }],
  });
  return [finished(1), finished(2), finished(3), {
    id: 'run-4', input: 'Request 4', status: 'running', resultEntries: [],
    rootEntries: [{ id: 'r4-a', kind: 'assistant', text: 'Planning', timestamp: at }],
    agents: [
      { id: 'a', title: 'Reader', input: 'Read', status: 'completed', entries: [{ id: 'r4-a1', kind: 'assistant', text: 'read', timestamp: at }] },
      { id: 'b', title: 'Writer', input: 'Write', status: 'running', entries: [{ id: 'r4-b1', kind: 'assistant', text: 'writing', timestamp: at }] },
    ],
  }];
}

function pass(runs: readonly MightyGraphRun[], previous: ReadonlyMap<string, ReturnType<typeof blockExtras> extends Map<string, infer E> ? E : never>, inputs: MightyInputs | null = null) {
  const layout = makeLayout(runs, '', true, new Set(), { splitsExecution: true, viewport: { w: 1200, h: 800 }, zoom: 1 });
  const extras = blockExtras({ layout, runs, inputs, draft: '', planHistory: undefined, resultSize: undefined, planText: undefined, latest: 'x' }, previous);
  return { layout, extras };
}

function drawn(a: { layout: Layout; extras: Map<string, unknown> }, b: { layout: Layout; extras: Map<string, unknown> }): string[] {
  const before = new Map(a.layout.nodes.map((n) => [n.id, n.frame]));
  return b.layout.nodes.filter((n) => {
    const f = before.get(n.id);
    return !f || f.x !== n.frame.x || f.y !== n.frame.y || f.w !== n.frame.w || f.h !== n.frame.h || a.extras.get(n.id) !== b.extras.get(n.id);
  }).map((n) => n.id);
}

describe('a streamed graph_run', () => {
  it('draws only the block whose transcript changed', () => {
    const first = shareRuns([], fixture());
    const one = pass(first, new Map());
    // The host sends the running run again, whole (a new object for every list), with one more word in the writer's entry.
    const streamed = JSON.parse(JSON.stringify(fixture())) as MightyGraphRun[];
    streamed[3]!.agents[1]!.entries[0]!.text += ' more';
    const second = shareRuns(first, streamed);
    const two = pass(second, one.extras);
    expect(second.slice(0, 3).every((r, i) => r === first[i])).toBe(true);
    expect(drawn(one, two)).toEqual(['5:run-4:agent:b']);
    expect(two.layout.nodes.length).toBe(3 * 4 + 4);
  });

  it('draws only the new block when a sub-agent starts', () => {
    const first = shareRuns([], fixture());
    const one = pass(first, new Map());
    const streamed = JSON.parse(JSON.stringify(fixture())) as MightyGraphRun[];
    streamed[3]!.agents.push({ id: 'c', title: 'Checker', input: 'Check', status: 'running', entries: [] });
    const two = pass(shareRuns(first, streamed), one.extras);
    // Siblings share a row: the two before it move aside to keep the row centred; nothing above them moves.
    expect(drawn(one, two).sort()).toEqual(['5:run-4:agent:a', '5:run-4:agent:b', '5:run-4:agent:c']);
  });

  it('keeps the settled results when the host sends its inputs again', () => {
    // The host's inputs, sent again whole (any part changed): every completed request's file list is a new, equal array.
    const inputs = (): MightyInputs => JSON.parse(JSON.stringify({
      planRecords: [], galleries: [], executions: [], files: { 'run-1': [], 'run-2': [], 'run-3': [{ path: 'src/a.ts' }] }, older: 0, retainedStart: 0,
      summary: 'Requests 4', summaryHelp: '', executionCards: {}, animations: true, header: 'Mighty',
    }));
    const first = shareRuns([], fixture());
    const one = pass(first, new Map(), inputs());
    const streamed = JSON.parse(JSON.stringify(fixture())) as MightyGraphRun[];
    streamed[3]!.agents[1]!.entries[0]!.text += ' more';
    const two = pass(shareRuns(first, streamed), one.extras, { ...inputs(), summary: 'Requests 4 · 1.2k' });
    expect(drawn(one, two)).toEqual(['5:run-4:agent:b']);
  });
});
