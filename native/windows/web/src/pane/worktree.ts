// The bar over an agent pane that works in its own worktree (the Mac's PaneWorktreeBar, docs/devtool-features.md §12): what it
// says and which actions it offers, from the pane's record and the status the host reads with git (paneWorktreeStatus).
import type { PaneWorktree } from '../store/models';

export interface WorktreeStatus {
  ahead: number;
  behind: number;
  changed: number;
  known: boolean;
  busy: boolean;
  conflict: boolean;
  canUndo: boolean;
}

export interface BarAction {
  action: string;
  label: string;
  help?: string;
}

export interface WorktreeBarModel {
  branch: string;
  help: string;
  ahead?: { text: string; help: string };
  behind?: { text: string; help: string };
  changed?: { text: string; help: string };
  port: { text: string; help: string };
  setup?: { tone: 'run' | 'done' | 'err'; text: string };
  primary: BarAction;
  resolve?: BarAction;
  menu: BarAction[];
}

type Translate = (key: string, vars?: Record<string, string | number>) => string;

/** The bar's words and actions: Merge, or Commit and merge while files are changed; Ask to resolve after a conflict. */
export function worktreeBar(pane: PaneWorktree, status: WorktreeStatus | null, t: Translate): WorktreeBarModel {
  const base = pane.baseBranch;
  const changed = status?.changed ?? 0;
  const model: WorktreeBarModel = {
    branch: t('paneWorktree.bar.branch', { branch: pane.branch, base }),
    help: pane.workingFolder ?? pane.worktreePath,
    port: { text: 'PORT ' + pane.port, help: t('paneWorktree.bar.portHelp', { port: pane.port }) },
    primary: changed > 0
      ? { action: 'commitMerge', label: t('paneWorktree.bar.commitMerge'), help: t('paneWorktree.bar.commitMergeHelp', { base }) }
      : { action: 'merge', label: t('paneWorktree.bar.merge'), help: t('paneWorktree.bar.mergeHelp', { base }) },
    menu: [
      { action: 'changes', label: t('paneWorktree.bar.changes') },
      { action: 'terminal', label: t('paneWorktree.bar.terminal') },
      { action: 'reveal', label: t('menu.showInExplorer') },
      { action: 'port', label: t('paneWorktree.bar.openPort', { port: pane.port }) },
    ],
  };
  if (status?.known) {
    model.ahead = { text: '↑' + status.ahead, help: t('paneWorktree.bar.ahead', { count: status.ahead, base }) };
    if (status.behind > 0) model.behind = { text: '↓' + status.behind, help: t('paneWorktree.bar.behind', { count: status.behind, base }) };
    if (status.changed > 0) model.changed = { text: '±' + status.changed, help: t('paneWorktree.bar.changed', { count: status.changed }) };
  }
  if (pane.setupStatus === 'running') model.setup = { tone: 'run', text: t('paneWorktree.bar.setupRunning') };
  else if (pane.setupStatus === 'done') model.setup = { tone: 'done', text: t('paneWorktree.bar.setupDone') };
  else if (pane.setupStatus === 'failed') model.setup = { tone: 'err', text: t('paneWorktree.bar.setupFailed') };
  if (status?.conflict) model.resolve = { action: 'resolve', label: t('paneWorktree.bar.resolve'), help: t('paneWorktree.bar.resolveHelp', { base }) };
  if (status?.canUndo) model.menu.push({ action: 'undo', label: t('paneWorktree.bar.undo', { base }) });
  model.menu.push({ action: 'pr', label: t('paneWorktree.bar.pr') }, { action: 'discard', label: t('paneWorktree.bar.discard') });
  return model;
}

/** A diff's lines with their kind, for the changes sheet. */
export function diffLines(text: string): { kind: 'add' | 'del' | 'hunk' | 'meta' | 'same'; text: string }[] {
  return text.replace(/\r\n/g, '\n').split('\n').map((line) => ({
    kind: line.startsWith('+++') || line.startsWith('---') || line.startsWith('diff ') || line.startsWith('index ') ? 'meta'
      : line.startsWith('@@') ? 'hunk' : line.startsWith('+') ? 'add' : line.startsWith('-') ? 'del' : 'same',
    text: line,
  }));
}
