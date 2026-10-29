import { useEffect, useRef, type KeyboardEvent } from 'react';
import { createPortal } from 'react-dom';
import { useServices } from '../app/context';
import { Markdown } from '../conversation/MarkdownView';
import { Icon } from '../icons/Icon';
import { usePageOverlay } from '../shell/overlay';
import type { PlanRecord } from './types';

/** One answered plan's meaning, kept with its text in both the history strip and the diagram. */
export function PlanRecordDetails({ record, expanded, metadata = true }: { record: PlanRecord; expanded: boolean; metadata?: boolean }) {
  const { t } = useServices();
  return <>
    {metadata && <div className="plan-record-meta">
      <span className={'plan-pill tone-' + record.tone}>{record.outcomeTitle}</span>
      <span className="plan-record-time">{record.time}</span>
    </div>}
    {expanded ? <div className="plan-document"><Markdown text={record.plan} /></div> : metadata && <div className="plan-record-headline">{record.headline}</div>}
    {expanded && record.truncated && <div className="card-note">{t('plan.history.truncated')}</div>}
    {record.feedback && <div className={'plan-feedback' + (expanded ? ' open' : '')} title={record.feedback}>{t('plan.history.feedback')} · {record.feedback}</div>}
  </>;
}

export interface PlanDocumentProps {
  title: string;
  subtitle?: string;
  plan: string;
  feedback?: string;
  truncated?: boolean;
  onClose(): void;
}

/**
 * A pending or answered plan opened over the whole window, outside the graph's transformed/clipped layer. Like the picture
 * viewer it hides overlapping native browser panes, owns Escape/Tab, and returns focus to its Expand button on close.
 */
export function PlanDocument(props: PlanDocumentProps) {
  return createPortal(<PlanDocumentDialog {...props} />, document.body);
}

/** Exported separately so the document's content and metadata can be checked without a browser. */
export function PlanDocumentDialog({ title, subtitle, plan, feedback, truncated, onClose }: PlanDocumentProps) {
  const { t } = useServices();
  const card = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  usePageOverlay(true);
  useEffect(() => {
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    card.current?.querySelector<HTMLElement>('[data-testid=plan-document-close]')?.focus();
    const escape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape' || event.isComposing || event.keyCode === 229) return;
      event.preventDefault(); event.stopPropagation(); close.current();
    };
    window.addEventListener('keydown', escape, true);
    return () => { window.removeEventListener('keydown', escape, true); if (before?.isConnected) before.focus(); };
  }, []);
  const key = (event: KeyboardEvent) => {
    event.stopPropagation();
    if (event.key !== 'Tab' || !card.current) return;
    const controls = [...card.current.querySelectorAll<HTMLElement>('button:not(:disabled), a[href], [tabindex="0"]')];
    const first = controls[0], last = controls[controls.length - 1];
    if (!first || !last) return;
    if (event.shiftKey && (document.activeElement === first || !card.current.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  };
  return (
    <div className="card-dialog" data-page-overlay="" onPointerDown={(event) => event.stopPropagation()} onWheel={(event) => event.stopPropagation()}
      onClick={(event) => { event.stopPropagation(); if (event.target === event.currentTarget) onClose(); }} onKeyDown={key}>
      <div className="card-dialog-body" data-testid="plan-document" ref={card} role="dialog" aria-modal="true" aria-label={title}>
        <div className="card-dialog-title">
          <Icon name="clipboard-list" size={15} weight={1.8} className="card-dialog-mark" />
          <div className="card-dialog-heading"><strong>{title}</strong>{subtitle && <span className="plan-record-time">{subtitle}</span>}</div>
          <span className="card-spacer" />
          <button type="button" className="card-dialog-close" data-testid="plan-document-close" onClick={onClose}>{t('plan.card.close')}</button>
        </div>
        <div className="card-dialog-document">
          <Markdown text={plan} />
          {truncated && <div className="card-note">{t('plan.history.truncated')}</div>}
          {feedback && <div className="plan-feedback open">{t('plan.history.feedback')} · {feedback}</div>}
        </div>
      </div>
    </div>
  );
}
