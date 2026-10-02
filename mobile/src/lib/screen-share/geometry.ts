import type { NormalizedPoint } from '@/lib/screen-share/input';
import { FULL_REGION, type ZoomRegion } from '@/lib/screen-share/zoom';

/**
 * Where the picture actually is on the phone. The video is drawn `contain`-fitted, so a
 * Mac display that is wider or taller than the stage leaves black bars; a touch has to be
 * measured against the picture, not the stage, and a touch on a bar is no touch at all.
 * Coordinates sent to the Mac are always on the whole display, zoomed or not.
 */

export interface Size {
  width: number;
  height: number;
}

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Width over height, or `undefined` for a size that is not one. */
function aspectOf(size: Size | undefined): number | undefined {
  if (!size || !(size.width > 0) || !(size.height > 0)) return undefined;
  return size.width / size.height;
}

/**
 * The aspect of what the `screen` track shows: the decoded frame's own when the view has
 * reported it, otherwise the zoom region's share of the display, otherwise the stage's.
 */
export function contentAspect(input: {
  video?: Size | undefined;
  display?: Size | undefined;
  region?: ZoomRegion | undefined;
  stage: Size;
}): number {
  const video = aspectOf(input.video);
  if (video !== undefined) return video;
  const display = aspectOf(input.display);
  if (display !== undefined) {
    const region = input.region ?? FULL_REGION;
    if (region.width > 0 && region.height > 0) return (display * region.width) / region.height;
    return display;
  }
  return aspectOf(input.stage) ?? 1;
}

/** The rectangle a `contain`-fitted picture of this aspect fills inside the stage. */
export function containRect(stage: Size, aspect: number): Rect {
  const width = Math.max(0, stage.width);
  const height = Math.max(0, stage.height);
  if (width === 0 || height === 0 || !(aspect > 0)) return { x: 0, y: 0, width, height };
  if (width / height > aspect) {
    // Stage wider than the picture: bars left and right.
    const fitted = height * aspect;
    return { x: (width - fitted) / 2, y: 0, width: fitted, height };
  }
  const fitted = width / aspect;
  return { x: 0, y: (height - fitted) / 2, width, height: fitted };
}

/**
 * A touch on the stage, as a point on the whole display — or `undefined` when it landed on
 * a letterbox bar, outside the picture.
 */
export function stagePointToDisplay(
  point: { x: number; y: number },
  content: Rect,
  region: ZoomRegion,
): NormalizedPoint | undefined {
  if (!(content.width > 0) || !(content.height > 0)) return undefined;
  const localX = (point.x - content.x) / content.width;
  const localY = (point.y - content.y) / content.height;
  if (localX < 0 || localX > 1 || localY < 0 || localY > 1) return undefined;
  return {
    x: region.x + localX * region.width,
    y: region.y + localY * region.height,
  };
}

/**
 * Where to draw the `overview` track (the whole display) so that the zoomed region on it
 * sits exactly under the sharp picture: scaled up by the zoom and shifted by the region's
 * offset. Outside the sharp picture the overview fills in the surroundings, softly.
 */
export function overviewRect(content: Rect, region: ZoomRegion): Rect {
  if (!(region.width > 0) || !(region.height > 0)) return content;
  const width = content.width / region.width;
  const height = content.height / region.height;
  return {
    x: content.x - region.x * width,
    y: content.y - region.y * height,
    width,
    height,
  };
}
