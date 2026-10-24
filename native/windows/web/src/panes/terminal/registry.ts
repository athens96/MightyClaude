// One xterm per terminal pane for the page's life: a pane that unmounts (another workspace shown) keeps its terminal, its
// buffer and its push subscription, and the next mount puts the same element back. It ends when the pane is closed.
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import type { Bridge } from '../../bridge/bridge';
import { listenPane } from '../channel';
import { base64Bytes, clipboardKey, defaultFamily, terminalFamily, terminalSize, terminalThemes } from './rules';
import type { TerminalInput } from './types';
import { entries, fitShown, type TerminalEntry } from './entries';

/** The pane's terminal, made the first time; its output and notices come as `terminal` pushes. */
export function ensureTerminal(paneId: string, bridge: Bridge): TerminalEntry {
  const existing = entries.get(paneId);
  if (existing) return existing;
  const terminal = new Terminal({ fontFamily: defaultFamily, fontSize: 12, scrollback: 10000, convertEol: false, allowProposedApi: false, theme: terminalThemes.dark, windowsPty: { backend: 'conpty', buildNumber: 19041 } });
  const fit = new FitAddon();
  terminal.loadAddon(fit);
  const root = document.createElement('div');
  root.className = 'terminal-surface';
  root.dataset.terminalPane = paneId;
  const send = (message: TerminalInput) => void bridge.call('terminalMessage', { paneId, message }).catch(() => {});
  const disposers = [
    terminal.onData((text) => send({ type: 'input', text })),
    terminal.onResize((size) => send({ type: 'resize', columns: size.cols, rows: size.rows })),
    terminal.onTitleChange((title) => send({ type: 'title', title: title.slice(0, 200) })),
    terminal.parser.registerOscHandler(7, (url) => {
      if (url.length <= 8192) send({ type: 'directory', url });
      return true;
    }),
  ];
  terminal.attachCustomKeyEventHandler((event) => {
    const key = clipboardKey(event, terminal.hasSelection());
    if (key === null) return true;
    if (key === 'copy' && terminal.hasSelection()) send({ type: 'copy', text: terminal.getSelection() });
    if (key === 'paste') send({ type: 'paste' });
    event.preventDefault();
    return false;
  });
  const entry: TerminalEntry = {
    terminal, fit, root, opened: false, footerListeners: new Set(), send,
    dispose: () => {
      stop();
      for (const disposer of disposers) disposer.dispose();
      terminal.dispose();
      root.remove();
      entries.delete(paneId);
    },
  };
  const stop = listenPane('terminal', paneId, ({ message }) => {
    switch (message.type) {
      case 'output':
        terminal.write(base64Bytes(message.data));
        break;
      case 'paste':
        terminal.paste(message.text);
        break;
      case 'focus':
        terminal.focus();
        break;
      case 'reset':
        terminal.reset();
        fitShown(entry);
        break;
      case 'font': {
        const size = terminalSize(message.size);
        if (size !== null && terminal.options.fontSize !== size) terminal.options.fontSize = size;
        const family = terminalFamily(message.family);
        if (terminal.options.fontFamily !== family) terminal.options.fontFamily = family;
        fitShown(entry);
        break;
      }
      case 'theme': {
        const theme = message.light ? terminalThemes.light : terminalThemes.dark;
        terminal.options.theme = theme;
        // The padding round the terminal is the surface's own, so it takes the surface colour.
        root.style.background = theme.background;
        break;
      }
      case 'footer':
        entry.footer = message.footer;
        for (const listener of entry.footerListeners) listener(message.footer);
        break;
    }
  });
  entries.set(paneId, entry);
  return entry;
}

/** Puts the terminal in `host`, opening it there the first time, and says it is ready (the host then starts the shell). */
export function mountTerminal(entry: TerminalEntry, host: HTMLElement): void {
  if (entry.root.parentElement !== host) host.appendChild(entry.root);
  if (!entry.opened) {
    entry.terminal.open(entry.root);
    entry.opened = true;
  }
  fitShown(entry);
  entry.send({ type: 'ready', columns: entry.terminal.cols, rows: entry.terminal.rows });
}
