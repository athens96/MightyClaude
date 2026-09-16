import { createHash } from 'node:crypto'
import type { LogEntry } from '../../shared/types'
import { isIdentifier, isRecord } from './validation'

const MAX_LINE_LENGTH = 1024 * 1024

/** Claude's completed assistant messages are used; partial stream events are ignored. */
export class ClaudeStreamParser {
  private buffer = ''
  private droppingLine = false
  private readonly seen = new Set<string>()
  private assistantSeen = false
  private lastResumeId: string | undefined
  failed = false

  constructor(
    private readonly log: (kind: LogEntry['kind'], text: string) => void,
    private readonly resume: (sessionId: string) => void,
  ) {}

  push(chunk: string): void {
    const lines = chunk.split('\n')
    for (let index = 0; index < lines.length; index++) {
      const part = lines[index]!
      if (!this.droppingLine) this.buffer += part
      if (this.buffer.length > MAX_LINE_LENGTH) {
        this.buffer = ''
        this.droppingLine = true
        this.log('system', '너무 긴 출력 한 줄을 생략했습니다.')
      }
      if (index < lines.length - 1) {
        if (!this.droppingLine) this.consume(this.buffer)
        this.buffer = ''
        this.droppingLine = false
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
    try { value = JSON.parse(line) } catch {
      this.log('output', line.slice(0, 32_768))
      return
    }
    if (!isRecord(value)) return
    if (isIdentifier(value.session_id) && value.session_id !== this.lastResumeId) {
      this.lastResumeId = value.session_id
      this.resume(value.session_id)
    }
    if (value.type === 'assistant' && isRecord(value.message) && Array.isArray(value.message.content)) {
      const message = value.message
      const content = (message.content as unknown[]).filter(isRecord).filter((block) => block.type === 'text' && typeof block.text === 'string').map((block) => block.text).join('\n')
      if (content) {
        // Different content blocks can share a Claude message ID. In particular,
        // a thinking block must not suppress the text block that follows it.
        const key = typeof value.uuid === 'string' ? value.uuid : `${String(message.id)}:${createHash('sha256').update(content).digest('hex')}`
        if (this.seen.has(key)) return
        this.seen.add(key)
        if (this.seen.size > 512) this.seen.delete(this.seen.values().next().value!)
        this.assistantSeen = true
        this.log('assistant', content)
      }
    } else if (value.type === 'result') {
      if (value.is_error === true || (typeof value.subtype === 'string' && value.subtype.startsWith('error'))) {
        this.failed = true
        const errors = Array.isArray(value.errors) ? value.errors.filter((error) => typeof error === 'string').join('\n') : ''
        this.log('error', errors || (typeof value.result === 'string' ? value.result : 'Claude Code 실행 중 오류가 발생했습니다.'))
      } else if (!this.assistantSeen && typeof value.result === 'string' && value.result) {
        this.assistantSeen = true
        this.log('assistant', value.result)
      }
    }
  }
}
