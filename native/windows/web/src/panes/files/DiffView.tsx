import { useMemo } from 'react';
import { useServices } from '../../app/context';
import { baseCaption, diffRows, gutterDigits } from './diff';
import type { FileChanges } from './types';

/**
 * The 변경사항 view of a source preview (M/FileDiffView.swift, the phone's DiffView): a caption naming the base and the
 * counts, the truncated note, then a unified diff in the source's monospace: per hunk its `@@` head, then its lines with
 * the old and new line numbers in two gutters, added lines on the done wash and removed ones on the error wash.
 */
export function DiffView({ changes }: { changes: FileChanges }) {
  const { t } = useServices();
  const rows = useMemo(() => diffRows(changes), [changes]);
  const width = gutterDigits(rows) + 'ch';
  const base = baseCaption(changes);
  const caption = base === 'created' ? t('files.changes.created')
    : base === 'run' ? t('files.changes.baseRun')
    : base === 'head' ? t('files.changes.baseHead', { sha: changes.headSha ?? '' })
    : t('files.changes.baseHeadPlain');
  return (
    <div className="files-diff" data-testid="files-diff">
      <div className="files-diff-caption">
        <span>{caption}</span>
        <span className="files-diff-count added">+{changes.added}</span>
        <span className="files-diff-count removed">−{changes.removed}</span>
      </div>
      {changes.truncated && <div className="files-banner">{t('files.changes.truncated')}</div>}
      <div className="files-diff-rows">
        {rows.map((row, index) => (
          <div key={index} className={'files-diff-row ' + row.kind}>
            <span className="files-diff-number" style={{ width }}>{row.old ?? ''}</span>
            <span className="files-diff-number" style={{ width }}>{row.new ?? ''}</span>
            <span className="files-diff-op" aria-hidden="true">{row.kind === 'added' ? '+' : row.kind === 'removed' ? '−' : ''}</span>
            <span className="files-diff-text">{row.kind === 'noNewline' ? '\\ ' + t('files.changes.noNewline') : row.text}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
