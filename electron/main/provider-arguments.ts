import type { StartRunRequest } from '../../shared/types'
import { validateStartRequest } from './validation'

/** Prompts are always supplied through stdin, never interpolated into argv. */
export function providerArguments(value: StartRunRequest): string[] {
  const request = validateStartRequest(value)
  const settings = request.settings!
  if (request.provider === 'codex') {
    const sandbox = settings.permissionMode === 'acceptEdits' ? 'workspace-write' : 'read-only'
    // Configuration overrides apply to both exec and exec resume. Never request
    // an interactive approval from a process whose stdin contains the prompt.
    const args = ['-c', 'approval_policy="never"', '-c', `sandbox_mode="${sandbox}"`, '-c', 'sandbox_workspace_write.network_access=false', 'exec']
    if (request.resumeId) args.push('resume', request.resumeId)
    args.push('--json', '--skip-git-repo-check')
    if (request.model !== 'default') args.push('--model', request.model)
    if (settings.effort !== 'default') args.push('-c', `model_reasoning_effort="${settings.effort}"`)
    args.push('-')
    return args
  }
  if (request.provider === 'gemini') {
    const mode = { manual: 'default', plan: 'plan', acceptEdits: 'auto_edit' }[settings.permissionMode]
    const args = ['--output-format', 'stream-json', '--approval-mode', mode]
    if (request.model !== 'default') args.push('--model', request.model)
    if (request.resumeId) args.push('--resume', request.resumeId)
    // A piped, non-TTY stdin selects Gemini's headless mode without exposing the
    // prompt in process listings through --prompt.
    return args
  }
  throw new Error('이 실행 인자 어댑터가 지원하지 않는 제공자입니다.')
}
