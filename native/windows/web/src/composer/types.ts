// What the host pushes of a pane's composer (MainWindow.WebShell.Composer.cs WebComposerState): read from the never-shown
// WinUI composer the page's draft is mirrored into, so every rule (block reasons, busy, queue, attachments) stays C#'s.
import type { Pills } from './menus';
import type { RunNow } from './queue';

export type { RunNow, RunNowChoice } from './queue';

/** A row delegation holds for the pane; only the first carries 바로 실행 (the host sends null for the rest). */
export interface QueueHeld {
  id: string;
  text: string;
  runNow: RunNow | null;
}

export interface QueueItem {
  id: string;
  text: string;
  attachments: string[];
  /** What 바로 실행 does on the row (Core QueueRunNow); null draws no button. */
  runNow: RunNow | null;
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
    held: QueueHeld[];
    items: QueueItem[];
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
  /** The 보낼 대상 switch (MainWindow.AskTargets.cs): the other CLIs a Claude pane with oh-my-claudecode may ask, in the menu's order; none (or absent) hides it. */
  askTargets?: AskTargetOption[];
  /** The target the pane's sends go to now; null or absent for Claude. */
  askTarget?: string | null;
  /** The notice that the saved target is gone and sends go to Claude (composer.ask.fellBack), until it is dismissed. */
  askNotice?: string | null;
}

/** One choice of the 보낼 대상 switch: an oh-my-claudecode ask target id and its name. */
export interface AskTargetOption {
  id: string;
  label: string;
}

/**
 * What `send` answers (SubmitReceipt.SendAnswer): whether the host took it, what it did with the draft, the user entry and graph
 * run of a request that started at once (two ids on Windows; none for a start that went out with held items, and no run for a
 * pane that draws no diagram here), the page's own clientId back, the editor's text after it, and the composer then. An older
 * host sends only outcome, draft and state.
 */
export interface SendResult {
  accepted?: boolean;
  outcome: 'started' | 'notStarted' | 'queued' | 'worktree' | 'answered' | 'builtin' | 'blocked' | 'none';
  entryId?: string;
  runId?: string;
  clientId?: string;
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
