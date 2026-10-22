import { chacha20poly1305 } from '@noble/ciphers/chacha';
import { hkdf } from '@noble/hashes/hkdf';
import { sha256 } from '@noble/hashes/sha256';

import { fromBase64, utf8Encode } from '@/api/relay/crypto';

/**
 * Opening a host's push (docs/mobile-remote.md "푸시 알림", native/contracts/README.md
 * "Push notifications"). The key comes from this phone's device token for that host:
 * HKDF-SHA256(SHA-256(token), salt clientId, info "mighty-push-v1"). The host keeps exactly
 * that SHA-256, so neither side needs a new secret, and the relay and FCM only ever carry
 * the envelope: base64url(0x01 ‖ nonce(12) ‖ ciphertext ‖ tag(16)), AAD "mighty-push-v1:" + handle.
 */
export const PUSH_INFO = 'mighty-push-v1';
export const PUSH_VERSION = 1;
export const PUSH_ENVELOPE_MAX = 3072;
const NONCE_LENGTH = 12;
const TAG_LENGTH = 16;
const ENVELOPE = /^[A-Za-z0-9_-]+$/;

export function pushKey(deviceToken: string, clientId: string): Uint8Array {
  return hkdf(sha256, sha256(utf8Encode(deviceToken)), utf8Encode(clientId), utf8Encode(PUSH_INFO), 32);
}

/** The plaintext, or null for anything that is not this host's envelope for this handle. */
export function openPush(envelope: string, key: Uint8Array, handle: string): Uint8Array | null {
  if (envelope.length > PUSH_ENVELOPE_MAX || !ENVELOPE.test(envelope)) return null;
  let bytes: Uint8Array;
  try {
    bytes = fromBase64(envelope);
  } catch {
    return null;
  }
  if (bytes.length < 1 + NONCE_LENGTH + TAG_LENGTH || bytes[0] !== PUSH_VERSION) return null;
  try {
    const nonce = bytes.subarray(1, 1 + NONCE_LENGTH);
    return chacha20poly1305(key, nonce, utf8Encode(`${PUSH_INFO}:${handle}`)).decrypt(bytes.subarray(1 + NONCE_LENGTH));
  } catch {
    return null;
  }
}
