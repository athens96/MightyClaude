// The role view's own nodes on the page (macOS MightyGraphAgentCard, MightyGraphDelegateLine, MightyGraphProgressPanel, MightyGraphLogStrip): a
// block folded to its card (a sub-agent, a background command, the 실행 block, a step), the delegate line, a request's plan column and the session
// log under the flow. Their words are the host's (Core FlowCopy); what reads the clock or the drawn height is here (flow.ts).
import { memo, useEffect, useState } from 'react';
import { Icon, type IconName } from '../icons/Icon';
import { useServices } from '../app/context';
import { kebab } from '../theme/tokens';
import { dashForCard, dashOffset } from './camera';
import { cardTime, elapsed, LogRows } from './flow';
import type { FlowCardCopy, FlowLineCopy, FlowLogCopy, FlowMarkCopy, FlowProgressCopy, FlowProgressRowCopy } from './types';

/** A design token's colour (Core FlowRoles.Token, a role's or a custom agent's ink). */
export const inkVar = (token: string) => `var(--${kebab(token)})`;

/** The symbol each mark is drawn with (macOS FlowMark.symbol). */
export const markIcon = (name: FlowMarkCopy['name']): IconName => (name === 'background' ? 'square-stack' : name === 'fork' ? 'copy' : 'git-branch');

/** The status tone a card's pill and figures take (macOS DesignTone(blockStatus:)); a step not begun is idle. */
const tone = (card: FlowCardCopy) => {
  if (card.pending) return 'idle';
  const status = card.status;
  return status === 'completed' ? 'done' : status === 'error' ? 'err' : status === 'stopped' ? 'stop' : status === 'waiting' ? 'wait' : 'run';
};

/** The running edge over a fixed node: the blocks' marching dashes (Block.tsx Outline). */
function Marching({ w, h, radius = 11 }: { w: number; h: number; radius?: number }) {
  if (w <= 2 || h <= 2) return null;
  const dash = dashForCard(w, h);
  const period = -dashOffset(1, dash);
  return (
    <svg className="mighty-outline outline-marching" width={w} height={h} aria-hidden="true" data-outline="marching">
      <rect x={1} y={1} width={w - 2} height={h - 2} rx={radius} ry={radius} style={{ strokeDasharray: dash.join(' '), '--march': `${-period}` } as React.CSSProperties} />
    </svg>
  );
}

/** The clock a running card's time reads, once a second while it runs; any other card reads it once. */
function useNow(running: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!running) return undefined;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);
  return now;
}

/** The card's second line: who it is (a command: its command line, set as code), then its figures — a command's exit code, the time read against the
 * page's clock, the tools and the tokens; the state is the header's pill. Only this line reads the clock. */
function CardFigures({ card, nodeId }: { card: FlowCardCopy; nodeId: string }) {
  const { t } = useServices();
  const now = useNow(card.running && card.startedAt !== undefined && !card.pending);
  const time = cardTime(t, card, now);
  const figures = [...card.before.slice(1), ...(time === null ? [] : [time]), ...card.after].join(' · ');
  return (
    <div className="flow-card-figures" data-testid={'mighty-card-figures-' + nodeId}>
      {card.identity && (
        <span className={'flow-card-identity' + (card.isTask ? ' code' : '')} style={card.isTask ? undefined : { color: inkVar(card.ink) }} title={card.identity}
          data-testid={'mighty-card-identity-' + nodeId}>{card.identity}</span>
      )}
      {card.identity && figures !== '' && <span className="flow-dot-sep">·</span>}
      {figures !== '' && <span className="flow-card-figures-text">{figures}</span>}
    </div>
  );
}

/**
 * A block of the role view folded to its card (FlowCard): a sub-agent, a background command, the 실행 block or a step. It wears the full block's frame
 * — the same header with its glyph in the role's ink, title, nested count, marks, status pill and open button, the same corners — and holds two lines
 * under it: what it is doing (or did) now, and who it is with its figures. A click goes to a finished background block's copy, else opens the block in
 * place; the open button always opens it in place. Never selected or resized: a wheel over it pans.
 */
export const FlowCardNode = memo(function FlowCardNode({ id, kind = 'agent', icon, x, y, w, h, card, outline, onActivate, onOpen }: {
  id: string; kind?: string; icon: IconName; x: number; y: number; w: number; h: number; card: FlowCardCopy; outline: boolean;
  onActivate: (id: string, copy: string | undefined) => void; onOpen: (id: string) => void;
}) {
  const { t } = useServices();
  const ink = inkVar(card.ink);
  const shade = tone(card);
  // What the main session said reads as text in the card's own ink; a command is code, quieter; a step not begun says so.
  const activity = card.activity ?? (card.pending ? card.stateWord : undefined);
  return (
    <div className={'mighty-flow-card tone-' + shade + (card.pending ? ' pending' : '')} data-block-id={id} data-kind={kind} data-fixed="1" data-testid={'mighty-node-' + id}
      role="button" tabIndex={-1} data-pending={card.pending ? '1' : undefined}
      style={{ left: x, top: y, width: w, height: h }} title={card.summary ? card.summary + '\n' + card.activateHelp : card.activateHelp}
      aria-label={[card.title, card.stateWord, card.identity, card.activity].filter(Boolean).join(', ')}
      onClick={(event) => { if (!(event.target as Element).closest('button')) onActivate(id, card.copyNodeId); }}>
      <div className="mighty-head flow-card-head">
        <span className="mighty-symbol" style={{ color: ink }} aria-hidden="true"><Icon name={icon} size={14} weight={1.75} /></span>
        <span className="mighty-title flow-card-title">{card.title}</span>
        {card.nested > 0 && <span className="flow-card-nested" title={card.nestedHelp} aria-label={card.nestedHelp} data-testid={'mighty-card-nested-' + id}>(+{card.nested})</span>}
        {card.marks.map((mark) => <span key={mark.name} className="flow-mark" title={mark.help} aria-label={mark.help}><Icon name={markIcon(mark.name)} size={10} weight={2} /></span>)}
        <span className="flow-head-spacer" />
        <span className="mighty-controls">
          <span className={'mighty-pill tone-' + shade} data-testid={'mighty-card-state-' + id}>{card.stateWord}</span>
          <button type="button" className="mighty-icon flow-card-open" title={t('graph.card.open')} aria-label={t('graph.card.open')} data-testid={'mighty-expand-' + id}
            onClick={() => onOpen(id)}><Icon name="unfold-vertical" size={14} weight={1.75} /></button>
        </span>
      </div>
      <div className="flow-card-body">
        {/* The step of the request's plan it was launched in: what it is part of. */}
        {card.step && <div className="flow-card-step" title={card.stepHelp ?? card.step} data-testid={'mighty-card-step-' + id}>
          <Icon name="corner-down-right" size={10} weight={2} /><span>{card.step}</span>
        </div>}
        <div className={'flow-card-activity' + (card.prose || card.activity === undefined ? ' prose' : '')} title={card.activity} data-testid={'mighty-card-activity-' + id}>{activity ?? ' '}</div>
        <CardFigures card={card} nodeId={id} />
      </div>
      {outline && !card.pending && <Marching w={w} h={h} />}
    </div>
  );
});

/** The line on the branch to a block's row (FlowDelegation): the count in ink, each kind in its ink, the model in the sub-agent ink, how many still run in the run blue. A label, not a block. */
export const DelegateLineNode = memo(function DelegateLineNode({ id, x, y, w, h, line }: { id: string; x: number; y: number; w: number; h: number; line: FlowLineCopy }) {
  return (
    <div className="mighty-flow-line" data-block-id={id} data-kind="delegate" data-fixed="1" data-testid={'mighty-node-' + id} title={line.text} aria-label={line.text}
      style={{ left: x, top: y, width: w, height: h }}>
      <span className="flow-line-icon" aria-hidden="true"><Icon name="corner-down-right" size={10} weight={2} /></span>
      <span className="flow-line-head">{line.head}</span>
      {line.parts.map((part, index) => <span key={index} className="flow-line-part"><span className="flow-dot-sep">·</span><span style={{ color: inkVar(part.ink) }}>{part.text}</span></span>)}
      {line.model && <span className="flow-line-part"><span className="flow-dot-sep">·</span><span className="flow-line-model">{line.model}</span></span>}
      {line.progress && <span className="flow-line-part"><span className="flow-dot-sep">·</span><span className="flow-line-progress">{line.progress}</span></span>}
    </div>
  );
});

/** A step's time in the progress panel: how long it took once it ended, the time since it began while it runs (read once a second), nothing for a
 * step not begun. */
function ProgressTime({ startedAt, endedAt, ticking }: { startedAt?: number; endedAt?: number; ticking: boolean }) {
  const { t } = useServices();
  const live = ticking && endedAt === undefined && startedAt !== undefined;
  const now = useNow(live);
  if (startedAt === undefined) return null;
  if (live) return <span className="flow-outline-time">{elapsed(t, startedAt, now)}</span>;
  if (endedAt !== undefined && endedAt >= startedAt) return <span className="flow-outline-time">{elapsed(t, startedAt, endedAt)}</span>;
  return null;
}

const progressMark: Record<FlowProgressRowCopy['state'], IconName> = { done: 'circle-check', current: 'circle-dot', pending: 'circle', stopped: 'circle-stop', failed: 'circle-x' };
const statusTone = (status: string) => (status === 'running' ? 'run' : status === 'completed' ? 'done' : status === 'error' ? 'err' : status === 'stopped' ? 'stop' : status === 'waiting' ? 'wait' : 'idle');

/** Panes whose progress panel is folded: per pane, for as long as the page runs (never saved). */
const progressFolded = new Set<string>();

/**
 * The role view's progress panel (macOS MightyGraphProgressPanel, Core FlowSteps.Progress), over the diagram's top-left corner where it stays while
 * the diagram scrolls under it: the request in view ("요청 27" and its first line) with its state, then its plan — every step with its mark (done, the
 * one in progress with its time running, not begun) and the time each took, then the background work it started (commands and sub-agents, running
 * ones first, with their time). The request line leads to the request; a step that began leads to its card. Folded, it keeps its header and the
 * request line. A press or a wheel in it never pans the diagram.
 */
export function ProgressPanel({ paneId, progress, onReveal }: { paneId: string; progress: FlowProgressCopy; onReveal: (nodeId: string) => void }) {
  const { t } = useServices();
  const [collapsed, setCollapsed] = useState(() => progressFolded.has(paneId));
  const toggle = () => {
    const next = !collapsed;
    if (next) progressFolded.add(paneId); else progressFolded.delete(paneId);
    setCollapsed(next);
  };
  const label = t(collapsed ? 'graph.steps.expand' : 'graph.steps.collapse');
  const background = progress.background ?? [];
  const backgroundHidden = progress.backgroundHidden ?? 0;
  return (
    <section className="mighty-progress" data-testid="mighty-progress" data-collapsed={collapsed ? '' : undefined} aria-label={progress.title + ' ' + progress.heading}
      onPointerDown={(event) => event.stopPropagation()}>
      <div className="flow-outline-head">
        <span className="flow-outline-glyph" aria-hidden="true"><Icon name="clipboard-list" size={14} weight={1.75} /></span>
        <span className="flow-outline-title">{progress.title}</span>
        {progress.count && <span className="flow-outline-progress" data-testid="mighty-progress-count">{progress.count}</span>}
        <span className="flow-head-spacer" />
        <button className="mighty-icon" title={label} aria-label={label} aria-expanded={!collapsed} onClick={toggle} data-testid="mighty-progress-fold">
          <Icon name={collapsed ? 'chevron-down' : 'chevron-up'} size={12} weight={2} />
        </button>
      </div>
      <button type="button" className="mighty-progress-request" title={progress.requestHelp} data-testid="mighty-progress-request" onClick={() => onReveal(progress.requestNodeId)}>
        <span className="mighty-progress-heading">
          <span className="mighty-progress-ordinal">{progress.heading}</span>
          <span className={'mighty-pill tone-' + statusTone(progress.status)}>{progress.stateWord}</span>
        </span>
        {progress.request !== '' && <span className="mighty-progress-text">{progress.request}</span>}
      </button>
      {!collapsed && progress.rows.length > 0 && (
        <div className="flow-outline-rows mighty-progress-rows">
          {progress.rows.map((row) => (
            <button key={row.index} type="button" className={'flow-outline-row state-' + row.state} title={row.text} disabled={row.nodeId === undefined}
              data-testid={'mighty-progress-row-' + row.index} onClick={() => { if (row.nodeId) onReveal(row.nodeId); }}>
              <span className={'flow-outline-mark state-' + row.state} aria-hidden="true"><Icon name={progressMark[row.state]} size={12} weight={2} /></span>
              <span className="flow-outline-text">{row.text}</span>
              <span className="flow-head-spacer" />
              <ProgressTime startedAt={row.startedAt} endedAt={row.endedAt} ticking={row.state === 'current' && progress.running} />
            </button>
          ))}
          {progress.hidden > 0 && progress.more && <div className="flow-outline-more" data-testid="mighty-progress-more">{progress.more}</div>}
        </div>
      )}
      {!collapsed && background.length > 0 && (
        <div className="flow-outline-rows mighty-progress-background" data-testid="mighty-progress-background">
          <div className="mighty-progress-background-head">
            <span className="mighty-progress-background-title">{t('graph.progress.background')}</span>
            <span className="mighty-progress-background-count" data-testid="mighty-progress-background-count">
              {background.filter((item) => item.state === 'current').length + '/' + (background.length + backgroundHidden)}
            </span>
          </div>
          {background.map((item) => (
            <div key={item.id} className={'flow-outline-row state-' + item.state} title={item.text} data-testid={'mighty-progress-background-' + item.id}>
              <span className={'flow-outline-mark state-' + item.state} aria-hidden="true"><Icon name={progressMark[item.state]} size={12} weight={2} /></span>
              <span className="mighty-progress-background-kind" aria-hidden="true"><Icon name={item.agent ? 'square-user-round' : 'terminal'} size={11} weight={1.75} /></span>
              <span className="flow-outline-text">{item.text}</span>
              <span className="flow-head-spacer" />
              <ProgressTime startedAt={item.startedAt} endedAt={item.endedAt} ticking={item.state === 'current'} />
            </div>
          ))}
          {backgroundHidden > 0 && <div className="flow-outline-more" data-testid="mighty-progress-background-more">{t('graph.progress.more', { count: backgroundHidden })}</div>}
        </div>
      )}
    </section>
  );
}

/**
 * The session log under a Claude pane's flow (FlowLog): its header is the status line, each part in its role's ink, and while open the last few
 * events with their time, who and what, in the room of five rows so the diagram over it does not move as rows come in. A row leads to its block.
 */
/** The session log under the flow. `squeezed`: the pane is too short for its rows beside the diagram (flow.ts logRowsFit), so it shows its
 * status line alone and offers no fold of its own; the pane's fold comes back with the room. */
export function FlowLogStrip({ paneId, log, squeezed = false, onToggle, onReveal }: { paneId: string; log: FlowLogCopy; squeezed?: boolean; onToggle: () => void; onReveal: (nodeId: string) => void }) {
  const { t } = useServices();
  const open = !log.collapsed && !squeezed;
  return (
    <div className="mighty-log" data-testid={'mighty-log-' + paneId} data-squeezed={squeezed ? '1' : undefined}>
      <div className="mighty-log-head">
        <Icon name="list" size={12} weight={1.75} className="mighty-log-glyph" />
        <span className="mighty-log-title">{t('graph.log.title')}</span>
        <span className="mighty-log-status" title={log.statusText} aria-label={log.statusText} data-testid={'mighty-log-status-' + paneId}>
          {log.status.map((part, index) => <span key={index}>{index > 0 && <span className="flow-dot-sep"> · </span>}<span style={{ color: inkVar(part.ink) }}>{part.text}</span></span>)}
        </span>
        {!squeezed && <button type="button" className="mighty-icon mighty-log-toggle" title={t(open ? 'graph.log.hide' : 'graph.log.show')} aria-label={t(open ? 'graph.log.hide' : 'graph.log.show')}
          aria-expanded={open} data-testid={'mighty-log-toggle-' + paneId} onClick={onToggle}><Icon name={open ? 'chevron-down' : 'chevron-up'} size={12} weight={2} /></button>}
      </div>
      {open && (
        <div className="mighty-log-rows" style={{ minHeight: `calc(${LogRows} * var(--log-row, 18px))` }}>
          {log.rows.length === 0 && <div className="mighty-log-empty" data-testid={'mighty-log-empty-' + paneId}>{t('graph.log.empty')}</div>}
          {log.rows.map((row) => (
            <button key={row.id} type="button" className="mighty-log-row" title={row.what + '\n' + t('graph.log.reveal')} data-testid={'mighty-log-row-' + row.id}
              aria-label={[row.clock, row.who, row.what].join(', ')} onClick={() => onReveal(row.nodeId)}>
              <span className="mighty-log-clock">{row.clock}</span>
              <span className="mighty-log-who" style={{ color: inkVar(row.ink) }}>{row.who}</span>
              <span className="mighty-log-what">{row.what}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
