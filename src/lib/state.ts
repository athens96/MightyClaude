import type {
  AppSnapshot,
  LayoutMode,
  RunEvent,
  RunSession,
  SessionKind,
  Workspace,
} from '../../shared/types'
import { EMPTY_SNAPSHOT } from '../../shared/types'
import { DEFAULT_RUN_SETTINGS, isClaudeModel } from '../../shared/claude-options'
import { normalizeProvider, normalizeProviderSettings } from '../../shared/provider-options'

export const MAX_LOG_ENTRIES = 300

export function makeId(prefix: string): string {
  return `${prefix}-${crypto.randomUUID()}`
}

export function createSession(workspaceId: string, kind: SessionKind, existing: RunSession[]): RunSession {
  const siblings = existing.filter((session) => session.workspaceId === workspaceId && session.kind === kind)
  const prefix = kind === 'claude' ? 'Claude' : '터미널'
  const usedTitles = new Set(siblings.map((session) => session.title))
  let number = siblings.length + 1
  while (usedTitles.has(`${prefix} ${number}`)) number += 1
  return {
    id: makeId('session'),
    workspaceId,
    title: `${prefix} ${number}`,
    kind,
    model: 'default',
    provider: 'claude',
    settings: { ...DEFAULT_RUN_SETTINGS },
    status: 'idle',
    logs: [],
    createdAt: new Date().toISOString(),
  }
}

export function addWorkspace(snapshot: AppSnapshot, workspace: Workspace): AppSnapshot {
  const existing = snapshot.workspaces.find((entry) => workspace.remote
    ? entry.remote?.connectionId === workspace.remote.connectionId && entry.remote.workspaceId === workspace.remote.workspaceId
    : !entry.remote && entry.path === workspace.path)
  if (existing) return selectWorkspace(snapshot, existing.id)
  const session = createSession(workspace.id, 'claude', snapshot.sessions)
  return {
    ...snapshot,
    workspaces: [...snapshot.workspaces, workspace],
    sessions: [...snapshot.sessions, session],
    activeWorkspaceId: workspace.id,
    activeSessionId: session.id,
  }
}

export function selectWorkspace(snapshot: AppSnapshot, workspaceId: string): AppSnapshot {
  if (!snapshot.workspaces.some((workspace) => workspace.id === workspaceId)) return snapshot
  const currentSession = snapshot.sessions.find((session) => session.id === snapshot.activeSessionId)
  return {
    ...snapshot,
    activeWorkspaceId: workspaceId,
    activeSessionId: currentSession?.workspaceId === workspaceId
      ? currentSession.id
      : snapshot.sessions.find((session) => session.workspaceId === workspaceId)?.id ?? null,
  }
}

export function removeSession(snapshot: AppSnapshot, sessionId: string): AppSnapshot {
  const sessions = snapshot.sessions.filter((session) => session.id !== sessionId)
  return {
    ...snapshot,
    sessions,
    activeSessionId: snapshot.activeSessionId === sessionId
      ? sessions.find((session) => session.workspaceId === snapshot.activeWorkspaceId)?.id ?? null
      : snapshot.activeSessionId,
  }
}

export function removeWorkspace(snapshot: AppSnapshot, workspaceId: string): AppSnapshot {
  const workspaces = snapshot.workspaces.filter((workspace) => workspace.id !== workspaceId)
  const sessions = snapshot.sessions.filter((session) => session.workspaceId !== workspaceId)
  const activeWorkspaceId = snapshot.activeWorkspaceId === workspaceId
    ? workspaces[0]?.id ?? null
    : snapshot.activeWorkspaceId
  const activeSession = sessions.find((session) => session.id === snapshot.activeSessionId)
  return {
    ...snapshot,
    workspaces,
    sessions,
    activeWorkspaceId,
    activeSessionId: activeSession?.workspaceId === activeWorkspaceId
      ? activeSession.id
      : sessions.find((session) => session.workspaceId === activeWorkspaceId)?.id ?? null,
  }
}

export function applyRunEvent(snapshot: AppSnapshot, event: RunEvent): AppSnapshot {
  return {
    ...snapshot,
    sessions: snapshot.sessions.map((session) => {
      if (session.id !== event.sessionId) return session
      if (event.type === 'status') return { ...session, status: event.status }
      if (event.type === 'resume') return { ...session, resumeId: event.resumeId }
      return { ...session, logs: [...session.logs, event.entry].slice(-MAX_LOG_ENTRIES) }
    }),
  }
}

/** Restore references together so a focused pane always belongs to the visible workspace. */
export function restoreSnapshot(value: AppSnapshot): AppSnapshot {
  if (!value || value.version !== 1 || !Array.isArray(value.workspaces) || !Array.isArray(value.sessions)) {
    return { ...EMPTY_SNAPSHOT, workspaces: [], sessions: [] }
  }
  const workspaces = value.workspaces.filter((workspace) => workspace && typeof workspace.id === 'string' && typeof workspace.path === 'string' && typeof workspace.name === 'string')
  const workspaceIds = new Set(workspaces.map((workspace) => workspace.id))
  const sessions = value.sessions.filter((session) => session && workspaceIds.has(session.workspaceId) && typeof session.id === 'string' && (session.kind === 'claude' || session.kind === 'shell')).map((session) => ({
    ...session,
    model: isClaudeModel(session.model) ? session.model : 'default',
    provider: normalizeProvider(session.provider),
    settings: normalizeProviderSettings(normalizeProvider(session.provider), session.settings),
    status: session.status === 'running' ? 'stopped' as const : session.status,
    logs: Array.isArray(session.logs) ? session.logs.slice(-MAX_LOG_ENTRIES) : [],
  }))
  const activeWorkspaceId = workspaceIds.has(value.activeWorkspaceId ?? '') ? value.activeWorkspaceId : workspaces[0]?.id ?? null
  const activeSession = sessions.find((session) => session.id === value.activeSessionId && session.workspaceId === activeWorkspaceId)
  const layouts: LayoutMode[] = ['grid', 'columns', 'focus']
  return {
    version: 1,
    workspaces,
    sessions,
    activeWorkspaceId,
    activeSessionId: activeSession?.id ?? sessions.find((session) => session.workspaceId === activeWorkspaceId)?.id ?? null,
    layout: layouts.includes(value.layout) ? value.layout : 'grid',
    theme: value.theme === 'light' ? 'light' : 'dark',
    sidebarWidth: Math.min(380, Math.max(208, Number(value.sidebarWidth) || 252)),
  }
}
