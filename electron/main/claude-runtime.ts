import { execFile } from 'node:child_process'
import { access } from 'node:fs/promises'
import { constants } from 'node:fs'
import { homedir } from 'node:os'
import { delimiter, join } from 'node:path'
import { promisify } from 'node:util'
import { fallbackModelCatalog } from '../../shared/claude-options'
import type { ClaudeModelCatalog, RuntimeInfo, StartRunRequest } from '../../shared/types'
import { validateRunSettings } from './validation'

const execFileAsync = promisify(execFile)
/** Compatibility baseline for this adapter, not an upstream minimum release. */
export const MODS_API_BASELINE = '2.1.271'

export interface ClaudeRuntime {
  binary: string | null
  version?: string
  supportsAdapter: boolean
  modelCatalog?: ClaudeModelCatalog
}

export function supportsModsAdapter(version: string): boolean {
  const match = /(?:^|\s)(\d+)\.(\d+)\.(\d+)(?:\s|$|[-+])/.exec(version)
  if (!match) return false
  const found = match.slice(1, 4).map(Number)
  const baseline = MODS_API_BASELINE.split('.').map(Number)
  for (let index = 0; index < 3; index++) {
    if (found[index]! !== baseline[index]!) return found[index]! > baseline[index]!
  }
  return !/\d+\.\d+\.\d+-/.test(version)
}

export function runtimeEnvironment(): NodeJS.ProcessEnv {
  const extra = process.platform === 'win32'
    ? [join(homedir(), '.local', 'bin')]
    : [join(homedir(), '.local', 'bin'), '/opt/homebrew/bin', '/usr/local/bin', '/usr/bin', '/bin']
  const entries = [...(process.env.PATH ?? '').split(delimiter), ...extra].filter(Boolean)
  return { ...process.env, PATH: [...new Set(entries)].join(delimiter) }
}

export async function discoverClaude(): Promise<ClaudeRuntime> {
  const env = runtimeEnvironment()
  const names = process.platform === 'win32' ? ['claude.exe'] : ['claude']
  const candidates = [...new Set((env.PATH ?? '').split(delimiter).flatMap((directory) => names.map((name) => join(directory, name))))]
  for (const binary of candidates) {
    try {
      await access(binary, process.platform === 'win32' ? constants.F_OK : constants.X_OK)
      const { stdout } = await execFileAsync(binary, ['--version'], { env, timeout: 4000, maxBuffer: 16_384, windowsHide: true })
      const version = stdout.trim().slice(0, 160)
      return { binary, version, supportsAdapter: supportsModsAdapter(version) }
    } catch {
      // Try the next installed CLI; this never installs or upgrades a binary.
    }
  }
  return { binary: null, supportsAdapter: false }
}

export function toRuntimeInfo(runtime: ClaudeRuntime, appVersion: string, modelCatalog?: ClaudeModelCatalog): RuntimeInfo {
  return {
    platform: process.platform as RuntimeInfo['platform'], appVersion,
    claudeAvailable: runtime.binary !== null,
    ...(runtime.version ? { claudeVersion: runtime.version } : {}),
    modelCatalog: modelCatalog ?? runtime.modelCatalog ?? fallbackModelCatalog(),
    mods: {
      status: runtime.binary === null ? 'unavailable' : runtime.supportsAdapter ? 'available' : 'unsupported',
      minimumVersion: MODS_API_BASELINE,
      detail: runtime.binary === null
        ? 'Claude Code 실행 파일을 찾을 수 없습니다. Claude Code의 네이티브 설치가 필요합니다.'
        : runtime.supportsAdapter
          ? '공개된 2.1.271 타입을 기준으로 한 Mods 연결입니다. 실제 hook 연결은 실행 중 확인하며, 관리자 정책에 따라 제한될 수 있습니다.'
          : `이 앱의 Mods 연결은 2.1.271 공개 타입을 기준으로 합니다. 현재 ${runtime.version ?? '알 수 없는 버전'}에서는 Claude 실행을 지원하지 않습니다.`,
    },
  }
}

export function claudeArguments(request: StartRunRequest, pluginDirectory: string): string[] {
  const args = ['--print', '--verbose', '--output-format', 'stream-json', '--permission-prompts', 'none', '--plugin-dir', pluginDirectory]
  const settings = validateRunSettings(request.settings)
  args.push('--permission-mode', settings.permissionMode)
  if (request.model !== 'default') args.push('--model', request.model)
  if (settings.effort !== 'default') {
    args.push('--effort', settings.effort)
    // A settings.env effort can otherwise override --effort or the inherited
    // environment. Keep this run's explicit choice consistent in both layers.
    args.push('--settings', JSON.stringify({ env: { CLAUDE_CODE_EFFORT_LEVEL: settings.effort } }))
  }
  if (settings.maxTurns !== null) args.push('--max-turns', String(settings.maxTurns))
  if (settings.maxBudgetUsd !== null) args.push('--max-budget-usd', String(settings.maxBudgetUsd))
  if (request.resumeId) args.push('--resume', request.resumeId)
  return args
}

export function claudeEnvironment(request: StartRunRequest, inherited: NodeJS.ProcessEnv = runtimeEnvironment()): NodeJS.ProcessEnv {
  const settings = validateRunSettings(request.settings)
  return { ...inherited, ...(settings.effort !== 'default' ? { CLAUDE_CODE_EFFORT_LEVEL: settings.effort } : {}) }
}
