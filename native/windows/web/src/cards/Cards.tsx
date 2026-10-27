import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react';
import { flushSync } from 'react-dom';
import { Icon } from '../icons/Icon';
import { useServices, useStoreState } from '../app/context';
import { Markdown } from '../conversation/MarkdownView';
import { cardKey, forwardIndex, restoreIndex } from './keys';
import * as draftOf from './questionnaire';
import { usePageOverlay } from '../shell/overlay';
import { Pending } from '../pending/Pending';
import { usePendingAction, type ActionResult } from '../pending/usePending';
import type {
  AntigravityCard, BackgroundCard, Card, LoginCard, NextActionsCard, PermissionCard, PlanCard, PlanHistoryCard, QuestionnaireCard,
  StatusLineCard, WebOpenCard,
} from './types';

type Call = (method: string, args?: Record<string, unknown>) => Promise<unknown>;
/** An answer's bridge call: what it returned, or the failure (already on the cards' error line). */
type Reply = (method: string, args?: Record<string, unknown>) => Promise<{ ok: true; value: unknown } | { ok: false; message: string }>;

/** A pane's cards and the way to answer them: every answer is a host method (MainWindow.WebShell.Cards.cs). */
function usePaneCards(paneId: string, load = false): { cards: readonly Card[]; call: Call; reply: Reply; error: string | null; setError(value: string | null): void } {
  const { bridge, store } = useServices();
  const cards = useStoreState().cards[paneId] ?? none;
  const [error, setError] = useState<string | null>(null);
  const call = useCallback<Call>(async (method, args = {}) => {
    setError(null);
    try {
      return await bridge.call(method, { paneId, ...args });
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
      return undefined;
    }
  }, [bridge, paneId]);
  const reply = useCallback<Reply>(async (method, args = {}) => {
    setError(null);
    try {
      return { ok: true, value: await bridge.call(method, { paneId, ...args }) };
    } catch (failure) {
      const message = failure instanceof Error ? failure.message : String(failure);
      setError(message);
      return { ok: false, message };
    }
  }, [bridge, paneId]);
  useEffect(() => {
    if (!load) return;
    bridge.call<Card[]>('cards', { paneId }).then((value) => { if (value) store.dispatch({ kind: 'cards', paneId, cards: value }); }, () => {});
  }, [bridge, store, paneId, load]);
  return { cards, call, reply, error, setError };
}

const none: readonly Card[] = [];

/**
 * The cards over the composer, in the WinUI order (MainWindow.Composer.cs): next actions, the plan history, background work,
 * the permission host (a plan, a question, a tool permission or the Antigravity card) and the web-open prompts.
 */
export function Cards({ paneId }: { paneId: string }) {
  const { cards, call, reply, error } = usePaneCards(paneId, true);
  const request = cards.find((card) => card.kind === 'permission' || card.kind === 'questionnaire' || card.kind === 'plan' || card.kind === 'antigravity');
  const shown = cards.filter((card) => card.kind !== 'login' && card.kind !== 'statusLine');
  if (shown.length === 0 && !error) return null;
  return (
    <div className="cards" data-testid="cards">
      {cards.map((card) => {
        switch (card.kind) {
          case 'nextActions': return <NextActions key="next" paneId={paneId} card={card} call={call} />;
          case 'planHistory': return <PlanHistory key="history" card={card} />;
          case 'background': return <Background key="background" card={card} call={call} />;
          case 'webOpen': return <WebOpen key={'web-' + card.requestId} card={card} reply={reply} />;
          default: return null;
        }
      })}
      {request && (
        <KeyboardCard>
          {request.kind === 'permission' && <Permission key={request.requestId} card={request} reply={reply} />}
          {request.kind === 'questionnaire' && <Questionnaire key={request.requestId} card={request} reply={reply} />}
          {request.kind === 'plan' && <Plan key={request.requestId} card={request} reply={reply} />}
          {request.kind === 'antigravity' && <Antigravity key={request.cardId} card={request} reply={reply} />}
        </KeyboardCard>
      )}
      {error && <div className="card-error" role="alert">{error}</div>}
    </div>
  );
}

/**
 * The permission host's keyboard (WaitCard, RestoreCardFocus): a press on the card's words or background puts the keyboard on
 * it (the chosen answer, else the first answer tile, else the first button); the arrows move between the card's answers and
 * Enter on a single question's answer picks it and goes on to Next or Send (keys.ts). When the card is drawn again (each pick
 * redraws it, Next turns into Send) with the keyboard in it, it stays there, on the same answer or the first one. Focus
 * elsewhere is left alone, so a card arriving never takes the keyboard from the composer. Keys in the card stay in the card:
 * the window's shortcuts do not see them.
 */
function KeyboardCard({ children }: { children: ReactNode }) {
  const host = useRef<HTMLDivElement>(null);
  const focused = useRef<string | null>(null);
  const inside = useRef(false);
  const answers = () => [...(host.current?.querySelectorAll<HTMLElement>('[data-answer]') ?? [])];
  const enabled = (element: HTMLElement) => !(element as HTMLButtonElement).disabled;
  const checked = (element: HTMLElement) => element.getAttribute('aria-checked') === 'true';
  const focusAt = (list: HTMLElement[], index: number) => {
    const element = list[index];
    if (!element) return;
    element.focus();
    focused.current = element.dataset.answer ?? null;
  };
  const restore = (previous: string | null) => {
    const list = answers();
    focusAt(list, restoreIndex(list.map((a) => a.dataset.answer ?? ''), list.map(enabled), previous, list.map(checked)));
  };

  // Any redraw of the card, the host's pushes and the card's own state alike: a focused answer that went away, turned
  // disabled or became another (Next into Send) gives the keyboard back to the card, never to the page.
  useEffect(() => {
    const element = host.current;
    if (!element) return;
    const settle = () => {
      if (!inside.current) return;
      const active = document.activeElement as HTMLElement | null;
      if (active && active !== element && element.contains(active)) {
        if (active.dataset.answer === undefined || (enabled(active) && active.dataset.answer === focused.current)) return;
      } else if (active && active !== document.body) { inside.current = false; return; }
      restore(focused.current);
    };
    const observer = new MutationObserver(settle);
    observer.observe(element, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled', 'data-answer'] });
    return () => observer.disconnect();
  }, []);

  const keyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    event.stopPropagation();
    const target = event.target as HTMLElement;
    if (!target.dataset.answer) return;
    const list = answers().filter(enabled);
    const action = cardKey(event.key, target.getAttribute('role'), list.indexOf(target), list.length, event.altKey || event.ctrlKey || event.metaKey);
    if (!action) return;
    event.preventDefault();
    if (action.kind === 'move') { focusAt(list, action.index); return; }
    if (event.repeat) return;
    flushSync(() => target.click());
    const all = answers();
    focusAt(all, forwardIndex(all.map((a) => a.dataset.answer ?? ''), all.map(enabled)));
  };

  // A press on the card that is not on one of its controls, and selects no words, puts the keyboard on the card.
  const click = (event: MouseEvent<HTMLDivElement>) => {
    const target = event.target as HTMLElement;
    if (target.closest('button, a, input, textarea, select, label, [role=dialog]')) return;
    if (window.getSelection()?.isCollapsed === false) return;
    inside.current = true;
    restore(null);
  };

  return (
    <div
      ref={host}
      className="card-host"
      onKeyDown={keyDown}
      onClick={click}
      onFocus={(event) => { inside.current = true; focused.current = (event.target as HTMLElement).dataset.answer ?? null; }}
      onBlur={(event) => { if (event.relatedTarget && !host.current?.contains(event.relatedTarget as Node)) inside.current = false; }}
    >
      {children}
    </div>
  );
}

function Badge({ children }: { children: ReactNode }) {
  return <span className="card-badge" aria-hidden="true">{children}</span>;
}

function NextActions({ paneId, card, call }: { paneId: string; card: NextActionsCard; call: Call }) {
  const { store, t } = useServices();
  const fill = async (index: number) => {
    const text = await call('nextActionFill', { entryId: card.entryId, index });
    if (typeof text === 'string') store.dispatch({ kind: 'fill', paneId, text });
  };
  return (
    <div className="next-actions" data-card="nextActions" aria-label={t('pane.nextActions.label')}>
      <div className="next-actions-label">{t('pane.nextActions.label')}</div>
      {card.actions.map((label, index) => (
        <button type="button" key={index} className="next-action" title={label} aria-description={t('pane.nextActions.fillHint')} onClick={() => void fill(index)}>
          <span>{label}</span><span className="next-action-arrow" aria-hidden="true">→</span>
        </button>
      ))}
    </div>
  );
}

function Fold({ open, label, onClick, children }: { open: boolean; label: string; onClick(): void; children: ReactNode }) {
  return (
    <button type="button" className="card-fold" aria-expanded={open} aria-label={label} onClick={onClick}>
      {children}<Icon name={open ? 'chevron-up' : 'chevron-down'} size={11} weight={2.4} className="card-chevron" />
    </button>
  );
}

function PlanHistory({ card }: { card: PlanHistoryCard }) {
  const { t } = useServices();
  const [open, setOpen] = useState(false);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set());
  const latest = card.records[0];
  const toggle = (id: string) => setExpanded((now) => { const next = new Set(now); if (!next.delete(id)) next.add(id); return next; });
  return (
    <div className="card-strip" data-card="planHistory">
      <Fold open={open} label={t(open ? 'plan.history.hide' : 'plan.history.show')} onClick={() => setOpen(!open)}>
        <span className="card-strip-mark" aria-hidden="true">≡</span>
        <strong>{t('plan.history.title', { count: card.records.length })}</strong>
        {latest && <span className="card-strip-summary">{latest.outcomeTitle} · {latest.headline}</span>}
      </Fold>
      {open && (
        <div className="card-strip-list">
          {card.records.map((record) => {
            const shown = expanded.has(record.id);
            return (
              <div className="plan-record" key={record.id} data-plan-record={record.id}>
                <Fold open={shown} label={t(shown ? 'plan.history.hide' : 'plan.history.show')} onClick={() => toggle(record.id)}>
                  <span className={'plan-pill tone-' + record.tone}>{record.outcomeTitle}</span>
                  <span className="plan-record-time">{record.time}</span>
                  <span className="card-strip-summary">{record.headline}</span>
                </Fold>
                {shown && <div className="plan-document"><Markdown text={record.plan} /></div>}
                {shown && record.truncated && <div className="card-note">{t('plan.history.truncated')}</div>}
                {record.feedback && <div className={'plan-feedback' + (shown ? ' open' : '')}>{t('plan.history.feedback')} · {record.feedback}</div>}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function Background({ card, call }: { card: BackgroundCard; call: Call }) {
  const { t } = useServices();
  return (
    <div className="card-strip" data-card="background">
      <div className="card-strip-line">
        <Fold open={card.open} label={t(card.open ? 'plan.background.hide' : 'plan.background.show')} onClick={() => void call('backgroundWork', { action: 'toggle' })}>
          <span className="card-strip-mark" aria-hidden="true"><Icon name="layers" size={14} weight={1.75} /></span>
          <strong>{t('plan.background.listTitle', { count: card.count })}</strong>
          <span className="card-strip-summary">{card.summary}</span>
        </Fold>
        <button type="button" className="card-icon-button" aria-label={t('plan.background.hideStrip')} title={t('plan.background.hideStripHelp')} onClick={() => void call('backgroundWork', { action: 'hide' })}><Icon name="eye-off" size={13} weight={1.75} /></button>
      </div>
      {card.open && (
        <div className="card-strip-list">
          {card.tasks.map((task, index) => (
            <div className="background-task" key={index} title={task.text + ' · ' + task.detail}>
              <span className={'background-dot status-' + task.status} aria-hidden="true" />
              <span className="background-text">{task.text}</span>
              <span className="background-detail">{task.detail}</span>
              {task.tail && <span className="background-tail" data-testid="background-tail">{task.tail}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/** A refusal the host answers with in words (a questionnaire or an Antigravity answer it did not take) is a failure too. */
function refused(result: Awaited<ReturnType<Reply>>, refusal: (words: string) => void): ActionResult {
  if (!result.ok) return result;
  if (typeof result.value === 'string') { refusal(result.value); return { ok: false, message: result.value }; }
  return { ok: true };
}

function Permission({ card, reply }: { card: PermissionCard; reply: Reply }) {
  const [json, setJson] = useState(false);
  // The answer locks the card while its call runs (natural submit A3); the card goes when the host has it.
  const { state, running, run } = usePendingAction();
  const answer = (allow: boolean) => void run(() => reply('permissionAnswer', { requestId: card.requestId, allow }));
  return (
    <div className="wait-card" data-card="permission" data-request-id={card.requestId}>
      <div className="wait-card-title">
        <Badge><Icon name="hand" size={12} weight={2.2} fill /></Badge>
        <strong className="card-title">{card.title}</strong>
        <span className="card-tool">{card.tool}</span>
        {card.waiting && <span className="card-waiting">{card.waiting}</span>}
      </div>
      {card.headline && <div className="card-headline">{card.headline}</div>}
      <div className="card-details">
        {card.fields.map((field, index) => (
          <div className="card-field" key={index}>
            <div className="card-field-label">{field.label}</div>
            <div className={field.code ? 'card-field-code' : 'card-field-value'}>{field.value}</div>
          </div>
        ))}
        {card.summary && <div className="card-field-code">{card.summary}</div>}
        {card.path && <div className="card-path">{card.path}</div>}
        {card.reason && <div className="card-note">{card.reason}</div>}
        <button type="button" className="card-fold small" aria-expanded={json} onClick={() => setJson(!json)}>
          <Icon name={json ? 'chevron-down' : 'chevron-right'} size={9} weight={2.6} className="card-chevron" />{card.rawJson}
        </button>
        {json && <pre className="card-json">{card.inputJson}</pre>}
      </div>
      {card.cannotAllow && <div className="card-note">{card.cannotAllow}</div>}
      <div className="wait-card-footer">
        <span className="card-hint">{card.onceOnly}</span>
        <Pending state={state} showsFailure={false} />
        <button type="button" className="card-button" data-answer="deny" disabled={running} onClick={() => answer(false)}>{card.deny}</button>
        <button type="button" className="card-button prominent" data-answer="allow" disabled={running || !card.canAllow} onClick={() => answer(true)}>{card.allowOnce}</button>
      </div>
    </div>
  );
}

function Questionnaire({ card, reply }: { card: QuestionnaireCard; reply: Reply }) {
  const { t } = useServices();
  const [draft, setDraft] = useState<draftOf.QuestionDraft>(draftOf.emptyDraft);
  const { state, running: sending, run } = usePendingAction();
  const [error, setError] = useState<string | null>(null);
  const total = card.questions.length;
  const step = Math.min(draft.step, total - 1);
  const question = card.questions[step];
  if (!question) return null;
  const last = step + 1 === total;
  const picks = draft.picks[step] ?? [];
  const custom = draft.custom[step] === true;
  const change = (next: draftOf.QuestionDraft) => { setError(null); setDraft(next); };
  const submit = () => run(async () => refused(await reply('questionnaireAnswer', { requestId: card.requestId, answers: draftOf.answers(draft, card.questions) }), setError));
  const forward = () => (last ? void submit() : change(draftOf.next(draft, total)));
  const enabled = !sending && (last ? draftOf.canSubmit(draft, total) : draftOf.answered(draft, step));
  return (
    <div className="wait-card" data-card="questionnaire" data-request-id={card.requestId}>
      <div className="wait-card-title">
        <Badge>?</Badge>
        <strong className="card-title">{t('phone.questionnaire.title')}</strong>
        <span className="card-waiting">{total > 1 ? t('phone.questionnaire.progress', { current: step + 1, total }) : t('questionnaire.oneQuestion')}</span>
        {total > 1 && (
          <span className="question-dots">
            {card.questions.map((_, index) => (
              <button
                type="button"
                key={index}
                data-answer={'step-' + index}
                className={'question-dot' + (index === step ? ' now' : draftOf.answered(draft, index) ? ' done' : '')}
                aria-label={t('phone.questionnaire.jump', { index: index + 1 })}
                disabled={sending || !draftOf.canJump(draft, index)}
                onClick={() => change(draftOf.jump(draft, index))}
              >{index + 1}</button>
            ))}
          </span>
        )}
        <span className="card-spacer" />
        {card.count > 1 && <span className="card-waiting">{t('phone.questionnaire.waiting', { count: card.count })}</span>}
      </div>
      <div className="question-section">
        <div className="question-kind">
          <span className="question-header">{question.header}</span>
          <span className="card-waiting">{t(question.multiSelect ? 'phone.questionnaire.multiple' : 'phone.questionnaire.single')}</span>
        </div>
        <div className="question-text">{question.question}</div>
        <div className="question-choices" role={question.multiSelect ? 'group' : 'radiogroup'}>
          {question.options.map((option, index) => {
            const on = picks.includes(option.label);
            return (
              <button
                type="button"
                key={option.label}
                role={question.multiSelect ? 'checkbox' : 'radio'}
                aria-checked={on}
                data-answer={`option-${step}-${index}`}
                data-option={index}
                className={'question-choice' + (on ? ' on' : '')}
                disabled={sending}
                onClick={() => change(draftOf.pick(draft, question, option.label))}
              >
                <span className="question-mark" aria-hidden="true">{question.multiSelect ? (on ? '☑' : '☐') : on ? '◉' : '○'}</span>
                <span className="question-words"><strong>{option.label}</strong>{option.description && <span>{option.description}</span>}</span>
              </button>
            );
          })}
          <button
            type="button"
            role={question.multiSelect ? 'checkbox' : 'radio'}
            aria-checked={custom}
            data-answer={'custom-' + step}
            className={'question-choice' + (custom ? ' on' : '')}
            title={t('phone.questionnaire.customHint')}
            disabled={sending}
            onClick={() => change(draftOf.toggleCustom(draft, question))}
          >
            <span className="question-mark" aria-hidden="true">{question.multiSelect ? (custom ? '☑' : '☐') : custom ? '◉' : '○'}</span>
            <span className="question-words"><strong>{t('phone.questionnaire.custom')}</strong></span>
          </button>
        </div>
        {custom && (
          <textarea
            className="card-text"
            data-testid="questionnaire-custom-text"
            value={draft.text[step] ?? ''}
            maxLength={8192}
            placeholder={t('phone.questionnaire.customPlaceholder')}
            aria-label={t('phone.questionnaire.customLabel', { header: question.header })}
            disabled={sending}
            onChange={(event) => change(draftOf.setText(draft, event.target.value))}
          />
        )}
      </div>
      {error && <div className="card-error" role="alert">{error}</div>}
      <div className="wait-card-footer">
        <span className="card-hint">{t(last ? 'phone.questionnaire.hintSubmit' : 'phone.questionnaire.hintNext')}</span>
        <Pending state={state} showsFailure={false} />
        <button type="button" className="card-button" data-answer="cancel" disabled={sending} onClick={() => void run(() => reply('permissionAnswer', { requestId: card.requestId, allow: false }))}>{t('phone.questionnaire.cancel')}</button>
        {step > 0 && <button type="button" className="card-button" data-answer="back" disabled={sending} onClick={() => change(draftOf.back(draft))}>{t('phone.questionnaire.back')}</button>}
        <button type="button" className="card-button prominent" data-answer={last ? 'submit' : 'next'} disabled={!enabled} onClick={forward}>
          {t(last ? 'phone.questionnaire.submit' : 'phone.questionnaire.next')}
        </button>
      </div>
    </div>
  );
}

function Plan({ card, reply }: { card: PlanCard; reply: Reply }) {
  const { t } = useServices();
  const { running, run } = usePendingAction();
  // The host's own flag (the decision on its way) and this card's call, one wait.
  const sending = card.sending || running;
  const [revising, setRevising] = useState(false);
  const [feedback, setFeedback] = useState('');
  const [full, setFull] = useState(false);
  const answers = useRef<HTMLDivElement>(null);
  const measure = useRef<HTMLDivElement>(null);
  const [oneLine, setOneLine] = useState(true);
  useLayoutEffect(() => {
    const row = answers.current, ideal = measure.current;
    if (!row || !ideal) return;
    const fit = () => setOneLine(ideal.scrollWidth <= row.clientWidth + 0.5);
    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(row);
    return () => observer.disconnect();
  }, []);
  usePageOverlay(full);
  const answer = (decision: string) => void run(() => reply('planAnswer', { requestId: card.requestId, decision, feedback: decision === 'revise' ? feedback : undefined }));
  const tile = (key: string, decision: string | null, accent = false) => (
    <button
      type="button"
      key={key}
      className={'card-button' + (accent ? ' accent' : '')}
      data-answer={'plan-' + (decision ?? 'revise-open')}
      disabled={sending || (decision === null && revising)}
      onClick={() => (decision ? answer(decision) : setRevising(true))}
    >{t(key)}</button>
  );
  return (
    <div className="wait-card" data-card="plan" data-request-id={card.requestId}>
      <div className="wait-card-title">
        <Badge><Icon name="clipboard-list" size={13} weight={2.4} /></Badge>
        <strong className="card-title">{t('plan.card.title')}</strong>
        <span className="card-waiting">{card.received}</span>
        <span className="card-spacer" />
        {card.count > 1 && <span className="card-waiting">{t('phone.questionnaire.waiting', { count: card.count })}</span>}
        {!card.actionsOnly && <button type="button" className="card-button with-icon" data-answer="plan-expand" onClick={() => setFull(true)}><Icon name="maximize-2" size={13} weight={2} />{t('plan.card.expand')}</button>}
      </div>
      {!card.actionsOnly && <div className="plan-document" data-testid="plan-text"><Markdown text={card.plan} /></div>}
      {revising && (
        <div className="plan-revise">
          <textarea className="card-text" value={feedback} placeholder={t('plan.card.revisePlaceholder')} aria-label={t('plan.card.revisePlaceholder')} disabled={sending} autoFocus onChange={(event) => setFeedback(event.target.value)} />
          <div className="plan-revise-actions">
            <button type="button" className="card-button" data-answer="plan-revise-close" disabled={sending} onClick={() => setRevising(false)}>{t('plan.card.reviseClose')}</button>
            <button type="button" className="card-button prominent" data-answer="plan-revise-send" disabled={sending || feedback.trim().length === 0} onClick={() => answer('revise')}>{t('plan.card.reviseSend')}</button>
          </div>
        </div>
      )}
      {card.error && <div className="card-error" role="alert">{card.error}</div>}
      {/* M/PlanApprovalCard.swift PlanActions (ViewThatFits): the hint, then the four answers at the right, on one line where all of
          it fits at its own width; else no hint, Cancel and Revise on one row and the two approvals under them, both at the right. */}
      <div className={'plan-answers' + (oneLine ? '' : ' stacked')} ref={answers}>
        <div className="plan-answers-measure" ref={measure} aria-hidden="true">
          <span className="card-hint">{t('plan.card.hint')}</span>
          {['plan.card.cancel', 'plan.card.revise', 'plan.card.approveConfirm', 'plan.card.approveAuto'].map((key) => <span key={key} className="card-button">{t(key)}</span>)}
        </div>
        {oneLine && <span className="card-hint-row"><Pending busy={sending} /><span className="card-hint">{t('plan.card.hint')}</span></span>}
        <span className="plan-answers-row">
          {!oneLine && <Pending busy={sending} />}
          {tile('plan.card.cancel', 'cancel')}
          {tile('plan.card.revise', null)}
        </span>
        <span className="plan-answers-row">
          {tile('plan.card.approveConfirm', 'approveConfirm')}
          {tile('plan.card.approveAuto', 'approveAuto', true)}
        </span>
      </div>
      {full && (
        <div className="card-dialog" role="dialog" data-page-overlay="" aria-label={t('plan.card.title')} onClick={() => setFull(false)}>
          {/* M/PlanApprovalCard.swift PlanDocumentSheet: the clipboard and the title over a rule with Close at the right, then the plan. */}
          <div className="card-dialog-body" data-testid="plan-document" onClick={(event) => event.stopPropagation()}>
            <div className="card-dialog-title">
              <Icon name="clipboard-list" size={15} weight={1.8} className="card-dialog-mark" /><strong>{t('plan.card.title')}</strong>
              <span className="card-spacer" />
              <button type="button" className="card-dialog-close" data-testid="plan-document-close" autoFocus onClick={() => setFull(false)}>{t('plan.card.close')}</button>
            </div>
            <div className="card-dialog-document"><Markdown text={card.plan} /></div>
          </div>
        </div>
      )}
    </div>
  );
}

function Antigravity({ card, reply }: { card: AntigravityCard; reply: Reply }) {
  const { t } = useServices();
  const [reason, setReason] = useState<string | null>(null);
  const { running, run } = usePendingAction();
  const busy = card.busy || running;
  const answer = (decision: string) => run(async () => {
    setReason(null);
    return refused(await reply('antigravityAnswer', { cardId: card.cardId, decision }), setReason);
  });
  const shown = reason ?? card.reason;
  return (
    <div className="wait-card" data-card="antigravity">
      <div className="wait-card-title"><Badge><Icon name="hand" size={12} weight={2.2} fill /></Badge><strong className="card-title">{t('antigravity.permission.title')}</strong></div>
      <div className="card-hint">{t('antigravity.permission.subtitle')}</div>
      <div className="card-details">
        {card.actions.map((action, index) => (
          <div className="card-field" key={index}>
            <div className="card-field-label">{action.heading}</div>
            {action.target && <div className={action.code ? 'card-field-code' : 'card-field-strong'}>{action.target}</div>}
            {action.rule ? <div className="card-rule">{action.rule}</div> : <div className="card-wait-note">{t('antigravity.permission.noRule')}</div>}
            {action.onceOnly && <div className="card-wait-note">{action.onceOnly}</div>}
          </div>
        ))}
      </div>
      {shown && <div className="card-error" role="alert">{shown}</div>}
      <div className="wait-card-footer">
        <span className="card-hint">{t('antigravity.permission.scopeNote')}</span>
        <Pending busy={busy} />
        <button type="button" className="card-button" data-answer="deny" disabled={busy} onClick={() => void answer('Deny')}>{t('antigravity.permission.deny')}</button>
        <button type="button" className="card-button" data-answer="always" title={card.alwaysTip} disabled={busy || !card.canAllowAlways} onClick={() => void answer('Always')}>{t('antigravity.permission.allowAlways')}</button>
        <button type="button" className="card-button prominent" data-answer="once" disabled={busy || !card.canAllow} onClick={() => void answer('Once')}>{t('permission.allowOnce')}</button>
      </div>
    </div>
  );
}

function WebOpen({ card, reply }: { card: WebOpenCard; reply: Reply }) {
  const { t } = useServices();
  const [remember, setRemember] = useState(false);
  const { state, running, run } = usePendingAction();
  const choose = (destination: string) => void run(() => reply('webOpenChoose', { requestId: card.requestId, destination, remember }));
  return (
    <div className="web-open" data-card="webOpen">
      <div className="web-open-title"><span className="web-open-mark" aria-hidden="true">◍</span><strong>{t('agentTerminal.urlOpen.dialogTitle')}</strong></div>
      <div>{t('agentTerminal.urlOpen.dialogMessage')}</div>
      <div className="web-open-url" title={card.url}>{card.url}</div>
      <label className="web-open-remember"><input type="checkbox" checked={remember} onChange={(event) => setRemember(event.target.checked)} />{t('agentTerminal.urlOpen.rememberToggle')}</label>
      <div className="wait-card-footer">
        <span className="card-hint">{t('agentTerminal.urlOpen.fallbackHint', { seconds: 30 })}</span>
        <Pending state={state} showsFailure={false} />
        <button type="button" className="card-button small" disabled={running} onClick={() => choose('external')}>{t('agentTerminal.urlOpen.externalButton')}</button>
        <button type="button" className="card-button small accent" disabled={running} onClick={() => choose('inApp')}>{t('agentTerminal.urlOpen.inAppButton')}</button>
      </div>
    </div>
  );
}

const loginLabels: Record<string, string> = { login: 'loginRecovery.loginButton', cancel: 'loginRecovery.cancel', terminal: 'loginRecovery.terminalButton', resend: 'loginRecovery.resendButton' };

function Login({ card, call }: { card: LoginCard; call: Call }) {
  const { t } = useServices();
  const [code, setCode] = useState('');
  const submit = () => { void call('loginAction', { action: 'code', code }); setCode(''); };
  return (
    <div className="login-card" data-card="login" role="status">
      <span className="login-mark" aria-hidden="true">⚠</span>
      <div className="login-words">
        <div className="login-title">{card.title}</div>
        {card.lines.map((line, index) => (
          <div key={index} className={'login-line tone-' + line.tone}><Pending busy={line.busy} showsLabel={false} />{line.text}</div>
        ))}
        {card.url && <button type="button" className="card-button small" title={card.url} onClick={() => void call('loginAction', { action: 'openLink' })}>{t('loginRecovery.openLink')}</button>}
        {card.asksForCode && (
          <>
            <div className="login-line tone-ink2">{t('loginRecovery.codePrompt')}</div>
            <div className="login-code">
              <input type="password" maxLength={4096} value={code} placeholder={t('loginRecovery.codePlaceholder')} aria-label={t('loginRecovery.codePlaceholder')} onChange={(event) => setCode(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); submit(); } }} />
              <button type="button" className="card-button small" onClick={submit}>{t('loginRecovery.codeSubmit')}</button>
            </div>
          </>
        )}
      </div>
      <div className="login-buttons">
        {card.buttons.map((button) => (
          <button type="button" key={button} className="card-button small" disabled={button === 'login' && !card.loginEnabled} title={button === 'resend' ? t('loginRecovery.resendHelp') : undefined} onClick={() => void call('loginAction', { action: button })}>
            {t(loginLabels[button] ?? button)}
          </button>
        ))}
        <button type="button" className="card-icon-button" aria-label={t('loginRecovery.dismiss')} onClick={() => void call('loginAction', { action: 'dismiss' })}>×</button>
      </div>
    </div>
  );
}

/** The status line (StatusLine.cs): the trust question a workspace's command asks first, then at most six coloured rows. */
function StatusLine({ card, call }: { card: StatusLineCard; call: Call }) {
  return (
    <div className="status-line" data-card="statusLine" aria-label={card.label} title={card.tip}>
      {card.trust && (
        <div className="status-trust">
          <div className="status-trust-question"><span aria-hidden="true">›_</span>{card.trust.prompt}</div>
          <div className="status-trust-command">{card.trust.command}</div>
          <div className="status-trust-answers">
            <button type="button" className="card-button small" onClick={() => void call('statusLineTrust', { allow: true })}>{card.trust.allow}</button>
            <button type="button" className="card-button small" onClick={() => void call('statusLineTrust', { allow: false })}>{card.trust.deny}</button>
            <span className="status-trust-note">{card.trust.note}</span>
          </div>
        </div>
      )}
      {card.lines.map((line, index) => (
        <div className="status-row" key={index}>
          {line.map((segment, at) => (
            <span
              key={at}
              style={{
                color: segment.color ?? (segment.ink === 'ink2' ? 'var(--ink2)' : segment.dim ? 'var(--ink)' : undefined),
                opacity: segment.dim ? 150 / 255 : undefined,
                fontWeight: segment.bold ? 600 : undefined,
                fontStyle: segment.italic ? 'italic' : undefined,
                textDecoration: segment.underline ? 'underline' : undefined,
              }}
            >{segment.text}</span>
          ))}
        </div>
      ))}
      {card.error && <div className="status-row status-error"><span aria-hidden="true">⚠ </span>{card.error}</div>}
    </div>
  );
}

/** The cards inside the composer card: the sign-in card over the toolbar, or the status line under it. */
export function ComposerCard({ paneId, kind }: { paneId: string; kind: 'login' | 'statusLine' }) {
  const { cards, call } = usePaneCards(paneId);
  const card = cards.find((item) => item.kind === kind);
  if (card?.kind === 'login') return <Login card={card} call={call} />;
  if (card?.kind === 'statusLine') return <StatusLine card={card} call={call} />;
  return null;
}
