import { describe, expect, it } from 'vitest'
import { EMPTY_SNAPSHOT, type AppSnapshot, type RunSession, type Workspace } from '../shared/types'
import { DEFAULT_RUN_SETTINGS, effortLevelsForModel } from '../shared/claude-options'
import { applyRunEvent, removeSession, removeWorkspace, restoreSnapshot, selectWorkspace } from '../src/lib/state'

const createdAt = '2026-09-16T00:00:00.000Z'
const workspace = (id: string): Workspace => ({ id, name: id, path: `/projects/${id}`, createdAt })
const session = (id: string, workspaceId: string): RunSession => ({ id, workspaceId, title: id, kind: 'claude', model: 'default', status: 'idle', logs: [], createdAt })
function fixture(): AppSnapshot {
  return { ...EMPTY_SNAPSHOT, workspaces: [workspace('one'), workspace('two')], sessions: [session('a', 'one'), session('b', 'one'), session('c', 'two')], activeWorkspaceId: 'one', activeSessionId: 'b' }
}

describe('workspace and session lifecycle', () => {
  it('switches focus to a pane in the chosen workspace without losing other panes', () => {
    const next = selectWorkspace(fixture(), 'two')
    expect(next.activeSessionId).toBe('c')
    expect(next.sessions.map((entry) => entry.id)).toEqual(['a', 'b', 'c'])
  })

  it('removing a workspace also removes its panes and repairs focus', () => {
    const next = removeWorkspace(fixture(), 'one')
    expect(next.workspaces.map((entry) => entry.id)).toEqual(['two'])
    expect(next.sessions.map((entry) => entry.id)).toEqual(['c'])
    expect(next.activeWorkspaceId).toBe('two')
    expect(next.activeSessionId).toBe('c')
  })

  it('late process events cannot resurrect a closed pane', () => {
    const closed = removeSession(fixture(), 'b')
    const next = applyRunEvent(closed, { sessionId: 'b', type: 'status', status: 'completed' })
    expect(next).toEqual(closed)
    expect(next.activeSessionId).toBe('a')
  })

  it('events and resume IDs remain scoped to their session across workspace switches', () => {
    const switched = selectWorkspace(fixture(), 'two')
    const next = applyRunEvent(switched, { sessionId: 'a', type: 'resume', resumeId: 'cli-a' })
    expect(next.activeSessionId).toBe('c')
    expect(next.sessions.find((entry) => entry.id === 'a')?.resumeId).toBe('cli-a')
    expect(next.sessions.find((entry) => entry.id === 'c')?.resumeId).toBeUndefined()
  })
})

describe('restart recovery', () => {
  it('migrates older panes and preserves independent model and execution settings', () => {
    const saved = fixture()
    saved.sessions[1] = { ...saved.sessions[1]!, model: 'claude-opus-5', settings: { effort: 'high', permissionMode: 'plan', maxTurns: 12, maxBudgetUsd: 2.5 } }
    saved.sessions[2]!.model = 'claude-sonnet-4-5@20250929'
    const restored = restoreSnapshot(JSON.parse(JSON.stringify(saved)) as AppSnapshot)
    expect(restored.sessions[0]?.settings).toEqual(DEFAULT_RUN_SETTINGS)
    expect(restored.sessions[1]?.settings).toEqual(saved.sessions[1]?.settings)
    expect(restored.sessions[1]?.model).toBe('claude-opus-5')
    expect(restored.sessions[2]?.settings).toEqual(DEFAULT_RUN_SETTINGS)
    expect(restored.sessions[2]?.model).toBe('claude-sonnet-4-5@20250929')
    expect(restored.sessions[1]?.resumeId).toBe(saved.sessions[1]?.resumeId)
  })

  it('does not restore phantom running processes or dangling workspace references', () => {
    const saved = fixture()
    saved.sessions[0]!.status = 'running'
    saved.sessions.push(session('orphan', 'removed-workspace'))
    saved.activeSessionId = 'c'
    const restored = restoreSnapshot(saved)
    expect(restored.sessions.find((entry) => entry.id === 'a')?.status).toBe('stopped')
    expect(restored.sessions.some((entry) => entry.id === 'orphan')).toBe(false)
    expect(restored.sessions.find((entry) => entry.id === restored.activeSessionId)?.workspaceId).toBe(restored.activeWorkspaceId)
    expect(saved.sessions[0]!.status).toBe('running')
  })

  it('bounds accumulated output while preserving the newest messages', () => {
    let value = fixture()
    for (let index = 0; index < 340; index++) {
      value = applyRunEvent(value, { sessionId: 'a', type: 'log', entry: { id: `log-${index}`, kind: 'output', text: String(index), timestamp: createdAt } })
    }
    expect(value.sessions[0]!.logs).toHaveLength(300)
    expect(value.sessions[0]!.logs.at(-1)?.text).toBe('339')
    expect(value.sessions[1]!.logs).toHaveLength(0)
  })
})

describe('model capabilities', () => {
  it('uses CLI capability metadata over alias assumptions, including organization limits', () => {
    const catalog = { source: 'cli' as const, detail: 'fixture', models: [
      { value: 'opus', displayName: 'Company Opus', description: 'Pinned deployment', supportsEffort: true, supportedEffortLevels: ['low' as const, 'high' as const] },
      { value: 'sonnet', displayName: 'Company Sonnet', description: 'Legacy deployment', supportsEffort: false },
    ] }
    expect(effortLevelsForModel('opus', catalog)).toEqual(['low', 'high'])
    expect(effortLevelsForModel('sonnet', catalog)).toEqual([])
    expect(effortLevelsForModel('haiku')).toEqual([])
    expect(effortLevelsForModel('claude-opus-4-6')).not.toContain('xhigh')
    expect(effortLevelsForModel('unknown-company-model')).toEqual([])
  })
})
