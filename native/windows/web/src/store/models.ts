// The C# wire records (MightyCode.Core) as the host serializes them with `Wire.Json`: JsonSerializerDefaults.Web
// (camelCase), nulls omitted. A field the C# side may leave null is optional here; dictionary keys keep their case.
// Field names come from Models.cs, PaneLayout.cs, AgentActivity.cs, ToolPermissions.cs, MightyGraph.cs,
// ExecutionGraph.cs, ClaudePlanMode.cs, SessionUsage.cs, AgentImages.cs and AntigravityPermissions.cs.

export type SessionStatus = 'idle' | 'running' | 'completed' | 'error' | 'stopped';
export type LogKind = 'user' | 'assistant' | 'system' | 'output' | 'error' | 'image';
export type ProviderId = 'claude' | 'codex' | 'gemini' | 'pi' | 'opencode' | 'hermes';

export interface RemoteWorkspaceReference {
  connectionId: string;
  workspaceId: string;
  hostName: string;
  version: number;
}

export interface RegisteredModelEntry {
  name: string;
  supportsEffort: boolean;
  supportedEffortLevels?: string[];
}

export interface ProviderModeDefaults {
  modeDefaults: Record<string, string>;
  registeredModels: RegisteredModelEntry[];
}

export interface ModelDefaultsConfig {
  claude: ProviderModeDefaults;
  codex: ProviderModeDefaults;
}

export interface Workspace {
  id: string;
  name: string;
  path: string;
  createdAt: string;
  modelDefaults?: ModelDefaultsConfig;
  remote?: RemoteWorkspaceReference;
  /** Where the workspace's agent panes work (Core PaneWorktreeMode): absent is ask, else further or every. */
  paneWorktreeMode?: string;
  worktreeSetupCommand?: string;
  worktreeCopyPaths?: string[];
  keptPaneWorktrees?: { sessionId: string; title: string; provider: string; worktree: PaneWorktree; keptAt: string }[];
}

/** A workspace agent pane's own worktree (Core PaneWorktree.cs, the Mac's PaneWorktree). */
export interface PaneWorktree {
  worktreePath: string;
  branch: string;
  baseBranch: string;
  baseCommit: string;
  checkout: string;
  port: number;
  setupCommand?: string | null;
  setupStatus?: 'running' | 'done' | 'failed' | null;
  setupExitCode?: number | null;
  mergedCommit?: string | null;
  preMergeCommit?: string | null;
  workingFolder?: string | null;
  noteGiven?: boolean | null;
}

/** RunSettingsJsonConverter: maxTurns and maxBudgetUsd are always written (null when unset); the Codex-only ones only when not default. */
export interface RunSettings {
  effort: string;
  permissionMode: string;
  maxTurns: number | null;
  maxBudgetUsd: number | null;
  fastMode?: boolean;
  webSearch?: string;
  networkAccess?: boolean;
  /** An advisor an older build let the pane name (Core RunSettings.Advisor): read, never sent (the app has no advisor). */
  advisor?: string;
}

export interface AgentActivity {
  id: string;
  provider: string;
  kind: string;
  state: string;
  summary: string;
  toolName?: string;
  output?: string;
  durationMs?: number;
}

export interface AgentImageRef {
  hash: string;
  mediaType: string;
  width: number;
  height: number;
  bytes: number;
  source: string;
  path?: string;
}

export interface LogEntry {
  id: string;
  kind: LogKind;
  text: string;
  timestamp: string;
  provider?: string;
  activity?: AgentActivity;
  images?: AgentImageRef[];
}

export interface AgentRunTiming {
  startedAt: string;
  lastObservedAt: string;
  finishedAt?: string;
  isApproximate: boolean;
}

export interface SessionRateLimit {
  kind: string;
  percentUsed?: number;
  resetsAt?: string;
}

export interface SessionUsage {
  provider: string;
  source: string;
  tokenScope: string;
  model?: string;
  selectedModel?: string;
  providerSessionId?: string;
  inputTokens?: number;
  outputTokens?: number;
  cacheReadTokens?: number;
  cacheWriteTokens?: number;
  reasoningTokens?: number;
  totalTokens?: number;
  contextUsedTokens?: number;
  contextWindowTokens?: number;
  costUSD?: number;
  costScope?: string;
  rateLimits?: SessionRateLimit[];
  rateLimitsUpdatedAt?: string;
  updatedAt: string;
}

export interface GraphTokenUsage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

export interface GraphResponseRecord {
  responseId: string;
  model?: string;
  usage: GraphTokenUsage;
  activityIds: string[];
  markedAsConfigured: boolean;
}

/** Who a sub-agent block is, as the CLI said it (Core GraphAgentIdentity, macOS GraphAgentIdentity): every part optional. */
export interface GraphAgentIdentity {
  type?: string;
  requestedModel?: string;
  resolvedModel?: string;
  isolation?: string;
}

/** A sub-agent's or a background command's own start, end, tool calls and exit code, as the app saw them (Core GraphAgentFigures). */
export interface GraphAgentFigures {
  startedAt?: string;
  endedAt?: string;
  toolCalls?: number;
  exitCode?: number;
}

/** One consultation of the advisor (Core GraphAdvisorCall). */
export interface GraphAdvisorCall {
  id: string;
  caller?: string;
  startedAt?: string;
  endedAt?: string;
  state: string;
  redacted?: boolean;
  text?: string;
  errorCode?: string;
  beforeChange?: boolean;
  followed?: boolean;
}

/** A run's use of the advisor, on its main block (Core GraphAdvisor). */
export interface GraphAdvisor {
  model?: string;
  usage?: GraphTokenUsage;
  calls?: GraphAdvisorCall[];
  total?: number;
}

export interface MightyGraphAgent {
  id: string;
  parentID?: string;
  title: string;
  input: string;
  status: string;
  entries: LogEntry[];
  kind?: string;
  usage?: GraphTokenUsage;
  activityGeneration?: number;
  responseRecords?: GraphResponseRecord[];
  /** Started in the background: a background sub-agent, and every task block. */
  background?: boolean;
  /** Claude's reply in the turn this block's notice alone opened. */
  noticeReply?: string;
  /** A copy in a notice run: the id of the finished background block it copies. */
  sourceBlockId?: string;
  /** Who a sub-agent is (absent on other blocks and in older saves). */
  identity?: GraphAgentIdentity;
  /** A sub-agent's or a task's start, end, tool calls and exit code (absent on other blocks and in older saves). */
  figures?: GraphAgentFigures;
}

export interface MightyGraphRun {
  id: string;
  input: string;
  status: string;
  rootEntries: LogEntry[];
  agents: MightyGraphAgent[];
  resultEntries: LogEntry[];
  sourceRunID?: string;
  finalOutput?: string;
  usage?: GraphTokenUsage;
  responseRecords?: GraphResponseRecord[];
  provider?: string;
  nodeModelLabel?: string;
  configuredModel?: string;
  /** Claude's reply to finished background work, not a request the user sent (mighty/notice.ts); absent otherwise. */
  noticeRun?: boolean;
  /** A notice run's title, "<kind> · <name>" (Core BackgroundNoticeRuns.Title); absent in request runs and older saves. */
  noticeTitle?: string;
  /** The pane's effort when the request started ("default" when the CLI decides); absent in older saves. */
  configuredEffort?: string;
  /** The run's consultations of the advisor, as an older build recorded them: read, never drawn (the app has no advisor). */
  advisor?: GraphAdvisor;
  /** The advisor's model an older build recorded for the request: read, never drawn. */
  configuredAdvisor?: string;
  /** The request's checklist as steps, with when each began and ended (Core/macOS FlowSteps); absent without a list and in notice runs. */
  steps?: FlowStep[];
  /** The keys of the earlier request's done items, which are never this request's steps. */
  stepsCarried?: string[];
}

/** One step of a request's checklist (Core/macOS FlowStep): times are ISO 8601, when the app saw it start and end. */
export interface FlowStep {
  key: string;
  content: string;
  activeForm?: string;
  /** pending, in_progress or completed. */
  status: string;
  startedAt?: string;
  endedAt?: string;
}

export interface GraphBlockSize {
  width: number;
  height: number;
}

export interface PlanRecord {
  id: string;
  runId: string;
  plan: string;
  receivedAt: string;
  decidedAt: string;
  outcome: string;
  feedback?: string;
  planTruncated?: boolean;
  graphRunId?: string;
  launchOverride?: string;
}

export interface TodoItem {
  content: string;
  status: string;
  activeForm?: string;
  id?: string;
}

export interface TodoProgress {
  items: TodoItem[];
  /** The graph run of the request whose stream last changed the list; older saved lists have none. */
  runId?: string;
}

export interface BackgroundTask {
  id: string;
  kind: string;
  description: string;
  startedAt: string;
  status: string;
  toolUseId?: string;
  summary?: string;
  endedAt?: string;
}

export interface BackgroundWork {
  tasks: BackgroundTask[];
  turnEnded: boolean;
}

export interface RunSession {
  id: string;
  workspaceId: string;
  title: string;
  kind: string;
  status: SessionStatus;
  model: string;
  provider: string;
  settings: RunSettings;
  logs: LogEntry[];
  resumeId?: string;
  createdAt: string;
  draft: string;
  runTiming?: AgentRunTiming;
  sessionUsage?: SessionUsage;
  agentViewMode?: 'default' | 'mighty';
  mightyStyle?: string;
  mightyStyleHash?: string;
  mightyStyleSince?: string;
  graphRuns?: MightyGraphRun[];
  graphResultSize?: GraphBlockSize;
  graphPlanSize?: GraphBlockSize;
  graphBlockSizes?: Record<string, GraphBlockSize>;
  graphSizesLayout?: number;
  graphViewMode?: string;
  /** The flow's session log folded to its status line (written only when folded). */
  flowLogCollapsed?: boolean;
  workspaceProfileKey?: string;
  ownerSessionId?: string;
  titleMode?: string;
  planHistory?: PlanRecord[];
  todoProgress?: TodoProgress;
  backgroundWork?: BackgroundWork;
  parentSessionId?: string;
  workingFolder?: string;
  /** Present on an agent pane that works in its own worktree (Core RunSession.PaneWorktree). */
  paneWorktree?: PaneWorktree | null;
  /** The shell blocks of the agent's replies the user ran (MightyCode.Core/ShellBlockRuns.cs), one per block key. */
  shellRuns?: ShellBlockRun[];
}

export type ShellBlockRunState = 'running' | 'completed' | 'failed' | 'stopped' | 'interrupted';

/** A shell block's run: `key` is `<entryId>#<n>`, `output` its cleaned output's last 16 KB. */
export interface ShellBlockRun {
  key: string;
  command: string;
  state: ShellBlockRunState;
  exitCode?: number;
  output: string;
  startedAt: string;
  endedAt?: string;
}

export interface PaneLayoutNode {
  id: string;
  kind: string;
  sessionIds: string[];
  selectedSessionId?: string;
  axis?: string;
  ratio: number;
  children: PaneLayoutNode[];
}

export interface MobileRemoteSettings {
  enabled: boolean;
  relayURL: string;
  allowLegacyPhones: boolean;
  directEnabled: boolean;
  directPort: number;
}

export interface PhaseModelsSnapshot {
  claudeMain: string;
  claudeOpusAlias: string;
  claudeSonnetAlias: string;
  claudeHaikuAlias: string;
  claudeSubagentDefault: string;
  codexReviewModel: string;
  codexSubagentDefault: string;
  codexPlanModeReasoningEffort: string;
  claudeMainEffort?: string;
  codexMainEffort?: string;
  codexSubagentEffort?: string;
}

export interface AppSnapshot {
  version: number;
  workspaces: Workspace[];
  sessions: RunSession[];
  activeWorkspaceId?: string;
  activeSessionId?: string;
  expandedWorkspaceIds?: string[];
  layout: string;
  paneLayouts?: Record<string, PaneLayoutNode>;
  paneLayoutModes?: Record<string, string>;
  paneLayoutActiveSessionIds?: Record<string, string>;
  theme: string;
  sidebarWidth: number;
  sidebarCollapsed: boolean;
  trustedStatusLines?: Record<string, string>;
  statusLineEnabled: boolean;
  completionNotificationsEnabled: boolean;
  autoUpdateCLIs?: boolean;
  autoUpdatePlugins?: boolean;
  autoLoginCLIs?: boolean;
  backgroundWorkOpen?: boolean;
  antigravityMigrated?: boolean;
  referenceBubbleWidth?: number;
  referenceBubbleHeight?: number;
  claudeDirectUsageLookupEnabled: boolean;
  appUpdateAutoCheck: boolean;
  appUpdateLastCheckedAt?: string;
  appUpdateManifestUrlOverride?: string;
  languagePreference: string;
  settingsPane: string;
  agentWebOpenChoices?: Record<string, string>;
  mobileRemote: MobileRemoteSettings;
  modelDefaults?: ModelDefaultsConfig;
  phaseModels?: PhaseModelsSnapshot;
  browserEngineEnabled: boolean;
}

/** Every AppSnapshot field but the panes: what a delta carries when the rest of the state changed. */
export type AppState = Omit<AppSnapshot, 'sessions'>;

export interface ToolPermissionRequest {
  id: string;
  runId: string;
  toolUseId: string;
  toolName: string;
  inputJson: string;
  summary: string;
  reason?: string;
  blockedPath?: string;
  state: string;
  canAllow: boolean;
  canAnswerQuestions: boolean;
  canAnswerPlan: boolean;
  receivedAt?: string;
}

export interface AntigravityDeniedAction {
  kind: string;
  action: string;
  displayName: string;
  target?: string;
  rule?: string;
}

export interface AntigravityPermissionRequest {
  id: string;
  runId: string;
  actions: AntigravityDeniedAction[];
  receivedAt?: string;
}

export type RunEventType = 'status' | 'resume' | 'log' | 'activity' | 'usage' | 'graph_run' | 'plan' | 'todos' | 'background' | 'antigravityPermission';

export interface RunEvent {
  sessionId: string;
  type: RunEventType | string;
  entry?: LogEntry;
  status?: SessionStatus;
  resumeId?: string;
  activity?: AgentActivity;
  usage?: SessionUsage;
  permission?: ToolPermissionRequest;
  graphRun?: MightyGraphRun;
  reason?: string;
  plan?: PlanRecord;
  todos?: TodoProgress;
  background?: BackgroundWork;
  antigravityPermission?: AntigravityPermissionRequest;
}
