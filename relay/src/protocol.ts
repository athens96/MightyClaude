/** Wire-level constants and query validation for `GET /ws`. */

export const WS_PATH = '/ws';
export const PROTOCOL_VERSION = '1';

export const SERVER_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
export const CONNECTION_ID_PATTERN = /^[A-Za-z0-9-]{8,64}$/;

/** Close codes used by the relay (see docs/relay.md, "릴레이 와이어"). */
export const CloseCode = {
  /** Normal closure. */
  normal: 1000,
  /** Malformed or missing query parameters. */
  badRequest: 4400,
  /** No host control socket, or unknown/expired connectionId. */
  notFound: 4404,
  /** Superseded by a newer host control socket. */
  conflict: 4409,
  /** Host control socket went away. */
  hostOffline: 4410,
  /** Client sent more frames than the relay buffers while the host attaches. */
  bufferOverflow: 4413,
  /** Per-serverId connection cap reached. */
  tooManyConnections: 4429,
  /** Host data socket did not attach in time. */
  attachTimeout: 4504,
} as const;

export type CloseCodeValue = (typeof CloseCode)[keyof typeof CloseCode];

export type SocketRole = 'server' | 'client';

/** Host control socket, host data socket, or client data socket. */
export type SocketKind = 'control' | 'host-data' | 'client-data';

/** A validated `GET /ws` query. */
export interface SocketParams {
  readonly kind: SocketKind;
  readonly serverId: string;
  /** Present for every kind except `control`. */
  readonly connectionId: string | null;
}

/** JSON notifications the relay pushes to the host control socket. */
export interface ControlNotice {
  readonly type: 'connected' | 'disconnected';
  readonly connectionId: string;
}

export type ParseResult =
  | { readonly ok: true; readonly params: SocketParams }
  | { readonly ok: false; readonly reason: string };

/** Validates the `GET /ws` query string; failures must be answered with close code 4400. */
export function parseSocketParams(query: URLSearchParams): ParseResult {
  const version = query.get('v');
  if (version !== PROTOCOL_VERSION) return { ok: false, reason: 'bad v' };

  const serverId = query.get('serverId');
  if (serverId === null || !SERVER_ID_PATTERN.test(serverId)) {
    return { ok: false, reason: 'bad serverId' };
  }

  const role = query.get('role');
  if (role !== 'server' && role !== 'client') return { ok: false, reason: 'bad role' };

  const connectionId = query.get('connectionId');
  if (connectionId !== null && !CONNECTION_ID_PATTERN.test(connectionId)) {
    return { ok: false, reason: 'bad connectionId' };
  }

  if (role === 'client') {
    if (connectionId === null) return { ok: false, reason: 'missing connectionId' };
    return { ok: true, params: { kind: 'client-data', serverId, connectionId } };
  }

  if (connectionId === null) {
    return { ok: true, params: { kind: 'control', serverId, connectionId: null } };
  }
  return { ok: true, params: { kind: 'host-data', serverId, connectionId } };
}

/**
 * WebSocket close codes are restricted on the wire; anything a peer cannot legally
 * send back (1005/1006 and friends) collapses to a normal closure.
 */
export function sendableCloseCode(code: number): number {
  if (code === 1000 || (code >= 3000 && code <= 4999)) return code;
  if (code >= 1001 && code <= 1014 && code !== 1004 && code !== 1005 && code !== 1006) return code;
  return CloseCode.normal;
}

/** Close reasons are capped at 123 bytes by RFC 6455. */
export function truncateReason(reason: string): string {
  let out = reason;
  while (Buffer.byteLength(out, 'utf8') > 123) out = out.slice(0, -1);
  return out;
}
