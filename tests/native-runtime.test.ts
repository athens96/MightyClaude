import { chmod, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { spawn } from 'node:child_process'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_RUN_SETTINGS, fallbackModelCatalog } from '../shared/claude-options'
import { EMPTY_SNAPSHOT, type AppSnapshot, type ClaudeModelCatalog, type RunEvent, type StartRunRequest, type Workspace } from '../shared/types'
import { claudeArguments, claudeEnvironment, supportsModsAdapter } from '../electron/main/claude-runtime'
import { ClaudeStreamParser } from '../electron/main/claude-stream'
import { RunManager } from '../electron/main/run-manager'
import { StateStore } from '../electron/main/state-store'
import { normalizeSnapshot, validateModelSelection, validateStartRequest } from '../electron/main/validation'
import { quoteWindowsArgument, windowsLaunch, windowsShellArguments } from '../electron/main/windows-launch'

const temporary: string[] = []
afterEach(async () => { await Promise.all(temporary.splice(0).map((path) => rm(path, { recursive: true, force: true }))) })
const request: StartRunRequest = { sessionId: 'session-1', workspaceId: 'workspace-1', kind: 'claude', input: '--dangerously-skip-permissions $(do-not-run)\nHello', model: 'sonnet', resumeId: 'prior-session' }

describe('Claude runtime boundary', () => {
  it('uses the published API baseline without guessing support from an unknown version', () => {
    expect(supportsModsAdapter('2.1.263 (Claude Code)')).toBe(false)
    expect(supportsModsAdapter('2.1.271 (Claude Code)')).toBe(true)
    expect(supportsModsAdapter('2.1.272 (Claude Code)')).toBe(true)
    expect(supportsModsAdapter('2.1.271-preview')).toBe(false)
    expect(supportsModsAdapter('unknown')).toBe(false)
  })

  it('keeps the prompt off argv, uses the bundled plugin, and never bypasses permission checks', () => {
    const args = claudeArguments(validateStartRequest(request), '/Applications/Mighty Claude/mods/mighty-bridge')
    expect(args).toEqual(['--print', '--verbose', '--output-format', 'stream-json', '--permission-prompts', 'none', '--plugin-dir', '/Applications/Mighty Claude/mods/mighty-bridge', '--permission-mode', 'manual', '--model', 'sonnet', '--resume', 'prior-session'])
    expect(args).not.toContain(request.input)
    expect(args.join(' ')).not.toContain('dangerously')
    expect(() => validateStartRequest({ ...request, model: '--help' })).toThrow()
    expect(() => validateStartRequest({ ...request, resumeId: '--resume injected' })).toThrow()
    expect(() => validateStartRequest({ ...request, input: 'x'.repeat(100_001) })).toThrow()
  })

  it('keeps each pane’s explicit effort consistent across argv, inline settings, and child env', () => {
    const custom = validateStartRequest({ ...request, settings: { effort: 'xhigh', permissionMode: 'plan', maxTurns: 7, maxBudgetUsd: 1.25 } })
    const args = claudeArguments(custom, '/bundled/plugin')
    expect(args.slice(args.indexOf('--effort'), args.indexOf('--effort') + 2)).toEqual(['--effort', 'xhigh'])
    expect(JSON.parse(args[args.indexOf('--settings') + 1]!)).toEqual({ env: { CLAUDE_CODE_EFFORT_LEVEL: 'xhigh' } })
    expect(args.slice(args.indexOf('--permission-mode'), args.indexOf('--permission-mode') + 2)).toEqual(['--permission-mode', 'plan'])
    expect(args.slice(args.indexOf('--max-turns'), args.indexOf('--max-turns') + 2)).toEqual(['--max-turns', '7'])
    expect(args.slice(args.indexOf('--max-budget-usd'), args.indexOf('--max-budget-usd') + 2)).toEqual(['--max-budget-usd', '1.25'])
    const inherited = { PATH: '/fixture/bin', CLAUDE_CODE_EFFORT_LEVEL: 'low' }
    expect(claudeEnvironment(custom, inherited)).toEqual({ PATH: '/fixture/bin', CLAUDE_CODE_EFFORT_LEVEL: 'xhigh' })
    expect(inherited.CLAUDE_CODE_EFFORT_LEVEL).toBe('low')
    expect(claudeEnvironment(request, inherited).CLAUDE_CODE_EFFORT_LEVEL).toBe('low')
    const defaults = claudeArguments({ ...request, model: 'default' }, '/bundled/plugin')
    expect(defaults).not.toContain('--effort')
    expect(defaults).not.toContain('--model')
    expect(defaults).not.toContain('--settings')
    expect(defaults).not.toContain('--max-turns')
    expect(defaults).not.toContain('--max-budget-usd')
  })

  it('rejects invalid settings and known unsupported Haiku effort instead of silently dropping them', () => {
    for (const patch of [
      { effort: 'ultracode' }, { effort: '--help' }, { permissionMode: 'bypassPermissions' }, { permissionMode: ['plan'] },
      { maxTurns: 0 }, { maxTurns: 1001 }, { maxTurns: 1.5 }, { maxTurns: NaN },
      { maxBudgetUsd: 0 }, { maxBudgetUsd: -1 }, { maxBudgetUsd: Infinity }, { maxBudgetUsd: 10_001 },
      { extra: true },
    ]) expect(() => validateStartRequest({ ...request, settings: { ...DEFAULT_RUN_SETTINGS, ...patch } })).toThrow()
    expect(() => validateStartRequest({ ...request, settings: { effort: 'high' } })).toThrow()
    expect(() => validateStartRequest({ ...request, settings: null })).toThrow()
    expect(() => validateStartRequest({ ...request, model: 'claude-haiku-4-5', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'high' } })).toThrow('Haiku')
    expect(validateStartRequest({ ...request, settings: { ...DEFAULT_RUN_SETTINGS, permissionMode: 'acceptEdits', maxTurns: 1000, maxBudgetUsd: 10_000 } }).settings).toMatchObject({ permissionMode: 'acceptEdits', maxTurns: 1000, maxBudgetUsd: 10_000 })
  })

  it('accepts official aliases, standard full Claude IDs and discovered provider IDs while enforcing capabilities', () => {
    const catalog: ClaudeModelCatalog = { source: 'cli', detail: 'Fixture', models: [{ value: 'company/team-model-v2', displayName: 'Company model', description: 'Provider fixture', supportsEffort: true, supportedEffortLevels: ['low', 'high'] }] }
    const provider = validateStartRequest({ ...request, model: 'company/team-model-v2', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'high' } })
    expect(() => validateModelSelection(provider, catalog)).not.toThrow()
    expect(() => validateModelSelection({ ...provider, settings: { ...DEFAULT_RUN_SETTINGS, effort: 'max' } }, catalog)).toThrow('지원하지 않습니다')
    expect(() => validateModelSelection(provider, fallbackModelCatalog())).toThrow('모델 목록')
    for (const model of ['best', 'fable', 'opusplan', 'opus[1m]', 'claude-opus-4-6', 'claude-3-5-sonnet-20241022']) {
      expect(() => validateModelSelection(validateStartRequest({ ...request, model }), fallbackModelCatalog())).not.toThrow()
    }
    for (const model of ['--settings', 'sonnet --dangerously-skip-permissions', 'sonnet\n--help', 'x;rm', '']) {
      expect(() => validateStartRequest({ ...request, model })).toThrow()
    }
    expect(() => validateModelSelection({ ...request, model: 'company/not-in-list' }, catalog)).toThrow('모델 목록')
  })

  it('parses chunk boundaries, ignores partial events, and retains same-ID text after thinking', () => {
    const output: { kind: string; text: string }[] = []
    const resumes: string[] = []
    const parser = new ClaudeStreamParser((kind, text) => output.push({ kind, text }), (id) => resumes.push(id))
    const records = [
      { type: 'system', subtype: 'init', session_id: 'claude-session' },
      { type: 'assistant', uuid: 'record-1', message: { id: 'message-a', content: [{ type: 'text', text: '먼저 구조를 확인합니다.' }] } },
      { type: 'assistant', uuid: 'record-2', message: { id: 'message-b', content: [{ type: 'thinking', thinking: 'private reasoning' }] } },
      { type: 'stream_event', event: { delta: { text: 'partial should not duplicate' } } },
      { type: 'assistant', uuid: 'record-3', message: { id: 'message-b', content: [{ type: 'text', text: '작업 완료.' }] } },
      { type: 'assistant', uuid: 'record-3', message: { id: 'message-b', content: [{ type: 'text', text: '작업 완료.' }] } },
      { type: 'result', is_error: false, result: '작업 완료.', session_id: 'claude-session' },
    ].map((value) => JSON.stringify(value)).join('\n')
    parser.push(records.slice(0, 17))
    parser.push(records.slice(17, 103))
    parser.push(records.slice(103))
    parser.flush()
    expect(output).toEqual([{ kind: 'assistant', text: '먼저 구조를 확인합니다.' }, { kind: 'assistant', text: '작업 완료.' }])
    expect(resumes).toEqual(['claude-session'])
    expect(parser.failed).toBe(false)
  })

  it('surfaces print-mode errors and drops oversized JSON records', () => {
    const output: string[] = []
    const parser = new ClaudeStreamParser((_kind, text) => output.push(text), () => undefined)
    parser.push('x'.repeat(1024 * 1024 + 1))
    parser.push('\n' + JSON.stringify({ type: 'result', is_error: true, errors: ['Authentication required'] }) + '\n')
    expect(output).toHaveLength(2)
    expect(output[1]).toBe('Authentication required')
    expect(parser.failed).toBe(true)
  })
})

describe('native state persistence', () => {
  it('stores only approved paths, serializes concurrent saves, and restores running panes as stopped', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mighty-state-test-'))
    temporary.push(directory)
    const store = new StateStore(directory)
    expect(await store.load()).toEqual(EMPTY_SNAPSHOT)
    const workspace = await store.approveWorkspace({ id: 'workspace-1', path: directory, name: 'Local project', createdAt: new Date().toISOString() })
    const state: AppSnapshot = { ...structuredClone(EMPTY_SNAPSHOT), workspaces: [workspace], activeWorkspaceId: workspace.id, sessions: [{ id: 'session-1', workspaceId: workspace.id, title: 'Claude', kind: 'claude', status: 'running', model: 'default', logs: [], createdAt: workspace.createdAt }], activeSessionId: 'session-1' }
    await expect(store.save({ ...state, workspaces: [{ ...workspace, path: join(directory, 'unapproved') }] })).rejects.toThrow('폴더 선택')
    await Promise.all([store.save({ ...state, theme: 'light' }), store.save({ ...state, theme: 'dark', layout: 'columns' })])
    const disk = JSON.parse(await readFile(join(directory, 'workspace-state.json'), 'utf8')) as AppSnapshot
    expect(disk.theme).toBe('dark')
    expect(disk.layout).toBe('columns')
    const restored = await new StateStore(directory).load()
    expect(restored.sessions[0]?.status).toBe('stopped')
    expect(restored.activeSessionId).toBe('session-1')
    expect(await store.resolveWorkspace(workspace.id)).toEqual(workspace)
    await expect(store.resolveWorkspace('unknown')).rejects.toThrow('폴더 선택')
  })

  it('discards malformed state and dangling session references', () => {
    expect(normalizeSnapshot({ version: 9 })).toEqual(EMPTY_SNAPSHOT)
    const normalized = normalizeSnapshot({ ...EMPTY_SNAPSHOT, sessions: [{ id: 'orphan', workspaceId: 'missing', kind: 'claude' }], sidebarWidth: Infinity })
    expect(normalized.sessions).toEqual([])
    expect(normalized.sidebarWidth).toBe(252)
  })

  it('migrates legacy settings, keeps safe provider IDs, and preserves different pane configurations', () => {
    const base = { ...EMPTY_SNAPSHOT, workspaces: [{ id: 'workspace-1', path: process.cwd(), name: 'Fixture', createdAt: new Date().toISOString() }], sessions: [
      { id: 'old', workspaceId: 'workspace-1', kind: 'claude', model: 'opus', logs: [] },
      { id: 'provider', workspaceId: 'workspace-1', kind: 'claude', model: 'company/team-model-v2', settings: { effort: 'high', permissionMode: 'acceptEdits', maxTurns: 8, maxBudgetUsd: 4.5 }, logs: [] },
      { id: 'damaged', workspaceId: 'workspace-1', kind: 'claude', model: '--help', settings: { effort: 'invalid', permissionMode: 'bypassPermissions', maxTurns: Infinity, maxBudgetUsd: -1 }, logs: [] },
    ] }
    const state = normalizeSnapshot(base, true)
    expect(state.sessions[0]?.settings).toEqual(DEFAULT_RUN_SETTINGS)
    expect(state.sessions[1]).toMatchObject({ model: 'company/team-model-v2', settings: { effort: 'high', permissionMode: 'acceptEdits', maxTurns: 8, maxBudgetUsd: 4.5 } })
    expect(state.sessions[2]).toMatchObject({ model: 'default', settings: DEFAULT_RUN_SETTINGS })
  })
})

describe('native command lifecycle', () => {
  it('a cancelled pending start cannot overwrite a newer run in the same pane', async () => {
    vi.useFakeTimers()
    const workspace: Workspace = { id: 'workspace-1', path: process.cwd(), name: 'Fixture', createdAt: new Date().toISOString() }
    let firstReady!: (value: Workspace) => void
    let secondReady!: (value: Workspace) => void
    const first = new Promise<Workspace>((resolve) => { firstReady = resolve })
    const second = new Promise<Workspace>((resolve) => { secondReady = resolve })
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '.', resolveWorkspace: vi.fn().mockReturnValueOnce(first).mockReturnValueOnce(second), emit: (event) => events.push(event) })
    const shell = { ...request, kind: 'shell', input: 'echo must-not-start', resumeId: undefined }
    try {
      const firstStart = manager.start(shell)
      const firstStop = manager.stop(request.sessionId)
      await vi.advanceTimersByTimeAsync(4001)
      await firstStop
      expect(events.filter((event) => event.type === 'status')).toEqual([{ sessionId: request.sessionId, type: 'status', status: 'stopped' }])
      const secondStart = manager.start(shell)
      const count = events.length
      firstReady(workspace)
      await firstStart
      expect(events).toHaveLength(count)
      await expect(manager.start(shell)).rejects.toThrow('이미 실행 중')
      const secondStop = manager.stop(request.sessionId)
      await vi.advanceTimersByTimeAsync(4001)
      await secondStop
      secondReady(workspace)
      await secondStart
    } finally {
      firstReady(workspace)
      secondReady(workspace)
      await manager.dispose()
      vi.useRealTimers()
    }
  })

  it('runs a harmless command in its approved workspace and completes', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mighty-run-test-'))
    temporary.push(directory)
    const workspace: Workspace = { id: 'workspace-1', path: directory, name: 'Fixture', createdAt: new Date().toISOString() }
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: join(process.cwd(), 'mods/mighty-bridge'), resolveWorkspace: async () => workspace, emit: (event) => events.push(event) })
    try {
      await manager.start({ ...request, kind: 'shell', input: process.platform === 'win32' ? 'cd' : 'pwd', resumeId: undefined })
      await waitUntil(() => events.some((event) => event.type === 'status' && event.status !== 'running'))
      expect(events.some((event) => event.type === 'status' && event.status === 'completed'), JSON.stringify(events)).toBe(true)
      expect(events.some((event) => event.type === 'log' && event.entry.text.includes(directory))).toBe(true)
      expect(events.filter((event) => event.type === 'status').map((event) => event.status)).toEqual(['running', 'completed'])
    } finally { await manager.dispose() }
  })

  it('rejects an unsupported CLI before starting a process', async () => {
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '.', resolveWorkspace: async () => ({ id: 'workspace-1', path: process.cwd(), name: 'Fixture', createdAt: new Date().toISOString() }), emit: (event) => events.push(event), discoverRuntime: async () => ({ binary: '/a-binary-that-must-never-run', version: '2.1.263', supportsAdapter: false }) })
    try {
      await expect(manager.start(request)).rejects.toThrow('2.1.271 공개 타입')
      expect(events).toEqual([])
    } finally { await manager.dispose() }
  })

  it.skipIf(process.platform === 'win32')('passes different models and settings to isolated pane processes without modifying the parent environment', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mighty-settings-test-'))
    temporary.push(directory)
    const binary = join(directory, 'fake-claude')
    await writeFile(binary, `#!/usr/bin/env node
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => { input += chunk });
process.stdin.on('end', () => {
  const output = { argv: process.argv.slice(2), effort: process.env.CLAUDE_CODE_EFFORT_LEVEL, input };
  process.stdout.write(JSON.stringify({ type: 'result', is_error: false, result: JSON.stringify(output) }) + '\\n');
});
`)
    await chmod(binary, 0o755)
    const provider = 'publishers/anthropic/models/claude-sonnet@20260501'
    const catalog: ClaudeModelCatalog = { source: 'cli', detail: 'Fixture', models: [{ value: provider, displayName: 'Provider model', description: 'Fixture', supportsEffort: true, supportedEffortLevels: ['low'] }] }
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: join(process.cwd(), 'mods/mighty-bridge'), resolveWorkspace: async () => ({ id: 'workspace-1', path: directory, name: 'Fixture', createdAt: new Date().toISOString() }), emit: (event) => events.push(event), discoverRuntime: async () => ({ binary, version: '2.1.271', supportsAdapter: true, modelCatalog: catalog }) })
    vi.stubEnv('CLAUDE_CODE_EFFORT_LEVEL', 'max')
    try {
      await Promise.all([
        manager.start({ ...request, sessionId: 'pane-first', settings: { effort: 'high', permissionMode: 'plan', maxTurns: 3, maxBudgetUsd: 0.5 } }),
        manager.start({ ...request, sessionId: 'pane-second', model: provider, settings: { effort: 'low', permissionMode: 'acceptEdits', maxTurns: 9, maxBudgetUsd: 2 } }),
      ])
      await waitUntil(() => events.filter((event) => event.type === 'status' && event.status === 'completed').length === 2)
      const readOutput = (sessionId: string): { argv: string[]; effort: string; input: string } => {
        const event = events.find((event) => event.sessionId === sessionId && event.type === 'log' && event.entry.kind === 'assistant')
        if (!event || event.type !== 'log') throw new Error('Expected fake CLI output')
        return JSON.parse(event.entry.text)
      }
      const first = readOutput('pane-first')
      const second = readOutput('pane-second')
      expect(first.effort).toBe('high')
      expect(second.effort).toBe('low')
      expect(first.argv[first.argv.indexOf('--model') + 1]).toBe('sonnet')
      expect(second.argv[second.argv.indexOf('--model') + 1]).toBe(provider)
      expect(first.argv[first.argv.indexOf('--permission-mode') + 1]).toBe('plan')
      expect(second.argv[second.argv.indexOf('--permission-mode') + 1]).toBe('acceptEdits')
      expect(first.argv[first.argv.indexOf('--max-turns') + 1]).toBe('3')
      expect(second.argv[second.argv.indexOf('--max-budget-usd') + 1]).toBe('2')
      expect(JSON.parse(first.argv[first.argv.indexOf('--settings') + 1]!)).toEqual({ env: { CLAUDE_CODE_EFFORT_LEVEL: 'high' } })
      expect(JSON.parse(second.argv[second.argv.indexOf('--settings') + 1]!)).toEqual({ env: { CLAUDE_CODE_EFFORT_LEVEL: 'low' } })
      expect(first.input).toBe(request.input)
      expect(second.input).toBe(request.input)
      expect(process.env.CLAUDE_CODE_EFFORT_LEVEL).toBe('max')
    } finally { await manager.dispose(); vi.unstubAllEnvs() }
  })

  it.skipIf(process.platform === 'win32')('stops the command and its descendants without leaving a child alive', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mighty-stop-test-'))
    temporary.push(directory)
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '.', resolveWorkspace: async () => ({ id: 'workspace-1', path: directory, name: 'Fixture', createdAt: new Date().toISOString() }), emit: (event) => events.push(event) })
    try {
      await manager.start({ ...request, kind: 'shell', input: 'sleep 30 & child=$!; printf "%s\\n" "$child"; wait', resumeId: undefined })
      await waitUntil(() => events.some((event) => event.type === 'log' && event.entry.kind === 'output' && /^\d+/.test(event.entry.text)))
      const entry = events.find((event) => event.type === 'log' && event.entry.kind === 'output' && /^\d+/.test(event.entry.text))
      if (!entry || entry.type !== 'log') throw new Error('Expected child PID output')
      const pid = Number(entry.entry.text.trim())
      await manager.stop(request.sessionId)
      expect(events.some((event) => event.type === 'status' && event.status === 'stopped')).toBe(true)
      expect(() => process.kill(pid, 0)).toThrow()
    } finally { await manager.dispose() }
  })

  it.skipIf(process.platform === 'win32')('closes a background child even when its parent exits before its output pipe closes', async () => {
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '.', resolveWorkspace: async () => ({ id: 'workspace-1', path: process.cwd(), name: 'Fixture', createdAt: new Date().toISOString() }), emit: (event) => events.push(event) })
    try {
      await manager.start({ ...request, kind: 'shell', input: 'sleep 30 & printf "%s\\n" "$!"; exit 0', resumeId: undefined })
      await waitUntil(() => events.some((event) => event.type === 'status' && event.status === 'completed'))
      const entry = events.find((event) => event.type === 'log' && event.entry.kind === 'output' && /^\d+/.test(event.entry.text))
      if (!entry || entry.type !== 'log') throw new Error('Expected child PID output')
      expect(() => process.kill(Number(entry.entry.text.trim()), 0)).toThrow()
    } finally { await manager.dispose() }
  })

  it.skipIf(process.platform !== 'win32')('preserves Korean output and quoted commands through the Windows job launcher', async () => {
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '.', resolveWorkspace: async () => ({ id: 'workspace-1', path: process.cwd(), name: 'Fixture', createdAt: new Date().toISOString() }), emit: (event) => events.push(event) })
    try {
      const binary = process.execPath.replaceAll('"', '')
      await manager.start({ ...request, kind: 'shell', input: `echo 안녕하세요 & "${binary}" -e "process.stdout.write('quoted path works')"`, resumeId: undefined })
      await waitUntil(() => events.some((event) => event.type === 'status' && event.status !== 'running'), 15_000)
      expect(events.some((event) => event.type === 'status' && event.status === 'completed'), JSON.stringify(events)).toBe(true)
      expect(events.some((event) => event.type === 'log' && event.entry.text.includes('안녕하세요'))).toBe(true)
      expect(events.some((event) => event.type === 'log' && event.entry.text.includes('quoted path works'))).toBe(true)
    } finally { await manager.dispose() }
  }, 20_000)

  it.skipIf(process.platform !== 'win32')('closes Windows job descendants after cmd exits', async () => {
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '.', resolveWorkspace: async () => ({ id: 'workspace-1', path: process.cwd(), name: 'Fixture', createdAt: new Date().toISOString() }), emit: (event) => events.push(event) })
    try {
      await manager.start({ ...request, kind: 'shell', input: `start "" /b "${process.execPath}" -e "console.log(process.pid);setInterval(()=>{},1000)"`, resumeId: undefined })
      await waitUntil(() => events.some((event) => event.type === 'status' && event.status !== 'running'), 15_000)
      expect(events.some((event) => event.type === 'status' && event.status === 'completed'), JSON.stringify(events)).toBe(true)
      const entry = events.find((event) => event.type === 'log' && event.entry.kind === 'output' && /^\d+/.test(event.entry.text))
      if (!entry || entry.type !== 'log') throw new Error('Expected Windows child PID output')
      expect(() => process.kill(Number(entry.entry.text.trim()), 0)).toThrow()
    } finally { await manager.dispose() }
  }, 20_000)
})

describe('Windows launcher specification', () => {
  it.skipIf(process.platform !== 'win32')('starts the real job helper and preserves literal argv and UTF-8 stdin', async () => {
    const argument = 'quoted path \\" 한국어 $(not-a-command)'
    const input = 'launcher stdin 한국어\n'
    const source = "let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',c=>input+=c);process.stdin.on('end',()=>console.log(JSON.stringify({input,args:process.argv.slice(1)})));"
    const launch = windowsLaunch(process.execPath, ['-e', source, argument], process.env)
    const child = spawn(launch.binary, launch.args, { env: launch.env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
    let output = '', errors = ''
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => { output += chunk })
    child.stderr.setEncoding('utf8').on('data', (chunk: string) => { errors += chunk })
    child.stdin.on('error', (error) => { errors += `\nstdin: ${error.message}` })
    const timer = setTimeout(() => child.kill(), 10_000)
    try {
      const exited = new Promise<number | null>((resolve, reject) => { child.once('error', reject); child.once('close', resolve) })
      child.stdin.end(input)
      const code = await exited
      expect(code, `Windows launcher exit=${code}\nstdout=${output}\nstderr=${errors}`).toBe(0)
      expect(JSON.parse(output.trim())).toEqual({ input, args: [argument] })
    } finally { clearTimeout(timer); if (child.exitCode === null && child.signalCode === null) child.kill() }
  }, 15_000)

  it('quotes native argv without evaluating PowerShell and encodes shell output as UTF-8', () => {
    expect(quoteWindowsArgument('plain')).toBe('plain')
    expect(quoteWindowsArgument('a b')).toBe('"a b"')
    expect(quoteWindowsArgument('a"b')).toBe('"a\\"b"')
    expect(quoteWindowsArgument('C:\\space path\\')).toBe('"C:\\space path\\\\"')
    const command = '"C:\\Program Files\\node.exe" -e "console.log(\'안녕하세요\')"'
    expect(windowsShellArguments(command)).toBe(`/d /s /c "chcp 65001>nul & ${command}"`)
    const launch = windowsLaunch('cmd.exe', [], {}, command)
    const spec = JSON.parse(Buffer.from(launch.env.MIGHTY_CLAUDE_LAUNCH_SPEC!, 'base64').toString()) as { arguments: string }
    expect(spec.arguments).toBe(windowsShellArguments(command))
    expect(launch.args.join(' ')).not.toContain(command)
    expect(launch.args).not.toContain('-ExecutionPolicy')
  })
})

async function waitUntil(check: () => boolean, timeoutMs = 5000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (!check()) {
    if (Date.now() > deadline) throw new Error('Timed out waiting for native command')
    await new Promise((resolve) => setTimeout(resolve, 20))
  }
}
