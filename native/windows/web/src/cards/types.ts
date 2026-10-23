// What the host pushes of the cards above a pane's composer and its status line (MainWindow.WebShell.Cards.cs, records
// Web*Card): read from the never-shown WinUI pane view, so which card shows and every rule behind it stays C#'s. Words the
// host already put together (titles with counts, C# string constants) come as text; fixed labels are locale keys here.

export interface NextActionsCard {
  kind: 'nextActions';
  entryId: string;
  actions: string[];
}

export interface PlanRecord {
  id: string;
  outcomeTitle: string;
  tone: 'done' | 'wait' | 'stop';
  time: string;
  headline: string;
  plan: string;
  truncated: boolean;
  feedback?: string;
}

export interface PlanHistoryCard {
  kind: 'planHistory';
  /** Newest first. */
  records: PlanRecord[];
}

export interface BackgroundTask {
  text: string;
  status: string;
  /** Kind · status · elapsed · notice, as the WinUI row says it. */
  detail: string;
}

export interface BackgroundCard {
  kind: 'background';
  count: number;
  summary: string;
  open: boolean;
  tasks: BackgroundTask[];
}

export interface PermissionField {
  label: string;
  value: string;
  code: boolean;
}

export interface PermissionCard {
  kind: 'permission';
  requestId: string;
  title: string;
  tool: string;
  waiting?: string;
  headline?: string;
  fields: PermissionField[];
  summary?: string;
  path?: string;
  reason?: string;
  cannotAllow?: string;
  canAllow: boolean;
  inputJson: string;
  rawJson: string;
  onceOnly: string;
  deny: string;
  allowOnce: string;
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

export interface QuestionnaireCard {
  kind: 'questionnaire';
  requestId: string;
  count: number;
  questions: Question[];
}

export interface PlanCard {
  kind: 'plan';
  requestId: string;
  count: number;
  received: string;
  plan: string;
  /** The diagram draws the plan; only the answers show here. */
  actionsOnly: boolean;
  sending: boolean;
  error?: string;
}

export interface AntigravityAction {
  heading: string;
  target?: string;
  code: boolean;
  rule?: string;
  onceOnly?: string;
}

export interface AntigravityCard {
  kind: 'antigravity';
  cardId: string;
  runId: string;
  actions: AntigravityAction[];
  reason?: string;
  busy: boolean;
  canAllow: boolean;
  canAllowAlways: boolean;
  alwaysTip?: string;
}

export interface WebOpenCard {
  kind: 'webOpen';
  requestId: string;
  url: string;
}

export interface LoginLine {
  text: string;
  tone: 'ink2' | 'wait';
  busy: boolean;
}

export type LoginButton = 'login' | 'cancel' | 'terminal' | 'resend';

export interface LoginCard {
  kind: 'login';
  title: string;
  lines: LoginLine[];
  url?: string;
  asksForCode: boolean;
  buttons: LoginButton[];
  loginEnabled: boolean;
}

export interface StatusSegment {
  text: string;
  /** The terminal's colour as #rrggbb, or none: then `ink` names the token. */
  color?: string;
  ink?: 'ink' | 'ink2';
  dim: boolean;
  bold: boolean;
  italic: boolean;
  underline: boolean;
}

export interface StatusTrust {
  prompt: string;
  command: string;
  allow: string;
  deny: string;
  note: string;
}

export interface StatusLineCard {
  kind: 'statusLine';
  lines: StatusSegment[][];
  error?: string;
  trust?: StatusTrust;
  tip: string;
  label: string;
}

export type Card =
  | NextActionsCard
  | PlanHistoryCard
  | BackgroundCard
  | PermissionCard
  | QuestionnaireCard
  | PlanCard
  | AntigravityCard
  | WebOpenCard
  | LoginCard
  | StatusLineCard;
