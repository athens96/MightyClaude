import type { QueuedItem } from '@/api/types';

/** Why a queued row's 바로 실행 is dimmed, as a word the phone puts into its own language. */
export type RunNowReason = 'turn' | 'held' | 'update' | 'agent';
export type RunNowRead = { kind: 'worktree' | 'here' } | { kind: 'unavailable'; reason: RunNowReason };

const reasons: readonly RunNowReason[] = ['turn', 'held', 'update', 'agent'];

/** The row's 바로 실행 as the host decided it. Any other `runNow` hides the button; an unknown reason reads as the current task. */
export function runNowOf(item: QueuedItem): RunNowRead | undefined {
  const value: unknown = item.runNow;
  if (value === 'worktree' || value === 'here') return { kind: value };
  if (value !== 'unavailable') return undefined;
  const reason: unknown = item.runNowReason;
  return { kind: 'unavailable', reason: (reasons as readonly unknown[]).includes(reason) ? (reason as RunNowReason) : 'turn' };
}

/** The locale key that says why a dimmed row cannot run now (the phone's own language, not the host's). */
export function runNowReasonKey(reason: RunNowReason): string {
  switch (reason) {
    case 'held':
      return 'queue.runNowAfterHeldHelp';
    case 'update':
      return 'queue.runNowUpdateHelp';
    case 'agent':
      return 'phone.queue.runNowAgentOnComputer';
    default:
      return 'queue.runNowUnavailableHelp';
  }
}
