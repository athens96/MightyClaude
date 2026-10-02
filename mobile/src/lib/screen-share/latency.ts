import { percentile } from '@/lib/screen-share/stats';

/**
 * Tap-to-visible latency, timed on the phone's clock.
 *
 * With a Mac that draws the tap marker (`tapMarker` in its state, docs/relay.md), a timed
 * tap carries a marker id. The Mac clicks, draws a ring at the point, and echoes the id once
 * the ring is on screen. The probe takes its frame baseline from the first `getStats`
 * reading after that echo and ends at the first reading past it: video goes through encode,
 * jitter buffer and decode, so the ring's frame lands after the echo, and the ring guarantees
 * a frame even when the tap itself changed nothing. A `shown:false` echo (the Mac refused
 * the tap) drops the probe and counts it as refused.
 *
 * With an older Mac there is no marker. The probe then takes its baseline right after the
 * tap and ends at the first new frame: right on a still screen, short on a moving one, and a
 * tap that changes nothing times out instead of reporting a number.
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
  /** Marked taps the Mac answered `shown:false`; present once there is one. */
  refused?: number;
  /** Tap → marker echo, the input path alone; present once there is a sample. */
  echoP50Ms?: number;
}

/** What the Mac sends back down the data channel for the measurement. */
export type HostMeasurementNote =
  | { t: 'marker'; id: string; shown: boolean }
  | { t: 'scene'; phase: ScenePhase };

/** The Mac's reference scene, in order (docs/relay.md, "측정 장면"). */
export const SCENE_PHASES = ['preroll', 'motion', 'still', 'done'] as const;
export type ScenePhase = (typeof SCENE_PHASES)[number];

/** Reads a `marker` echo or a `scene` note; anything else is not one of ours. */
export function parseHostMeasurementNote(message: unknown): HostMeasurementNote | undefined {
  if (message === null || typeof message !== 'object') return undefined;
  const raw = message as Record<string, unknown>;
  if (raw.t === 'marker') {
    if (typeof raw.id !== 'string' || typeof raw.shown !== 'boolean') return undefined;
    return { t: 'marker', id: raw.id, shown: raw.shown };
  }
  if (raw.t === 'scene') {
    const phase = SCENE_PHASES.find((value) => value === raw.phase);
    return phase ? { t: 'scene', phase } : undefined;
  }
  return undefined;
}

/** A marker id for the `sequence`-th timed tap at phone time `at`: short, unique, `[a-z0-9-]`. */
export function tapMarkerId(at: number, sequence: number): string {
  return `m${Math.max(0, Math.floor(at)).toString(36)}-${Math.max(0, Math.floor(sequence)).toString(36)}`.slice(0, 32);
}

interface Probe {
  tappedAt: number;
  baseline?: number;
  /** Set for a marked tap; the probe waits for its echo before it counts frames. */
  markerId?: string;
  echoAt?: number;
}

export class TapLatencyTracker {
  private probe: Probe | undefined;
  private readonly values: number[] = [];
  private readonly echoes: number[] = [];
  private timedOut = 0;
  private refusedCount = 0;

  /** True while a tap is waiting for its frame: the caller polls fast meanwhile. */
  get pending(): boolean {
    return this.probe !== undefined;
  }

  get samples(): readonly number[] {
    return this.values;
  }

  /** Tap → marker echo, ms. */
  get echoSamples(): readonly number[] {
    return this.echoes;
  }

  /**
   * A tap went out at `at`, with the marker id it carried when the Mac draws markers. A
   * probe still open from an earlier tap is abandoned.
   */
  tapped(at: number, markerId?: string): void {
    this.probe = markerId === undefined ? { tappedAt: at } : { tappedAt: at, markerId };
  }

  /** The Mac's echo for a marked tap. An echo for any other tap is ignored. */
  marker(id: string, shown: boolean, at: number): void {
    const probe = this.probe;
    if (!probe || probe.markerId !== id || probe.echoAt !== undefined) return;
    if (!shown) {
      this.refusedCount += 1;
      this.probe = undefined;
      return;
    }
    probe.echoAt = at;
    delete probe.baseline;
    this.echoes.push(Math.max(0, at - probe.tappedAt));
    if (this.echoes.length > LATENCY_MAX_SAMPLES) this.echoes.shift();
  }

  /**
   * Gives up on a probe that has waited past the timeout by the wall clock, even when no
   * reading arrives to notice it (stats stopped answering, the session went away).
   */
  expire(now: number): void {
    const probe = this.probe;
    if (!probe || now - probe.tappedAt <= LATENCY_PROBE_TIMEOUT_MS) return;
    this.timedOut += 1;
    this.probe = undefined;
  }

  /** One `getStats` reading. The first one that counts only sets the baseline. */
  observe(sample: { at: number; framesDecoded: number }): void {
    const probe = this.probe;
    if (!probe || sample.at < probe.tappedAt) return;
    if (sample.at - probe.tappedAt > LATENCY_PROBE_TIMEOUT_MS) {
      this.timedOut += 1;
      this.probe = undefined;
      return;
    }
    if (probe.markerId !== undefined) {
      // A marked tap counts frames only from its echo on.
      if (probe.echoAt === undefined || sample.at < probe.echoAt) return;
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
    if (this.refusedCount > 0) summary.refused = this.refusedCount;
    const echo = percentile(this.echoes, 50);
    if (echo !== undefined) summary.echoP50Ms = echo;
    return summary;
  }
}
