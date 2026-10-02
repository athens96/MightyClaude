/** Wire types for the MightyClaude mobile protocol ("m1", protocol version 1). */

export const PROTOCOL_VERSION = 1;
/** Server-side limit for submitted text. */
export const MAX_TEXT_BYTES = 32 * 1024;
/** Server-side limit for the long-poll `wait` query parameter, in seconds. */
export const MAX_WAIT_SECONDS = 10;

/** Longest session title the host accepts, after trimming. */
export const MAX_TITLE_LENGTH = 80;
/** Largest `limit` the entries route accepts. */
export const MAX_ENTRY_PAGE = 100;
/** Page size the app asks for when walking back through history. */
export const ENTRY_PAGE_SIZE = 50;
/** Most `statusLine` lines the host may send. */
export const MAX_STATUS_LINES = 6;

/** Longest `output` the contract lets a Mighty block carry. */
export const MAX_BLOCK_OUTPUT = 2000;
/** Longest `result` the newest settled Mighty run carries. */
export const MAX_RUN_RESULT = 20_000;
/** Longest `summary`, and longest `activity` line, a Mighty block carries. */
export const MAX_BLOCK_SUMMARY = 1000;
/** Most `activity` lines a Mighty block carries. */
export const MAX_BLOCK_ACTIVITY = 8;

/** Attachment limits, the same numbers the Mac enforces. */
export const MAX_ATTACHMENTS = 8;
export const MAX_ATTACHMENT_BYTES = 5 * 1024 * 1024;
export const MAX_ATTACHMENTS_TOTAL_BYTES = 8 * 1024 * 1024;
/** `chunkSize` the host announces today; used when an answer leaves it out. */
export const DEFAULT_CHUNK_SIZE = 196_608;
/** Body limit of the chunk route alone. */
export const MAX_CHUNK_BODY_BYTES = 300 * 1024;
/**
 * Largest chunk whose base64 still fits `MAX_CHUNK_BODY_BYTES`: base64 grows three
 * bytes into four, so `4 * ceil(n / 3) <= 307200` gives `n <= 230400`.
 */
export const MAX_CHUNK_BYTES = 230_400;

/** The pane kinds the phone may create. */
export type SessionKind = 'claude' | 'shell';
/**
 * The IO panes an agent pane owns: its dedicated terminal pane and the browser
 * pane it opened a URL into. The phone lists them beside the agent pane and
 * cannot command them (they arrive with `terminal: true`).
 */
export type AgentIOPaneKind = 'agent-terminal' | 'agent-browser';
/** Every pane kind the host may put in the pane list. */
export type PaneKind = SessionKind | 'browser' | AgentIOPaneKind;

/** True for a pane an agent pane owns rather than one the user opened. */
export function isAgentIOPane(kind: string): kind is AgentIOPaneKind {
  return kind === 'agent-terminal' || kind === 'agent-browser';
}
export type Provider = 'claude' | 'codex' | 'gemini';
export type SessionStatus = 'idle' | 'running' | 'completed' | 'error' | 'stopped';
export type LogEntryKind = 'user' | 'assistant' | 'system' | 'output' | 'error';
export type SubmitAccepted = 'started' | 'steered' | 'queued';
export type SubmitMode = 'steer' | 'queue';
export type AgentViewMode = 'plain' | 'mighty';
/**
 * The fixed `mightyStyle` vocabulary (contract 7.2). A pane running any other style
 * carries `cli` here and the truth in `styleId`, so an older phone sees a plain CLI pane.
 */
export type MightyStyle = 'cli' | 'ouroboros' | 'paperthin';
export type CommandSource = 'app' | 'builtin' | 'project' | 'user' | 'plugin';
export type MightyBlockKind = 'main' | 'agent' | 'task' | 'steer' | 'compact' | 'question';
export type MightyBlockStatus = 'running' | 'waiting' | 'completed' | 'error' | 'stopped';
export type CommandAction = 'model' | 'permission' | 'clear' | 'usage' | 'help' | 'rename';
/** The command actions the host runs through `POST /command`. */
export type MessageCommandAction = 'clear' | 'usage' | 'help';

/** Optional host features; a feature is shown only when `/m1/info` advertises it. */
export type Capability =
  | 'submit-mode'
  | 'queue'
  | 'pane'
  | 'history'
  | 'settings'
  | 'commands'
  | 'mighty'
  | 'status'
  | 'attachments'
  | 'style'
  | 'files'
  | 'screenShare';

export const CAPABILITIES: readonly Capability[] = [
  'submit-mode',
  'queue',
  'pane',
  'history',
  'settings',
  'commands',
  'mighty',
  'status',
  'attachments',
  'style',
  'files',
  'screenShare',
] as const;

export interface HostInfo {
  protocol: number;
  hostId: string;
  hostName: string;
  appVersion: string;
  platform: string;
  /** Absent on hosts older than the capability extension. */
  capabilities?: string[];
}

export interface MobileWorkspace {
  id: string;
  name: string;
  path: string;
}

export interface MobileSessionPreview {
  kind: string;
  text: string;
}

export interface MobileSessionSummary {
  id: string;
  workspaceId: string;
  title: string;
  kind: PaneKind;
  provider: Provider;
  model: string;
  status: SessionStatus;
  revision: number;
  updatedAt: string;
  preview?: MobileSessionPreview;
  pendingPermissions: number;
  pendingQuestions: number;
  queued: number;
  resumeId?: string;
  /** Local terminal pane: cannot receive commands from mobile. */
  terminal: boolean;
  agentViewMode?: AgentViewMode;
  mightyStyle?: MightyStyle;
  /**
   * The style actually driving the pane ("style"). Open string: the host owns the set,
   * and `styleId` wins over `mightyStyle` whenever both arrive (contract 7.2).
   */
  styleId?: string;
  /**
   * "auto" — title follows the latest request; "fixed" — user renamed it.
   * Absent on older hosts; treat absence as "auto".
   */
  titleMode?: 'auto' | 'fixed';
}

export interface MobileState {
  protocol: number;
  revision: number;
  hostName: string;
  workspaces: MobileWorkspace[];
  sessions: MobileSessionSummary[];
}

export interface LogActivity {
  id: string;
  kind: string;
  state: string;
  summary: string;
  toolName?: string;
  output?: string;
  /** Tool wall-clock time, shown next to the activity. */
  durationMs?: number;
  provider?: string;
}

export interface LogEntry {
  id: string;
  kind: LogEntryKind;
  text: string;
  timestamp: string;
  provider?: string;
  activity?: LogActivity;
}

export interface PermissionField {
  label: string;
  value: string;
}

export interface QuestionOption {
  label: string;
  description: string;
}

export interface Question {
  header: string;
  question: string;
  multiSelect: boolean;
  options: QuestionOption[];
}

export interface Questionnaire {
  questions: Question[];
}

export interface MobilePermission {
  id: string;
  runId: string;
  toolName: string;
  title: string;
  headline?: string;
  fields: PermissionField[];
  summary: string;
  canAllow: boolean;
  questionnaire?: Questionnaire;
}

export interface QueuedItem {
  id: string;
  text: string;
}

export interface MobileUsage {
  model?: string;
  contextUsedTokens?: number;
  contextWindowTokens?: number;
  contextPercent?: number;
  totalTokens?: number;
  costUSD?: number;
}

export interface SettingOption {
  id: string;
  label: string;
  /** Drawn next to the label; a style that is not bundled carries its source here. */
  badge?: string;
}

/**
 * One choosable style ("style"). `source` stays a plain string so a word this app does
 * not know can never read as `bundled`, which is the one value that goes unbadged, and
 * it is absent on `cli` alone — the word for running no style at all.
 */
export interface StyleOption {
  id: string;
  label: string;
  source?: string;
}

export interface MobileSettingsOptions {
  models: SettingOption[];
  permissionModes: SettingOption[];
  efforts: SettingOption[];
  mightyStyles: SettingOption[];
  /** Approved and bundled styles this pane may run; absent on a host without "style". */
  styles?: StyleOption[];
}

export interface MobileSettings {
  /** False while the pane is running; the host answers 409 to writes. */
  editable: boolean;
  model: string;
  permissionMode: string;
  effort?: string;
  agentViewMode: string;
  mightyStyle: string;
  /** Absent on a host without "style". */
  styleId?: string;
  options: MobileSettingsOptions;
}

/** One coloured run of text inside a status line. */
export interface StatusSegment {
  text: string;
  /** `#RRGGBB`; anything else is dropped by the sanitiser. */
  fg?: string;
  bold?: boolean;
}

export interface StatusLine {
  lines: StatusSegment[][];
}

export interface RateLimit {
  label: string;
  usedPercent: number;
  resetsAt?: string;
}

/**
 * One block of a Mighty run. `kind` and `status` stay plain strings: the contract fixes
 * the words the host uses today, and a word it adds later must render neutrally instead
 * of being forced into one we know.
 */
export interface MobileBlock {
  id: string;
  kind: string;
  title: string;
  status: string;
  /** What the block was asked: a child's prompt, or the request itself on `main`. */
  summary?: string;
  /** Up to `MAX_BLOCK_OUTPUT` characters. */
  output?: string;
  durationMs?: number;
  /** Model label projected from the Mac's request node; present only on `main` blocks. */
  nodeModelLabel?: string;
  /**
   * The block's most recent steps, oldest first, while it is still running or waiting;
   * absent once it settles and on a host that predates the field.
   */
  activity?: string[];
}

export interface MobileMightyRun {
  id: string;
  /** What was asked; shown as a one-line preview. */
  input: string;
  title?: string;
  status: string;
  blocks: MobileBlock[];
  /**
   * The run's final answer, up to `MAX_RUN_RESULT` characters. Only the newest run
   * carries it, once it has settled; absent on older runs and on a Mac that predates it.
   */
  result?: string;
  /** Older blocks the phone dropped to keep the list drawable; absent when none were. */
  omittedBlocks?: number;
}

/** One Ouroboros skill the host offers as a button. */
export interface OuroborosAction {
  skill: string;
  title: string;
  help: string;
}

export interface MobileOuroboros {
  /** Phase id (`goal`, `interview`, …); an unknown one is shown as it arrived. */
  phase: string;
  /** False while a prerequisite (plugin, uvx) is missing on the Mac. */
  ready: boolean;
  /** Skills whose prompt carries what the user typed; the rest go out bare. */
  takesText: string[];
  next: OuroborosAction[];
  all: OuroborosAction[];
}

export interface PaperthinSkill {
  name: string;
  emoji: string;
  summary: string;
  scope: string;
  /** Only a human may fire it. */
  userInvoked: boolean;
  readOnly: boolean;
}

export interface PaperthinDomain {
  id: string;
  title: string;
  /** Where the domain sits on the 2×2 map. */
  axis: string;
  question: string;
  skills: PaperthinSkill[];
}

export interface PaperthinCasebook {
  name: string;
  weight: string;
  files: string[];
}

export interface MobilePaperthin {
  installed: boolean;
  recommended?: string;
  domains: PaperthinDomain[];
  casebook?: PaperthinCasebook;
}

/**
 * The style behind a guided pane (contract 7.3). `source` and `tint` stay plain strings:
 * the phone maps the words it knows and falls back for the rest, so only the exact word
 * `bundled` goes without a source badge and an unknown palette draws in the accent.
 */
export interface StylePanelStyle {
  id: string;
  name: string;
  source: string;
  tint?: string;
}

export interface StylePhase {
  id: string;
  title: string;
  /** Counted from zero; `count` is 0 when the host sends no ordering. */
  index: number;
  count: number;
}

export interface StyleGroup {
  id: string;
  title: string;
  /** Where the group sits on the map; a group list without one is a plain list. */
  axis?: string;
  question?: string;
  selected: boolean;
  /** Action ids, in the order the manifest wrote them. */
  actions: string[];
}

/**
 * One action of the style's catalogue. `flags` keeps the host's words: a mark this app
 * does not draw is carried rather than guessed at.
 */
export interface StyleAction {
  id: string;
  title: string;
  /** Exactly one grapheme; this app has no SF Symbol renderer, so `icon` is dropped. */
  glyph?: string;
  help: string;
  scope?: string;
  takesText: boolean;
  /** A UI hint only: the chip is disabled while the composer is empty. */
  requiresText: boolean;
  flags: string[];
  /** True on the first entry of `next`. */
  prominent: boolean;
}

/** Read-only information a built-in capability produced; the phone cannot open files. */
export interface StyleAttachment {
  id: string;
  title: string;
  detail?: string;
  readOnly: boolean;
}

export interface StyleSetup {
  ready: boolean;
  missing: string[];
  hint?: string;
  /** Shown as text. There is no route that runs it from the phone (contract 4.6). */
  installCommand?: string;
}

export interface StylePresentation {
  headerTitle: string;
  source: string;
  tint?: string;
}

/**
 * One rendered state widget — exactly three kinds, closed vocabulary (§1.16).
 * The Mac evaluates state sources and sends the results; the phone just renders.
 * A progress bar's `value` is the count done and `total` the whole count.
 */
export type StyleWidget =
  | { kind: 'progressBar'; value: number; total?: number }
  | { kind: 'list'; items: string[] }
  | { kind: 'label'; text: string };

/**
 * `MobileMighty.panel` — everything one guided pane draws. Present only when the pane is
 * actually running an approved or bundled style, so its absence means a plain CLI pane.
 */
export interface StylePanel {
  style: StylePanelStyle;
  phase?: StylePhase;
  groups: StyleGroup[];
  /** The whole catalogue; `groups` and `next` decide what reaches the screen. */
  actions: StyleAction[];
  /** Action ids in order; the first is the prominent one. */
  next: string[];
  recommended?: string;
  attachments: StyleAttachment[];
  setup: StyleSetup;
  /** The panel's bottom line, with `{phase}` already substituted. */
  guidance?: string;
  presentation: StylePresentation;
  /** Computed state widgets, at most one per source (§1.16). */
  widgets?: StyleWidget[];
}

export interface MobileMighty {
  style: string;
  /** The last 20 requests, oldest first. */
  runs: MobileMightyRun[];
  /** The style actually running, or absent on a host without "style". */
  styleId?: string;
  panel?: StylePanel;
  /**
   * Set by the normaliser, not by the wire: the host sent a `panel` this app could not
   * read. It is kept apart from `panel` being absent, which simply means a CLI pane.
   */
  panelUnreadable?: boolean;
  ouroboros?: MobileOuroboros;
  paperthin?: MobilePaperthin;
}

/** Body of `POST /guided`; `text` is left out when the action takes none. */
export interface GuidedRequest {
  styleId: string;
  actionId: string;
  text?: string;
  /**
   * Set against a host without the "style" capability, whose route only knows the old
   * `{style, skill}` body and answers 400 to anything else. Only the two built-in ids
   * reach it, because that host sends no other style's panel (contract 7.4, 7.5).
   */
  legacy?: boolean;
}

export interface UploadTicket {
  protocol: number;
  uploadId: string;
  chunkSize: number;
}

export interface ChunkReceipt {
  protocol: number;
  ok: boolean;
  received: number;
}

export interface UploadAttachment {
  id: string;
  name: string;
  size: number;
}

export interface CompletedUpload {
  protocol: number;
  attachment: UploadAttachment;
}

export interface MobileSessionDetail {
  protocol: number;
  revision: number;
  session: MobileSessionSummary;
  entries: LogEntry[];
  permissions: MobilePermission[];
  queued: QueuedItem[];
  usage?: MobileUsage;
  elapsedSeconds?: number;
  /** True when entries older than the first one are still on the host. */
  hasOlder?: boolean;
  settings?: MobileSettings;
  /** Present only on a pane in Mighty view, on a host that advertised "mighty". */
  mighty?: MobileMighty;
  statusLine?: StatusLine;
  rateLimits?: RateLimit[];
}

export interface EntriesPage {
  protocol: number;
  entries: LogEntry[];
  hasMore: boolean;
}

export interface MobileCommand {
  name: string;
  description: string;
  /** Where the host found the command; one of {@link CommandSource} when it is known. */
  source: string;
  argumentHint?: string;
  /** When present the phone handles the command itself instead of inserting text. */
  action?: string;
}

export interface CommandsResponse {
  protocol: number;
  commands: MobileCommand[];
}

export interface CommandResponse {
  protocol: number;
  ok: boolean;
  /** Body of `usage` / `help`. */
  message?: string;
}

/**
 * At least one field must be set; the host rejects an empty patch with 400. Values stay
 * plain strings: the host owns the allowed set and answers 400 for anything outside it,
 * so the phone forwards the chosen id instead of quietly replacing it.
 */
export interface SettingsPatch {
  model?: string;
  permissionMode?: string;
  effort?: string;
  agentViewMode?: string;
  mightyStyle?: string;
  /** When present the host ignores `mightyStyle` (contract 7.5). */
  styleId?: string;
}

export interface SubmitOptions {
  mode?: SubmitMode;
  /** Upload ids that finished `/complete`; each may be used once. */
  attachments?: string[];
}

export interface SubmitResponse {
  protocol: number;
  accepted: SubmitAccepted;
}

export interface StopResponse {
  protocol: number;
  stopped: boolean;
}

export interface OkResponse {
  protocol: number;
  ok: boolean;
}

export interface CreateSessionResponse {
  protocol: number;
  sessionId: string;
}

export interface QuestionAnswer {
  selectedOptions: string[];
  customText?: string;
}

/** Keys are the question text, values the chosen option labels. */
export type QuestionAnswers = Record<string, QuestionAnswer>;

// ------------------------------------------------------------------ files ("files")

/** `GET /m1/workspaces/{id}/files`: a folder, at most this many entries. */
export const MAX_FILE_ENTRIES = 2000;

export type FileEntryKind = 'folder' | 'file' | 'symlink-folder' | 'symlink-file';

export interface FileEntry {
  name: string;
  /** Under the workspace root, `/`-separated; what `path=` takes back. */
  relativePath: string;
  kind: FileEntryKind;
  /** Bytes; files only. */
  size?: number;
  /** ISO-8601. */
  modified?: string;
  /** A heavy or generated folder the Mac dims (`node_modules`, `.git`, …). */
  noise: boolean;
}

export interface FileListing {
  protocol: number;
  workspaceId: string;
  path: string;
  entries: FileEntry[];
  truncated: boolean;
}

export type FilePreviewType = 'source' | 'markdown' | 'image' | 'unsupported';
export type UnsupportedReason = 'binary' | 'notRegularFile' | 'tooLarge' | 'undecodable';

/** `GET /m1/workspaces/{id}/file`: which optional fields are present follows `type`. */
export interface FilePreview {
  protocol: number;
  workspaceId: string;
  path: string;
  name: string;
  size: number;
  modified?: string;
  type: FilePreviewType;
  /** `source` only: the Mac highlighter's language, `plain` for unhighlighted text. */
  language?: string;
  /** `source`/`markdown`: the decoding the Mac used, e.g. "UTF-8", "CP949 (EUC-KR)". */
  encoding?: string;
  text?: string;
  /** Only the start of the file is in `text` (512 KiB, less when escaping would overflow). */
  truncated?: boolean;
  lineCount?: number;
  /** `image`: the thumbnail's own format. */
  mime?: 'image/jpeg' | 'image/png';
  /** The image's own size: pixels, or points for svg and pdf. */
  width?: number;
  height?: number;
  thumbnailWidth?: number;
  thumbnailHeight?: number;
  /** The thumbnail, base64 (at most 2048 px on its long side and 512 KiB). */
  data?: string;
  reason?: UnsupportedReason;
}

/** The `code` a refused file request carries beside its message. */
export type FileErrorCode =
  | 'workspaceNotFound'
  | 'notFound'
  | 'outsideWorkspace'
  | 'notReadable'
  | 'notDirectory'
  | 'badPath';

// ─────────────────────────────── 화면 공유 (BETA) ───────────────────────────────
// docs/relay.md "화면 공유 시그널링". The Mac decides everything that matters; these
// types only describe what it says and what the phone is allowed to ask for.

/** Per-phone grant the Mac enforces. A newly paired phone starts at `none`. */
export type ScreenGrant = 'none' | 'view' | 'control';
/** A live session is either watched or driven; `control` needs a fresh signature. */
export type ScreenMode = 'view' | 'control';
/** Negotiated video codec. HEVC is excluded on purpose. */
export type ScreenCodec = 'H264' | 'VP9' | 'AV1';
/** What the phone is on right now, which picks the quality ceiling. */
export type ScreenNetwork = 'wifi' | 'cellular';
/** The ICE path that actually carried the video, as `getStats` reports it. */
export type ScreenCandidateType = 'host' | 'srflx' | 'prflx' | 'relay';

/** One attached display the Mac offers. */
export interface ScreenDisplay {
  displayId: number;
  width: number;
  height: number;
  main: boolean;
}

/** The ceiling for this session: Wi-Fi up to 1080p30, mobile data up to 720p15. */
export interface ScreenQuality {
  width: number;
  height: number;
  fps: number;
  maxBitrateKbps: number;
}

/** A TURN/STUN server, with credentials the relay minted and the Mac forwarded. */
export interface ScreenIceServer {
  urls: string | string[];
  username?: string;
  credential?: string;
}

/** `GET /m1/screen-share/state`'s `screenShare` object. */
export interface ScreenShareState {
  /** The Mac's allow-list flag for this phone; false for a newly paired one. */
  allowed: boolean;
  grant: ScreenGrant;
  /** Always true while the feature ships as BETA. */
  isBeta: boolean;
  displays: ScreenDisplay[];
  /** Present only when `grant` is `control`; signed once per control session. */
  controlChallengeB64?: string;
  /**
   * The fingerprint of the control key the Mac stores for this phone, when it stores one.
   * Not a secret: the phone compares it with its own key's to decide whether to enrol.
   */
  controlKeyFingerprint?: string;
  iceServers?: ScreenIceServer[];
  /** 600 in control, 1800 in view-only. */
  idleTimeoutSeconds?: number;
}

export interface ScreenShareStateResponse {
  screenShare: ScreenShareState;
}

/** `POST /m1/screen-share/sessions` body. */
export interface ScreenSessionRequest {
  mode: ScreenMode;
  displayId: number;
  /** ECDSA DER over `controlChallengeB64`, base64. Control sessions only. */
  controlSignatureB64?: string;
  network: ScreenNetwork;
  /** What this phone can decode, from `getCapabilities`. */
  decodes: ScreenCodec[];
}

export interface ScreenSessionResponse {
  sessionId: string;
  mode: ScreenMode;
  displayId: number;
  codec: ScreenCodec;
  quality: ScreenQuality;
}

/** `POST /m1/screen-share/control-key` reply. */
export interface ScreenControlKeyResponse {
  fingerprint: string;
}

/** Why the Mac refused a session or a control key; `4xx {"error":{"reason":…}}`. */
export type ScreenRejectReason =
  | 'legacy-client'
  | 'device-not-allowed'
  | 'insufficient-grant'
  | 'control-signature'
  | 'concurrency-limit'
  | 'screen-permission'
  | 'lock-screen'
  | 'secure-input'
  | 'session-stopped'
  | 'bad-request'
  | 'control-key-present'
  | 'control-key-pending'
  | 'control-key-not-confirmed';

export const SCREEN_REJECT_REASONS: readonly ScreenRejectReason[] = [
  'legacy-client',
  'device-not-allowed',
  'insufficient-grant',
  'control-signature',
  'concurrency-limit',
  'screen-permission',
  'lock-screen',
  'secure-input',
  'session-stopped',
  'bad-request',
  'control-key-present',
  'control-key-pending',
  'control-key-not-confirmed',
] as const;

/** Why a session stopped on its own. */
export type ScreenEndReason =
  | 'user-stop'
  | 'background'
  | 'peer-failed'
  | 'idle-timeout'
  | 'peer-left'
  | 'display-gone';

/** Why the Mac cut everything off. The Mac has already stopped; this only explains it. */
export type ScreenKillReason =
  | 'revoked'
  | 'grant-downgrade'
  | 'rekey-pairing'
  | 'kill-switch'
  | 'lock-screen'
  | 'secure-input'
  | 'concurrency-limit';

export const SCREEN_KILL_REASONS: readonly ScreenKillReason[] = [
  'revoked',
  'grant-downgrade',
  'rekey-pairing',
  'kill-switch',
  'lock-screen',
  'secure-input',
  'concurrency-limit',
] as const;

/** Idle limits the contract fixes: 10 min driving, 30 min watching. */
export const SCREEN_IDLE_TIMEOUT_CONTROL_SECONDS = 600;
export const SCREEN_IDLE_TIMEOUT_VIEW_SECONDS = 1800;
/** The phone ends a session this long after the app leaves the foreground. */
export const SCREEN_BACKGROUND_GRACE_SECONDS = 30;
/** One plaintext signalling JSON stays well under the relay's 1 MiB frame. */
export const SCREEN_SIGNAL_MAX_BYTES = 64 * 1024;
/** Clipboard limit, both directions, before compression. */
export const SCREEN_CLIPBOARD_MAX_BYTES = 1024 * 1024;
