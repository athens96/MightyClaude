// The diagram's 진행 단계 panel (.omc/plans/progress-steps.md): which pane shows the agent's checklist, and how each step reads.
// The same rules as the Mac's diagram panel.
import type { MightyGraphRun, RunSession } from '../store/models';
import { newestRequestRun } from './notice';

export type StepState = 'completed' | 'inProgress' | 'pending';

export interface StepRow { text: string; state: StepState }

export interface StepsModel { done: number; total: number; rows: StepRow[]; running: boolean }

const stateOf = (status: string): StepState => (status === 'completed' ? 'completed' : status === 'in_progress' ? 'inProgress' : 'pending');

/**
 * The panel for a pane, or null when nothing is drawn: only a Claude or Codex agent pane (Antigravity keeps no checklist),
 * only a list that has steps, and only while that list belongs to the pane's newest request (its `runId` is that request's
 * graph run), so an older request's list never sits over a new one. `runs` are the diagram's runs, a sent request's stand-in
 * included. The step being worked on reads its activeForm while the pane runs.
 */
export function stepsModel(session: RunSession, runs: readonly MightyGraphRun[]): StepsModel | null {
  if (session.kind !== 'claude' || (session.provider !== 'claude' && session.provider !== 'codex')) return null;
  const progress = session.todoProgress;
  const items = progress?.items ?? [];
  if (items.length === 0 || !progress?.runId || progress.runId !== newestRequestRun(runs)?.id) return null;
  const running = session.status === 'running';
  const rows = items.map((item): StepRow => {
    const state = stateOf(item.status);
    return { state, text: state === 'inProgress' && running ? item.activeForm ?? item.content : item.content };
  });
  return { done: rows.filter((row) => row.state === 'completed').length, total: rows.length, rows, running };
}
