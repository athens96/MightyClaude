import { describe, expect, it } from 'vitest';
import { blockExtras } from './extras';
import type { Layout } from './layout';
import type { MightyGraphRun, PlanRecord } from '../store/models';
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

  it('retains the complete answered plan and redraws when its decision details change without its text changing', () => {
    const id = '2:r1:plan-record:plan1';
    const planLayout = { ...layout, nodes: [{ id, kind: 'planRecord', frame: { x: 0, y: 0, w: 320, h: 104 } }] } as Layout;
    const record: PlanRecord = { id: 'plan1', runId: 'r1', plan: '# Plan', receivedAt: '2026-10-29T10:00:00Z', decidedAt: '2026-10-29T10:05:00Z', outcome: 'revised', feedback: '다시 검토', planTruncated: true };
    const input = { ...base, layout: planLayout, inputs: null, planHistory: [record] };
    const first = blockExtras(input, new Map());
    expect(first.get(id)?.planRecord).toEqual(record);
    // A new snapshot with identical records does not redraw settled graph blocks.
    const same = blockExtras({ ...input, planHistory: [{ ...record }] }, first);
    expect(same.get(id)).toBe(first.get(id));
    const changed = blockExtras({ ...input, planHistory: [{ ...record, feedback: '테스트 먼저 추가', planTruncated: false }] }, same);
    expect(changed.get(id)).not.toBe(same.get(id));
    expect(changed.get(id)?.planRecord).toMatchObject({ feedback: '테스트 먼저 추가', planTruncated: false, plan: '# Plan' });
  });
});
