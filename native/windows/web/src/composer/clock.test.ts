import { describe, expect, it } from 'vitest';
import { clockLabel, isValidTiming } from './clock';

const start = '2026-10-24T00:00:00Z';
const at = (seconds: number) => Date.parse(start) + seconds * 1000;

describe('clockLabel', () => {
  it('counts to now while running, as mm:ss then h:mm:ss', () => {
    const timing = { startedAt: start, lastObservedAt: start, isApproximate: false };
    expect(clockLabel(timing, at(65), '≈{time}')).toBe('01:05');
    expect(clockLabel(timing, at(3600 + 62), '≈{time}')).toBe('1:01:02');
  });
  it('stops at the finish and marks an approximate time', () => {
    const timing = { startedAt: start, lastObservedAt: '2026-10-24T00:00:09Z', finishedAt: '2026-10-24T00:00:09Z', isApproximate: true };
    expect(clockLabel(timing, at(500), '≈{time}')).toBe('≈00:09');
  });
  it('reads an invalid timing as zero', () => {
    const timing = { startedAt: start, lastObservedAt: '2026-10-23T00:00:00Z', isApproximate: false };
    expect(isValidTiming(timing)).toBe(false);
    expect(clockLabel(timing, at(30), '{time}')).toBe('00:00');
  });
});
