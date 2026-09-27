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

/**
 * Pulling past the newest content to refresh: a drag that starts at the bottom and
 * carries the finger this far further up. Starting anywhere else is an ordinary scroll.
 */
export const PULL_REFRESH_DISTANCE = 72;

/** Closer than this to the bottom is where a pull may start. */
export const PULL_START_SLACK = 4;

/** Past this, a finger moving down is a scroll and one moving up is a pull. */
export const PULL_GIVE_UP = 8;

export type PullPhase = 'idle' | 'pulling' | 'armed';

/** The phase of a pull, from how far the finger has moved up since it started at the bottom. */
export function pullPhase(liftedBy: number): PullPhase {
  if (liftedBy >= PULL_REFRESH_DISTANCE) return 'armed';
  return liftedBy > 0 ? 'pulling' : 'idle';
}
