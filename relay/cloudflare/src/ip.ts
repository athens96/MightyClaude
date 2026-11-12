/** `pushIpKey` of ../src/push.ts without node:net. */

/** The eight 16-bit groups of a valid IPv6 address (zone already removed), or null. */
function ipv6Groups(ip: string): number[] | null {
  let text = ip;
  const lastColon = text.lastIndexOf(':');
  const tail = text.slice(lastColon + 1);
  if (tail.includes('.')) {
    const octets = tail.split('.').map(Number);
    if (octets.length !== 4 || octets.some((o) => !Number.isInteger(o) || o < 0 || o > 255)) return null;
    const [a = 0, b = 0, c = 0, d = 0] = octets;
    text = `${text.slice(0, lastColon + 1)}${((a << 8) | b).toString(16)}:${((c << 8) | d).toString(16)}`;
  }
  const halves = text.split('::');
  if (halves.length > 2) return null;
  const parse = (part: string): number[] => (part === '' ? [] : part.split(':').map((g) => (/^[0-9A-Fa-f]{1,4}$/.test(g) ? Number.parseInt(g, 16) : Number.NaN)));
  const head = parse(halves[0] ?? '');
  const rest = halves.length === 2 ? parse(halves[1] ?? '') : [];
  const fill = halves.length === 2 ? 8 - head.length - rest.length : 0;
  if (fill < 0) return null;
  const groups = [...head, ...new Array<number>(fill).fill(0), ...rest];
  return groups.length === 8 && groups.every((g) => Number.isInteger(g) && g >= 0 && g <= 0xffff) ? groups : null;
}

/**
 * The key push limits count a source under: IPv4 as is, IPv6 by its /64 (one subscriber
 * usually holds a whole /64), and an IPv4-mapped IPv6 address as its IPv4 address.
 */
export function pushIpKey(ip: string): string {
  const bare = ip.split('%')[0] ?? ip;
  if (!bare.includes(':')) return ip;
  const groups = ipv6Groups(bare);
  if (groups === null) return ip;
  const [g0, g1, g2, g3, g4, g5, g6 = 0, g7 = 0] = groups;
  if (g0 === 0 && g1 === 0 && g2 === 0 && g3 === 0 && g4 === 0 && g5 === 0xffff) {
    return `${g6 >> 8}.${g6 & 0xff}.${g7 >> 8}.${g7 & 0xff}`;
  }
  return `${groups.slice(0, 4).map((g) => g.toString(16)).join(':')}::/64`;
}
