// The role view's own nodes on the page (macOS MightyGraphAgentCard, MightyGraphDelegateLine, MightyGraphReturnNode, MightyGraphAdvisorBand,
// MightyGraphLogStrip): a running sub-agent's or a background command's card, the delegate line, the return node, the advisor's band beside a run
// and the session log under the flow. Their words are the host's (Core FlowCopy); what reads the clock or the drawn height is here (flow.ts).
import { memo, useEffect, useState } from 'react';
import { Icon, type IconName } from '../icons/Icon';
import { useServices } from '../app/context';
import { kebab } from '../theme/tokens';
import { dashForCard, dashOffset } from './camera';
import { advisorVisible, cardFigures, LogRows } from './flow';
import type { FlowAdvisorCopy, FlowBackCopy, FlowCardCopy, FlowLineCopy, FlowLogCopy, FlowMarkCopy } from './types';

/** A design token's colour (Core FlowRoles.Token, a role's or a custom agent's ink). */
export const inkVar = (token: string) => `var(--${kebab(token)})`;

/** The symbol each mark is drawn with (macOS FlowMark.symbol). */
export const markIcon = (name: FlowMarkCopy['name']): IconName => (name === 'background' ? 'square-stack' : name === 'fork' ? 'copy' : 'git-branch');

/** The status tone a card's dot and figures take (macOS DesignTone(blockStatus:)). */
const tone = (status: string) => (status === 'completed' ? 'done' : status === 'error' ? 'err' : status === 'stopped' ? 'stop' : status === 'waiting' ? 'wait' : 'run');

/** The running edge over a fixed node: the blocks' marching dashes (Block.tsx Outline). */
function Marching({ w, h, radius = 10 }: { w: number; h: number; radius?: number }) {
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

/** The card's last line: its state and its figures. Only this line reads the clock, so the time that passes draws nothing else. */
function CardFigures({ card, nodeId }: { card: FlowCardCopy; nodeId: string }) {
  const { t } = useServices();
  const now = useNow(card.running && card.startedAt !== undefined);
  const shade = tone(card.status);
  return (
    <div className={'flow-card-figures tone-' + shade} data-testid={'mighty-card-figures-' + nodeId}>
      <span className={'flow-card-dot tone-' + shade} aria-hidden="true" />
      <span className="flow-card-figures-text">{cardFigures(t, card, now)}</span>
    </div>
  );
}

/**
 * A running sub-agent's, a background sub-agent's or a background command's compact card (FlowCard): its name, who it is (a command's line set as
 * code), what it is doing now, its state and figures. A click goes to a finished background block's copy, else opens the block in place; the
 * expand button always opens it in place. Never selected or resized: a wheel over it pans.
 */
export const FlowCardNode = memo(function FlowCardNode({ id, x, y, w, h, card, outline, onActivate, onOpen }: {
  id: string; x: number; y: number; w: number; h: number; card: FlowCardCopy; outline: boolean;
  onActivate: (id: string, copy: string | undefined) => void; onOpen: (id: string) => void;
}) {
  const { t } = useServices();
  const ink = inkVar(card.ink);
  return (
    <div className="mighty-flow-card" data-block-id={id} data-kind="agent" data-fixed="1" data-testid={'mighty-node-' + id} role="button" tabIndex={-1}
      style={{ left: x, top: y, width: w, height: h }} title={card.summary ? card.summary + '\n' + card.activateHelp : card.activateHelp}
      aria-label={[card.title, card.identity, card.activity].filter(Boolean).join(', ')}
      onClick={(event) => { if (!(event.target as Element).closest('button')) onActivate(id, card.copyNodeId); }}>
      <span className="flow-bar" style={{ background: ink }} aria-hidden="true" />
      <div className="flow-card-body">
        <div className="flow-card-head">
          <span className="flow-card-title">{card.title}</span>
          {card.nested > 0 && <span className="flow-card-nested" title={card.nestedHelp} aria-label={card.nestedHelp} data-testid={'mighty-card-nested-' + id}>(+{card.nested})</span>}
          <span className="flow-card-spacer" />
          {card.marks.map((mark) => <span key={mark.name} className="flow-mark" title={mark.help} aria-label={mark.help}><Icon name={markIcon(mark.name)} size={10} weight={2} /></span>)}
          <button type="button" className="mighty-icon flow-card-open" title={t('graph.card.open')} aria-label={t('graph.card.open')} data-testid={'mighty-expand-' + id}
            onClick={() => onOpen(id)}><Icon name="unfold-vertical" size={12} weight={1.75} /></button>
        </div>
        {card.identity && (
          // A command's line is code, in its own quiet ink; an agent's is who it is, in its role's.
          <div className={'flow-card-identity' + (card.isTask ? ' code' : '')} style={card.isTask ? undefined : { color: ink }} title={card.identity} data-testid={'mighty-card-identity-' + id}>{card.identity}</div>
        )}
        <div className="flow-card-activity" title={card.activity} data-testid={'mighty-card-activity-' + id}>{card.activity ?? ' '}</div>
        <span className="flow-card-spacer" />
        <CardFigures card={card} nodeId={id} />
      </div>
      {outline && <Marching w={w} h={h} />}
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

/** The return node where the result will go (FlowReturn): the lead's ink and the running edge, since it stands only while the run goes on. */
export const ReturnNode = memo(function ReturnNode({ id, x, y, w, h, back }: { id: string; x: number; y: number; w: number; h: number; back: FlowBackCopy }) {
  return (
    <div className="mighty-flow-return" data-block-id={id} data-kind="return" data-fixed="1" data-testid={'mighty-node-' + id} title={back.text} aria-label={back.text}
      style={{ left: x, top: y, width: w, height: h }}>
      <Icon name={back.waiting ? 'hourglass' : 'undo-2'} size={11} weight={2} />
      <span className="flow-return-text">{back.text}</span>
      <Marching w={w} h={h} />
    </div>
  );
});

/**
 * The advisor's band beside a run (FlowAdvisor): its header (the advisor, its model, what it is doing), a row for each consultation that fits (who
 * asked in its ink, when and how long, where in the caller's work and how it ended), the line about replies with no text, and the totals. A row's
 * click goes to the block that asked. No advice is shown that the CLI did not give as text: the row's help holds it when it did.
 */
export const AdvisorBandNode = memo(function AdvisorBandNode({ id, x, y, w, h, band, onReveal }: {
  id: string; x: number; y: number; w: number; h: number; band: FlowAdvisorCopy; onReveal: (nodeId: string) => void;
}) {
  const { t } = useServices();
  const shown = advisorVisible(band, h);
  return (
    <div className="mighty-flow-advisor" data-block-id={id} data-kind="advisor" data-fixed="1" data-testid={'mighty-node-' + id} aria-label={band.header + ', ' + band.totals}
      style={{ left: x, top: y, width: w, height: h }}>
      <span className="flow-bar flow-bar-advisor" aria-hidden="true" />
      <div className="flow-advisor-body">
        <div className="flow-advisor-head" title={band.header} data-testid={'mighty-advisor-header-' + id}>
          <Icon name="lightbulb" size={11} weight={2} className="flow-advisor-bulb" />
          <span className="flow-advisor-name">{t('graph.identity.role.advisor')}</span>
          {band.model && <span className="flow-advisor-model">{band.model}</span>}
          <span className="flow-card-spacer" />
          {band.state && <span className={'flow-advisor-state' + (band.consulting ? ' consulting' : '')}>{band.state}</span>}
        </div>
        {band.empty && <div className="flow-advisor-empty" data-testid={'mighty-advisor-empty-' + id}>{band.empty}</div>}
        {shown.earlier > 0 && <div className="flow-advisor-earlier" data-testid={'mighty-advisor-earlier-' + id}>{t('graph.advisor.earlier', { count: shown.earlier })}</div>}
        {shown.rows.map((row) => (
          <button key={row.id} type="button" className="flow-advisor-row" title={row.help} data-testid={'mighty-advisor-row-' + row.id}
            aria-label={[row.caller, row.when, row.line].filter((s) => s !== '').join(', ')} onClick={() => onReveal(row.callerNodeId)}>
            <span className="flow-advisor-row-head">
              <span className="flow-advisor-caller" style={{ color: inkVar(row.ink) }}>{row.caller}</span>
              <span className="flow-card-spacer" />
              <span className="flow-advisor-when">{row.when}</span>
            </span>
            <span className={'flow-advisor-line state-' + row.state}>{row.line}</span>
          </button>
        ))}
        <span className="flow-card-spacer" />
        {band.note && <div className="flow-advisor-note" data-testid={'mighty-advisor-note-' + id}>{band.note}</div>}
        <div className="flow-advisor-totals" title={band.totalsHelp ?? band.totals} data-testid={'mighty-advisor-totals-' + id}>{band.totals}</div>
      </div>
      {band.consulting && <Marching w={w} h={h} />}
    </div>
  );
});

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
