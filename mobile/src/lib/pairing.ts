import { DEFAULT_PORT } from '@/api/types';

export const PAIRING_SCHEME = 'mightyclaude://pair';

export interface PairingPayload {
  host: string;
  port: number;
  key: string;
  name: string;
}

export type PairingParseResult =
  | { ok: true; value: PairingPayload }
  | { ok: false; error: string };

function readParams(raw: string): URLSearchParams | null {
  const trimmed = raw.trim();
  const queryStart = trimmed.indexOf('?');
  if (queryStart < 0) return null;
  const base = trimmed.slice(0, queryStart).toLowerCase();
  if (base !== PAIRING_SCHEME && base !== `${PAIRING_SCHEME}/`) return null;
  return new URLSearchParams(trimmed.slice(queryStart + 1));
}

/**
 * Parses a `mightyclaude://pair?v=1&host=…&port=…&key=…&name=…` pairing string.
 * Pure: safe to unit-test without native modules.
 */
export function parsePairingUrl(raw: string): PairingParseResult {
  const params = readParams(raw);
  if (!params) return { ok: false, error: '페어링 주소 형식이 아닙니다.' };

  const version = params.get('v');
  if (version !== null && version !== '1') {
    return { ok: false, error: `지원하지 않는 페어링 버전입니다 (v=${version}).` };
  }

  const host = (params.get('host') ?? '').trim();
  if (!host) return { ok: false, error: '호스트 주소가 없습니다.' };

  const key = (params.get('key') ?? '').trim();
  if (!key) return { ok: false, error: '페어링 키가 없습니다.' };

  const portRaw = params.get('port');
  const port = portRaw === null || portRaw.trim() === '' ? DEFAULT_PORT : Number(portRaw);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    return { ok: false, error: `포트 번호가 올바르지 않습니다 (${portRaw}).` };
  }

  const name = (params.get('name') ?? '').trim() || host;
  return { ok: true, value: { host, port, key, name } };
}

/** Builds the canonical pairing string for a payload. */
export function formatPairingUrl(payload: PairingPayload): string {
  const params = new URLSearchParams();
  params.set('v', '1');
  params.set('host', payload.host);
  params.set('port', String(payload.port));
  params.set('key', payload.key);
  params.set('name', payload.name);
  return `${PAIRING_SCHEME}?${params.toString()}`;
}

/** `http://host:port`, bracketing bare IPv6 literals. */
export function buildBaseUrl(host: string, port: number): string {
  const needsBrackets = host.includes(':') && !host.startsWith('[');
  return `http://${needsBrackets ? `[${host}]` : host}:${port}`;
}

/** Stable identity for a paired host before `/m1/info` reports its real hostId. */
export function pairingFingerprint(host: string, port: number): string {
  return `${host.toLowerCase()}:${port}`;
}
