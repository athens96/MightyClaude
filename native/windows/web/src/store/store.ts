// The page's copy of the host state: the snapshot, replaced pane by pane as deltas come, plus what the snapshot does not
// hold (each pane's current activity, the tool-permission requests waiting). Pure functions; a pane that did not change
// keeps its object, so a view memoized on it does not draw again.
import type { PushMessage } from '../bridge/protocol';
import type { Card } from '../cards/types';
import type { ComposerState } from '../composer/types';
import { applyMighty, type MightyPane } from '../mighty/types';
import { applySessionDelta, type SessionDelta } from './delta';
import type { AgentActivity, AppSnapshot, RunEvent, RunSession, ToolPermissionRequest } from './models';
import type { OpenDialog, ShellModel } from '../shell/types';

export interface StoreState {
  snapshot: AppSnapshot | null;
  /** A pane's latest activity (RunSession.CurrentActivity is not on the wire); cleared when its run ends. */
  activities: Readonly<Record<string, AgentActivity>>;
  /** Waiting tool-permission requests, by pane id, oldest first. */
  permissions: Readonly<Record<string, readonly ToolPermissionRequest[]>>;
  /** Each pane's composer state as the host last pushed it (only panes the page has written in). */
  composers: Readonly<Record<string, ComposerState>>;
  /** Each pane's cards as the host last pushed them. */
  cards: Readonly<Record<string, readonly Card[]>>;
  /** Words a card put in a pane's draft (a next action); `seq` counts them so the same words twice still fill. */
  fills: Readonly<Record<string, { text: string; seq: number }>>;
  /** Each shown pane's Mighty model as the host pushed it (MainWindow.WebShell.Mighty.cs). */
  mighty: Readonly<Record<string, MightyPane>>;
  /** The frame's model as the host last pushed it (MainWindow.WebShell.Shell.cs). */
  shell: ShellModel | null;
  /** The dialogs the host's flows asked the page to draw, oldest first. */
  dialogs: readonly OpenDialog[];
  /** A pane the page made active before the host's state says so (a tab or row click), so the switch draws at once. */
  chosen?: string;
  /** Counts the host's `select` pushes (a notification's click), so the same pane twice still scrolls into view. */
  selected?: { paneId: string; seq: number };
}

/** The page's own message: a card fills the pane's composer, which listens for it. */
export interface FillMessage {
  kind: 'fill';
  paneId: string;
  text: string;
}

/** The page's own message: a pane chosen by a click, shown before the host's state follows (undefined: forget the choice). */
export interface ChooseMessage {
  kind: 'choose';
  paneId?: string;
}

export type StoreMessage = PushMessage | FillMessage | ChooseMessage;

export const emptyState: StoreState = { snapshot: null, activities: {}, permissions: {}, composers: {}, cards: {}, fills: {}, mighty: {}, shell: null, dialogs: [] };

/** A choice the host's state has caught up with is forgotten. */
function settle(state: StoreState): StoreState {
  return state.chosen !== undefined && state.snapshot?.activeSessionId === state.chosen ? { ...state, chosen: undefined } : state;
}

const terminal = new Set(['completed', 'error', 'stopped']);

/**
 * The panes after a delta: changed ones patched (or added when the delta holds the whole record), removed ones dropped, then
 * put in `order` when it came. A pane the delta does not name keeps its object.
 */
export function mergeSessions(current: readonly RunSession[], changed: readonly SessionDelta[], removed: readonly string[], order?: readonly string[]): RunSession[] {
  if (changed.length === 0 && removed.length === 0 && !order) return current as RunSession[];
  const byId = new Map(current.map((session) => [session.id, session]));
  for (const id of removed) byId.delete(id);
  const added: RunSession[] = [];
  for (const delta of changed) {
    const before = byId.get(delta.id);
    const session = applySessionDelta(before, delta);
    if (!session) continue;
    if (!before) added.push(session);
    byId.set(session.id, session);
  }
  if (order) return order.map((id) => byId.get(id)).filter((session): session is RunSession => session !== undefined);
  const kept = current.filter((session) => byId.has(session.id)).map((session) => byId.get(session.id) as RunSession);
  return kept.concat(added);
}

function withoutKey<T>(record: Readonly<Record<string, T>>, key: string): Record<string, T> {
  if (!(key in record)) return record as Record<string, T>;
  const next = { ...record };
  delete next[key];
  return next;
}

/** A run event: what it says about the pane that the snapshot will not (its activity). The pane's record comes with the next delta. */
export function applyEvent(state: StoreState, event: RunEvent): StoreState {
  if (event.type === 'activity' && event.activity) return { ...state, activities: { ...state.activities, [event.sessionId]: event.activity } };
  if (event.type === 'status' && event.status && terminal.has(event.status)) {
    const activities = withoutKey(state.activities, event.sessionId);
    const permissions = withoutKey(state.permissions, event.sessionId);
    return activities === state.activities && permissions === state.permissions ? state : { ...state, activities, permissions };
  }
  return state;
}

/** A permission request: a pending one is added (or replaced by id), any other state takes it away. */
export function applyPermission(state: StoreState, request: ToolPermissionRequest): StoreState {
  const waiting = state.permissions[request.runId] ?? [];
  const others = waiting.filter((item) => item.id !== request.id);
  const next = request.state === 'pending' ? [...others, request] : others;
  if (next.length === 0) return { ...state, permissions: withoutKey(state.permissions, request.runId) };
  return { ...state, permissions: { ...state.permissions, [request.runId]: next } };
}

export function reduce(state: StoreState, message: StoreMessage): StoreState {
  switch (message.kind) {
    case 'snapshot':
      return settle({ ...state, snapshot: message.snapshot });
    case 'delta': {
      const current = state.snapshot;
      if (!current) return state;
      const sessions = mergeSessions(current.sessions, message.sessions, message.removed, message.order);
      if (sessions === current.sessions && !message.state) return state;
      const gone = new Set(message.removed);
      const activities = Object.fromEntries(Object.entries(state.activities).filter(([id]) => !gone.has(id)));
      const permissions = Object.fromEntries(Object.entries(state.permissions).filter(([id]) => !gone.has(id)));
      const snapshot: AppSnapshot = message.state ? { ...message.state, sessions } : { ...current, sessions };
      const composers = Object.fromEntries(Object.entries(state.composers).filter(([id]) => !gone.has(id)));
      const cards = Object.fromEntries(Object.entries(state.cards).filter(([id]) => !gone.has(id)));
      const fills = Object.fromEntries(Object.entries(state.fills).filter(([id]) => !gone.has(id)));
      const mighty = Object.fromEntries(Object.entries(state.mighty).filter(([id]) => !gone.has(id)));
      return settle(gone.size === 0 ? { ...state, snapshot } : { ...state, snapshot, activities, permissions, composers, cards, fills, mighty });
    }
    case 'event':
      return applyEvent(state, message.event);
    case 'permission':
      return applyPermission(state, message.request);
    case 'composer':
      return { ...state, composers: { ...state.composers, [message.state.paneId]: message.state } };
    case 'cards':
      return { ...state, cards: { ...state.cards, [message.paneId]: message.cards } };
    case 'mighty':
      return { ...state, mighty: { ...state.mighty, [message.paneId]: applyMighty(state.mighty[message.paneId], message) } };
    case 'shell':
      return { ...state, shell: message.shell };
    case 'dialog':
      return { ...state, dialogs: [...state.dialogs.filter((open) => open.id !== message.id), { id: message.id, dialog: message.dialog }] };
    case 'dialogClose':
      return { ...state, dialogs: state.dialogs.filter((open) => open.id !== message.id) };
    case 'dialogUpdate':
      return {
        ...state,
        dialogs: state.dialogs.map((open) => (open.id === message.id && open.dialog.type === 'resumeChoice' ? { id: open.id, dialog: { ...open.dialog, checking: message.checking } } : open)),
      };
    case 'select':
      return { ...state, selected: { paneId: message.paneId, seq: (state.selected?.seq ?? 0) + 1 } };
    case 'choose':
      return message.paneId === state.chosen ? state : settle({ ...state, chosen: message.paneId });
    case 'fill':
      return { ...state, fills: { ...state.fills, [message.paneId]: { text: message.text, seq: (state.fills[message.paneId]?.seq ?? 0) + 1 } } };
  }
}

/** A tiny external store for useSyncExternalStore: one state, replaced whole, listeners told after each change. */
export class Store {
  private state: StoreState;
  private readonly listeners = new Set<() => void>();

  constructor(initial: StoreState = emptyState) {
    this.state = initial;
  }

  getState = (): StoreState => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  dispatch(message: StoreMessage): void {
    const next = reduce(this.state, message);
    if (next === this.state) return;
    this.state = next;
    for (const listener of this.listeners) listener();
  }
}

/** The pane shown in the main area: the active one, else the first. */
export function selectedSession(snapshot: AppSnapshot | null): RunSession | undefined {
  if (!snapshot) return undefined;
  return snapshot.sessions.find((session) => session.id === snapshot.activeSessionId) ?? snapshot.sessions[0];
}

/** The active pane as the page shows it: the one just chosen by a click, else the host's. */
export function activeSessionId(state: StoreState): string | undefined {
  return state.chosen ?? state.snapshot?.activeSessionId;
}
