import { describe, expect, it, vi } from 'vitest';
import { devSnapshot } from '../bridge/devHost';
import type { AppState, RunSession, ToolPermissionRequest } from './models';
import type { ComposerState } from '../composer/types';
import { Store, activeSessionId, applyPermission, emptyState, mergeSessions, reduce, selectedSession, settlePending } from './store';
import { StartedGrace, newPendingRequest } from '../pending/requests';

const loaded = () => reduce(emptyState, { kind: 'snapshot', snapshot: devSnapshot() });
const pane = (id: string, title = id): RunSession => ({ ...devSnapshot().sessions[0]!, id, title });

describe('mergeSessions', () => {
  it('keeps the same array when nothing changed', () => {
    const sessions = [pane('a'), pane('b')];
    expect(mergeSessions(sessions, [], [])).toBe(sessions);
  });

  it('replaces a changed pane in place and keeps the others by reference', () => {
    const a = pane('a'), b = pane('b'), c = pane('c');
    const next = mergeSessions([a, b, c], [{ id: 'b', set: { title: 'renamed' } }], []);
    expect(next.map((s) => s.title)).toEqual(['a', 'renamed', 'c']);
    expect(next[0]).toBe(a);
    expect(next[2]).toBe(c);
  });

  it('drops removed panes and appends new ones', () => {
    const next = mergeSessions([pane('a'), pane('b')], [{ id: 'c', session: pane('c') }], ['a']);
    expect(next.map((s) => s.id)).toEqual(['b', 'c']);
  });

  it('ignores a patch for a pane it does not have', () => {
    const sessions = [pane('a')];
    expect(mergeSessions(sessions, [{ id: 'zz', set: { title: 'x' } }], []).map((s) => s.id)).toEqual(['a']);
  });

  it('follows the order when one comes', () => {
    const a = pane('a'), b = pane('b');
    const next = mergeSessions([a, b], [], [], ['b', 'a']);
    expect(next).toEqual([b, a]);
    expect(next[1]).toBe(a);
  });
});

describe('reduce', () => {
  it('ignores a delta before the snapshot', () => {
    expect(reduce(emptyState, { kind: 'delta', sessions: [{ id: 'x', session: pane('x') }], removed: [] })).toBe(emptyState);
  });

  it('applies a delta without touching unchanged panes', () => {
    const state = loaded();
    const [first, second] = state.snapshot!.sessions;
    const next = reduce(state, { kind: 'delta', sessions: [{ id: second!.id, set: { status: 'completed' } }], removed: [] });
    expect(next.snapshot!.sessions[0]).toBe(first);
    expect(next.snapshot!.sessions[1]!.status).toBe('completed');
    expect(next.snapshot!.theme).toBe('dark');
  });

  it('replaces every other field when the delta carries the state', () => {
    const state = loaded();
    const { sessions: _sessions, activeSessionId: _active, ...rest } = state.snapshot!;
    const changed: AppState = { ...rest, theme: 'light' };
    const next = reduce(state, { kind: 'delta', sessions: [], removed: [], state: changed });
    expect(next.snapshot!.theme).toBe('light');
    expect(next.snapshot!.activeSessionId).toBeUndefined();
    expect(next.snapshot!.sessions).toBe(state.snapshot!.sessions);
  });

  it('returns the same state for an empty delta', () => {
    const state = loaded();
    expect(reduce(state, { kind: 'delta', sessions: [], removed: [] })).toBe(state);
  });

  it('tracks activity from events and clears it when the run ends', () => {
    const activity = { id: 'a1', provider: 'claude', kind: 'command', state: 'running', summary: 'npm test' };
    let state = reduce(loaded(), { kind: 'event', event: { sessionId: 's2', type: 'activity', activity } });
    expect(state.activities.s2).toEqual(activity);
    state = reduce(state, { kind: 'event', event: { sessionId: 's2', type: 'status', status: 'completed' } });
    expect(state.activities.s2).toBeUndefined();
  });

  it('forgets a removed pane\'s activity and permissions', () => {
    let state = reduce(loaded(), { kind: 'event', event: { sessionId: 's2', type: 'activity', activity: { id: 'a', provider: 'claude', kind: 'command', state: 'running', summary: '' } } });
    state = reduce(state, { kind: 'delta', sessions: [], removed: ['s2'] });
    expect(state.snapshot!.sessions.map((s) => s.id)).toEqual(['s1']);
    expect(state.activities).toEqual({});
  });

  it('opens and closes the picture viewer, and closes it with its pane', () => {
    const picture = { url: 'data:image/png;base64,AA==', alt: 'shot.png', source: { paneId: 's2', hash: 'h' } };
    const open = reduce(loaded(), { kind: 'picture', picture });
    expect(open.picture).toBe(picture);
    expect(reduce(open, { kind: 'picture', picture })).toBe(open);
    expect(reduce(open, { kind: 'picture' }).picture).toBeUndefined();
    expect(reduce(open, { kind: 'delta', sessions: [], removed: ['s1'] }).picture).toBe(picture);
    expect(reduce(open, { kind: 'delta', sessions: [], removed: ['s2'] }).picture).toBeUndefined();
  });
});

describe('applyPermission', () => {
  const request = (id: string, state = 'pending'): ToolPermissionRequest => ({
    id, runId: 's1', toolUseId: 't' + id, toolName: 'Bash', inputJson: '{}', summary: 'ls', state, canAllow: true, canAnswerQuestions: false, canAnswerPlan: false,
  });

  it('adds pending requests in order and removes settled ones', () => {
    let state = applyPermission(emptyState, request('1'));
    state = applyPermission(state, request('2'));
    expect(state.permissions.s1!.map((r) => r.id)).toEqual(['1', '2']);
    state = applyPermission(state, request('1', 'allowed'));
    expect(state.permissions.s1!.map((r) => r.id)).toEqual(['2']);
    state = applyPermission(state, request('2', 'denied'));
    expect(state.permissions.s1).toBeUndefined();
  });
});

describe('Store', () => {
  it('tells listeners only when the state changes', () => {
    const store = new Store();
    let calls = 0;
    store.subscribe(() => calls++);
    store.dispatch({ kind: 'delta', sessions: [], removed: [] });
    expect(calls).toBe(0);
    store.dispatch({ kind: 'snapshot', snapshot: devSnapshot() });
    expect(calls).toBe(1);
    expect(selectedSession(store.getState().snapshot)?.id).toBe('s1');
  });
});

describe('the frame', () => {
  it('shows a chosen pane at once and forgets the choice once the host state has it', () => {
    let state = reduce(loaded(), { kind: 'choose', paneId: 's2' });
    expect(activeSessionId(state)).toBe('s2');
    const { sessions: _sessions, ...rest } = state.snapshot!;
    state = reduce(state, { kind: 'delta', sessions: [], removed: [], state: { ...rest, activeSessionId: 's2' } as AppState });
    expect(state.chosen).toBeUndefined();
    expect(activeSessionId(state)).toBe('s2');
  });
  it('keeps the dialogs the host asks for until one is answered or closed', () => {
    let state = reduce(loaded(), { kind: 'dialog', id: 1, dialog: { type: 'resumeChoice', provider: 'claude', title: 't', message: 'm', checking: true } });
    state = reduce(state, { kind: 'dialog', id: 2, dialog: { type: 'confirm', title: 'x', message: 'y', close: 'c', defaultPrimary: false } });
    state = reduce(state, { kind: 'dialogUpdate', id: 1, checking: false });
    expect(state.dialogs.map((d) => d.id)).toEqual([1, 2]);
    expect(state.dialogs[0]!.dialog.type === 'resumeChoice' && state.dialogs[0]!.dialog.checking).toBe(false);
    state = reduce(state, { kind: 'dialogClose', id: 2 });
    expect(state.dialogs.map((d) => d.id)).toEqual([1]);
  });
  it('keeps the latest clone progress per dialog until that dialog closes', () => {
    const sheet = { type: 'gitCreate' as const, title: 't', subtitle: 's', location: 'C:\\', labels: { name: '', location: '', choose: '', path: '', gitInit: '', create: '', cancel: '', url: '', urlPlaceholder: '', test: '', checking: '', reachable: '', openSetup: '', starting: '' } };
    let state = reduce(loaded(), { kind: 'dialog', id: 3, dialog: sheet });
    state = reduce(state, { kind: 'dialog', id: 4, dialog: sheet });
    state = reduce(state, { kind: 'gitProgress', id: 3, phase: 'receiving', percent: 10, fraction: 0.05, text: 'r 10' });
    state = reduce(state, { kind: 'gitProgress', id: 3, phase: 'resolving', percent: 50, fraction: 0.85, text: 'd 50' });
    state = reduce(state, { kind: 'gitProgress', id: 4, phase: 'starting', percent: 0, fraction: 0, text: 's' });
    expect(state.gitProgress?.[3]).toEqual({ phase: 'resolving', percent: 50, fraction: 0.85, text: 'd 50' });
    state = reduce(state, { kind: 'dialogClose', id: 3 });
    expect(state.gitProgress?.[3]).toBeUndefined();
    expect(state.gitProgress?.[4]?.text).toBe('s');
    // A late push for the closed sheet is dropped.
    state = reduce(state, { kind: 'gitProgress', id: 3, phase: 'checkout', percent: 90, fraction: 0.99, text: 'late' });
    expect(state.gitProgress?.[3]).toBeUndefined();
  });
});

describe('sent requests and view choices (natural submit)', () => {
  const at = Date.now();
  const add = (state = loaded()) => {
    const session = selectedSession(state.snapshot)!;
    return { session, state: reduce(state, { kind: 'pending', action: 'add', paneId: session.id, request: newPendingRequest(session, 'c1', 'store request words', at) }) };
  };

  it('adds a request on Enter and counts it as an own send', () => {
    const { session, state } = add();
    expect(state.pending[session.id]?.map((r) => r.clientId)).toEqual(['c1']);
    expect(state.ownSubmits[session.id]).toBe(1);
  });

  it('drops it on queued and keeps a started one until its entry comes with a delta', () => {
    const { session, state } = add();
    expect(reduce(state, { kind: 'pending', action: 'answer', paneId: session.id, clientId: 'c1', outcome: 'queued', message: '', at }).pending[session.id]).toBeUndefined();
    let started = reduce(state, { kind: 'pending', action: 'answer', paneId: session.id, clientId: 'c1', outcome: 'started', entryId: 'new-entry', runId: 'new-run', message: '', at });
    expect(started.pending[session.id]?.[0]?.phase).toBe('started');
    // The answer named a request the page drew: no second own count.
    expect(started.ownSubmits[session.id]).toBe(1);
    const added = { id: 'new-entry', kind: 'user' as const, text: 'store request words', timestamp: new Date(at).toISOString() };
    started = reduce(started, { kind: 'delta', sessions: [{ id: session.id, logs: { added: [added] } }], removed: [] });
    // A diagram pane waits for its run as well.
    expect(started.pending[session.id]?.[0]?.phase).toBe('started');
    const graphRun = { id: 'new-run', input: 'store request words', status: 'running', rootEntries: [], agents: [], resultEntries: [] };
    started = reduce(started, { kind: 'delta', sessions: [{ id: session.id, graphRuns: { added: [graphRun] } }], removed: [] });
    expect(started.pending[session.id]).toBeUndefined();
  });

  it('turns it failed with the message, and a new send takes its place', () => {
    const { session, state } = add();
    const failed = reduce(state, { kind: 'pending', action: 'answer', paneId: session.id, clientId: 'c1', outcome: 'blocked', message: 'no CLI', at: at + 5 });
    expect(failed.pending[session.id]?.[0]).toMatchObject({ phase: 'failed', message: 'no CLI' });
    const thrown = reduce(state, { kind: 'pending', action: 'fail', paneId: session.id, clientId: 'c1', message: 'gone', at: at + 5 });
    expect(thrown.pending[session.id]?.[0]).toMatchObject({ phase: 'failed', message: 'gone' });
    const next = reduce(failed, { kind: 'pending', action: 'add', paneId: session.id, request: newPendingRequest(session, 'c2', 'again', at + 10) });
    expect(next.pending[session.id]?.map((r) => r.clientId)).toEqual(['c2']);
  });

  it('moves the views to a start it drew no block for, and remembers a steer', () => {
    const state = loaded();
    const id = selectedSession(state.snapshot)!.id;
    const started = reduce(state, { kind: 'pending', action: 'answer', paneId: id, clientId: 'unknown', outcome: 'started', message: '', at });
    expect(started.ownSubmits[id]).toBe(1);
    const steered = reduce(state, { kind: 'pending', action: 'answer', paneId: id, clientId: 'unknown', outcome: 'steered', runId: 'run-1', message: '', at });
    expect(steered.steered).toEqual({ paneId: id, runId: 'run-1', seq: 1 });
  });

  it('forgets the requests of a pane that closed', () => {
    const { session, state } = add();
    expect(reduce(state, { kind: 'delta', sessions: [], removed: [session.id] }).pending[session.id]).toBeUndefined();
  });

  it('shows a view choice at once, keeps it until the host holds it, and takes it back on failure', () => {
    const state = loaded();
    const session = selectedSession(state.snapshot)!;
    const other = session.agentViewMode === 'mighty' ? 'default' : 'mighty';
    const chosen = reduce(state, { kind: 'view', paneId: session.id, choice: { mode: other } });
    expect(chosen.views[session.id]).toEqual({ mode: other });
    expect(reduce(chosen, { kind: 'view', paneId: session.id, choice: { mode: other }, revert: true }).views[session.id]).toBeUndefined();
    const saved = reduce(chosen, { kind: 'delta', sessions: [{ id: session.id, set: { agentViewMode: other } }], removed: [] });
    expect(saved.views[session.id]).toBeUndefined();
    // A choice the host already holds is never kept.
    expect(reduce(state, { kind: 'view', paneId: session.id, choice: { presentation: session.graphViewMode ?? 'never-saved' } }).views[session.id]).toEqual(
      session.graphViewMode === undefined ? { presentation: 'never-saved' } : undefined);
  });

  it('counts a queued request that now runs (or 다음 실행) as an own send, by its words', () => {
    const state = loaded();
    const session = selectedSession(state.snapshot)!;
    const composer = (texts: string[]) => ({ paneId: session.id, queue: { held: [], items: texts.map((text, i) => ({ id: 'q' + i, text, attachments: [] })), runNext: false, waitsOnBackground: null } }) as unknown as ComposerState;
    let next = reduce(state, { kind: 'composer', state: composer(['queued words', 'later']) });
    // The host lets go of the first item, then its request entry comes.
    next = reduce(next, { kind: 'composer', state: composer(['later']) });
    expect(next.dequeued?.[session.id]?.map((item) => item.text)).toEqual(['queued words']);
    const entry = { id: 'qe', kind: 'user' as const, text: 'queued words', timestamp: new Date().toISOString() };
    next = reduce(next, { kind: 'delta', sessions: [{ id: session.id, logs: { added: [entry] } }], removed: [] });
    expect(next.ownSubmits[session.id]).toBe(1);
    expect(next.dequeued?.[session.id]).toBeUndefined();
    // Anyone else's request is not own.
    const other = { id: 'oe', kind: 'user' as const, text: 'from the phone', timestamp: new Date().toISOString() };
    expect(reduce(next, { kind: 'delta', sessions: [{ id: session.id, logs: { added: [other] } }], removed: [] }).ownSubmits[session.id]).toBe(1);
    // An item still in the queue that starts (the entry before the composer push) counts too.
    const still = { id: 'se', kind: 'user' as const, text: 'later', timestamp: new Date().toISOString() };
    expect(reduce(next, { kind: 'delta', sessions: [{ id: session.id, logs: { added: [still] } }], removed: [] }).ownSubmits[session.id]).toBe(2);
  });

  it('keeps the stand-in until the shown Mighty view has its own data for the run', () => {
    const { session, state } = add();
    let next = reduce(state, { kind: 'delta', sessions: [{ id: session.id, set: { agentViewMode: 'mighty', graphViewMode: 'timeline' } }], removed: [] });
    next = reduce(next, { kind: 'mighty', paneId: session.id, timeline: [] });
    next = reduce(next, { kind: 'pending', action: 'answer', paneId: session.id, clientId: 'c1', outcome: 'started', entryId: 'e2', runId: 'r2', message: '', at });
    const added = { id: 'e2', kind: 'user' as const, text: 'store request words', timestamp: new Date(at).toISOString() };
    const graphRun = { id: 'r2', input: 'store request words', status: 'running', rootEntries: [], agents: [], resultEntries: [] };
    next = reduce(next, { kind: 'delta', sessions: [{ id: session.id, logs: { added: [added] }, graphRuns: { added: [graphRun] } }], removed: [] });
    expect(next.pending[session.id]?.[0]).toMatchObject({ phase: 'arrived', arrivedRunIds: ['r2'] });
    const group = { runId: 'r2', ordinal: 1, title: 'Request 1', status: 'running', statusWord: 'Running', tone: 'run', input: 'store request words', tally: '', open: true, older: false, rows: [], resultFolds: false };
    next = reduce(next, { kind: 'mighty', paneId: session.id, timeline: [group] });
    expect(next.pending[session.id]).toBeUndefined();
  });

  it('settles a quiet pane once a started request is long overdue', () => {
    const { session, state } = add();
    const started = reduce(state, { kind: 'pending', action: 'answer', paneId: session.id, clientId: 'c1', outcome: 'started', entryId: 'never', message: '', at: Date.now() });
    expect(started.pending[session.id]).toHaveLength(1);
    expect(settlePending(started, Date.now() + StartedGrace + 100).pending[session.id]).toBeUndefined();
  });

  it('wakes itself at the deadline, with nothing pushed meanwhile', () => {
    vi.useFakeTimers();
    try {
      const store = new Store(loaded());
      const session = selectedSession(store.getState().snapshot)!;
      store.dispatch({ kind: 'pending', action: 'add', paneId: session.id, request: newPendingRequest(session, 'c1', 'quiet words', Date.now()) });
      store.dispatch({ kind: 'pending', action: 'answer', paneId: session.id, clientId: 'c1', outcome: 'started', entryId: 'never', message: '', at: Date.now() });
      vi.advanceTimersByTime(StartedGrace - 100);
      expect(store.getState().pending[session.id]).toHaveLength(1);
      vi.advanceTimersByTime(200);
      expect(store.getState().pending[session.id]).toBeUndefined();
    } finally {
      vi.useRealTimers();
    }
  });

  it('takes back a view choice the host answered for but did not take, at the next delta', () => {
    const state = loaded();
    const session = selectedSession(state.snapshot)!;
    let next = reduce(state, { kind: 'view', paneId: session.id, choice: { presentation: 'structure' } });
    next = reduce(next, { kind: 'view', paneId: session.id, choice: { presentation: 'structure' }, saved: true });
    // Answered, but no delta yet: still shown.
    expect(next.views[session.id]).toEqual({ presentation: 'structure', presentationSaved: true });
    next = reduce(next, { kind: 'delta', sessions: [{ id: session.id, set: { title: 'other change' } }], removed: [] });
    expect(next.views[session.id]).toBeUndefined();
  });

  it('does not count the past requests of a pane record sent whole again', () => {
    const state = loaded();
    const session = selectedSession(state.snapshot)!;
    const past = session.logs.find((entry) => entry.kind === 'user')!;
    const composer = { paneId: session.id, queue: { held: [], items: [{ id: 'q', text: past.text, attachments: [] }], runNext: false, waitsOnBackground: null } } as unknown as ComposerState;
    const next = reduce(reduce(state, { kind: 'composer', state: composer }), { kind: 'delta', sessions: [{ id: session.id, session: { ...session, title: 'again' } }], removed: [] });
    expect(next.ownSubmits[session.id]).toBeUndefined();
  });
});

