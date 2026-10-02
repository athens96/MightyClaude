import { useCallback, useEffect, useRef, useState } from 'react';
import { Share } from 'react-native';
import type { ScreenShareController } from '@/lib/screen-share/controller';
import {
  ScreenMeasurement,
  formatMeasurementReport,
  measurementReport,
  type MeasurementSummary,
} from '@/lib/screen-share/measure';
import { readStatsSample } from '@/lib/screen-share/stats';

/**
 * The measurement overlay's engine: reads `getStats` once a second while the overlay is
 * on and the session is live, and every 30 ms while a tap waits for its frame, so a
 * tap-to-visible sample is accurate to a frame or two rather than to a second.
 */

const POLL_MS = 1_000;
const PROBE_POLL_MS = 30;

export interface ScreenMeasurementHook {
  summary: MeasurementSummary | undefined;
  /** Call after a tap went out; starts a latency probe while the overlay is on. */
  markTap(): void;
  /** Opens the share sheet with the JSON report. */
  exportReport(): Promise<void>;
}

export function useScreenMeasurement(
  controller: ScreenShareController | undefined,
  options: { enabled: boolean; live: boolean; exportTitle: string },
): ScreenMeasurementHook {
  const measurement = useRef(new ScreenMeasurement());
  const [summary, setSummary] = useState<MeasurementSummary | undefined>(undefined);
  const probeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const active = options.enabled && options.live && controller !== undefined;

  const read = useCallback(async () => {
    if (!controller) return;
    const report = await controller.statsReport();
    if (report === undefined) return;
    measurement.current.record(readStatsSample(report, Date.now()));
    setSummary(measurement.current.summary());
  }, [controller]);

  // A new controller is a new host: its numbers start from nothing.
  useEffect(() => {
    measurement.current = new ScreenMeasurement();
    setSummary(undefined);
  }, [controller]);

  useEffect(() => {
    if (!active) return undefined;
    void read();
    const timer = setInterval(() => void read(), POLL_MS);
    return () => {
      clearInterval(timer);
      if (probeTimer.current) clearTimeout(probeTimer.current);
      probeTimer.current = undefined;
    };
  }, [active, read]);

  const markTap = useCallback(() => {
    if (!active) return;
    measurement.current.tapped(Date.now());
    if (probeTimer.current) clearTimeout(probeTimer.current);
    const poll = () => {
      void read().then(() => {
        if (!measurement.current.latency.pending) {
          probeTimer.current = undefined;
          return;
        }
        probeTimer.current = setTimeout(poll, PROBE_POLL_MS);
      });
    };
    poll();
  }, [active, read]);

  const exportReport = useCallback(async () => {
    const session = controller?.snapshot().session;
    const report = measurementReport(measurement.current, {
      exportedAt: Date.now(),
      mode: session?.mode,
      codec: session?.codec,
      quality: session?.quality,
    });
    await Share.share({ title: options.exportTitle, message: formatMeasurementReport(report) });
  }, [controller, options.exportTitle]);

  return { summary, markTap, exportReport };
}
