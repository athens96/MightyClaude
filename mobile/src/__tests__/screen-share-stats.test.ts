import {
  percentile,
  readStatsSample,
  selectedCandidateType,
  statsEntries,
} from '@/lib/screen-share/stats';

/** `getStats` as react-native-webrtc returns it: a Map of report id → stats object. */
function report(entries: Record<string, Record<string, unknown>>) {
  return new Map(Object.entries(entries));
}

describe('reading getStats', () => {
  const stats = report({
    T: { type: 'transport', selectedCandidatePairId: 'P2' },
    P1: { type: 'candidate-pair', localCandidateId: 'L1', state: 'succeeded', currentRoundTripTime: 0.5 },
    P2: { type: 'candidate-pair', localCandidateId: 'L2', state: 'succeeded', currentRoundTripTime: 0.031 },
    L1: { type: 'local-candidate', candidateType: 'host' },
    L2: { type: 'local-candidate', candidateType: 'relay' },
    S: {
      type: 'inbound-rtp',
      kind: 'video',
      trackIdentifier: 'screen-s1',
      framesDecoded: 300,
      totalDecodeTime: 1.5,
      jitter: 0.004,
      freezeCount: 2,
      totalFreezesDuration: 0.9,
      bytesReceived: 1_000_000,
      frameWidth: 1920,
      frameHeight: 1200,
    },
    O: { type: 'inbound-rtp', kind: 'video', trackIdentifier: 'overview-s1', framesDecoded: 20, bytesReceived: 50_000 },
  });

  it('follows the transport’s own pointer to the selected pair', () => {
    expect(selectedCandidateType(statsEntries(stats))).toBe('relay');
  });

  it('reads the screen track, not the overview, and both tracks’ bytes', () => {
    expect(readStatsSample(stats, 1234)).toEqual({
      at: 1234,
      rttMs: 31,
      jitterMs: 4,
      framesDecoded: 300,
      totalDecodeTimeS: 1.5,
      freezeCount: 2,
      totalFreezesDurationS: 0.9,
      bytesReceived: 1_050_000,
      frameWidth: 1920,
      frameHeight: 1200,
      candidateType: 'relay',
    });
  });

  it('falls back to a selected or nominated pair when there is no transport entry', () => {
    const entries = statsEntries({
      P: { type: 'candidate-pair', localCandidateId: 'L', nominated: true, state: 'succeeded' },
      L: { type: 'local-candidate', candidateType: 'srflx' },
    });
    expect(selectedCandidateType(entries)).toBe('srflx');
  });

  it('copes with an empty or odd report', () => {
    expect(readStatsSample(undefined, 5)).toEqual({ at: 5, framesDecoded: 0, bytesReceived: 0 });
    expect(readStatsSample(new Map([['x', 3]]), 5)).toEqual({ at: 5, framesDecoded: 0, bytesReceived: 0 });
  });
});

describe('percentiles', () => {
  it('interpolates between the closest ranks', () => {
    expect(percentile([10, 20, 30, 40], 50)).toBe(25);
    expect(percentile([10, 20, 30, 40], 95)).toBeCloseTo(38.5);
    expect(percentile([10, 20, 30, 40], 0)).toBe(10);
    expect(percentile([10, 20, 30, 40], 100)).toBe(40);
  });

  it('does not care about input order, and ignores non-numbers', () => {
    expect(percentile([40, 10, Number.NaN, 30, 20], 50)).toBe(25);
  });

  it('is the value itself for one sample, and nothing for none', () => {
    expect(percentile([123], 95)).toBe(123);
    expect(percentile([], 50)).toBeUndefined();
  });

  it('matches a hand-worked p50/p95 over a hundred samples', () => {
    const values = Array.from({ length: 100 }, (_, index) => index + 1);
    expect(percentile(values, 50)).toBeCloseTo(50.5);
    expect(percentile(values, 95)).toBeCloseTo(95.05);
  });
});
