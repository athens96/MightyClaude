import { execFile } from 'node:child_process'
import { lookup as systemLookup } from 'node:dns/promises'
import { access } from 'node:fs/promises'
import { constants } from 'node:fs'
import { isIP } from 'node:net'
import { delimiter, join } from 'node:path'
import { promisify } from 'node:util'
import type { RemoteState } from '../../../shared/types'
import { isRecord } from '../validation'

const execFileAsync = promisify(execFile)
export type TailscaleInfo = RemoteState['tailscale'] & { peerAddresses?: string[] }
export type AddressLookup = (hostname: string) => Promise<{ address: string; family: number }[]>
export interface PinnedAddress { address: string; family: 4 | 6; port: number; host: string; origin: string }

/** Tailscale's documented CGNAT and unique-local address ranges. */
export function isTailscaleAddress(address: string): boolean {
  if (isIP(address) === 4) {
    const octets = address.split('.').map(Number)
    return octets[0] === 100 && octets[1]! >= 64 && octets[1]! <= 127 && address !== '100.100.100.100'
  }
  return isIP(address) === 6 && address.toLowerCase().startsWith('fd7a:115c:a1e0:')
}

export function isAllowedAddress(address: string, allowLoopback = false): boolean {
  return isTailscaleAddress(address) || (allowLoopback && (address === '127.0.0.1' || address === '::1'))
}

export function parseRemoteAddress(value: unknown): URL {
  if (typeof value !== 'string' || value.length > 2048 || value !== value.trim() || /[\s\\\u0000-\u001f]/.test(value)) throw new Error('Tailscale HTTP 주소를 입력해 주세요.')
  let url: URL
  try { url = new URL(value) } catch { throw new Error('주소는 http://Tailscale주소:포트 형식이어야 합니다.') }
  if (url.protocol !== 'http:' || url.username || url.password || url.search || url.hash || url.pathname !== '/') throw new Error('Tailscale HTTP 주소에는 사용자 정보, 경로, 쿼리를 넣을 수 없습니다.')
  const hostname = url.hostname.replace(/^\[|\]$/g, '')
  if (!hostname || hostname.includes('%')) throw new Error('Tailscale 주소가 올바르지 않습니다.')
  if (!isIP(hostname) && !/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.?$/i.test(hostname)) throw new Error('Tailscale IP 또는 MagicDNS 이름을 입력해 주세요.')
  return url
}

/** Resolve once, check every answer, then connect to the IP rather than resolving again. */
export async function pinRemoteAddress(value: unknown, allowLoopback = false, lookup: AddressLookup = (hostname) => systemLookup(hostname, { all: true, verbatim: true })): Promise<PinnedAddress> {
  const url = parseRemoteAddress(value)
  const hostname = url.hostname.replace(/^\[|\]$/g, '')
  const direct = isIP(hostname)
  let timeout: ReturnType<typeof setTimeout> | undefined
  const addresses = direct ? [{ address: hostname, family: direct }] : await Promise.race([
    lookup(hostname),
    new Promise<never>((_resolve, reject) => { timeout = setTimeout(() => reject(new Error('MagicDNS 주소 조회 시간이 초과되었습니다.')), 3000) }),
  ]).finally(() => clearTimeout(timeout))
  if (!addresses.length || addresses.some((entry) => !isAllowedAddress(entry.address, allowLoopback))) throw new Error('원격 연결은 Tailscale 장치 주소만 허용합니다. 공용·LAN·로컬 주소는 사용할 수 없습니다.')
  const chosen = addresses.find((entry) => isIP(entry.address) === 4) ?? addresses[0]!
  return { address: chosen.address, family: isIP(chosen.address) as 4 | 6, port: Number(url.port || 80), host: url.host, origin: url.origin }
}

export function tailscaleInfoFromStatus(value: unknown): TailscaleInfo {
  if (!isRecord(value) || value.BackendState !== 'Running') return { available: false, addresses: [], detail: 'Tailscale을 실행하고 로그인한 뒤 다시 확인해 주세요.' }
  const self = isRecord(value.Self) ? value.Self : {}
  const source = Array.isArray(self.TailscaleIPs) ? self.TailscaleIPs : Array.isArray(value.TailscaleIPs) ? value.TailscaleIPs : []
  const addresses = [...new Set(source.filter((address): address is string => typeof address === 'string' && isTailscaleAddress(address)))].slice(0, 8)
  const name = typeof self.HostName === 'string' ? self.HostName.replace(/[\u0000-\u001f\u007f]/g, '').slice(0, 120) : undefined
  const peerAddresses = isRecord(value.Peer) ? [...new Set(Object.values(value.Peer).flatMap((peer) => isRecord(peer) && Array.isArray(peer.TailscaleIPs) ? peer.TailscaleIPs.filter((address): address is string => typeof address === 'string' && isTailscaleAddress(address)) : []))].slice(0, 4096) : undefined
  return { available: addresses.length > 0, addresses, ...(name ? { deviceName: name } : {}), ...(peerAddresses ? { peerAddresses } : {}), detail: addresses.length ? 'Tailscale 장치 주소를 확인했습니다.' : '사용 가능한 Tailscale 장치 주소가 없습니다.' }
}

export async function discoverTailscale(): Promise<TailscaleInfo> {
  const names = process.platform === 'win32' ? ['tailscale.exe'] : ['tailscale']
  const candidates = [...new Set([
    ...(process.env.PATH ?? '').split(delimiter).filter(Boolean).flatMap((directory) => names.map((name) => join(directory, name))),
    ...(process.platform === 'darwin' ? ['/Applications/Tailscale.app/Contents/MacOS/Tailscale', '/usr/local/bin/tailscale', '/opt/homebrew/bin/tailscale'] : []),
    ...(process.platform === 'win32' ? [join(process.env.ProgramFiles || 'C:\\Program Files', 'Tailscale', 'tailscale.exe'), 'C:\\Program Files (x86)\\Tailscale\\tailscale.exe'] : []),
    ...(process.platform === 'linux' ? ['/usr/bin/tailscale', '/usr/local/bin/tailscale'] : []),
  ])]
  for (const binary of candidates) {
    try {
      await access(binary, process.platform === 'win32' ? constants.F_OK : constants.X_OK)
      const { stdout } = await execFileAsync(binary, ['status', '--json'], { timeout: 3500, maxBuffer: 1024 * 1024, windowsHide: true })
      return tailscaleInfoFromStatus(JSON.parse(stdout))
    } catch { /* Try another installed CLI. Never install, log in, or alter Tailscale. */ }
  }
  return { available: false, addresses: [], detail: 'Tailscale CLI를 찾거나 상태를 확인하지 못했습니다. Tailscale 설치·실행·로그인을 확인해 주세요.' }
}
