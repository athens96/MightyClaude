import { describe, expect, it } from 'vitest';
import { diffLines, worktreeBar } from './worktree';
import type { PaneWorktree } from '../store/models';

const t = (key: string, vars?: Record<string, string | number>) => key + (vars ? JSON.stringify(vars) : '');
const pane: PaneWorktree = { worktreePath: 'C:\\wt\\p', branch: 'mighty/p', baseBranch: 'main', baseCommit: 'a'.repeat(40), checkout: 'C:\\repo', port: 4141 };

describe('the worktree bar', () => {
  it('merges when nothing is changed and offers commit and merge while files are', () => {
    expect(worktreeBar(pane, null, t).primary.action).toBe('merge');
    const changed = worktreeBar(pane, { ahead: 2, behind: 1, changed: 3, known: true, busy: false, conflict: false, canUndo: false }, t);
    expect(changed.primary.action).toBe('commitMerge');
    expect([changed.ahead?.text, changed.behind?.text, changed.changed?.text]).toEqual(['↑2', '↓1', '±3']);
  });

  it('asks to resolve after a conflict and offers undo after a merge', () => {
    const model = worktreeBar({ ...pane, setupStatus: 'failed' }, { ahead: 1, behind: 0, changed: 0, known: true, busy: false, conflict: true, canUndo: true }, t);
    expect(model.resolve?.action).toBe('resolve');
    expect(model.behind).toBeUndefined();
    expect(model.setup?.tone).toBe('err');
    expect(model.menu.map((m) => m.action)).toEqual(['changes', 'terminal', 'reveal', 'port', 'undo', 'pr', 'discard']);
  });

  it('reads a diff line by line', () => {
    expect(diffLines('--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same').map((l) => l.kind)).toEqual(['meta', 'meta', 'hunk', 'del', 'add', 'same']);
  });
});
