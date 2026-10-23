// The pane header's clock, ticking in the page each second: AgentRunTiming.Label (C/AgentRunTiming.cs) — mm:ss, h:mm:ss from an
// hour, counted to the finish or to now, "≈" through the locale's template when the time is approximate.
import type { AgentRunTiming } from '../store/models';

export function isValidTiming(timing: AgentRunTiming): boolean {
  const started = Date.parse(timing.startedAt);
  const observed = Date.parse(timing.lastObservedAt);
  const finished = timing.finishedAt ? Date.parse(timing.finishedAt) : null;
  return Number.isFinite(started) && Number.isFinite(observed) && observed >= started && (finished === null || finished >= observed);
}

export function elapsedSeconds(timing: AgentRunTiming, now: number): number {
  if (!isValidTiming(timing)) return 0;
  const end = timing.finishedAt ? Date.parse(timing.finishedAt) : now;
  return Math.max(0, Math.floor((end - Date.parse(timing.startedAt)) / 1000));
}

const two = (value: number) => String(value).padStart(2, '0');

/** `approximate` is the `dashboard.card.approximate` template, holding `{time}`. */
export function clockLabel(timing: AgentRunTiming, now: number, approximate: string): string {
  const seconds = elapsedSeconds(timing, now);
  const label = seconds >= 3600 ? `${Math.floor(seconds / 3600)}:${two(Math.floor(seconds / 60) % 60)}:${two(seconds % 60)}` : `${two(Math.floor(seconds / 60))}:${two(seconds % 60)}`;
  return timing.isApproximate ? approximate.replace('{time}', label) : label;
}
