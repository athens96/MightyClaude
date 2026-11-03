import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { WorkMapVersion } from '@/api/types';
import {
  LANE_ORDER,
  chipDate,
  dotSize,
  laneColor,
  laneShares,
  shortDate,
  statusLabel,
  topicCommits,
  topicRequests,
  topicsWithRequests,
  versionName,
} from '@/lib/work-map';
import { darkPalette, lightPalette } from '@/theme';

const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/work-map.json'), 'utf8'),
) as {
  lanes: string[];
  dotSize: { cases: { count: number; largest: number; size: number }[] };
};

function version(over: Partial<WorkMapVersion>): WorkMapVersion {
  return {
    key: 'next',
    version: null,
    tag: null,
    date: '2026-11-02',
    status: 'unreleased',
    releaseStep: null,
    rangeEnd: 'x',
    total: 0,
    truncated: false,
    counts: {},
    commits: [],
    topics: null,
    topicsStale: false,
    summaryError: null,
    requests: [],
    ...over,
  };
}

describe('work map display rules', () => {
  it('lists the lanes in the host order', () => {
    expect([...LANE_ORDER]).toEqual(fixture.lanes);
  });

  it.each(fixture.dotSize.cases)('sizes a dot of $count of $largest as $size', ({ count, largest, size }) => {
    expect(dotSize(count, largest)).toBe(size);
  });

  it('splits the ratio bar by lane, in lane order, skipping empty lanes', () => {
    const shares = laneShares({ phone: 2, mac: 4, ci: 2 });
    expect(shares.map((entry) => entry.lane)).toEqual(['mac', 'phone', 'ci']);
    expect(shares.map((entry) => entry.share)).toEqual([0.5, 0.25, 0.25]);
    expect(laneShares({})).toEqual([]);
  });

  it('colours every lane differently in both themes', () => {
    for (const palette of [lightPalette, darkPalette]) {
      expect(new Set(LANE_ORDER.map((lane) => laneColor(palette, lane))).size).toBe(LANE_ORDER.length);
    }
  });

  it('writes dates as month/day and names an unnamed version as the next one', () => {
    expect(shortDate('2026-11-02')).toBe('11/02');
    expect(shortDate(null)).toBe('');
    expect(versionName(version({ version: '0.4.49' }))).toBe('0.4.49');
    expect(versionName(version({ tag: 'v0.4.48', key: 'v0.4.48' }))).toBe('0.4.48');
    expect(versionName(version({}))).not.toBe('');
  });

  it('words the status and the chip caption', () => {
    expect(statusLabel(version({ status: 'released', date: '2026-11-02' }))).toContain('11/02');
    expect(statusLabel(version({ status: 'releasing', releaseStep: 'ci' }))).toContain('ci');
    expect(chipDate(version({ status: 'released', date: '2026-11-02' }))).toBe('11/02');
    expect(chipDate(version({ status: 'releasing' }))).not.toContain('/');
  });

  it('takes a topic commits and requests from its version and filters by requests', () => {
    const base = version({
      commits: [
        { sha: 'a', date: '', lanes: ['mac'], scope: null, subject: 'A', filesCount: 1 },
        { sha: 'b', date: '', lanes: ['phone'], scope: null, subject: 'B', filesCount: 2 },
      ],
      requests: [{ id: 'p/r', paneId: 'p', text: 'do it', date: null, commits: ['a'] }],
    });
    const withRequest = { title: 'T', summary: '', lanes: ['mac' as const], commits: ['a'], requests: ['p/r'] };
    const without = { title: 'U', summary: '', lanes: ['phone' as const], commits: ['b'], requests: [] };
    expect(topicCommits(base, withRequest).map((commit) => commit.sha)).toEqual(['a']);
    expect(topicRequests(base, withRequest).map((request) => request.id)).toEqual(['p/r']);
    expect(topicRequests(base, without)).toEqual([]);
    expect(topicsWithRequests([withRequest, without])).toEqual([withRequest]);
  });
});
