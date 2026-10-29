import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useServices } from '../app/context';
import { Entry } from '../conversation/Entry';
import { Markdown } from '../conversation/MarkdownView';
import type { LogEntry, MightyGraphRun, RunSession } from '../store/models';
import type { TimelineGroup, TimelineRow } from './types';
import { useReferenceClick } from '../conversation/referenceClick';
import { Icon, type IconName } from '../icons/Icon';
import { ProviderMark } from '../conversation/ProviderMark';
import { providerLabel } from '../conversation/format';
import { Pending } from '../pending/Pending';
import type { PendingState } from '../pending/display';
import { holding, pendingRunId, requestState, runsFor, type PendingRequest } from '../pending/requests';
import { quietNotice } from './notice';
import { timelineResultTitle } from './resultTitle';
import { animateScrollTop, isNearBottom, submitScroll } from '../pending/submitScroll';
import { Motion, reducesMotion } from '../theme/tokens';

/** A row's glyph, as the diagram gives its block (M/MightyGraphView.swift timelineLook): the request's arrow, 실행's gears, a task's terminal… */
function rowSymbol(kind: string): IconName {
  switch (kind) {
    case 'main': return 'circle-arrow-up';
    case 'execution': return 'cog';
    case 'task': return 'square-terminal';
    case 'steer': return 'message-square-text';
    case 'compact': return 'shrink';
    case 'question': return 'message-circle-question-mark';
    default: return 'square-user-round';
  }
}

/** The tint of a row's kind, the diagram's (the request in the accent, 실행 and agents in the agent ink). */
function rowTint(kind: string): string {
  switch (kind) {
    case 'main': return 'accent';
    case 'task': return 'task';
    case 'steer': return 'steer';
    case 'compact': return 'compact';
    case 'question': return 'question';
    default: return 'agent';
  }
}

/** "요청 1 · Claude" with the provider's mark before its name (ProviderBadgeIcon.labelled). */
function RequestTitle({ title, provider }: { title: string; provider: string }) {
  const label = providerLabel(provider);
  const at = title.lastIndexOf(label);
  if (at < 0) return <>{title}</>;
  return <>{title.slice(0, at)}<span className="timeline-mark"><ProviderMark provider={provider} size={15} /></span>{title.slice(at)}</>;
}

/** A row's transcript, newest entries, drawn only while the row is open (UI/MightyTimeline.cs: a 260-tall body). */
function RowBody({ entries, paneId, provider, input }: { entries: readonly LogEntry[]; paneId: string; provider: string; input?: string }) {
  const shown = useMemo(() => entries.filter((e) => e.kind !== 'user').slice(-40), [entries]);
  const toggle = useMemo(() => () => {}, []);
  const onClick = useReferenceClick();
  return (
    <div className="timeline-body" onClick={onClick}>
      {input && <div className="mighty-band">{input}</div>}
      <div className="timeline-transcript">
        {shown.map((entry) => <Entry key={entry.id} entry={entry} paneId={paneId} provider={provider} shell={false} live={false} expanded={false} onToggle={toggle} />)}
      </div>
    </div>
  );
}

/**
 * One block on the timeline (M/MightyGraphTimelineView.swift MightyTimelineRowCard, MightyTimelineMarker): a 32-wide rail with a
 * 24pt disc in the node's fill holding the kind's glyph, the rail on under it in the next tone; the white card at radius 14 with
 * the title (13 bold), the glyph in its tint and "kind · capsule · duration" (11), the latest line in mono while it runs, the pill.
 */
const Row = memo(function Row({ row, run, paneId, provider, open, onToggle, first, last, above }: {
  row: TimelineRow; run: MightyGraphRun | undefined; paneId: string; provider: string; open: boolean; onToggle: (id: string) => void; first: boolean; last: boolean; above?: string;
}) {
  const entries = row.agentIndex !== undefined ? run?.agents[row.agentIndex]?.entries : row.kind === 'execution' ? run?.rootEntries : undefined;
  const meta = [row.kindLabel, row.capsule, row.duration].filter(Boolean).join(' · ');
  const symbol = rowSymbol(row.kind);
  return (
    <div className={'timeline-row tone-' + row.tone} data-node-id={row.nodeId}>
      <div className="timeline-rail" aria-hidden="true">
        {!first && <span className={'timeline-line above tone-' + (above ?? 'none')} />}
        {!last && <span className={'timeline-line below tone-' + (row.rail ?? 'none')} />}
        <span className={'timeline-node tone-' + row.tone + (row.ring ? ' ring' : '')}><Icon name={symbol} size={11} weight={2.4} /></span>
      </div>
      <div className={'timeline-card' + (row.tone === 'run' ? ' edge-run' : row.tone === 'wait' ? ' edge-wait' : '')}>
        <button className="timeline-card-head" onClick={() => onToggle(row.nodeId)} disabled={!entries && !row.input}>
          <span className="timeline-card-words">
            <span className="timeline-title">{row.title}</span>
            <span className="timeline-meta"><span className={'timeline-kind tint-' + rowTint(row.kind)}><Icon name={symbol} size={10} weight={2.4} /></span>{meta}</span>
            {row.latest && <span className="timeline-latest">{row.tone === 'run' && <span className="timeline-pulse" />}<span>{row.latest}</span></span>}
          </span>
          {row.statusWord && <span className={'mighty-pill tone-' + row.tone}>{row.statusWord}</span>}
        </button>
        {open && entries && <RowBody entries={entries} paneId={paneId} provider={provider} input={row.input} />}
      </div>
    </div>
  );
});

const Group = memo(function Group({ group, run, paneId, provider, onOpenRow, pending }: {
  group: TimelineGroup; run: MightyGraphRun | undefined; paneId: string; provider: string; onOpenRow?: (nodeId: string) => void;
  /** A sent request's group before the host's: its 전송 중 (or failure) in place of the pill, and nothing to open yet. */
  pending?: PendingState;
}) {
  const { bridge, t } = useServices();
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set());
  const [full, setFull] = useState(false);
  // Opening a row aims the diagram at its block (MainWindow.MightyTimeline.cs ToggleTimelineRow).
  const toggle = useMemo(() => (id: string) => setOpen((s) => {
    const n = new Set(s);
    if (!n.delete(id)) { n.add(id); onOpenRow?.(id); }
    return n;
  }), [onOpenRow]);
  const prompt = group.input.split(/\r?\n/).join(' ').trim();
  const resultTone = group.resultTone ?? 'done';
  // M/MightyGraphView.swift timelineGroup: the header, the rows while the request is open, and its result card either way.
  return (
    <section className="timeline-group" data-run-id={group.runId} data-testid="timeline-group" data-pending-request={pending ? group.runId : undefined}>
      <button type="button" className="timeline-head" aria-expanded={group.open} disabled={pending !== undefined}
        onClick={() => void bridge.call('mightyTimelineFlip', { paneId, runId: group.runId })}>
        <span className="timeline-head-line">
          <span className="timeline-chevron"><Icon name={group.open ? 'chevron-down' : 'chevron-right'} size={10} weight={3} /></span>
          <span className="timeline-group-title"><RequestTitle title={group.title} provider={provider} /></span>
          {group.older && <span className="timeline-older" title={t('graph.history.tag')}><Icon name="clock-fading" size={11} weight={2} /></span>}
          <span className="spacer" />
          {pending ? <Pending state={pending} /> : <span className={'mighty-pill tall tone-' + group.tone}>{group.statusWord}</span>}
        </span>
        {prompt && <span className="timeline-prompt">{prompt}</span>}
        <span className="timeline-tally">{group.tally}</span>
      </button>
      {group.open && (
        <div className="timeline-rows">
          {group.rows.map((row, index) => (
            <Row key={row.nodeId} row={row} run={run} paneId={paneId} provider={provider} open={open.has(row.nodeId)} onToggle={toggle}
              first={index === 0} last={index === group.rows.length - 1} above={index > 0 ? group.rows[index - 1]?.rail : undefined} />
          ))}
        </div>
      )}
      {group.result !== undefined && (
        <div className={'timeline-result tone-' + resultTone} data-node-id={group.resultNodeId}>
          <div className="timeline-result-head">
            <Icon name={resultTone === 'err' ? 'triangle-alert' : resultTone === 'stop' ? 'circle-stop' : 'badge-check'} size={12} weight={2.4} />
            <span className="timeline-result-title">{timelineResultTitle(group, t)}</span>
            <span className="spacer" />
            {group.resultCaption && <span className="timeline-result-caption">{group.resultCaption}</span>}
          </div>
          {group.result && (
            <div className="timeline-result-body">
              <div className={'timeline-result-text' + (group.resultFolds && !full ? ' folded' : '')}>{group.result}</div>
              {group.resultFolds && <button className="mighty-link" onClick={() => setFull((f) => !f)}>{t(full ? 'phone.blocks.resultLess' : 'phone.blocks.resultMore')}</button>}
            </div>
          )}
        </div>
      )}
    </section>
  );
});

const terminalStatus = new Set(['completed', 'error', 'stopped']);

/**
 * The 로그 view (UI/MightyTimeline.cs): the host's groups, one per request, newest at the bottom. It follows the bottom when
 * it first draws, when a new request arrives and when a request finishes; any other change (a row opened, older requests read
 * in at the top, a streamed line) keeps the first group in view where it was (anchor restore). The top reads older requests.
 */
export function Timeline({ session, groups: hostGroups, older, history, onOpenRow, pending: sent, own = 0 }: {
  session: RunSession; groups: readonly TimelineGroup[] | null; older: readonly MightyGraphRun[]; history?: { text: string; acts: boolean } | undefined; onOpenRow?: (nodeId: string) => void;
  /** The request the composer sent that the host's group has not replaced yet: drawn as the newest group. */
  pending?: PendingRequest;
  /** Counts the pane's own sends: each scrolls to the last group whatever the position (natural submit B2). */
  own?: number;
}) {
  const { bridge, t } = useServices();
  const scroller = useRef<HTMLDivElement>(null);
  const runs = useMemo(() => new Map([...older, ...(session.graphRuns ?? [])].map((r) => [r.id, r])), [older, session.graphRuns]);
  // The stand-in group stays until the host's groups hold the request's run (they come a moment after the run itself).
  const arrived = sent && sent.phase !== 'failed' ? runsFor(sent, session) : [];
  const held = arrived.length > 0 && (hostGroups ?? []).some((group) => arrived.includes(group.runId));
  // One whose 전송 중 has shown keeps its shortest time in place of the host's group, which waits meanwhile.
  const pending = sent && (!held || holding(sent)) ? sent : undefined;
  const heldBack = pending && held ? arrived.join('\n') : '';
  const pendingId = pending ? pendingRunId(pending) : null;
  const pendingState = useMemo(() => (pending ? requestState(pending) : undefined), [pending]);
  const groups = useMemo(() => {
    if (!pending) return hostGroups;
    const shownGroups = heldBack ? (hostGroups ?? []).filter((group) => !heldBack.split('\n').includes(group.runId)) : hostGroups;
    const failed = pending.phase === 'failed';
    const group: TimelineGroup = {
      runId: pendingRunId(pending), ordinal: (shownGroups ?? []).filter((group) => group.noticeRun !== true).length + 1, title: t('graph.block.nextRequest'), status: failed ? 'error' : 'starting',
      statusWord: '', tone: failed ? 'err' : 'run', input: pending.text, tally: '', open: false, older: false, rows: [], resultFolds: false,
    };
    return [...(shownGroups ?? []), group];
  }, [hostGroups, pending, heldBack, t]);
  const count = groups?.length ?? 0;
  /** The view stood at its newest end before this draw (32 px), as the not-own follow rule reads it. */
  const atEnd = useRef(true);
  const ownSeen = useRef(own);
  const moving = useRef<(() => void) | null>(null);
  useEffect(() => () => moving.current?.(), []);
  /** The reader's own wheel or drag stops a move to the last group (submitScroll.ts animateScrollTop). */
  const stopMoving = () => { moving.current?.(); moving.current = null; };
  const progress = useRef<{ last: string | null; finished: ReadonlyMap<string, boolean> } | null>(null);
  const anchor = useRef<{ runId: string; offset: number } | null>(null);
  const restoring = useRef(false);
  const armed = useRef(false);

  /** The first group whose bottom is in view, and how far its top stands from the view's top. */
  const remember = () => {
    const element = scroller.current;
    if (!element) return;
    const top = element.getBoundingClientRect().top;
    const first = [...element.querySelectorAll<HTMLElement>('[data-run-id]')].find((g) => g.getBoundingClientRect().bottom >= top);
    anchor.current = first ? { runId: first.dataset.runId ?? '', offset: first.getBoundingClientRect().top - top } : null;
  };

  useLayoutEffect(() => {
    const element = scroller.current;
    if (!element) return;
    const list = groups ?? [];
    // A request's result is revealed as it finishes; a notice run's never steals the view (notice.ts).
    const finished = new Map(list.filter((g) => g.noticeRun !== true).map((g) => [g.runId, terminalStatus.has(g.status)]));
    // The newest work: the last request, or a notice run after it with a reply or still running, and whether it is answered,
    // followed only from the newest end; a quiet notice run (taken into a running turn) never moves the view.
    const followed = [...list].reverse().find((g) => { const run = runs.get(g.runId); return !run || !quietNotice(run); });
    const last = followed ? followed.runId + (followed.noticeRun && followed.resultNodeId ? ':answered' : '') : null;
    const before = progress.current;
    const newlyFinished = !!before && list.some((g) => finished.get(g.runId) && before.finished.get(g.runId) === false);
    // An own send (this composer's Enter, or its group taking the place of the one the page drew) goes to the last group
    // whatever the position, gliding over motion.scroll; anyone else's new request is followed only from the newest end.
    const newRequest = before !== null && last !== before.last;
    const ownSend = own !== ownSeen.current || (newRequest && (last === pendingId || before?.last?.startsWith('pending-') === true));
    ownSeen.current = own;
    const decision = submitScroll('timeline', ownSend, true, atEnd.current);
    const follow = before === null || newlyFinished || (newRequest && decision.scroll === 'follow') || decision.scroll === 'toRequest';
    progress.current = { last, finished };
    restoring.current = true;
    if (decision.scroll === 'toRequest' && before !== null) {
      moving.current?.();
      moving.current = animateScrollTop(element, () => element.scrollHeight - element.clientHeight, Motion.scroll, reducesMotion());
      atEnd.current = true;
    } else if (follow) { element.scrollTop = element.scrollHeight; atEnd.current = true; }
    else if (anchor.current) {
      const target = element.querySelector<HTMLElement>(`[data-run-id="${CSS.escape(anchor.current.runId)}"]`);
      if (target) element.scrollTop += target.getBoundingClientRect().top - element.getBoundingClientRect().top - anchor.current.offset;
    }
    armed.current = element.scrollTop > 2;
    remember();
    requestAnimationFrame(() => { restoring.current = false; });
  }, [groups, own, pendingId]);

  const onScroll = () => {
    const element = scroller.current;
    if (!element) return;
    atEnd.current = isNearBottom(element.scrollHeight, element.scrollTop, element.clientHeight);
    if (restoring.current) return;
    remember();
    if (element.scrollTop > 2) armed.current = true;
    else if (armed.current) { armed.current = false; void bridge.call('mightyLoadOlder', { paneId: session.id }); }
  };
  return (
    <div className="mighty-timeline" ref={scroller} data-testid="mighty-timeline" onScroll={onScroll} onWheel={stopMoving} onPointerDown={stopMoving}>
      {/* The history card the diagram has at its top (M/MightyGraphView.swift historyCard), centred. */}
      {history && (
        <div className="timeline-history">
          {history.acts ? <button className="mighty-link" onClick={() => void bridge.call('mightyLoadOlder', { paneId: session.id })}><Icon name="circle-arrow-up" size={13} weight={2} />{history.text}</button>
            : <span className="mighty-muted">{history.text}</span>}
        </div>
      )}
      {count === 0 ? <div className="mighty-muted mighty-pad">{t('graph.timeline.empty')}</div>
        : groups!.map((group) => <Group key={group.runId} group={group} run={runs.get(group.runId)} paneId={session.id} provider={session.provider} onOpenRow={onOpenRow}
          pending={group.runId === pendingId ? pendingState : undefined} />)}
    </div>
  );
}
