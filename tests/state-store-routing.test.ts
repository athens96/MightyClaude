import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { EMPTY_SNAPSHOT, type AppSnapshot, type Workspace } from '../shared/types'
import { StateStore } from '../electron/main/state-store'

const temporary: string[] = []
afterEach(async () => { await Promise.all(temporary.splice(0).map((path) => rm(path, { recursive: true, force: true }))) })

describe('local and remote workspace routing', () => {
  it('keeps same-path remote folders separate and cannot execute them as local folders', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mighty-workspace-routing-'))
    temporary.push(directory)
    const store = new StateStore(directory)
    const local: Workspace = { id: 'local-project', name: 'Local', path: directory, createdAt: new Date().toISOString() }
    await store.approveWorkspace(local)
    const remote = await store.approveRemoteWorkspace('host-1', { ...local, id: 'peer-project', name: 'Remote' }, 'Build computer')
    expect(remote.id).not.toBe(local.id)
    expect(remote.remote).toEqual({ connectionId: 'host-1', workspaceId: 'peer-project', hostName: 'Build computer' })
    expect((await store.approveRemoteWorkspace('host-1', { ...local, id: 'peer-project' }, 'Build computer')).id).toBe(remote.id)
    await expect(store.resolveWorkspace(remote.id)).rejects.toThrow('원격 워크스페이스')
    expect(await store.resolveWorkspace(local.id)).toEqual(local)
    expect((await store.getWorkspace(remote.id)).remote?.connectionId).toBe('host-1')
  })

  it('preserves Windows peer paths and provider choices across a macOS restart without trusting renderer rerouting', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mighty-remote-restore-'))
    temporary.push(directory)
    const store = new StateStore(directory)
    const remote = await store.approveRemoteWorkspace('host-2', { id: 'windows-project', name: 'Windows project', path: 'C:\\Projects\\MightyClaude', createdAt: new Date().toISOString() }, 'Windows')
    const state: AppSnapshot = {
      ...structuredClone(EMPTY_SNAPSHOT), workspaces: [remote], activeWorkspaceId: remote.id,
      sessions: [{ id: 'session-remote', workspaceId: remote.id, title: 'Codex', kind: 'claude', provider: 'codex', model: 'default', status: 'idle', logs: [], createdAt: remote.createdAt }], activeSessionId: 'session-remote',
    }
    await store.save(state)
    const restored = await new StateStore(directory).load()
    expect(restored.workspaces[0]).toEqual(remote)
    expect(restored.sessions[0]?.provider).toBe('codex')
    await expect(store.save({ ...state, workspaces: [{ ...remote, remote: undefined }] })).rejects.toThrow()
    await expect(store.save({ ...state, workspaces: [{ ...remote, remote: { ...remote.remote!, connectionId: 'other-host' } }] })).rejects.toThrow()
    await expect(store.save({ ...state, workspaces: [{ ...remote, path: directory }] })).rejects.toThrow()
  })
})
