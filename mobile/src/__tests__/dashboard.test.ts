import type {
  LogEntry,
  MobileSessionDetail,
  MobileSessionSummary,
  MobileState,
} from '@/api/types';
import {
  actionCount,
  attentionReason,
  buildAttentionList,
  contextPercentOf,
  countStats,
  displayStatus,
  formatClock,
  freshGlance,
  liveElapsed,
  relativeAge,
  resolveSelectedHost,
  sessionTone,
  toolCount,
} from '@/lib/dashboard';
import { countAttention } from '@/lib/merge';

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

function state(sessions: MobileSessionSummary[], hostName = 'mac'): MobileState {
  return {
    protocol: 1,
    revision: 3,
    hostName,
    workspaces: [{ id: 'w1', name: 'ws', path: '/ws' }],
    sessions,
  };
}

function detail(summary: MobileSessionSummary, extra: Partial<MobileSessionDetail> = {}): MobileSessionDetail {
  return {
    protocol: 1,
    revision: 10,
    session: summary,
    entries: [],
    permissions: [],
    queued: [],
    ...extra,
  };
}

describe('countStats', () => {
  it('counts running and completed panes and every pending request', () => {
    const list = state([
      session({ id: 'a', status: 'running' }),
      session({ id: 'b', status: 'running', pendingQuestions: 1 }),
      session({ id: 'c', status: 'completed' }),
      session({ id: 'd', status: 'idle', pendingPermissions: 2 }),
      session({ id: 'e', status: 'error' }),
      session({ id: 'f', status: 'stopped' }),
    ]);
    expect(countStats(list)).toEqual({ running: 2, waiting: 3, done: 1 });
  });

  it('waits on the requests the 알림 badge counts: countAttention without agent-owned panes', () => {
    const list = state([
      session({ id: 'a', pendingQuestions: 2, pendingPermissions: 1 }),
      session({ id: 'b', kind: 'agent-terminal', pendingPermissions: 1 }),
    ]);
    expect(countStats(list).waiting).toBe(3);
    expect(countAttention(list)).toBe(4);
    expect(countStats(list).waiting).toBe(actionCount(buildAttentionList([{ id: 'h', name: 'h' }], { h: list })));
  });

  it("leaves an agent's own terminal and browser out of running and done", () => {
    const list = state([
      session({ id: 'agent-terminal:x', kind: 'agent-terminal', status: 'running' }),
      session({ id: 'agent-browser:x', kind: 'agent-browser', status: 'completed' }),
    ]);
    expect(countStats(list)).toEqual({ running: 0, waiting: 0, done: 0 });
  });

  it('is all zeros before the host has answered', () => {
    expect(countStats(undefined)).toEqual({ running: 0, waiting: 0, done: 0 });
  });
});

describe('displayStatus and sessionTone', () => {
  it('shows a pane holding a request as waiting, whatever its own status', () => {
    const asking = session({ status: 'running', pendingQuestions: 1 });
    expect(displayStatus(asking)).toBe('waiting');
    expect(sessionTone(asking)).toBe('wait');
  });

  it('maps the pane statuses to their tones and an unknown word to idle', () => {
    expect(sessionTone(session({ status: 'running' }))).toBe('run');
    expect(sessionTone(session({ status: 'completed' }))).toBe('done');
    expect(sessionTone(session({ status: 'error' }))).toBe('err');
    expect(sessionTone(session({ status: 'stopped' }))).toBe('stop');
    expect(sessionTone(session({ status: 'idle' }))).toBe('idle');
    expect(sessionTone(session({ status: 'paused' as MobileSessionSummary['status'] }))).toBe('idle');
  });
});

describe('buildAttentionList', () => {
  const hosts = [
    { id: 'h1', name: 'paired name' },
    { id: 'h2', name: 'second' },
  ];

  it('orders questions, permissions, errors, then finished runs, newest first within each', () => {
    const items = buildAttentionList(hosts, {
      h1: state([
        session({ id: 'done-old', status: 'completed', updatedAt: '2026-01-01T00:00:00.000Z' }),
        session({ id: 'err', status: 'error' }),
        session({ id: 'perm', pendingPermissions: 2 }),
        session({ id: 'idle', status: 'idle' }),
      ]),
      h2: state(
        [
          session({ id: 'done-new', status: 'completed', updatedAt: '2026-02-01T00:00:00.000Z' }),
          session({ id: 'ask', status: 'running', pendingQuestions: 1 }),
        ],
        'Mac two',
      ),
    });
    expect(items.map((item) => [item.session.id, item.reason, item.count])).toEqual([
      ['ask', 'question', 1],
      ['perm', 'permission', 2],
      ['err', 'error', 1],
      ['done-new', 'finished', 1],
      ['done-old', 'finished', 1],
    ]);
    expect(items[0]?.hostName).toBe('Mac two');
    expect(items[0]?.hostId).toBe('h2');
  });

  it('keys rows by host and pane, so the same pane id on two hosts stays two rows', () => {
    const items = buildAttentionList(hosts, {
      h1: state([session({ id: 'same', status: 'error' })]),
      h2: state([session({ id: 'same', status: 'error' })]),
    });
    expect(new Set(items.map((item) => item.key)).size).toBe(2);
  });

  it('reads only hosts that are still paired, and skips agent-owned panes', () => {
    const items = buildAttentionList([{ id: 'h1', name: 'one' }], {
      h1: state([session({ id: 'agent-terminal:a', kind: 'agent-terminal', status: 'error' })]),
      gone: state([session({ id: 'x', pendingQuestions: 1 })]),
    });
    expect(items).toEqual([]);
  });

  it('falls back to the paired name when the host sent none', () => {
    const items = buildAttentionList(hosts, { h1: state([session({ status: 'error' })], '') });
    expect(items[0]?.hostName).toBe('paired name');
  });

  it('badges only the requests to answer', () => {
    const items = buildAttentionList(hosts, {
      h1: state([
        session({ id: 'a', pendingQuestions: 2 }),
        session({ id: 'b', pendingPermissions: 1 }),
        session({ id: 'c', status: 'error' }),
        session({ id: 'd', status: 'completed' }),
      ]),
    });
    expect(actionCount(items)).toBe(3);
  });

  it('counts every request on a pane that holds both kinds', () => {
    const items = buildAttentionList(hosts, {
      h1: state([session({ id: 'both', pendingQuestions: 2, pendingPermissions: 1 })]),
    });
    expect(items.map((item) => [item.reason, item.count])).toEqual([['question', 3]]);
  });

  it('gives a question priority over a permission on the same pane', () => {
    expect(attentionReason(session({ pendingQuestions: 1, pendingPermissions: 1 }))).toBe('question');
    expect(attentionReason(session({ status: 'idle' }))).toBeUndefined();
  });
});

describe('freshGlance', () => {
  const summary = session({ id: 's1', revision: 5, status: 'running' });
  const now = 1_000_000;

  it('reads context and elapsed time from a detail of the same revision', () => {
    const glance = freshGlance(
      summary,
      detail(summary, { usage: { contextPercent: 41.2 }, elapsedSeconds: 134 }),
      now,
      now,
    );
    expect(glance).toEqual({ contextPercent: 41.2, elapsedSeconds: 134 });
  });

  it("carries a running pane's clock forward from when its detail arrived", () => {
    const glance = freshGlance(summary, detail(summary, { elapsedSeconds: 40 }), now - 300_000, now);
    expect(glance?.elapsedSeconds).toBe(340);
  });

  it("drops a running pane's reading when the phone does not know when it arrived", () => {
    expect(freshGlance(summary, detail(summary, { elapsedSeconds: 40 }), undefined, now)).toBeUndefined();
  });

  it('keeps a stopped clock as the host read it', () => {
    const idle = session({ id: 's1', revision: 5, status: 'completed' });
    expect(freshGlance(idle, detail(idle, { elapsedSeconds: 75 }), now - 600_000, now)).toEqual({
      elapsedSeconds: 75,
    });
    expect(freshGlance(idle, detail(idle, { elapsedSeconds: 75 }), undefined, now)).toEqual({
      elapsedSeconds: 75,
    });
  });

  it('shows nothing from an older detail, rather than a number that is no longer true', () => {
    const older = session({ id: 's1', revision: 4 });
    expect(
      freshGlance(summary, detail(older, { usage: { contextPercent: 10 }, elapsedSeconds: 9 }), now, now),
    ).toBeUndefined();
  });

  it('shows nothing without a detail, or when the detail carries neither number', () => {
    expect(freshGlance(summary, undefined, now, now)).toBeUndefined();
    expect(freshGlance(summary, detail(summary), now, now)).toBeUndefined();
  });

  it('never runs the clock backwards when the phone clock is behind the arrival time', () => {
    expect(liveElapsed(detail(summary, { elapsedSeconds: 12 }), now + 5_000, now)).toBe(12);
  });

  it('works context out from the token counts and clamps it to 0–100', () => {
    expect(contextPercentOf({ contextUsedTokens: 50_000, contextWindowTokens: 200_000 })).toBe(25);
    expect(contextPercentOf({ contextPercent: 130 })).toBe(100);
    expect(contextPercentOf({ contextUsedTokens: 5 })).toBeUndefined();
    expect(contextPercentOf({ contextPercent: Number.NaN })).toBeUndefined();
  });
});

describe('formatClock', () => {
  it('reads like a stopwatch', () => {
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(134)).toBe('2:14');
    expect(formatClock(3729)).toBe('1:02:09');
    expect(formatClock(-4)).toBe('0:00');
    expect(formatClock(Number.NaN)).toBe('0:00');
  });
});

describe('relativeAge', () => {
  const now = Date.parse('2026-03-01T12:00:00.000Z');

  it('picks the largest whole unit', () => {
    expect(relativeAge('2026-03-01T11:59:40.000Z', now)).toEqual({ unit: 'now', count: 0 });
    expect(relativeAge('2026-03-01T11:49:00.000Z', now)).toEqual({ unit: 'minutes', count: 11 });
    expect(relativeAge('2026-03-01T09:00:00.000Z', now)).toEqual({ unit: 'hours', count: 3 });
    expect(relativeAge('2026-02-26T12:00:00.000Z', now)).toEqual({ unit: 'days', count: 3 });
  });

  it('treats a time ahead of the phone clock as now and a bad date as unknown', () => {
    expect(relativeAge('2026-03-01T12:05:00.000Z', now)).toEqual({ unit: 'now', count: 0 });
    expect(relativeAge('not a date', now)).toBeUndefined();
  });
});

describe('toolCount', () => {
  const entries: LogEntry[] = [
    { id: '1', kind: 'user', text: 'hi', timestamp: '' },
    {
      id: '2',
      kind: 'system',
      text: '',
      timestamp: '',
      activity: { id: 'a', kind: 'tool', state: 'completed', summary: 'src/lib/merge.ts', toolName: 'Read' },
    },
    {
      id: '3',
      kind: 'system',
      text: '',
      timestamp: '',
      activity: { id: 'b', kind: 'tool', state: 'error', summary: 'npx jest', toolName: 'Bash' },
    },
    {
      id: '4',
      kind: 'system',
      text: '',
      timestamp: '',
      activity: { id: 'c', kind: 'thinking', state: 'completed', summary: 'thinking' },
    },
    { id: '5', kind: 'assistant', text: 'done', timestamp: '' },
  ];

  it('counts the activities that name a tool, in a transcript the phone holds whole', () => {
    expect(toolCount(entries, true)).toBe(2);
  });

  it('says nothing while older entries are still on the host', () => {
    expect(toolCount(entries, false)).toBeUndefined();
  });
});

describe('resolveSelectedHost', () => {
  const hosts = [{ id: 'a' }, { id: 'b' }];

  it('keeps the picked host while it is paired and falls back to the first otherwise', () => {
    expect(resolveSelectedHost(hosts, 'b')).toBe('b');
    expect(resolveSelectedHost(hosts, 'gone')).toBe('a');
    expect(resolveSelectedHost(hosts, undefined)).toBe('a');
    expect(resolveSelectedHost([], 'a')).toBeUndefined();
  });
});
