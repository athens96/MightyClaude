/** WebCrypto twins of the Node relay's hashing (../src/protocol.ts, ../src/push-store.ts). */

function hex(bytes: ArrayBuffer): string {
  return [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export async function sha256Hex(value: string): Promise<string> {
  return hex(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)));
}

/** Same rule as `ownsServerId`: the serverId is the lowercase hex SHA-256 of the UTF-8 hostToken. */
export async function ownsServerIdAsync(serverId: string, hostToken: string | null): Promise<boolean> {
  if (hostToken === null) return false;
  return (await sha256Hex(hostToken)) === serverId;
}

/** 32 random bytes, base64url without padding (`newPushSecret`). */
export function newSecret(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** Constant-time comparison of two equal-length hex digests. */
export function sameDigest(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
