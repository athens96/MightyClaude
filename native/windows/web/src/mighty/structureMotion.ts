// The structure view's pure motion rules, ported rule for rule from MightyClaude.Core: MightyStructurePan (dragging empty
// space pans: the page sideways, each column up and down; click slop 3, release grace 0.25 s), MightyStructureWheel (the
// wheel tracker: settle 0.3 s, stale 1 s) and MightyStructureFollow (PageWatch and ColumnWatch, slack 36: only the newest
// board follows, each column its own end). Origins are top-down, as a scroller's scrollLeft/scrollTop.

export interface XY { x: number; y: number }

// ── MightyStructurePan ──

export const ClickSlop = 3;
export const ReleaseGrace = 0.25;

export type PanTarget = 'page' | 'textOrControl' | 'innerScroll' | 'outside';

export const mayPan = (target: PanTarget, modified: boolean, scrollable: boolean) => target === 'page' && !modified && scrollable;
export const isDrag = (start: XY, point: XY) => Math.max(Math.abs(point.x - start.x), Math.abs(point.y - start.y)) >= ClickSlop;
export const scrollable = (minimum: XY, maximum: XY) => maximum.x - minimum.x > 0.5 || maximum.y - minimum.y > 0.5;

function clamp(value: number, low: number, high: number, current: number): number {
  return Number.isFinite(value) && Number.isFinite(low) && Number.isFinite(high) && low <= high ? Math.min(Math.max(value, low), high) : current;
}

/** The page's origin with the grabbed point kept under the pointer, clamped to the page's edges. */
export const panOrigin = (start: XY, press: XY, cursor: XY, minimum: XY, maximum: XY): XY =>
  ({ x: clamp(start.x - (cursor.x - press.x), minimum.x, maximum.x, start.x), y: clamp(start.y - (cursor.y - press.y), minimum.y, maximum.y, start.y) });

export type Axis = 'horizontal' | 'vertical';

/** The origins a scroller may take moving along `axis` only, the other axis held at `current`. */
export const confined = (minimum: XY, maximum: XY, current: XY, axis: Axis): { minimum: XY; maximum: XY } =>
  axis === 'horizontal' ? { minimum: { x: minimum.x, y: current.y }, maximum: { x: maximum.x, y: current.y } } : { minimum: { x: current.x, y: minimum.y }, maximum: { x: current.x, y: maximum.y } };

// ── MightyStructureWheel ──

export const SettleGrace = 0.3;
export const StaleAfter = 1.0;

export type Stage = 'step' | 'began' | 'changed' | 'ended' | 'cancelled' | 'momentum' | 'momentumEnded';

export const axisOf = (delta: XY): Axis | null => (delta.x === 0 && delta.y === 0 ? null : Math.abs(delta.x) > Math.abs(delta.y) ? 'horizontal' : 'vertical');

/** A scroller's origin after a wheel movement: a positive y shows what is above, a positive x what is to the left. */
export function wheelOrigin(current: XY, delta: XY, axis: Axis | null, minimum: XY, maximum: XY): XY {
  const dx = axis === 'vertical' ? 0 : delta.x, dy = axis === 'horizontal' ? 0 : delta.y;
  return { x: clamp(current.x - dx, minimum.x, maximum.x, current.x), y: clamp(current.y - dy, minimum.y, maximum.y, current.y) };
}

export function canMove(current: XY, delta: XY, axis: Axis | null, minimum: XY, maximum: XY): boolean {
  const next = wheelOrigin(current, delta, axis, minimum, maximum);
  return Math.abs(next.x - current.x) > 0.5 || Math.abs(next.y - current.y) > 0.5;
}

/** Which scroller takes a movement, innermost first and the page last: the first that can still move, otherwise the page. */
export function taker(canMoves: readonly boolean[]): number {
  const index = canMoves.indexOf(true);
  return index >= 0 ? index : Math.max(0, canMoves.length - 1);
}

export type WheelAction = 'pass' | 'hold' | 'choose' | 'move';

/** A page's wheel gestures, one at a time (MightyStructureWheel.Tracker). A browser wheel event carries no gesture: a step. */
export class WheelTracker {
  private owned = false;
  private chosen = false;
  private live = false;
  private lastEvent = Number.NEGATIVE_INFINITY;
  axis: Axis | null = null;

  feed(stage: Stage, overPage: boolean, delta: XY, now: number): WheelAction {
    const moves = delta.x !== 0 || delta.y !== 0;
    if (stage === 'step') {
      this.owned = false; this.chosen = false; this.live = false; this.axis = null;
      if (!overPage) return 'pass';
      this.lastEvent = now;
      return moves ? 'choose' : 'hold';
    }
    if (stage === 'began') {
      this.owned = overPage; this.chosen = false; this.live = overPage; this.axis = null;
      if (!this.owned) return 'pass';
      this.lastEvent = now;
      if (!moves) return 'hold';
      this.chosen = true; this.axis = axisOf(delta);
      return 'choose';
    }
    if (this.owned && !this.live && now - this.lastEvent >= SettleGrace) { this.owned = false; this.chosen = false; }
    if (!this.owned) return 'pass';
    this.lastEvent = now;
    this.live = stage === 'changed' || stage === 'momentum';
    const action: WheelAction = !moves ? 'hold' : this.chosen ? 'move' : 'choose';
    if (action === 'choose') { this.chosen = true; this.axis = axisOf(delta); }
    if (stage === 'cancelled' || stage === 'momentumEnded') { this.owned = false; this.chosen = false; }
    return action;
  }

  userScrolling(now: number): boolean {
    const since = now - this.lastEvent;
    return (this.live && since < StaleAfter) || since < SettleGrace;
  }
}

/** The page is moving under the user's hand: a reported scroll, a drag or the moment after it, or a wheel. */
export const userMoving = (scrollPhase: boolean, panning: boolean, panReleasedAt: number, wheel: WheelTracker, now: number) =>
  scrollPhase || panning || now - panReleasedAt < ReleaseGrace || wheel.userScrolling(now);

// ── MightyStructureFollow ──

export const Slack = 36;

/** Where a scroller stands, top-down: the newest card's id and its bottom when it is laid out. */
export interface Page {
  originY: number;
  visibleHeight: number;
  documentHeight: number;
  newestId: string | null;
  newestBottom: number | null;
}

const atNewest = (p: Page) => (p.newestId === null ? p.originY + p.visibleHeight >= p.documentHeight - Slack
  : p.newestBottom !== null && p.newestBottom >= p.originY && p.newestBottom <= p.originY + p.visibleHeight + Slack);
const newestHidden = (p: Page) => p.newestBottom !== null && (p.newestBottom > p.originY + p.visibleHeight + 0.5 || p.newestBottom < p.originY);
const moved = (p: Page, last: Page) => Math.abs(p.originY - last.originY) > 0.5;
const laidOut = (p: Page, last: Page) => p.newestId !== last.newestId || Math.abs(p.documentHeight - last.documentHeight) > 0.5 || Math.abs(p.visibleHeight - last.visibleHeight) > 0.5
  || (p.newestBottom === null) !== (last.newestBottom === null) || Math.abs((p.newestBottom ?? 0) - (last.newestBottom ?? 0)) > 0.5;

/** Where the view scrolls: `{ card: null }` the page's end, `{ card }` that card's bottom. */
export interface Scroll { card: string | null }
export const ScrollBottom: Scroll = { card: null };

const ShowTries = 3;

export class PageWatch {
  following = true;
  private last: Page | null = null;
  private showing: string | null = null;
  private tries = 0;
  private judging = false;
  private userMove = 0;

  stop(): void { this.following = false; this.showing = null; this.judging = false; }

  userScrolled(dy: number): void {
    this.showing = null; this.judging = false;
    if (!this.last) return;
    this.userMove += dy;
    this.following = atNewest({ ...this.last, originY: this.last.originY + this.userMove });
  }

  appeared(newestId: string | null): Scroll | null {
    this.last = null;
    if (!this.following) { this.judging = true; return null; }
    if (newestId === null) return ScrollBottom;
    this.showing = newestId; this.tries = ShowTries;
    return { card: newestId };
  }

  newRequest(id: string): Scroll {
    this.following = true; this.judging = false; this.showing = id; this.tries = ShowTries;
    return { card: id };
  }

  observe(page: Page, userScrolling: boolean): Scroll | null {
    const previous = this.last;
    this.last = page; this.userMove = 0;
    if (this.judging) { this.judging = false; this.following = atNewest(page); return null; }
    if (userScrolling) { this.showing = null; this.following = atNewest(page); return null; }
    if (previous && moved(page, previous) && !laidOut(page, previous) && this.showing === null) { this.following = atNewest(page); return null; }
    if (!this.following) return null;
    if (this.showing !== null && page.newestId === this.showing && page.newestBottom !== null && !newestHidden(page)) this.showing = null;
    if (!newestHidden(page) || page.newestId === null || page.newestBottom === null) return null;
    if (this.showing !== null && --this.tries <= 0) this.showing = null;
    return page.documentHeight - page.newestBottom <= Slack ? ScrollBottom : { card: page.newestId };
  }
}

export class ColumnWatch {
  boardId: string | null = null;
  newest = false;
  private columns = new Map<string, PageWatch>();

  following(column: string): boolean {
    return this.newest && (this.columns.get(column)?.following ?? true);
  }

  private show(board: string, newest: boolean): void {
    if (board === this.boardId && newest === this.newest) return;
    this.boardId = board; this.newest = newest; this.columns.clear();
  }

  private watch(column: string): PageWatch {
    let watch = this.columns.get(column);
    if (!watch) { watch = new PageWatch(); this.columns.set(column, watch); }
    return watch;
  }

  appeared(board: string, newest: boolean, column: string): Scroll | null {
    this.show(board, newest);
    return newest ? this.watch(column).appeared(column) : null;
  }

  newRequest(board: string, column: string): Scroll {
    this.show(board, true);
    const watch = new PageWatch();
    this.columns.set(column, watch);
    return watch.newRequest(column);
  }

  stop(column: string): void { if (this.newest) this.watch(column).stop(); }

  userScrolled(column: string, dy: number): void { if (this.newest) this.columns.get(column)?.userScrolled(dy); }

  observe(board: string, newest: boolean, column: string, page: Page, userScrolling: boolean): Scroll | null {
    this.show(board, newest);
    return newest ? this.watch(column).observe(page, userScrolling) : null;
  }
}

// ── MightyStructureBoard.Navigation ──

/** What the navigation reads of a board. */
export interface NavigableBoard { id: string; runIds: readonly string[]; quiet?: boolean }

/** The newest board: the last one, but a quiet notice's board after it, which the view never moves on to by itself. */
export function newestBoardIndex(boards: readonly NavigableBoard[]): number | null {
  for (let index = boards.length - 1; index >= 0; index--) if (boards[index]!.quiet !== true) return index;
  return boards.length > 0 ? boards.length - 1 : null;
}

/**
 * Which board the view shows (MightyStructureBoard.Navigation): on the newest board the view moves on with new ones; a board
 * the user picks is kept by its id (and the requests it held), and a newer board arriving meanwhile is unseen until the user
 * goes to the newest board again.
 */
export class StructureNavigation {
  chosen: string | null = null;
  private chosenRuns: readonly string[] = [];
  private seenNewest: string | null = null;

  index(boards: readonly NavigableBoard[]): number | null {
    if (boards.length === 0) return null;
    if (this.chosen === null) return newestBoardIndex(boards);
    const at = boards.findIndex((board) => board.id === this.chosen);
    if (at >= 0) return at;
    const runs = new Set(this.chosenRuns);
    const held = boards.findIndex((board) => board.runIds.some((id) => runs.has(id)));
    return held >= 0 ? held : 0;
  }

  onNewest(boards: readonly NavigableBoard[]): boolean {
    const index = this.index(boards);
    return index === null || index === newestBoardIndex(boards);
  }

  hasUnseen(boards: readonly NavigableBoard[]): boolean {
    const newest = newestBoardIndex(boards);
    return !this.onNewest(boards) && (newest === null ? null : boards[newest]!.id) !== this.seenNewest;
  }

  select(id: string, boards: readonly NavigableBoard[]): void {
    const target = boards.find((board) => board.id === id);
    if (!target) return;
    const at = newestBoardIndex(boards);
    const newest = at === null ? null : boards[at]!.id;
    if (target.id === newest) { this.chosen = null; this.chosenRuns = []; this.seenNewest = null; return; }
    if (this.onNewest(boards)) this.seenNewest = newest;
    this.chosen = target.id; this.chosenRuns = target.runIds;
  }

  step(offset: number, boards: readonly NavigableBoard[]): void {
    const index = this.index(boards);
    if (index === null) return;
    this.select(boards[Math.min(Math.max(index + offset, 0), boards.length - 1)]!.id, boards);
  }

  showNewest(boards: readonly NavigableBoard[]): void {
    const newest = newestBoardIndex(boards);
    if (newest === null) { this.chosen = null; return; }
    this.select(boards[newest]!.id, boards);
  }
}

/** "Request ✓ → Run ● → Result ○": each step and where it stands (StructureStepsSummary). */
export function stepsSummary(columns: readonly { title: string; step: string }[]): string {
  const marks: Record<string, string> = { done: '✓', current: '●', pending: '○', error: '✕' };
  return columns.map((column) => column.title + ' ' + (marks[column.step] ?? '■')).join(' → ');
}
