import type { MobileSessionDetail, MobileSessionSummary, MobileState } from '@/api/types';
import {
  MAX_BACKOFF_MS,
  MIN_BACKOFF_MS,
  countAttention,
  groupSessionsByWorkspace,
  mergeSessionDetail,
  mergeState,
  nextBackoff,
} from '@/lib/merge';

function session(overrides: Partial<MobileSessionSummary>): MobileSessionSummary {
  return {
    id: 's1',
    workspaceId: 'w1',
    title: '세션',
    kind: 'claude',
    provider: 'claude',
    model: 'opus',
    status: 'idle',
    revision: 1,
    updatedAt: '2026-01-01T00:00:00.000Z',
    pendingPermissions: 0,
    pendingQuestions: 0,
    queued: 0,
    terminal: false,
    ...overrides,
  };
}

function state(revision: number, sessions: MobileSessionSummary[] = []): MobileState {
  return {
    protocol: 1,
    revision,
    hostName: 'mac',
    workspaces: [
      { id: 'w1', name: '작업1', path: '/a', remote: false },
      { id: 'w2', name: '작업2', path: '/b', remote: true },
    ],
    sessions,
  };
}

describe('nextBackoff', () => {
  it('starts at 1s and doubles up to 10s', () => {
    expect(nextBackoff(undefined)).toBe(MIN_BACKOFF_MS);
    expect(nextBackoff(1000)).toBe(2000);
    expect(nextBackoff(2000)).toBe(4000);
    expect(nextBackoff(4000)).toBe(8000);
    expect(nextBackoff(8000)).toBe(MAX_BACKOFF_MS);
    expect(nextBackoff(MAX_BACKOFF_MS)).toBe(MAX_BACKOFF_MS);
  });
});

describe('mergeState', () => {
  it('adopts the first response', () => {
    const first = state(5);
    expect(mergeState(undefined, first)).toBe(first);
  });

  it('adopts newer revisions', () => {
    const older = state(5);
    const newer = state(6);
    expect(mergeState(older, newer)).toBe(newer);
  });

  it('keeps the previous value for equal or stale revisions', () => {
    const current = state(6);
    expect(mergeState(current, state(6))).toBe(current);
    expect(mergeState(current, state(4))).toBe(current);
  });
});

describe('mergeSessionDetail', () => {
  const detail = (revision: number, entryText: string): MobileSessionDetail => ({
    protocol: 1,
    revision,
    session: session({ revision }),
    entries: [{ id: 'e1', kind: 'assistant', text: entryText, timestamp: '2026-01-01T00:00:00Z' }],
    permissions: [],
    queued: [],
  });

  it('replaces entries wholesale when the revision advances', () => {
    const previous = detail(1, '안녕');
    const incoming = detail(2, '안녕하세요');
    const merged = mergeSessionDetail(previous, incoming);
    expect(merged).toBe(incoming);
    expect(merged.entries).toHaveLength(1);
    expect(merged.entries[0]?.text).toBe('안녕하세요');
  });

  it('ignores stale and repeated revisions', () => {
    const previous = detail(3, '최신');
    expect(mergeSessionDetail(previous, detail(3, '동일'))).toBe(previous);
    expect(mergeSessionDetail(previous, detail(2, '과거'))).toBe(previous);
  });
});

describe('groupSessionsByWorkspace', () => {
  it('groups sessions under their workspace and keeps empty workspaces', () => {
    const groups = groupSessionsByWorkspace(
      state(1, [session({ id: 'a' }), session({ id: 'b', workspaceId: 'w2' })]),
    );
    expect(groups).toHaveLength(2);
    expect(groups[0]?.sessions.map((entry) => entry.id)).toEqual(['a']);
    expect(groups[1]?.sessions.map((entry) => entry.id)).toEqual(['b']);
  });

  it('drops sessions whose workspace is unknown', () => {
    const groups = groupSessionsByWorkspace(state(1, [session({ id: 'x', workspaceId: 'gone' })]));
    expect(groups.flatMap((group) => group.sessions)).toHaveLength(0);
  });

  it('sorts pending permissions first, then by status, then recency', () => {
    const groups = groupSessionsByWorkspace(
      state(1, [
        session({ id: 'idle', status: 'idle', updatedAt: '2026-01-01T00:00:00.000Z' }),
        session({ id: 'running', status: 'running', updatedAt: '2026-01-01T00:00:01.000Z' }),
        session({ id: 'needs', status: 'completed', pendingPermissions: 1 }),
        session({ id: 'recent-idle', status: 'idle', updatedAt: '2026-01-02T00:00:00.000Z' }),
      ]),
    );
    expect(groups[0]?.sessions.map((entry) => entry.id)).toEqual([
      'needs',
      'running',
      'recent-idle',
      'idle',
    ]);
  });
});

describe('countAttention', () => {
  it('sums pending permissions and questions', () => {
    expect(
      countAttention(
        state(1, [
          session({ id: 'a', pendingPermissions: 2, pendingQuestions: 1 }),
          session({ id: 'b', pendingQuestions: 3 }),
        ]),
      ),
    ).toBe(6);
  });
});
