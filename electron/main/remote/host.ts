import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { RunEvent, RuntimeInfo, ShareRequest, StartRunRequest, Workspace } from '../../../shared/types'
import { isIdentifier, isRecord, validateStartRequest } from '../validation'
import { JOB_LEASE_MS, MAX_BODY_BYTES, REMOTE_PROTOCOL, REMOTE_VERSION_HEADER, cleanText, safeRunEvent, safeRuntime, safeWorkspace, type WireEvent } from './protocol'
import { isAllowedAddress } from './tailscale'

export interface RunManagerLike { start(value: StartRunRequest): Promise<void>; stop(sessionId: string): Promise<void>; dispose(): Promise<void> }
export interface RemoteHostOptions {
  address: string
  port: number
  hostId: string
  hostName: string
  workspaceIds: ShareRequest['workspaceIds']
  resolveWorkspace(id: string): Promise<Workspace>
  listWorkspaces(): Promise<Workspace[]> | Workspace[]
  getRuntimeInfo(): Promise<RuntimeInfo>
  createRunManager(emit: (event: RunEvent) => void): RunManagerLike
  allowLoopback?: boolean
  leaseMs?: number
  cleanupIntervalMs?: number
}

interface RemoteJob {
  id: string
  cursor: number
  events: WireEvent[]
  bytes: number
  lastPoll: number
  done: boolean
  stopping: boolean
  finishedAt?: number
  stop?: Promise<void>
}

class HttpFailure extends Error { constructor(readonly status: number, message: string) { super(message) } }

function send(res: ServerResponse, status: number, body: object): void {
  if (res.destroyed || res.writableEnded) return
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff', connection: 'close', [REMOTE_VERSION_HEADER]: String(REMOTE_PROTOCOL) })
  res.end(JSON.stringify({ protocol: REMOTE_PROTOCOL, ...body }))
}

async function readBody(req: IncomingMessage): Promise<unknown> {
  if (req.headers['content-type']?.split(';')[0]?.trim() !== 'application/json') throw new HttpFailure(415, 'JSON 요청이 필요합니다.')
  if (Number(req.headers['content-length']) > MAX_BODY_BYTES) throw new HttpFailure(413, '요청 크기 제한을 초과했습니다.')
  const buffers: Buffer[] = []
  let size = 0
  for await (const chunk of req) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
    size += buffer.length
    if (size > MAX_BODY_BYTES) throw new HttpFailure(413, '요청 크기 제한을 초과했습니다.')
    buffers.push(buffer)
  }
  try { return JSON.parse(Buffer.concat(buffers).toString('utf8')) } catch { throw new HttpFailure(400, 'JSON 요청이 올바르지 않습니다.') }
}

export class RemoteHost {
  readonly token = randomBytes(32).toString('base64url')
  private readonly tokenHash = createHash('sha256').update(this.token).digest()
  private readonly jobs = new Map<string, RemoteJob>()
  private readonly peers = new Map<string, { at: number; count: number }>()
  private readonly manager: RunManagerLike
  private readonly server: Server
  private readonly workspaceIds: Set<string>
  private cleanup?: ReturnType<typeof setInterval>
  private closing = false
  private shutdown?: Promise<void>
  private boundPort = 0
  private rateAt = Date.now()
  private rateCount = 0

  constructor(private readonly options: RemoteHostOptions) {
    this.workspaceIds = new Set(options.workspaceIds)
    this.manager = options.createRunManager((event) => this.onEvent(event))
    this.server = createServer({ maxHeaderSize: 8192 }, (req, res) => { void this.handle(req, res) })
    this.server.maxConnections = 64
    this.server.maxHeadersCount = 32
    this.server.headersTimeout = 5000
    this.server.requestTimeout = 10_000
    this.server.keepAliveTimeout = 1000
    this.server.on('clientError', (_error, socket) => { socket.destroy() })
  }

  get address(): string { return `http://${this.options.address.includes(':') ? `[${this.options.address}]` : this.options.address}:${this.boundPort}` }
  get port(): number { return this.boundPort }
  get activeRuns(): number { return [...this.jobs.values()].filter((job) => !job.done).length }

  async listen(): Promise<void> {
    if (this.closing) throw new Error('원격 공유가 종료 중입니다.')
    if (!isAllowedAddress(this.options.address, this.options.allowLoopback)) throw new Error('Tailscale 장치 주소에만 공유 서버를 열 수 있습니다.')
    await new Promise<void>((resolve, reject) => {
      const cleanup = (): void => { this.server.removeListener('listening', ready); this.server.removeListener('error', failed); this.server.removeListener('close', closed) }
      const failed = (error: Error): void => { cleanup(); reject(error) }
      const ready = (): void => { cleanup(); if (this.closing) reject(new Error('원격 공유가 종료 중입니다.')); else resolve() }
      const closed = (): void => { cleanup(); reject(new Error('원격 공유가 시작 전에 종료되었습니다.')) }
      this.server.once('error', failed)
      this.server.once('listening', ready)
      this.server.once('close', closed)
      this.server.listen({ host: this.options.address, port: this.options.port, ipv6Only: this.options.address.includes(':') })
    })
    const address = this.server.address()
    if (!address || typeof address === 'string') throw new Error('공유 주소를 확인하지 못했습니다.')
    this.boundPort = address.port
    this.cleanup = setInterval(() => this.expireJobs(), this.options.cleanupIntervalMs ?? 1000)
    this.cleanup.unref()
  }

  private authorize(req: IncomingMessage): void {
    if (this.closing) throw new HttpFailure(503, '원격 공유가 종료 중입니다.')
    if (req.headers.origin !== undefined) throw new HttpFailure(403, '브라우저의 직접 요청은 허용하지 않습니다.')
    const peer = req.socket.remoteAddress ?? ''
    if (!isAllowedAddress(peer, this.options.allowLoopback)) throw new HttpFailure(403, 'Tailscale 연결만 허용합니다.')
    const now = Date.now()
    if (now - this.rateAt > 1000) { this.rateAt = now; this.rateCount = 0 }
    if (++this.rateCount > 100) throw new HttpFailure(429, '요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.')
    let bucket = this.peers.get(peer)
    if (!bucket || now - bucket.at > 1000) {
      bucket = { at: now, count: 0 }
      this.peers.set(peer, bucket)
      if (this.peers.size > 64) this.peers.delete(this.peers.keys().next().value!)
    }
    if (++bucket.count > 60) throw new HttpFailure(429, '요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.')
    const authorization = req.headers.authorization
    if (typeof authorization !== 'string' || authorization.length > 256 || !authorization.startsWith('Bearer ') || !timingSafeEqual(createHash('sha256').update(authorization.slice(7)).digest(), this.tokenHash)) throw new HttpFailure(401, '연결 키가 올바르지 않습니다. 호스트에서 현재 키를 확인해 주세요.')
    if (req.headers[REMOTE_VERSION_HEADER] !== String(REMOTE_PROTOCOL)) throw new HttpFailure(426, 'MightyClaude 원격 프로토콜 버전이 다릅니다.')
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    try {
      this.authorize(req)
      const url = new URL(req.url ?? '/', 'http://mighty.invalid')
      if (req.method === 'GET' && url.pathname === '/v1/info' && !url.search) {
        const workspaces = (await this.options.listWorkspaces()).filter((workspace) => this.workspaceIds.has(workspace.id) && !workspace.remote).map(safeWorkspace).filter((workspace): workspace is Workspace => workspace !== null).slice(0, 64)
        const runtime = safeRuntime(await this.options.getRuntimeInfo())
        send(res, 200, { hostId: this.options.hostId, hostName: this.options.hostName, workspaces, runtime })
        return
      }
      if (req.method === 'POST' && url.pathname === '/v1/runs' && !url.search) {
        const body = await readBody(req)
        let request: StartRunRequest
        try { request = validateStartRequest(isRecord(body) ? body.request : null) } catch (error) { throw new HttpFailure(400, error instanceof Error ? error.message : '실행 요청이 올바르지 않습니다.') }
        if (!this.workspaceIds.has(request.workspaceId)) throw new HttpFailure(403, '이 워크스페이스는 원격 공유되지 않았습니다.')
        let workspace: Workspace
        try { workspace = await this.options.resolveWorkspace(request.workspaceId) } catch { throw new HttpFailure(403, '이 워크스페이스는 더 이상 공유할 수 없습니다.') }
        if (workspace.remote || workspace.id !== request.workspaceId) throw new HttpFailure(403, '로컬 워크스페이스만 원격 실행할 수 있습니다.')
        if (this.closing) throw new HttpFailure(503, '원격 공유가 종료 중입니다.')
        if (this.activeRuns >= 16) throw new HttpFailure(429, '원격 실행은 동시에 16개까지 가능합니다.')
        this.pruneJobs()
        const job: RemoteJob = { id: randomUUID(), cursor: 0, events: [], bytes: 0, lastPoll: Date.now(), done: false, stopping: false }
        this.jobs.set(job.id, job)
        // The host supplies the process/session ID; a remote pane cannot target a local one.
        void this.manager.start({ ...request, sessionId: job.id }).catch((error: unknown) => {
          if (job.done) return
          this.onEvent({ sessionId: job.id, type: 'log', entry: { id: randomUUID(), kind: 'error', text: cleanText(error instanceof Error ? error.message : '원격 실행을 시작하지 못했습니다.', 2000), timestamp: new Date().toISOString() } })
          this.onEvent({ sessionId: job.id, type: 'status', status: job.stopping ? 'stopped' : 'error' })
        })
        send(res, 202, { jobId: job.id })
        return
      }
      const match = /^\/v1\/runs\/([a-zA-Z0-9._:-]+)\/(events|stop)$/.exec(url.pathname)
      if (match && isIdentifier(match[1])) {
        const job = this.jobs.get(match[1])
        if (!job) throw new HttpFailure(404, '원격 실행 기록을 찾을 수 없습니다.')
        if (req.method === 'GET' && match[2] === 'events') {
          if ([...url.searchParams.keys()].some((key) => key !== 'cursor') || url.searchParams.getAll('cursor').length > 1 || !/^\d{1,12}$/.test(url.searchParams.get('cursor') ?? '')) throw new HttpFailure(400, '출력 위치가 올바르지 않습니다.')
          const cursor = Number(url.searchParams.get('cursor'))
          if (!Number.isSafeInteger(cursor) || cursor > job.cursor) throw new HttpFailure(400, '출력 위치가 올바르지 않습니다.')
          job.lastPoll = Date.now()
          const events = job.events.filter((entry) => entry.cursor > cursor).slice(0, 100)
          send(res, 200, { events, cursor: events.at(-1)?.cursor ?? cursor, lastCursor: job.cursor, gap: (job.events[0]?.cursor ?? 1) > cursor + 1, done: job.done })
          return
        }
        if (req.method === 'POST' && match[2] === 'stop' && !url.search) {
          await readBody(req)
          await this.stopJob(job)
          send(res, 200, { stopped: true })
          return
        }
      }
      throw new HttpFailure(404, '원격 API를 찾을 수 없습니다.')
    } catch (error) {
      send(res, error instanceof HttpFailure ? error.status : 500, { error: error instanceof HttpFailure ? error.message : '원격 요청을 처리하지 못했습니다.' })
    }
  }

  private onEvent(value: RunEvent): void {
    const job = this.jobs.get(value.sessionId)
    if (!job || job.done) return
    const event = safeRunEvent(value, job.id)
    if (!event) return
    const entry = { cursor: ++job.cursor, event }
    job.events.push(entry)
    job.bytes += Buffer.byteLength(JSON.stringify(entry))
    while (job.events.length > 256 || job.bytes > 512 * 1024) job.bytes -= Buffer.byteLength(JSON.stringify(job.events.shift()!))
    if (event.type === 'status' && ['completed', 'error', 'stopped'].includes(event.status)) { job.done = true; job.finishedAt = Date.now() }
  }

  private stopJob(job: RemoteJob): Promise<void> {
    if (job.done) return Promise.resolve()
    if (job.stop) return job.stop
    job.stopping = true
    return job.stop = this.manager.stop(job.id).catch(() => undefined).then(() => {
      if (!job.done) this.onEvent({ sessionId: job.id, type: 'status', status: 'stopped' })
    })
  }

  private expireJobs(): void {
    const now = Date.now()
    for (const job of this.jobs.values()) if (!job.done && now - job.lastPoll > (this.options.leaseMs ?? JOB_LEASE_MS)) void this.stopJob(job)
    this.pruneJobs()
  }

  private pruneJobs(): void {
    const completed = [...this.jobs.values()].filter((job) => job.done)
    for (const job of completed) if (Date.now() - (job.finishedAt ?? 0) > 120_000) this.jobs.delete(job.id)
    const remaining = [...this.jobs.values()].filter((job) => job.done)
    for (const job of remaining.slice(0, Math.max(0, remaining.length - 32))) this.jobs.delete(job.id)
  }

  stop(): Promise<void> {
    if (this.shutdown) return this.shutdown
    this.closing = true
    clearInterval(this.cleanup)
    this.server.closeAllConnections()
    return this.shutdown = Promise.all([
      new Promise<void>((resolve) => this.server.close(() => resolve())),
      this.manager.dispose(),
    ]).then(() => { this.jobs.clear() })
  }
}
