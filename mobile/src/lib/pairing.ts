import { fromBase64, toBase64, KEY_LENGTH } from '@/api/relay/crypto';

export const PAIRING_SCHEME = 'mightyclaude://pair';
export const PAIRING_VERSION = 2;

/** Shown when an older desktop build still emits the v1 Tailscale QR. */
export const LEGACY_PAIRING_ERROR = '이 QR은 이전 방식입니다. Mac 앱을 업데이트하세요.';

export interface PairingPayload {
  /** Relay routing key for this desktop. */
  serverId: string;
  /** `ws://host:port` or `wss://host:port`. */
  relayUrl: string;
  /** Host static X25519 public key, stored as standard base64. */
  hostPublicKeyB64: string;
  pairingKey: string;
  name: string;
}

export type PairingParseResult =
  | { ok: true; value: PairingPayload }
  | { ok: false; error: string };

const SERVER_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;

function readParams(raw: string): URLSearchParams | null {
  const trimmed = raw.trim();
  const queryStart = trimmed.indexOf('?');
  if (queryStart < 0) return null;
  const base = trimmed.slice(0, queryStart).toLowerCase();
  if (base !== PAIRING_SCHEME && base !== `${PAIRING_SCHEME}/`) return null;
  return new URLSearchParams(trimmed.slice(queryStart + 1));
}

/** Accepts `ws://`/`wss://` origins only; a bare host defaults to `wss://`. */
export function normalizeRelayUrl(raw: string): string | undefined {
  const trimmed = raw.trim().replace(/\/+$/, '');
  if (!trimmed) return undefined;
  const withScheme = /^wss?:\/\//i.test(trimmed) ? trimmed : `wss://${trimmed}`;
  const match = /^(wss?):\/\/([^/?#]+)$/i.exec(withScheme);
  if (!match) return undefined;
  return `${match[1]?.toLowerCase()}://${match[2]}`;
}

/**
 * Parses `mightyclaude://pair?v=2&sid=…&pk=…&relay=…&key=…&name=…`.
 * Pure: safe to unit-test without native modules. v1 strings are rejected.
 */
export function parsePairingUrl(raw: string): PairingParseResult {
  const params = readParams(raw);
  if (!params) return { ok: false, error: '페어링 주소 형식이 아닙니다.' };

  const version = params.get('v');
  if (version === '1' || (version === null && params.has('host'))) {
    return { ok: false, error: LEGACY_PAIRING_ERROR };
  }
  if (version !== String(PAIRING_VERSION)) {
    return { ok: false, error: `지원하지 않는 페어링 버전입니다 (v=${version ?? '없음'}).` };
  }

  const serverId = (params.get('sid') ?? '').trim();
  if (!serverId) return { ok: false, error: '서버 ID가 없습니다.' };
  if (!SERVER_ID_PATTERN.test(serverId)) {
    return { ok: false, error: '서버 ID 형식이 올바르지 않습니다.' };
  }

  const relayRaw = (params.get('relay') ?? '').trim();
  if (!relayRaw) return { ok: false, error: '릴레이 주소가 없습니다.' };
  const relayUrl = normalizeRelayUrl(relayRaw);
  if (!relayUrl) return { ok: false, error: `릴레이 주소가 올바르지 않습니다 (${relayRaw}).` };

  const publicKeyRaw = (params.get('pk') ?? '').trim();
  if (!publicKeyRaw) return { ok: false, error: '호스트 공개키가 없습니다.' };
  let hostPublicKeyB64: string;
  try {
    const bytes = fromBase64(publicKeyRaw);
    if (bytes.length !== KEY_LENGTH) {
      return { ok: false, error: `호스트 공개키 길이가 올바르지 않습니다 (${bytes.length}B).` };
    }
    hostPublicKeyB64 = toBase64(bytes);
  } catch {
    return { ok: false, error: '호스트 공개키를 해석할 수 없습니다.' };
  }

  const pairingKey = (params.get('key') ?? '').trim();
  if (!pairingKey) return { ok: false, error: '페어링 키가 없습니다.' };

  const name = (params.get('name') ?? '').trim() || serverId;
  return { ok: true, value: { serverId, relayUrl, hostPublicKeyB64, pairingKey, name } };
}

/** Builds the canonical v2 pairing string for a payload. */
export function formatPairingUrl(payload: PairingPayload): string {
  const params = new URLSearchParams();
  params.set('v', String(PAIRING_VERSION));
  params.set('sid', payload.serverId);
  params.set('pk', toBase64Url(payload.hostPublicKeyB64));
  params.set('relay', payload.relayUrl);
  params.set('key', payload.pairingKey);
  params.set('name', payload.name);
  return `${PAIRING_SCHEME}?${params.toString()}`;
}

function toBase64Url(standardBase64: string): string {
  return standardBase64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * Whether a relay address points inside the local network, which is the only place
 * iOS still allows plain `ws://`: App Transport Security is configured with
 * `NSAllowsLocalNetworking` alone, so loopback, the private IPv4 ranges, link-local
 * addresses and Bonjour names keep working while a public `ws://` relay does not.
 *
 * Pure string work: no DNS, no sockets. A name that is not obviously local is treated
 * as remote, because guessing the other way would hide the failure until connect time.
 */
export function isLocalRelayHost(relayUrl: string): boolean {
  const host = hostnameOf(relayUrl);
  if (!host) return false;
  if (host === 'localhost' || host.endsWith('.localhost')) return true;
  if (host.endsWith('.local')) return true;

  const ipv4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  if (ipv4) {
    const parts = ipv4.slice(1).map((part) => Number(part));
    if (parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) return false;
    const [a = 0, b = 0] = parts;
    if (a === 127) return true;
    if (a === 10) return true;
    if (a === 192 && b === 168) return true;
    if (a === 172 && b >= 16 && b <= 31) return true;
    // Link-local (169.254.0.0/16), which is what two devices pick with no DHCP.
    if (a === 169 && b === 254) return true;
    return false;
  }

  if (host.includes(':')) {
    // IPv6: loopback, link-local (fe80::/10) and unique-local (fc00::/7).
    const plain = host.replace(/%.*$/, '');
    if (plain === '::1') return true;
    if (/^fe[89ab][0-9a-f]?:/.test(plain)) return true;
    if (/^f[cd][0-9a-f]{2}:/.test(plain)) return true;
    return false;
  }
  return false;
}

/** The host part of `ws://host:port`, lowercased, with an IPv6 literal unbracketed. */
function hostnameOf(relayUrl: string): string | undefined {
  const match = /^wss?:\/\/([^/?#]+)$/i.exec(relayUrl.trim().replace(/\/+$/, ''));
  const authority = match?.[1];
  if (!authority) return undefined;
  const bracketed = /^\[([^\]]+)\](?::\d+)?$/.exec(authority);
  if (bracketed?.[1]) return bracketed[1].toLowerCase();
  const withoutPort = authority.replace(/:\d+$/, '');
  return withoutPort.length > 0 ? withoutPort.toLowerCase() : undefined;
}

/**
 * The reason a relay cannot be reached from this phone, or undefined when it can. Only
 * iOS refuses plain `ws://` outside the local network; Android keeps its cleartext
 * allowance, so the same link still pairs there.
 */
export function relayTransportError(
  relayUrl: string,
  platform: string,
): string | undefined {
  if (platform !== 'ios') return undefined;
  if (!/^ws:\/\//i.test(relayUrl.trim())) return undefined;
  if (isLocalRelayHost(relayUrl)) return undefined;
  return 'iOS에서는 로컬 네트워크 밖의 릴레이에 ws://로 연결할 수 없습니다. Mac에서 릴레이 주소를 wss://로 바꾼 뒤 다시 페어링하세요.';
}

/** Stable identity for a paired host: the relay `serverId`. */
export function pairingFingerprint(serverId: string): string {
  return serverId;
}

/** Human-readable address for the hosts list (`relay.example.com` + short id). */
export function describeRelayTarget(relayUrl: string, serverId: string): string {
  return `${relayUrl.replace(/^wss?:\/\//i, '')} · ${serverId}`;
}
