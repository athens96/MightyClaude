import { chacha20poly1305 } from '@noble/ciphers/chacha';
import { x25519 } from '@noble/curves/ed25519';
import { hkdf } from '@noble/hashes/hkdf';
import { sha256 } from '@noble/hashes/sha256';
import { randomBytes } from '@/api/relay/random';

/** HKDF `info` string fixed by docs/relay.md. */
export const HKDF_INFO = 'mightyclaude-relay-v1';
/** Frame nonce direction byte for client → host traffic. */
export const DIRECTION_CLIENT_TO_HOST = 0x01;
/** Frame nonce direction byte for host → client traffic. */
export const DIRECTION_HOST_TO_CLIENT = 0x02;
export const NONCE_LENGTH = 12;
export const KEY_LENGTH = 32;
/** Handshake `nonce` field length in bytes. */
export const HANDSHAKE_NONCE_LENGTH = 16;
/** ChaCha20-Poly1305 authentication tag length. */
export const TAG_LENGTH = 16;

export class RelayCryptoError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'RelayCryptoError';
  }
}

const STANDARD_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const URL_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

/** `=`, as a char code. */
const PAD_CODE = 61;
/**
 * Characters handed to one `String.fromCharCode` call. Growing a string one character
 * at a time costs a fresh allocation per chunk of a 5 MB attachment (≈7 M appends);
 * building char codes and joining a few thousand at a time keeps that to a handful.
 */
const ENCODE_BATCH = 4096;

function encodeBase64With(bytes: Uint8Array, alphabet: string, pad: boolean): string {
  const parts: string[] = [];
  const codes: number[] = [];
  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i] ?? 0;
    const b1 = bytes[i + 1];
    const b2 = bytes[i + 2];
    const triple = (b0 << 16) | ((b1 ?? 0) << 8) | (b2 ?? 0);
    codes.push(alphabet.charCodeAt((triple >> 18) & 63));
    codes.push(alphabet.charCodeAt((triple >> 12) & 63));
    if (b1 === undefined) {
      if (pad) codes.push(PAD_CODE);
    } else {
      codes.push(alphabet.charCodeAt((triple >> 6) & 63));
    }
    if (b2 === undefined) {
      if (pad) codes.push(PAD_CODE);
    } else {
      codes.push(alphabet.charCodeAt(triple & 63));
    }
    if (codes.length >= ENCODE_BATCH) {
      parts.push(String.fromCharCode(...codes));
      codes.length = 0;
    }
  }
  if (codes.length > 0) parts.push(String.fromCharCode(...codes));
  return parts.length === 1 ? (parts[0] ?? '') : parts.join('');
}

/** Standard base64 with `=` padding. */
export function toBase64(bytes: Uint8Array): string {
  return encodeBase64With(bytes, STANDARD_ALPHABET, true);
}

/** URL-safe base64 without padding (`-`/`_`, no `=`). */
export function toBase64Url(bytes: Uint8Array): string {
  return encodeBase64With(bytes, URL_ALPHABET, false);
}

/** Decodes both the standard and URL-safe alphabets, with or without padding. */
export function fromBase64(text: string): Uint8Array {
  const clean = text.trim().replace(/[=\s]/g, '');
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const char of clean) {
    let value = STANDARD_ALPHABET.indexOf(char);
    if (value < 0) value = URL_ALPHABET.indexOf(char);
    if (value < 0) throw new RelayCryptoError(`base64 문자열이 아닙니다: ${text}`);
    buffer = (buffer << 6) | value;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  return Uint8Array.from(bytes);
}

export function utf8Encode(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

export function utf8Decode(bytes: Uint8Array): string {
  return new TextDecoder().decode(bytes);
}

function concatBytes(...parts: Uint8Array[]): Uint8Array {
  const total = parts.reduce((sum, part) => sum + part.length, 0);
  const out = new Uint8Array(total);
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

function isAllZero(bytes: Uint8Array): boolean {
  for (const byte of bytes) {
    if (byte !== 0) return false;
  }
  return true;
}

/** Constant-time-ish equality for short public keys. */
export function bytesEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i += 1) diff |= (a[i] ?? 0) ^ (b[i] ?? 0);
  return diff === 0;
}

export interface RelayKeyPair {
  publicKey: Uint8Array;
  secretKey: Uint8Array;
}

/** A fresh ephemeral X25519 key pair for one connection. */
export function generateKeyPair(): RelayKeyPair {
  const secretKey = randomBytes(KEY_LENGTH);
  return { secretKey, publicKey: x25519.getPublicKey(secretKey) };
}

/** The 16-byte handshake nonce each side contributes to the HKDF salt. */
export function generateHandshakeNonce(): Uint8Array {
  return randomBytes(HANDSHAKE_NONCE_LENGTH);
}

/** RFC 4122 v4 UUID for the relay `connectionId` query parameter. */
export function randomUuid(): string {
  const bytes = randomBytes(16);
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export interface DeriveKeyInput {
  secretKey: Uint8Array;
  peerPublicKey: Uint8Array;
  clientNonce: Uint8Array;
  serverNonce: Uint8Array;
}

/**
 * `key = HKDF-SHA256(ikm = X25519(secret, peer), salt = clientNonce‖serverNonce,
 * info = "mightyclaude-relay-v1", 32B)`. Rejects an all-zero shared secret.
 */
export function deriveSessionKey(input: DeriveKeyInput): Uint8Array {
  let shared: Uint8Array;
  try {
    shared = x25519.getSharedSecret(input.secretKey, input.peerPublicKey);
  } catch {
    throw new RelayCryptoError('공유 비밀을 만들 수 없습니다 (잘못된 공개키).');
  }
  if (isAllZero(shared)) {
    throw new RelayCryptoError('공유 비밀이 0입니다 (잘못된 공개키).');
  }
  const salt = concatBytes(input.clientNonce, input.serverNonce);
  return hkdf(sha256, shared, salt, utf8Encode(HKDF_INFO), KEY_LENGTH);
}

/** `[direction 1B][0,0,0][counter 8B big-endian]`. */
export function buildNonce(direction: number, counter: bigint): Uint8Array {
  if (counter < 0n || counter > 0xffffffffffffffffn) {
    throw new RelayCryptoError('프레임 카운터 범위를 벗어났습니다.');
  }
  const nonce = new Uint8Array(NONCE_LENGTH);
  nonce[0] = direction;
  new DataView(nonce.buffer).setBigUint64(4, counter, false);
  return nonce;
}

/** Reads back a nonce produced by `buildNonce`, validating the reserved bytes. */
export function parseNonce(nonce: Uint8Array): { direction: number; counter: bigint } {
  if (nonce.length !== NONCE_LENGTH) throw new RelayCryptoError('nonce 길이가 잘못되었습니다.');
  if ((nonce[1] ?? 0) !== 0 || (nonce[2] ?? 0) !== 0 || (nonce[3] ?? 0) !== 0) {
    throw new RelayCryptoError('nonce 예약 바이트가 0이 아닙니다.');
  }
  const view = new DataView(nonce.buffer, nonce.byteOffset, nonce.byteLength);
  return { direction: nonce[0] ?? 0, counter: view.getBigUint64(4, false) };
}

/**
 * Seals and opens `[12B nonce][ChaCha20-Poly1305 ciphertext+tag]` frames for one
 * direction pair. Receive counters must strictly increase, so replayed or reordered
 * frames are rejected.
 */
export class RelayCipher {
  private sendCounter = 0n;
  private lastReceivedCounter: bigint | undefined;

  constructor(
    private readonly key: Uint8Array,
    private readonly sendDirection: number,
    private readonly receiveDirection: number,
  ) {
    if (key.length !== KEY_LENGTH) throw new RelayCryptoError('세션 키 길이가 잘못되었습니다.');
  }

  seal(plaintext: Uint8Array): Uint8Array {
    const nonce = buildNonce(this.sendDirection, this.sendCounter);
    this.sendCounter += 1n;
    const sealed = chacha20poly1305(this.key, nonce).encrypt(plaintext);
    return concatBytes(nonce, sealed);
  }

  sealJson(value: unknown): Uint8Array {
    return this.seal(utf8Encode(JSON.stringify(value)));
  }

  open(frame: Uint8Array): Uint8Array {
    if (frame.length < NONCE_LENGTH + TAG_LENGTH) {
      throw new RelayCryptoError('프레임이 너무 짧습니다.');
    }
    const { direction, counter } = parseNonce(frame.slice(0, NONCE_LENGTH));
    if (direction !== this.receiveDirection) {
      throw new RelayCryptoError('프레임 방향이 잘못되었습니다.');
    }
    if (this.lastReceivedCounter !== undefined && counter <= this.lastReceivedCounter) {
      throw new RelayCryptoError('프레임 카운터가 증가하지 않았습니다 (재전송).');
    }
    const nonce = frame.slice(0, NONCE_LENGTH);
    let plaintext: Uint8Array;
    try {
      plaintext = chacha20poly1305(this.key, nonce).decrypt(frame.slice(NONCE_LENGTH));
    } catch {
      throw new RelayCryptoError('프레임 복호화에 실패했습니다.');
    }
    this.lastReceivedCounter = counter;
    return plaintext;
  }

  openJson(frame: Uint8Array): unknown {
    return JSON.parse(utf8Decode(this.open(frame))) as unknown;
  }
}
