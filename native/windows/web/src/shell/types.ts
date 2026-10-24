// The frame's model as the host builds it (MightyClaude.WinUI/MainWindow.WebShell.Shell.cs WebShellModel): every word and
// count comes from the WinUI frame's own rules, so the page only lays them out.
import type { PaneLayoutNode } from '../store/models';

export type Tone = 'run' | 'wait' | 'done' | 'err' | 'stop' | 'idle';

export interface Counts {
  questions: number;
  permissions: number;
  errors: number;
  running: number;
  done: number;
  stopped: number;
  idle: number;
}

/** What a pane shows wherever it is listed (WebPaneInfo). */
export interface PaneInfo {
  id: string;
  workspaceId: string;
  title: string;
  help: string;
  kind: string;
  provider: string;
  beta: boolean;
  tone: Tone;
  word: string;
  settled: boolean;
  /** The agent whose mark leads the meta line; absent for a pane that is not an agent's. */
  agent?: string;
  agentLabel?: string;
  meta: string;
  wait?: string;
  tabMark: boolean;
  waiting: number;
  dashMeta: string;
  activity?: string;
  activityError: boolean;
  dashWait?: string;
}

export interface CardAction {
  action: 'merge' | 'undo' | 'discard';
  label: string;
  help?: string;
}

export interface ChildCard {
  busy: boolean;
  actions: CardAction[];
  held?: string;
  note?: string;
  noteTone?: Tone;
  kept?: string;
}

export type SidebarItem =
  | { type: 'pane'; id: string; depth: number; title?: string; stateWord?: string; stateTone?: Tone; card?: ChildCard; menu?: CardAction[] }
  | { type: 'closedChild'; id: string; depth: number; title: string; tone: Tone; stateWord: string; stateTone: Tone; card?: ChildCard; menu: CardAction[] }
  | { type: 'parentClosed'; id: string; depth: number; branch: string; count: number; help: string; label: string; name: string };

export interface SidebarWorkspace {
  id: string;
  name: string;
  path: string;
  remoteHost?: string;
  local: boolean;
  counts: Counts;
  expanded: boolean;
  items?: SidebarItem[];
}

export interface GitInfo {
  label: string;
  dirty: boolean;
  ahead: number;
  behind: number;
  help: string;
}

export interface HeaderModel {
  id: string;
  name: string;
  path: string;
  git?: GitInfo;
  remoteHost?: string;
  filesEnabled: boolean;
  filesHelp: string;
}

export interface LayoutModel {
  workspaceId: string;
  mode: string;
  root: PaneLayoutNode;
}

export interface UsageChip {
  provider: string;
  name: string;
  words: { text: string; warning: boolean }[];
}

export interface StatusModel {
  machine: string;
  /** Present for a remote workspace: whether its computer is connected. */
  remote?: boolean;
  panes: string;
  running: string;
  update?: string;
  updateHelp: string;
  staged?: string;
  setup: boolean;
  chips?: UsageChip[];
  chipsTitle: string;
  chipsTooltip: string;
}

export interface UsageDetails {
  title: string;
  refreshing: boolean;
  refreshLabel: string;
  refreshTooltip: string;
  cards: {
    provider: string;
    name: string;
    title: string;
    beta: boolean;
    account?: string;
    windows: { label: string; used: string; fraction: number; reset?: string; warning: boolean }[];
    detail?: string;
    note?: string;
    checkedAt?: string;
    reset?: { title: string; rows: { program: string; label: string; text: string }[]; link: string; url: string };
  }[];
  direct?: { label: string; description: string; on: boolean };
  shared: string;
}

export interface DashboardModel {
  subtitle: string;
  running: number;
  waiting: number;
  done: number;
  usage?: { provider: string; name: string; bars: { label: string; percent: string; fraction: number; warning: boolean }[] }[];
  workspaces: { id: string; name: string; path: string; local: boolean; git?: GitInfo; rows: string[] }[];
}

export interface ShellModel {
  version: string;
  dialogOpen: boolean;
  dashboard?: DashboardModel;
  panes: PaneInfo[];
  entry: Counts;
  workspaces: SidebarWorkspace[];
  header?: HeaderModel;
  layout?: LayoutModel;
  error?: string;
  resource?: string;
  countdown?: string;
  status: StatusModel;
  usage?: UsageDetails;
  setupNeeded: boolean;
}

export interface AddMenuEntry {
  entry: string;
  label: string;
  provider?: string;
  shortcut?: string;
}

export interface AddMenu {
  workspaceId?: string;
  entries: AddMenuEntry[];
  disabled: boolean;
}

/** A dialog one of the window's flows asked the page to draw; the page answers it with `dialogAnswer`. */
export type DialogRequest =
  | { type: 'confirm'; title: string; message: string; primary?: string; close: string; defaultPrimary: boolean }
  | { type: 'rename'; title: string; hint: string; current: string; field: string; save: string; cancel: string; automatic?: string }
  | { type: 'resumeChoice'; provider: string; title: string; message: string; checking: boolean }
  | { type: 'resumeList'; provider: string; title: string; path: string };

export interface OpenDialog {
  id: number;
  dialog: DialogRequest;
}

export interface ResumeRow {
  id: string;
  provider: string;
  title: string;
  untitled: boolean;
  beta: boolean;
  details: string;
  busy: boolean;
  help: string;
}

export interface ResumeRows {
  rows: ResumeRow[];
  hidden?: string;
  empty?: string;
  reason?: string;
}

/** host → web pushes of the frame. */
export type ShellPush =
  | { kind: 'shell'; shell: ShellModel }
  | { kind: 'dialog'; id: number; dialog: DialogRequest }
  | { kind: 'dialogClose'; id: number }
  | { kind: 'dialogUpdate'; id: number; checking: boolean }
  | { kind: 'select'; paneId: string };
