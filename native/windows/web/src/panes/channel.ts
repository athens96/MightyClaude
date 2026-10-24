// The pushes of the panes that are not a conversation (MainWindow.WebShell.Panes.cs): a terminal's output and notices, a
// browser's navigation state. They go to the pane that listens for them, not into the store: a terminal's output is a stream
// the view writes straight into xterm, and a browser's state is only its own toolbar's.
import type { BrowserState } from './browser/types';
import type { TerminalMessage } from './terminal/types';

export interface TerminalPush {
  kind: 'terminal';
  paneId: string;
  message: TerminalMessage;
}

export interface BrowserPush {
  kind: 'browser';
  paneId: string;
  state: BrowserState;
}

export type PanePush = TerminalPush | BrowserPush;

type Listener = (message: PanePush) => void;
const listeners = new Map<string, Set<Listener>>();

export function isPanePush(message: { kind: string }): message is PanePush {
  return message.kind === 'terminal' || message.kind === 'browser';
}

/** Listens for one pane's pushes of one kind until the returned function is called. */
export function listenPane<K extends PanePush['kind']>(kind: K, paneId: string, listener: (message: Extract<PanePush, { kind: K }>) => void): () => void {
  const key = kind + ':' + paneId;
  const set = listeners.get(key) ?? new Set<Listener>();
  listeners.set(key, set);
  const wrapped = listener as Listener;
  set.add(wrapped);
  return () => {
    set.delete(wrapped);
    if (set.size === 0) listeners.delete(key);
  };
}

export function dispatchPanePush(message: PanePush): void {
  for (const listener of listeners.get(message.kind + ':' + message.paneId) ?? []) listener(message);
}
