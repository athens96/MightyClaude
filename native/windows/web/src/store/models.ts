// The C# wire records (MightyClaude.Core) as the host serializes them with `Wire.Json`: JsonSerializerDefaults.Web
// (camelCase), nulls omitted. A field the C# side may leave null is optional here; dictionary keys keep their case.
// Field names come from Models.cs, PaneLayout.cs, AgentActivity.cs, ToolPermissions.cs, MightyGraph.cs,
// ExecutionGraph.cs, ClaudePlanMode.cs, SessionUsage.cs, AgentImages.cs and AntigravityPermissions.cs.

export type SessionStatus = 'idle' | 'running' | 'completed' | 'error' | 'stopped';
export type LogKind = 'user' | 'assistant' | 'system' | 'output' | 'error' | 'image';
export type ProviderId = 'claude' | 'codex' | 'gemini';

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
  workspaceProfileKey?: string;
  ownerSessionId?: string;
  titleMode?: string;
  planHistory?: PlanRecord[];
  todoProgress?: TodoProgress;
  backgroundWork?: BackgroundWork;
  parentSessionId?: string;
  workingFolder?: string;
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
  showsBackgroundWork?: boolean;
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
