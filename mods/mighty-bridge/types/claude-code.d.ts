/**
 * Narrow, locally authored adapter contract checked against Claude Code 2.1.273.
 * Reference: https://github.com/anthropics/claude-code/blob/main/mods/types/claude-code.d.ts
 * This is not the complete upstream API or a claim of backwards compatibility.
 */
export interface HttpInit {
  method?: string
  headers?: Record<string, string>
  body?: string
}

export interface HttpResponse {
  status: number
  ok: boolean
  headers: Record<string, string>
  text: string
}

export interface EngineEffects {
  env: { get(name: string): Promise<string | undefined> }
  http: { fetch(url: string, init?: HttpInit): Promise<HttpResponse> }
  session: { id(): Promise<string>; usage(): Promise<SessionUsageReading> }
  clock: { now(): Promise<number> }
}

/** Plain $.session.usage() only. No token-count/breakdown API is requested. */
export interface SessionUsageReading {
  context: { tokens?: number; window: number; percent?: number }
  rateLimits: { kind: string; percentUsed: number; resetsAt?: string }[]
  cost?: { usd: number }
}

interface Events {
  'session.start': { input: { cwd: string; surface: 'terminal' | null; isInteractive: boolean }; output: { cwd: string } }
  'turn.start': { input: { text: string; turnId: string }; output: unknown }
  'turn.complete': { input: { answer: string; durationMs: number; isAborted: boolean; turnId: string; agentId?: string; reason: 'answer' | 'aborted' | 'refusal' | 'error'; usage?: { model: string } }; output: { text: string; usage?: unknown } }
  'session.compact': { input: { trigger: string; agentId?: string; [key: string]: unknown }; output: unknown }
  'tool.call': { input: { tool: string; tool_use_id?: string; agentId?: string; [key: string]: unknown }; output: unknown }
  'tool.check': { input: { tool: string; tool_use_id?: string; input: unknown }; output: { decision: 'allow' | 'ask' | 'deny'; reason?: string; rule?: string } }
  'agent.spawn': {
    input: { tool_use_id: string; prompt: string; description: string; subagentType: string; parentModel: string; parentAgentId?: string; model?: string; name?: string; background: boolean; fork: boolean; [key: string]: unknown }
    output: { model: string; agentId?: string; deny?: undefined } | { deny: string; model?: undefined; agentId?: undefined }
  }
}

export type On = <K extends keyof Events>(
  event: K,
  handler: ($: EngineEffects, event: Readonly<Events[K]['input']>, next: (event: Readonly<Events[K]['input']>) => Promise<Events[K]['output']>) => Promise<Events[K]['output']>,
) => void

export type Register = (on: On, options: Readonly<Record<string, string | number | boolean | readonly string[]>>) => unknown
