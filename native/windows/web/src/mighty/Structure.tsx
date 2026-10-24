import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useServices } from '../app/context';
import { Markdown } from '../conversation/MarkdownView';
import { nodeId, RequestStep } from './layout';
import {
  ColumnWatch, StructureNavigation, WheelTracker, confined, isDrag, mayPan, panOrigin, scrollable, stepsSummary, userMoving,
  type Page, type PanTarget, type Scroll, type XY,
} from './structureMotion';
import type { StructureBoard, StructureCard } from './types';
import { usePageOverlay } from '../shell/overlay';

/** Shows a block in the diagram: the block, or its request when the diagram does not draw it. */
export type Reveal = (nodeId: string, fallbackNodeId: string) => void;

const Card = memo(function Card({ card, onFold, onReveal }: { card: StructureCard; onFold: (element: HTMLElement) => void; onReveal: Reveal }) {
  const { t } = useServices();
  const [open, setOpen] = useState(!card.finished);
  return (
    <div className={'structure-card tone-' + card.tone + ' kind-' + card.kind} data-card-id={card.id} data-node-id={card.nodeId}>
      <div className="structure-card-row">
        <button className="structure-card-head" onClick={(event) => { onFold(event.currentTarget); setOpen((o) => !o); }} aria-expanded={open}>
          <span className="structure-card-title">{card.title}</span>
          <span className={'mighty-pill tone-' + card.tone}>{card.statusWord}</span>
        </button>
        <button className="mighty-icon structure-reveal" data-testid="structure-reveal" title={t('graph.structure.reveal')} aria-label={t('graph.structure.reveal')}
          onClick={() => onReveal(card.nodeId, nodeId(card.runId, RequestStep))}>
          <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M5 2H2v8h8V7M7 2h3v3M10 2 5.5 6.5" /></svg>
        </button>
      </div>
      {card.parent && <div className="mighty-muted">{card.parent}</div>}
      {!open && card.resultLine && <div className="structure-line">{card.resultLine}</div>}
      {open && (
        <div className="structure-card-body">
          {card.kind === 'result' || card.kind === 'summary' ? (card.answer ? <Markdown text={card.answer} /> : card.text && <div className="structure-text">{card.text}</div>)
            : card.text && <div className="structure-text">{card.text}</div>}
          {card.kind === 'execution' && card.message && <div className="structure-text">{card.message}</div>}
          {card.activity.length > 0 && <ul className="structure-activity">{card.activity.map((a, i) => <li key={i}>{a}</li>)}</ul>}
          {card.kind !== 'agent' && card.tally && <div className="mighty-muted">{card.tally}</div>}
        </div>
      )}
    </div>
  );
});

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
        <button className="mighty-icon" title={t('graph.structure.board.previous')} aria-label={t('graph.structure.board.previous')} disabled={index <= 0} onClick={() => move(() => navigation.step(-1, list))}>◀</button>
        <span className="structure-place">
          <button className="structure-position" data-testid="structure-list-button" title={t('graph.structure.board.list')} aria-haspopup="listbox" aria-expanded={listOpen} onClick={() => setListOpen((o) => !o)}>
            {position(board, index)} <span aria-hidden="true">▾</span>
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
        <button className="mighty-icon" title={t('graph.structure.board.next')} aria-label={t('graph.structure.board.next')} disabled={index >= list.length - 1} onClick={() => move(() => navigation.step(1, list))}>▶</button>
        {unseen && (
          <button className="structure-badge" data-testid="structure-unseen" title={t('graph.structure.board.showNewest')} onClick={() => move(() => navigation.showNewest(list))}>
            {t(board.kind === 'pipeline' ? 'graph.structure.board.newPipeline' : 'graph.structure.board.newRequest')}
          </button>
        )}
      </nav>
      <div className="structure-header">
        <span className="structure-headline">{title(board, index)}</span>
        <span className={'mighty-pill tone-' + board.tone}>{board.statusWord}</span>
      </div>
      <div className="structure-columns" ref={page} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={endPress} onPointerCancel={endPress}
        onLostPointerCapture={endPress} onClickCapture={onClickCapture} onScrollCapture={onColumnScroll}>
        {board.columns.map((column) => (
          <div key={column.id} className={'structure-column step-' + column.step} data-column-id={column.id}>
            <header className="structure-column-head">
              <span className="structure-column-title">{column.title}</span>
              <span className={'structure-chip step-' + column.step}>{column.stepWord}</span>
              <span className="mighty-muted">{column.count}</span>
            </header>
            <div className="structure-column-body">{column.cards.map((card) => <Card key={card.id} card={card} onFold={onFold} onReveal={onReveal} />)}</div>
          </div>
        ))}
      </div>
    </div>
  );
}
