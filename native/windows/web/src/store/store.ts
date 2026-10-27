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
import { applyNative, type NativeOpen, type TextSetting } from '../native/types';
import type { OpenPicture } from '../pictures/viewer';
import { applyAnswer, drawsDiagram, failRequest, nextDeadline, reconcile, type PendingRequest, type SendOutcome } from '../pending/requests';
import { nodeId, RequestStep } from '../mighty/layout';

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
  /** A 'Git으로 생성' sheet's clone progress as the host last pushed it, by dialog id; dropped as the dialog closes. */
  gitProgress?: Readonly<Record<number, GitProgress>>;
  /** The host's WinUI views drawn by the page (Settings, setup, sheets, dialogs, the companion popover), oldest first. */
  natives: readonly NativeOpen[];
  /** The text setting the page's sizes and families follow; undefined until the host says. */
  text?: TextSetting;
  /** A pane the page made active before the host's state says so (a tab or row click), so the switch draws at once. */
  chosen?: string;
  /** Counts the host's `select` pushes (a notification's click), so the same pane twice still scrolls into view. */
  selected?: { paneId: string; seq: number };
  /**
   * How many of the page's menus, popovers and in-page dialogs are open (src/shell/overlay.ts); each marks its element with
   * `data-page-overlay`. A browser pane hides its native control while one of them overlaps its slot.
   */
  overlays: number;
  /** The picture the viewer shows over the window (src/pictures/PictureViewer.tsx); undefined while it is closed. */
  picture?: OpenPicture;
  /** The requests each pane's composer sent that the host's blocks have not replaced yet (src/pending/requests.ts), oldest first. */
  pending: Readonly<Record<string, readonly PendingRequest[]>>;
  /** Counts each pane's own sends (natural submit B2): every view moves to the new request when it changes. */
  ownSubmits: Readonly<Record<string, number>>;
  /** A pane's view chosen by a click before the host has saved it (Default | Mighty, and the Mighty view); dropped once it has. */
  views: Readonly<Record<string, ViewChoice>>;
  /** A send that went into the running run as a new turn: the diagram shows its steer block (`seq` counts them). */
  steered?: { paneId: string; runId?: string; seq: number };
  /**
   * The queue items each pane's composer let go of lately (they ran, 다음 실행 took them, or they were removed), with when: a
   * request entry with their words that comes soon after is an own send that now runs (B2), and the views move to it.
   */
  dequeued?: Readonly<Record<string, readonly { text: string; at: number }[]>>;
}

/** A view the page shows before the host's state says so. */
export interface ViewChoice {
  mode?: 'default' | 'mighty';
  presentation?: string;
  /** The host's call for that part answered: the next delta decides, and a choice it does not hold goes back to the host's. */
  modeSaved?: boolean;
  presentationSaved?: boolean;
}

/** One push of a running clone's progress (MainWindow.WorkspaceCreate.cs): its phase, percent and 0–1 fraction, and the words for them. */
export interface GitProgress {
  phase: string;
  percent: number;
  fraction: number;
  text: string;
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

/** The page's own message: one of its menus, popovers or in-page dialogs opened or closed (src/shell/overlay.ts). */
export interface OverlayMessage {
  kind: 'overlay';
  open: boolean;
}

/** The page's own message: a thumbnail or a diagram tile opens the picture viewer (undefined: close it). */
export interface PictureMessage {
  kind: 'picture';
  picture?: OpenPicture;
}

/**
 * The page's own message about a sent request (natural submit B1): Enter added it, the send's answer came, or the send threw.
 * `message` is the failure's words for a start that did not happen.
 */
export type PendingMessage =
  | { kind: 'pending'; action: 'add'; paneId: string; request: PendingRequest }
  | { kind: 'pending'; action: 'answer'; paneId: string; clientId: string; outcome: SendOutcome | string | null; entryId?: string | null; runId?: string | null; message: string; at: number }
  | { kind: 'pending'; action: 'fail'; paneId: string; clientId: string; message: string; at: number }
  /** A send the page drew no block for (a queued item, a slash command) that started: the views move to it all the same. */
  | { kind: 'pending'; action: 'own'; paneId: string }
  /** A moment a request may go by time alone (StartedGrace, ArrivedGrace) came: every pane is settled again. */
  | { kind: 'pending'; action: 'settle' };

/**
 * The page's own message: a view switch clicked (shown at once), taken back because the host's call failed (`revert`), or its
 * call answered (`saved`: the next delta that still differs takes it back to the host's state).
 */
export interface ViewMessage {
  kind: 'view';
  paneId: string;
  choice: ViewChoice;
  revert?: boolean;
  saved?: boolean;
}

export type StoreMessage = PushMessage | FillMessage | ChooseMessage | OverlayMessage | PictureMessage | PendingMessage | ViewMessage;

export const emptyState: StoreState = {
  snapshot: null, activities: {}, permissions: {}, composers: {}, cards: {}, fills: {}, mighty: {}, shell: null, dialogs: [], natives: [], overlays: 0,
  pending: {}, ownSubmits: {}, views: {},
};

/**
 * Whether the pane's shown Mighty view has its own data for a request's run, so its stand-in may go: the diagram the run's request
 * block copy, the timeline a group, the pipeline a board. A pane outside the Mighty view, or one whose Mighty model is not pushed,
 * has nothing to wait for.
 */
export function mightyHolds(state: StoreState, session: RunSession): (request: PendingRequest) => boolean {
  const choice = state.views[session.id];
  const pane = state.mighty[session.id];
  if ((choice?.mode ?? session.agentViewMode) !== 'mighty' || !drawsDiagram(session) || !pane) return () => true;
  const presentation = choice?.presentation ?? session.graphViewMode;
  return (request) => {
    const runs = request.arrivedRunIds ?? (request.runId ? [request.runId] : []);
    if (presentation === 'timeline') return pane.timeline?.some((group) => runs.includes(group.runId)) ?? false;
    if (presentation === 'structure') return pane.structure?.some((board) => board.runIds.some((id) => runs.includes(id))) ?? false;
    return runs.some((id) => pane.blocks[nodeId(id, RequestStep)] !== undefined);
  };
}

/** Each pane's sent requests against its state now: the ones the host's blocks replaced go, as do a closed pane's. */
export function settlePending(state: StoreState, now = Date.now()): StoreState {
  const panes = Object.keys(state.pending);
  if (panes.length === 0 || !state.snapshot) return state;
  let changed = false;
  const next: Record<string, readonly PendingRequest[]> = {};
  for (const id of panes) {
    const before = state.pending[id]!;
    const session = state.snapshot.sessions.find((pane) => pane.id === id);
    const after = reconcile(before, session, now, session ? mightyHolds(state, session) : undefined);
    if (after !== before) changed = true;
    if (after.length > 0) next[id] = after;
  }
  return changed ? { ...state, pending: next } : state;
}

/**
 * View choices the host's state now holds are forgotten; a pane that is gone forgets its choice. After a delta (`fromHost`), a
 * part whose call has answered and that the host still does not hold goes back to the host's view.
 */
export function settleViews(state: StoreState, fromHost = false): StoreState {
  const panes = Object.keys(state.views);
  if (panes.length === 0 || !state.snapshot) return state;
  let changed = false;
  const next: Record<string, ViewChoice> = {};
  for (const id of panes) {
    const choice = state.views[id]!;
    const session = state.snapshot.sessions.find((pane) => pane.id === id);
    if (!session) { changed = true; continue; }
    const kept: ViewChoice = {};
    if (choice.mode !== undefined && choice.mode !== (session.agentViewMode ?? 'default') && !(fromHost && choice.modeSaved)) {
      kept.mode = choice.mode;
      if (choice.modeSaved) kept.modeSaved = true;
    }
    if (choice.presentation !== undefined && choice.presentation !== session.graphViewMode && !(fromHost && choice.presentationSaved)) {
      kept.presentation = choice.presentation;
      if (choice.presentationSaved) kept.presentationSaved = true;
    }
    if (kept.mode !== choice.mode || kept.presentation !== choice.presentation) changed = true;
    if (kept.mode !== undefined || kept.presentation !== undefined) next[id] = kept;
  }
  return changed ? { ...state, views: next } : state;
}

function withPending(state: StoreState, paneId: string, requests: readonly PendingRequest[]): StoreState {
  if (requests === state.pending[paneId]) return state;
  const pending = { ...state.pending };
  if (requests.length > 0) pending[paneId] = requests; else delete pending[paneId];
  return { ...state, pending };
}

/** How long a queue item that left the queue may still be matched to the request entry it starts. */
export const DequeuedMemory = 10_000;

const queueTexts = (composer: ComposerState | undefined) =>
  composer ? [...composer.queue.held.map((item) => item.text.trim()), ...composer.queue.items.map((item) => item.text.trim())] : [];

/** The composer state pushed for a pane: the queue items it no longer holds are remembered for a while (`dequeued`). */
export function rememberDequeued(state: StoreState, next: ComposerState, now: number): StoreState['dequeued'] {
  const kept = new Set(queueTexts(next));
  const gone = queueTexts(state.composers[next.paneId]).filter((text) => text.length > 0 && !kept.has(text));
  const recent = (state.dequeued?.[next.paneId] ?? []).filter((item) => now - item.at <= DequeuedMemory);
  if (gone.length === 0 && recent.length === (state.dequeued?.[next.paneId]?.length ?? 0)) return state.dequeued;
  const list = [...recent, ...gone.map((text) => ({ text, at: now }))];
  const dequeued = { ...state.dequeued };
  if (list.length > 0) dequeued[next.paneId] = list; else delete dequeued[next.paneId];
  return dequeued;
}

/**
 * A delta's new request entries that carry the words of a queue item (still queued, or let go of lately): a queued send that
 * now runs, or 다음 실행. Each is an own send, so every view moves to it (scrollRule `own`).
 */
export function queuedStarts(state: StoreState, sessions: readonly SessionDelta[], now: number, previous: readonly RunSession[] = []): StoreState {
  let next = state;
  for (const delta of sessions) {
    // Only entries the pane did not have before this delta: a whole record sent again carries every past request too.
    const known = new Set(previous.find((session) => session.id === delta.id)?.logs.map((entry) => entry.id) ?? []);
    const added = [...(delta.logs?.added ?? []), ...(delta.logs?.replaced ?? []), ...(delta.session?.logs ?? [])]
      .filter((entry) => entry.kind === 'user' && !known.has(entry.id)).map((entry) => entry.text.trim());
    if (added.length === 0) continue;
    const recent = (state.dequeued?.[delta.id] ?? []).filter((item) => now - item.at <= DequeuedMemory);
    const words = new Set([...queueTexts(state.composers[delta.id]), ...recent.map((item) => item.text)]);
    const matched = added.filter((text) => text.length > 0 && words.has(text));
    if (matched.length === 0) continue;
    next = bumpOwn(next, delta.id);
    const left = recent.filter((item) => !matched.includes(item.text));
    const dequeued = { ...next.dequeued };
    if (left.length > 0) dequeued[delta.id] = left; else delete dequeued[delta.id];
    next = { ...next, dequeued };
  }
  return next;
}

const bumpOwn = (state: StoreState, paneId: string): StoreState => ({ ...state, ownSubmits: { ...state.ownSubmits, [paneId]: (state.ownSubmits[paneId] ?? 0) + 1 } });

/** A sent request's change (PendingMessage). */
export function applyPending(state: StoreState, message: PendingMessage): StoreState {
  if (message.action === 'settle') return settlePending(state);
  const requests = state.pending[message.paneId] ?? [];
  switch (message.action) {
    case 'add':
      // A new send takes the place of a failed one: its message has been read.
      return settlePending(bumpOwn(withPending(state, message.paneId, [...requests.filter((request) => request.phase !== 'failed'), message.request]), message.paneId));
    case 'answer': {
      const known = requests.some((request) => request.clientId === message.clientId);
      let next = withPending(state, message.paneId, applyAnswer(requests, message.clientId, message.outcome, { entryId: message.entryId, runId: message.runId }, message.message, message.at));
      if (message.outcome === 'steered') next = { ...next, steered: { paneId: message.paneId, runId: message.runId ?? undefined, seq: (state.steered?.seq ?? 0) + 1 } };
      // A start whose block the page did not draw (its delta may come first) still moves the views to it.
      else if (message.outcome === 'started' && !known) next = bumpOwn(next, message.paneId);
      return settlePending(next);
    }
    case 'fail':
      return withPending(state, message.paneId, failRequest(requests, message.clientId, message.message, message.at));
    case 'own':
      return bumpOwn(state, message.paneId);
  }
}

/** A view switch (ViewMessage): shown at once, or taken back. */
export function applyView(state: StoreState, message: ViewMessage): StoreState {
  const current = state.views[message.paneId] ?? {};
  let next: ViewChoice;
  if (message.revert || message.saved) {
    next = { ...current };
    const mode = message.choice.mode !== undefined && current.mode === message.choice.mode;
    const presentation = message.choice.presentation !== undefined && current.presentation === message.choice.presentation;
    if (message.revert) {
      if (mode) { delete next.mode; delete next.modeSaved; }
      if (presentation) { delete next.presentation; delete next.presentationSaved; }
    } else {
      if (mode) next.modeSaved = true;
      if (presentation) next.presentationSaved = true;
    }
  } else {
    // A new click waits for its own call again.
    next = { ...current, ...message.choice };
    if (message.choice.mode !== undefined) delete next.modeSaved;
    if (message.choice.presentation !== undefined) delete next.presentationSaved;
  }
  const views = { ...state.views };
  if (next.mode !== undefined || next.presentation !== undefined) views[message.paneId] = next; else delete views[message.paneId];
  return settleViews({ ...state, views });
}

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
      return settleViews(settlePending(settle({ ...state, snapshot: message.snapshot })), true);
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
      // A picture whose pane closed closes with it.
      const picture = state.picture && gone.has(state.picture.source.paneId) ? undefined : state.picture;
      const moved = settleViews(settlePending(settle(gone.size === 0 ? { ...state, snapshot } : { ...state, snapshot, activities, permissions, composers, cards, fills, mighty, picture })), true);
      return queuedStarts(moved, message.sessions, Date.now(), current.sessions);
    }
    case 'event':
      return applyEvent(state, message.event);
    case 'permission':
      return applyPermission(state, message.request);
    case 'composer': {
      const dequeued = rememberDequeued(state, message.state, Date.now());
      return { ...state, composers: { ...state.composers, [message.state.paneId]: message.state }, ...(dequeued !== state.dequeued ? { dequeued } : {}) };
    }
    case 'cards':
      return { ...state, cards: { ...state.cards, [message.paneId]: message.cards } };
    case 'mighty':
      // The Mighty view's own data came: a request's stand-in it now holds goes.
      return settlePending({ ...state, mighty: { ...state.mighty, [message.paneId]: applyMighty(state.mighty[message.paneId], message) } });
    case 'shell':
      return { ...state, shell: message.shell };
    case 'dialog':
      return { ...state, dialogs: [...state.dialogs.filter((open) => open.id !== message.id), { id: message.id, dialog: message.dialog }] };
    case 'dialogClose': {
      const dialogs = state.dialogs.filter((open) => open.id !== message.id);
      if (!state.gitProgress || !(message.id in state.gitProgress)) return { ...state, dialogs };
      const gitProgress = { ...state.gitProgress };
      delete gitProgress[message.id];
      return { ...state, dialogs, gitProgress };
    }
    case 'gitProgress':
      // A push queued before its sheet closed arrives after dialogClose: it is dropped, not kept for good.
      if (!state.dialogs.some((open) => open.id === message.id)) return state;
      return { ...state, gitProgress: { ...state.gitProgress, [message.id]: { phase: message.phase, percent: message.percent, fraction: message.fraction, text: message.text } } };
    case 'dialogUpdate':
      return {
        ...state,
        dialogs: state.dialogs.map((open) => (open.id === message.id && open.dialog.type === 'resumeChoice' ? { id: open.id, dialog: { ...open.dialog, checking: message.checking } } : open)),
      };
    case 'native':
      return { ...state, natives: applyNative(state.natives, message) };
    case 'text':
      return { ...state, text: { scale: message.scale, family: message.family, transcriptFamily: message.transcriptFamily, codeFamily: message.codeFamily } };
    case 'select':
      return { ...state, selected: { paneId: message.paneId, seq: (state.selected?.seq ?? 0) + 1 } };
    case 'choose':
      return message.paneId === state.chosen ? state : settle({ ...state, chosen: message.paneId });
    case 'terminal':
    case 'browser':
      return state;
    case 'overlay':
      return { ...state, overlays: Math.max(0, state.overlays + (message.open ? 1 : -1)) };
    case 'picture':
      return message.picture === state.picture ? state : { ...state, picture: message.picture };
    case 'fill':
      return { ...state, fills: { ...state.fills, [message.paneId]: { text: message.text, seq: (state.fills[message.paneId]?.seq ?? 0) + 1 } } };
    case 'pending':
      return applyPending(state, message);
    case 'view':
      return applyView(state, message);
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
    const pendingChanged = next.pending !== this.state.pending;
    this.state = next;
    if (pendingChanged) this.scheduleSettle();
    for (const listener of this.listeners) listener();
  }

  private settleTimer: ReturnType<typeof setTimeout> | null = null;

  /** A request that may only go by time (StartedGrace, ArrivedGrace) is settled then, even on a pane nothing is pushed for. */
  private scheduleSettle(): void {
    if (this.settleTimer !== null) clearTimeout(this.settleTimer);
    this.settleTimer = null;
    let next: number | null = null;
    for (const requests of Object.values(this.state.pending)) {
      const at = nextDeadline(requests);
      if (at !== null && (next === null || at < next)) next = at;
    }
    if (next === null) return;
    this.settleTimer = setTimeout(() => { this.settleTimer = null; this.dispatch({ kind: 'pending', action: 'settle' }); }, Math.max(0, next - Date.now()));
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
