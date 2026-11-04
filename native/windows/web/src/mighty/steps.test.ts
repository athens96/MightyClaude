// The 진행 단계 panel's rules (steps.ts, .omc/plans/progress-steps.md): the same as the Mac's diagram panel.
import { describe, expect, it } from 'vitest';
import type { MightyGraphRun, RunSession, TodoItem } from '../store/models';
import { stepsModel } from './steps';

const run = (id: string, extra: Partial<MightyGraphRun> = {}): MightyGraphRun => ({ id, input: id, status: 'completed', rootEntries: [], agents: [], resultEntries: [], ...extra });
const items: TodoItem[] = [
  { content: 'Implement the app', status: 'completed' },
  { content: 'Build the app', status: 'in_progress', activeForm: 'Building the app' },
  { content: 'Deploy the app', status: 'pending' },
];
const pane = (extra: Partial<RunSession> = {}): RunSession => ({
  id: 'p', workspaceId: 'w', title: 'Claude', kind: 'claude', status: 'running', model: 'default', provider: 'claude',
  settings: { effort: 'default', permissionMode: 'manual' }, logs: [], createdAt: '2026-11-04T00:00:00.000Z', draft: '',
  todoProgress: { items, runId: 'r2' }, ...extra,
} as RunSession);
const runs = [run('r1'), run('r2', { status: 'running' })];

describe('steps panel', () => {
  it('draws the newest request’s list with each step’s state', () => {
    const model = stepsModel(pane(), runs)!;
    expect(model).toMatchObject({ done: 1, total: 3, running: true });
    expect(model.rows).toEqual([
      { text: 'Implement the app', state: 'completed' },
      { text: 'Building the app', state: 'inProgress' },
      { text: 'Deploy the app', state: 'pending' },
    ]);
  });

  it('reads the current step’s content once the pane is not running', () => {
    expect(stepsModel(pane({ status: 'completed' }), runs)!.rows[1]).toEqual({ text: 'Build the app', state: 'inProgress' });
    expect(stepsModel(pane({ status: 'completed' }), runs)!.running).toBe(false);
  });

  it('draws for Codex, never for Antigravity or a shell', () => {
    expect(stepsModel(pane({ provider: 'codex' }), runs)).not.toBeNull();
    expect(stepsModel(pane({ provider: 'gemini' }), runs)).toBeNull();
    expect(stepsModel(pane({ kind: 'shell' }), runs)).toBeNull();
  });

  it('draws nothing over a request the list does not belong to', () => {
    expect(stepsModel(pane({ todoProgress: { items, runId: 'r1' } }), runs)).toBeNull();
    expect(stepsModel(pane({ todoProgress: { items } }), runs)).toBeNull();
    expect(stepsModel(pane(), [...runs, run('r3')])).toBeNull();
    expect(stepsModel(pane(), [])).toBeNull();
  });

  it('a notice run is not a new request', () => {
    expect(stepsModel(pane(), [...runs, run('r2:notice:1', { noticeRun: true })])).not.toBeNull();
  });

  it('draws nothing for an empty list, and reads an unknown state as pending', () => {
    expect(stepsModel(pane({ todoProgress: { items: [], runId: 'r2' } }), runs)).toBeNull();
    expect(stepsModel(pane({ todoProgress: { items: [{ content: 'x', status: 'weird' }], runId: 'r2' } }), runs)!.rows[0]!.state).toBe('pending');
  });
});
