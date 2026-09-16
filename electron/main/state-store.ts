import { randomUUID } from 'node:crypto'
import { mkdir, readFile, rename, stat, unlink, writeFile } from 'node:fs/promises'
import { isAbsolute, join, win32 } from 'node:path'
import { EMPTY_SNAPSHOT, type AppSnapshot, type Workspace } from '../../shared/types'
import { isIdentifier, isRecord, MAX_STATE_BYTES, normalizeSnapshot } from './validation'

export class StateStore {
  private snapshot: AppSnapshot = structuredClone(EMPTY_SNAPSHOT)
  private readonly approvedWorkspaces = new Map<string, Workspace>()
  private readonly ready: Promise<void>
  private writes: Promise<void> = Promise.resolve()

  constructor(private readonly directory: string) {
    this.ready = this.restore()
  }

  private async restore(): Promise<void> {
    try {
      const file = join(this.directory, 'workspace-state.json')
      if ((await stat(file)).size > MAX_STATE_BYTES) return
      this.snapshot = normalizeSnapshot(JSON.parse(await readFile(file, 'utf8')), true)
      for (const workspace of this.snapshot.workspaces) this.approvedWorkspaces.set(workspace.id, workspace)
    } catch {
      // A missing or damaged local state file starts with an empty workspace.
    }
  }

  async load(): Promise<AppSnapshot> {
    await this.ready
    return structuredClone(this.snapshot)
  }

  async approveWorkspace(workspace: Workspace): Promise<Workspace> {
    await this.ready
    if (workspace.remote) throw new Error('로컬 폴더 선택에 원격 경로를 사용할 수 없습니다.')
    const existing = [...this.approvedWorkspaces.values()].find((item) => !item.remote && item.path === workspace.path)
    if (existing) return structuredClone(existing)
    this.approvedWorkspaces.set(workspace.id, workspace)
    return structuredClone(workspace)
  }

  /** Called only with a workspace returned by an authenticated remote connection. */
  async approveRemoteWorkspace(connectionId: string, peerWorkspace: Workspace, hostName: string): Promise<Workspace> {
    await this.ready
    if (!isIdentifier(connectionId) || !isIdentifier(peerWorkspace.id) || peerWorkspace.remote ||
        typeof peerWorkspace.path !== 'string' || peerWorkspace.path.length > 4096 || peerWorkspace.path.includes('\0') ||
        (!isAbsolute(peerWorkspace.path) && !win32.isAbsolute(peerWorkspace.path))) {
      throw new Error('원격 워크스페이스 정보가 올바르지 않습니다.')
    }
    const existing = [...this.approvedWorkspaces.values()].find((item) => item.remote?.connectionId === connectionId && item.remote.workspaceId === peerWorkspace.id)
    const workspace: Workspace = {
      id: existing?.id ?? randomUUID(),
      name: typeof peerWorkspace.name === 'string' ? peerWorkspace.name.slice(0, 120) : 'Remote workspace',
      path: peerWorkspace.path,
      createdAt: existing?.createdAt ?? new Date().toISOString(),
      remote: { connectionId, workspaceId: peerWorkspace.id, hostName: hostName.slice(0, 120) },
    }
    this.approvedWorkspaces.set(workspace.id, workspace)
    return structuredClone(workspace)
  }

  async getWorkspace(id: string): Promise<Workspace> {
    await this.ready
    const workspace = this.approvedWorkspaces.get(id)
    if (!workspace) throw new Error('등록된 워크스페이스를 찾을 수 없습니다.')
    return structuredClone(workspace)
  }

  async resolveWorkspace(id: string): Promise<Workspace> {
    await this.ready
    const workspace = this.approvedWorkspaces.get(id)
    if (!workspace) throw new Error('폴더 선택으로 워크스페이스를 먼저 추가해 주세요.')
    if (workspace.remote) throw new Error('원격 워크스페이스는 해당 컴퓨터에 연결한 뒤 실행해야 합니다.')
    try {
      if (!(await stat(workspace.path)).isDirectory()) throw new Error('not-directory')
    } catch {
      throw new Error('워크스페이스 폴더를 찾을 수 없습니다. 폴더를 다시 선택해 주세요.')
    }
    return structuredClone(workspace)
  }

  async save(value: unknown): Promise<void> {
    await this.ready
    if (!isRecord(value) || value.version !== 1 || !Array.isArray(value.workspaces) || !Array.isArray(value.sessions) || value.workspaces.length > 64 || value.sessions.length > 128) {
      throw new Error('저장할 워크스페이스 상태가 올바르지 않습니다.')
    }
    for (const row of value.workspaces) {
      const approved = isRecord(row) && typeof row.id === 'string' ? this.approvedWorkspaces.get(row.id) : undefined
      const remoteMatches = isRecord(row) && (approved?.remote
        ? isRecord(row.remote) && row.remote.connectionId === approved.remote.connectionId && row.remote.workspaceId === approved.remote.workspaceId && row.remote.hostName === approved.remote.hostName
        : row.remote === undefined)
      if (!approved || !isRecord(row) || approved.path !== row.path || !remoteMatches) {
        throw new Error('워크스페이스 경로는 폴더 선택에서만 추가할 수 있습니다.')
      }
    }
    const snapshot = normalizeSnapshot(value)
    const serialized = JSON.stringify(snapshot)
    if (Buffer.byteLength(serialized) > MAX_STATE_BYTES) throw new Error('저장할 실행 기록이 너무 큽니다.')
    this.snapshot = snapshot
    this.writes = this.writes.catch(() => undefined).then(async () => {
      await mkdir(this.directory, { recursive: true })
      const temporary = join(this.directory, `workspace-state.${randomUUID()}.tmp`)
      try {
        await writeFile(temporary, serialized, { encoding: 'utf8', mode: 0o600 })
        await rename(temporary, join(this.directory, 'workspace-state.json'))
      } finally {
        await unlink(temporary).catch(() => undefined)
      }
    })
    await this.writes
  }

  async flush(): Promise<void> {
    await this.ready
    await this.writes
  }
}
