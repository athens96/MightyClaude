import {
  containRect,
  contentAspect,
  overviewRect,
  pictureRect,
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

describe('the picture when the stage changes size for full screen', () => {
  // A 16:10 Mac on a 2400×1080 phone held sideways (dp at 2.625×): with the header and the
  // panel the stage is a short strip; in full screen it is the whole display.
  const display = { width: 1600, height: 1000 };
  const video = { width: 1280, height: 800 };
  const panelled = { width: 914, height: 220 };
  const fullScreen = { width: 914, height: 411 };

  it('is re-fitted to the new stage, bars and all', () => {
    const before = pictureRect({ stage: panelled, video, display, region: FULL_REGION });
    const after = pictureRect({ stage: fullScreen, video, display, region: FULL_REGION });
    expect(before.width).toBeCloseTo(352);
    expect(before.height).toBe(220);
    expect(before.x).toBeCloseTo((914 - 352) / 2);
    expect(before.y).toBe(0);
    expect(after.height).toBe(411);
    expect(after.width).toBeCloseTo(411 * 1.6);
    expect(after.x).toBeCloseTo((914 - 411 * 1.6) / 2);
    expect(after.y).toBe(0);
  });

  it('sends the same Mac point for the same spot on the picture before and after', () => {
    const before = pictureRect({ stage: panelled, video, display, region: FULL_REGION });
    const after = pictureRect({ stage: fullScreen, video, display, region: FULL_REGION });
    const at = (rect: { x: number; y: number; width: number; height: number }, u: number, v: number) => ({
      x: rect.x + u * rect.width,
      y: rect.y + v * rect.height,
    });
    for (const [u, v] of [
      [0, 0],
      [0.25, 0.75],
      [0.5, 0.5],
      [1, 1],
    ] as const) {
      const a = stagePointToDisplay(at(before, u, v), before, FULL_REGION);
      const b = stagePointToDisplay(at(after, u, v), after, FULL_REGION);
      expect(a?.x).toBeCloseTo(u);
      expect(a?.y).toBeCloseTo(v);
      expect(b?.x).toBeCloseTo(u);
      expect(b?.y).toBeCloseTo(v);
    }
  });

  it('would miss if the old frame were kept: a full-screen touch must use the new one', () => {
    const before = pictureRect({ stage: panelled, video, display, region: FULL_REGION });
    const after = pictureRect({ stage: fullScreen, video, display, region: FULL_REGION });
    const corner = { x: after.x + after.width, y: after.y + after.height };
    const mapped = stagePointToDisplay(corner, after, FULL_REGION);
    expect(mapped?.x).toBeCloseTo(1);
    expect(mapped?.y).toBeCloseTo(1);
    // Measured against the panelled frame the same touch is off the picture altogether.
    expect(stagePointToDisplay(corner, before, FULL_REGION)).toBeUndefined();
  });

  it('still drops a touch on the full-screen letterbox bars', () => {
    const after = pictureRect({ stage: fullScreen, video, display, region: FULL_REGION });
    expect(stagePointToDisplay({ x: after.x / 2, y: 200 }, after, FULL_REGION)).toBeUndefined();
    expect(
      stagePointToDisplay({ x: after.x + after.width + 5, y: 200 }, after, FULL_REGION),
    ).toBeUndefined();
  });

  it('keeps a zoomed region mapped to the whole display in full screen', () => {
    const region = zoomRegionFor(2, { x: 0.75, y: 0.25 });
    // Before the first frame arrives the aspect comes from the display and the region.
    const after = pictureRect({ stage: fullScreen, display, region });
    expect(after.width / after.height).toBeCloseTo(1.6);
    expect(stagePointToDisplay({ x: after.x, y: after.y }, after, region)).toEqual({
      x: region.x,
      y: region.y,
    });
    const middle = stagePointToDisplay(
      { x: after.x + after.width / 2, y: after.y + after.height / 2 },
      after,
      region,
    );
    expect(middle?.x).toBeCloseTo(region.x + region.width / 2);
    expect(middle?.y).toBeCloseTo(region.y + region.height / 2);
  });

  it('fits a picture taller than the full-screen display with bars top and bottom', () => {
    const portraitMac = { width: 1080, height: 1920 };
    const rect = pictureRect({ stage: fullScreen, video: portraitMac, region: FULL_REGION });
    expect(rect.height).toBe(411);
    expect(rect.width).toBeCloseTo(411 * (1080 / 1920));
  });
});
