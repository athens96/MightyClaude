import type { FileChanges, FileChangesRequest, FileDiffHunk } from '@/api/types';
import { t } from '@/lib/i18n';

/**
 * The file preview's 변경사항 view (docs/mobile-remote.md "변경사항", the desktop's DiffView): the rows a host's
 * `changes` draws, which caption names its base, and the request the preview screen sends.
 */

/**
 * One drawn row: a hunk's `@@` head, a context, added or removed line with its old and new line numbers (undefined on
 * the side it is not on), or the "no newline at end of file" mark under the line it belongs to.
 */
export interface DiffRow {
  kind: 'hunk' | 'context' | 'added' | 'removed' | 'noNewline';
  old?: number;
  new?: number;
  text: string;
}

/** A hunk's head as git writes it, its counts always written: `@@ -3,7 +3,8 @@`. */
export function hunkHeader(hunk: FileDiffHunk): string {
  return `@@ -${hunk.oldStart},${hunk.oldLines} +${hunk.newStart},${hunk.newLines} @@`;
}

/**
 * The rows hunk after hunk, each hunk numbered from its own starts (a side with no lines starts at the line before it, so
 * it numbers nothing): a removed line on the old side only, an added one on the new side only.
 */
export function diffRows(changes: Pick<FileChanges, 'hunks'>): DiffRow[] {
  const rows: DiffRow[] = [];
  for (const hunk of changes.hunks) {
    rows.push({ kind: 'hunk', text: hunkHeader(hunk) });
    let old = hunk.oldStart;
    let next = hunk.newStart;
    for (const line of hunk.lines) {
      if (line.op === '-') rows.push({ kind: 'removed', old: old++, text: line.text });
      else if (line.op === '+') rows.push({ kind: 'added', new: next++, text: line.text });
      else rows.push({ kind: 'context', old: old++, new: next++, text: line.text });
      if (line.noNewline) rows.push({ kind: 'noNewline', text: '' });
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

/** What the caption says the diff is against: a created file, the request's original, or HEAD. */
export function baseCaption(changes: Pick<FileChanges, 'base' | 'created' | 'headSha'>): string {
  if (changes.created) return t('files.changes.created');
  if (changes.base === 'run') return t('files.changes.baseRun');
  return changes.headSha ? t('files.changes.baseHead', { sha: changes.headSha }) : t('files.changes.baseHeadPlain');
}

/** Whether a preview's `changes` earns the 변경사항 | 전체 보기 toggle: it changed something. */
export function showsChanges(changes: FileChanges | undefined): changes is FileChanges {
  return !!changes && !changes.unchanged && changes.hunks.length > 0;
}

/**
 * What the preview screen asks a host listing "fileChanges" for: `changes=1`, with the pane and run a result's files row
 * came from (route params `session`, `run`); undefined against an older host, which would answer 400.
 */
export function changesRequest(
  canAsk: boolean,
  params: { session?: string | string[]; run?: string | string[] },
): FileChangesRequest | undefined {
  if (!canAsk) return undefined;
  const one = (value: string | string[] | undefined) => (typeof value === 'string' && value.length > 0 ? value : undefined);
  const request: FileChangesRequest = {};
  const session = one(params.session);
  const run = one(params.run);
  if (session) request.session = session;
  if (run) request.run = run;
  return request;
}
