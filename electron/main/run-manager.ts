import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { stat } from 'node:fs/promises'
import { join } from 'node:path'
import { normalizeProvider, providerLabel } from '../../shared/provider-options'
import type { ClaudeModelCatalog, LogEntry, RunEvent, StartRunRequest, Workspace } from '../../shared/types'
import { claudeArguments, claudeEnvironment, discoverClaude, MODS_API_BASELINE, type ClaudeRuntime } from './claude-runtime'
import { discoverModelCatalog } from './claude-models'
import { ClaudeStreamParser } from './claude-stream'
import { ModBridgeServer, type ModConnection, type ModEnvelope } from './mod-bridge'
import { isIdentifier, validateProviderSelection, validateStartRequest } from './validation'
import { windowsLaunch } from './windows-launch'
import { providerArguments } from './provider-arguments'
import { discoverProviderCommand, discoverProviderModels, providerEnvironment, type ProviderCommand } from './provider-runtime'
import { CodexStreamParser, GeminiStreamParser, type RunOutputParser } from './provider-stream'

const OUTPUT_BUDGET = 2 * 1024 * 1024

interface ManagedRun {
  request: StartRunRequest
  child?: ChildProcessWithoutNullStreams
  connection?: ModConnection
  parser?: RunOutputParser
  started: boolean
  stopping: boolean
  finished: boolean
  outputBytes: number
  truncated: boolean
  done: Promise<void>
  resolveDone: () => void
  termination?: Promise<void>
}

interface RunManagerOptions {
  pluginDirectory: string
  resolveWorkspace: (id: string) => Promise<Workspace>
  emit: (event: RunEvent) => void
  discoverRuntime?: () => Promise<ClaudeRuntime>
  discoverModels?: (runtime: ClaudeRuntime) => Promise<ClaudeModelCatalog>
  discoverProvider?: (provider: ProviderCommand['provider']) => Promise<ProviderCommand>
  discoverProviderModels?: (command: ProviderCommand) => Promise<ClaudeModelCatalog>
}

function processGroupExists(pid: number): boolean {
  try { process.kill(-pid, 0); return true } catch { return false }
}

async function terminateTree(child: ChildProcessWithoutNullStreams): Promise<void> {
  if (!child.pid) return
  if (process.platform === 'win32') {
    if (child.exitCode !== null || child.signalCode !== null) return
    await new Promise<void>((resolve) => {
      const taskkill = spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' })
      const finish = (): void => { clearTimeout(timeout); child.kill(); resolve() }
      const timeout = setTimeout(() => { taskkill.kill(); finish() }, 1500)
      taskkill.once('error', finish)
      taskkill.once('close', finish)
    })
    return
  }
  if (!processGroupExists(child.pid)) return
  try { process.kill(-child.pid, 'SIGTERM') } catch { child.kill('SIGTERM') }
  await new Promise<void>((resolve) => setTimeout(resolve, 350))
  try { process.kill(-child.pid, 'SIGKILL') } catch { /* The process group already exited. */ }
}

export class RunManager {
  private readonly runs = new Map<string, ManagedRun>()
  private readonly bridge = new ModBridgeServer()
  private disposed = false

  constructor(private readonly options: RunManagerOptions) {}

  private emitLog(run: ManagedRun, kind: LogEntry['kind'], value: string): void {
    if ((kind === 'output' || kind === 'assistant') && run.truncated) return
    const text = value.replace(/\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))/g, '').replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '')
    if (!text) return
    for (let offset = 0; offset < text.length; offset += 16_384) {
      const part = text.slice(offset, offset + 16_384)
      if (kind === 'output' || kind === 'assistant') {
        run.outputBytes += Buffer.byteLength(part)
        if (run.outputBytes > OUTPUT_BUDGET) {
          run.truncated = true
          this.emitLog(run, 'system', '출력이 2 MB를 넘어서 이후 출력 표시를 생략합니다. 실행 중지는 계속 사용할 수 있습니다.')
          return
        }
      }
      this.options.emit({ sessionId: run.request.sessionId, type: 'log', entry: { id: randomUUID(), kind, text: part, timestamp: new Date().toISOString(), ...(run.request.kind === 'claude' ? { provider: normalizeProvider(run.request.provider) } : {}) } })
    }
  }

  private receiveMod(run: ManagedRun, event: ModEnvelope): void {
    if (run.finished) return
    if (run.connection?.received() === 1) this.emitLog(run, 'system', 'Claude Mods 연결됨 · Mighty bridge')
    this.options.emit({ sessionId: run.request.sessionId, type: 'resume', resumeId: event.claudeSessionId })
    if (event.event === 'turn.start') this.emitLog(run, 'system', 'Claude가 요청을 처리하고 있습니다.')
    if (event.event === 'tool.call') this.emitLog(run, 'system', `도구 실행 · ${event.tool}`)
    if (event.event === 'turn.complete' && event.reason !== 'answer') this.emitLog(run, 'system', `Claude 턴 종료 · ${event.reason ?? '완료'}`)
  }

  async start(value: unknown): Promise<void> {
    const request = validateStartRequest(value)
    if (this.disposed) throw new Error('앱이 종료 중입니다.')
    if (this.runs.has(request.sessionId)) throw new Error('이 실행 창은 이미 실행 중입니다.')
    if (this.runs.size >= 16) throw new Error('동시에 실행할 수 있는 창은 16개입니다.')
    let resolveDone!: () => void
    const run: ManagedRun = { request, started: false, stopping: false, finished: false, outputBytes: 0, truncated: false, done: new Promise((resolve) => { resolveDone = resolve }), resolveDone: () => resolveDone() }
    this.runs.set(request.sessionId, run)
    try {
      const workspace = await this.options.resolveWorkspace(request.workspaceId)
      if (run.stopping || this.disposed) return this.finishCancelled(run)
      if (workspace.remote) throw new Error('원격 워크스페이스는 연결된 호스트에서 실행해야 합니다.')
      let binary: string
      let args: string[]
      let env = providerEnvironment()
      const provider = normalizeProvider(request.provider)
      if (request.kind === 'claude' && provider === 'claude') {
        const runtime = await (this.options.discoverRuntime ?? discoverClaude)()
        if (run.stopping || this.disposed) return this.finishCancelled(run)
        if (!runtime.binary) throw new Error('Claude Code 실행 파일을 찾을 수 없습니다. 네이티브 Claude Code를 설치한 뒤 다시 시도해 주세요.')
        if (!runtime.supportsAdapter) throw new Error(`이 앱의 Mods 연결은 ${MODS_API_BASELINE} 공개 타입을 기준으로 합니다. 현재 ${runtime.version ?? '알 수 없는 버전'}에서는 Claude 실행을 지원하지 않습니다.`)
        const catalog = runtime.modelCatalog ?? await (this.options.discoverModels ?? discoverModelCatalog)(runtime)
        if (run.stopping || this.disposed) return this.finishCancelled(run)
        validateProviderSelection(request, catalog)
        env = claudeEnvironment(request, env)
        await stat(join(this.options.pluginDirectory, '.claude-plugin', 'plugin.json')).catch(() => { throw new Error('Mighty bridge Mod 파일을 찾을 수 없습니다. 앱 설치를 확인해 주세요.') })
        if (run.stopping || this.disposed) return this.finishCancelled(run)
        run.connection = await this.bridge.register((event) => this.receiveMod(run, event))
        Object.assign(env, {
          CLAUDE_CODE_ENABLE_FUNCTION_HOOKS: '1',
          MIGHTY_CLAUDE_BRIDGE_URL: run.connection.url,
          MIGHTY_CLAUDE_BRIDGE_TOKEN: run.connection.token,
          MIGHTY_CLAUDE_RUN_ID: run.connection.runId,
        })
        binary = runtime.binary
        args = claudeArguments(request, this.options.pluginDirectory)
        run.parser = new ClaudeStreamParser((kind, text) => this.emitLog(run, kind, text), (resumeId) => this.options.emit({ sessionId: request.sessionId, type: 'resume', resumeId }))
      } else if (request.kind === 'claude') {
        const command = await (this.options.discoverProvider ?? discoverProviderCommand)(provider as ProviderCommand['provider'])
        if (run.stopping || this.disposed) return this.finishCancelled(run)
        if (!command.binary) throw new Error(`${providerLabel(provider)} CLI 실행 파일을 찾을 수 없습니다. CLI를 설치한 뒤 다시 시도해 주세요.`)
        const catalog = command.modelCatalog ?? await (this.options.discoverProviderModels ?? discoverProviderModels)(command)
        if (run.stopping || this.disposed) return this.finishCancelled(run)
        validateProviderSelection(request, catalog)
        binary = command.binary
        args = [...command.argsPrefix, ...providerArguments(request)]
        const Parser = provider === 'codex' ? CodexStreamParser : GeminiStreamParser
        run.parser = new Parser((kind, text) => this.emitLog(run, kind, text), (resumeId) => this.options.emit({ sessionId: request.sessionId, type: 'resume', resumeId }))
      } else if (process.platform === 'win32') {
        binary = process.env.ComSpec || 'C:\\Windows\\System32\\cmd.exe'
        args = ['/d', '/s', '/c', request.input]
      } else {
        binary = process.env.SHELL || '/bin/sh'
        args = ['-l', '-c', request.input]
      }
      if (run.stopping || this.disposed) return this.finishCancelled(run)
      if (process.platform === 'win32') {
        const launch = windowsLaunch(binary, args, env, request.kind === 'shell' ? request.input : undefined, workspace.path)
        binary = launch.binary
        args = launch.args
        env = launch.env
      }
      const child = spawn(binary, args, { cwd: workspace.path, env, windowsHide: true, detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'] })
      run.child = child
      child.stdout.setEncoding('utf8')
      child.stderr.setEncoding('utf8')
      child.stdout.on('data', (chunk: string) => {
        if (run.parser) run.parser.push(chunk)
        else this.emitLog(run, 'output', chunk)
      })
      child.stderr.on('data', (chunk: string) => this.emitLog(run, 'output', chunk))
      child.stdin.on('error', (error: NodeJS.ErrnoException) => {
        if (run.started && !run.stopping && error.code !== 'EPIPE') this.emitLog(run, 'error', error.message)
      })
      child.once('close', (code, signal) => { void this.finish(run, code, signal) })
      child.once('exit', () => {
        // `close` can be delayed by inherited pipes held by a background child.
        if (process.platform !== 'win32') void this.terminate(run)
      })
      child.on('error', (error) => { if (run.started && !run.stopping) this.emitLog(run, 'error', error.message) })
      await new Promise<void>((resolve, reject) => {
        child.once('spawn', () => { run.started = true; resolve() })
        child.once('error', reject)
      })
      this.options.emit({ sessionId: request.sessionId, type: 'status', status: 'running' })
      this.emitLog(run, 'system', request.kind === 'claude' ? provider === 'claude' ? 'Claude Code 실행 · Mods 연결을 확인하고 있습니다.' : `${providerLabel(provider)} CLI 실행 · 비대화형 모드` : '명령 실행 · 비대화형 셸')
      child.stdin.end(request.kind === 'claude' ? request.input : undefined)
    } catch (error) {
      run.connection?.release()
      if (run.finished) return
      if (this.runs.get(request.sessionId) === run) this.runs.delete(request.sessionId)
      run.resolveDone()
      if (run.stopping) {
        this.options.emit({ sessionId: request.sessionId, type: 'status', status: 'stopped' })
        return
      }
      throw error
    }
  }

  private finishCancelled(run: ManagedRun): void {
    run.connection?.release()
    if (run.finished) return
    run.finished = true
    if (this.runs.get(run.request.sessionId) === run) this.runs.delete(run.request.sessionId)
    this.options.emit({ sessionId: run.request.sessionId, type: 'status', status: 'stopped' })
    run.resolveDone()
  }

  private async finish(run: ManagedRun, code: number | null, signal: NodeJS.Signals | null): Promise<void> {
    if (run.finished) return
    run.finished = true
    run.parser?.flush()
    // A shell may exit before a background child. Close its Unix process group.
    if (run.child && process.platform !== 'win32') await this.terminate(run)
    if (run.started || run.stopping) {
      if (run.connection?.received() === 0 && !run.stopping) this.emitLog(run, 'system', 'Mods 연결 이벤트를 받지 못했습니다. 표시된 응답은 CLI 출력이며, function hooks 활성화 또는 관리자 정책을 확인해 주세요.')
      if (code !== 0 && !run.stopping) this.emitLog(run, 'system', `프로세스 종료 · ${signal ? `신호 ${signal}` : `코드 ${code ?? '알 수 없음'}`}`)
      const status = run.stopping ? 'stopped' : code === 0 && !run.parser?.failed ? 'completed' : 'error'
      this.options.emit({ sessionId: run.request.sessionId, type: 'status', status })
    }
    run.connection?.release()
    if (this.runs.get(run.request.sessionId) === run) this.runs.delete(run.request.sessionId)
    run.resolveDone()
  }

  async stop(sessionId: unknown): Promise<void> {
    if (!isIdentifier(sessionId)) throw new Error('실행 창 ID가 올바르지 않습니다.')
    const run = this.runs.get(sessionId)
    if (!run) return
    run.stopping = true
    if (run.child) await this.terminate(run)
    let timeout: ReturnType<typeof setTimeout> | undefined
    await Promise.race([
      run.done,
      new Promise<void>((resolve) => { timeout = setTimeout(() => {
        this.emitLog(run, 'error', '프로세스 종료가 지연되어 실행 창을 강제 종료했습니다.')
        run.child?.kill('SIGKILL')
        run.child?.stdin.destroy()
        run.child?.stdout.destroy()
        run.child?.stderr.destroy()
        void this.finish(run, null, 'SIGKILL').finally(resolve)
      }, 4000) }),
    ])
    clearTimeout(timeout)
  }

  private terminate(run: ManagedRun): Promise<void> {
    if (!run.child) return Promise.resolve()
    return run.termination ??= terminateTree(run.child)
  }

  async dispose(): Promise<void> {
    this.disposed = true
    await Promise.all([...this.runs.keys()].map((sessionId) => this.stop(sessionId)))
    await this.bridge.close()
  }
}
