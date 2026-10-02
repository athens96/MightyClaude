import {
  containRect,
  contentAspect,
  overviewRect,
  stagePointToDisplay,
} from '@/lib/screen-share/geometry';
import { FULL_REGION, zoomRegionFor } from '@/lib/screen-share/zoom';

/**
 * A touch is measured against the picture, not the stage: a 16:10 Mac on a 19.5:9 phone in
 * landscape leaves bars at the sides, and a touch on a bar must not click the Mac's edge.
 */

describe('where the contain-fitted picture is', () => {
  it('leaves bars left and right on a stage wider than the picture', () => {
    expect(containRect({ width: 2000, height: 1000 }, 16 / 10)).toEqual({
      x: 200,
      y: 0,
      width: 1600,
      height: 1000,
    });
  });

  it('leaves bars top and bottom on a stage taller than the picture', () => {
    expect(containRect({ width: 1000, height: 1000 }, 2)).toEqual({ x: 0, y: 250, width: 1000, height: 500 });
  });

  it('takes the frame’s own aspect when the view reported it', () => {
    expect(
      contentAspect({
        video: { width: 1280, height: 800 },
        display: { width: 1920, height: 1080 },
        region: FULL_REGION,
        stage: { width: 10, height: 10 },
      }),
    ).toBeCloseTo(1.6);
  });

  it('otherwise works it out from the zoom region and the display', () => {
    const region = { x: 0.1, y: 0.2, width: 0.25, height: 0.5 };
    expect(
      contentAspect({ display: { width: 1600, height: 1000 }, region, stage: { width: 10, height: 10 } }),
    ).toBeCloseTo((1.6 * 0.25) / 0.5);
    expect(contentAspect({ stage: { width: 300, height: 100 } })).toBe(3);
  });
});

describe('a touch on the stage', () => {
  const content = containRect({ width: 2000, height: 1000 }, 16 / 10);

  it('maps the picture’s corners to the display’s', () => {
    expect(stagePointToDisplay({ x: 200, y: 0 }, content, FULL_REGION)).toEqual({ x: 0, y: 0 });
    expect(stagePointToDisplay({ x: 1800, y: 1000 }, content, FULL_REGION)).toEqual({ x: 1, y: 1 });
    expect(stagePointToDisplay({ x: 1000, y: 500 }, content, FULL_REGION)).toEqual({ x: 0.5, y: 0.5 });
  });

  it('is dropped when it lands on a letterbox bar', () => {
    expect(stagePointToDisplay({ x: 100, y: 500 }, content, FULL_REGION)).toBeUndefined();
    expect(stagePointToDisplay({ x: 1900, y: 500 }, content, FULL_REGION)).toBeUndefined();
  });

  it('lands on the whole display while zoomed', () => {
    const region = zoomRegionFor(4, { x: 0.5, y: 0.5 });
    const zoomed = containRect({ width: 2000, height: 1000 }, 16 / 10);
    // The middle of the zoomed picture is the middle of the display…
    expect(stagePointToDisplay({ x: 1000, y: 500 }, zoomed, region)).toEqual({ x: 0.5, y: 0.5 });
    // …and its top-left corner is the region's.
    expect(stagePointToDisplay({ x: 200, y: 0 }, zoomed, region)).toEqual({ x: region.x, y: region.y });
  });
});

describe('the overview under a zoom', () => {
  it('is scaled and shifted so the zoomed region lies exactly under the sharp picture', () => {
    const content = { x: 100, y: 0, width: 800, height: 500 };
    const region = { x: 0.25, y: 0.5, width: 0.5, height: 0.5 };
    const rect = overviewRect(content, region);
    expect(rect).toEqual({ x: -300, y: -500, width: 1600, height: 1000 });
    // The region's corner on the overview is the sharp picture's corner.
    expect(rect.x + region.x * rect.width).toBe(content.x);
    expect(rect.y + region.y * rect.height).toBe(content.y);
  });

  it('is the picture itself when nothing is zoomed', () => {
    const content = { x: 0, y: 10, width: 100, height: 50 };
    expect(overviewRect(content, FULL_REGION)).toEqual(content);
  });
});
