import { isAbsolute, win32 } from 'node:path'
import { DEFAULT_RUN_SETTINGS, EFFORT_LEVELS, effortLevelsForModel, fallbackModelCatalog, isClaudeModel } from '../../shared/claude-options'
import { fallbackProviderRuntime, normalizeProvider, normalizeProviderSettings, providerEffortLevels, providerLabel, PROVIDER_IDS } from '../../shared/provider-options'
import { EMPTY_SNAPSHOT, type AppSnapshot, type ClaudeModelCatalog, type ClaudeRunSettings, type LogEntry, type ProviderId, type RunSession, type StartRunRequest, type Workspace } from '../../shared/types'

const IDENTIFIER = /^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/
const OFFICIAL_MODEL_ALIASES = new Set(fallbackModelCatalog().models.map((model) => model.value))
const STANDARD_CLAUDE_MODEL_ID = /^claude-(?:(?:opus|sonnet|haiku|fable)-\d+(?:[-.]\d+)*|\d+(?:-\d+)*-(?:opus|sonnet|haiku|fable)(?:-\d+)*)(?:\[1m\])?$/
const SETTINGS_KEYS = new Set(['effort', 'permissionMode', 'maxTurns', 'maxBudgetUsd'])
const STATUSES = new Set(['idle', 'running', 'completed', 'error', 'stopped'])
const LOG_KINDS = new Set(['user', 'assistant', 'system', 'output', 'error'])
export const MAX_INPUT_LENGTH = 100_000
export const MAX_STATE_BYTES = 8 * 1024 * 1024

export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function isIdentifier(value: unknown): value is string {
  return typeof value === 'string' && IDENTIFIER.test(value)
}

function shortText(value: unknown, max: number, fallback = ''): string {
  return typeof value === 'string' ? value.slice(0, max) : fallback
}

function date(value: unknown): string {
  return typeof value === 'string' && Number.isFinite(Date.parse(value)) ? value : new Date().toISOString()
}

/** Also used when recovering state written by an older or interrupted app. */
export function normalizeSnapshot(value: unknown, restoring = false): AppSnapshot {
  if (!isRecord(value) || value.version !== 1 || !Array.isArray(value.workspaces) || !Array.isArray(value.sessions)) {
    return structuredClone(EMPTY_SNAPSHOT)
  }
  const workspaceIds = new Set<string>()
  const workspaces: Workspace[] = []
  for (const row of value.workspaces.slice(0, 64)) {
    if (!isRecord(row) || !isIdentifier(row.id) || workspaceIds.has(row.id) || typeof row.path !== 'string' || row.path.length > 4096 || row.path.includes('\0')) continue
    const remote = isRecord(row.remote) && isIdentifier(row.remote.connectionId) && isIdentifier(row.remote.workspaceId) && typeof row.remote.hostName === 'string' && row.remote.hostName.trim()
      ? { connectionId: row.remote.connectionId, workspaceId: row.remote.workspaceId, hostName: row.remote.hostName.slice(0, 160) }
      : undefined
    if (row.remote !== undefined && !remote) continue
    if (!isAbsolute(row.path) && !(remote && win32.isAbsolute(row.path))) continue
    workspaceIds.add(row.id)
    workspaces.push({ id: row.id, name: shortText(row.name, 120, 'Workspace'), path: row.path, createdAt: date(row.createdAt), ...(remote ? { remote } : {}) })
  }
  const sessionIds = new Set<string>()
  const sessions: RunSession[] = []
  let remainingLogCharacters = 4 * 1024 * 1024
  for (const row of value.sessions.slice(0, 128)) {
    if (!isRecord(row) || !isIdentifier(row.id) || sessionIds.has(row.id) || typeof row.workspaceId !== 'string' || !workspaceIds.has(row.workspaceId) || !['claude', 'shell'].includes(String(row.kind))) continue
    sessionIds.add(row.id)
    const logs: LogEntry[] = []
    if (Array.isArray(row.logs)) {
      for (const entry of row.logs.slice(-400)) {
        if (!isRecord(entry) || !isIdentifier(entry.id) || !LOG_KINDS.has(String(entry.kind)) || typeof entry.text !== 'string' || remainingLogCharacters <= 0) continue
        const text = entry.text.slice(0, Math.min(32_768, remainingLogCharacters))
        remainingLogCharacters -= text.length
        logs.push({ id: entry.id, kind: entry.kind as LogEntry['kind'], text, timestamp: date(entry.timestamp), ...(typeof entry.provider === 'string' && PROVIDER_IDS.includes(entry.provider as ProviderId) ? { provider: entry.provider as ProviderId } : {}) })
      }
    }
    const status = STATUSES.has(String(row.status)) ? row.status as RunSession['status'] : 'idle'
    sessions.push({
      id: row.id, workspaceId: row.workspaceId, kind: row.kind as RunSession['kind'],
      title: shortText(row.title, 120, row.kind === 'claude' ? 'Claude' : 'Command'),
      model: isClaudeModel(row.model) ? row.model : 'default',
      provider: normalizeProvider(row.provider),
      settings: normalizeProviderSettings(normalizeProvider(row.provider), row.settings),
      status: restoring && status === 'running' ? 'stopped' : status,
      logs, createdAt: date(row.createdAt),
      ...(isIdentifier(row.resumeId) ? { resumeId: row.resumeId } : {}),
    })
  }
  const activeWorkspaceId = typeof value.activeWorkspaceId === 'string' && workspaceIds.has(value.activeWorkspaceId) ? value.activeWorkspaceId : workspaces[0]?.id ?? null
  const activeSession = sessions.find((session) => session.id === value.activeSessionId && session.workspaceId === activeWorkspaceId)
  return {
    version: 1, workspaces, sessions, activeWorkspaceId,
    activeSessionId: activeSession?.id ?? sessions.find((session) => session.workspaceId === activeWorkspaceId)?.id ?? null,
    layout: ['grid', 'columns', 'focus'].includes(String(value.layout)) ? value.layout as AppSnapshot['layout'] : 'grid',
    theme: value.theme === 'light' ? 'light' : 'dark',
    sidebarWidth: typeof value.sidebarWidth === 'number' && Number.isFinite(value.sidebarWidth) ? Math.min(400, Math.max(200, value.sidebarWidth)) : 252,
  }
}

export function validateRunSettings(value: unknown): ClaudeRunSettings {
  if (value === undefined) return { ...DEFAULT_RUN_SETTINGS }
  if (!isRecord(value) || Object.keys(value).some((key) => !SETTINGS_KEYS.has(key))) throw new Error('실행 설정의 형식이 올바르지 않습니다.')
  if (value.effort !== 'default' && !EFFORT_LEVELS.includes(value.effort as never)) throw new Error('추론 강도 설정이 올바르지 않습니다.')
  if (typeof value.permissionMode !== 'string' || !['manual', 'plan', 'acceptEdits'].includes(value.permissionMode)) throw new Error('권한 모드 설정이 올바르지 않습니다.')
  if (value.maxTurns !== null && (typeof value.maxTurns !== 'number' || !Number.isInteger(value.maxTurns) || value.maxTurns < 1 || value.maxTurns > 1000)) throw new Error('최대 턴 수는 1부터 1,000까지의 정수로 입력해 주세요.')
  if (value.maxBudgetUsd !== null && (typeof value.maxBudgetUsd !== 'number' || !Number.isFinite(value.maxBudgetUsd) || value.maxBudgetUsd <= 0 || value.maxBudgetUsd > 10_000)) throw new Error('비용 한도는 0보다 크고 10,000달러 이하인 값으로 입력해 주세요.')
  return { effort: value.effort as ClaudeRunSettings['effort'], permissionMode: value.permissionMode as ClaudeRunSettings['permissionMode'], maxTurns: value.maxTurns as number | null, maxBudgetUsd: value.maxBudgetUsd as number | null }
}

/** Structural validation runs before any asynchronous workspace or CLI lookup. */
export function validateStartRequest(value: unknown): StartRunRequest {
  if (!isRecord(value) || !isIdentifier(value.sessionId) || !isIdentifier(value.workspaceId) || (value.kind !== 'claude' && value.kind !== 'shell') || !isClaudeModel(value.model)) {
    throw new Error('실행 요청의 형식이 올바르지 않습니다.')
  }
  if (value.provider !== undefined && (typeof value.provider !== 'string' || !PROVIDER_IDS.includes(value.provider as ProviderId))) throw new Error('지원하지 않는 CLI 제공자입니다.')
  const provider = normalizeProvider(value.provider)
  if (typeof value.input !== 'string' || !value.input.trim() || value.input.length > MAX_INPUT_LENGTH || value.input.includes('\0')) {
    throw new Error('실행할 내용을 입력해 주세요. 입력은 100,000자까지 지원합니다.')
  }
  if (value.resumeId !== undefined && !isIdentifier(value.resumeId)) throw new Error('CLI 세션 ID가 올바르지 않습니다.')
  const settings = validateRunSettings(value.settings)
  if (value.kind === 'claude') {
    const capabilities = fallbackProviderRuntime(provider).capabilities
    if (!capabilities.permissionModes.includes(settings.permissionMode)) throw new Error(`${providerLabel(provider)} CLI는 선택한 권한 모드를 지원하지 않습니다.`)
    if (!capabilities.effort && settings.effort !== 'default') throw new Error(`${providerLabel(provider)} CLI는 실행 창의 추론 강도 설정을 지원하지 않습니다.`)
    if (!capabilities.maxTurns && settings.maxTurns !== null) throw new Error(`${providerLabel(provider)} CLI는 실행 창의 최대 턴 설정을 지원하지 않습니다.`)
    if (!capabilities.maxBudgetUsd && settings.maxBudgetUsd !== null) throw new Error(`${providerLabel(provider)} CLI는 실행 창의 비용 한도 설정을 지원하지 않습니다.`)
    if (provider === 'claude' && /haiku/i.test(value.model) && settings.effort !== 'default') throw new Error('Haiku 모델은 추론 강도 설정을 지원하지 않습니다. 기본값을 선택해 주세요.')
  }
  return {
    sessionId: value.sessionId, workspaceId: value.workspaceId, kind: value.kind,
    input: value.input, model: value.model, provider, settings,
    ...(value.resumeId ? { resumeId: value.resumeId as string } : {}),
  }
}

export function validateProviderSelection(request: StartRunRequest, catalog: ClaudeModelCatalog): void {
  const provider = normalizeProvider(request.provider)
  if (provider === 'claude') return validateModelSelection(request, catalog)
  if (!isClaudeModel(request.model)) throw new Error('모델 ID의 형식이 올바르지 않습니다.')
  const settings = validateRunSettings(request.settings)
  if (settings.effort !== 'default' && !providerEffortLevels(provider, request.model, catalog).includes(settings.effort)) throw new Error(`${providerLabel(provider)}의 선택한 모델이 이 추론 강도를 지원하지 않습니다.`)
}

/** Standard Claude IDs are accepted directly; provider IDs come from this CLI. */
export function validateModelSelection(request: StartRunRequest, catalog: ClaudeModelCatalog): void {
  const official = OFFICIAL_MODEL_ALIASES.has(request.model) || /^(?:opus|sonnet|fable)\[1m\]$/.test(request.model) || STANDARD_CLAUDE_MODEL_ID.test(request.model)
  if (!isClaudeModel(request.model) || (!official && !catalog.models.some((model) => (model.value === request.model || model.resolvedModel === request.model) && isClaudeModel(model.value)))) {
    throw new Error('선택한 모델을 Claude Code 모델 목록에서 확인할 수 없습니다. 모델 목록을 새로고침하거나 공식 모델 별칭을 선택해 주세요.')
  }
  const settings = validateRunSettings(request.settings)
  if (settings.effort !== 'default' && (/haiku/i.test(request.model) || !effortLevelsForModel(request.model, catalog).includes(settings.effort))) {
    throw new Error('선택한 모델이 이 추론 강도를 지원하지 않습니다. 모델에 지원되는 값이나 기본값을 선택해 주세요.')
  }
}
