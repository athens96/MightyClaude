import { randomBytes, randomUUID, timingSafeEqual } from 'node:crypto'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import { isIdentifier, isRecord } from './validation'

export const MAX_BRIDGE_BODY = 4096
const EVENTS = new Set(['session.start', 'turn.start', 'turn.complete', 'tool.call'])
const KEYS = new Set(['version', 'runId', 'claudeSessionId', 'event', 'turnId', 'tool', 'durationMs', 'reason'])
const REASONS = new Set(['answer', 'aborted', 'refusal', 'error'])

export interface ModEnvelope {
  version: 1
  runId: string
  claudeSessionId: string
  event: 'session.start' | 'turn.start' | 'turn.complete' | 'tool.call'
  turnId?: string
  tool?: string
  durationMs?: number
  reason?: 'answer' | 'aborted' | 'refusal' | 'error'
}

export function validateModEnvelope(value: unknown): ModEnvelope | null {
  if (!isRecord(value) || Object.keys(value).some((key) => !KEYS.has(key)) || value.version !== 1 || !isIdentifier(value.runId) || !isIdentifier(value.claudeSessionId) || !EVENTS.has(String(value.event))) return null
  if (value.turnId !== undefined && !isIdentifier(value.turnId)) return null
  if (value.tool !== undefined && (typeof value.tool !== 'string' || !/^[a-zA-Z][a-zA-Z0-9_.:/-]{0,127}$/.test(value.tool))) return null
  if (value.durationMs !== undefined && (typeof value.durationMs !== 'number' || !Number.isFinite(value.durationMs) || value.durationMs < 0 || value.durationMs > 86_400_000)) return null
  if (value.reason !== undefined && !REASONS.has(String(value.reason))) return null
  if (value.event === 'tool.call' && typeof value.tool !== 'string') return null
  if ((value.event === 'turn.start' || value.event === 'turn.complete') && !isIdentifier(value.turnId)) return null
  return value as unknown as ModEnvelope
}

interface BridgeRun {
  token: Buffer
  received: number
  windowStart: number
  windowCount: number
  onEvent: (event: ModEnvelope) => void
}

export interface ModConnection {
  runId: string
  url: string
  token: string
  received(): number
  release(): void
}

/** Only the spawned CLI gets a token. No CORS route or renderer-facing HTTP API. */
export class ModBridgeServer {
  private server: Server | null = null
  private starting: Promise<number> | null = null
  private readonly runs = new Map<string, BridgeRun>()

  private start(): Promise<number> {
    if (this.starting) return this.starting
    this.starting = new Promise((resolve, reject) => {
      const server = createServer((request, response) => this.handle(request, response))
      this.server = server
      server.requestTimeout = 2500
      server.headersTimeout = 2500
      server.keepAliveTimeout = 1000
      server.maxConnections = 16
      server.once('error', reject)
      server.listen(0, '127.0.0.1', () => {
        const address = server.address()
        if (!address || typeof address === 'string') return reject(new Error('Mods 로컬 연결을 열 수 없습니다.'))
        resolve(address.port)
      })
    })
    return this.starting
  }

  async register(onEvent: (event: ModEnvelope) => void): Promise<ModConnection> {
    const port = await this.start()
    const runId = randomUUID()
    const token = randomBytes(32).toString('hex')
    const run: BridgeRun = { token: Buffer.from(token), received: 0, windowStart: Date.now(), windowCount: 0, onEvent }
    this.runs.set(runId, run)
    return { runId, token, url: `http://127.0.0.1:${port}/events`, received: () => run.received, release: () => { this.runs.delete(runId) } }
  }

  private handle(request: IncomingMessage, response: ServerResponse): void {
    const end = (status: number): void => { response.writeHead(status, { 'Cache-Control': 'no-store', Connection: 'close' }); response.end() }
    if (request.socket.remoteAddress !== '127.0.0.1' || request.method !== 'POST' || request.url !== '/events' || request.headers.origin !== undefined || request.headers['sec-fetch-site'] !== undefined) return end(403)
    if (!/^application\/json(?:\s*;|$)/i.test(request.headers['content-type'] ?? '')) return end(415)
    const authorization = request.headers.authorization
    if (typeof authorization !== 'string' || !/^Bearer [a-f0-9]{64}$/.test(authorization)) return end(401)
    const supplied = Buffer.from(authorization.slice(7))
    const authenticated = [...this.runs.entries()].find(([, run]) => timingSafeEqual(supplied, run.token))
    if (!authenticated) return end(401)
    const [runId, run] = authenticated
    if (Date.now() - run.windowStart >= 1000) { run.windowStart = Date.now(); run.windowCount = 0 }
    if (++run.windowCount > 120 || run.received >= 4096) return end(429)
    const declaredLength = Number(request.headers['content-length'])
    if (Number.isFinite(declaredLength) && declaredLength > MAX_BRIDGE_BODY) return end(413)
    let body = ''
    let bytes = 0
    let rejected = false
    request.setEncoding('utf8')
    request.on('data', (chunk: string) => {
      bytes += Buffer.byteLength(chunk)
      if (bytes > MAX_BRIDGE_BODY) {
        if (!rejected) end(413)
        rejected = true
      } else if (!rejected) body += chunk
    })
    request.on('end', () => {
      if (rejected) return
      let value: unknown
      try { value = JSON.parse(body) } catch { return end(400) }
      const event = validateModEnvelope(value)
      if (!event || event.runId !== runId || this.runs.get(runId) !== run) return end(400)
      run.received++
      try { run.onEvent(event) } catch { return end(500) }
      end(204)
    })
    request.on('error', () => { if (!response.writableEnded) end(400) })
  }

  async close(): Promise<void> {
    this.runs.clear()
    if (!this.server) return
    const server = this.server
    await new Promise<void>((resolve) => {
      server.close(() => resolve())
      server.closeAllConnections()
    })
    this.server = null
    this.starting = null
  }
}
