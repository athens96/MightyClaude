import { resetLanguage } from '@/lib/i18n';
import {
  MEASUREMENT_FORMAT,
  ScreenMeasurement,
  formatMeasurementReport,
  measurementReport,
} from '@/lib/screen-share/measure';
import { measurementLines, measurementMarkerNote } from '@/lib/screen-share/strings';

/** The overlay's numbers and the file the share sheet hands on. */

afterEach(() => resetLanguage());

function filled(): ScreenMeasurement {
  const measurement = new ScreenMeasurement();
  const base = { candidateType: 'host' as const, frameWidth: 1920, frameHeight: 1080 };
  measurement.record({ ...base, at: 0, framesDecoded: 0, bytesReceived: 0, rttMs: 12, jitterMs: 2, totalDecodeTimeS: 0, freezeCount: 1, totalFreezesDurationS: 0.2 });
  measurement.record({ ...base, at: 1000, framesDecoded: 30, bytesReceived: 250_000, rttMs: 14, jitterMs: 3, totalDecodeTimeS: 0.09, freezeCount: 1, totalFreezesDurationS: 0.2 });
  measurement.tapped(1500);
  measurement.record({ ...base, at: 1505, framesDecoded: 30, bytesReceived: 250_000 });
  measurement.record({ ...base, at: 1580, framesDecoded: 31, bytesReceived: 251_000 });
  measurement.record({ ...base, at: 2000, framesDecoded: 31, bytesReceived: 251_000, rttMs: 16, jitterMs: 4, totalDecodeTimeS: 0.093, freezeCount: 2, totalFreezesDurationS: 0.5 });
  return measurement;
}

describe('the summary', () => {
  it('keeps the latest path and timings, and counts freezes and decode time over the window', () => {
    const summary = filled().summary();
    expect(summary.rttMs).toBe(16);
    expect(summary.jitterMs).toBe(4);
    expect(summary.candidateType).toBe('host');
    expect(summary.decodeMsPerFrame).toBeCloseTo(3);
    expect(summary.freezeCount).toBe(1);
    expect(summary.freezeSeconds).toBeCloseTo(0.3);
    expect(summary.latency).toEqual({ count: 1, p50Ms: 80, p95Ms: 80, timeouts: 0 });
    expect(summary.bitrate.activeKbps).toBeCloseTo(2000);
  });

  it('keeps only the one-second readings in the series, not the probe’s fast ones', () => {
    expect(filled().samples.map((sample) => sample.at)).toEqual([0, 1000, 2000]);
  });
});

describe('the export', () => {
  it('is versioned JSON with the session, the summary, every latency sample and the series', () => {
    const report = measurementReport(filled(), {
      exportedAt: Date.UTC(2026, 9, 2, 3, 4, 5),
      mode: 'control',
      codec: 'H264',
      quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
    });
    expect(report.format).toBe(MEASUREMENT_FORMAT);
    expect(report.exportedAt).toBe('2026-10-02T03:04:05.000Z');
    expect(report.session).toEqual({
      mode: 'control',
      codec: 'H264',
      quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
    });
    expect(report.hostTapMarker).toBe(false);
    expect(report.latencySamplesMs).toEqual([80]);
    expect(report.series).toEqual([
      { t: 0, bytesReceived: 0, framesDecoded: 0, rttMs: 12, jitterMs: 2 },
      { t: 1, bytesReceived: 250_000, framesDecoded: 30, rttMs: 14, jitterMs: 3 },
      { t: 2, bytesReceived: 251_000, framesDecoded: 31, rttMs: 16, jitterMs: 4 },
    ]);
    const parsed = JSON.parse(formatMeasurementReport(report));
    expect(parsed).toEqual(JSON.parse(JSON.stringify(report)));
  });

  it('leaves out what it does not know rather than inventing it', () => {
    const report = measurementReport(new ScreenMeasurement(), { exportedAt: 0 });
    expect(report.session).toEqual({});
    expect(report.series).toEqual([]);
    expect(report.summary.latency).toEqual({ count: 0, timeouts: 0 });
  });
});

describe('the overlay lines', () => {
  it('fill every number in both languages, and say plainly what is unknown', () => {
    for (const language of ['ko', 'en'] as const) {
      resetLanguage(language);
      const lines = measurementLines(filled().summary());
      expect(lines).toHaveLength(4);
      for (const line of lines) {
        expect(line).not.toContain('phone.screenShare');
        expect(line).not.toMatch(/\{[a-z0-9]+\}/i);
      }
      expect(lines[2]).toContain('80');
      expect(measurementLines(undefined)[0]).toContain('—');
      expect(measurementMarkerNote()).not.toContain('phone.screenShare');
    }
  });
});
