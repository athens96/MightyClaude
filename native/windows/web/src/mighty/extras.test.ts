import { describe, expect, it } from 'vitest';
import { blockExtras } from './extras';
import type { Layout } from './layout';
import type { MightyGraphRun } from '../store/models';
import type { MightyInputs } from './types';

// A settled result drawn before its file lookup came back must keep its parts when the lookup finds nothing.
describe('blockExtras', () => {
  const run = { id: 'r1', input: 'q', status: 'completed', rootEntries: [], agents: [], resultEntries: [], finalOutput: 'a' } as unknown as MightyGraphRun;
  const layout = { nodes: [{ id: '2:r1:result', kind: 'result', frame: { x: 0, y: 0, w: 500, h: 200 } }], fittedResultID: null } as unknown as Layout;
  const base = { layout, runs: [run], draft: '', planHistory: undefined, resultSize: undefined, planText: undefined, latest: null };

  it('treats an empty file list as no list, keeping the block parts', () => {
    const first = blockExtras({ ...base, inputs: null }, new Map());
    const second = blockExtras({ ...base, inputs: { files: { r1: [] }, galleries: [], executionCards: {} } as unknown as MightyInputs }, first);
    expect(second.get('2:r1:result')).toBe(first.get('2:r1:result'));
  });

  it('draws again when files arrive', () => {
    const first = blockExtras({ ...base, inputs: null }, new Map());
    const second = blockExtras({ ...base, inputs: { files: { r1: [{ path: 'a.ts' }] }, galleries: [], executionCards: {} } as unknown as MightyInputs }, first);
    expect(second.get('2:r1:result')).not.toBe(first.get('2:r1:result'));
  });
});
