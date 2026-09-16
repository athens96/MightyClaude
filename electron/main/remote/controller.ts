import { randomUUID } from 'node:crypto'
import { lstat, mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { hostname } from 'node:os'
import { join } from 'node:path'
import type { ConnectRemoteRequest, RemoteConnectionInfo, RemoteState, RunEvent, RuntimeInfo, ShareRequest, StartRunRequest, Workspace } from '../../../shared/types'
import { isIdentifier, isRecord, validateStartRequest } from '../validation'
import { RemoteHost, type RunManagerLike } from './host'
import { DEFAULT_REMOTE_PORT, REMOTE_PROTOCOL, cleanText, readWireInfo, safeRunEvent, validToken } from './protocol'
import { discoverTailscale, isAllowedAddress, parseRemoteAddress, pinRemoteAddress, type AddressLookup, type PinnedAddress, type TailscaleInfo } from './tailscale'
import { remoteRequest } from './transport'

export interface RemoteControllerOptions {
  directory: string
  appVersion: string
  resolveWorkspace(id: string): Promise<Workspace>
  listWorkspaces(): Promise<Workspace[]> | Workspace[]
  getRuntimeInfo(): Promise<RuntimeInfo>
  createRunManager(emit: (event: RunEvent) => void): RunManagerLike
  emit(event: RunEvent): void
  encrypt?(plain: string): Buffer
  decrypt?(encrypted: Buffer): string
}

/** Constructor-only injection for loopback tests; never accepted from renderer IPC. */
export interface RemoteTestDependencies {
  allowLoopback?: boolean
  discoverTailscale?: () => Promise<TailscaleInfo>
  lookup?: AddressLookup
  pollIntervalMs?: number
  leaseMs?: number
  cleanupIntervalMs?: number
  requestTimeoutMs?: number
}

interface Connection {
  info: RemoteConnectionInfo
  token: string
  target?: PinnedAddress
  operations: Set<AbortController>
}

interface ClientRun {
  sessionId: string
  connection?: Connection
  jobId?: string
  cursor: number
  stopping: boolean
  finished: boolean
  abort: AbortController
  timer?: ReturnType<typeof setTimeout>
  stop?: Promise<void>
}

export class RemoteController {
  private readonly connections = new Map<string, Connection>()
  private readonly runs = new Map<string, ClientRun>()
  private readonly cleanups = new Set<Promise<void>>()
  private readonly hostId = randomUUID()
  private readonly ready: Promise<void>
  private host?: RemoteHost
  private sharedIds: string[] = []
  private disposed = false
  private shutdown?: Promise<void>
  private mutations: Promise<void> = Promise.resolve()
  private tailscale?: { at: number; promise: Promise<TailscaleInfo> }

  constructor(private readonly options: RemoteControllerOptions, private readonly testing: RemoteTestDependencies = {}) {
    this.ready = this.loadConnections()
  }

  private assertActive(): void { if (this.disposed) throw new Error('앱이 종료 중입니다.') }

  private mutate<T>(action: () => Promise<T>): Promise<T> {
    const result = this.mutations.then(async () => { await this.ready; this.assertActive(); return action() })
    this.mutations = result.then(() => undefined, () => undefined)
    return result
  }

  private getTailscale(force = false): Promise<TailscaleInfo> {
    if (!force && this.tailscale && Date.now() - this.tailscale.at < 5000) return this.tailscale.promise
    if (this.disposed) return Promise.resolve({ available: false, addresses: [], detail: '앱이 종료 중입니다.' })
    const promise = (this.testing.discoverTailscale ?? discoverTailscale)().catch(() => ({ available: false, addresses: [], detail: 'Tailscale 상태를 확인하지 못했습니다.' }))
    this.tailscale = { at: Date.now(), promise }
    return promise
  }

  async getState(): Promise<RemoteState> {
    await this.ready
    const state = await this.getTailscale()
    return {
      tailscale: { available: state.available, addresses: [...state.addresses], ...(state.deviceName ? { deviceName: state.deviceName } : {}), detail: state.detail },
      host: this.host ? { enabled: true, address: this.host.address, token: this.host.token, port: this.host.port, workspaceIds: [...this.sharedIds], activeRuns: this.host.activeRuns, detail: '선택한 로컬 워크스페이스를 Tailscale 안에서 공유 중입니다.' } : { enabled: false, workspaceIds: [], activeRuns: 0, detail: '공유가 꺼져 있습니다. 앱을 다시 시작하면 항상 꺼집니다.' },
      connections: [...this.connections.values()].map((connection) => structuredClone(connection.info)),
    }
  }

  startSharing(value: ShareRequest): Promise<RemoteState> {
    return this.mutate(async () => {
      if (!isRecord(value) || Object.keys(value).some((key) => !['workspaceIds', 'port'].includes(key)) || !Array.isArray(value.workspaceIds) || !value.workspaceIds.length || value.workspaceIds.length > 64 || value.workspaceIds.some((id) => !isIdentifier(id))) throw new Error('공유할 로컬 워크스페이스를 선택해 주세요.')
      const port = value.port ?? DEFAULT_REMOTE_PORT
      if (!Number.isInteger(port) || port < (this.testing.allowLoopback ? 0 : 1024) || port > 65535) throw new Error('공유 포트는 1024부터 65535까지 입력해 주세요.')
      const workspaceIds = [...new Set(value.workspaceIds)]
      for (const id of workspaceIds) {
        const workspace = await this.options.resolveWorkspace(id)
        if (workspace.remote || workspace.id !== id) throw new Error('이 컴퓨터의 로컬 워크스페이스만 공유할 수 있습니다.')
      }
      const tailscale = await this.getTailscale(true)
      this.assertActive()
      const address = tailscale.addresses.find((entry) => isAllowedAddress(entry, this.testing.allowLoopback) && !entry.includes(':')) ?? tailscale.addresses.find((entry) => isAllowedAddress(entry, this.testing.allowLoopback))
      if (!tailscale.available || !address) throw new Error('실행 중인 Tailscale의 장치 주소가 필요합니다.')
      await this.closeHost()
      this.assertActive()
      const host = new RemoteHost({ address, port, hostId: this.hostId, hostName: cleanText(tailscale.deviceName || hostname(), 120, 'MightyClaude'), workspaceIds,
        resolveWorkspace: this.options.resolveWorkspace, listWorkspaces: this.options.listWorkspaces, getRuntimeInfo: this.options.getRuntimeInfo, createRunManager: this.options.createRunManager,
        allowLoopback: this.testing.allowLoopback, leaseMs: this.testing.leaseMs, cleanupIntervalMs: this.testing.cleanupIntervalMs,
      })
      this.host = host
      try { await host.listen(); this.assertActive(); this.sharedIds = workspaceIds } catch (error) { await host.stop().catch(() => undefined); if (this.host === host) this.host = undefined; throw error }
      return this.getState()
    })
  }

  stopSharing(): Promise<RemoteState> { return this.mutate(async () => { await this.closeHost(); return this.getState() }) }

  private async closeHost(): Promise<void> {
    const host = this.host
    this.host = undefined
    this.sharedIds = []
    await host?.stop()
  }

  private async targetFor(address: string): Promise<PinnedAddress> {
    const tailscale = await this.getTailscale(true)
    this.assertActive()
    if (!this.testing.allowLoopback && !tailscale.available) throw new Error('원격 연결 전에 이 컴퓨터의 Tailscale을 실행하고 로그인해 주세요.')
    const target = await pinRemoteAddress(address, this.testing.allowLoopback, this.testing.lookup)
    this.assertActive()
    if (!this.testing.allowLoopback && tailscale.peerAddresses && ![...tailscale.addresses, ...tailscale.peerAddresses].includes(target.address)) throw new Error('이 주소는 현재 Tailscale 장치 목록에 없습니다. 같은 tailnet의 장치인지 확인해 주세요.')
    return target
  }

  connectRemote(value: ConnectRemoteRequest): Promise<RemoteState> {
    return this.mutate(async () => {
      if (!isRecord(value) || Object.keys(value).some((key) => !['name', 'address', 'token'].includes(key)) || typeof value.name !== 'string' || !value.name.trim() || value.name.length > 120 || !validToken(value.token)) throw new Error('원격 이름, Tailscale 주소, 연결 키를 확인해 주세요.')
      const address = parseRemoteAddress(value.address).origin
      const existing = [...this.connections.values()].find((connection) => connection.info.address === address)
      if (!existing && this.connections.size >= 16) throw new Error('원격 컴퓨터는 16개까지 연결할 수 있습니다.')
      const target = await this.targetFor(address)
      if (existing) await this.disconnectConnection(existing)
      const connection: Connection = existing ?? { info: { id: randomUUID(), name: cleanText(value.name.trim(), 120), address, status: 'disconnected', workspaces: [] }, token: value.token, operations: new Set() }
      connection.token = value.token
      connection.target = target
      connection.info.name = cleanText(value.name.trim(), 120)
      this.connections.set(connection.info.id, connection)
      try { await this.updateConnection(connection) } catch (error) {
        connection.info.status = 'disconnected'
        connection.info.detail = cleanText(error instanceof Error ? error.message : '원격 연결을 확인하지 못했습니다.', 300)
        if (!existing) this.connections.delete(connection.info.id)
        throw error
      }
      this.assertActive()
      if (!await this.saveConnections()) connection.info.detail = '연결되었습니다. 이 환경에서는 연결 키를 저장할 수 없어 앱을 닫으면 다시 입력해야 합니다.'
      return this.getState()
    })
  }

  refreshRemote(connectionId: string): Promise<RemoteState> {
    return this.mutate(async () => {
      const connection = this.connectionFor(connectionId)
      try {
        // Active runs keep their pinned address until stopped; never move an in-flight job.
        if (![...this.runs.values()].some((run) => run.connection === connection)) connection.target = await this.targetFor(connection.info.address)
        await this.updateConnection(connection)
      } catch (error) {
        await this.failConnection(connection, error instanceof Error ? error.message : '원격 연결이 끊겼습니다.')
      }
      return this.getState()
    })
  }

  disconnectRemote(connectionId: string): Promise<RemoteState> {
    return this.mutate(async () => { await this.disconnectConnection(this.connectionFor(connectionId)); return this.getState() })
  }

  private connectionFor(id: unknown): Connection {
    if (!isIdentifier(id)) throw new Error('원격 연결 ID가 올바르지 않습니다.')
    const connection = this.connections.get(id)
    if (!connection) throw new Error('원격 연결을 찾을 수 없습니다.')
    return connection
  }

  private async updateConnection(connection: Connection): Promise<void> {
    const result = readWireInfo(await this.rpc(connection, 'GET', '/v1/info'))
    this.assertActive()
    connection.info = { ...connection.info, status: 'connected', hostId: result.hostId, hostName: result.hostName, workspaces: result.workspaces, runtime: result.runtime, detail: '원격 MightyClaude에 연결되었습니다.' }
  }

  async getRemoteWorkspace(connectionId: string, workspaceId: string): Promise<Workspace> {
    await this.ready
    this.assertActive()
    const connection = this.connectionFor(connectionId)
    if (connection.info.status !== 'connected') throw new Error('원격 컴퓨터를 먼저 연결해 주세요.')
    const workspace = connection.info.workspaces.find((entry) => entry.id === workspaceId)
    if (!workspace) throw new Error('공유된 원격 워크스페이스를 찾을 수 없습니다.')
    return structuredClone(workspace)
  }

  isRemoteRun(sessionId: string): boolean { return this.runs.has(sessionId) }

  async startRun(value: StartRunRequest, workspace: Workspace): Promise<void> {
    this.assertActive()
    const request = validateStartRequest(value)
    if (!workspace.remote || workspace.id !== request.workspaceId) throw new Error('원격 워크스페이스가 올바르지 않습니다.')
    if (this.runs.has(request.sessionId)) throw new Error('이 실행 창은 이미 실행 중입니다.')
    if (this.runs.size >= 16) throw new Error('동시에 원격 실행할 수 있는 창은 16개입니다.')
    const run: ClientRun = { sessionId: request.sessionId, cursor: 0, stopping: false, finished: false, abort: new AbortController() }
    this.runs.set(run.sessionId, run)
    try {
      await this.ready
      if (run.finished || run.stopping || this.disposed) return this.finishRun(run, 'stopped')
      const connection = this.connectionFor(workspace.remote.connectionId)
      if (connection.info.status !== 'connected' || !connection.target) throw new Error('원격 컴퓨터가 연결되어 있지 않습니다. 연결을 새로고침해 주세요.')
      if (!connection.info.workspaces.some((entry) => entry.id === workspace.remote!.workspaceId)) throw new Error('이 워크스페이스는 현재 원격 공유 목록에 없습니다.')
      run.connection = connection
      const result = await this.rpc(connection, 'POST', '/v1/runs', { request: { ...request, workspaceId: workspace.remote.workspaceId } }, run.abort.signal)
      if (!isIdentifier(result.jobId)) throw new Error('원격 실행 ID가 올바르지 않습니다.')
      run.jobId = result.jobId
      if (run.finished || run.stopping || this.disposed) { await this.bestEffortStop(run); this.finishRun(run, 'stopped'); return }
      void this.poll(run)
    } catch (error) {
      if (run.finished) return
      if (run.stopping || this.disposed) { this.finishRun(run, 'stopped'); return }
      this.removeRun(run)
      throw error
    }
  }

  private async rpc(connection: Connection, method: 'GET' | 'POST', path: string, body?: unknown, parentSignal?: AbortSignal): Promise<Record<string, unknown>> {
    this.assertActive()
    if (!connection.target) throw new Error('원격 주소를 확인하지 못했습니다.')
    const controller = new AbortController()
    const abort = (): void => controller.abort()
    if (parentSignal?.aborted) controller.abort()
    else parentSignal?.addEventListener('abort', abort, { once: true })
    connection.operations.add(controller)
    try { return await remoteRequest(connection.target, connection.token, method, path, body, controller.signal, this.testing.requestTimeoutMs) } finally { parentSignal?.removeEventListener('abort', abort); connection.operations.delete(controller) }
  }

  private async poll(run: ClientRun): Promise<void> {
    if (run.finished || run.stopping || this.disposed || !run.connection || !run.jobId) return
    try {
      const result = await this.rpc(run.connection, 'GET', `/v1/runs/${run.jobId}/events?cursor=${run.cursor}`, undefined, run.abort.signal)
      if (run.finished || run.stopping || this.disposed) return
      if (result.protocol !== REMOTE_PROTOCOL || !Array.isArray(result.events) || result.events.length > 100 || !Number.isSafeInteger(result.cursor) || !Number.isSafeInteger(result.lastCursor) || (result.cursor as number) < run.cursor || (result.lastCursor as number) < (result.cursor as number) || typeof result.done !== 'boolean') throw new Error('원격 출력 응답이 올바르지 않습니다.')
      if (result.gap === true) this.log(run, 'system', '원격 출력 버퍼 한도를 넘어 앞부분 일부를 생략했습니다.')
      let cursor = run.cursor
      let terminal: 'completed' | 'error' | 'stopped' | undefined
      for (const item of result.events) {
        if (!isRecord(item) || !Number.isSafeInteger(item.cursor) || (item.cursor as number) <= cursor || (item.cursor as number) > (result.cursor as number)) throw new Error('원격 출력 순서가 올바르지 않습니다.')
        const event = safeRunEvent(item.event, run.jobId)
        if (!event) throw new Error('원격 실행 이벤트가 올바르지 않습니다.')
        cursor = item.cursor as number
        this.options.emit({ ...event, sessionId: run.sessionId })
        if (event.type === 'status' && ['completed', 'error', 'stopped'].includes(event.status)) terminal = event.status as 'completed' | 'error' | 'stopped'
      }
      if (cursor !== result.cursor) throw new Error('원격 출력 위치가 올바르지 않습니다.')
      run.cursor = cursor
      if (result.done && cursor >= (result.lastCursor as number)) {
        if (!terminal) throw new Error('원격 실행의 종료 상태를 확인하지 못했습니다.')
        this.removeRun(run)
        return
      }
      run.timer = setTimeout(() => { void this.poll(run) }, this.testing.pollIntervalMs ?? 500)
      run.timer.unref()
    } catch (error) {
      if (!run.finished && !run.stopping && !this.disposed) this.trackCleanup(this.failConnection(run.connection, error instanceof Error ? error.message : '원격 연결이 끊겼습니다.'))
    }
  }

  private log(run: ClientRun, kind: 'error' | 'system', text: string): void {
    this.options.emit({ sessionId: run.sessionId, type: 'log', entry: { id: randomUUID(), kind, text: cleanText(text, 2000), timestamp: new Date().toISOString() } })
  }

  private removeRun(run: ClientRun): void {
    run.finished = true
    clearTimeout(run.timer)
    run.abort.abort()
    if (this.runs.get(run.sessionId) === run) this.runs.delete(run.sessionId)
  }

  private finishRun(run: ClientRun, status: 'stopped' | 'error', message?: string): void {
    if (run.finished) return
    if (message) this.log(run, 'error', message)
    this.options.emit({ sessionId: run.sessionId, type: 'status', status })
    this.removeRun(run)
  }

  private async bestEffortStop(run: ClientRun): Promise<void> {
    if (!run.jobId || !run.connection?.target) return
    await remoteRequest(run.connection.target, run.connection.token, 'POST', `/v1/runs/${run.jobId}/stop`, {}, undefined, Math.min(this.testing.requestTimeoutMs ?? 3000, 3000)).catch(() => undefined)
  }

  stopRun(sessionId: string): Promise<void> {
    if (!isIdentifier(sessionId)) return Promise.reject(new Error('실행 창 ID가 올바르지 않습니다.'))
    const run = this.runs.get(sessionId)
    if (!run || run.finished) return Promise.resolve()
    if (run.stop) return run.stop
    run.stopping = true
    clearTimeout(run.timer)
    run.abort.abort()
    return run.stop = this.bestEffortStop(run).finally(() => this.finishRun(run, 'stopped'))
  }

  private async disconnectConnection(connection: Connection): Promise<void> {
    connection.info.status = 'disconnected'
    connection.info.detail = '연결을 해제했습니다. 새로고침하면 다시 연결합니다.'
    const pending = [...this.runs.values()].filter((run) => run.connection === connection)
    for (const run of pending) run.stopping = true
    for (const operation of connection.operations) operation.abort()
    await Promise.all(pending.map((run) => this.stopRun(run.sessionId)))
  }

  private async failConnection(connection: Connection, message: string): Promise<void> {
    connection.info.status = 'disconnected'
    connection.info.detail = cleanText(message, 300)
    const pending = [...this.runs.values()].filter((run) => run.connection === connection && !run.finished)
    for (const run of pending) this.finishRun(run, 'error', `원격 연결이 끊겼습니다. ${cleanText(message, 300)} 호스트는 상태 조회가 중단된 실행을 자동 정리합니다.`)
    for (const operation of connection.operations) operation.abort()
    await Promise.all(pending.map((run) => this.bestEffortStop(run)))
  }

  private trackCleanup(promise: Promise<void>): void {
    this.cleanups.add(promise)
    void promise.catch(() => undefined).finally(() => this.cleanups.delete(promise))
  }

  private get credentialsPath(): string { return join(this.options.directory, 'remote-connections.json') }

  private async loadConnections(): Promise<void> {
    if (!this.options.encrypt || !this.options.decrypt) return
    try {
      const info = await lstat(this.credentialsPath)
      if (!info.isFile() || info.isSymbolicLink() || info.size > 256 * 1024) return
      const value: unknown = JSON.parse(await readFile(this.credentialsPath, 'utf8'))
      if (!isRecord(value) || value.version !== 1 || !Array.isArray(value.connections)) return
      for (const row of value.connections.slice(0, 16)) {
        if (!isRecord(row) || !isIdentifier(row.id) || typeof row.name !== 'string' || typeof row.encryptedToken !== 'string' || row.encryptedToken.length > 16_384 || !/^[A-Za-z0-9+/]+={0,2}$/.test(row.encryptedToken)) continue
        try {
          const address = parseRemoteAddress(row.address).origin
          const token = this.options.decrypt(Buffer.from(row.encryptedToken, 'base64'))
          if (!validToken(token) || [...this.connections.values()].some((connection) => connection.info.address === address)) continue
          this.connections.set(row.id, { info: { id: row.id, name: cleanText(row.name, 120), address, status: 'disconnected', workspaces: [], detail: '저장된 연결입니다. 새로고침하여 연결하세요.' }, token, operations: new Set() })
        } catch { /* A key unavailable to this OS account is never treated as plaintext. */ }
      }
    } catch { /* Missing or damaged connection preferences do not block local work. */ }
  }

  private async saveConnections(): Promise<boolean> {
    if (!this.options.encrypt || !this.options.decrypt) return false
    let temporary: string | undefined
    try {
      const connections = [...this.connections.values()].map((connection) => ({ id: connection.info.id, name: connection.info.name, address: connection.info.address, encryptedToken: this.options.encrypt!(connection.token).toString('base64') }))
      await mkdir(this.options.directory, { recursive: true })
      temporary = `${this.credentialsPath}.${randomUUID()}.tmp`
      await writeFile(temporary, JSON.stringify({ version: 1, connections }), { mode: 0o600, flag: 'wx' })
      await rename(temporary, this.credentialsPath)
      return true
    } catch { return false } finally { if (temporary) await rm(temporary, { force: true }).catch(() => undefined) }
  }

  dispose(): Promise<void> {
    if (this.shutdown) return this.shutdown
    this.disposed = true
    for (const connection of this.connections.values()) for (const operation of connection.operations) operation.abort()
    const stopping = [...this.runs.keys()].map((id) => this.stopRun(id))
    return this.shutdown = (async () => {
      await Promise.all([...stopping, this.closeHost()])
      await this.mutations
      await this.closeHost()
      await Promise.all([...this.cleanups])
    })()
  }
}
