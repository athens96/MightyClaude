import { request } from 'node:http'
import { MAX_RESPONSE_BYTES, REMOTE_PROTOCOL, REMOTE_VERSION_HEADER, cleanText } from './protocol'
import type { PinnedAddress } from './tailscale'
import { isRecord } from '../validation'

/** No redirects, proxy environment, second DNS lookup, or reusable public socket. */
export async function remoteRequest(target: PinnedAddress, token: string, method: 'GET' | 'POST', path: string, body?: unknown, signal?: AbortSignal, timeoutMs = 10_000): Promise<Record<string, unknown>> {
  const encoded = body === undefined ? undefined : JSON.stringify(body)
  return new Promise((resolve, reject) => {
    const req = request({ hostname: target.address, family: target.family, port: target.port, method, path, agent: false, signal,
      headers: { host: target.host, authorization: `Bearer ${token}`, [REMOTE_VERSION_HEADER]: String(REMOTE_PROTOCOL), accept: 'application/json', ...(encoded === undefined ? {} : { 'content-type': 'application/json', 'content-length': Buffer.byteLength(encoded) }) },
    }, (res) => {
      const chunks: Buffer[] = []
      let size = 0
      res.on('data', (chunk: Buffer) => {
        size += chunk.length
        if (size > MAX_RESPONSE_BYTES) { req.destroy(new Error('원격 응답 크기 제한을 초과했습니다.')); res.destroy(); return }
        chunks.push(chunk)
      })
      res.once('error', reject)
      res.once('end', () => {
        try {
          const value: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8'))
          if (!isRecord(value) || value.protocol !== REMOTE_PROTOCOL) throw new Error('원격 MightyClaude 프로토콜이 호환되지 않습니다.')
          if (!res.statusCode || res.statusCode < 200 || res.statusCode >= 300) throw new Error(cleanText(value.error, 300, `원격 요청이 거부되었습니다 (${res.statusCode ?? 0}).`))
          resolve(value)
        } catch (error) { reject(error instanceof Error ? error : new Error('원격 응답을 읽지 못했습니다.')) }
      })
    })
    const timeout = setTimeout(() => req.destroy(new Error('원격 MightyClaude 응답 시간이 초과되었습니다.')), timeoutMs)
    req.once('close', () => clearTimeout(timeout))
    req.once('error', reject)
    req.end(encoded)
  })
}
