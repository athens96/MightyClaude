import { describe, expect, it } from 'vitest';
import {
  BandInside, Bottom, BottomRight, Left, Right, Top, band, edgesAt, edgesCursor, handleSide, horizontal, pinnedAnchor, pinnedCamera,
  pinnedUnit, resizeTarget, resizedSize, vertical,
} from './resize';

// Translated from MightyClaude.Core.Tests/MightyGraphInteractionVerification.cs.
const Frame = { x: 100, y: 100, w: 300, h: 200 };

describe('resize edges (Core ResizeEdgeRules)', () => {
  it('the band reaches further outside than inside', () => {
    const at = (x: number, y: number) => edgesAt(x, y, Frame, 5, 2);
    expect([at(97, 200), at(403, 200), at(250, 101), at(250, 298)]).toEqual([Left, Right, Top, Bottom]);
    expect(at(101, 101)).toBe(Left | Top);
    expect([at(104, 200), at(250, 200), at(90, 200)]).toEqual([0, 0, 0]);
    expect([band(1), band(0.5), band(1.5), BandInside]).toEqual([6, 4, 9, 2]);
    expect([handleSide(1), handleSide(0.5), handleSide(1.5)]).toEqual([22, 16, 33]);
  });
  it('every side grows away from the box and honours zoom and bounds', () => {
    const minimum = { w: 300, h: 140 }, maximum = { w: 1400, h: 1200 }, initial = { w: 360, h: 240 };
    expect(resizedSize(BottomRight, initial, 90, 60, 1.5, minimum, maximum)).toEqual({ w: 420, h: 280 });
    expect(resizedSize(Left, initial, -40, 99, 1, minimum, maximum)).toEqual({ w: 400, h: 240 });
    expect(resizedSize(Top, initial, 99, -30, 1, minimum, maximum)).toEqual({ w: 360, h: 270 });
    expect(resizedSize(Left | Top, initial, 4000, 4000, 1, minimum, maximum)).toEqual(minimum);
    expect(resizedSize(Right, initial, 9000, 0, 1, minimum, maximum).w).toBe(1400);
    expect(resizedSize(Bottom, initial, 0, 50, Number.NaN, minimum, maximum)).toEqual({ w: 360, h: 290 });
    expect([horizontal(BottomRight), vertical(BottomRight), horizontal(Left), vertical(Left), horizontal(Top)]).toEqual([true, true, true, false, false]);
  });
  it('the corner opposite the dragged sides stays put', () => {
    expect([pinnedUnit(BottomRight), pinnedUnit(Left), pinnedUnit(Left | Top)]).toEqual([{ x: 0, y: 0 }, { x: 1, y: 0 }, { x: 1, y: 1 }]);
    const zoom = 0.8, camera = { x: 40, y: -60 };
    const unit = pinnedUnit(Left);
    const anchor = pinnedAnchor(unit, Frame, camera, zoom);
    const recentred = { x: 50, y: 130, w: 400, h: 200 };
    const pinned = pinnedCamera(anchor, unit, recentred, zoom);
    expect(pinned.x + (recentred.x + recentred.w) * zoom).toBeCloseTo(anchor.x, 3);
    expect(pinned.y + recentred.y * zoom).toBeCloseTo(anchor.y, 3);
    const same = pinnedCamera(anchor, unit, Frame, zoom);
    expect(same.x).toBeCloseTo(camera.x, 3);
    expect(same.y).toBeCloseTo(camera.y, 3);
  });
  it('a press resizes by the handle, then by a band', () => {
    const frames = [{ id: 'block', frame: Frame }, { id: 'under', frame: { x: 380, y: 280, w: 300, h: 200 } }];
    expect(resizeTarget(395, 295, [frames[0]!], 1)).toEqual({ id: 'block', edges: BottomRight });
    expect(resizeTarget(250, 304, [frames[0]!], 1)).toEqual({ id: 'block', edges: Bottom });
    expect(resizeTarget(250, 308, [frames[0]!], 1)).toBeNull();
    expect(resizeTarget(250, 200, [frames[0]!], 1)).toBeNull();
    expect(resizeTarget(381, 281, frames, 1)).toEqual({ id: 'under', edges: Left | Top });
    expect(resizeTarget(372, 272, [frames[0]!], 1.5)?.edges).toBe(BottomRight);
    expect(resizeTarget(372, 272, [frames[0]!], 1)).toBeNull();
    expect(resizeTarget(97, 200, [frames[0]!], 1)).toEqual({ id: 'block', edges: Left });
    expect(resizeTarget(250, 95, [frames[0]!], 1)).toEqual({ id: 'block', edges: Top });
  });
  it('the cursor of the sides', () => {
    expect([edgesCursor(0), edgesCursor(Left), edgesCursor(Bottom), edgesCursor(Left | Top), edgesCursor(Right | Top), edgesCursor(Left | Bottom)])
      .toEqual(['', 'ew-resize', 'ns-resize', 'nwse-resize', 'nesw-resize', 'nesw-resize']);
  });
});
