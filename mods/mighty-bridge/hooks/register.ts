import type { HttpInit, HttpResponse, Register, SessionUsageReading } from '../types/claude-code'

type Metadata = {
  event: 'session.start' | 'turn.start' | 'turn.complete' | 'tool.call' | 'tool.waiting' | 'tool.complete'
  turnId?: string
  tool?: string
  durationMs?: number
  reason?: 'answer' | 'aborted' | 'refusal' | 'error'
  toolUseId?: string
  summary?: string
  output?: string
  isError?: boolean
  agentId?: string
}

type GraphMetadata = {
  version: 1
  phase: 'starting' | 'running' | 'completed' | 'error' | 'stopped'
  agentId?: string
  parentAgentId?: string
  parentToolUseId?: string
  name?: string
  agentType?: string
  model?: string
  input?: string
  output?: string
}

const maximumGraphInputBytes = 16_384
const maximumGraphOutputBytes = 32_768
const maximumGraphBodyBytes = 65_536
const graphAgents = new Map<string, Omit<GraphMetadata, 'version' | 'phase' | 'input' | 'output'>>()
const toolAgents = new Map<string, string>()
function identity(value: unknown): string | undefined {
  return typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/.test(value) ? value : undefined
}
function remember<K, V>(values: Map<K, V>, key: K, value: V): void {
  values.delete(key); values.set(key, value)
  while (values.size > 256) values.delete(values.keys().next().value!)
}

function clean(value: unknown, limit: number, singleLine = false): string | undefined {
  if (typeof value !== 'string') return undefined
  const plain = value.replace(/\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))/g, '')
  let output = '', bytes = 0
  for (let character of plain) {
    const code = character.codePointAt(0)!
    if (code >= 0xd800 && code <= 0xdfff || code < 32 && ![9, 10, 13].includes(code) || code >= 127 && code <= 159) continue
    if (singleLine && /\s/.test(character)) character = ' '
    const count = character === ' ' ? 1 : code < 128 ? 1 : code < 2048 ? 2 : code < 65536 ? 3 : 4
    if (bytes + count > limit) break
    output += character; bytes += count
  }
  return output.trim()
}

function summary(tool: string, input: unknown): string {
  if (input && typeof input === 'object') {
    const values = input as Record<string, unknown>
    for (const key of ['command', 'file_path', 'absolute_path', 'path', 'pattern', 'query', 'url', 'description', 'target_file', 'filename', 'glob']) {
      const value = clean(values[key], 1000, true)
      if (value) return value
    }
  }
  return clean(tool, 1000, true) ?? 'Tool'
}

let sequence = 0
let generatedToolId = 0
// HTTP is best effort. The published HTTP effect has no timeout option, so an
// unavailable desktop must never be awaited on the engine's execution path.
let pending = 0
function report(
  metadata: Metadata,
  context: () => Promise<(string | undefined)[]>,
  send: (url: string, init: HttpInit) => Promise<HttpResponse>,
): void {
  if (pending >= 8) return
  pending++
  const order = ++sequence
  void (async () => {
    const [url, token, runId, claudeSessionId, activity, graph] = await context()
    if (!url || !/^http:\/\/127\.0\.0\.1:\d{1,5}\/events$/.test(url) || !token || !/^[a-f0-9]{64}$/.test(token) || !runId || !claudeSessionId) return
    // Existing Windows/Electron receivers accept the original metadata schema.
    // Rich observations require an explicit host opt-in, never a global setting.
    let payload: object = { ...metadata, sequence: order }
    if (activity !== '1' && graph !== '1') {
      if (metadata.event === 'tool.complete' || metadata.event === 'tool.waiting') return
      const { event, turnId, tool, durationMs, reason } = metadata
      payload = { event, turnId, tool, durationMs, reason }
    } else if (graph !== '1' && metadata.event.startsWith('tool.')) {
      // Child ownership is a graph addition. Older rich-activity receivers
      // continue to receive their previous tool envelope unchanged.
      const { agentId: _agentId, ...existing } = metadata
      payload = { ...existing, sequence: order }
    }
    await send(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ version: 1, runId, claudeSessionId, ...payload }),
    })
  })().catch(() => undefined).finally(() => { pending-- })
}

// Independent bounded capacity keeps lifecycle observations from being starved
// by ordinary tool/usage telemetry. It never delays or changes engine work.
let pendingGraph = 0
function reportGraph(
  event: 'agent.spawn' | 'agent.complete',
  graph: GraphMetadata,
  context: () => Promise<(string | undefined)[]>,
  send: (url: string, init: HttpInit) => Promise<HttpResponse>,
): void {
  if (pendingGraph >= 8) return
  pendingGraph++
  const order = ++sequence
  void (async () => {
    const [url, token, runId, claudeSessionId, enabled] = await context()
    if (enabled !== '1' || !url || !/^http:\/\/127\.0\.0\.1:\d{1,5}\/events$/.test(url) || !token || !/^[a-f0-9]{64}$/.test(token) || !identity(runId) || !identity(claudeSessionId)) return
    const bounded = { ...graph }
    let body = JSON.stringify({ version: 1, runId, claudeSessionId, event, sequence: order, graph: bounded })
    // JSON escaping can expand otherwise bounded text (quotes/backslashes).
    // Shrink only display content so every transmitted request fits the cap.
    for (let attempt = 0; new TextEncoder().encode(body).length > maximumGraphBodyBytes && attempt < 4; attempt++) {
      bounded.input = clean(bounded.input, maximumGraphInputBytes >> (attempt + 1))
      bounded.output = clean(bounded.output, maximumGraphOutputBytes >> (attempt + 1))
      body = JSON.stringify({ version: 1, runId, claudeSessionId, event, sequence: order, graph: bounded })
    }
    if (new TextEncoder().encode(body).length > maximumGraphBodyBytes) return
    await send(url, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, 'X-Mighty-Graph': '1' }, body })
  })().catch(() => undefined).finally(() => { pendingGraph-- })
}

function boundedNumber(value: unknown, maximum: number, integer = false): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= maximum && (!integer || Number.isSafeInteger(value)) ? value : undefined
}

// This separate opt-in keeps unchanged Windows/Electron receivers on their
// original v1 metadata. Only free figures already held by the engine are read.
function reportUsage(
  model: string | undefined,
  context: () => Promise<(string | undefined)[]>,
  read: () => Promise<[SessionUsageReading, number]>,
  send: (url: string, init: HttpInit) => Promise<HttpResponse>,
): void {
  if (pending >= 8) return
  pending++
  const order = ++sequence
  void (async () => {
    const [url, token, runId, claudeSessionId, enabled] = await context()
    if (enabled !== '1' || !url || !/^http:\/\/127\.0\.0\.1:\d{1,5}\/events$/.test(url) || !token || !/^[a-f0-9]{64}$/.test(token) || !runId || !claudeSessionId) return
    const [reading, now] = await read()
    if (!reading?.context || !Number.isFinite(now)) return
    const window = boundedNumber(reading.context.window, 1_000_000_000, true)
    if (!window) return
    const rateLimits = (Array.isArray(reading.rateLimits) ? reading.rateLimits : []).slice(0, 16).flatMap(row => {
      const kind = clean(row?.kind, 80, true)
      if (!kind) return []
      const percentUsed = boundedNumber(row.percentUsed, kind === 'spend_limit' ? 1_000_000 : 100)
      const resetsAt = typeof row.resetsAt === 'string' && row.resetsAt.length <= 80 && Number.isFinite(Date.parse(row.resetsAt)) ? row.resetsAt : undefined
      return percentUsed === undefined && resetsAt === undefined ? [] : [{ kind, percentUsed, resetsAt }]
    })
    const costUSD = boundedNumber(reading.cost?.usd, 1_000_000_000)
    const usage = {
      provider: 'claude', source: 'claude.mods', tokenScope: 'session', providerSessionId: claudeSessionId,
      model: clean(model, 200, true), contextUsedTokens: boundedNumber(reading.context.tokens, 9_000_000_000_000, true),
      contextWindowTokens: window, costUSD, costScope: costUSD === undefined ? undefined : 'session', rateLimits,
      updatedAt: new Date(now).toISOString(), rateLimitsUpdatedAt: new Date(now).toISOString(),
    }
    await send(url, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ version: 1, runId, claudeSessionId, event: 'session.usage', sequence: order, usage }) })
  })().catch(() => undefined).finally(() => { pending-- })
}

/** Observe lifecycle and bounded tool display fields. Never rewrite decisions. */
export const register: Register = (on, _options) => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    report({ event: 'session.start' },
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_ACTIVITY')]),
      (url, init) => $.http.fetch(url, init))
    reportUsage(undefined,
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_USAGE')]),
      () => Promise.all([$.session.usage(), $.clock.now()]), (url, init) => $.http.fetch(url, init))
    return result
  })

  on('turn.start', async ($, e, next) => {
    report({ event: 'turn.start', turnId: e.turnId },
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_ACTIVITY')]),
      (url, init) => $.http.fetch(url, init))
    reportUsage(undefined,
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_USAGE')]),
      () => Promise.all([$.session.usage(), $.clock.now()]), (url, init) => $.http.fetch(url, init))
    return next(e)
  })

  on('tool.call', async ($, e, next) => {
    const toolUseId = e.tool_use_id ?? `observed-${++generatedToolId}`
    const agentId = identity(e.agentId)
    if (agentId) remember(toolAgents, toolUseId, agentId)
    const tool = clean(e.tool, 160, true) ?? 'Tool'
    const display = summary(tool, e)
    const context = () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_ACTIVITY'), $.env.get('MIGHTY_CLAUDE_GRAPH')])
    const send = (url: string, init: HttpInit) => $.http.fetch(url, init)
    report({ event: 'tool.call', tool, toolUseId, summary: display, agentId }, context, send)
    if (!e.agentId) reportUsage(undefined,
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_USAGE')]),
      () => Promise.all([$.session.usage(), $.clock.now()]), send)
    try {
      const result = await next(e)
      const value = result && typeof result === 'object' ? result as Record<string, unknown> : undefined
      report({ event: 'tool.complete', tool, toolUseId, summary: display, agentId, isError: value?.isError === true || typeof value?.deny === 'string', output: clean(value?.deny ?? value?.text, 4096) }, context, send)
      return result
    } catch (error) {
      report({ event: 'tool.complete', tool, toolUseId, summary: display, agentId, isError: true, output: clean(error instanceof Error ? error.message : '도구 실행 오류', 4096) }, context, send)
      throw error
    } finally { toolAgents.delete(toolUseId) }
  })

  on('tool.check', async ($, e, next) => {
    const result = await next(e)
    if (result.decision === 'ask' && e.tool_use_id) {
      report({ event: 'tool.waiting', tool: clean(e.tool, 160, true), toolUseId: e.tool_use_id, summary: summary(e.tool, e.input), agentId: toolAgents.get(e.tool_use_id) },
        () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_ACTIVITY'), $.env.get('MIGHTY_CLAUDE_GRAPH')]),
        (url, init) => $.http.fetch(url, init))
    }
    return result
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    report({ event: 'turn.complete', turnId: e.turnId, durationMs: e.durationMs, reason: e.reason, agentId: e.agentId },
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_ACTIVITY')]),
      (url, init) => $.http.fetch(url, init))
    const agentId = identity(e.agentId)
    if (agentId) {
      reportGraph('agent.complete', { ...graphAgents.get(agentId), version: 1,
        phase: e.reason === 'aborted' ? 'stopped' : e.reason === 'error' || e.reason === 'refusal' ? 'error' : 'completed',
        agentId, output: clean(e.answer, maximumGraphOutputBytes) },
        () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_GRAPH')]),
        (url, init) => $.http.fetch(url, init))
    }
    if (!e.agentId) reportUsage(e.usage?.model,
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_USAGE')]),
      () => Promise.all([$.session.usage(), $.clock.now()]), (url, init) => $.http.fetch(url, init))
    return result
  })

  on('agent.spawn', async ($, e, next) => {
    const parentToolUseId = identity(e.tool_use_id)
    const context = () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_GRAPH')])
    const send = (url: string, init: HttpInit) => $.http.fetch(url, init)
    const fields = { parentToolUseId, parentAgentId: identity(e.parentAgentId),
      name: clean(e.name ?? e.description, 160, true), agentType: clean(e.subagentType, 160, true),
      model: clean(e.model, 200, true) }
    if (parentToolUseId) reportGraph('agent.spawn', { ...fields, version: 1, phase: 'starting', input: clean(e.prompt, maximumGraphInputBytes) }, context, send)
    try {
      const result = await next(e)
      const agentId = identity(result.agentId)
      if (agentId) {
        const started = { ...fields, agentId, model: clean(result.model, 200, true) }
        remember(graphAgents, agentId, started)
        if (parentToolUseId) reportGraph('agent.spawn', { ...started, version: 1, phase: 'running' }, context, send)
      } else if (parentToolUseId) {
        reportGraph('agent.spawn', { ...fields, version: 1, phase: result.deny ? 'error' : 'stopped',
          output: clean(result.deny ?? '하위 에이전트가 시작되지 않았습니다.', maximumGraphOutputBytes) }, context, send)
      }
      return result
    } catch (error) {
      if (parentToolUseId) reportGraph('agent.spawn', { ...fields, version: 1, phase: 'error',
        output: clean(error instanceof Error ? error.message : '하위 에이전트 시작 오류', maximumGraphOutputBytes) }, context, send)
      throw error
    }
  })

  on('session.compact', async ($, e, next) => {
    const result = await next(e)
    if (!e.agentId) reportUsage(undefined,
      () => Promise.all([$.env.get('MIGHTY_CLAUDE_BRIDGE_URL'), $.env.get('MIGHTY_CLAUDE_BRIDGE_TOKEN'), $.env.get('MIGHTY_CLAUDE_RUN_ID'), $.session.id(), $.env.get('MIGHTY_CLAUDE_USAGE')]),
      () => Promise.all([$.session.usage(), $.clock.now()]), (url, init) => $.http.fetch(url, init))
    return result
  })
}
