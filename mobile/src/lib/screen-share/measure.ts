import type {
  ScreenCandidateType,
  ScreenCodec,
  ScreenMode,
  ScreenQuality,
} from '@/api/types';
import { TapLatencyTracker, type LatencySummary } from '@/lib/screen-share/latency';
import {
  bitrateSummary,
  decodeMsPerFrame,
  type BitrateSummary,
  type ScreenStatsSample,
} from '@/lib/screen-share/stats';

/**
 * The measurement overlay's numbers, gathered from `getStats` readings and taps. Nothing
 * here leaves the phone unless the user exports it through the share sheet.
 */

/** About ten minutes of one-second readings. */
export const MEASURE_MAX_SAMPLES = 600;

export interface MeasurementSummary {
  rttMs?: number;
  jitterMs?: number;
  decodeMsPerFrame?: number;
  freezeCount?: number;
  freezeSeconds?: number;
  candidateType?: ScreenCandidateType;
  frameWidth?: number;
  frameHeight?: number;
  latency: LatencySummary;
  bitrate: BitrateSummary;
}

export class ScreenMeasurement {
  readonly latency = new TapLatencyTracker();
  private readonly readings: ScreenStatsSample[] = [];

  get samples(): readonly ScreenStatsSample[] {
    return this.readings;
  }

  /** A tap went out; the next readings decide how long it took to show. */
  tapped(at: number): void {
    this.latency.tapped(at);
  }

  /**
   * One reading. Fast probe readings feed the latency tracker; only readings at least
   * `minIntervalMs` apart are kept for the bitrate series, so a burst of probe polls does
   * not skew it.
   */
  record(sample: ScreenStatsSample, minIntervalMs = 900): void {
    this.latency.observe(sample);
    const last = this.readings[this.readings.length - 1];
    if (last && sample.at - last.at < minIntervalMs) return;
    this.readings.push(sample);
    if (this.readings.length > MEASURE_MAX_SAMPLES) this.readings.shift();
  }

  summary(): MeasurementSummary {
    const first = this.readings[0];
    const last = this.readings[this.readings.length - 1];
    const summary: MeasurementSummary = {
      latency: this.latency.summary(),
      bitrate: bitrateSummary(this.readings),
    };
    if (!last) return summary;
    if (last.rttMs !== undefined) summary.rttMs = last.rttMs;
    if (last.jitterMs !== undefined) summary.jitterMs = last.jitterMs;
    if (last.candidateType) summary.candidateType = last.candidateType;
    if (last.frameWidth !== undefined && last.frameHeight !== undefined) {
      summary.frameWidth = last.frameWidth;
      summary.frameHeight = last.frameHeight;
    }
    if (first) {
      const decode = decodeMsPerFrame(first, last);
      if (decode !== undefined) summary.decodeMsPerFrame = decode;
    }
    if (last.freezeCount !== undefined) {
      summary.freezeCount = last.freezeCount - (first?.freezeCount ?? 0);
    }
    if (last.totalFreezesDurationS !== undefined) {
      summary.freezeSeconds = last.totalFreezesDurationS - (first?.totalFreezesDurationS ?? 0);
    }
    return summary;
  }
}

/** The export's format name; bump it when a field changes meaning. */
export const MEASUREMENT_FORMAT = 'mightyclaude.screen-measurement/1';

export interface MeasurementReport {
  format: typeof MEASUREMENT_FORMAT;
  /** ISO 8601, phone clock. */
  exportedAt: string;
  session: {
    mode?: ScreenMode;
    codec?: ScreenCodec;
    quality?: ScreenQuality;
  };
  /** False until the Mac draws a marker at each tap; see `latency.ts`. */
  hostTapMarker: boolean;
  summary: MeasurementSummary;
  /** Every tap-to-visible sample, ms. */
  latencySamplesMs: number[];
  /** The readings behind the bitrate split, seconds since the first. */
  series: {
    t: number;
    bytesReceived: number;
    framesDecoded: number;
    rttMs?: number;
    jitterMs?: number;
  }[];
}

function rounded(value: number, digits = 1): number {
  const scale = 10 ** digits;
  return Math.round(value * scale) / scale;
}

/** The export: the summary, the raw latency samples and the reading series. */
export function measurementReport(
  measurement: ScreenMeasurement,
  context: {
    exportedAt: number;
    mode?: ScreenMode | undefined;
    codec?: ScreenCodec | undefined;
    quality?: ScreenQuality | undefined;
  },
): MeasurementReport {
  const start = measurement.samples[0]?.at ?? 0;
  const session: MeasurementReport['session'] = {};
  if (context.mode) session.mode = context.mode;
  if (context.codec) session.codec = context.codec;
  if (context.quality) session.quality = context.quality;
  return {
    format: MEASUREMENT_FORMAT,
    exportedAt: new Date(context.exportedAt).toISOString(),
    session,
    hostTapMarker: false,
    summary: measurement.summary(),
    latencySamplesMs: measurement.latency.samples.map((value) => rounded(value)),
    series: measurement.samples.map((sample) => {
      const row: MeasurementReport['series'][number] = {
        t: rounded((sample.at - start) / 1000, 3),
        bytesReceived: sample.bytesReceived,
        framesDecoded: sample.framesDecoded,
      };
      if (sample.rttMs !== undefined) row.rttMs = rounded(sample.rttMs);
      if (sample.jitterMs !== undefined) row.jitterMs = rounded(sample.jitterMs);
      return row;
    }),
  };
}

/** Pretty JSON for the share sheet. */
export function formatMeasurementReport(report: MeasurementReport): string {
  return JSON.stringify(report, null, 2);
}
