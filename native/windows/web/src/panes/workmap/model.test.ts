import { describe, expect, it } from 'vitest';
import fixture from '@tokens/work-map.json';
import { slimKinds, paneKindWords } from '../Panes';
import {
  chipLanes, dotSize, laneInk, laneShares, largestCount, overviewColumns, pickVersion, shortDate, shortStamp, topicCommits, topicDimmed, topicFiles, topicRequests, versionName, visibleTopics,
  type WorkMapCommit, type WorkMapModel, type WorkMapTopic, type WorkMapVersion,
} from './model';

const commit = (sha: string, lanes: string[], files: string[] = []): WorkMapCommit => ({ sha, date: '2026-11-02T10:00:00+09:00', lanes, subject: sha, files, filesCount: files.length });
const topic = (title: string, lanes: string[], commits: string[], requests: string[] = []): WorkMapTopic => ({ title, summary: '', lanes, commits, requests });
const version = (key: string, over: Partial<WorkMapVersion> = {}): WorkMapVersion => ({
  key, version: key.replace(/^v/, ''), status: 'released', rangeEnd: 'e', total: 0, truncated: false, counts: {}, commits: [], topicsStale: false, requests: [], ...over,
});

describe('the overview dots', () => {
  it('sizes them as the shared fixture does', () => {
    expect(fixture.dotSize.minimum).toBe(6);
    expect(fixture.dotSize.range).toBe(14);
    for (const c of fixture.dotSize.cases) expect(dotSize(c.count, c.largest)).toBe(c.size);
  });

  it('finds the largest lane count of any version', () => {
    expect(largestCount([version('a', { counts: { mac: 4, ci: 1 } }), version('b', { counts: { windows: 61 } })])).toBe(61);
    expect(largestCount([])).toBe(0);
  });

  it('lists the columns oldest first', () => {
    expect(overviewColumns([version('v3'), version('v2'), version('v1')]).map((v) => v.key)).toEqual(['v1', 'v2', 'v3']);
  });
});

describe('lanes', () => {
  it('draws them in the tokens: mac agent, windows run, phone done, ci wait, other stop', () => {
    expect(laneInk).toEqual({ mac: 'var(--agent)', windows: 'var(--run)', phone: 'var(--done)', ci: 'var(--wait)', etc: 'var(--stop)' });
    expect(Object.keys(laneInk)).toEqual(fixture.lanes);
    expect(chipLanes).toEqual(['mac', 'windows', 'phone', 'ci']);
  });

  it('shares a bar out in lane order', () => {
    expect(laneShares({ windows: 1, mac: 3 }, fixture.lanes)).toEqual([{ lane: 'mac', share: 0.75 }, { lane: 'windows', share: 0.25 }]);
    expect(laneShares({}, fixture.lanes)).toEqual([]);
  });

  it('dims a topic only when every platform it touched is off', () => {
    expect(topicDimmed(topic('t', ['mac', 'windows'], []), new Set(['mac']))).toBe(false);
    expect(topicDimmed(topic('t', ['mac', 'windows'], []), new Set(['mac', 'windows']))).toBe(true);
    expect(topicDimmed(topic('t', [], []), new Set(['mac']))).toBe(false);
  });
});

describe('topics', () => {
  const v = version('next', {
    commits: [commit('aaaaaaa1', ['mac'], ['a.ts', 'b.ts']), commit('bbbbbbb2', ['windows'], ['b.ts', 'c.ts'])],
    requests: [{ id: 'p/r1', paneId: 'p', text: 'do it', files: [], commits: [] }],
    topics: [topic('with', ['mac'], ['aaaaaaa1'], ['p/r1']), topic('without', ['windows'], ['bbbbbbb2']), topic('stale request', ['mac'], ['aaaaaaa1'], ['gone'])],
  });

  it('hides the topics without a request on demand', () => {
    expect(visibleTopics(v, false).map((e) => e.topic.title)).toEqual(['with', 'without', 'stale request']);
    expect(visibleTopics(v, true).map((e) => [e.topic.title, e.index])).toEqual([['with', 0]]);
    expect(visibleTopics(version('x'), false)).toEqual([]);
  });

  it('reads a topic\'s requests, commits and files from its version', () => {
    const t = v.topics![0]!;
    expect(topicRequests(v, t).map((r) => r.id)).toEqual(['p/r1']);
    expect(topicCommits(v, topic('all', [], ['bbbbbbb2', 'aaaaaaa1', 'nope'])).map((c) => c.sha)).toEqual(['bbbbbbb2', 'aaaaaaa1']);
    expect(topicFiles(v, topic('all', [], ['aaaaaaa1', 'bbbbbbb2']))).toEqual(['a.ts', 'b.ts', 'c.ts']);
    expect(topicFiles(v, topic('all', [], ['aaaaaaa1', 'bbbbbbb2']), 2)).toEqual(['a.ts', 'b.ts']);
  });
});

describe('versions', () => {
  it('names an unnamed unreleased one with the caller\'s word', () => {
    expect(versionName(version('next', { version: null }), 'Next version')).toBe('Next version');
    expect(versionName(version('next', { version: '0.4.49' }), 'Next version')).toBe('0.4.49');
    expect(versionName(version('v0.4.48', { version: undefined }), 'x')).toBe('0.4.48');
  });

  it('keeps the chosen version while it exists, else takes the newest', () => {
    const model = { available: true, lanes: [], versions: [version('next'), version('v1')] } as WorkMapModel;
    expect(pickVersion(model, 'v1')).toBe('v1');
    expect(pickVersion(model, 'v0')).toBe('next');
    expect(pickVersion(model, null)).toBe('next');
    expect(pickVersion({ ...model, versions: [] }, 'v1')).toBeNull();
  });

  it('shortens dates', () => {
    expect(shortDate('2026-11-02')).toBe('11/02');
    expect(shortDate(null)).toBe('');
    expect(shortStamp('2026-11-02T20:58:00+09:00')).toBe('11/02 20:58');
    expect(shortStamp(undefined)).toBe('');
  });
});

describe('the pane', () => {
  it('is a slim pane that says what it is', () => {
    expect(slimKinds.has('workmap')).toBe(true);
    expect(paneKindWords('workmap', (key) => key)).toBe('workMap.title');
  });
});
