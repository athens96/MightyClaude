import { describe, expect, it } from 'vitest';
import { readRunNow, runNowBranches, runNowTip, runNowWaits, type RunNow } from './queue';

const names: RunNow[] = ['worktree', 'agentMenu', 'here', 'steer', 'held', 'waitsForTurn', 'waitsForHeld', 'waitsForUpdate'];

describe('바로 실행 on a queued row', () => {
  it('reads the eight names the host sends and nothing else', () => {
    for (const name of names) expect(readRunNow(name)).toBe(name);
    for (const value of ['hidden', 'runNext', '', null, 1, undefined]) expect(readRunNow(value)).toBeNull();
  });

  it('gives each answer its tooltip (the Global Constraints table)', () => {
    expect(runNowTip('held', 3)).toEqual(['queue.runNowHeldHelp', { count: 3 }]);
    const keys: Record<RunNow, string> = {
      worktree: 'queue.runNowWorktreeHelp', agentMenu: 'queue.worktreeMenuHintMac', here: 'queue.runNowHereHelp', steer: 'queue.runNowSteerHelp',
      held: 'queue.runNowHeldHelp', waitsForTurn: 'queue.runNowUnavailableHelp', waitsForHeld: 'queue.runNowAfterHeldHelp', waitsForUpdate: 'queue.runNowUpdateHelp',
    };
    for (const name of names) expect(runNowTip(name, 1)[0]).toBe(keys[name]);
  });

  it('draws only the three waiting answers disabled, and the branch on worktree rows', () => {
    expect(names.filter(runNowWaits)).toEqual(['waitsForTurn', 'waitsForHeld', 'waitsForUpdate']);
    expect(names.filter(runNowBranches)).toEqual(['worktree', 'agentMenu']);
  });
});
