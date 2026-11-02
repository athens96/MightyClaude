import type { FileChanges, FileDiffHunk } from './types';

/**
 * One drawn row of the 변경사항 view (DiffView.tsx): a hunk's `@@` head, a context, added or removed line with its old and new
 * line numbers (null on the side it is not on), or the "no newline at end of file" mark under the line it belongs to.
 */
export interface DiffRow {
  kind: 'hunk' | 'context' | 'added' | 'removed' | 'noNewline';
  old: number | null;
  new: number | null;
  text: string;
}

/** A hunk's head as git writes it, its counts always written: `@@ -3,7 +3,8 @@`. */
export function hunkHeader(hunk: FileDiffHunk): string {
  return `@@ -${hunk.oldStart},${hunk.oldLines} +${hunk.newStart},${hunk.newLines} @@`;
}

/**
 * The rows of a change set, hunk after hunk: each hunk's lines numbered from its own starts (a side with no lines starts at
 * the line before it, so it numbers nothing), a removed line on the old side only, an added one on the new side only.
 */
export function diffRows(changes: Pick<FileChanges, 'hunks'>): DiffRow[] {
  const rows: DiffRow[] = [];
  for (const hunk of changes.hunks) {
    rows.push({ kind: 'hunk', old: null, new: null, text: hunkHeader(hunk) });
    let old = hunk.oldStart, next = hunk.newStart;
    for (const line of hunk.lines) {
      if (line.op === '-') rows.push({ kind: 'removed', old: old++, new: null, text: line.text });
      else if (line.op === '+') rows.push({ kind: 'added', old: null, new: next++, text: line.text });
      else rows.push({ kind: 'context', old: old++, new: next++, text: line.text });
      if (line.noNewline) rows.push({ kind: 'noNewline', old: null, new: null, text: '' });
    }
  }
  return rows;
}

/** The widest line number either gutter draws, in digits (at least 1). */
export function gutterDigits(rows: readonly DiffRow[]): number {
  let most = 0;
  for (const row of rows) most = Math.max(most, row.old ?? 0, row.new ?? 0);
  return String(most).length;
}

/** Which caption names the base: a created file, the run's original, or HEAD with or without its short commit. */
export function baseCaption(changes: Pick<FileChanges, 'base' | 'created' | 'headSha'>): 'created' | 'run' | 'head' | 'headPlain' {
  if (changes.created) return 'created';
  if (changes.base === 'run') return 'run';
  return changes.headSha ? 'head' : 'headPlain';
}

/** Whether a change set earns the 변경사항 | 전체 보기 toggle: there is one, it is not the base unchanged and it has a hunk. */
export function showsChanges(changes: FileChanges | null | undefined): changes is FileChanges {
  return !!changes && !changes.unchanged && changes.hunks.length > 0;
}
