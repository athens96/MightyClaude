// Which sides of a diagram block a drag moves, ported from Core/ResizeEdges.cs (macOS ResizeEdges): the corner handle, then a
// band along each border (6 points outside it at 100%, 2 inside), the opposite corner pinned while and after the drag.
// Coordinates are y-down, on screen unless named otherwise.

import type { Rect } from './layout';

export const Left = 1, Right = 2, Top = 4, Bottom = 8;
export const BottomRight = Right | Bottom;
/** A set of sides as flags: {@link Left} | {@link Right} | {@link Top} | {@link Bottom}; 0 for none. */
export type Edges = number;

export const horizontal = (edges: Edges) => (edges & (Left | Right)) !== 0;
export const vertical = (edges: Edges) => (edges & (Top | Bottom)) !== 0;

/** How far outside a border a press still grabs that side, in screen points at `zoom`. */
export const band = (zoom: number) => Math.max(4, 6 * zoom);
export const BandInside = 2;
/** The corner handle's side on screen at `zoom`. */
export const handleSide = (zoom: number) => Math.max(16, 22 * zoom);

/** ResizeEdgeRules.At: the sides whose band holds the point, `outside` beyond the border and `inside` within it. */
export function edgesAt(x: number, y: number, frame: Rect, outside: number, inside: number): Edges {
  if (x < frame.x - outside || x >= frame.x + frame.w + outside || y < frame.y - outside || y >= frame.y + frame.h + outside) return 0;
  const inBand = (distance: number) => distance >= -outside && distance <= inside;
  let edges = 0;
  if (inBand(x - frame.x)) edges |= Left; else if (inBand(frame.x + frame.w - x)) edges |= Right;
  if (inBand(y - frame.y)) edges |= Top; else if (inBand(frame.y + frame.h - y)) edges |= Bottom;
  return edges;
}

/**
 * ResizeEdgeRules.Target: the block and sides a press would resize, the bottom-right handle first, then a band along each
 * border; the block drawn last wins. `frames` are the resizable blocks' frames on screen, in drawing order.
 */
export function resizeTarget(x: number, y: number, frames: readonly { id: string; frame: Rect }[], zoom: number): { id: string; edges: Edges } | null {
  const side = handleSide(zoom);
  for (let i = frames.length - 1; i >= 0; i--) {
    const { id, frame } = frames[i]!;
    const inside = x >= frame.x && x < frame.x + frame.w && y >= frame.y && y < frame.y + frame.h;
    if (inside && x >= frame.x + frame.w - side && y >= frame.y + frame.h - side) return { id, edges: BottomRight };
    const edges = edgesAt(x, y, frame, band(zoom), BandInside);
    if (edges !== 0) return { id, edges };
  }
  return null;
}

/** ResizeEdgeRules.Size: the size after the pointer moved (dx, dy) on screen; a left or top side grows as it moves away. */
export function resizedSize(edges: Edges, initial: { w: number; h: number }, dx: number, dy: number, zoom: number, minimum: { w: number; h: number }, maximum: { w: number; h: number }): { w: number; h: number } {
  const scale = Number.isFinite(zoom) && zoom > 0 ? zoom : 1;
  let { w, h } = initial;
  if (edges & Right) w += dx / scale; else if (edges & Left) w -= dx / scale;
  if (edges & Bottom) h += dy / scale; else if (edges & Top) h -= dy / scale;
  return { w: Math.min(maximum.w, Math.max(minimum.w, w)), h: Math.min(maximum.h, Math.max(minimum.h, h)) };
}

/** The corner that stays put, as a fraction of the box: the one opposite the dragged sides. */
export const pinnedUnit = (edges: Edges) => ({ x: edges & Left ? 1 : 0, y: edges & Top ? 1 : 0 });

/** The pinned corner's point in the viewport for a block's frame (in diagram units) under `camera`. */
export const pinnedAnchor = (unit: { x: number; y: number }, frame: Rect, camera: { x: number; y: number }, zoom: number) =>
  ({ x: camera.x + (frame.x + unit.x * frame.w) * zoom, y: camera.y + (frame.y + unit.y * frame.h) * zoom });

/** ResizeEdgeRules.PinnedCamera: the camera that keeps the pinned corner of `frame` at `anchor`. */
export const pinnedCamera = (anchor: { x: number; y: number }, unit: { x: number; y: number }, frame: Rect, zoom: number) =>
  ({ x: anchor.x - (frame.x + unit.x * frame.w) * zoom, y: anchor.y - (frame.y + unit.y * frame.h) * zoom });

/** The resize cursor of the sides under the pointer ('' when none). */
export function edgesCursor(edges: Edges): string {
  if (edges === 0) return '';
  if (horizontal(edges) && vertical(edges)) return edges === (Left | Bottom) || edges === (Right | Top) ? 'nesw-resize' : 'nwse-resize';
  return horizontal(edges) ? 'ew-resize' : 'ns-resize';
}
