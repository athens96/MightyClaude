import { TAP_MARKER_ID } from '@/lib/screen-share/input';
import {
  LATENCY_PROBE_TIMEOUT_MS,
  TapLatencyTracker,
  parseHostMeasurementNote,
  tapMarkerId,
} from '@/lib/screen-share/latency';

/** Tap → first new decoded frame, measured on the phone's own clock. */

describe('tap-to-visible latency', () => {
  it('counts from the tap to the first reading past the post-tap baseline', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(1000);
    expect(tracker.pending).toBe(true);
    tracker.observe({ at: 1005, framesDecoded: 40 }); // baseline
    tracker.observe({ at: 1060, framesDecoded: 40 });
    tracker.observe({ at: 1120, framesDecoded: 41 });
    expect(tracker.pending).toBe(false);
    expect(tracker.samples).toEqual([120]);
  });

  it('reports p50 and p95 over the samples', () => {
    const tracker = new TapLatencyTracker();
    for (const latency of [100, 110, 120, 130, 300]) {
      tracker.tapped(0);
      tracker.observe({ at: 0, framesDecoded: 0 });
      tracker.observe({ at: latency, framesDecoded: 1 });
    }
    expect(tracker.summary()).toEqual({ count: 5, p50Ms: 120, p95Ms: 266, timeouts: 0 });
  });

  it('drops a tap that brought no new frame in time, and counts it as a timeout', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(0);
    tracker.observe({ at: 10, framesDecoded: 5 });
    tracker.observe({ at: LATENCY_PROBE_TIMEOUT_MS + 11, framesDecoded: 6 });
    expect(tracker.samples).toEqual([]);
    expect(tracker.summary()).toEqual({ count: 0, timeouts: 1 });
  });

  it('ignores readings with no tap pending, and a new tap replaces an open probe', () => {
    const tracker = new TapLatencyTracker();
    tracker.observe({ at: 5, framesDecoded: 9 });
    tracker.tapped(100);
    tracker.observe({ at: 101, framesDecoded: 9 });
    tracker.tapped(200);
    tracker.observe({ at: 201, framesDecoded: 9 });
    tracker.observe({ at: 290, framesDecoded: 10 });
    expect(tracker.samples).toEqual([90]);
  });
});

describe('a probe nothing answers', () => {
  it('expires by the wall clock alone, once, and not before the timeout', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(1_000);
    tracker.expire(1_000 + LATENCY_PROBE_TIMEOUT_MS);
    expect(tracker.pending).toBe(true);
    tracker.expire(1_001 + LATENCY_PROBE_TIMEOUT_MS);
    expect(tracker.pending).toBe(false);
    tracker.expire(9_999_999);
    expect(tracker.summary()).toEqual({ count: 0, timeouts: 1 });
  });
});

describe('tap-to-visible against the Mac’s tap marker', () => {
  it('counts frames only from the echo on, so frames already moving before it do not end the probe', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(1000, 'm1');
    // The screen is moving: new frames arrive before the Mac has drawn anything.
    tracker.observe({ at: 1010, framesDecoded: 50 });
    tracker.observe({ at: 1040, framesDecoded: 52 });
    expect(tracker.pending).toBe(true);
    tracker.marker('m1', true, 1045);
    tracker.observe({ at: 1046, framesDecoded: 52 }); // baseline, right after the echo
    tracker.observe({ at: 1076, framesDecoded: 52 });
    tracker.observe({ at: 1106, framesDecoded: 53 });
    expect(tracker.pending).toBe(false);
    expect(tracker.samples).toEqual([106]);
    expect(tracker.echoSamples).toEqual([45]);
    expect(tracker.summary()).toEqual({ count: 1, p50Ms: 106, p95Ms: 106, timeouts: 0, echoP50Ms: 45 });
  });

  it('drops a tap the Mac refused and counts it, instead of waiting for a frame that will not come', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(0, 'm2');
    tracker.marker('m2', false, 30);
    expect(tracker.pending).toBe(false);
    tracker.observe({ at: 40, framesDecoded: 1 });
    tracker.observe({ at: 80, framesDecoded: 2 });
    expect(tracker.samples).toEqual([]);
    expect(tracker.summary()).toEqual({ count: 0, timeouts: 0, refused: 1 });
  });

  it('ignores an echo for another tap, and times out a marked tap whose echo never came', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(0, 'm3');
    tracker.marker('old', true, 10);
    tracker.observe({ at: 20, framesDecoded: 1 });
    tracker.observe({ at: 40, framesDecoded: 2 });
    expect(tracker.pending).toBe(true);
    tracker.observe({ at: LATENCY_PROBE_TIMEOUT_MS + 1, framesDecoded: 3 });
    expect(tracker.pending).toBe(false);
    expect(tracker.summary()).toEqual({ count: 0, timeouts: 1 });
  });

  it('keeps the still-screen method for a tap without a marker (an older Mac)', () => {
    const tracker = new TapLatencyTracker();
    tracker.tapped(0);
    tracker.marker('m4', true, 5);
    tracker.observe({ at: 10, framesDecoded: 7 });
    tracker.observe({ at: 70, framesDecoded: 8 });
    expect(tracker.samples).toEqual([70]);
    expect(tracker.echoSamples).toEqual([]);
  });
});

describe('the Mac’s measurement notes', () => {
  it('reads marker echoes and scene phases, and nothing else', () => {
    expect(parseHostMeasurementNote({ t: 'marker', id: 'm1', shown: true })).toEqual({
      t: 'marker',
      id: 'm1',
      shown: true,
    });
    expect(parseHostMeasurementNote({ t: 'scene', phase: 'still' })).toEqual({ t: 'scene', phase: 'still' });
    expect(parseHostMeasurementNote({ t: 'scene', phase: 'later' })).toBeUndefined();
    expect(parseHostMeasurementNote({ t: 'marker', id: 'm1' })).toBeUndefined();
    expect(parseHostMeasurementNote({ t: 'clipboard', dir: 'to-phone' })).toBeUndefined();
    expect(parseHostMeasurementNote('marker')).toBeUndefined();
    expect(parseHostMeasurementNote(null)).toBeUndefined();
  });

  it('makes marker ids the Mac accepts', () => {
    const ids = new Set<string>();
    for (let sequence = 1; sequence <= 50; sequence += 1) {
      const id = tapMarkerId(1_760_000_000_000 + sequence, sequence);
      expect(id).toMatch(TAP_MARKER_ID);
      ids.add(id);
    }
    expect(ids.size).toBe(50);
    expect(tapMarkerId(Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER)).toMatch(TAP_MARKER_ID);
  });
});
