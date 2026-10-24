import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { useServices } from '../app/context';
import { Entry } from '../conversation/Entry';
import { Markdown } from '../conversation/MarkdownView';
import type { LogEntry, MightyGraphRun, RunSession } from '../store/models';
import type { TimelineGroup, TimelineRow } from './types';
import { useReferenceClick } from '../conversation/referenceClick';

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

const Row = memo(function Row({ row, run, paneId, provider, open, onToggle }: { row: TimelineRow; run: MightyGraphRun | undefined; paneId: string; provider: string; open: boolean; onToggle: (id: string) => void }) {
  const entries = row.agentIndex !== undefined ? run?.agents[row.agentIndex]?.entries : row.kind === 'execution' ? run?.rootEntries : undefined;
  const meta = [row.kindLabel, row.capsule, row.duration].filter(Boolean).join(' · ');
  return (
    <div className={'timeline-row tone-' + row.tone} data-node-id={row.nodeId}>
      <div className="timeline-rail">
        <span className={'timeline-node tone-' + row.tone + (row.ring ? ' ring' : '')} />
        {row.rail && <span className={'timeline-line tone-' + row.rail} />}
      </div>
      <div className={'timeline-card' + (row.status === 'running' || row.status === 'waiting' ? ' edge' : '')}>
        <button className="timeline-card-head" onClick={() => onToggle(row.nodeId)} disabled={!entries && !row.input}>
          <span className="timeline-title">{row.title}</span>
          {row.statusWord && <span className={'mighty-pill tone-' + row.tone}>{row.statusWord}</span>}
        </button>
        <div className="timeline-meta">{meta}</div>
        {row.latest && <div className="timeline-latest"><span className="timeline-pulse" />{row.latest}</div>}
        {open && entries && <RowBody entries={entries} paneId={paneId} provider={provider} input={row.input} />}
      </div>
    </div>
  );
});

const Group = memo(function Group({ group, run, paneId, provider }: { group: TimelineGroup; run: MightyGraphRun | undefined; paneId: string; provider: string }) {
  const { bridge, t } = useServices();
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set());
  const [full, setFull] = useState(false);
  const toggle = useMemo(() => (id: string) => setOpen((s) => { const n = new Set(s); if (!n.delete(id)) n.add(id); return n; }), []);
  return (
    <section className="timeline-group" data-run-id={group.runId} data-testid="timeline-group">
      <header className="timeline-head">
        <button className="mighty-icon" onClick={() => void bridge.call('mightyTimelineFlip', { paneId, runId: group.runId })}>{group.open ? '▾' : '▸'}</button>
        <span className="timeline-group-title">{group.title}</span>
        <span className={'mighty-pill tone-' + group.tone}>{group.statusWord}</span>
        <span className="timeline-tally">{group.tally}</span>
      </header>
      <div className="timeline-prompt">{group.input}</div>
      {group.open && group.rows.slice(1).map((row) => (
        <Row key={row.nodeId} row={row} run={run} paneId={paneId} provider={provider} open={open.has(row.nodeId)} onToggle={toggle} />
      ))}
      {group.open && group.result !== undefined && (
        <div className={'timeline-result tone-' + (group.resultTone ?? 'done')}>
          <div className={'timeline-result-text' + (group.resultFolds && !full ? ' folded' : '')}>{group.result ? <Markdown text={group.result} /> : null}</div>
          {group.resultFolds && <button className="mighty-link" onClick={() => setFull((f) => !f)}>{t(full ? 'phone.blocks.resultLess' : 'phone.blocks.resultMore')}</button>}
          {group.resultCaption && <div className="timeline-meta">{group.resultCaption}</div>}
        </div>
      )}
    </section>
  );
});

/** The 로그 view (UI/MightyTimeline.cs): the host's groups, one per request, newest at the bottom, following it. */
export function Timeline({ session, groups, older }: { session: RunSession; groups: readonly TimelineGroup[] | null; older: readonly MightyGraphRun[] }) {
  const { bridge, t } = useServices();
  const scroller = useRef<HTMLDivElement>(null);
  const runs = useMemo(() => new Map([...older, ...(session.graphRuns ?? [])].map((r) => [r.id, r])), [older, session.graphRuns]);
  const count = groups?.length ?? 0;
  const lastStatus = groups?.[count - 1]?.status;
  useEffect(() => {
    const element = scroller.current;
    if (element) element.scrollTop = element.scrollHeight;
  }, [count, lastStatus]);
  const onScroll = () => {
    if (scroller.current && scroller.current.scrollTop <= 2) void bridge.call('mightyLoadOlder', { paneId: session.id });
  };
  return (
    <div className="mighty-timeline" ref={scroller} data-testid="mighty-timeline" onScroll={onScroll}>
      {count === 0 ? <div className="mighty-muted mighty-pad">{t('graph.timeline.empty')}</div>
        : groups!.map((group) => <Group key={group.runId} group={group} run={runs.get(group.runId)} paneId={session.id} provider={session.provider} />)}
    </div>
  );
}
