// Ported from the "Mods middleware contract" block of the deleted
// tests/native-mod-bridge.test.ts (e4f973b^), vitest -> node:test. The
// "Mods loopback receiver" block tested the deleted Electron receiver and is not ported.
import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { register } from '../hooks/register.ts'
import type { EngineEffects, On } from '../types/claude-code'

// The original metadata schema that receivers without the activity/graph
// opt-in accept, copied from the deleted electron/main/mod-bridge.ts validateModEnvelope.
const identifier = (value: unknown) => typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/.test(value)
function legacyEnvelope(value: Record<string, unknown>): boolean {
  const keys = new Set(['version', 'runId', 'claudeSessionId', 'event', 'turnId', 'tool', 'durationMs', 'reason'])
  if (Object.keys(value).some(key => !keys.has(key)) || value.version !== 1 || !identifier(value.runId) || !identifier(value.claudeSessionId) || !['session.start', 'turn.start', 'turn.complete', 'tool.call'].includes(String(value.event))) return false
  if (value.turnId !== undefined && !identifier(value.turnId)) return false
  if (value.tool !== undefined && (typeof value.tool !== 'string' || !/^[a-zA-Z][a-zA-Z0-9_.:/-]{0,127}$/.test(value.tool))) return false
  if (value.durationMs !== undefined && (typeof value.durationMs !== 'number' || !Number.isFinite(value.durationMs) || value.durationMs < 0 || value.durationMs > 86_400_000)) return false
  if (value.reason !== undefined && !['answer', 'aborted', 'refusal', 'error'].includes(String(value.reason))) return false
  if (value.event === 'tool.call' && typeof value.tool !== 'string') return false
  if ((value.event === 'turn.start' || value.event === 'turn.complete') && !identifier(value.turnId)) return false
  return true
}

const usageEffects = {
  session: { id: async () => 'claude-session', usage: async () => ({ context: { window: 200_000 }, rateLimits: [] }) },
  clock: { now: async () => Date.parse('2026-09-16T10:00:00Z') },
}

type FixtureHook = (effects: EngineEffects, event: never, next: (event: never) => Promise<unknown>) => Promise<unknown>

describe('Mods middleware contract', () => {
  it('calls next once with the original event and forwards metadata without prompts or tool arguments', async () => {
    const hooks = new Map<string, FixtureHook>()
    register(((name: string, callback: FixtureHook) => hooks.set(name, callback)) as unknown as On, {})
    const sent: Record<string, unknown>[] = []
    const variables: Record<string, string> = { MIGHTY_CLAUDE_BRIDGE_URL: 'http://127.0.0.1:54321/events', MIGHTY_CLAUDE_BRIDGE_TOKEN: 'a'.repeat(64), MIGHTY_CLAUDE_RUN_ID: 'run-1' }
    const effects: EngineEffects = { ...usageEffects, env: { get: async (name) => variables[name] }, http: { fetch: async (_url, init) => { sent.push(JSON.parse(init?.body ?? '{}')); return { status: 204, ok: true, headers: {}, text: '' } } } }
    const events = {
      'session.start': Object.freeze({ cwd: '/private/workspace', surface: 'terminal', isInteractive: false }),
      'turn.start': Object.freeze({ text: 'secret user prompt', turnId: 'turn-1' }),
      'tool.call': Object.freeze({ tool: 'Bash', tool_use_id: 'tool-1', command: 'cat private-credentials' }),
      'turn.complete': Object.freeze({ answer: 'secret answer', durationMs: 120, isAborted: false, turnId: 'turn-1', reason: 'answer' }),
    }
    for (const [name, event] of Object.entries(events)) {
      let calls = 0
      const originalResult = Object.freeze({ sentinel: name })
      const result = await hooks.get(name)!(effects, event as never, async (forwarded) => {
        calls++
        assert.equal(forwarded, event)
        return originalResult
      })
      assert.equal(calls, 1)
      assert.equal(result, originalResult)
    }
    await new Promise((resolve) => setTimeout(resolve, 0))
    assert.equal(sent.length, 4)
    assert.equal(sent.every((event) => legacyEnvelope(event)), true)
    const serialized = JSON.stringify(sent)
    assert.ok(!serialized.includes('secret'))
    assert.ok(!serialized.includes('private-credentials'))
    assert.ok(!serialized.includes('/private/workspace'))
  })

  it('does not block the engine when network policy rejects the observation effect', async () => {
    const hooks = new Map<string, FixtureHook>()
    register(((name: string, callback: FixtureHook) => hooks.set(name, callback)) as unknown as On, {})
    const effects: EngineEffects = { ...usageEffects, env: { get: async () => { throw new Error('Policy denied') } }, http: { fetch: async () => { throw new Error('must not reach network') } } }
    const event = Object.freeze({ tool: 'Read', path: '/private/file' })
    let calls = 0
    const result = await hooks.get('tool.call')!(effects, event as never, async () => { calls++; return 'original result' })
    await new Promise((resolve) => setTimeout(resolve, 0))
    assert.equal(calls, 1)
    assert.equal(result, 'original result')
  })

  it('only sends bounded direct tool activity when the native host opts in', async () => {
    const hooks = new Map<string, FixtureHook>()
    register(((name: string, callback: FixtureHook) => hooks.set(name, callback)) as unknown as On, {})
    const sent: Record<string, unknown>[] = []
    const variables: Record<string, string> = { MIGHTY_CLAUDE_BRIDGE_URL: 'http://127.0.0.1:54321/events', MIGHTY_CLAUDE_BRIDGE_TOKEN: 'a'.repeat(64), MIGHTY_CLAUDE_RUN_ID: 'run-activity', MIGHTY_CLAUDE_ACTIVITY: '1' }
    const effects: EngineEffects = { ...usageEffects, env: { get: async name => variables[name] }, http: { fetch: async (_url, init) => { sent.push(JSON.parse(init?.body ?? '{}')); return { status: 200, ok: true, headers: {}, text: '' } } } }
    const call = Object.freeze({ tool: 'Bash', tool_use_id: 'tool-1', command: 'printf hello', prompt: 'PRIVATE_PROMPT', token: 'PRIVATE_TOKEN' })
    const result = Object.freeze({ text: 'hello\n' + '🦀'.repeat(4000), result: { stdout: 'original' } })
    let calls = 0
    const observed = await hooks.get('tool.call')!(effects, call as never, async event => { calls++; assert.equal(event, call); return result })
    await new Promise(resolve => setTimeout(resolve, 0))
    assert.equal(observed, result); assert.equal(calls, 1)
    assert.deepEqual(sent.map(event => event.event), ['tool.call', 'tool.complete'])
    assert.equal(sent.every(event => event.toolUseId === 'tool-1' && event.summary === 'printf hello'), true)
    assert.equal(sent[1]?.isError, false)
    assert.ok(Buffer.byteLength(sent[1]?.output as string) <= 4096)
    assert.ok(Buffer.byteLength(JSON.stringify(sent[1])) < 16_384)
    assert.ok((sent[1]?.sequence as number) > (sent[0]?.sequence as number))
    assert.ok(!JSON.stringify(sent).includes('PRIVATE_'))
  })

  it('observes ask and deny without changing permissions or swallowing tool failures', async () => {
    const hooks = new Map<string, FixtureHook>()
    register(((name: string, callback: FixtureHook) => hooks.set(name, callback)) as unknown as On, {})
    const sent: Record<string, unknown>[] = []
    const variables: Record<string, string> = { MIGHTY_CLAUDE_BRIDGE_URL: 'http://127.0.0.1:54321/events', MIGHTY_CLAUDE_BRIDGE_TOKEN: 'a'.repeat(64), MIGHTY_CLAUDE_RUN_ID: 'run-activity', MIGHTY_CLAUDE_ACTIVITY: '1' }
    const effects: EngineEffects = { ...usageEffects, env: { get: async name => variables[name] }, http: { fetch: async (_url, init) => { sent.push(JSON.parse(init?.body ?? '{}')); return { status: 200, ok: true, headers: {}, text: '' } } } }
    const check = Object.freeze({ tool: 'Read', tool_use_id: 'tool-1', input: { file_path: 'README.md' } })
    const verdict = Object.freeze({ decision: 'ask', reason: 'existing policy' })
    assert.equal(await hooks.get('tool.check')!(effects, check as never, async event => { assert.equal(event, check); return verdict }), verdict)
    const call = Object.freeze({ tool: 'Read', tool_use_id: 'tool-1', file_path: 'README.md' })
    const denied = Object.freeze({ deny: 'Permission denied' })
    assert.equal(await hooks.get('tool.call')!(effects, call as never, async () => denied), denied)
    const failure = new Error('fixture failure')
    await assert.rejects(hooks.get('tool.call')!(effects, { ...call, tool_use_id: 'tool-2' } as never, async () => { throw failure }), error => error === failure)
    await new Promise(resolve => setTimeout(resolve, 0))
    assert.equal(sent.some(event => event.event === 'tool.waiting' && event.summary === 'README.md'), true)
    assert.equal(sent.some(event => event.event === 'tool.complete' && event.toolUseId === 'tool-1' && event.isError === true && event.output === 'Permission denied'), true)
    assert.equal(sent.some(event => event.event === 'tool.complete' && event.toolUseId === 'tool-2' && event.isError === true && event.output === 'fixture failure'), true)
  })
})
