import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { constants } from 'node:fs'
import { access } from 'node:fs/promises'
import { homedir, tmpdir } from 'node:os'
import { delimiter, join } from 'node:path'
import { EFFORT_LEVELS, isClaudeModel } from '../../shared/claude-options'
import { fallbackProviderCatalog, fallbackProviderRuntime, providerLabel } from '../../shared/provider-options'
import type { ClaudeModelCatalog, ClaudeModelOption, ProviderId, ProviderRuntime, RuntimeInfo } from '../../shared/types'
import { discoverModelCatalog } from './claude-models'
import { discoverClaude, runtimeEnvironment, toRuntimeInfo } from './claude-runtime'
import { isRecord } from './validation'
import { windowsLaunch } from './windows-launch'

export interface ProviderCommand {
  provider: Exclude<ProviderId, 'claude'>
  binary: string | null
  /** Windows npm installations are launched through node, never a .cmd shell. */
  argsPrefix: string[]
  version?: string
  modelCatalog?: ClaudeModelCatalog
}

const activeLookups = new Set<{ cancel(): void; done: Promise<void> }>()
const modelCache = new Map<string, { started: number; result: Promise<ClaudeModelCatalog> }>()
const commandCache = new Map<string, { started: number; result: Promise<ProviderCommand> }>()
let runtimeCache: { started: number; version: string; result: Promise<RuntimeInfo> } | undefined
let shuttingDown = false

export function providerEnvironment(): NodeJS.ProcessEnv {
  const inherited = runtimeEnvironment()
  const extra = [join(homedir(), '.npm-global', 'bin'), ...(process.platform === 'win32' ? [join(process.env.APPDATA ?? join(homedir(), 'AppData', 'Roaming'), 'npm')] : [])]
  return { ...inherited, PATH: [...new Set([...(inherited.PATH ?? '').split(delimiter), ...extra].filter(Boolean))].join(delimiter) }
}

async function closeProbeChild(child: ChildProcessWithoutNullStreams | undefined): Promise<void> {
  if (!child?.pid) return
  child.stdin.destroy()
  if (process.platform === 'win32') {
    // Closing the launcher releases its kill-on-close Windows Job Object.
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL')
  } else {
    try { process.kill(-child.pid, 'SIGTERM') } catch { return }
    await new Promise((resolve) => setTimeout(resolve, 100))
    try { process.kill(-child.pid, 'SIGKILL') } catch { /* Already exited. */ }
  }
  child.stdout.destroy(); child.stderr.destroy()
}

interface ProbeCallbacks<T> {
  ready(send: (message: unknown) => void): void
  data(chunk: string, send: (message: unknown) => void, resolve: (value: T) => void): void
  closed(resolve: (value: T) => void, code: number | null): void
}

/** All discovery subprocesses are bounded, tracked, and cancelled on app quit. */
async function probe<T>(command: ProviderCommand, args: string[], timeoutMs: number, callbacks: ProbeCallbacks<T>): Promise<T> {
  if (shuttingDown || !command.binary) throw new Error('Provider discovery unavailable')
  let child: ChildProcessWithoutNullStreams | undefined
  let timeout: ReturnType<typeof setTimeout> | undefined
  let finishLookup!: () => void
  let cancel = (): void => undefined
  const lookup = { cancel: () => cancel(), done: new Promise<void>((resolve) => { finishLookup = resolve }) }
  activeLookups.add(lookup)
  try {
    return await new Promise<T>((resolve, reject) => {
      cancel = () => reject(new Error('Provider lookup cancelled'))
      const env = providerEnvironment()
      const childArgs = [...command.argsPrefix, ...args]
      const launch = process.platform === 'win32' ? windowsLaunch(command.binary!, childArgs, env, undefined, tmpdir()) : { binary: command.binary!, args: childArgs, env }
      child = spawn(launch.binary, launch.args, { cwd: tmpdir(), env: launch.env, windowsHide: true, detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'] })
      const send = (message: unknown): void => { child?.stdin.write(`${JSON.stringify(message)}\n`) }
      let outputBytes = 0
      child.stdout.setEncoding('utf8')
      child.stderr.resume()
      child.stdin.on('error', reject)
      child.once('error', reject)
      child.once('spawn', () => { try { callbacks.ready(send) } catch (error) { reject(error) } })
      child.stdout.on('data', (chunk: string) => {
        outputBytes += Buffer.byteLength(chunk)
        if (outputBytes > 1024 * 1024) { reject(new Error('Provider metadata output limit exceeded')); return }
        try { callbacks.data(chunk, send, resolve) } catch (error) { reject(error) }
      })
      child.once('close', (code) => {
        try { callbacks.closed(resolve, code) } catch (error) { reject(error) }
        reject(new Error('Provider metadata process closed before responding'))
      })
      timeout = setTimeout(() => reject(new Error('Provider metadata timed out')), timeoutMs)
    })
  } finally {
    clearTimeout(timeout)
    await closeProbeChild(child).catch(() => undefined)
    activeLookups.delete(lookup)
    finishLookup()
  }
}

async function canExecute(binary: string): Promise<boolean> {
  try { await access(binary, process.platform === 'win32' ? constants.F_OK : constants.X_OK); return true } catch { return false }
}

async function discoverCommand(provider: ProviderCommand['provider']): Promise<ProviderCommand> {
  const absent: ProviderCommand = { provider, binary: null, argsPrefix: [] }
  if (shuttingDown) return absent
  const paths = [...new Set((providerEnvironment().PATH ?? '').split(delimiter).filter(Boolean))].slice(0, 64)
  const candidates: ProviderCommand[] = paths.map((directory) => ({ provider, binary: join(directory, `${provider}${process.platform === 'win32' ? '.exe' : ''}`), argsPrefix: [] }))
  if (process.platform === 'win32') {
    const node = (await Promise.all(paths.map(async (directory) => { const path = join(directory, 'node.exe'); return await canExecute(path) ? path : null }))).find(Boolean)
    if (node) {
      const entries = provider === 'codex' ? [['@openai', 'codex', 'bin', 'codex.js']] : [['@google', 'gemini-cli', 'bundle', 'gemini.js'], ['@google', 'gemini-cli', 'dist', 'index.js']]
      for (const directory of paths) for (const entry of entries) {
        const script = join(directory, 'node_modules', ...entry)
        if (await canExecute(script)) candidates.push({ provider, binary: node, argsPrefix: [script] })
      }
    }
  }
  for (const candidate of candidates) {
    if (shuttingDown) return absent
    if (!candidate.binary || !await canExecute(candidate.binary)) continue
    let output = ''
    try {
      const version = await probe<string>(candidate, ['--version'], 4000, {
        ready: () => undefined,
        data(chunk) { output += chunk; if (output.length > 16_384) throw new Error('Invalid version output') },
        closed(resolve, code) { const version = output.trim().slice(0, 160); if (code !== 0 || !version) throw new Error('Missing version'); resolve(version) },
      })
      return { ...candidate, version }
    } catch { /* Try another existing installation. Never install or upgrade. */ }
  }
  return absent
}

export function discoverProviderCommand(provider: ProviderCommand['provider']): Promise<ProviderCommand> {
  const existing = commandCache.get(provider)
  if (existing && Date.now() - existing.started < 60_000) return existing.result
  const result = discoverCommand(provider)
  commandCache.set(provider, { started: Date.now(), result })
  return result
}

function label(value: unknown, fallback: string, limit: number): string {
  return typeof value === 'string' && value.trim() ? value.replace(/[\u0000-\u001f\u007f]/g, ' ').slice(0, limit) : fallback
}

export function normalizeCodexModelCatalog(value: unknown): ClaudeModelCatalog {
  const fallback = fallbackProviderCatalog('codex')
  if (!Array.isArray(value)) return fallback
  const models: ClaudeModelOption[] = [{ ...fallback.models[0]! }]
  const seen = new Set(['default'])
  for (const row of value.slice(0, 128)) {
    if (!isRecord(row) || !isClaudeModel(row.model) || seen.has(row.model) || row.hidden === true) continue
    seen.add(row.model)
    const levels = Array.isArray(row.supportedReasoningEfforts)
      ? [...new Set(row.supportedReasoningEfforts.filter(isRecord).map((entry) => entry.reasoningEffort).filter((entry): entry is typeof EFFORT_LEVELS[number] => EFFORT_LEVELS.includes(entry as never)))]
      : undefined
    models.push({ value: row.model, displayName: label(row.displayName, row.model, 160), description: label(row.description, '', 2400), ...(levels ? { supportsEffort: levels.length > 0, supportedEffortLevels: levels } : {}) })
  }
  return models.length > 1 ? { source: 'cli', models, detail: '설치된 Codex의 model/list가 제공한 모델 목록입니다. 실제 사용 가능 여부에는 계정·제공자·조직 정책이 적용됩니다.' } : fallback
}

/** Stdio app-server initialization and model/list only: no thread or user turn. */
export async function readCodexModelCatalog(command: ProviderCommand, timeoutMs = 6000): Promise<ClaudeModelCatalog> {
  const fallback = fallbackProviderCatalog('codex')
  if (shuttingDown || !command.binary || command.provider !== 'codex') return fallback
  let buffer = ''
  let expectedId = 1
  let pages = 0
  const rows: unknown[] = []
  try {
    return await probe<ClaudeModelCatalog>(command, ['app-server', '--listen', 'stdio://'], timeoutMs, {
      ready(send) { send({ id: 1, method: 'initialize', params: { clientInfo: { name: 'mighty_claude', title: 'MightyClaude', version: '0.1.0' } } }) },
      data(chunk, send, resolve) {
        buffer += chunk
        let newline: number
        while ((newline = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, newline); buffer = buffer.slice(newline + 1)
          let message: unknown
          try { message = JSON.parse(line) } catch { continue }
          if (!isRecord(message) || message.id !== expectedId) continue
          if (message.error) throw new Error('Codex rejected model metadata request')
          if (expectedId === 1) {
            send({ method: 'initialized' })
            send({ id: ++expectedId, method: 'model/list', params: { limit: 64, includeHidden: false } })
          } else {
            if (!isRecord(message.result) || !Array.isArray(message.result.data)) throw new Error('Invalid Codex model list')
            rows.push(...message.result.data.slice(0, 128 - rows.length))
            pages++
            if (typeof message.result.nextCursor === 'string' && message.result.nextCursor && message.result.nextCursor.length < 4096 && pages < 4 && rows.length < 128) {
              send({ id: ++expectedId, method: 'model/list', params: { limit: 64, includeHidden: false, cursor: message.result.nextCursor } })
            } else resolve(normalizeCodexModelCatalog(rows))
          }
        }
      },
      closed: () => undefined,
    })
  } catch {
    return { ...fallback, detail: 'Codex 모델 목록을 가져오지 못해 공식 모델 이름 예시를 표시합니다. CLI 로그인·제공자 설정을 확인할 수 있습니다.' }
  }
}

export function discoverProviderModels(command: ProviderCommand): Promise<ClaudeModelCatalog> {
  if (command.modelCatalog) return Promise.resolve(command.modelCatalog)
  if (shuttingDown || !command.binary || command.provider !== 'codex') return Promise.resolve(fallbackProviderCatalog(command.provider))
  const key = `${command.binary}\0${command.argsPrefix.join('\0')}\0${command.version ?? ''}`
  const existing = modelCache.get(key)
  if (existing && Date.now() - existing.started < 60_000) return existing.result
  const result = readCodexModelCatalog(command)
  modelCache.set(key, { started: Date.now(), result })
  if (modelCache.size > 8) modelCache.delete(modelCache.keys().next().value!)
  return result
}

export function getProviderRuntimeInfo(appVersion: string): Promise<RuntimeInfo> {
  if (shuttingDown) return Promise.resolve({ ...toRuntimeInfo({ binary: null, supportsAdapter: false }, appVersion), providers: ['claude', 'codex', 'gemini'].map((id) => fallbackProviderRuntime(id as ProviderId)) })
  if (runtimeCache?.version === appVersion && Date.now() - runtimeCache.started < 60_000) return runtimeCache.result
  const result = (async () => {
    const [claude, codex, gemini] = await Promise.all([discoverClaude(), discoverProviderCommand('codex'), discoverProviderCommand('gemini')])
    const [claudeModels, codexModels] = await Promise.all([discoverModelCatalog(claude), discoverProviderModels(codex)])
    const legacy = toRuntimeInfo(claude, appVersion, claudeModels)
    const providers: ProviderRuntime[] = [
      { ...fallbackProviderRuntime('claude'), available: Boolean(claude.binary && claude.supportsAdapter), ...(claude.version ? { version: claude.version } : {}), modelCatalog: claudeModels, detail: legacy.mods!.detail },
      ...[codex, gemini].map((command): ProviderRuntime => ({
        ...fallbackProviderRuntime(command.provider), available: command.binary !== null,
        ...(command.version ? { version: command.version } : {}),
        modelCatalog: command.provider === 'codex' ? codexModels : fallbackProviderCatalog('gemini'),
        detail: command.binary ? `${providerLabel(command.provider)} CLI가 설치되어 있습니다. 실행에는 CLI의 로그인·제공자 설정이 적용됩니다.` : `${providerLabel(command.provider)} CLI 실행 파일을 찾을 수 없습니다.`,
      })),
    ]
    return { ...legacy, providers }
  })()
  runtimeCache = { started: Date.now(), version: appVersion, result }
  return result
}

export async function closeProviderRuntimeLookups(): Promise<void> {
  shuttingDown = true
  const pending = [...activeLookups]
  for (const lookup of pending) lookup.cancel()
  await Promise.all(pending.map((lookup) => lookup.done))
}
