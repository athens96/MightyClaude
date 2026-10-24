// The reference bubble's sizing rules, ported from Core/ReferencePreview.cs (BubbleSize and the bubble's constants) and
// WinUI MainWindow.ReferencePreview.cs (FitReferencePreview, the grip's drag): 420 wide until dragged, at least 300×200,
// at most 85% of the room's width and the room's height, 12 from the room's edges, a 12-long tail toward the content.

export const BubbleDefaultWidth = 420;
export const BubbleMinimumWidth = 300;
export const BubbleMinimumHeight = 200;
export const BubbleMargin = 12;
export const BubbleTail = 12;

export interface BubbleSize {
  width: number;
  /** 0: as tall as the room. */
  height: number;
}

/** ReferencePreview.BubbleSize: the saved size as it is kept; a value that is not a positive number is none. */
export function savedBubbleSize(width: number | null | undefined, height: number | null | undefined): { width: number | null; height: number | null } {
  const ok = (value: number | null | undefined): value is number => typeof value === 'number' && Number.isFinite(value) && value > 0;
  return { width: ok(width) ? Math.max(BubbleMinimumWidth, width) : null, height: ok(height) ? Math.max(BubbleMinimumHeight, height) : null };
}

/** The size the bubble opens at from the app's saved size: the default width, as tall as the room when none was saved. */
export function wantedBubbleSize(savedWidth: number | null | undefined, savedHeight: number | null | undefined): BubbleSize {
  const saved = savedBubbleSize(savedWidth, savedHeight);
  return { width: saved.width ?? BubbleDefaultWidth, height: saved.height ?? 0 };
}

/** FitReferencePreview: the card's size (the tail not counted) in a room of `roomWidth` × `roomHeight`. */
export function bubbleFrame(roomWidth: number, roomHeight: number, wanted: BubbleSize): { width: number; height: number } {
  const width = Math.max(0, roomWidth - 2 * BubbleMargin);
  const height = Math.max(0, roomHeight - 2 * BubbleMargin);
  return {
    width: Math.min(Math.max(BubbleMinimumWidth, wanted.width), Math.max(BubbleMinimumWidth, width * 0.85)),
    height: wanted.height > 0 ? Math.min(Math.max(BubbleMinimumHeight, wanted.height), Math.max(BubbleMinimumHeight, height)) : Math.max(BubbleMinimumHeight, height),
  };
}

/**
 * The grip's drag: it sits in the bottom corner next to the content, so on the right a move to the left widens the bubble and
 * on the left a move to the right does. The width is clamped again by {@link bubbleFrame} when drawn.
 */
export function bubbleDrag(start: { width: number; height: number }, dx: number, dy: number, onLeft: boolean): BubbleSize {
  return { width: start.width + (onLeft ? dx : -dx), height: Math.max(BubbleMinimumHeight, start.height + dy) };
}

/** Where the tail stands: its middle at the head's middle, or half-way down a short bubble (18 across). */
export function tailTop(height: number, head: number): number {
  return Math.min(head, height / 2) - 9;
}

/** The head's symbol by what the file is (PaneView.ReferenceSymbol). */
export function referenceSymbol(path: string): 'markdown' | 'html' | 'image' | 'source' {
  const dot = path.lastIndexOf('.');
  const slash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  const extension = dot > slash ? path.slice(dot).toLowerCase() : '';
  if (['.md', '.markdown', '.mdx'].includes(extension)) return 'markdown';
  if (['.html', '.htm'].includes(extension)) return 'html';
  if (['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.tif', '.tiff', '.heic', '.ico', '.svg'].includes(extension)) return 'image';
  return 'source';
}

/** The file's own name: the last part of the path as written, or the path when it ends in a separator. */
export function referenceName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, '');
  const last = trimmed.split(/[\\/]/).pop() ?? '';
  return last.length > 0 ? last : path;
}

/** The source view's scroll for a line: lines are drawn `lineHeight` apart, the first at 0. */
export function lineScrollTop(line: number | null | undefined, lineHeight: number): number {
  return line && line > 1 ? (line - 1) * lineHeight : 0;
}
