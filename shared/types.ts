export type SessionKind = 'claude' | 'shell'
export type SessionStatus = 'idle' | 'running' | 'completed' | 'error' | 'stopped'
export type LayoutMode = 'grid' | 'columns' | 'focus'
export type Theme = 'dark' | 'light'
export type ProviderId = 'claude' | 'codex' | 'gemini'
/** Model aliases and provider-specific IDs returned by the installed Claude CLI. */
export type ClaudeModel = string
export type ClaudeEffort = 'default' | 'low' | 'medium' | 'high' | 'xhigh' | 'max'
export interface ClaudeRunSettings {
  effort: ClaudeEffort
  permissionMode: 'manual' | 'plan' | 'acceptEdits'
  maxTurns: number | null
  maxBudgetUsd: number | null
}

export interface ClaudeModelOption {
  value: ClaudeModel
  displayName: string
  description: string
  resolvedModel?: string
  supportsEffort?: boolean
  supportedEffortLevels?: Exclude<ClaudeEffort, 'default'>[]
}

export interface ClaudeModelCatalog {
  source: 'cli' | 'fallback' | 'preview'
  models: ClaudeModelOption[]
  detail: string
}

export interface Workspace {
  id: string
  name: string
  path: string
  createdAt: string
  remote?: { connectionId: string; workspaceId: string; hostName: string }
}

export interface LogEntry {
  id: string
  kind: 'user' | 'assistant' | 'system' | 'output' | 'error'
  text: string
  timestamp: string
  provider?: ProviderId
}

export interface RunSession {
  id: string
  workspaceId: string
  title: string
  kind: SessionKind
  status: SessionStatus
  model: ClaudeModel
  provider?: ProviderId
  settings?: ClaudeRunSettings
  logs: LogEntry[]
  resumeId?: string
  createdAt: string
}

export interface AppSnapshot {
  version: 1
  workspaces: Workspace[]
  sessions: RunSession[]
  activeWorkspaceId: string | null
  activeSessionId: string | null
  layout: LayoutMode
  theme: Theme
  sidebarWidth: number
}

export interface StartRunRequest {
  sessionId: string
  workspaceId: string
  kind: SessionKind
  input: string
  model: ClaudeModel
  provider?: ProviderId
  settings?: ClaudeRunSettings
  resumeId?: string
}

export type RunEvent =
  | { sessionId: string; type: 'log'; entry: LogEntry }
  | { sessionId: string; type: 'status'; status: SessionStatus }
  | { sessionId: string; type: 'resume'; resumeId: string }

export interface RuntimeInfo {
  platform: 'darwin' | 'win32' | 'linux' | 'browser'
  appVersion: string
  claudeAvailable: boolean
  claudeVersion?: string
  modelCatalog?: ClaudeModelCatalog
  providers?: ProviderRuntime[]
  mods?: {
    status: 'preview' | 'unavailable' | 'unsupported' | 'available'
    minimumVersion: string
    detail: string
  }
}

export interface ProviderRuntime {
  id: ProviderId
  name: string
  available: boolean
  version?: string
  detail: string
  modelCatalog: ClaudeModelCatalog
  capabilities: {
    effort: boolean
    permissionModes: ClaudeRunSettings['permissionMode'][]
    maxTurns: boolean
    maxBudgetUsd: boolean
    resume: boolean
  }
}

export interface RemoteConnectionInfo {
  id: string
  name: string
  address: string
  status: 'connected' | 'disconnected'
  hostId?: string
  hostName?: string
  workspaces: Workspace[]
  runtime?: RuntimeInfo
  detail?: string
}

export interface RemoteState {
  tailscale: { available: boolean; addresses: string[]; deviceName?: string; detail: string }
  host: {
    enabled: boolean
    address?: string
    token?: string
    port?: number
    workspaceIds: string[]
    activeRuns: number
    detail?: string
  }
  connections: RemoteConnectionInfo[]
}

export interface ShareRequest { workspaceIds: string[]; port?: number }
export interface ConnectRemoteRequest { name: string; address: string; token: string }

export interface DesktopBridge {
  isNative: boolean
  loadState(): Promise<AppSnapshot>
  saveState(snapshot: AppSnapshot): Promise<void>
  pickWorkspace(): Promise<Workspace | null>
  getRuntimeInfo(): Promise<RuntimeInfo>
  startRun(request: StartRunRequest): Promise<void>
  stopRun(sessionId: string): Promise<void>
  onRunEvent(listener: (event: RunEvent) => void): () => void
  onBeforeQuit?(listener: () => Promise<void>): () => void
  windowAction(action: 'minimize' | 'maximize' | 'close'): void
  getRemoteState?(): Promise<RemoteState>
  startSharing?(request: ShareRequest): Promise<RemoteState>
  stopSharing?(): Promise<RemoteState>
  connectRemote?(request: ConnectRemoteRequest): Promise<RemoteState>
  refreshRemote?(connectionId: string): Promise<RemoteState>
  disconnectRemote?(connectionId: string): Promise<RemoteState>
  importRemoteWorkspace?(request: { connectionId: string; workspaceId: string }): Promise<Workspace>
}

export const EMPTY_SNAPSHOT: AppSnapshot = {
  version: 1,
  workspaces: [],
  sessions: [],
  activeWorkspaceId: null,
  activeSessionId: null,
  layout: 'grid',
  theme: 'dark',
  sidebarWidth: 252,
}
