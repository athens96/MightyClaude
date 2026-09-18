/** Wire types for the MightyClaude mobile protocol ("m1", protocol version 1). */

export const PROTOCOL_VERSION = 1;
/** Server-side limit for submitted text. */
export const MAX_TEXT_BYTES = 32 * 1024;
/** Server-side limit for the long-poll `wait` query parameter, in seconds. */
export const MAX_WAIT_SECONDS = 10;

export type SessionKind = 'claude' | 'shell';
export type Provider = 'claude' | 'codex' | 'gemini';
export type SessionStatus = 'idle' | 'running' | 'completed' | 'error' | 'stopped';
export type LogEntryKind = 'user' | 'assistant' | 'system' | 'output' | 'error';
export type SubmitAccepted = 'started' | 'steered' | 'queued';

export interface HostInfo {
  protocol: number;
  hostId: string;
  hostName: string;
  appVersion: string;
  platform: string;
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

export interface MobileSessionDetail {
  protocol: number;
  revision: number;
  session: MobileSessionSummary;
  entries: LogEntry[];
  permissions: MobilePermission[];
  queued: QueuedItem[];
  usage?: MobileUsage;
  elapsedSeconds?: number;
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
