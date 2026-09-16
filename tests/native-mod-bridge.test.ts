import { afterEach, describe, expect, it } from 'vitest'
import { ModBridgeServer, MAX_BRIDGE_BODY, validateModEnvelope } from '../electron/main/mod-bridge'
import { register } from '../mods/mighty-bridge/hooks/register'
import type { EngineEffects, On } from '../mods/mighty-bridge/types/claude-code'

const servers: ModBridgeServer[] = []
afterEach(async () => { await Promise.all(servers.splice(0).map((server) => server.close())) })
const envelope = { version: 1, runId: 'run-1', claudeSessionId: 'claude-session', event: 'tool.call', tool: 'Read' }
const usageEffects = {
  session: { id: async () => 'claude-session', usage: async () => ({ context: { window: 200_000 }, rateLimits: [] }) },
  clock: { now: async () => Date.parse('2026-09-16T10:00:00Z') },
}

describe('Mods loopback receiver', () => {
  it('only accepts bounded lifecycle metadata', () => {
    expect(validateModEnvelope(envelope)).toEqual(envelope)
    expect(validateModEnvelope({ ...envelope, prompt: 'private prompt' })).toBeNull()
    expect(validateModEnvelope({ ...envelope, input: { path: 'private file' } })).toBeNull()
    expect(validateModEnvelope({ ...envelope, tool: 'Read\nforged log' })).toBeNull()
    expect(validateModEnvelope({ ...envelope, event: 'engine.execute' })).toBeNull()
    expect(validateModEnvelope({ ...envelope, durationMs: Infinity })).toBeNull()
  })

  it('authenticates each run, rejects browser requests and caps bodies', async () => {
    const server = new ModBridgeServer()
    servers.push(server)
    const received: unknown[] = []
    const connection = await server.register((event) => received.push(event))
    const body = { ...envelope, runId: connection.runId }
    const post = (payload: unknown, headers: Record<string, string> = {}) => fetch(connection.url, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${connection.token}`, ...headers }, body: JSON.stringify(payload) })
    expect((await post(body)).status).toBe(204)
    expect(connection.received()).toBe(1)
    expect(received).toEqual([body])
    expect((await post(body, { Authorization: 'Bearer ' + '0'.repeat(64) })).status).toBe(401)
    expect((await post(body, { Origin: 'https://example.com' })).status).toBe(403)
    expect((await post({ ...body, runId: 'other-run' })).status).toBe(400)
    expect((await post({ ...body, answer: 'must not forward content' })).status).toBe(400)
    expect((await post({ ...body, extra: 'x'.repeat(MAX_BRIDGE_BODY) })).status).toBe(413)
    connection.release()
    expect((await post(body)).status).toBe(401)
    expect(received).toHaveLength(1)
  })
})

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
        expect(forwarded).toBe(event)
        return originalResult
      })
      expect(calls).toBe(1)
      expect(result).toBe(originalResult)
    }
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(sent).toHaveLength(4)
    expect(sent.every((event) => validateModEnvelope(event) !== null)).toBe(true)
    const serialized = JSON.stringify(sent)
    expect(serialized).not.toContain('secret')
    expect(serialized).not.toContain('private-credentials')
    expect(serialized).not.toContain('/private/workspace')
  })

  it('does not block the engine when network policy rejects the observation effect', async () => {
    const hooks = new Map<string, FixtureHook>()
    register(((name: string, callback: FixtureHook) => hooks.set(name, callback)) as unknown as On, {})
    const effects: EngineEffects = { ...usageEffects, env: { get: async () => { throw new Error('Policy denied') } }, http: { fetch: async () => { throw new Error('must not reach network') } } }
    const event = Object.freeze({ tool: 'Read', path: '/private/file' })
    let calls = 0
    const result = await hooks.get('tool.call')!(effects, event as never, async () => { calls++; return 'original result' })
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(calls).toBe(1)
    expect(result).toBe('original result')
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
    const observed = await hooks.get('tool.call')!(effects, call as never, async event => { calls++; expect(event).toBe(call); return result })
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(observed).toBe(result); expect(calls).toBe(1)
    expect(sent.map(event => event.event)).toEqual(['tool.call', 'tool.complete'])
    expect(sent.every(event => event.toolUseId === 'tool-1' && event.summary === 'printf hello')).toBe(true)
    expect(sent[1]?.isError).toBe(false)
    expect(Buffer.byteLength(sent[1]?.output as string)).toBeLessThanOrEqual(4096)
    expect(Buffer.byteLength(JSON.stringify(sent[1]))).toBeLessThan(16_384)
    expect((sent[1]?.sequence as number)).toBeGreaterThan(sent[0]?.sequence as number)
    expect(JSON.stringify(sent)).not.toContain('PRIVATE_')
  })

  it('observes ask and deny without changing permissions or swallowing tool failures', async () => {
    const hooks = new Map<string, FixtureHook>()
    register(((name: string, callback: FixtureHook) => hooks.set(name, callback)) as unknown as On, {})
    const sent: Record<string, unknown>[] = []
    const variables: Record<string, string> = { MIGHTY_CLAUDE_BRIDGE_URL: 'http://127.0.0.1:54321/events', MIGHTY_CLAUDE_BRIDGE_TOKEN: 'a'.repeat(64), MIGHTY_CLAUDE_RUN_ID: 'run-activity', MIGHTY_CLAUDE_ACTIVITY: '1' }
    const effects: EngineEffects = { ...usageEffects, env: { get: async name => variables[name] }, http: { fetch: async (_url, init) => { sent.push(JSON.parse(init?.body ?? '{}')); return { status: 200, ok: true, headers: {}, text: '' } } } }
    const check = Object.freeze({ tool: 'Read', tool_use_id: 'tool-1', input: { file_path: 'README.md' } })
    const verdict = Object.freeze({ decision: 'ask', reason: 'existing policy' })
    expect(await hooks.get('tool.check')!(effects, check as never, async event => { expect(event).toBe(check); return verdict })).toBe(verdict)
    const call = Object.freeze({ tool: 'Read', tool_use_id: 'tool-1', file_path: 'README.md' })
    const denied = Object.freeze({ deny: 'Permission denied' })
    expect(await hooks.get('tool.call')!(effects, call as never, async () => denied)).toBe(denied)
    const failure = new Error('fixture failure')
    await expect(hooks.get('tool.call')!(effects, { ...call, tool_use_id: 'tool-2' } as never, async () => { throw failure })).rejects.toBe(failure)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(sent.some(event => event.event === 'tool.waiting' && event.summary === 'README.md')).toBe(true)
    expect(sent.some(event => event.event === 'tool.complete' && event.toolUseId === 'tool-1' && event.isError === true && event.output === 'Permission denied')).toBe(true)
    expect(sent.some(event => event.event === 'tool.complete' && event.toolUseId === 'tool-2' && event.isError === true && event.output === 'fixture failure')).toBe(true)
  })
})
