/**
 * The direct path (docs/relay.md, "Tailscale 직접 연결"): a Mac on the same tailnet as
 * the phone listens on its Tailscale addresses, and the phone opens its data socket
 * there instead of at the relay. Only the transport changes; the handshake, the
 * encryption and the auth are the relay's, word for word.
 *
 * An endpoint is `host:port` (an IPv6 literal in brackets). The phone only ever dials
 * hosts inside Tailscale's ranges — 100.64.0.0/10, fd7a:115c:a1e0::/48 or a MagicDNS
 * name under `ts.net` — whatever a QR or a host says, which is also the whole of what
 * the app's cleartext exceptions cover.
 */

/** Which way a tunnel reached the Mac. */
export type ConnectionRoute = 'direct' | 'relay';

/** At most this many endpoints are kept per host. */
export const MAX_DIRECT_ENDPOINTS = 8;

/** 100.64.0.0/10. */
export function isTailscaleIPv4(host: string): boolean {
  const match = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  if (!match) return false;
  const texts = match.slice(1);
  // A leading zero is octal to some parsers; refuse it rather than guess.
  if (texts.some((part) => part.length > 1 && part.startsWith('0'))) return false;
  const parts = texts.map((part) => Number(part));
  if (parts.some((part) => part > 255)) return false;
  const [a = 0, b = 0] = parts;
  return a === 100 && b >= 64 && b <= 127;
}

/** The eight 16-bit groups of an IPv6 literal, or undefined when it is not one. */
function ipv6Groups(host: string): number[] | undefined {
  const text = host.toLowerCase();
  if (!/^[0-9a-f:]+$/.test(text) || text.length > 39) return undefined;
  const halves = text.split('::');
  if (halves.length > 2) return undefined;
  const read = (part: string | undefined): number[] | undefined => {
    if (!part) return [];
    const groups = part.split(':');
    if (groups.some((group) => !/^[0-9a-f]{1,4}$/.test(group))) return undefined;
    return groups.map((group) => parseInt(group, 16));
  };
  const head = read(halves[0]);
  const tail = halves.length === 2 ? read(halves[1]) : [];
  if (!head || !tail) return undefined;
  if (halves.length === 1) return head.length === 8 ? head : undefined;
  const missing = 8 - head.length - tail.length;
  if (missing < 1) return undefined;
  return [...head, ...new Array<number>(missing).fill(0), ...tail];
}

/** fd7a:115c:a1e0::/48. */
export function isTailscaleIPv6(host: string): boolean {
  const groups = ipv6Groups(host);
  return groups !== undefined && groups[0] === 0xfd7a && groups[1] === 0x115c && groups[2] === 0xa1e0;
}

/** A MagicDNS name: lowercase DNS labels under `ts.net`. */
export function isTailnetName(host: string): boolean {
  return (
    host.length <= 253 &&
    /^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+ts\.net$/.test(host)
  );
}

/** Splits `host:port` / `[v6]:port`; undefined when it is neither. */
export function splitEndpoint(endpoint: string): { host: string; port: number } | undefined {
  const bracketed = /^\[([^\]]+)\]:(\d{1,5})$/.exec(endpoint);
  const plain = bracketed ? undefined : /^([^:[\]]+):(\d{1,5})$/.exec(endpoint);
  const match = bracketed ?? plain;
  if (!match?.[1] || !match[2]) return undefined;
  const port = Number(match[2]);
  if (!Number.isInteger(port) || port < 1 || port > 65535) return undefined;
  return { host: match[1], port };
}

/**
 * The endpoint in the one spelling this app stores, or undefined when it is not a
 * Tailscale endpoint at all.
 */
export function normalizeDirectEndpoint(raw: string): string | undefined {
  const text = raw.trim().toLowerCase();
  if (text.length === 0 || text.length > 270) return undefined;
  const parts = splitEndpoint(text);
  if (!parts) return undefined;
  if (isTailscaleIPv4(parts.host) || isTailnetName(parts.host)) return `${parts.host}:${parts.port}`;
  if (isTailscaleIPv6(parts.host)) return `[${parts.host}]:${parts.port}`;
  return undefined;
}

/**
 * Keeps the Tailscale endpoints of a list, in order, without repeats and at most
 * `MAX_DIRECT_ENDPOINTS` of them. Anything that is not a list of strings reads as none.
 */
export function sanitizeDirectEndpoints(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  const kept: string[] = [];
  for (const entry of value) {
    if (typeof entry !== 'string') continue;
    const endpoint = normalizeDirectEndpoint(entry);
    if (endpoint && !kept.includes(endpoint)) kept.push(endpoint);
    if (kept.length >= MAX_DIRECT_ENDPOINTS) break;
  }
  return kept;
}

/** The QR's `direct=` value (already percent-decoded): comma-separated endpoints. */
export function parseDirectParam(raw: string | null | undefined): string[] {
  if (!raw) return [];
  return sanitizeDirectEndpoints(raw.split(','));
}

/** Whether two endpoint lists say the same thing. */
export function sameEndpoints(a: readonly string[] | undefined, b: readonly string[] | undefined): boolean {
  const left = a ?? [];
  const right = b ?? [];
  return left.length === right.length && left.every((entry, index) => entry === right[index]);
}

/**
 * Whether the phone dials this endpoint: Tailscale address literals only. A MagicDNS
 * name is kept for showing, never raced, because without a Tailscale route on the phone
 * it would be resolved through the public DNS of whatever network the phone is on.
 */
export function isDialableDirectEndpoint(endpoint: string): boolean {
  const parts = splitEndpoint(endpoint);
  return parts !== undefined && (isTailscaleIPv4(parts.host) || isTailscaleIPv6(parts.host));
}
