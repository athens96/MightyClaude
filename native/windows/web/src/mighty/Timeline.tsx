import { memo, useLayoutEffect, useMemo, useRef, useState } from 'react';
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

const Group = memo(function Group({ group, run, paneId, provider, onOpenRow }: { group: TimelineGroup; run: MightyGraphRun | undefined; paneId: string; provider: string; onOpenRow?: (nodeId: string) => void }) {
  const { bridge, t } = useServices();
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set());
  const [full, setFull] = useState(false);
  // Opening a row aims the diagram at its block (MainWindow.MightyTimeline.cs ToggleTimelineRow).
  const toggle = useMemo(() => (id: string) => setOpen((s) => {
    const n = new Set(s);
    if (!n.delete(id)) { n.add(id); onOpenRow?.(id); }
    return n;
  }), [onOpenRow]);
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

const terminalStatus = new Set(['completed', 'error', 'stopped']);

/**
 * The 로그 view (UI/MightyTimeline.cs): the host's groups, one per request, newest at the bottom. It follows the bottom when
 * it first draws, when a new request arrives and when a request finishes; any other change (a row opened, older requests read
 * in at the top, a streamed line) keeps the first group in view where it was (anchor restore). The top reads older requests.
 */
export function Timeline({ session, groups, older, onOpenRow }: { session: RunSession; groups: readonly TimelineGroup[] | null; older: readonly MightyGraphRun[]; onOpenRow?: (nodeId: string) => void }) {
  const { bridge, t } = useServices();
  const scroller = useRef<HTMLDivElement>(null);
  const runs = useMemo(() => new Map([...older, ...(session.graphRuns ?? [])].map((r) => [r.id, r])), [older, session.graphRuns]);
  const count = groups?.length ?? 0;
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
    const finished = new Map(list.map((g) => [g.runId, terminalStatus.has(g.status)]));
    const last = list[list.length - 1]?.runId ?? null;
    const before = progress.current;
    const newlyFinished = !!before && list.some((g) => finished.get(g.runId) && before.finished.get(g.runId) === false);
    const follow = before === null || last !== before.last || newlyFinished;
    progress.current = { last, finished };
    restoring.current = true;
    if (follow) element.scrollTop = element.scrollHeight;
    else if (anchor.current) {
      const target = element.querySelector<HTMLElement>(`[data-run-id="${CSS.escape(anchor.current.runId)}"]`);
      if (target) element.scrollTop += target.getBoundingClientRect().top - element.getBoundingClientRect().top - anchor.current.offset;
    }
    armed.current = element.scrollTop > 2;
    remember();
    requestAnimationFrame(() => { restoring.current = false; });
  }, [groups]);

  const onScroll = () => {
    const element = scroller.current;
    if (!element || restoring.current) return;
    remember();
    if (element.scrollTop > 2) armed.current = true;
    else if (armed.current) { armed.current = false; void bridge.call('mightyLoadOlder', { paneId: session.id }); }
  };
  return (
    <div className="mighty-timeline" ref={scroller} data-testid="mighty-timeline" onScroll={onScroll}>
      {count === 0 ? <div className="mighty-muted mighty-pad">{t('graph.timeline.empty')}</div>
        : groups!.map((group) => <Group key={group.runId} group={group} run={runs.get(group.runId)} paneId={session.id} provider={session.provider} onOpenRow={onOpenRow} />)}
    </div>
  );
}
