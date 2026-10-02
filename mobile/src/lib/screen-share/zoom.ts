/**
 * Zoom on the phone is a streaming request, not a crop of what already arrived: the Mac
 * sends the zoomed region at full resolution over a low-resolution overview of the whole
 * screen, so terminal text stays readable instead of being blown up from a shrunken frame.
 */

/** A normalized rectangle inside one display: 0–1 on both axes. */
export interface ZoomRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** The whole display — what the Mac streams when nothing is zoomed. */
export const FULL_REGION: ZoomRegion = { x: 0, y: 0, width: 1, height: 1 };

/** Past this the phone asks for a region; at or under it the full screen is enough. */
export const MIN_ZOOM_SCALE = 1.05;
/** Beyond 8× a phone screen shows a few characters, so that is the ceiling. */
export const MAX_ZOOM_SCALE = 8;

function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.min(1, Math.max(0, value));
}

export function clampZoomScale(scale: number): number {
  if (!Number.isFinite(scale) || scale < 1) return 1;
  return Math.min(MAX_ZOOM_SCALE, scale);
}

/**
 * The region to ask for, from a pinch scale and the normalized point the user centred.
 * The rectangle is clamped so it always stays inside the display: panning to an edge
 * slides the window rather than asking for coordinates that do not exist.
 */
export function zoomRegionFor(scale: number, centre: { x: number; y: number }): ZoomRegion {
  const limited = clampZoomScale(scale);
  if (limited < MIN_ZOOM_SCALE) return { ...FULL_REGION };
  const width = 1 / limited;
  const height = 1 / limited;
  const x = clamp01(clamp01(centre.x) - width / 2);
  const y = clamp01(clamp01(centre.y) - height / 2);
  return {
    x: Math.min(x, 1 - width),
    y: Math.min(y, 1 - height),
    width,
    height,
  };
}

/** True when the Mac would be asked to stream a region rather than the whole display. */
export function isZoomed(region: ZoomRegion): boolean {
  return region.width < 1 || region.height < 1;
}

/**
 * Turns a touch inside the zoomed view back into a point on the display, so a tap lands
 * where the user sees it while only part of the screen is being streamed.
 */
export function pointInRegion(
  region: ZoomRegion,
  local: { x: number; y: number },
): { x: number; y: number } {
  return {
    x: clamp01(region.x + clamp01(local.x) * region.width),
    y: clamp01(region.y + clamp01(local.y) * region.height),
  };
}

/** The overview layer under the zoom: the whole display, small enough to cost nothing. */
export const OVERVIEW_MAX_WIDTH = 640;

export function overviewSize(display: { width: number; height: number }): {
  width: number;
  height: number;
} {
  if (display.width <= 0 || display.height <= 0) return { width: 0, height: 0 };
  if (display.width <= OVERVIEW_MAX_WIDTH) return { width: display.width, height: display.height };
  const scale = OVERVIEW_MAX_WIDTH / display.width;
  return {
    width: OVERVIEW_MAX_WIDTH,
    height: Math.max(1, Math.round(display.height * scale)),
  };
}
