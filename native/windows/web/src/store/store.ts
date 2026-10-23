// The page's copy of the host state: the snapshot, replaced pane by pane as deltas come, plus what the snapshot does not
// hold (each pane's current activity, the tool-permission requests waiting). Pure functions; a pane that did not change
// keeps its object, so a view memoized on it does not draw again.
import type { PushMessage } from '../bridge/protocol';
import type { Card } from '../cards/types';
import type { ComposerState } from '../composer/types';
import { applySessionDelta, type SessionDelta } from './delta';
import type { AgentActivity, AppSnapshot, RunEvent, RunSession, ToolPermissionRequest } from './models';

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
}

/** The page's own message: a card fills the pane's composer, which listens for it. */
export interface FillMessage {
  kind: 'fill';
  paneId: string;
  text: string;
}

export type StoreMessage = PushMessage | FillMessage;

export const emptyState: StoreState = { snapshot: null, activities: {}, permissions: {}, composers: {}, cards: {}, fills: {} };

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
      return { ...state, snapshot: message.snapshot };
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
      return gone.size === 0 ? { ...state, snapshot } : { snapshot, activities, permissions, composers, cards, fills };
    }
    case 'event':
      return applyEvent(state, message.event);
    case 'permission':
      return applyPermission(state, message.request);
    case 'composer':
      return { ...state, composers: { ...state.composers, [message.state.paneId]: message.state } };
    case 'cards':
      return { ...state, cards: { ...state.cards, [message.paneId]: message.cards } };
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
