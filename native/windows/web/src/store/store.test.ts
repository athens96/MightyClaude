import { describe, expect, it } from 'vitest';
import { devSnapshot } from '../bridge/devHost';
import type { AppState, RunSession, ToolPermissionRequest } from './models';
import { Store, activeSessionId, applyPermission, emptyState, mergeSessions, reduce, selectedSession } from './store';

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
