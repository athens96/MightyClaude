// The layout rules a drag preview and the scroll area need in the page, ported from MightyClaude.Core/PaneLayout.cs and the
// window's LayoutMinimum (MainWindow.Layout.cs). Saving a move or a share is always the host's (DockSession, ResizeSplit).
import tokens from '@tokens/design-tokens.json';
import type { PaneLayoutNode } from '../store/models';

export const layoutMetrics = tokens.metrics.layout;

/** PaneLayout.DropMergeStrip and DropEdgeShare. */
export const dropMergeStrip = 36;
export const dropEdgeShare = 0.24;

export type DropEdge = 'center' | 'left' | 'right' | 'top' | 'bottom';

/** PaneLayout.DropZone: the strip on top merges; else the nearest edge within the share (ties: left, right, top, bottom). */
export function dropZone(x: number, y: number, width: number, height: number): DropEdge {
  if (y < dropMergeStrip || !(width > 0) || !(height > 0)) return 'center';
  const across = Math.min(Math.max(x / width, 0), 1);
  const down = Math.min(Math.max(y / height, 0), 1);
  const zones: [DropEdge, number][] = [['left', across], ['right', 1 - across], ['top', down], ['bottom', 1 - down]];
  let best = zones[0]!;
  for (const zone of zones) if (zone[1] < best[1]) best = zone;
  return best[1] < dropEdgeShare ? best[0] : 'center';
}

/** PaneLayout.ClampRatio. */
export function clampRatio(ratio: number): number {
  return Number.isFinite(ratio) ? Math.min(Math.max(ratio, 0.15), 0.85) : 0.5;
}

/** LayoutMinimum: a group's least size, a split's the sum along its axis plus the divider. */
export function layoutMinimum(node: PaneLayoutNode): { width: number; height: number } {
  if (node.kind === 'tabs' || node.children.length < 2) return { width: layoutMetrics.paneMinWidth, height: layoutMetrics.paneMinHeight };
  const a = layoutMinimum(node.children[0]!);
  const b = layoutMinimum(node.children[1]!);
  return node.axis === 'horizontal'
    ? { width: a.width + b.width + layoutMetrics.splitDivider, height: Math.max(a.height, b.height) }
    : { width: Math.max(a.width, b.width), height: a.height + b.height + layoutMetrics.splitDivider };
}

/** PaneLayout.Groups: every tab group, in order. */
export function layoutGroups(node: PaneLayoutNode | undefined): PaneLayoutNode[] {
  if (!node) return [];
  return node.kind === 'tabs' ? [node] : node.children.flatMap(layoutGroups);
}
