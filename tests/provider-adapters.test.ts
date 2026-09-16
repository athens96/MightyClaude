import { describe, expect, it } from 'vitest'
import { DEFAULT_RUN_SETTINGS } from '../shared/claude-options'
import { fallbackProviderCatalog, normalizeProviderSettings, providerEffortLevels } from '../shared/provider-options'
import { EMPTY_SNAPSHOT, type StartRunRequest } from '../shared/types'
import { providerArguments } from '../electron/main/provider-arguments'
import { CodexStreamParser, GeminiStreamParser } from '../electron/main/provider-stream'
import { normalizeCodexModelCatalog } from '../electron/main/provider-runtime'
import { normalizeSnapshot, validateProviderSelection, validateStartRequest } from '../electron/main/validation'

const request: StartRunRequest = { sessionId: 'pane-1', workspaceId: 'workspace-1', kind: 'claude', provider: 'codex', model: 'default', input: '--yolo $(not-a-command)\n한국어 입력' }

describe('provider settings and arguments', () => {
  it('keeps Codex prompts on stdin and applies restrictive sandbox settings to initial and resumed runs', () => {
    const initial = providerArguments(request)
    expect(initial).toEqual(['-c', 'approval_policy="never"', '-c', 'sandbox_mode="read-only"', '-c', 'sandbox_workspace_write.network_access=false', 'exec', '--json', '--skip-git-repo-check', '-'])
    const resumed = providerArguments({ ...request, model: 'company/model-v2', resumeId: 'thread-123', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'high', permissionMode: 'acceptEdits' } })
    expect(resumed).toContain('sandbox_mode="workspace-write"')
    expect(resumed).toContain('model_reasoning_effort="high"')
    expect(resumed).toContain('company/model-v2')
    expect(resumed.slice(resumed.indexOf('exec'), resumed.indexOf('exec') + 3)).toEqual(['exec', 'resume', 'thread-123'])
    expect(resumed.at(-1)).toBe('-')
    for (const args of [initial, resumed]) {
      expect(args).not.toContain(request.input)
      expect(args.join(' ')).not.toMatch(/dangerously|yolo|full-auto|danger-full-access/)
    }
  })

  it('maps Gemini permissions exactly without exposing prompt text or inventing effort flags', () => {
    for (const [permissionMode, expected] of [['manual', 'default'], ['plan', 'plan'], ['acceptEdits', 'auto_edit']] as const) {
      const args = providerArguments({ ...request, provider: 'gemini', model: 'gemini-3-pro-preview', resumeId: 'session-id', settings: { ...DEFAULT_RUN_SETTINGS, permissionMode } })
      expect(args).toEqual(['--output-format', 'stream-json', '--approval-mode', expected, '--model', 'gemini-3-pro-preview', '--resume', 'session-id'])
      expect(args).not.toContain(request.input)
    }
  })

  it('rejects unsupported settings and injection-shaped provider, model, resume, or permission values', () => {
    for (const patch of [{ provider: 'other' }, { provider: ['codex'] }, { model: '--yolo' }, { model: 'model\n--help' }, { resumeId: '-latest' }, { settings: { ...DEFAULT_RUN_SETTINGS, permissionMode: ['manual'] } }, { settings: { ...DEFAULT_RUN_SETTINGS, permissionMode: 'plan' } }, { settings: { ...DEFAULT_RUN_SETTINGS, maxTurns: 3 } }, { settings: { ...DEFAULT_RUN_SETTINGS, maxBudgetUsd: 1 } }]) {
      expect(() => validateStartRequest({ ...request, ...patch })).toThrow()
    }
    expect(() => validateStartRequest({ ...request, provider: 'gemini', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'low' } })).toThrow('추론 강도')
    expect(validateStartRequest({ ...request, provider: undefined }).provider).toBe('claude')
    expect(() => validateProviderSelection({ ...request, model: 'vertex/provider@20260101' }, fallbackProviderCatalog('codex'))).not.toThrow()
  })

  it('honors returned Codex effort limits and keeps default model semantics independent of catalog defaults', () => {
    const catalog = normalizeCodexModelCatalog([
      { model: 'company/v2', displayName: 'Company\nModel', description: 'Account metadata', supportedReasoningEfforts: [{ reasoningEffort: 'low' }, { reasoningEffort: 'high' }, { reasoningEffort: 'ultra' }] },
      { model: 'company/plain', supportedReasoningEfforts: [] },
      { model: '--malicious' }, { model: 'hidden', hidden: true }, { model: 'company/v2' },
    ])
    expect(catalog.source).toBe('cli')
    expect(catalog.models.map((row) => row.value)).toEqual(['default', 'company/v2', 'company/plain'])
    expect(catalog.models[0]?.displayName).toBe('Codex 설정 따름')
    expect(catalog.models[0]?.supportedEffortLevels).toBeUndefined()
    expect(providerEffortLevels('codex', 'company/v2', catalog)).toEqual(['low', 'high'])
    expect(providerEffortLevels('codex', 'company/plain', catalog)).toEqual([])
    expect(providerEffortLevels('codex', 'default', catalog)).toEqual([])
    expect(providerEffortLevels('codex', 'custom/unknown', catalog)).toEqual([])
    expect(() => validateProviderSelection({ ...request, model: 'company/v2', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'xhigh' } }, catalog)).toThrow()
    expect(() => validateProviderSelection({ ...request, model: 'company/v2', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'high' } }, catalog)).not.toThrow()
    expect(normalizeProviderSettings('gemini', { effort: 'high', permissionMode: 'plan', maxTurns: 5, maxBudgetUsd: 2 })).toEqual({ ...DEFAULT_RUN_SETTINGS, permissionMode: 'plan' })
  })

  it('restores providers and log attribution while allowing Windows paths only for valid remote references', () => {
    const snapshot = normalizeSnapshot({ ...EMPTY_SNAPSHOT,
      workspaces: [
        { id: 'remote', name: 'Remote', path: 'C:\\Projects\\app', remote: { connectionId: 'peer-1', workspaceId: 'host-project', hostName: 'Windows host' } },
        { id: 'invalid', name: 'Invalid', path: 'C:\\Projects\\app', remote: { connectionId: '--bad', workspaceId: 'host-project', hostName: 'Host' } },
      ],
      sessions: [{ id: 'run', workspaceId: 'remote', kind: 'claude', provider: 'gemini', model: 'custom/model', status: 'running', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'high' }, logs: [{ id: 'log', kind: 'assistant', provider: 'codex', text: 'Previous provider output' }] }],
    }, true)
    expect(snapshot.workspaces).toHaveLength(1)
    expect(snapshot.workspaces[0]?.remote?.hostName).toBe('Windows host')
    expect(snapshot.sessions[0]).toMatchObject({ provider: 'gemini', model: 'custom/model', status: 'stopped', settings: DEFAULT_RUN_SETTINGS, logs: [{ provider: 'codex' }] })
  })
})

describe('provider JSONL output', () => {
  it('handles fragmented Codex output, resumes and completed items without displaying reasoning or duplicate text', () => {
    const logs: [string, string][] = []; const resumes: string[] = []
    const parser = new CodexStreamParser((kind, text) => logs.push([kind, text]), (id) => resumes.push(id))
    const message = { type: 'item.completed', item: { id: 'item-2', type: 'agent_message', text: '작업 완료' } }
    const jsonl = [{ type: 'thread.started', thread_id: 'thread-123' }, { type: 'item.updated', item: { id: 'item-2', type: 'agent_message', text: 'partial' } }, { type: 'item.completed', item: { id: 'item-1', type: 'reasoning', text: 'private reasoning' } }, message, message, { type: 'unknown', secret: 'not displayed' }, { type: 'turn.completed' }].map((event) => JSON.stringify(event)).join('\n')
    parser.push(jsonl.slice(0, 23)); parser.push(jsonl.slice(23)); parser.flush()
    expect(logs).toEqual([['assistant', '작업 완료']])
    expect(resumes).toEqual(['thread-123'])
    expect(parser.failed).toBe(false)
    parser.push('\n{"type":"turn.failed","error":{"message":"Authentication required"}}\n')
    expect(parser.failed).toBe(true)
    expect(logs.at(-1)).toEqual(['error', 'Authentication required'])
  })

  it('groups Gemini assistant deltas and separates recoverable tool warnings from terminal errors', () => {
    const logs: [string, string][] = []; const resumes: string[] = []
    const parser = new GeminiStreamParser((kind, text) => logs.push([kind, text]), (id) => resumes.push(id))
    const events = [
      { type: 'init', session_id: 'gemini-session' }, { type: 'message', role: 'user', content: 'do not repeat prompt' },
      { type: 'message', role: 'assistant', content: '안녕', delta: true }, { type: 'message', role: 'assistant', content: '하세요', delta: true },
      { type: 'tool_use', tool_name: 'read_file', parameters: { secret: 'do not display parameters' } },
      { type: 'error', severity: 'warning', message: 'Retrying' }, { type: 'result', status: 'success' },
    ]
    parser.push(events.map((event) => JSON.stringify(event)).join('\n')); parser.flush()
    expect(resumes).toEqual(['gemini-session'])
    expect(logs).toEqual([['assistant', '안녕하세요'], ['system', '도구 실행 · read_file'], ['system', 'Retrying']])
    expect(parser.failed).toBe(false)
    parser.push('{"type":"result","status":"error","error":{"message":"Quota exhausted"}}\n')
    expect(parser.failed).toBe(true)
    expect(logs.at(-1)).toEqual(['error', 'Quota exhausted'])
  })

  it('bounds giant or malformed records and resumes parsing the next event', () => {
    for (const Parser of [CodexStreamParser, GeminiStreamParser]) {
      const logs: string[] = []
      const parser = new Parser((_kind, text) => logs.push(text), () => undefined)
      parser.push('x'.repeat(1024 * 1024 + 1)); parser.push('\nplain diagnostic\n{"type":"error","message":"Failed"}\n'); parser.flush()
      expect(logs).toEqual(['너무 긴 출력 한 줄을 생략했습니다.', 'plain diagnostic', 'Failed'])
      expect(parser.failed).toBe(true)
    }
  })
})
