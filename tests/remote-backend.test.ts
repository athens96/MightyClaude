import { createCipheriv, createDecipheriv, randomBytes, randomUUID } from 'node:crypto'
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises'
import { createServer, request as httpRequest } from 'node:http'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_RUN_SETTINGS } from '../shared/claude-options'
import type { RemoteState, RunEvent, RuntimeInfo, StartRunRequest, Workspace } from '../shared/types'
import { RemoteController, type RemoteControllerOptions, type RemoteTestDependencies } from '../electron/main/remote/controller'
import { RemoteHost, type RunManagerLike } from '../electron/main/remote/host'
import { REMOTE_PROTOCOL, REMOTE_VERSION_HEADER } from '../electron/main/remote/protocol'
import { isTailscaleAddress, parseRemoteAddress, pinRemoteAddress, tailscaleInfoFromStatus } from '../electron/main/remote/tailscale'
import { remoteRequest } from '../electron/main/remote/transport'
import { RunManager } from '../electron/main/run-manager'

const controllers: RemoteController[] = []
const directories: string[] = []
afterEach(async () => {
  await Promise.all(controllers.splice(0).map((controller) => controller.dispose()))
  await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })))
})
const runtime: RuntimeInfo = { platform: process.platform as RuntimeInfo['platform'], appVersion: '0.1.0', claudeAvailable: false }
const testNetwork: RemoteTestDependencies = { allowLoopback: true, discoverTailscale: async () => ({ available: true, addresses: ['127.0.0.1'], deviceName: 'Fixture host', detail: 'Test only' }), pollIntervalMs: 15, requestTimeoutMs: 1000 }
const baseRequest: StartRunRequest = { sessionId: 'client-pane', workspaceId: 'shared', kind: 'shell', input: 'hold', model: 'default', settings: { ...DEFAULT_RUN_SETTINGS } }

class FakeRuns implements RunManagerLike {
  readonly requests: StartRunRequest[] = []
  readonly stopped: string[] = []
  readonly active = new Set<string>()
  disposed = false
  constructor(readonly emit: (event: RunEvent) => void) {}
  async start(value: StartRunRequest): Promise<void> {
    this.requests.push(value)
    this.active.add(value.sessionId)
    this.emit({ sessionId: value.sessionId, type: 'status', status: 'running' })
    if (value.input === 'pending') await new Promise(() => undefined)
    if (value.input === 'complete') {
      this.emit({ sessionId: value.sessionId, type: 'log', entry: { id: randomUUID(), kind: 'output', text: 'REMOTE_OK', timestamp: new Date().toISOString(), provider: 'codex' } })
      this.active.delete(value.sessionId)
      this.emit({ sessionId: value.sessionId, type: 'status', status: 'completed' })
    }
  }
  async stop(id: string): Promise<void> { this.stopped.push(id); this.active.delete(id); this.emit({ sessionId: id, type: 'status', status: 'stopped' }) }
  async dispose(): Promise<void> { this.disposed = true; await Promise.all([...this.active].map((id) => this.stop(id))) }
}

async function fixture(overrides: Partial<RemoteControllerOptions> = {}, network: RemoteTestDependencies = {}) {
  const directory = await mkdtemp(join(tmpdir(), 'mighty-remote-test-'))
  directories.push(directory)
  const workspaces: Workspace[] = ['shared', 'private'].map((id) => ({ id, name: id, path: directory, createdAt: new Date().toISOString() }))
  const managers: FakeRuns[] = []
  const events: RunEvent[] = []
  const options: RemoteControllerOptions = { directory, appVersion: '0.1.0', listWorkspaces: () => workspaces, resolveWorkspace: async (id) => {
    const workspace = workspaces.find((entry) => entry.id === id)
    if (!workspace) throw new Error('Unknown workspace')
    return workspace
  }, getRuntimeInfo: async () => runtime, createRunManager: (emit) => { const manager = new FakeRuns(emit); managers.push(manager); return manager }, emit: (event) => events.push(event), ...overrides }
  const controller = new RemoteController(options, { ...testNetwork, ...network })
  controllers.push(controller)
  return { controller, directory, workspaces, managers, events, options }
}

async function share(controller: RemoteController): Promise<RemoteState> { return controller.startSharing({ workspaceIds: ['shared'], port: 0 }) }
async function connect(client: RemoteController, shared: RemoteState) { return client.connectRemote({ name: 'Other computer', address: shared.host.address!, token: shared.host.token! }) }
function imported(connectionId: string, peer: Workspace): Workspace { return { ...peer, id: 'imported-workspace', remote: { connectionId, workspaceId: peer.id, hostName: 'Fixture host' } } }

async function rawRequest(address: string, token: string, path: string, options: { method?: string; headers?: Record<string, string>; body?: string } = {}): Promise<{ status: number; body: Record<string, unknown> }> {
  return new Promise((resolve, reject) => {
    const request = httpRequest(`${address}${path}`, { method: options.method ?? 'GET', agent: false, headers: { authorization: `Bearer ${token}`, [REMOTE_VERSION_HEADER]: String(REMOTE_PROTOCOL), ...options.headers } }, (response) => {
      let data = ''
      response.setEncoding('utf8')
      response.on('data', (chunk: string) => { data += chunk })
      response.once('end', () => resolve({ status: response.statusCode!, body: JSON.parse(data) as Record<string, unknown> }))
    })
    request.once('error', reject)
    request.end(options.body)
  })
}

describe('Tailscale address boundary', () => {
  it('accepts only device ranges and rejects paths, user info, browser URLs and unsafe DNS answers', async () => {
    expect(isTailscaleAddress('100.64.0.1')).toBe(true)
    expect(isTailscaleAddress('100.127.255.254')).toBe(true)
    expect(isTailscaleAddress('fd7a:115c:a1e0::1234')).toBe(true)
    for (const address of ['100.63.255.255', '100.128.0.1', '100.100.100.100', '192.168.1.2', '127.0.0.1', '8.8.8.8', '::1', '::ffff:100.64.1.1', 'fd00::1']) expect(isTailscaleAddress(address)).toBe(false)
    for (const address of ['https://100.64.1.2', 'http://user:secret@100.64.1.2', 'http://100.64.1.2/path', 'http://100.64.1.2/?key=x', 'http://100.64.1.2/#secret', 'file:///tmp/x']) expect(() => parseRemoteAddress(address)).toThrow()
    for (const address of ['http://127.0.0.1:9', 'http://192.168.1.2:9', 'http://8.8.8.8:9']) await expect(pinRemoteAddress(address)).rejects.toThrow('Tailscale')
    await expect(pinRemoteAddress('http://mighty.tail.ts.net:43127', false, async () => [{ address: '100.64.1.2', family: 4 }, { address: '8.8.8.8', family: 4 }])).rejects.toThrow('Tailscale')
    const info = tailscaleInfoFromStatus({ BackendState: 'Running', Self: { HostName: 'Host', TailscaleIPs: ['100.64.1.2', '192.168.0.1'] }, Peer: { secretId: { TailscaleIPs: ['100.64.1.3'], UserID: 'never expose' } } })
    expect(info.addresses).toEqual(['100.64.1.2'])
    expect(info.peerAddresses).toEqual(['100.64.1.3'])
    expect(JSON.stringify(info)).not.toContain('secretId')
    expect(tailscaleInfoFromStatus({ BackendState: 'NeedsLogin', TailscaleIPs: ['100.64.1.2'] }).available).toBe(false)
  })

  it('pins a MagicDNS result and does not resolve again when sending credentials', async () => {
    const host = await fixture()
    const shared = await share(host.controller)
    const lookup = vi.fn().mockResolvedValueOnce([{ address: '127.0.0.1', family: 4 }]).mockResolvedValue([{ address: '8.8.8.8', family: 4 }])
    const target = await pinRemoteAddress(`http://mighty.tail.ts.net:${shared.host.port}`, true, lookup)
    const result = await remoteRequest(target, shared.host.token!, 'GET', '/v1/info')
    expect(result.hostName).toBe('Fixture host')
    expect(lookup).toHaveBeenCalledOnce()
    expect(target.address).toBe('127.0.0.1')
  })

  it('keeps local work available without Tailscale and requires an active tailnet before real connections', async () => {
    const client = await fixture({}, { allowLoopback: false, discoverTailscale: async () => ({ available: false, addresses: [], detail: 'Not installed' }) })
    expect((await client.controller.getState()).tailscale.available).toBe(false)
    await expect(client.controller.connectRemote({ name: 'Host', address: 'http://100.64.1.2:43127', token: 'a'.repeat(43) })).rejects.toThrow('Tailscale')
    await expect(client.controller.startSharing({ workspaceIds: ['shared'] })).rejects.toThrow('Tailscale')
  })

  it('does not follow a redirect or send the key to its destination', async () => {
    let redirectedRequests = 0
    const destination = createServer((_req, res) => { redirectedRequests++; res.end('{}') })
    await new Promise<void>((resolve) => destination.listen(0, '127.0.0.1', resolve))
    const address = destination.address() as { port: number }
    const source = createServer((_req, res) => { res.writeHead(302, { location: `http://127.0.0.1:${address.port}/steal` }); res.end(JSON.stringify({ protocol: 1 })) })
    await new Promise<void>((resolve) => source.listen(0, '127.0.0.1', resolve))
    try {
      const target = await pinRemoteAddress(`http://127.0.0.1:${(source.address() as { port: number }).port}`, true)
      await expect(remoteRequest(target, 'x'.repeat(43), 'GET', '/v1/info')).rejects.toThrow('302')
      expect(redirectedRequests).toBe(0)
    } finally {
      await Promise.all([new Promise<void>((resolve) => source.close(() => resolve())), new Promise<void>((resolve) => destination.close(() => resolve()))])
    }
  })
})

describe('authenticated remote host', () => {
  it('requires a current key and protocol, blocks browser requests and exposes only selected workspaces', async () => {
    const host = await fixture()
    expect((await host.controller.getState()).host.enabled).toBe(false)
    const state = await share(host.controller)
    const address = state.host.address!
    const token = state.host.token!
    expect((await rawRequest(address, 'x'.repeat(43), '/v1/info')).status).toBe(401)
    expect((await rawRequest(address, token, '/v1/info', { headers: { origin: 'http://malicious.invalid' } })).status).toBe(403)
    expect((await rawRequest(address, token, '/v1/info', { headers: { [REMOTE_VERSION_HEADER]: '999' } })).status).toBe(426)
    const info = await rawRequest(address, token, '/v1/info')
    expect(info.status).toBe(200)
    expect((info.body.workspaces as Workspace[]).map((workspace) => workspace.id)).toEqual(['shared'])
    const forbidden = await rawRequest(address, token, '/v1/runs', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ request: { ...baseRequest, workspaceId: 'private', path: '/etc' } }) })
    expect(forbidden.status).toBe(403)
    expect(host.managers[0]?.requests).toHaveLength(0)
    const invalid = await rawRequest(address, token, '/v1/runs', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ request: { ...baseRequest, settings: { ...DEFAULT_RUN_SETTINGS, permissionMode: 'bypassPermissions' } } }) })
    expect(invalid.status).toBe(400)
    const oversized = await rawRequest(address, token, '/v1/runs', { method: 'POST', headers: { 'content-type': 'application/json', 'content-length': String(600 * 1024) }, body: 'x' })
    expect(oversized.status).toBe(413)
  })

  it('returns a new job ID before slow startup, bounds event retention and validates cursors', async () => {
    const host = await fixture()
    const state = await share(host.controller)
    const target = await pinRemoteAddress(state.host.address!, true)
    const started = await remoteRequest(target, state.host.token!, 'POST', '/v1/runs', { request: { ...baseRequest, input: 'pending' } })
    const jobId = started.jobId as string
    expect(jobId).not.toBe(baseRequest.sessionId)
    const manager = host.managers[0]!
    expect(manager.requests[0]?.sessionId).toBe(jobId)
    for (let index = 0; index < 300; index++) manager.emit({ sessionId: jobId, type: 'log', entry: { id: randomUUID(), kind: 'output', text: `line ${index}`, timestamp: new Date().toISOString() } })
    const poll = await remoteRequest(target, state.host.token!, 'GET', `/v1/runs/${jobId}/events?cursor=0`)
    expect(poll.gap).toBe(true)
    expect((poll.events as unknown[]).length).toBe(100)
    expect(poll.lastCursor).toBe(301)
    await expect(remoteRequest(target, state.host.token!, 'GET', `/v1/runs/${jobId}/events?cursor=9999`)).rejects.toThrow('출력 위치')
    await remoteRequest(target, state.host.token!, 'POST', `/v1/runs/${jobId}/stop`, {})
    expect(manager.stopped).toContain(jobId)
  })

  it('stops abandoned jobs even when the client never receives the start response', async () => {
    const host = await fixture({}, { leaseMs: 60, cleanupIntervalMs: 10 })
    const shared = await share(host.controller)
    await new Promise<void>((resolve, reject) => {
      const req = httpRequest(`${shared.host.address}/v1/runs`, { method: 'POST', headers: { authorization: `Bearer ${shared.host.token}`, [REMOTE_VERSION_HEADER]: '1', 'content-type': 'application/json' } }, (response) => { response.destroy(); resolve() })
      req.once('error', reject)
      req.end(JSON.stringify({ request: baseRequest }))
    })
    await vi.waitFor(() => expect(host.managers[0]?.stopped).toHaveLength(1), { timeout: 1500, interval: 20 })
    expect((await host.controller.getState()).host.activeRuns).toBe(0)
  })

  it('stops all remote jobs and rotates the key when sharing restarts', async () => {
    const host = await fixture()
    const first = await share(host.controller)
    const target = await pinRemoteAddress(first.host.address!, true)
    await remoteRequest(target, first.host.token!, 'POST', '/v1/runs', { request: baseRequest })
    const stopped = await host.controller.stopSharing()
    expect(stopped.host.enabled).toBe(false)
    expect(host.managers[0]?.disposed).toBe(true)
    expect(host.managers[0]?.stopped).toHaveLength(1)
    const second = await host.controller.startSharing({ workspaceIds: ['shared'], port: first.host.port })
    expect(second.host.token).not.toBe(first.host.token)
    await expect(remoteRequest(target, first.host.token!, 'GET', '/v1/info')).rejects.toThrow('연결 키')
  })

  it('bounds repeated requests and settles a stop issued while listen is pending', async () => {
    const host = await fixture()
    const shared = await share(host.controller)
    const responses = []
    for (let index = 0; index < 65; index++) responses.push(await rawRequest(shared.host.address!, 'x'.repeat(43), '/v1/info'))
    expect(responses.some((response) => response.status === 429)).toBe(true)
    const raced = new RemoteHost({ address: '127.0.0.1', port: 0, hostId: 'fixture-host', hostName: 'Fixture', workspaceIds: ['shared'], allowLoopback: true, resolveWorkspace: host.options.resolveWorkspace, listWorkspaces: host.options.listWorkspaces, getRuntimeInfo: host.options.getRuntimeInfo, createRunManager: host.options.createRunManager })
    const listening = raced.listen()
    const stopping = raced.stop()
    const outcomes = await Promise.allSettled([listening, stopping])
    expect(outcomes[1]?.status).toBe('fulfilled')
    expect(host.managers.at(-1)?.disposed).toBe(true)
  }, 2000)
})

describe('remote client controller', () => {
  it('maps job output to the originating pane and upserts rotated credentials without breaking imported IDs', async () => {
    const host = await fixture()
    const client = await fixture()
    const shared = await share(host.controller)
    const state = await connect(client.controller, shared)
    const id = state.connections[0]!.id
    const peer = await client.controller.getRemoteWorkspace(id, 'shared')
    expect(peer.remote).toBeUndefined()
    const workspace = imported(id, peer)
    await client.controller.startRun({ ...baseRequest, workspaceId: workspace.id, input: 'complete' }, workspace)
    await vi.waitFor(() => expect(client.events.some((event) => event.type === 'status' && event.status === 'completed')).toBe(true))
    expect(client.events.every((event) => event.sessionId === baseRequest.sessionId)).toBe(true)
    expect(client.events.find((event) => event.type === 'log')).toMatchObject({ type: 'log', entry: { text: 'REMOTE_OK', provider: 'codex' } })
    expect(client.controller.isRemoteRun(baseRequest.sessionId)).toBe(false)
    await host.controller.stopSharing()
    const rotated = await host.controller.startSharing({ workspaceIds: ['shared'], port: shared.host.port })
    expect((await client.controller.refreshRemote(id)).connections[0]?.status).toBe('disconnected')
    const reconnected = await connect(client.controller, rotated)
    expect(reconnected.connections).toHaveLength(1)
    expect(reconnected.connections[0]?.id).toBe(id)
    expect(reconnected.connections[0]?.status).toBe('connected')
  })

  it('supports immediate stop, disconnect, network failure and shutdown without resurrecting a pane', async () => {
    const host = await fixture()
    const client = await fixture()
    const shared = await share(host.controller)
    const state = await connect(client.controller, shared)
    const id = state.connections[0]!.id
    const workspace = imported(id, state.connections[0]!.workspaces[0]!)
    const request = { ...baseRequest, workspaceId: workspace.id }
    const early = client.controller.startRun(request, workspace)
    expect(client.controller.isRemoteRun(request.sessionId)).toBe(true)
    await client.controller.stopRun(request.sessionId)
    await early
    expect(client.controller.isRemoteRun(request.sessionId)).toBe(false)
    await client.controller.startRun(request, workspace)
    await vi.waitFor(() => expect(host.managers[0]?.active.size).toBe(1))
    await client.controller.disconnectRemote(id)
    expect(host.managers[0]?.active.size).toBe(0)
    await client.controller.refreshRemote(id)
    await client.controller.startRun(request, workspace)
    await host.controller.stopSharing()
    await vi.waitFor(() => expect(client.events.some((event) => event.type === 'log' && event.entry.kind === 'error' && event.entry.text.includes('원격 연결이 끊겼습니다'))).toBe(true), { timeout: 2000 })
    expect(client.controller.isRemoteRun(request.sessionId)).toBe(false)
    await client.controller.dispose()
    await expect(client.controller.startRun(request, workspace)).rejects.toThrow('종료 중')
  })

  it('stores only encrypted keys and restores named connections as disconnected', async () => {
    const key = randomBytes(32)
    const crypto = { encrypt: (text: string): Buffer => { const iv = randomBytes(12); const cipher = createCipheriv('aes-256-gcm', key, iv); const data = Buffer.concat([cipher.update(text, 'utf8'), cipher.final()]); return Buffer.concat([iv, cipher.getAuthTag(), data]) }, decrypt: (data: Buffer): string => { const cipher = createDecipheriv('aes-256-gcm', key, data.subarray(0, 12)); cipher.setAuthTag(data.subarray(12, 28)); return Buffer.concat([cipher.update(data.subarray(28)), cipher.final()]).toString('utf8') } }
    const host = await fixture()
    const client = await fixture(crypto)
    const shared = await share(host.controller)
    const connected = await connect(client.controller, shared)
    const file = join(client.directory, 'remote-connections.json')
    const disk = await readFile(file, 'utf8')
    expect(disk).not.toContain(shared.host.token!)
    expect(disk).toContain('encryptedToken')
    if (process.platform !== 'win32') expect((await stat(file)).mode & 0o077).toBe(0)
    const restarted = new RemoteController(client.options, testNetwork)
    controllers.push(restarted)
    const restored = await restarted.getState()
    expect(restored.connections[0]).toMatchObject({ id: connected.connections[0]?.id, status: 'disconnected', address: shared.host.address })
    expect(JSON.stringify(restored.connections)).not.toContain(shared.host.token!)
    expect((await restarted.refreshRemote(restored.connections[0]!.id)).connections[0]?.status).toBe('connected')
    const ephemeral = await fixture()
    await connect(ephemeral.controller, shared)
    await expect(readFile(join(ephemeral.directory, 'remote-connections.json'))).rejects.toMatchObject({ code: 'ENOENT' })
  })

  it('runs and stops a real harmless shell process through two controllers', async () => {
    const host = await fixture()
    const realHost = new RemoteController({ ...host.options, createRunManager: (emit) => new RunManager({ pluginDirectory: '.', resolveWorkspace: host.options.resolveWorkspace, emit }) }, testNetwork)
    controllers.push(realHost)
    const client = await fixture()
    const shared = await share(realHost)
    const connected = await connect(client.controller, shared)
    const workspace = imported(connected.connections[0]!.id, connected.connections[0]!.workspaces[0]!)
    await client.controller.startRun({ ...baseRequest, workspaceId: workspace.id, input: 'echo MIGHTY_REMOTE_OK' }, workspace)
    await vi.waitFor(() => expect(client.events.some((event) => event.type === 'status' && event.status !== 'running')).toBe(true), { timeout: 5000, interval: 20 })
    expect(client.events.some((event) => event.type === 'status' && event.status === 'completed'), JSON.stringify(client.events)).toBe(true)
    expect(client.events.some((event) => event.type === 'log' && event.entry.text.includes('MIGHTY_REMOTE_OK'))).toBe(true)
    const second = { ...baseRequest, sessionId: 'long-pane', workspaceId: workspace.id, input: `"${process.execPath}" -e "console.log('MIGHTY_REMOTE_RUNNING');setInterval(()=>{},1000)"` }
    await client.controller.startRun(second, workspace)
    await vi.waitFor(() => expect(client.events.some((event) => event.sessionId === 'long-pane' && event.type === 'log' && event.entry.text.includes('MIGHTY_REMOTE_RUNNING'))).toBe(true), { timeout: 5000, interval: 20 })
    await client.controller.stopRun('long-pane')
    expect(client.controller.isRemoteRun('long-pane')).toBe(false)
    expect((await realHost.getState()).host.activeRuns).toBe(0)
    expect(client.events.some((event) => event.sessionId === 'long-pane' && event.type === 'status' && event.status === 'stopped')).toBe(true)
  }, 15_000)
})
