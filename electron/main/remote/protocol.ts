import { posix, win32 } from 'node:path'
import type { ClaudeModelCatalog, ClaudeModelOption, ProviderRuntime, RunEvent, RuntimeInfo, Workspace } from '../../../shared/types'
import { EFFORT_LEVELS, isClaudeModel } from '../../../shared/claude-options'
import { isIdentifier, isRecord } from '../validation'

export const REMOTE_PROTOCOL = 1
export const REMOTE_VERSION_HEADER = 'x-mighty-remote-version'
export const MAX_BODY_BYTES = 512 * 1024
export const MAX_RESPONSE_BYTES = 2 * 1024 * 1024
export const DEFAULT_REMOTE_PORT = 43137
export const JOB_LEASE_MS = 20_000

export interface WireInfo { protocol: 1; hostId: string; hostName: string; workspaces: Workspace[]; runtime: RuntimeInfo }
export interface WireEvent { cursor: number; event: RunEvent }
export interface WirePoll { protocol: 1; cursor: number; lastCursor: number; gap: boolean; done: boolean; events: WireEvent[] }

export function cleanText(value: unknown, max: number, fallback = ''): string {
  return typeof value === 'string' ? value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '').slice(0, max) : fallback
}

export function validToken(value: unknown): value is string {
  return typeof value === 'string' && /^[a-zA-Z0-9_-]{43,128}$/.test(value)
}

export function safeWorkspace(value: unknown): Workspace | null {
  if (!isRecord(value) || !isIdentifier(value.id) || typeof value.path !== 'string' || value.path.length > 4096 || value.path.includes('\0') || (!posix.isAbsolute(value.path) && !win32.isAbsolute(value.path)) || value.remote !== undefined) return null
  return { id: value.id, name: cleanText(value.name, 120, 'Workspace'), path: value.path, createdAt: typeof value.createdAt === 'string' && Number.isFinite(Date.parse(value.createdAt)) ? value.createdAt : new Date().toISOString() }
}

function safeCatalog(value: unknown): ClaudeModelCatalog | undefined {
  if (!isRecord(value) || !Array.isArray(value.models) || !['cli', 'fallback', 'preview'].includes(String(value.source))) return undefined
  const models: ClaudeModelOption[] = []
  for (const row of value.models.slice(0, 128)) {
    if (!isRecord(row) || !isClaudeModel(row.value) || models.some((model) => model.value === row.value)) continue
    models.push({ value: row.value, displayName: cleanText(row.displayName, 160, row.value), description: cleanText(row.description, 2400),
      ...(isClaudeModel(row.resolvedModel) ? { resolvedModel: row.resolvedModel } : {}),
      ...(typeof row.supportsEffort === 'boolean' ? { supportsEffort: row.supportsEffort } : {}),
      ...(Array.isArray(row.supportedEffortLevels) ? { supportedEffortLevels: row.supportedEffortLevels.filter((level): level is typeof EFFORT_LEVELS[number] => EFFORT_LEVELS.includes(level as never)) } : {}),
    })
  }
  return { source: value.source as ClaudeModelCatalog['source'], models, detail: cleanText(value.detail, 2000) }
}

export function safeRuntime(value: unknown): RuntimeInfo {
  if (!isRecord(value) || !['darwin', 'win32', 'linux', 'browser'].includes(String(value.platform)) || typeof value.appVersion !== 'string') throw new Error('원격 실행 환경 응답이 올바르지 않습니다.')
  const runtime: RuntimeInfo = { platform: value.platform as RuntimeInfo['platform'], appVersion: cleanText(value.appVersion, 80), claudeAvailable: value.claudeAvailable === true }
  if (typeof value.claudeVersion === 'string') runtime.claudeVersion = cleanText(value.claudeVersion, 160)
  const modelCatalog = safeCatalog(value.modelCatalog)
  if (modelCatalog) runtime.modelCatalog = modelCatalog
  if (isRecord(value.mods) && ['preview', 'unavailable', 'unsupported', 'available'].includes(String(value.mods.status))) {
    runtime.mods = { status: value.mods.status as NonNullable<RuntimeInfo['mods']>['status'], minimumVersion: cleanText(value.mods.minimumVersion, 80), detail: cleanText(value.mods.detail, 2000) }
  }
  if (Array.isArray(value.providers)) {
    const providers: ProviderRuntime[] = []
    for (const provider of value.providers.slice(0, 3)) {
      if (!isRecord(provider) || !['claude', 'codex', 'gemini'].includes(String(provider.id)) || !isRecord(provider.capabilities)) continue
      const catalog = safeCatalog(provider.modelCatalog)
      if (!catalog) continue
      const caps = provider.capabilities
      providers.push({ id: provider.id as ProviderRuntime['id'], name: cleanText(provider.name, 80), available: provider.available === true, ...(typeof provider.version === 'string' ? { version: cleanText(provider.version, 160) } : {}), detail: cleanText(provider.detail, 2000), modelCatalog: catalog,
        capabilities: { effort: caps.effort === true, permissionModes: Array.isArray(caps.permissionModes) ? caps.permissionModes.filter((mode): mode is 'manual' | 'plan' | 'acceptEdits' => ['manual', 'plan', 'acceptEdits'].includes(mode)) : [], maxTurns: caps.maxTurns === true, maxBudgetUsd: caps.maxBudgetUsd === true, resume: caps.resume === true },
      })
    }
    runtime.providers = providers
  }
  return runtime
}

export function readWireInfo(value: unknown): WireInfo {
  if (!isRecord(value) || value.protocol !== REMOTE_PROTOCOL || !isIdentifier(value.hostId) || !Array.isArray(value.workspaces) || value.workspaces.length > 64) throw new Error('호환되는 MightyClaude 호스트 응답이 아닙니다.')
  const workspaces = value.workspaces.map(safeWorkspace)
  if (workspaces.some((workspace) => workspace === null) || new Set(workspaces.map((workspace) => workspace!.id)).size !== workspaces.length) throw new Error('원격 워크스페이스 목록이 올바르지 않습니다.')
  return { protocol: REMOTE_PROTOCOL, hostId: value.hostId, hostName: cleanText(value.hostName, 120, 'MightyClaude'), workspaces: workspaces as Workspace[], runtime: safeRuntime(value.runtime) }
}

export function safeRunEvent(value: unknown, sessionId: string): RunEvent | null {
  if (!isRecord(value) || value.sessionId !== sessionId) return null
  if (value.type === 'status' && ['idle', 'running', 'completed', 'error', 'stopped'].includes(String(value.status))) return { sessionId, type: 'status', status: value.status as 'idle' | 'running' | 'completed' | 'error' | 'stopped' }
  if (value.type === 'resume' && isIdentifier(value.resumeId)) return { sessionId, type: 'resume', resumeId: value.resumeId }
  if (value.type === 'log' && isRecord(value.entry) && isIdentifier(value.entry.id) && ['user', 'assistant', 'system', 'output', 'error'].includes(String(value.entry.kind)) && typeof value.entry.text === 'string') {
    return { sessionId, type: 'log', entry: { id: value.entry.id, kind: value.entry.kind as 'user' | 'assistant' | 'system' | 'output' | 'error', text: cleanText(value.entry.text, 32_768), timestamp: typeof value.entry.timestamp === 'string' && Number.isFinite(Date.parse(value.entry.timestamp)) ? value.entry.timestamp : new Date().toISOString(), ...(['claude', 'codex', 'gemini'].includes(String(value.entry.provider)) ? { provider: value.entry.provider as 'claude' | 'codex' | 'gemini' } : {}) } }
  }
  return null
}
