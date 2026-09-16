import { query, type Query, type SDKUserMessage } from '@anthropic-ai/claude-agent-sdk'
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { tmpdir } from 'node:os'
import { EFFORT_LEVELS, fallbackModelCatalog, isClaudeModel } from '../../shared/claude-options'
import type { ClaudeModelCatalog, ClaudeModelOption } from '../../shared/types'
import { runtimeEnvironment, type ClaudeRuntime } from './claude-runtime'
import { isRecord } from './validation'
import { windowsLaunch } from './windows-launch'

type ModelQuery = Pick<Query, 'supportedModels' | 'close'>
export type ModelQueryFactory = (parameters: Parameters<typeof query>[0]) => ModelQuery
const activeLookups = new Set<{ cancel(): void; done: Promise<void> }>()
const cache = new Map<string, { started: number; result: Promise<ClaudeModelCatalog> }>()
let shuttingDown = false

function label(value: unknown, fallback: string, limit: number): string {
  return typeof value === 'string' && value.trim() ? value.replace(/[\u0000-\u001f\u007f]/g, ' ').slice(0, limit) : fallback
}

/** Keep provider IDs and descriptions while discarding fields outside our UI contract. */
export function normalizeModelCatalog(value: unknown): ClaudeModelCatalog {
  const fallback = fallbackModelCatalog()
  if (!Array.isArray(value)) return fallback
  const models: ClaudeModelOption[] = []
  const seen = new Set<string>()
  for (const row of value.slice(0, 128)) {
    if (!isRecord(row) || !isClaudeModel(row.value) || seen.has(row.value)) continue
    seen.add(row.value)
    if (row.value === 'default') {
      // Omitting --model follows settings or a resumed conversation, rather than
      // necessarily resolving to the account default reported at initialization.
      models.push({ ...fallback.models[0]! })
      continue
    }
    const effortLevels = Array.isArray(row.supportedEffortLevels)
      ? [...new Set(row.supportedEffortLevels.filter((level): level is typeof EFFORT_LEVELS[number] => EFFORT_LEVELS.includes(level as never)))]
      : undefined
    models.push({
      value: row.value,
      displayName: label(row.displayName, row.value, 160),
      description: label(row.description, '', 2400),
      ...(isClaudeModel(row.resolvedModel) ? { resolvedModel: row.resolvedModel } : {}),
      ...(typeof row.supportsEffort === 'boolean' ? { supportsEffort: row.supportsEffort } : {}),
      ...(effortLevels ? { supportedEffortLevels: effortLevels } : {}),
      ...(/haiku/i.test(row.value) ? { supportsEffort: false, supportedEffortLevels: [] } : {}),
    })
  }
  if (!models.length) return fallback
  if (!seen.has('default')) models.unshift({ ...fallback.models[0]! })
  return { source: 'cli', models, detail: '설치된 Claude Code가 초기화 시 제공한 모델 목록입니다. 실제 사용 가능 여부와 추론 한도에는 계정·제공자·조직 정책이 적용됩니다.' }
}

async function closeMetadataChild(child: ChildProcessWithoutNullStreams | undefined): Promise<void> {
  if (!child?.pid) return
  if (process.platform === 'win32') {
    // The Windows launcher owns a kill-on-close job containing its descendants.
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL')
  } else {
    try { process.kill(-child.pid, 'SIGTERM') } catch { return }
    await new Promise((resolve) => setTimeout(resolve, 100))
    try { process.kill(-child.pid, 'SIGKILL') } catch { /* Already closed. */ }
  }
}

/** Initialize the official SDK transport without yielding any user prompt. */
export async function readCliModelCatalog(binary: string, createQuery: ModelQueryFactory = query, timeoutMs = 6000): Promise<ClaudeModelCatalog> {
  if (shuttingDown) return fallbackModelCatalog()
  let finishInput!: () => void
  const inputGate = new Promise<void>((resolve) => { finishInput = resolve })
  async function* metadataOnlyInput(): AsyncGenerator<SDKUserMessage> { await inputGate }
  const controller = new AbortController()
  let session: ModelQuery | undefined
  let child: ChildProcessWithoutNullStreams | undefined
  let finishLookup!: () => void
  const lookup = { cancel: () => controller.abort(), done: new Promise<void>((resolve) => { finishLookup = resolve }) }
  activeLookups.add(lookup)
  let timeout: ReturnType<typeof setTimeout> | undefined
  try {
    session = createQuery({
      prompt: metadataOnlyInput(),
      options: {
        pathToClaudeCodeExecutable: binary,
        cwd: tmpdir(),
        env: {
          ...runtimeEnvironment(),
          CLAUDE_CODE_SAFE_MODE: '1', CLAUDE_CODE_DISABLE_TERMINAL_TITLE: '1',
          CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL: '1', CLAUDE_CODE_DISABLE_BACKGROUND_TASKS: '1',
          DISABLE_AUTOUPDATER: '1', DISABLE_TELEMETRY: '1', DISABLE_ERROR_REPORTING: '1', CLAUDE_CODE_SKIP_PROMPT_HISTORY: '1',
        },
        abortController: controller,
        persistSession: false, strictMcpConfig: true, mcpServers: {}, tools: [],
        permissionMode: 'dontAsk', permissionPrompts: 'none', extraArgs: { 'safe-mode': null },
        stderr: () => undefined,
        spawnClaudeCodeProcess(options) {
          const launch = process.platform === 'win32' ? windowsLaunch(options.command, options.args, options.env) : { binary: options.command, args: options.args, env: options.env }
          child = spawn(launch.binary, launch.args, { cwd: options.cwd, env: launch.env, windowsHide: true, detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'] })
          child.stderr.resume()
          return child
        },
      },
    })
    const models = await Promise.race([
      session.supportedModels(),
      new Promise<never>((_resolve, reject) => { timeout = setTimeout(() => reject(new Error('Model metadata initialization timed out')), timeoutMs) }),
      new Promise<never>((_resolve, reject) => { controller.signal.addEventListener('abort', () => reject(new Error('Model lookup cancelled')), { once: true }) }),
    ])
    return normalizeModelCatalog(models)
  } catch {
    return { ...fallbackModelCatalog(), detail: 'Claude Code 모델 목록을 가져오지 못해 공식 별칭을 표시합니다. 설치·로그인·제공자 설정을 확인한 뒤 새로고침할 수 있습니다.' }
  } finally {
    clearTimeout(timeout)
    try { session?.close() } catch { /* Cleanup still closes any child below. */ }
    finishInput()
    await closeMetadataChild(child).catch(() => undefined)
    activeLookups.delete(lookup)
    finishLookup()
  }
}

export async function discoverModelCatalog(runtime: ClaudeRuntime, force = false): Promise<ClaudeModelCatalog> {
  if (shuttingDown || !runtime.binary) return fallbackModelCatalog()
  const key = `${runtime.binary}\0${runtime.version ?? ''}`
  const existing = cache.get(key)
  if (!force && existing && Date.now() - existing.started < 60_000) return existing.result
  const result = readCliModelCatalog(runtime.binary)
  cache.set(key, { started: Date.now(), result })
  if (cache.size > 8) cache.delete(cache.keys().next().value!)
  return result
}

export async function closeModelCatalogLookups(): Promise<void> {
  shuttingDown = true
  const pending = [...activeLookups]
  for (const lookup of pending) lookup.cancel()
  await Promise.all(pending.map((lookup) => lookup.done))
}
