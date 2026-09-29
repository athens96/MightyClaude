// Ported from the deleted tests/native-mod-usage.test.ts (e4f973b^), vitest -> node:test.
import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { register } from '../hooks/register.ts'
import type { EngineEffects, On } from '../types/claude-code'

type Hook = (effects: EngineEffects, event: never, next: (event: never) => Promise<unknown>) => Promise<unknown>
function hooks() {
  const result = new Map<string, Hook>()
  register(((name: string, callback: Hook) => result.set(name, callback)) as unknown as On, {})
  return result
}
const variables = () => ({ MIGHTY_CLAUDE_BRIDGE_URL: 'http://127.0.0.1:54321/events', MIGHTY_CLAUDE_BRIDGE_TOKEN: 'a'.repeat(64), MIGHTY_CLAUDE_RUN_ID: 'run-usage', MIGHTY_CLAUDE_USAGE: '1' }) as Record<string, string>
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
// vitest toMatchObject for the flat object below.
function matches(actual: unknown, expected: Record<string, unknown>): void {
  assert.ok(actual && typeof actual === 'object', 'expected an object')
  for (const [key, value] of Object.entries(expected)) assert.deepEqual((actual as Record<string, unknown>)[key], value, key)
}

describe('free direct Claude Mods usage', () => {
  it('only reads free usage after opt-in and leaves compacted context unknown', async () => {
    const callbacks = hooks(), sent: Record<string, unknown>[] = [], env = variables()
    let reads = 0, compacted = false
    const effects: EngineEffects = {
      env: { get: async name => env[name] }, clock: { now: async () => Date.parse('2026-09-16T10:00:00Z') },
      session: { id: async () => 'claude-session', usage: async (...args: unknown[]) => {
        assert.equal(args.length, 0); reads++
        return { context: { tokens: compacted ? undefined : 40_000, window: 200_000 }, cost: { usd: 1.25 }, rateLimits: [{ kind: 'five_hour', percentUsed: 25, resetsAt: '2026-09-16T12:00:00Z' }, { kind: 'spend_limit', percentUsed: 120 }] }
      } },
      http: { fetch: async (_url, init) => { sent.push(JSON.parse(init?.body ?? '{}')); return { status: 200, ok: true, headers: {}, text: '' } } },
    }
    const event = Object.freeze({ answer: 'PRIVATE ANSWER', durationMs: 1, isAborted: false, turnId: 'turn-usage', reason: 'answer', usage: { model: 'claude-sonnet-4-6' } })
    const result = Object.freeze({ text: 'PRIVATE ANSWER' })
    assert.equal(await callbacks.get('turn.complete')!(effects, event as never, async original => { assert.equal(original, event); return result }), result)
    await settle()
    const snapshot = sent.find(row => row.event === 'session.usage')?.usage as Record<string, unknown>
    matches(snapshot, { contextUsedTokens: 40_000, contextWindowTokens: 200_000, costUSD: 1.25, costScope: 'session', model: 'claude-sonnet-4-6' })
    assert.deepEqual(snapshot.rateLimits, [{ kind: 'five_hour', percentUsed: 25, resetsAt: '2026-09-16T12:00:00Z' }, { kind: 'spend_limit', percentUsed: 120 }])
    assert.ok(!JSON.stringify(snapshot).includes('PRIVATE'))
    compacted = true
    await callbacks.get('session.compact')!(effects, { trigger: 'auto' } as never, async () => ({ messages: [] }))
    await settle()
    assert.equal((sent.at(-1)?.usage as Record<string, unknown>).contextUsedTokens, undefined)
    assert.equal(reads, 2)
    delete env.MIGHTY_CLAUDE_USAGE
    await callbacks.get('session.compact')!(effects, { trigger: 'auto' } as never, async () => ({ messages: [] }))
    await settle(); assert.equal(reads, 2)
  })

  it('ignores subagent usage and bounds malformed measurements', async () => {
    const callbacks = hooks(), sent: Record<string, unknown>[] = [], env = variables()
    let reads = 0
    const effects: EngineEffects = { env: { get: async name => env[name] }, clock: { now: async () => Date.parse('2026-09-16T10:00:00Z') },
      session: { id: async () => 'claude-session', usage: async () => { reads++; return { context: { tokens: -1, window: 200_000 }, cost: { usd: Infinity }, rateLimits: [{ kind: 'five_hour', percentUsed: NaN }] } } },
      http: { fetch: async (_url, init) => { sent.push(JSON.parse(init?.body ?? '{}')); return { status: 200, ok: true, headers: {}, text: '' } } },
    }
    await callbacks.get('session.compact')!(effects, { trigger: 'auto', agentId: 'subagent' } as never, async () => ({ messages: [] }))
    await settle(); assert.equal(reads, 0)
    await callbacks.get('session.compact')!(effects, { trigger: 'auto' } as never, async () => ({ messages: [] }))
    await settle()
    const snapshot = sent[0]?.usage as Record<string, unknown>
    assert.equal(snapshot.contextUsedTokens, undefined); assert.equal(snapshot.costUSD, undefined); assert.deepEqual(snapshot.rateLimits, [])
    assert.ok(Buffer.byteLength(JSON.stringify(sent[0])) < 16_384)
  })
})
