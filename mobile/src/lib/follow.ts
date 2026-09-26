/**
 * Whether a growing list should stay pinned to its newest content. The list follows
 * until the user scrolls away from the bottom to read, and follows again once they come
 * back near it. Only the user's own scrolling decides: the list's jumps to the end, and
 * content that grows under a still viewport, never turn following off.
 */

/** How close to the bottom, in points, still counts as being at it. */
export const FOLLOW_THRESHOLD = 80;

export interface ScrollMetrics {
  contentHeight: number;
  viewportHeight: number;
  offsetY: number;
}

/** A list shorter than its viewport is at its bottom. */
export function isNearBottom(metrics: ScrollMetrics, threshold = FOLLOW_THRESHOLD): boolean {
  return metrics.contentHeight - metrics.viewportHeight - metrics.offsetY < threshold;
}

/** Following after one scroll event; `byUser` is whether a finger drove it. */
export function followAfterScroll(following: boolean, metrics: ScrollMetrics, byUser: boolean): boolean {
  return byUser ? isNearBottom(metrics) : following;
}
