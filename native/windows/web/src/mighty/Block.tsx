import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useServices } from '../app/context';
import { Entry } from '../conversation/Entry';
import { HeightCache, estimateHeight, layout, nearBottom, visibleRange } from '../conversation/virtual';
import { Markdown } from '../conversation/MarkdownView';
import { ProviderMark } from '../conversation/ProviderMark';
import type { LogEntry } from '../store/models';
import { dashForCard, dashOffset } from './camera';
import { isResizable, visibleImages } from './layout';
import type { MightyBlockCopy, MightyExecutionCard, MightyFile } from './types';
import { BottomRight, type Edges } from './resize';

/** What a block's buttons and grips ask of the diagram; one stable object for every block. */
export interface BlockActions {
  toggleExpand(id: string, expanded: boolean): void;
  /** A press on the corner grip: the diagram hit-tests the four borders' bands itself (src/mighty/resize.ts). */
  startResize(id: string, edges: Edges, event: React.PointerEvent): void;
  resetSize(id: string): void;
  toggleFiles(runId: string): void;
  fitResult(id: string): void;
  openFile(file: MightyFile): void;
  loadOlder(): void;
  measured(id: string, height: number): void;
}

/** What only some kinds draw; each part keeps its object while it does not change. */
export interface BlockExtra {
  input?: string;
  entries?: readonly LogEntry[];
  finalOutput?: string;
  markdown?: string;
  files?: readonly MightyFile[];
  filesOpen?: boolean;
  images?: { runId: string; step: string; count: number };
  execution?: MightyExecutionCard;
  history?: { text: string; acts: boolean; phase: string };
  draft?: string;
  fitted?: boolean;
  measure?: boolean;
}

export interface BlockProps {
  paneId: string;
  provider: string;
  id: string;
  kind: string;
  x: number;
  y: number;
  w: number;
  h: number;
  copy: MightyBlockCopy | undefined;
  extra: BlockExtra;
  /** In or near the viewport: its body is drawn; off screen only the header is. */
  visible: boolean;
  selected: boolean;
  expanded: boolean;
  running: boolean;
  actions: BlockActions;
}

/**
 * The transcript of a block, windowed as the conversation is (src/conversation/virtual.ts): every entry but the request, only
 * those near the block body's viewport in the page, each measured once per width and the rest stood in for by their heights.
 * It opens at its end and follows new entries while its body stands within 32 px of the bottom.
 */
const Transcript = memo(function Transcript({ entries, paneId, provider, running }: { entries: readonly LogEntry[]; paneId: string; provider: string; running: boolean }) {
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set());
  const shown = useMemo(() => entries.filter((e) => e.kind !== 'user'), [entries]);
  const toggle = useMemo(() => (id: string) => setOpen((s) => { const n = new Set(s); if (!n.delete(id)) n.add(id); return n; }), []);
  const root = useRef<HTMLDivElement>(null);
  const [heights] = useState(() => new HeightCache());
  const width = useRef(0);
  const following = useRef(true);
  const frame = useRef(0);
  const [view, setView] = useState({ top: Number.MAX_SAFE_INTEGER, height: 400, width: 0 });
  const [version, setVersion] = useState(0);
  const scroller = useCallback(() => root.current?.closest<HTMLElement>('.mighty-body') ?? null, []);
  const stick = useCallback(() => {
    const body = scroller();
    if (body && following.current) body.scrollTop = body.scrollHeight;
  }, [scroller]);
  const [rows] = useState(() => new ResizeObserver((records) => {
    let changed = false;
    for (const record of records) {
      const element = record.target as HTMLElement;
      const id = element.dataset.entryId;
      if (!id || !element.isConnected) continue;
      if (heights.set(id, width.current, Math.ceil(record.borderBoxSize[0]?.blockSize ?? element.offsetHeight))) changed = true;
    }
    if (!changed) return;
    cancelAnimationFrame(frame.current);
    frame.current = requestAnimationFrame(() => setVersion((v) => v + 1));
  }));
  useEffect(() => () => { cancelAnimationFrame(frame.current); rows.disconnect(); }, [rows]);
  const observe = useCallback((element: HTMLDivElement | null) => {
    if (!element) return undefined;
    rows.observe(element);
    return () => rows.unobserve(element);
  }, [rows]);
  useLayoutEffect(() => {
    const body = scroller(), element = root.current;
    if (!body || !element) return undefined;
    // The transcript's own top inside the body (a request band may stand over it).
    const read = () => {
      following.current = nearBottom(body.scrollHeight, body.scrollTop, body.clientHeight);
      const next = { top: body.scrollTop - element.offsetTop, height: body.clientHeight, width: Math.round(element.clientWidth) };
      if (next.width !== width.current) width.current = next.width;
      setView((current) => (current.top === next.top && current.height === next.height && current.width === next.width ? current : next));
    };
    const fit = () => { stick(); read(); };
    fit();
    const resize = new ResizeObserver(fit);
    resize.observe(body);
    body.addEventListener('scroll', read, { passive: true });
    return () => { resize.disconnect(); body.removeEventListener('scroll', read); };
  }, [scroller, stick]);
  useLayoutEffect(stick);
  const offsets = useMemo(() => layout(shown, (entry) => heights.get(entry.id, view.width) ?? estimateHeight(entry)), [shown, view.width, version, heights]);
  const [start, end] = visibleRange(offsets, view.top, view.height);
  const total = offsets[offsets.length - 1] ?? 0;
  return (
    <div className="mighty-transcript" ref={root} data-entry-count={shown.length}>
      <div style={{ paddingTop: offsets[start], paddingBottom: total - (offsets[end] ?? total) }}>
        {shown.slice(start, end).map((entry) => (
          <div key={entry.id} data-entry-id={entry.id} ref={observe}>
            <Entry entry={entry} paneId={paneId} provider={provider} shell={false}
              live={running && !!entry.activity && (entry.activity.state === 'running' || entry.activity.state === 'waiting')}
              expanded={open.has(entry.id)} onToggle={toggle} />
          </div>
        ))}
      </div>
    </div>
  );
});

function Bars() {
  return <span className="mighty-bars" aria-hidden="true">{[0, 1, 2, 3].map((i) => <span key={i} style={{ animationDelay: `${-(i / 5) * 1.6}s` }} />)}</span>;
}

function Indicator({ kind }: { kind: string }) {
  if (kind === 'animating') return <Bars />;
  if (kind === 'static') return <svg className="mighty-glyph tone-run" viewBox="0 0 12 12" aria-hidden="true"><path d="M7 0 1 7h4l-1 5 6-7H6z" fill="currentColor" /></svg>;
  if (kind === 'waiting') return <svg className="mighty-glyph tone-wait" viewBox="0 0 12 12" aria-hidden="true"><circle cx="6" cy="6" r="5" fill="none" stroke="currentColor" /><path d="M4.5 3.5v5M7.5 3.5v5" stroke="currentColor" /></svg>;
  return null;
}

/** The running or waiting edge over a card (MightyGraphActivity): dashes fitted to its outline, marching once per period. */
function Outline({ outline, w, h }: { outline: string; w: number; h: number }) {
  if (outline === 'none' || w <= 2 || h <= 2) return null;
  const dash = dashForCard(w, h);
  const period = -dashOffset(1, dash);
  return (
    <svg className={'mighty-outline outline-' + outline} width={w} height={h} aria-hidden="true" data-outline={outline}>
      <rect x={1} y={1} width={w - 2} height={h - 2} rx={11} ry={11}
        style={outline === 'marching' ? ({ strokeDasharray: dash.join(' '), '--march': `${-period}` } as React.CSSProperties) : undefined} />
    </svg>
  );
}

function Images({ paneId, images }: { paneId: string; images: { runId: string; step: string; count: number } }) {
  const { bridge, t } = useServices();
  const [open, setOpen] = useState(false);
  const [tiles, setTiles] = useState<{ id: string; alt: string; url?: string | null }[] | null>(null);
  useEffect(() => {
    let live = true;
    bridge.call<{ count: number; tiles: { id: string; alt: string; url?: string | null }[] } | null>('mightyImages', { paneId, runId: images.runId, step: images.step, expanded: open })
      .then((value) => { if (live) setTiles(value?.tiles ?? []); }).catch(() => { if (live) setTiles([]); });
    return () => { live = false; };
  }, [bridge, paneId, images.runId, images.step, images.count, open]);
  const visible = visibleImages(images.count, open);
  const rest = images.count - visible;
  return (
    <div className="mighty-images">
      <div className="mighty-tiles">
        {(tiles ?? []).slice(0, visible).map((tile, i) => (
          <div key={tile.id} className="mighty-tile" title={tile.alt}>
            {tile.url ? <img src={tile.url} alt={tile.alt} draggable={false} /> : <span className="mighty-tile-missing">{t('images.missing')}</span>}
            {i === visible - 1 && rest > 0 && <button className="mighty-tile-more" onClick={() => setOpen(true)}>{t('graph.images.more', { count: rest })}</button>}
          </div>
        ))}
      </div>
      {images.count > 6 && <button className="mighty-link" onClick={() => setOpen((o) => !o)}>{t(open ? 'graph.images.collapse' : 'graph.images.expand')}</button>}
    </div>
  );
}

/**
 * A background Ouroboros execution (BuildOuroborosCard): the goal, the AC progress, the four counts, phase and activity, a note
 * while nothing can be read, the AC list while it is open (the first 300, each its state's mark before its title, stepped per
 * level), and the execution's id beside "open dashboard" (the host opens the run's loopback dashboard page).
 */
function Execution({ card, paneId, open }: { card: MightyExecutionCard; paneId: string; open: boolean }) {
  const { bridge, t } = useServices();
  return (
    <div className="mighty-execution">
      {card.goal && <div className={'mighty-execution-goal' + (open ? ' open' : '')} title={card.goal}>{card.goal}</div>}
      {card.progress && (
        <div className="mighty-execution-progress">
          {card.done !== undefined && <div className={'mighty-execution-bar' + (card.status === 'failed' ? ' failed' : '')}><span style={{ width: `${card.done * 100}%` }} /></div>}
          <span>{card.progress}</span>
        </div>
      )}
      {card.counts.length > 0 && <div className="mighty-execution-counts">{card.counts.map((c) => <span key={c}>{c}</span>)}</div>}
      {card.line && <div className="mighty-muted">{card.line}</div>}
      {card.note && <div className="mighty-muted">{card.note}</div>}
      {open && (
        <div className="mighty-execution-acs" data-testid="mighty-ouroboros-acs">
          {card.acs ? card.acs.map((ac, i) => (
            <div key={i} className={'mighty-ac ac-' + ac.status} style={{ paddingLeft: `calc(var(--space-sm) + ${ac.depth} * var(--space-lg))` }} title={ac.title} aria-label={ac.title + ', ' + ac.statusWord}>
              <span className="mighty-ac-mark" aria-hidden="true" />
              <span className="mighty-ac-title">{ac.title}</span>
            </div>
          )) : <div className="mighty-muted">{card.listNote ?? t('graph.ouroboros.loadingACs')}</div>}
        </div>
      )}
      <div className="mighty-execution-foot">
        <span className={card.openFailed ? 'mighty-execution-failed' : 'mighty-execution-id'}>{card.footer}</span>
        <button className="mighty-link mighty-execution-open" data-testid="mighty-ouroboros-dashboard" disabled={card.opening}
          onClick={() => void bridge.call('mightyOpenDashboard', { paneId, key: card.key }).catch(() => {})}>{t('graph.ouroboros.openDashboard')}</button>
      </div>
    </div>
  );
}

function Body(props: BlockProps) {
  const { t } = useServices();
  const { kind, extra, copy, paneId, provider, running, actions, id } = props;
  const content = useRef<HTMLDivElement>(null);
  // The newest result is as tall as its answer, up to its cap: its content is measured as drawn.
  useLayoutEffect(() => {
    if (!extra.measure || !content.current) return;
    const element = content.current;
    const report = () => actions.measured(id, element.scrollHeight + 38 + 2);
    report();
    const observer = new ResizeObserver(report);
    observer.observe(element);
    return () => observer.disconnect();
  }, [extra.measure, actions, id]);
  switch (kind) {
    case 'draft':
      return <div className="mighty-body mighty-draft-body">{extra.draft ? <div className="mighty-draft-text">{extra.draft}</div> : <div className="mighty-muted">{t('graph.block.draftHint')}</div>}</div>;
    case 'request':
      return <div className="mighty-body"><div className="mighty-band">{extra.input}</div></div>;
    case 'plan':
    case 'planRecord':
      return <div className="mighty-body"><div className="mighty-pad">{extra.markdown ? <Markdown text={extra.markdown} /> : null}</div></div>;
    case 'resultFiles':
      return (
        <div className="mighty-body">
          {(extra.files ?? []).map((file) => (
            <button key={file.path + ':' + (file.line ?? '')} className="mighty-file" title={t('graph.resultFiles.openFile', { path: file.path })} onClick={() => actions.openFile(file)}>
              {file.path}{file.line ? ':' + file.line : ''}
            </button>
          ))}
        </div>
      );
    case 'images':
      return <div className="mighty-body">{extra.images && <Images paneId={paneId} images={extra.images} />}</div>;
    case 'execution':
      return <div className="mighty-body">{extra.execution && <Execution card={extra.execution} paneId={paneId} open={props.expanded} />}</div>;
    default: {
      const entries = extra.entries ?? [];
      const any = entries.some((e) => e.kind !== 'user');
      return (
        <div className="mighty-body">
          <div ref={content}>
            {extra.input && kind !== 'result' && kind !== 'work' && <div className="mighty-band">{extra.input}</div>}
            {any ? <Transcript entries={entries} paneId={paneId} provider={provider} running={running} />
              : kind === 'result' && extra.finalOutput ? <div className="mighty-pad"><Markdown text={extra.finalOutput} /></div>
                : <div className="mighty-muted mighty-pad">{t(copy && copy.status !== '' && !['completed', 'error', 'stopped'].includes(copy.status) ? 'graph.block.waitingResponse' : 'graph.block.noResponse')}</div>}
          </div>
        </div>
      );
    }
  }
}

/**
 * One diagram block (MightyGraph.cs BuildGraphCard): a 12-radius card with its 38pt header, and its body only while it is in
 * view. Memoized on its props, all of which keep their identity while the block does not change: a streamed update draws
 * the blocks it changed and no other. `data-renders` counts its draws for the smoke.
 */
export const Block = memo(function Block(props: BlockProps) {
  const { t } = useServices();
  const renders = useRef(0);
  renders.current += 1;
  // Which props differed from the last draw (by identity): the smoke reads it to tell why a block drew again.
  const previous = useRef<BlockProps | null>(null);
  const changed = previous.current ? (Object.keys(props) as (keyof BlockProps)[]).filter((key) => previous.current![key] !== props[key]).join(',') : '';
  previous.current = props;
  const { id, kind, x, y, w, h, copy, extra, selected, expanded, actions } = props;
  if (kind === 'history') {
    const history = extra.history;
    return (
      <div className="mighty-history" data-block-id={id} data-kind="history" style={{ left: x, top: y, width: w, height: h }}>
        {history?.acts ? <button className="mighty-link" onClick={actions.loadOlder}>{history.text}</button> : <span className="mighty-muted">{history?.text}</span>}
      </div>
    );
  }
  const tone = copy?.tone ?? 'idle';
  const status = copy?.status ?? '';
  const resizable = isResizable(kind);
  return (
    <div className={`mighty-block kind-${kind} tone-${tone}${selected ? ' selected' : ''}${copy?.outline === 'marching' || copy?.outline === 'solid' ? ' halo' : ''}`}
      data-block-id={id} data-kind={kind} data-renders={renders.current} data-changed={changed} data-visible={props.visible ? '1' : '0'}
      style={{ left: x, top: y, width: w, height: h }} title={copy?.fromRecord ? t('graph.history.tag') : undefined}>
      <div className="mighty-head">
        <span className={'mighty-symbol kind-' + kind} aria-hidden="true" />
        <span className="mighty-title">
          {copy?.provider && <ProviderMark provider={copy.provider} size={12} />}
          {copy?.title ?? ''}
        </span>
        <span className="mighty-controls">
          <Indicator kind={copy?.indicator ?? 'none'} />
          {copy?.state && status !== '' && <span className={'mighty-pill tone-' + tone}>{copy.state}</span>}
          {copy?.capsule && <span className="mighty-capsule" title={copy.help}>{copy.capsule}</span>}
          {kind === 'result' && copy?.filesRunId && (extra.files?.length ?? 0) > 0 && (
            <button className={'mighty-icon' + (extra.filesOpen ? ' on' : '')} title={t(extra.filesOpen ? 'graph.resultFiles.closeButton' : 'graph.resultFiles.openButton')}
              onClick={() => actions.toggleFiles(copy.filesRunId!)}>{extra.files?.length}</button>
          )}
          {kind === 'result' && !extra.fitted && extra.measure && (
            <button className="mighty-icon" data-fit="" title={t('graph.result.fitToWindow')} onClick={() => actions.fitResult(id)}>⤢</button>
          )}
          {kind === 'execution' && (
            <button className={'mighty-icon' + (expanded ? ' on' : '')} data-testid="mighty-ouroboros-list" title={t(expanded ? 'graph.ouroboros.hideACs' : 'graph.ouroboros.showACs')}
              aria-pressed={expanded} onClick={() => actions.toggleExpand(id, !expanded)}>☰</button>
          )}
          {resizable && !extra.fitted && (
            <button className="mighty-icon" data-expand="" title={t(expanded ? 'graph.block.collapse' : 'graph.block.expand')}
              onClick={() => actions.toggleExpand(id, !expanded)}>{expanded ? '−' : '+'}</button>
          )}
        </span>
      </div>
      {props.visible && <Body {...props} />}
      {kind === 'draft' && <svg className="mighty-draft-outline" width={w} height={h} aria-hidden="true"><rect x={0.75} y={0.75} width={w - 1.5} height={h - 1.5} rx={12} /></svg>}
      <Outline outline={copy?.outline ?? 'none'} w={w} h={h} />
      {resizable && (
        <>
          <div className="mighty-grip" title={t('graph.block.resize')} onPointerDown={(e) => actions.startResize(id, BottomRight, e)}
            onDoubleClick={() => actions.resetSize(id)} onContextMenu={(e) => { e.preventDefault(); actions.resetSize(id); }} />
        </>
      )}
    </div>
  );
});
