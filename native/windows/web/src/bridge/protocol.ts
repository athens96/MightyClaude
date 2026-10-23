// The messages between this page and the host (MightyClaude.WinUI/WebBridge.cs), JSON over
// chrome.webview.postMessage / PostWebMessageAsJson.
import type { Card } from '../cards/types';
import type { ComposerState } from '../composer/types';
import type { SessionDelta } from '../store/delta';
import type { AppSnapshot, AppState, RunEvent, ToolPermissionRequest } from '../store/models';

/** web → host: one method call; the host answers with a {@link ResultMessage} of the same id. */
export interface CallMessage {
  kind: 'call';
  id: number;
  method: string;
  args?: unknown;
}

/** host → web: the answer to a call. */
export interface ResultMessage {
  kind: 'result';
  id: number;
  ok: boolean;
  value?: unknown;
  error?: string;
}

/** host → web: the whole state, once after `ready` (and again whenever the page loads again). */
export interface SnapshotMessage {
  kind: 'snapshot';
  snapshot: AppSnapshot;
}

/**
 * host → web: what changed since the last snapshot or delta, at most one per ~16 ms. `sessions` are the panes whose record
 * was replaced (the host compares by reference), each as only what changed in it ({@link SessionDelta}); `removed` the ids
 * gone; `order` is every pane id in order, present only when the order or membership changed; `state` is every other field,
 * present only when one of them changed.
 */
export interface DeltaMessage {
  kind: 'delta';
  sessions: SessionDelta[];
  removed: string[];
  order?: string[];
  state?: AppState;
}

/**
 * host → web: one run event as applied (DesktopService.RunEventReceived). A `log` event comes without its `entry` and a
 * `graph_run` event without its `graphRun`: the pane's delta carries them, so a streamed line is not sent twice.
 */
export interface EventMessage {
  kind: 'event';
  event: RunEvent;
}

/** host → web: a Claude tool-permission request waiting or settled (DesktopService.ToolPermissionChanged). */
export interface PermissionMessage {
  kind: 'permission';
  request: ToolPermissionRequest;
}

/** host → web: a pane's composer state, when it changed (MainWindow.WebShell.Composer.cs). */
export interface ComposerMessage {
  kind: 'composer';
  state: ComposerState;
}

/** host → web: the cards above a pane's composer and its status line, in order, when they changed (MainWindow.WebShell.Cards.cs). */
export interface CardsMessage {
  kind: 'cards';
  paneId: string;
  cards: Card[];
}

export type PushMessage = SnapshotMessage | DeltaMessage | EventMessage | PermissionMessage | ComposerMessage | CardsMessage;
export type HostMessage = ResultMessage | PushMessage;

/** What `getLocale` answers: the saved preference and the language it resolves to on this computer. */
export interface LocaleInfo {
  preference: string;
  language: string;
}
