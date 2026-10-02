import { SCREEN_CLIPBOARD_MAX_BYTES, type ScreenMode } from '@/api/types';
import { fromBase64, toBase64, utf8Decode, utf8Encode } from '@/api/relay/crypto';

/**
 * The clipboard goes both ways, but only when the user presses a button: nothing is
 * synced in the background, so the Mac's pasteboard is never read without a deliberate
 * tap and the phone's is never overwritten by one. A payload is compressed with zstd
 * before it is encrypted — text and logs shrink to a fraction — and it travels on the
 * peer connection's data channel, never through the relay.
 *
 * Secrets are never compressed next to anything an attacker chose: each payload is one
 * clipboard read of its own, compressed alone, so no shared dictionary can leak a length.
 */

/** Which compressor produced the bytes; the receiver reads the tag, never guesses. */
export type ClipboardEncoding = 'zstd' | 'raw';
export type ClipboardDirection = 'to-mac' | 'to-phone';

export interface ClipboardPayload {
  t: 'clipboard';
  dir: ClipboardDirection;
  enc: ClipboardEncoding;
  /** Plaintext byte count, before compression; the receiver checks it against the limit. */
  bytes: number;
  /** Base64 of the compressed (or plain) bytes. */
  data: string;
  /** The Mac sets this for a pasteboard item marked concealed; it is never read. */
  concealed?: boolean;
}

/** Why a clipboard transfer did not happen. */
export type ClipboardRefusal = 'empty' | 'too-large' | 'not-control' | 'concealed' | 'undecodable';

/** The zstd surface used here; `react-native-zstd` satisfies it as-is. */
export interface ZstdCodec {
  compress(data: string, level?: number): ArrayBuffer;
  decompress(data: ArrayBuffer): string;
}

/** zstd level 3 is the usual default: fast enough to feel instant on a phone. */
export const ZSTD_LEVEL = 3;

function utf8Length(text: string): number {
  let bytes = 0;
  for (const char of text) {
    const code = char.codePointAt(0) ?? 0;
    if (code < 0x80) bytes += 1;
    else if (code < 0x800) bytes += 2;
    else if (code < 0x10000) bytes += 3;
    else bytes += 4;
  }
  return bytes;
}

export type ClipboardResult =
  | { ok: true; payload: ClipboardPayload }
  | { ok: false; refusal: ClipboardRefusal };

export type ClipboardTextResult =
  | { ok: true; text: string }
  | { ok: false; refusal: ClipboardRefusal };

/**
 * Packs one clipboard read for the Mac. A missing zstd module is not a failure: the
 * payload goes out tagged `raw` and the Mac reads the tag, so an older or stripped build
 * still works — it just sends more bytes.
 */
export function encodeClipboard(
  text: string,
  options: { mode: ScreenMode; codec?: ZstdCodec | undefined; direction?: ClipboardDirection },
): ClipboardResult {
  if (!text) return { ok: false, refusal: 'empty' };
  // Sending the clipboard is an injection into the Mac, so it needs the control grant.
  if (options.mode !== 'control') return { ok: false, refusal: 'not-control' };
  const bytes = utf8Length(text);
  if (bytes > SCREEN_CLIPBOARD_MAX_BYTES) return { ok: false, refusal: 'too-large' };

  const direction = options.direction ?? 'to-mac';
  if (options.codec) {
    try {
      const compressed = options.codec.compress(text, ZSTD_LEVEL);
      return {
        ok: true,
        payload: {
          t: 'clipboard',
          dir: direction,
          enc: 'zstd',
          bytes,
          data: toBase64(new Uint8Array(compressed)),
        },
      };
    } catch {
      // A compressor that fails must not cost the user their paste.
    }
  }
  return {
    ok: true,
    payload: {
      t: 'clipboard',
      dir: direction,
      enc: 'raw',
      bytes,
      data: toBase64(utf8Encode(text)),
    },
  };
}

/** True for a pasteboard item the Mac marked concealed (a password manager's). */
export function isConcealed(payload: Pick<ClipboardPayload, 'concealed'>): boolean {
  return payload.concealed === true;
}

/**
 * Unpacks what the Mac sent. A concealed pasteboard type is skipped rather than pasted —
 * the Mac already leaves those out, and this is the second line for a build that does not.
 */
export function decodeClipboard(
  payload: ClipboardPayload,
  options: { codec?: ZstdCodec | undefined } = {},
): ClipboardTextResult {
  if (isConcealed(payload)) return { ok: false, refusal: 'concealed' };
  if (payload.bytes > SCREEN_CLIPBOARD_MAX_BYTES) return { ok: false, refusal: 'too-large' };
  if (!payload.data) return { ok: false, refusal: 'empty' };

  try {
    const raw = fromBase64(payload.data);
    if (payload.enc === 'zstd') {
      if (!options.codec) return { ok: false, refusal: 'undecodable' };
      const buffer = raw.buffer.slice(
        raw.byteOffset,
        raw.byteOffset + raw.byteLength,
      ) as ArrayBuffer;
      const text = options.codec.decompress(buffer);
      if (utf8Length(text) > SCREEN_CLIPBOARD_MAX_BYTES) return { ok: false, refusal: 'too-large' };
      return { ok: true, text };
    }
    const text = utf8Decode(raw);
    if (utf8Length(text) > SCREEN_CLIPBOARD_MAX_BYTES) return { ok: false, refusal: 'too-large' };
    return { ok: true, text };
  } catch {
    return { ok: false, refusal: 'undecodable' };
  }
}

/** The pull side of the manual buttons: ask the Mac for its pasteboard once. */
export interface ClipboardRequest {
  t: 'clipboard-request';
}

export function clipboardRequest(): ClipboardRequest {
  return { t: 'clipboard-request' };
}

/** Reads one data-channel frame as a clipboard payload, or `undefined` if it is not one. */
export function parseClipboardPayload(message: unknown): ClipboardPayload | undefined {
  if (message === null || typeof message !== 'object') return undefined;
  const raw = message as Record<string, unknown>;
  if (raw.t !== 'clipboard') return undefined;
  if (typeof raw.data !== 'string') return undefined;
  const enc = raw.enc === 'zstd' ? 'zstd' : raw.enc === 'raw' ? 'raw' : undefined;
  if (!enc) return undefined;
  const dir = raw.dir === 'to-mac' ? 'to-mac' : 'to-phone';
  const bytes = typeof raw.bytes === 'number' && Number.isFinite(raw.bytes) ? raw.bytes : 0;
  const payload: ClipboardPayload = { t: 'clipboard', dir, enc, bytes, data: raw.data };
  if (raw.concealed === true) payload.concealed = true;
  return payload;
}
