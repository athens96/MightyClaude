import { describe, expect, it } from 'vitest'
import { register } from '../mods/mighty-bridge/hooks/register'
import type { EngineEffects, HttpInit, On } from '../mods/mighty-bridge/types/claude-code'

type Hook = (effects: EngineEffects, event: never, next: (event: never) => Promise<unknown>) => Promise<unknown>
type Sent = { event: string; graph?: Record<string, unknown>; [key: string]: unknown }
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
function fixture(enabled = true) {
  const hooks = new Map<string, Hook>(), sent: Sent[] = [], requests: HttpInit[] = []
  register(((name: string, callback: Hook) => hooks.set(name, callback)) as unknown as On, {})
  const env: Record<string, string> = {
    MIGHTY_CLAUDE_BRIDGE_URL: 'http://127.0.0.1:54321/events', MIGHTY_CLAUDE_BRIDGE_TOKEN: 'a'.repeat(64),
    MIGHTY_CLAUDE_RUN_ID: 'graph-run', MIGHTY_CLAUDE_ACTIVITY: '1', MIGHTY_CLAUDE_GRAPH: enabled ? '1' : '0',
  }
  const effects: EngineEffects = {
    env: { get: async name => env[name] },
    session: { id: async () => 'graph-session', usage: async () => { throw new Error('Graph never requests usage') } },
    clock: { now: async () => 0 },
    http: { fetch: async (_url, init) => { requests.push(init!); sent.push(JSON.parse(init!.body!)); return { status: 200, ok: true, headers: {}, text: '' } } },
  }
  const invoke = (name: string, event: unknown, next: (event: never) => Promise<unknown>) => hooks.get(name)!(effects, event as never, next)
  return { effects, env, sent, requests, invoke }
}
const spawn = (toolUseId: string, overrides: Record<string, unknown> = {}) => Object.freeze({
  tool_use_id: toolUseId, prompt: 'Read the project and explain the result.', description: 'Read project', subagentType: 'Explore',
  parentModel: 'claude-sonnet-4-6', background: false, fork: false, ...overrides,
})
const complete = (agentId: string, overrides: Record<string, unknown> = {}) => Object.freeze({
  agentId, turnId: 'child-turn', answer: 'The project has three modules.', durationMs: 125, reason: 'answer', isAborted: false, ...overrides,
})

describe('direct opt-in Claude agent graph', () => {
  it('keeps legacy activity unchanged and never sends graph prompt/output without opt-in', async () => {
    const f = fixture(false), event = spawn('legacy-spawn', { prompt: 'PRIVATE_GRAPH_PROMPT' })
    const answer = Object.freeze({ agentId: 'legacy-child', model: 'claude-sonnet-4-6' })
    let calls = 0
    expect(await f.invoke('agent.spawn', event, async actual => { calls++; expect(actual).toBe(event); return answer })).toBe(answer)
    await f.invoke('tool.call', { tool: 'Read', tool_use_id: 'legacy-tool', agentId: 'legacy-child', file_path: 'README.md' }, async () => ({ text: 'fixture' }))
    await f.invoke('turn.complete', complete('legacy-child', { answer: 'PRIVATE_GRAPH_OUTPUT' }), async () => ({ text: 'PRIVATE_GRAPH_OUTPUT' }))
    await settle()
    expect(calls).toBe(1)
    expect(f.sent.every(row => row.graph === undefined && !row.event.startsWith('agent.'))).toBe(true)
    expect(f.sent.filter(row => row.event.startsWith('tool.')).every(row => row.agentId === undefined)).toBe(true)
    expect(JSON.stringify(f.sent)).not.toContain('PRIVATE_GRAPH')
  })

  it('links actual child IDs to spawning tools and preserves nested tool ownership and final answer', async () => {
    const f = fixture(), event = spawn('nested-spawn', { parentAgentId: 'parent-agent', name: 'reader' })
    const answer = Object.freeze({ agentId: 'nested-child', model: 'claude-haiku-4-5' })
    expect(await f.invoke('agent.spawn', event, async actual => { expect(actual).toBe(event); return answer })).toBe(answer)
    await settle()
    const started = f.sent.filter(row => row.event === 'agent.spawn')
    expect(started.map(row => row.graph?.phase)).toEqual(['starting', 'running'])
    expect(started[0]?.graph).toMatchObject({ version: 1, parentToolUseId: 'nested-spawn', parentAgentId: 'parent-agent', name: 'reader', agentType: 'Explore', input: event.prompt })
    expect(started[1]?.graph).toMatchObject({ agentId: 'nested-child', model: 'claude-haiku-4-5' })
    await f.invoke('tool.call', { tool: 'Read', tool_use_id: 'nested-read', agentId: 'nested-child', file_path: 'README.md' }, async () => {
      const verdict = Object.freeze({ decision: 'ask' })
      expect(await f.invoke('tool.check', { tool: 'Read', tool_use_id: 'nested-read', input: { file_path: 'README.md' } }, async () => verdict)).toBe(verdict)
      return { text: 'Read output' }
    })
    await settle()
    const tools = f.sent.filter(row => row.event.startsWith('tool.'))
    expect(tools.map(row => row.event)).toEqual(['tool.call', 'tool.waiting', 'tool.complete'])
    expect(tools.every(row => row.agentId === 'nested-child' && row.toolUseId === 'nested-read')).toBe(true)
    const ended = complete('nested-child'), result = Object.freeze({ text: ended.answer })
    expect(await f.invoke('turn.complete', ended, async actual => { expect(actual).toBe(ended); return result })).toBe(result)
    await settle()
    expect(f.sent.find(row => row.event === 'agent.complete')?.graph).toMatchObject({ phase: 'completed', agentId: 'nested-child', parentToolUseId: 'nested-spawn', parentAgentId: 'parent-agent', output: ended.answer })
    expect(f.requests.filter(row => JSON.parse(row.body!).graph).every(row => row.headers?.['X-Mighty-Graph'] === '1')).toBe(true)
  })

  it('reports denied, interrupted and failed lifecycles without fabricating a child or rewriting errors', async () => {
    const f = fixture(), denied = Object.freeze({ deny: 'Existing permission rule' })
    expect(await f.invoke('agent.spawn', spawn('denied-spawn'), async () => denied)).toBe(denied)
    await settle()
    const failure = new Error('fixture spawn failure')
    await expect(f.invoke('agent.spawn', spawn('failed-spawn'), async () => { throw failure })).rejects.toBe(failure)
    await settle()
    await f.invoke('turn.complete', complete('interrupted-child', { reason: 'aborted', isAborted: true, answer: '' }), async () => ({ text: '' }))
    await settle()
    expect(f.sent.find(row => row.graph?.parentToolUseId === 'denied-spawn' && row.graph?.phase === 'error')?.graph).toMatchObject({ output: 'Existing permission rule' })
    expect(f.sent.filter(row => row.event === 'agent.spawn').every(row => row.graph?.agentId === undefined)).toBe(true)
    expect(f.sent.some(row => row.graph?.parentToolUseId === 'failed-spawn' && row.graph?.output === failure.message)).toBe(true)
    expect(f.sent.find(row => row.event === 'agent.complete')?.graph).toMatchObject({ agentId: 'interrupted-child', phase: 'stopped' })
  })

  it('bounds Unicode and JSON escaping while excluding unrelated engine fields', async () => {
    const f = fixture()
    await f.invoke('agent.spawn', spawn('bounded-spawn', { prompt: '\u001b[31m' + '🦀'.repeat(20_000), token: 'PRIVATE_TOKEN', cwd: '/PRIVATE_CWD' }), async () => ({ agentId: 'bounded-child', model: 'claude-sonnet-4-6' }))
    await settle()
    await f.invoke('turn.complete', complete('bounded-child', { answer: '\\"'.repeat(40_000) }), async () => ({ text: '' }))
    await settle()
    const first = f.sent.find(row => row.graph?.phase === 'starting')?.graph
    const last = f.sent.find(row => row.event === 'agent.complete')?.graph
    expect(Buffer.byteLength(first?.input as string)).toBeLessThanOrEqual(16_384)
    expect(Buffer.byteLength(last?.output as string)).toBeLessThanOrEqual(32_768)
    expect(f.requests.every(row => Buffer.byteLength(row.body!) <= 65_536)).toBe(true)
    expect(JSON.stringify(f.sent)).not.toContain('PRIVATE_')
    expect(first?.input).not.toContain('\u001b')
  })

  it('bounds pending telemetry and never awaits an unavailable desktop on the engine path', async () => {
    const f = fixture(), finish: (() => void)[] = []
    let sent = 0, nextCalls = 0
    f.effects.http.fetch = async () => {
      sent++
      await new Promise<void>(resolve => finish.push(resolve))
      return { status: 200, ok: true, headers: {}, text: '' }
    }
    for (let index = 0; index < 12; index++) {
      await f.invoke('agent.spawn', spawn(`pending-${index}`), async () => { nextCalls++; return { agentId: `pending-child-${index}`, model: 'claude-sonnet-4-6' } })
    }
    await settle()
    expect(nextCalls).toBe(12); expect(sent).toBe(8)
    finish.forEach(resolve => resolve()); await settle()
  })
})
