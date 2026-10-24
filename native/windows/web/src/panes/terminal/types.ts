// A terminal pane's messages (MainWindow.Terminal.cs, MainWindow.WebShell.Panes.cs): the page holds the xterm view, the host its
// ConPTY shell or an agent's terminal. They are the old terminal page's messages (Assets/Terminal/terminal.js), with output as
// base64 UTF-8 so a chunk that ends inside a character is written whole by xterm.

/** What the footer and the notice above it say (the WinUI pane's own TextBlocks, read by the host). */
export interface TerminalFooter {
  /** False: this pane has no terminal here (a remote workspace's shell runs on its host through the composer). */
  available: boolean;
  shell: boolean;
  /** The notice ("starting", the exit code, a failure); absent while the terminal runs. */
  notice?: string;
  restart: boolean;
  restartEnabled: boolean;
  /** "ConPTY", or the agent terminal's name. */
  engine: string;
  title: string;
  directory: string;
  grid: string;
}

/** host → page. */
export type TerminalMessage =
  | { type: 'output'; data: string }
  | { type: 'paste'; text: string }
  | { type: 'focus' }
  | { type: 'reset' }
  | { type: 'font'; size?: number; family?: string | null }
  | { type: 'theme'; light: boolean }
  | { type: 'footer'; footer: TerminalFooter };

/** page → host (the `terminalMessage` method's `message`). */
export type TerminalInput =
  | { type: 'ready'; columns: number; rows: number }
  | { type: 'input'; text: string }
  | { type: 'resize'; columns: number; rows: number }
  | { type: 'title'; title: string }
  | { type: 'directory'; url: string }
  | { type: 'copy'; text: string }
  | { type: 'paste' };
