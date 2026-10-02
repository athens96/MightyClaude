import { percentile } from '@/lib/screen-share/stats';

/**
 * Tap-to-visible latency, the phone's half. A tap starts a probe; the probe ends at the
 * first `getStats` reading whose decoded-frame count is past the count seen right after
 * the tap. The Mac sends no frames for a still screen, so on a still screen that first new
 * frame is the tap's own effect.
 *
 * The contract's other half — the Mac drawing a marker at the tap so every tap is
 * guaranteed to change a pixel — is not in the Mac engine yet. Until it is, a tap that
 * changes nothing on the Mac produces no frame and the probe times out instead of
 * reporting a number, and a screen that is already moving makes the probe read short.
 */

/** A probe with no new frame by then is dropped, not counted. */
export const LATENCY_PROBE_TIMEOUT_MS = 2_000;
/** Keep the newest samples; a long session does not grow the list without bound. */
export const LATENCY_MAX_SAMPLES = 500;

export interface LatencySummary {
  count: number;
  p50Ms?: number;
  p95Ms?: number;
  /** Probes that saw no new frame within the timeout. */
  timeouts: number;
}

export class TapLatencyTracker {
  private probe: { tappedAt: number; baseline?: number } | undefined;
  private readonly values: number[] = [];
  private timedOut = 0;

  /** True while a tap is waiting for its frame: the caller polls fast meanwhile. */
  get pending(): boolean {
    return this.probe !== undefined;
  }

  get samples(): readonly number[] {
    return this.values;
  }

  /** A tap went out at `at`. A probe still open from an earlier tap is abandoned. */
  tapped(at: number): void {
    this.probe = { tappedAt: at };
  }

  /** One `getStats` reading. The first one after a tap only sets the baseline. */
  observe(sample: { at: number; framesDecoded: number }): void {
    const probe = this.probe;
    if (!probe || sample.at < probe.tappedAt) return;
    if (sample.at - probe.tappedAt > LATENCY_PROBE_TIMEOUT_MS) {
      this.timedOut += 1;
      this.probe = undefined;
      return;
    }
    if (probe.baseline === undefined) {
      probe.baseline = sample.framesDecoded;
      return;
    }
    if (sample.framesDecoded > probe.baseline) {
      this.values.push(sample.at - probe.tappedAt);
      if (this.values.length > LATENCY_MAX_SAMPLES) this.values.shift();
      this.probe = undefined;
    }
  }

  summary(): LatencySummary {
    const summary: LatencySummary = { count: this.values.length, timeouts: this.timedOut };
    const p50 = percentile(this.values, 50);
    const p95 = percentile(this.values, 95);
    if (p50 !== undefined) summary.p50Ms = p50;
    if (p95 !== undefined) summary.p95Ms = p95;
    return summary;
  }
}
