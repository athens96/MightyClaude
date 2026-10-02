import { ACTIVE_FPS_THRESHOLD, bitrateSummary, decodeMsPerFrame } from '@/lib/screen-share/stats';

/**
 * Active vs idle bitrate: the Mac sends nothing for a still screen but one refresh every
 * 5 s, so the idle figure should be close to zero and the active one near the ceiling.
 */

function series(steps: { ms: number; bytes: number; frames: number }[]) {
  let at = 0;
  let bytesReceived = 0;
  let framesDecoded = 0;
  const out = [{ at, bytesReceived, framesDecoded }];
  for (const step of steps) {
    at += step.ms;
    bytesReceived += step.bytes;
    framesDecoded += step.frames;
    out.push({ at, bytesReceived, framesDecoded });
  }
  return out;
}

describe('bitrate split by motion', () => {
  it('averages each kind over its own time', () => {
    const summary = bitrateSummary(
      series([
        // Two seconds moving at 30 fps, 250 kB/s = 2000 kbps.
        { ms: 1000, bytes: 250_000, frames: 30 },
        { ms: 1000, bytes: 250_000, frames: 30 },
        // Three seconds still: one refresh frame, 3 kB in total.
        { ms: 1000, bytes: 3_000, frames: 1 },
        { ms: 1000, bytes: 0, frames: 0 },
        { ms: 1000, bytes: 0, frames: 0 },
      ]),
    );
    expect(summary.activeKbps).toBeCloseTo(2000);
    expect(summary.idleKbps).toBeCloseTo(8);
    expect(summary.activeSeconds).toBe(2);
    expect(summary.idleSeconds).toBe(3);
  });

  it('draws the line at the threshold frame rate', () => {
    const atThreshold = bitrateSummary(series([{ ms: 1000, bytes: 1000, frames: ACTIVE_FPS_THRESHOLD }]));
    expect(atThreshold.activeSeconds).toBe(1);
    const below = bitrateSummary(series([{ ms: 1000, bytes: 1000, frames: ACTIVE_FPS_THRESHOLD - 1 }]));
    expect(below.idleSeconds).toBe(1);
  });

  it('skips an interval whose counters went backwards, and reports nothing without data', () => {
    const summary = bitrateSummary([
      { at: 0, bytesReceived: 1000, framesDecoded: 50 },
      { at: 1000, bytesReceived: 10, framesDecoded: 1 },
    ]);
    expect(summary).toEqual({ activeSeconds: 0, idleSeconds: 0 });
    expect(bitrateSummary([])).toEqual({ activeSeconds: 0, idleSeconds: 0 });
  });
});

describe('decode time per frame', () => {
  it('divides the decode time spent by the frames decoded in between', () => {
    expect(decodeMsPerFrame({ framesDecoded: 100, totalDecodeTimeS: 0.5 }, { framesDecoded: 400, totalDecodeTimeS: 1.4 })).toBeCloseTo(3);
  });

  it('has nothing to say without new frames or without the counter', () => {
    expect(decodeMsPerFrame({ framesDecoded: 1, totalDecodeTimeS: 1 }, { framesDecoded: 1, totalDecodeTimeS: 1 })).toBeUndefined();
    expect(decodeMsPerFrame({ framesDecoded: 1 }, { framesDecoded: 9 })).toBeUndefined();
  });
});
