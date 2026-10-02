import { LATENCY_PROBE_TIMEOUT_MS, TapLatencyTracker } from '@/lib/screen-share/latency';

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
