import type { MobilePaneWorktree, MobileSessionSummary, PaneWorktreeAction } from '@/api/types';
import { t } from '@/lib/i18n';

/**
 * A pane that works in its own git worktree on the Mac (`MobilePaneWorktree`, docs/mobile-remote.md "창마다 worktree"):
 * what the phone says about it and which of the Mac's actions it offers. The Mac does the git work and refuses what does
 * not hold any more; these rules only keep the phone from offering what the Mac's bar would not.
 */

/** `worktree mighty/… → main`, the Mac bar's words. */
export function worktreeLabel(worktree: MobilePaneWorktree): string {
  return t('paneWorktree.bar.branch', { branch: worktree.branch, base: worktree.base });
}

/** The row's and the header's spoken line: `자기 worktree에서 일함: mighty/… → main`. */
export function worktreeMarker(worktree: MobilePaneWorktree): string {
  return t('paneWorktree.marker', { branch: worktree.branch, base: worktree.base });
}

/** `커밋 ↑2 · 기준 ↓0 · 변경 3`, once the Mac has read the counts; nothing before. */
export function worktreeCounts(worktree: MobilePaneWorktree): string | undefined {
  if (worktree.ahead === undefined && worktree.behind === undefined && worktree.changed === undefined) return undefined;
  return t('paneWorktree.list.counts', {
    ahead: worktree.ahead ?? 0,
    behind: worktree.behind ?? 0,
    changed: worktree.changed ?? 0,
  });
}

/** The setup command's state in the bar's words; nothing without one (or a word this build does not know). */
export function worktreeSetup(worktree: MobilePaneWorktree): { text: string; failed: boolean } | undefined {
  switch (worktree.setup) {
    case 'running':
      return { text: t('paneWorktree.bar.setupRunning'), failed: false };
    case 'done':
      return { text: t('paneWorktree.bar.setupDone'), failed: false };
    case 'failed':
      return { text: t('paneWorktree.bar.setupFailed'), failed: true };
    default:
      return undefined;
  }
}

export interface WorktreeOffer {
  action: PaneWorktreeAction;
  label: string;
  /** Asks first (discard). */
  confirm: boolean;
  /** Shown but not tappable, with the reason underneath. */
  disabledReason?: string;
}

/**
 * The bar's main button: **커밋하고 병합** while the worktree has changes not committed (the Mac would refuse a plain
 * merge), else **병합**. A conflict adds **충돌 해결 요청** before it.
 */
export function primaryOffers(worktree: MobilePaneWorktree): WorktreeOffer[] {
  const offers: WorktreeOffer[] = [];
  if (worktree.conflict) {
    offers.push({ action: 'resolveConflict', label: t('paneWorktree.bar.resolve'), confirm: false });
  }
  offers.push(
    (worktree.changed ?? 0) > 0
      ? { action: 'commitMerge', label: t('paneWorktree.bar.commitMerge'), confirm: false }
      : { action: 'merge', label: t('paneWorktree.bar.merge'), confirm: false },
  );
  return offers;
}

/** The bar's menu: the other merge, undo while the last merge can be undone, and discard (refused while children branch from the pane). */
export function menuOffers(worktree: MobilePaneWorktree): WorktreeOffer[] {
  const offers: WorktreeOffer[] = [];
  if ((worktree.changed ?? 0) > 0) offers.push({ action: 'merge', label: t('paneWorktree.bar.merge'), confirm: false });
  else offers.push({ action: 'commitMerge', label: t('paneWorktree.bar.commitMerge'), confirm: false });
  if (worktree.canUndo) {
    offers.push({ action: 'undo', label: t('paneWorktree.bar.undo', { base: worktree.base }), confirm: false });
  }
  const children = worktree.children ?? 0;
  offers.push({
    action: 'discard',
    label: t('paneWorktree.bar.discard'),
    confirm: true,
    ...(children > 0 ? { disabledReason: t('paneWorktree.refused.hasChildren', { count: children }) } : {}),
  });
  return offers;
}

/** The pane's worktree, from its summary; undefined for every other pane and for an older host. */
export function paneWorktreeOf(session: MobileSessionSummary | undefined): MobilePaneWorktree | undefined {
  return session?.worktree;
}
