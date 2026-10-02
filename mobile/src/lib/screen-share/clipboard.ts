import { SCREEN_CLIPBOARD_MAX_BYTES, type ScreenMode } from '@/api/types';
import { fromBase64, toBase64, utf8Decode, utf8Encode } from '@/api/relay/crypto';

/**
 * The clipboard goes both ways, but only when the user presses a button: nothing is
 * synced in the background, so the Mac's pasteboard is never read without a deliberate
 * tap and the phone's is never overwritten by one. A payload is compressed with zstd
 * before it is encrypted and travels on the peer connection's data channel in slices,
 * never through the relay (docs/relay.md, "`clipboard` 조각").
 *
 * Secrets are never compressed next to anything an attacker chose: each payload is one
 * clipboard read of its own, compressed alone, so no shared dictionary can leak a length.
 */

/** Which compressor produced the bytes; the receiver reads the tag, never guesses. */
export type ClipboardEncoding = 'zstd' | 'raw';
export type ClipboardDirection = 'to-mac' | 'to-phone';

/** One `clipboard` slice on the data channel. Every field is required on the wire. */
export interface ClipboardFrame {
  t: 'clipboard';
  dir: ClipboardDirection;
  /** Transfer id, 1–64 characters, the same on every slice of one transfer. */
  id: string;
  seq: number;
  total: number;
  enc: ClipboardEncoding;
  /** Plaintext byte count of the whole transfer, before compression. */
  bytes: number;
  /** Base64 of this slice of the (possibly compressed) payload. */
  data: string;
  /** The Mac sets this for a pasteboard item marked concealed; it is never read. */
  concealed?: boolean;
}

/** Why a clipboard transfer did not happen. */
export type ClipboardRefusal =
  | 'empty'
  | 'too-large'
  | 'not-control'
  | 'concealed'
  | 'undecodable'
  /** The data channel would not take a slice. */
  | 'not-sent';

/** The zstd surface used here; `react-native-zstd` satisfies it as-is. */
export interface ZstdCodec {
  compress(data: string, level?: number): ArrayBuffer;
  decompress(data: ArrayBuffer): string;
}

/** zstd level 3 is the usual default: fast enough to feel instant on a phone. */
export const ZSTD_LEVEL = 3;
/** Raw bytes per slice; base64 plus the envelope stays far inside 64 KiB. */
export const CLIPBOARD_CHUNK_BYTES = 32 * 1024;
/** The contract's cap on slices per transfer. */
export const CLIPBOARD_MAX_CHUNKS = 64;
/** One data-channel message, as the Mac reads it. */
export const DATA_CHANNEL_MAX_BYTES = 64 * 1024;
/** An unfinished transfer is dropped after this long. */
export const CLIPBOARD_ASSEMBLY_TIMEOUT_MS = 30_000;

export function utf8Length(text: string): number {
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

/** One clipboard read, packed but not yet sliced. */
export interface ClipboardPacket {
  enc: ClipboardEncoding;
  bytes: number;
  payload: Uint8Array;
}

export type ClipboardPackResult =
  | { ok: true; packet: ClipboardPacket }
  | { ok: false; refusal: ClipboardRefusal };

export type ClipboardTextResult =
  | { ok: true; text: string }
  | { ok: false; refusal: ClipboardRefusal };

/**
 * Packs one clipboard read for the Mac: zstd when the codec is there and it actually
 * shrinks the text, `raw` otherwise. A missing zstd module is not a failure — the Mac
 * reads the tag, so the paste still lands; it just costs more bytes.
 */
export function packClipboard(
  text: string,
  options: { mode: ScreenMode; codec?: ZstdCodec | undefined },
): ClipboardPackResult {
  if (!text) return { ok: false, refusal: 'empty' };
  // Sending the clipboard is an injection into the Mac, so it needs the control grant.
  if (options.mode !== 'control') return { ok: false, refusal: 'not-control' };
  const plain = utf8Encode(text);
  if (plain.length > SCREEN_CLIPBOARD_MAX_BYTES) return { ok: false, refusal: 'too-large' };
  if (options.codec) {
    try {
      const compressed = new Uint8Array(options.codec.compress(text, ZSTD_LEVEL));
      if (compressed.length > 0 && compressed.length < plain.length) {
        return { ok: true, packet: { enc: 'zstd', bytes: plain.length, payload: compressed } };
      }
    } catch {
      // A compressor that fails must not cost the user their paste.
    }
  }
  return { ok: true, packet: { enc: 'raw', bytes: plain.length, payload: plain } };
}

/** Slices one packet into `to-mac` frames, each one data-channel message. */
export function clipboardFrames(id: string, packet: ClipboardPacket): ClipboardFrame[] {
  const total = Math.max(1, Math.ceil(packet.payload.length / CLIPBOARD_CHUNK_BYTES));
  const frames: ClipboardFrame[] = [];
  for (let seq = 0; seq < total; seq += 1) {
    const slice = packet.payload.subarray(
      seq * CLIPBOARD_CHUNK_BYTES,
      (seq + 1) * CLIPBOARD_CHUNK_BYTES,
    );
    frames.push({
      t: 'clipboard',
      dir: 'to-mac',
      id,
      seq,
      total,
      enc: packet.enc,
      bytes: packet.bytes,
      data: toBase64(slice),
    });
  }
  return frames;
}

/** The pull side of the manual buttons: ask the Mac for its pasteboard once. */
export interface ClipboardRequest {
  t: 'clipboard-request';
}

export function clipboardRequest(): ClipboardRequest {
  return { t: 'clipboard-request' };
}

function integer(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) ? value : undefined;
}

/**
 * Reads one data-channel frame as a Mac → phone clipboard slice, or `undefined` if it is
 * not one or does not hold up. Only `to-phone` slices are read on this side.
 */
export function parseClipboardFrame(message: unknown): ClipboardFrame | undefined {
  if (message === null || typeof message !== 'object') return undefined;
  const raw = message as Record<string, unknown>;
  if (raw.t !== 'clipboard' || raw.dir !== 'to-phone') return undefined;
  const enc = raw.enc === 'zstd' || raw.enc === 'raw' ? raw.enc : undefined;
  const id = typeof raw.id === 'string' ? raw.id : undefined;
  const seq = integer(raw.seq);
  const total = integer(raw.total);
  const bytes = integer(raw.bytes);
  if (!enc || !id || id.length > 64 || typeof raw.data !== 'string') return undefined;
  if (seq === undefined || total === undefined || bytes === undefined) return undefined;
  const frame: ClipboardFrame = {
    t: 'clipboard',
    dir: 'to-phone',
    id,
    seq,
    total,
    enc,
    bytes,
    data: raw.data,
  };
  if (raw.concealed === true) frame.concealed = true;
  return frame;
}

export type ClipboardAssembly =
  | { kind: 'waiting'; received: number; total: number }
  | { kind: 'complete'; enc: ClipboardEncoding; bytes: number; payload: Uint8Array }
  | { kind: 'concealed' }
  | { kind: 'rejected'; refusal: ClipboardRefusal };

/**
 * Puts the Mac's slices back together. One transfer at a time: a new id drops the old
 * one, and so does 30 s without finishing. The declared plaintext size is checked before a
 * byte is buffered, and the buffered bytes are capped too, so a sender cannot make the
 * phone hold more than 1 MiB by promising it.
 */
export class ClipboardAssembler {
  private id: string | undefined;
  private enc: ClipboardEncoding = 'raw';
  private bytes = 0;
  private total = 0;
  private startedAt = 0;
  private buffered = 0;
  private slices = new Map<number, Uint8Array>();

  accept(frame: ClipboardFrame, now: number): ClipboardAssembly {
    if (this.id !== undefined && now - this.startedAt > CLIPBOARD_ASSEMBLY_TIMEOUT_MS) {
      this.reset();
    }
    if (frame.concealed) {
      this.reset();
      return { kind: 'concealed' };
    }
    if (frame.bytes < 1 || frame.bytes > SCREEN_CLIPBOARD_MAX_BYTES) {
      this.reset();
      return {
        kind: 'rejected',
        refusal: frame.bytes > SCREEN_CLIPBOARD_MAX_BYTES ? 'too-large' : 'empty',
      };
    }
    if (frame.total < 1 || frame.total > CLIPBOARD_MAX_CHUNKS) return this.reject('undecodable');
    if (frame.seq < 0 || frame.seq >= frame.total) return this.reject('undecodable');
    if (this.id !== frame.id) {
      this.reset();
      this.id = frame.id;
      this.enc = frame.enc;
      this.bytes = frame.bytes;
      this.total = frame.total;
      this.startedAt = now;
    }
    if (frame.enc !== this.enc || frame.bytes !== this.bytes || frame.total !== this.total) {
      return this.reject('undecodable');
    }
    let slice: Uint8Array;
    try {
      slice = fromBase64(frame.data);
    } catch {
      return this.reject('undecodable');
    }
    this.buffered += slice.length - (this.slices.get(frame.seq)?.length ?? 0);
    this.slices.set(frame.seq, slice);
    if (this.buffered > SCREEN_CLIPBOARD_MAX_BYTES) return this.reject('too-large');
    if (this.slices.size < this.total) {
      return { kind: 'waiting', received: this.slices.size, total: this.total };
    }
    const payload = new Uint8Array(this.buffered);
    let offset = 0;
    for (let seq = 0; seq < this.total; seq += 1) {
      const part = this.slices.get(seq);
      if (!part) return this.reject('undecodable');
      payload.set(part, offset);
      offset += part.length;
    }
    const outcome: ClipboardAssembly = {
      kind: 'complete',
      enc: this.enc,
      bytes: this.bytes,
      payload,
    };
    this.reset();
    return outcome;
  }

  reset(): void {
    this.id = undefined;
    this.enc = 'raw';
    this.bytes = 0;
    this.total = 0;
    this.startedAt = 0;
    this.buffered = 0;
    this.slices = new Map();
  }

  private reject(refusal: ClipboardRefusal): ClipboardAssembly {
    this.reset();
    return { kind: 'rejected', refusal };
  }
}

const ZSTD_MAGIC = [0x28, 0xb5, 0x2f, 0xfd];

/**
 * The plaintext size a zstd frame header declares, or `undefined` when the header is
 * missing one. `react-native-zstd` allocates exactly this much before decompressing, so it
 * is read here first: a frame that promises more than the transfer's `bytes` — a
 * compression bomb — is refused before any native code runs.
 */
export function zstdFrameContentSize(frame: Uint8Array): number | undefined {
  if (frame.length < 6) return undefined;
  for (let index = 0; index < 4; index += 1) {
    if (frame[index] !== ZSTD_MAGIC[index]) return undefined;
  }
  const descriptor = frame[4] ?? 0;
  const fcsFlag = descriptor >> 6;
  const singleSegment = (descriptor >> 5) & 1;
  const dictFlag = descriptor & 3;
  const fcsSize = fcsFlag === 0 ? singleSegment : fcsFlag === 1 ? 2 : fcsFlag === 2 ? 4 : 8;
  if (fcsSize === 0) return undefined;
  const dictSize = dictFlag === 0 ? 0 : dictFlag === 1 ? 1 : dictFlag === 2 ? 2 : 4;
  const offset = 5 + (singleSegment ? 0 : 1) + dictSize;
  if (frame.length < offset + fcsSize) return undefined;
  let value = 0;
  for (let index = fcsSize - 1; index >= 0; index -= 1) {
    value = value * 256 + (frame[offset + index] ?? 0);
  }
  return fcsSize === 2 ? value + 256 : value;
}

/**
 * Turns a finished Mac → phone transfer back into text. The size the sender declared is
 * the ceiling: a zstd frame must declare exactly that many bytes before it is
 * decompressed, and the text that comes out must not be longer.
 */
export function decodeClipboardPayload(
  packet: ClipboardPacket,
  options: { codec?: ZstdCodec | undefined } = {},
): ClipboardTextResult {
  if (packet.bytes < 1) return { ok: false, refusal: 'empty' };
  if (packet.bytes > SCREEN_CLIPBOARD_MAX_BYTES) return { ok: false, refusal: 'too-large' };
  try {
    let text: string;
    if (packet.enc === 'zstd') {
      if (!options.codec) return { ok: false, refusal: 'undecodable' };
      if (zstdFrameContentSize(packet.payload) !== packet.bytes) {
        return { ok: false, refusal: 'undecodable' };
      }
      const buffer = packet.payload.buffer.slice(
        packet.payload.byteOffset,
        packet.payload.byteOffset + packet.payload.byteLength,
      ) as ArrayBuffer;
      text = options.codec.decompress(buffer);
    } else {
      if (packet.payload.length !== packet.bytes) return { ok: false, refusal: 'undecodable' };
      text = utf8Decode(packet.payload);
    }
    const length = utf8Length(text);
    if (length > packet.bytes) return { ok: false, refusal: 'too-large' };
    if (length === 0) return { ok: false, refusal: 'empty' };
    return { ok: true, text };
  } catch {
    return { ok: false, refusal: 'undecodable' };
  }
}
