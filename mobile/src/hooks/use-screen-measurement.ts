import { useCallback, useEffect, useRef, useState } from 'react';
import { Share } from 'react-native';
import type { ScreenShareController } from '@/lib/screen-share/controller';
import type { NormalizedPoint } from '@/lib/screen-share/input';
import { tapMarkerId } from '@/lib/screen-share/latency';
import {
  ScreenMeasurement,
  formatMeasurementReport,
  measurementReport,
  pollTapProbe,
  type MeasurementSummary,
} from '@/lib/screen-share/measure';
import { readStatsSample } from '@/lib/screen-share/stats';

/**
 * The measurement overlay's engine: reads `getStats` once a second while the overlay is
 * on and the session is live, and every 30 ms while a tap waits for its frame, so a
 * tap-to-visible sample is accurate to a frame or two rather than to a second.
 *
 * With a Mac that draws the tap marker (`hostTapMarker`), each timed tap carries a marker
 * id and the probe runs from the Mac's echo (see `latency.ts`); otherwise it falls back to
 * the first new frame after the tap.
 */

const POLL_MS = 1_000;
const PROBE_POLL_MS = 30;

export interface ScreenMeasurementHook {
  summary: MeasurementSummary | undefined;
  /** Sends a tap. While the overlay is on it is timed, with a marker when the Mac draws one. */
  tap(point: NormalizedPoint): boolean;
  /** Opens the share sheet with the JSON report. */
  exportReport(): Promise<void>;
}

export function useScreenMeasurement(
  controller: ScreenShareController | undefined,
  options: { enabled: boolean; live: boolean; hostTapMarker: boolean; exportTitle: string },
): ScreenMeasurementHook {
  const measurement = useRef(new ScreenMeasurement());
  const [summary, setSummary] = useState<MeasurementSummary | undefined>(undefined);
  const stopProbe = useRef<(() => void) | undefined>(undefined);
  const tapCount = useRef(0);
  const active = options.enabled && options.live && controller !== undefined;
  // Read by a probe poll that is already in flight: it stops once this turns false.
  const activeRef = useRef(active);
  activeRef.current = active;

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
      stopProbe.current?.();
      stopProbe.current = undefined;
    };
  }, [active, read]);

  // Unmounted: nothing may poll on behind the screen.
  useEffect(
    () => () => {
      activeRef.current = false;
      stopProbe.current?.();
      stopProbe.current = undefined;
    },
    [],
  );

  // The Mac's echoes and scene phases, while the overlay is on.
  useEffect(() => {
    if (!active || !controller) return undefined;
    return controller.onMeasurementNote((note) => {
      if (note.t === 'marker') {
        measurement.current.markerEcho(note.id, note.shown, Date.now());
        // The baseline is the first reading after the echo: take it now.
        void read();
      } else {
        measurement.current.scenePhase(note.phase, Date.now());
      }
      setSummary(measurement.current.summary());
    });
  }, [active, controller, read]);

  const tap = useCallback(
    (point: NormalizedPoint): boolean => {
      if (!controller) return false;
      if (!active) return controller.tap(point);
      const at = Date.now();
      tapCount.current += 1;
      const marker = options.hostTapMarker ? tapMarkerId(at, tapCount.current) : undefined;
      if (!controller.tap(point, marker)) return false;
      measurement.current.tapped(at, marker);
      stopProbe.current?.();
      stopProbe.current = pollTapProbe(measurement.current, {
        read,
        isActive: () => activeRef.current,
        now: () => Date.now(),
        intervalMs: PROBE_POLL_MS,
      });
      return true;
    },
    [active, controller, options.hostTapMarker, read],
  );

  const exportReport = useCallback(async () => {
    const session = controller?.snapshot().session;
    const report = measurementReport(measurement.current, {
      exportedAt: Date.now(),
      mode: session?.mode,
      codec: session?.codec,
      quality: session?.quality,
      hostTapMarker: options.hostTapMarker,
    });
    await Share.share({ title: options.exportTitle, message: formatMeasurementReport(report) });
  }, [controller, options.exportTitle, options.hostTapMarker]);

  return { summary, tap, exportReport };
}
