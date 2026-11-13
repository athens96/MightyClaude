import type { MobilePaneWorktree } from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import { menuOffers, primaryOffers, worktreeCounts, worktreeLabel, worktreeSetup } from '@/lib/pane-worktree';

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

const base: MobilePaneWorktree = { branch: 'mighty/p1', base: 'main', port: 4321 };

describe('a pane that works in its own worktree', () => {
  it('names its branch and base as the Mac bar does, and its counts only once read', () => {
    expect(worktreeLabel(base)).toBe('worktree mighty/p1 → main');
    expect(worktreeCounts(base)).toBeUndefined();
    expect(worktreeCounts({ ...base, ahead: 2, behind: 0, changed: 3 })).toBe('커밋 ↑2 · 기준 ↓0 · 변경 3');
    expect(worktreeSetup(base)).toBeUndefined();
    expect(worktreeSetup({ ...base, setup: 'failed' })).toEqual({ text: '준비 실패', failed: true });
    expect(worktreeSetup({ ...base, setup: 'someday' })).toBeUndefined();
  });

  it('offers commit-and-merge while there are changes, merge otherwise, and conflict help after a conflict', () => {
    expect(primaryOffers(base).map((offer) => offer.action)).toEqual(['merge']);
    expect(primaryOffers({ ...base, changed: 2 }).map((offer) => offer.action)).toEqual(['commitMerge']);
    expect(primaryOffers({ ...base, conflict: true }).map((offer) => offer.action)).toEqual(['resolveConflict', 'merge']);
    // The menu holds the other merge, undo only while it can be undone, and discard.
    expect(menuOffers(base).map((offer) => offer.action)).toEqual(['commitMerge', 'discard']);
    expect(menuOffers({ ...base, changed: 1, canUndo: true }).map((offer) => offer.action)).toEqual(['merge', 'undo', 'discard']);
  });

  it('asks before a discard and refuses it while children branch from the pane', () => {
    const discard = menuOffers(base).find((offer) => offer.action === 'discard');
    expect(discard?.confirm).toBe(true);
    expect(discard?.disabledReason).toBeUndefined();
    const blocked = menuOffers({ ...base, children: 2 }).find((offer) => offer.action === 'discard');
    expect(blocked?.disabledReason).toContain('2개');
  });
});
