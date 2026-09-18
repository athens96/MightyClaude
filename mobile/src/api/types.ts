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

export type SessionKind = 'claude' | 'shell';
export type Provider = 'claude' | 'codex' | 'gemini';
export type SessionStatus = 'idle' | 'running' | 'completed' | 'error' | 'stopped';
export type LogEntryKind = 'user' | 'assistant' | 'system' | 'output' | 'error';
export type SubmitAccepted = 'started' | 'steered' | 'queued';
export type SubmitMode = 'steer' | 'queue';
export type AgentViewMode = 'plain' | 'mighty';
export type MightyStyle = 'cli' | 'ouroboros' | 'paperthin';
export type CommandSource = 'app' | 'builtin' | 'project' | 'user' | 'plugin';
/** The two Mighty styles that drive the pane through `/guided`; `cli` has no panel. */
export type GuidedStyle = 'ouroboros' | 'paperthin';
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
  | 'attachments';

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
  remote: boolean;
}

export interface MobileSessionPreview {
  kind: string;
  text: string;
}

export interface MobileSessionSummary {
  id: string;
  workspaceId: string;
  title: string;
  kind: SessionKind;
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
}

export interface MobileSettingsOptions {
  models: SettingOption[];
  permissionModes: SettingOption[];
  efforts: SettingOption[];
  mightyStyles: SettingOption[];
}

export interface MobileSettings {
  /** False while the pane is running; the host answers 409 to writes. */
  editable: boolean;
  model: string;
  permissionMode: string;
  effort?: string;
  agentViewMode: string;
  mightyStyle: string;
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
  summary?: string;
  /** Up to `MAX_BLOCK_OUTPUT` characters. */
  output?: string;
  durationMs?: number;
}

export interface MobileMightyRun {
  id: string;
  /** What was asked; shown as a one-line preview. */
  input: string;
  title?: string;
  status: string;
  blocks: MobileBlock[];
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

export interface MobileMighty {
  style: string;
  /** The last 20 requests, oldest first. */
  runs: MobileMightyRun[];
  ouroboros?: MobileOuroboros;
  paperthin?: MobilePaperthin;
}

/** Body of `POST /guided`; `text` is left out when the skill takes none. */
export interface GuidedRequest {
  style: GuidedStyle;
  skill: string;
  text?: string;
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
