// 바로 실행 on a queued row: what the host decided for it (Core QueueRunNow, M/QueueRunNow.swift) and how the button reads.

/** What 바로 실행 does on a queued row, as the host decided it (Core QueueRunNow, M/QueueRunNow.swift). */
export type RunNow = 'worktree' | 'agentMenu' | 'here' | 'steer' | 'held' | 'waitsForTurn' | 'waitsForHeld' | 'waitsForUpdate';
/** One row of a queued row's agent menu (`queueRunNowMenu`). */
export interface RunNowChoice { agent: string; text: string; enabled: boolean }

const kinds: readonly RunNow[] = ['worktree', 'agentMenu', 'here', 'steer', 'held', 'waitsForTurn', 'waitsForHeld', 'waitsForUpdate'];
/** The host's value, or null for anything else (no button). */
export function readRunNow(value: unknown): RunNow | null {
  return typeof value === 'string' && (kinds as readonly string[]).includes(value) ? (value as RunNow) : null;
}
/** The button's tooltip key and its values: which of a new worktree or this pane happens, or why the row waits. */
export function runNowTip(runNow: RunNow, held: number): [string, Record<string, number>?] {
  switch (runNow) {
    case 'worktree': return ['queue.runNowWorktreeHelp'];
    case 'agentMenu': return ['queue.worktreeMenuHintMac'];
    case 'steer': return ['queue.runNowSteerHelp'];
    case 'held': return ['queue.runNowHeldHelp', { count: held }];
    case 'waitsForTurn': return ['queue.runNowUnavailableHelp'];
    case 'waitsForHeld': return ['queue.runNowAfterHeldHelp'];
    case 'waitsForUpdate': return ['queue.runNowUpdateHelp'];
    default: return ['queue.runNowHereHelp'];
  }
}
/** A row that cannot run now: drawn disabled (aria-disabled, so its title still shows) and its click does nothing. */
export const runNowWaits = (runNow: RunNow): boolean => runNow.startsWith('waitsFor');
/** Worktree rows draw the branch, the rest the play triangle. */
export const runNowBranches = (runNow: RunNow): boolean => runNow === 'worktree' || runNow === 'agentMenu';
