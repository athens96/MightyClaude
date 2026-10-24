// The page's terminals by pane, without xterm itself: the view reads them at once, and the xterm module (registry.ts, the
// heaviest part of the page) loads only when the first terminal pane shows.
import type { Terminal } from '@xterm/xterm';
import type { FitAddon } from '@xterm/addon-fit';
import type { TerminalFooter, TerminalInput } from './types';

export interface TerminalEntry {
  terminal: Terminal;
  fit: FitAddon;
  /** The element the terminal was opened in, moved into each mount's host. */
  root: HTMLDivElement;
  opened: boolean;
  footer?: TerminalFooter;
  readonly footerListeners: Set<(footer: TerminalFooter) => void>;
  send(message: TerminalInput): void;
  dispose(): void;
}

export const entries = new Map<string, TerminalEntry>();

export function terminalEntry(paneId: string): TerminalEntry | undefined {
  return entries.get(paneId);
}

/** Fits the grid to its room, only while the terminal has room (a hidden pane has none). */
export function fitShown(entry: TerminalEntry): void {
  if (entry.opened && entry.root.clientWidth > 20 && entry.root.clientHeight > 20) entry.fit.fit();
}

/** The text of the terminal's buffer, for the web smoke (`mightyPanes.terminalText(id)`). */
export function terminalText(paneId: string): string | null {
  const buffer = entries.get(paneId)?.terminal.buffer.active;
  if (!buffer) return null;
  return Array.from({ length: buffer.length }, (_, index) => buffer.getLine(index)?.translateToString() ?? '').join('\n');
}

/** Types into the terminal as the keyboard does (its onData goes to the host), for the web smoke (`mightyPanes.terminalInput`). */
export function terminalInput(paneId: string, text: string): boolean {
  const entry = entries.get(paneId);
  if (!entry?.opened) return false;
  entry.terminal.input(text, true);
  return true;
}

(globalThis as unknown as { mightyPanes?: object }).mightyPanes = { terminalText, terminalInput };
