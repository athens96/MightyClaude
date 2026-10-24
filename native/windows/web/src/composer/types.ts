// What the host pushes of a pane's composer (MainWindow.WebShell.Composer.cs WebComposerState): read from the never-shown
// WinUI composer the page's draft is mirrored into, so every rule (block reasons, busy, queue, attachments) stays C#'s.
import type { Pills } from './menus';

export interface QueueItem {
  id: string;
  text: string;
  attachments: string[];
}

export interface ComposerAttachment {
  id: string;
  name: string;
  mediaType: string;
  size: number;
}

export interface ComposerState {
  paneId: string;
  kind: string;
  busy: boolean;
  starting: boolean;
  stopping: boolean;
  canSend: boolean;
  checkingModels: boolean;
  attachmentsLoading: boolean;
  placeholderKey: string;
  /** Why the draft cannot run, already in the language (the C# reason); null when nothing blocks. */
  blocked: string | null;
  /** The blocked row offers the CLI's install. */
  install: boolean;
  /** The provider's label while a background CLI update holds sends. */
  hold: string | null;
  primary: 'send' | 'stop' | 'queue';
  primaryEnabled: boolean;
  worktree: boolean;
  worktreeMenuOnly: boolean;
  worktreeEnabled: boolean;
  queue: {
    held: { id: string; text: string }[];
    items: QueueItem[];
    runNext: boolean;
    waitsOnBackground: string | null;
  };
  attachments: ComposerAttachment[];
  attachmentError: string | null;
  attachmentsAllowed: boolean;
  /** The toolbar's pills; null on a shell pane. */
  pills: Pills | null;
  /** The pane header's word (in its tone) and the figures after the clock, with their tooltip. */
  header: { word: string; tone: string; timed: boolean; rest: string; help: string };
  /** The pane goes on from an earlier session: the toolbar shows the resume mark. */
  resumed?: boolean;
  /** A Claude pane's background-work and status-line toggles, each on or off (no status line for a remote pane); null elsewhere. */
  toggles?: { background: boolean; statusLine: boolean | null } | null;
  /** A Mighty Claude pane's style row (M/SessionPaneView.swift composer): the chosen style's name, its source badge, what it is for; null elsewhere. */
  style?: { label: string; badge: string | null; hint: string; enabled: boolean } | null;
}

/** What `send` answers: what the host did with the draft, the editor's text after it, and the composer then. */
export interface SendResult {
  outcome: 'started' | 'notStarted' | 'queued' | 'worktree' | 'answered' | 'builtin' | 'blocked' | 'none';
  draft: string;
  state: ComposerState;
}

export interface AttachmentPreview {
  name: string;
  mediaType: string;
  size: number;
  image?: string;
  text?: string;
}
