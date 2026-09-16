import { createHash } from 'node:crypto'
import type { LogEntry } from '../../shared/types'
import { isIdentifier, isRecord } from './validation'

export interface RunOutputParser { failed: boolean; push(chunk: string): void; flush(): void }
type Log = (kind: LogEntry['kind'], text: string) => void
const MAX_LINE_LENGTH = 1024 * 1024

/** Bound incomplete JSON lines while preserving arbitrary UTF-8 chunk splits. */
abstract class JsonLineParser implements RunOutputParser {
  private buffer = ''
  private droppingLine = false
  failed = false
  constructor(protected readonly log: Log, protected readonly resume: (id: string) => void) {}

  push(chunk: string): void {
    const lines = chunk.split('\n')
    for (let index = 0; index < lines.length; index++) {
      if (!this.droppingLine) this.buffer += lines[index]!
      if (this.buffer.length > MAX_LINE_LENGTH) {
        this.buffer = ''; this.droppingLine = true
        this.log('system', '너무 긴 출력 한 줄을 생략했습니다.')
      }
      if (index < lines.length - 1) {
        if (!this.droppingLine) this.consume(this.buffer)
        this.buffer = ''; this.droppingLine = false
      }
    }
  }

  flush(): void {
    if (this.buffer && !this.droppingLine) this.consume(this.buffer)
    this.buffer = ''
  }

  private consume(line: string): void {
    if (!line.trim()) return
    let value: unknown
    try { value = JSON.parse(line) } catch { this.log('output', line.slice(0, 32_768)); return }
    if (isRecord(value)) this.event(value)
  }
  protected abstract event(value: Record<string, unknown>): void
}

function errorText(value: unknown, fallback: string): string {
  if (typeof value === 'string' && value) return value.slice(0, 32_768)
  if (isRecord(value) && typeof value.message === 'string') return value.message.slice(0, 32_768)
  return fallback
}

export class CodexStreamParser extends JsonLineParser {
  private readonly seen = new Set<string>()
  private lastResume: string | undefined

  protected event(value: Record<string, unknown>): void {
    if (value.type === 'thread.started' && isIdentifier(value.thread_id) && value.thread_id !== this.lastResume) {
      this.lastResume = value.thread_id; this.resume(value.thread_id)
    } else if (value.type === 'item.completed' && isRecord(value.item)) {
      const item = value.item
      // Final item events contain the complete text; partial updates would
      // duplicate it. Reasoning items are intentionally not displayed.
      if (item.type === 'agent_message' && typeof item.text === 'string' && item.text) {
        const key = `${String(item.id)}:${createHash('sha256').update(item.text).digest('hex')}`
        if (this.seen.has(key)) return
        this.seen.add(key)
        if (this.seen.size > 512) this.seen.delete(this.seen.values().next().value!)
        this.log('assistant', item.text)
      } else if (item.type === 'command_execution') {
        if (typeof item.aggregated_output === 'string' && item.aggregated_output) this.log('output', item.aggregated_output)
        if (item.status === 'failed') this.log('system', 'Codex 명령 실행이 실패했습니다. CLI가 다음 단계를 처리합니다.')
      } else if (item.type === 'error') {
        this.log('error', errorText(item.message, 'Codex 작업 오류가 발생했습니다.'))
      }
    } else if (value.type === 'turn.failed' || value.type === 'error') {
      this.failed = true
      this.log('error', errorText(value.error ?? value.message, 'Codex 실행 중 오류가 발생했습니다.'))
    }
  }
}

export class GeminiStreamParser extends JsonLineParser {
  private pendingText = ''
  private lastResume: string | undefined

  private flushText(): void {
    if (this.pendingText) this.log('assistant', this.pendingText)
    this.pendingText = ''
  }
  override flush(): void { super.flush(); this.flushText() }

  protected event(value: Record<string, unknown>): void {
    if (value.type === 'init' && isIdentifier(value.session_id) && value.session_id !== this.lastResume) {
      this.lastResume = value.session_id; this.resume(value.session_id)
    } else if (value.type === 'message' && value.role === 'assistant' && typeof value.content === 'string') {
      if (value.delta === true) {
        this.pendingText += value.content
        if (this.pendingText.length >= 4096) this.flushText()
      } else {
        this.flushText()
        if (value.content) this.log('assistant', value.content)
      }
    } else if (value.type === 'tool_use') {
      this.flushText()
      if (typeof value.tool_name === 'string') this.log('system', `도구 실행 · ${value.tool_name.slice(0, 160)}`)
    } else if (value.type === 'tool_result') {
      this.flushText()
      if (typeof value.output === 'string' && value.output) this.log('output', value.output)
      if (value.status === 'error') this.log('system', errorText(value.error, 'Gemini 도구 실행이 실패했습니다.'))
    } else if (value.type === 'error') {
      this.flushText()
      if (value.severity !== 'warning') this.failed = true
      this.log(value.severity === 'warning' ? 'system' : 'error', errorText(value.message, 'Gemini 실행 중 오류가 발생했습니다.'))
    } else if (value.type === 'result') {
      this.flushText()
      if (value.status === 'error') {
        this.failed = true
        this.log('error', errorText(value.error, 'Gemini 실행 중 오류가 발생했습니다.'))
      }
    }
  }
}
