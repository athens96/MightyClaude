import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useServices } from '../app/context';
import { Markdown } from '../conversation/MarkdownView';
import { nodeId, RequestStep } from './layout';
import {
  ColumnWatch, StructureNavigation, WheelTracker, confined, isDrag, mayPan, panOrigin, scrollable, stepsSummary, userMoving,
  type Page, type PanTarget, type Scroll, type XY,
} from './structureMotion';
import type { StructureActivity, StructureBoard, StructureCard } from './types';
import { chipClass, edgeClass, tintClass } from './structureTone';
import { usePageOverlay } from '../shell/overlay';
import { Icon, type IconName } from '../icons/Icon';

/** Shows a block in the diagram: the block, or its request when the diagram does not draw it. */
export type Reveal = (nodeId: string, fallbackNodeId: string) => void;

/** M/MightyStructureView.swift's fold lines: a request's ten, the latest reply's eight, an agent's prompt two and output six. */
const RequestLines = 10, MessageLines = 8, PromptLines = 2, OutputLines = 6;

/** A tool call's glyph (M/AgentActivityView.swift AgentActivityRow.symbol); commands, reads and edits are set in mono. */
function activitySymbol(kind: string): IconName {
  switch (kind) {
    case 'command': return 'square-terminal';
    case 'read': return 'file-search';
    case 'edit': return 'pencil-line';
    case 'search': return 'search';
    case 'web': return 'globe';
    case 'agent': return 'users';
    case 'turn': return 'sparkles';
    default: return 'wrench';
  }
}
const monospaced = new Set(['command', 'read', 'edit']);

function ActivityRow({ line }: { line: StructureActivity }) {
  return (
    <div className="structure-activity-row" title={line.text}>
      <span className={'structure-activity-glyph state-' + line.state}><Icon name={activitySymbol(line.kind)} size={11} weight={1.75} /></span>
      <span className={'structure-activity-text' + (monospaced.has(line.kind) ? ' mono' : '')}>{line.text}</span>
    </div>
  );
}

/** Text folded to its first lines, with 더 보기 / 접기 where the lines cut it (foldableText). */
function Fold({ text, lines, className, onFold }: { text: string; lines: number; className: string; onFold: (element: HTMLElement) => void }) {
  const { t } = useServices();
  const [open, setOpen] = useState(false);
  const [cut, setCut] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => { const e = ref.current; if (e && !open) setCut(e.scrollHeight > e.clientHeight + 1); }, [text, open]);
  return (
    <div className="structure-fold">
      <div ref={ref} className={className} style={open ? undefined : { WebkitLineClamp: lines }} data-folded={open ? undefined : ''}>{text}</div>
      {(cut || open) && <button type="button" className="structure-more" onClick={(event) => { onFold(event.currentTarget); setOpen((o) => !o); }}>{t(open ? 'graph.structure.less' : 'graph.structure.more')}</button>}
    </div>
  );
}

function RevealButton({ id, fallback, onReveal }: { id: string; fallback: string; onReveal: Reveal }) {
  const { t } = useServices();
  return (
    <button className="structure-reveal" data-testid="structure-reveal" title={t('graph.structure.reveal')} aria-label={t('graph.structure.reveal')} onClick={() => onReveal(id, fallback)}>
      <Icon name="square-arrow-out-up-right" size={12} weight={1.75} />
    </button>
  );
}

/** The bolt a running card shows (MightyGraphActivityIndicator, still under reduced motion). */
const Bolt = ({ status }: { status: string }) => status === 'running' ? <span className="structure-bolt" aria-hidden="true"><Icon name="zap" size={12} weight={0} fill /></span> : null;

/**
 * A card of the board (M/MightyStructureView.swift card): the request's words; the 실행 part with its pill, tally, latest reply and
 * tool calls; the result's answer or what it waits for; a styled board's summary; or a sub-agent's small card with its status bar.
 */
const Card = memo(function Card({ card, onFold, onReveal }: { card: StructureCard; onFold: (element: HTMLElement) => void; onReveal: Reveal }) {
  const { t } = useServices();
  const request = nodeId(card.runId, RequestStep);
  const base = 'structure-card tone-' + card.tone + ' kind-' + card.kind + edgeClass(card.status) + tintClass(card.tint);
  const progress = (
    <>
      {card.message ? <Fold text={card.message} lines={MessageLines} className="structure-text" onFold={onFold} />
        : !card.finished && card.activity.length === 0 ? <div className="structure-quiet">{t('graph.block.waitingResponse')}</div> : null}
      {card.activity.length > 0 && <div className="structure-activity">{card.activity.map((a, i) => <ActivityRow key={i} line={a} />)}</div>}
    </>
  );
  const pill = <span className={'mighty-pill tone-' + card.tone}>{card.statusWord}</span>;
  switch (card.kind) {
    case 'request':
      return (
        <div className={'structure-card kind-request' + tintClass(card.tint)} data-card-id={card.id} data-node-id={card.nodeId}>
          <div className="structure-card-row top">
            {card.text ? <Fold text={card.text} lines={RequestLines} className="structure-text" onFold={onFold} /> : <span className="structure-heading">{card.title}</span>}
            <RevealButton id={card.nodeId} fallback={request} onReveal={onReveal} />
          </div>
        </div>
      );
    case 'execution':
      return (
        <div className={base} data-card-id={card.id} data-node-id={card.nodeId}>
          <div className="structure-card-row"><Bolt status={card.status} />{pill}<span className="structure-tally">{card.tally}</span><span className="spacer" /><RevealButton id={card.nodeId} fallback={request} onReveal={onReveal} /></div>
          {progress}
        </div>
      );
    case 'result': {
      const ended = card.status === 'error' || card.status === 'stopped';
      const reveal = <RevealButton id={card.finished ? card.resultNodeId ?? card.nodeId : card.executionNodeId ?? card.nodeId} fallback={request} onReveal={onReveal} />;
      const outcome = card.answer ? <div className="structure-answer"><Markdown text={card.answer} /></div>
        : <div className="structure-quiet">{card.finished ? card.statusWord : t('graph.structure.resultPending')}</div>;
      return (
        <div className={'structure-card tone-' + card.tone + ' kind-result' + tintClass(card.tint)} data-card-id={card.id} data-node-id={card.nodeId}>
          {ended ? <><div className="structure-card-row"><ResultTitle card={card} /><span className="spacer" />{reveal}</div>{outcome}</>
            : <div className="structure-card-row top">{outcome}{reveal}</div>}
        </div>
      );
    }
    case 'summary':
      return (
        <div className={base} data-card-id={card.id} data-node-id={card.nodeId}>
          <div className="structure-card-row"><span className="structure-heading">{card.title}</span><span className="spacer" /><RevealButton id={card.nodeId} fallback={request} onReveal={onReveal} /></div>
          {card.text && <Fold text={card.text} lines={RequestLines} className="structure-text" onFold={onFold} />}
          <div className="structure-rule" />
          <div className="structure-card-row">
            <span className="structure-execution"><Icon name="cog" size={11} weight={2} />{t('graph.block.executionOf', { n: String(card.title.match(/\d+/)?.[0] ?? '') })}</span>
            <Bolt status={card.status} />{pill}<span className="structure-tally">{card.tally}</span><span className="spacer" />
            <RevealButton id={card.executionNodeId ?? card.nodeId} fallback={request} onReveal={onReveal} />
          </div>
          {progress}
          {card.finished && (
            <div className="structure-card-row">
              <ResultTitle card={card} /><span className={'structure-line' + (card.resultLine ? '' : ' tone-' + card.tone)} title={card.resultLine ?? undefined}>{card.resultLine ?? card.statusWord}</span><span className="spacer" />
              <RevealButton id={card.resultNodeId ?? card.nodeId} fallback={request} onReveal={onReveal} />
            </div>
          )}
        </div>
      );
    default: {
      const task = card.nodeKind === 'task';
      // A task notice (the only steer the structure keeps) wears the steer's glyph and ink, as its timeline row does.
      const steer = card.nodeKind === 'steer';
      const last = card.activity[card.activity.length - 1];
      return (
        <div className={'structure-node tone-' + card.tone + edgeClass(card.status) + tintClass(card.tint)} data-card-id={card.id} data-node-id={card.nodeId}
          style={{ marginLeft: Math.min(card.depth ?? 0, 3) * 16 }}>
          <div className="structure-card-row">
            <span className={'structure-node-glyph' + (task ? ' task' : steer ? ' steer' : '')}><Icon name={task ? 'square-terminal' : steer ? 'message-square-text' : 'square-user-round'} size={11} weight={2} /></span>
            <span className="structure-node-title">{card.title}</span>
            {card.meta && <span className="structure-node-meta">{card.meta}</span>}
            <span className="spacer" /><Bolt status={card.status} /><RevealButton id={card.nodeId} fallback={request} onReveal={onReveal} />
          </div>
          {card.parent && <div className="structure-parent"><Icon name="corner-down-right" size={10} weight={2} />{card.parent}</div>}
          {card.prompt && <Fold text={card.prompt} lines={PromptLines} className="structure-prompt" onFold={onFold} />}
          {card.text && <Fold text={card.text} lines={OutputLines} className="structure-output" onFold={onFold} />}
          {last && <ActivityRow line={last} />}
        </div>
      );
    }
  }
});

/** The result's title in the outcome's ink once there is one (resultTitle). */
function ResultTitle({ card }: { card: StructureCard }) {
  const { t } = useServices();
  const [key, icon]: [string, IconName] = card.status === 'error' ? ['graph.block.resultError', 'triangle-alert'] : card.status === 'stopped' ? ['graph.block.resultStopped', 'circle-stop'] : ['graph.block.result', 'badge-check'];
  return <span className={'structure-result-title' + (card.finished ? ' tone-' + card.tone : '')}><Icon name={icon} size={11} weight={2} />{t(key)}</span>;
}

/** A column's step as the pipeline's chip (M/MightyStructureView.swift chip): done green with a check, current the accent with a dot, pending grey with a ring. */
function stepIcon(step: string): IconName {
  switch (step) {
    case 'done': return 'check';
    case 'current': return 'circle';
    case 'error': return 'x';
    case 'stopped': return 'square';
    default: return 'circle';
  }
}

/** What a press on the page lands on (MightyStructurePan.Target): selectable text and native controls take their own drag. */
function pressTarget(event: ReactPointerEvent, root: HTMLElement): PanTarget {
  const element = event.target as HTMLElement;
  if (!root.contains(element)) return 'outside';
  if (element.closest('input, textarea, select, .structure-list')) return 'textOrControl';
  // A scroll bar: the press lies past the element's client box.
  if (event.nativeEvent.offsetX > element.clientWidth || event.nativeEvent.offsetY > element.clientHeight) return 'textOrControl';
  if (element.closest('button')) return 'page';
  const ownText = [...element.childNodes].some((node) => node.nodeType === Node.TEXT_NODE && (node.textContent ?? '').trim().length > 0);
  return ownText ? 'textOrControl' : 'page';
}

/** A column's page as the follow rule reads it: its newest card is its last. */
function columnPage(body: HTMLElement, column: string): Page {
  const last = body.lastElementChild as HTMLElement | null;
  const bottom = last ? last.offsetTop - body.offsetTop + last.offsetHeight : null;
  return { originY: body.scrollTop, visibleHeight: body.clientHeight, documentHeight: body.scrollHeight, newestId: last ? column : null, newestBottom: bottom };
}

function scrollTo(body: HTMLElement, scroll: Scroll) {
  const last = body.lastElementChild as HTMLElement | null;
  if (scroll.card === null || !last) body.scrollTop = body.scrollHeight;
  else body.scrollTop = Math.max(0, last.offsetTop - body.offsetTop + last.offsetHeight - body.clientHeight);
}

/**
 * The 파이프라인 view (UI/MightyStructure.cs): one board at a time, newest by default; ◀, the board's place over the list of
 * boards, ▶, and the badge of a newer board unseen; a column per step, each scrolling up and down on its own under a page that
 * scrolls sideways. Dragging the page's empty space (or a card's header) pans the page sideways and the pressed column up and
 * down (MightyStructurePan); a short press is a click. The newest board's columns follow their newest card (ColumnWatch) unless
 * the reader moved away; a card's reveal button shows its block in the diagram.
 */
export function Structure({ boards, onReveal }: { boards: readonly StructureBoard[] | null; onReveal: Reveal }) {
  const { t } = useServices();
  const list = boards ?? [];
  const [navigation] = useState(() => new StructureNavigation());
  const [, setVersion] = useState(0);
  const redraw = () => setVersion((v) => v + 1);
  const [listOpen, setListOpen] = useState(false);
  usePageOverlay(listOpen);
  const page = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const follow = useRef(new ColumnWatch());
  const wheel = useRef(new WheelTracker());
  const press = useRef<{ start: XY; pointer: number; page: XY; column: HTMLElement | null; columnTop: number; panning: boolean } | null>(null);
  const panned = useRef(false);
  const releasedAt = useRef(Number.NEGATIVE_INFINITY);
  const seenRuns = useRef<{ board: string; count: number } | null>(null);

  const index = navigation.index(list);
  const board = index === null ? undefined : list[index];
  const newest = index !== null && index === list.length - 1;
  const now = () => performance.now() / 1000;
  const moving = () => userMoving(false, !!press.current?.panning, releasedAt.current, wheel.current, now());

  const bodies = useCallback(() => [...(page.current?.querySelectorAll<HTMLElement>('.structure-column-body') ?? [])], []);
  const columnOf = (body: HTMLElement) => body.closest<HTMLElement>('[data-column-id]')?.dataset.columnId ?? '';

  // A board shown (or the view back): its columns appear; a new request on the newest board starts every column over.
  useLayoutEffect(() => {
    if (!board) return;
    const runs = board.runIds.length;
    const again = seenRuns.current?.board === board.id;
    const grew = again && newest && runs > (seenRuns.current?.count ?? runs);
    seenRuns.current = { board: board.id, count: runs };
    if (again && !grew) return;
    for (const body of bodies()) {
      const column = columnOf(body);
      const scroll = grew ? follow.current.newRequest(board.id, column) : follow.current.appeared(board.id, newest, column);
      if (scroll) scrollTo(body, scroll);
      else if (!again) body.scrollTop = 0;
    }
  }, [board, newest, bodies]);

  // After every draw, each column reports where it stands; a following column is taken to its end.
  useLayoutEffect(() => {
    if (!board) return;
    for (const body of bodies()) {
      const column = columnOf(body);
      const scroll = follow.current.observe(board.id, newest, column, columnPage(body, column), false);
      if (scroll) scrollTo(body, scroll);
    }
  });
  // A column that grows without a draw of this view (a Markdown picture, a font): judged as layout.
  useEffect(() => {
    if (!board) return undefined;
    const observer = new ResizeObserver(() => {
      for (const body of bodies()) {
        const column = columnOf(body);
        const scroll = follow.current.observe(board.id, newest, column, columnPage(body, column), moving());
        if (scroll) scrollTo(body, scroll);
      }
    });
    for (const body of bodies()) { observer.observe(body); for (const child of body.children) observer.observe(child); }
    return () => observer.disconnect();
  });

  const onColumnScroll = (event: React.UIEvent<HTMLElement>) => {
    const body = event.target as HTMLElement;
    if (!board || !body.classList.contains('structure-column-body')) return;
    const column = columnOf(body);
    const scroll = follow.current.observe(board.id, newest, column, columnPage(body, column), moving());
    if (scroll) scrollTo(body, scroll);
  };

  // The wheel: a column under the pointer scrolls itself; elsewhere a vertical wheel moves the page sideways.
  useEffect(() => {
    const element = page.current;
    if (!element) return undefined;
    const onWheel = (event: WheelEvent) => {
      wheel.current.feed('step', true, { x: event.deltaX, y: event.deltaY }, now());
      const body = (event.target as Element).closest<HTMLElement>('.structure-column-body');
      if (body) { follow.current.userScrolled(columnOf(body), event.deltaY); return; }
      if (Math.abs(event.deltaY) > Math.abs(event.deltaX) && element.scrollWidth > element.clientWidth) {
        event.preventDefault();
        element.scrollLeft += event.deltaY;
      }
    };
    element.addEventListener('wheel', onWheel, { passive: false });
    return () => element.removeEventListener('wheel', onWheel);
  }, [board?.id]);

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    const element = page.current;
    if (event.button !== 0 || !element || !root.current) return;
    const column = (event.target as Element).closest<HTMLElement>('.structure-column')?.querySelector<HTMLElement>('.structure-column-body') ?? null;
    const movable = scrollable({ x: 0, y: 0 }, { x: element.scrollWidth - element.clientWidth, y: column ? column.scrollHeight - column.clientHeight : 0 });
    if (!mayPan(pressTarget(event, root.current), event.ctrlKey || event.shiftKey || event.altKey || event.metaKey, movable)) return;
    press.current = { start: { x: event.clientX, y: event.clientY }, pointer: event.pointerId, page: { x: element.scrollLeft, y: 0 }, column, columnTop: column?.scrollTop ?? 0, panning: false };
  };
  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const p = press.current, element = page.current;
    if (!p || !element || event.pointerId !== p.pointer) return;
    const cursor = { x: event.clientX, y: event.clientY };
    if (!p.panning) {
      if (!isDrag(p.start, cursor)) return;
      p.panning = true;
      element.setPointerCapture(event.pointerId);
      window.getSelection()?.removeAllRanges();
    }
    const sideways = confined({ x: 0, y: 0 }, { x: element.scrollWidth - element.clientWidth, y: 0 }, p.page, 'horizontal');
    element.scrollLeft = panOrigin(p.page, p.start, cursor, sideways.minimum, sideways.maximum).x;
    if (p.column) {
      const before = p.column.scrollTop;
      const upDown = confined({ x: 0, y: 0 }, { x: 0, y: p.column.scrollHeight - p.column.clientHeight }, { x: 0, y: p.columnTop }, 'vertical');
      p.column.scrollTop = panOrigin({ x: 0, y: p.columnTop }, p.start, cursor, upDown.minimum, upDown.maximum).y;
      if (board && p.column.scrollTop !== before) follow.current.userScrolled(columnOf(p.column), p.column.scrollTop - before);
    }
  };
  const endPress = () => {
    const p = press.current;
    press.current = null;
    if (p?.panning) { panned.current = true; releasedAt.current = now(); }
  };
  // A press that became a drag is not a click on what it began on.
  const onClickCapture = (event: React.MouseEvent) => {
    if (!panned.current) return;
    panned.current = false;
    event.stopPropagation();
    event.preventDefault();
  };

  const onFold = useCallback((element: HTMLElement) => {
    const column = element.closest<HTMLElement>('[data-column-id]')?.dataset.columnId;
    if (column) follow.current.stop(column);
  }, []);

  // The list closes on a press outside it or Escape.
  useEffect(() => {
    if (!listOpen) return undefined;
    const close = (event: Event) => { if (!(event.target as Element).closest?.('.structure-list, .structure-place')) setListOpen(false); };
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') setListOpen(false); };
    window.addEventListener('pointerdown', close);
    window.addEventListener('keydown', key);
    return () => { window.removeEventListener('pointerdown', close); window.removeEventListener('keydown', key); };
  }, [listOpen]);

  const position = (b: StructureBoard, at: number) => t(b.kind === 'pipeline' ? 'graph.structure.board.pipeline' : 'graph.structure.board.request', { n: at + 1, count: list.length });
  const title = (b: StructureBoard, at: number) => b.headline ?? b.styleName ?? position(b, at);
  const summaries = useMemo(() => list.map((b) => stepsSummary(b.columns)), [list]);
  if (!board || index === null) return <div className="mighty-structure" data-testid="mighty-structure"><div className="mighty-muted mighty-pad">{t('graph.timeline.empty')}</div></div>;
  const move = (change: () => void) => { if (panned.current) return; change(); redraw(); };
  const unseen = navigation.hasUnseen(list);
  return (
    <div className="mighty-structure" data-testid="mighty-structure" data-board-count={list.length} data-board-id={board.id} ref={root}>
      <nav className="structure-nav">
        <button className="structure-step" title={t('graph.structure.board.previous')} aria-label={t('graph.structure.board.previous')} disabled={index <= 0} onClick={() => move(() => navigation.step(-1, list))}><Icon name="chevron-left" size={13} weight={2.4} /></button>
        <span className="structure-place">
          <button className="structure-position" data-testid="structure-list-button" title={t('graph.structure.board.list')} aria-haspopup="listbox" aria-expanded={listOpen} onClick={() => setListOpen((o) => !o)}>
            {position(board, index)}<Icon name="chevron-down" size={9} weight={2.6} />
          </button>
          {listOpen && (
            <div className="structure-list popover" role="listbox" data-page-overlay="" aria-label={t('graph.structure.board.list')} data-testid="structure-list">
              {list.map((b, at) => (
                <button key={b.id} role="option" aria-selected={at === index} className={'structure-entry' + (at === index ? ' chosen' : '')} data-board-id={b.id}
                  onClick={() => { setListOpen(false); move(() => navigation.select(b.id, list)); }}>
                  <span className="structure-entry-number">{at + 1}</span>
                  <span className={'structure-entry-mark tone-' + b.tone} aria-hidden="true" />
                  <span className="structure-entry-words">
                    <span className="structure-entry-title">{title(b, at)}</span>
                    <span className="structure-entry-steps">{summaries[at]}</span>
                  </span>
                </button>
              ))}
            </div>
          )}
        </span>
        <button className="structure-step" title={t('graph.structure.board.next')} aria-label={t('graph.structure.board.next')} disabled={index >= list.length - 1} onClick={() => move(() => navigation.step(1, list))}><Icon name="chevron-right" size={13} weight={2.4} /></button>
        {unseen && (
          <button className="structure-badge" data-testid="structure-unseen" title={t('graph.structure.board.showNewest')} onClick={() => move(() => navigation.showNewest(list))}>
            {t(board.kind === 'pipeline' ? 'graph.structure.board.newPipeline' : 'graph.structure.board.newRequest')}
          </button>
        )}
      </nav>
      {/* The board's status, and a pipeline's name; a request's words are its 요청 card right below (M/MightyStructureView.swift header). */}
      <div className="structure-header">
        <Bolt status={board.status} />
        <span className={'mighty-pill tone-' + board.tone}>{board.statusWord}</span>
        {board.kind === 'pipeline' && <span className="structure-headline">{title(board, index)}</span>}
      </div>
      <div className="structure-columns" ref={page} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={endPress} onPointerCancel={endPress}
        onLostPointerCapture={endPress} onClickCapture={onClickCapture} onScrollCapture={onColumnScroll}>
        {board.columns.map((column, at) => (
          <div key={column.id} className={'structure-column step-' + column.step + tintClass(column.tone)} data-column-id={column.id}>
            <header className="structure-column-head" title={column.title + ' · ' + column.stepWord}>
              <span className={'structure-chip step-' + column.step + chipClass(column.step, column.tone)}><Icon name={stepIcon(column.step)} size={8} weight={3.2} fill={column.step === 'current'} />{column.title}</span>
              <span className="structure-count">{column.count}</span>
              {at < board.columns.length - 1 && <span className={'structure-link' + (column.step === 'done' ? ' done' : '')} aria-hidden="true" />}
            </header>
            <div className="structure-column-frame">
              <div className="structure-column-body">{column.cards.map((card) => <Card key={card.id} card={card} onFold={onFold} onReveal={onReveal} />)}</div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
