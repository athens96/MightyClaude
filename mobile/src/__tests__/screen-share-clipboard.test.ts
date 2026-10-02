import { SCREEN_CLIPBOARD_MAX_BYTES } from '@/api/types';
import { fromBase64, toBase64, utf8Decode, utf8Encode } from '@/api/relay/crypto';
import {
  CLIPBOARD_ASSEMBLY_TIMEOUT_MS,
  CLIPBOARD_CHUNK_BYTES,
  ClipboardAssembler,
  DATA_CHANNEL_MAX_BYTES,
  clipboardFrames,
  clipboardRequest,
  decodeClipboardPayload,
  packClipboard,
  parseClipboardFrame,
  utf8Length,
  zstdFrameContentSize,
  type ClipboardFrame,
  type ZstdCodec,
} from '@/lib/screen-share/clipboard';
import { testZstd } from './support/test-zstd';

/**
 * The clipboard: both directions, manual only, at most 1 MB, compressed before it is
 * encrypted, sliced into data-channel messages of at most 64 KiB, and never a concealed
 * pasteboard item (docs/relay.md, "`clipboard` 조각").
 */

/** The Mac's side of a transfer: what `ScreenShareDataChannel.clipboardFrames` sends. */
function macFrames(
  text: string,
  options: { codec?: ZstdCodec; id?: string; bytes?: number } = {},
): ClipboardFrame[] {
  const plain = utf8Encode(text);
  const payload = options.codec ? new Uint8Array(options.codec.compress(text)) : plain;
  const enc = options.codec ? 'zstd' : 'raw';
  const total = Math.max(1, Math.ceil(payload.length / CLIPBOARD_CHUNK_BYTES));
  return Array.from({ length: total }, (_, seq) => ({
    t: 'clipboard' as const,
    dir: 'to-phone' as const,
    id: options.id ?? 'm1',
    seq,
    total,
    enc,
    bytes: options.bytes ?? plain.length,
    data: toBase64(payload.subarray(seq * CLIPBOARD_CHUNK_BYTES, (seq + 1) * CLIPBOARD_CHUNK_BYTES)),
  }));
}

describe('sending the phone clipboard to the Mac', () => {
  it('needs the control grant: a view-only session cannot inject anything', () => {
    expect(packClipboard('hello', { mode: 'view', codec: testZstd() })).toEqual({
      ok: false,
      refusal: 'not-control',
    });
  });

  it('compresses with zstd when that is smaller, and says so in every slice', () => {
    const codec = testZstd();
    const text = '터미널에 붙여넣을 글 '.repeat(200);
    const packed = packClipboard(text, { mode: 'control', codec });
    expect(packed.ok).toBe(true);
    if (!packed.ok) return;
    expect(packed.packet.enc).toBe('zstd');
    expect(packed.packet.bytes).toBe(utf8Length(text));
    const frames = clipboardFrames('p1', packed.packet);
    for (const frame of frames) {
      expect(frame).toMatchObject({ t: 'clipboard', dir: 'to-mac', id: 'p1', enc: 'zstd', bytes: packed.packet.bytes });
    }
    expect([...fromBase64(frames[0]?.data ?? '').subarray(0, 4)]).toEqual([0x28, 0xb5, 0x2f, 0xfd]);
  });

  it('sends raw when compression would not help, or when there is no zstd', () => {
    const tiny = packClipboard('x', { mode: 'control', codec: testZstd() });
    expect(tiny.ok && tiny.packet.enc).toBe('raw');
    const plain = packClipboard('plain', { mode: 'control' });
    expect(plain.ok && plain.packet.enc).toBe('raw');
    if (plain.ok) expect(utf8Decode(plain.packet.payload)).toBe('plain');
  });

  it('falls back to raw rather than losing the paste when the compressor throws', () => {
    const broken: ZstdCodec = {
      compress() {
        throw new Error('nitro is not here');
      },
      decompress() {
        throw new Error('nitro is not here');
      },
    };
    const packed = packClipboard('still works', { mode: 'control', codec: broken });
    expect(packed.ok && packed.packet.enc).toBe('raw');
  });

  it('refuses an empty clipboard and one over 1 MB, counted in bytes', () => {
    expect(packClipboard('', { mode: 'control' })).toEqual({ ok: false, refusal: 'empty' });
    expect(packClipboard('a'.repeat(SCREEN_CLIPBOARD_MAX_BYTES + 1), { mode: 'control' })).toEqual({
      ok: false,
      refusal: 'too-large',
    });
    const korean = '가'.repeat(SCREEN_CLIPBOARD_MAX_BYTES / 3 + 1);
    expect(korean.length).toBeLessThan(SCREEN_CLIPBOARD_MAX_BYTES);
    expect(packClipboard(korean, { mode: 'control' })).toEqual({ ok: false, refusal: 'too-large' });
    expect(packClipboard('a'.repeat(SCREEN_CLIPBOARD_MAX_BYTES), { mode: 'control' }).ok).toBe(true);
  });

  it('slices a full 1 MB clipboard into messages that each fit in 64 KiB', () => {
    const packed = packClipboard('가나다라'.repeat(SCREEN_CLIPBOARD_MAX_BYTES / 12), { mode: 'control' });
    expect(packed.ok).toBe(true);
    if (!packed.ok) return;
    const frames = clipboardFrames('p-big', packed.packet);
    expect(frames.length).toBeGreaterThan(1);
    expect(frames.length).toBeLessThanOrEqual(64);
    frames.forEach((frame, seq) => {
      expect(frame.seq).toBe(seq);
      expect(frame.total).toBe(frames.length);
      expect(utf8Length(JSON.stringify(frame))).toBeLessThanOrEqual(DATA_CHANNEL_MAX_BYTES);
    });
    const joined = frames.flatMap((frame) => [...fromBase64(frame.data)]);
    expect(joined).toHaveLength(packed.packet.payload.length);
  });
});

describe('reassembling the Mac clipboard on the phone', () => {
  it('puts the Mac’s slices back together, in any order', () => {
    const text = 'x'.repeat(CLIPBOARD_CHUNK_BYTES * 2 + 10);
    const frames = macFrames(text);
    expect(frames).toHaveLength(3);
    const assembler = new ClipboardAssembler();
    expect(assembler.accept(frames[2]!, 0)).toEqual({ kind: 'waiting', received: 1, total: 3 });
    expect(assembler.accept(frames[0]!, 0).kind).toBe('waiting');
    const done = assembler.accept(frames[1]!, 0);
    expect(done.kind).toBe('complete');
    if (done.kind !== 'complete') return;
    expect(decodeClipboardPayload(done)).toEqual({ ok: true, text });
  });

  it('decodes a zstd transfer', () => {
    const codec = testZstd();
    const text = 'log line\n'.repeat(5000);
    const assembler = new ClipboardAssembler();
    let outcome;
    for (const frame of macFrames(text, { codec })) outcome = assembler.accept(frame, 0);
    expect(outcome?.kind).toBe('complete');
    if (outcome?.kind !== 'complete') return;
    expect(decodeClipboardPayload(outcome, { codec })).toEqual({ ok: true, text });
  });

  it('never pastes an item the Mac marked concealed', () => {
    const assembler = new ClipboardAssembler();
    expect(
      assembler.accept(
        { t: 'clipboard', dir: 'to-phone', id: 'c', seq: 0, total: 1, enc: 'raw', bytes: 0, data: '', concealed: true },
        0,
      ),
    ).toEqual({ kind: 'concealed' });
  });

  it('checks the declared size before buffering a single byte', () => {
    const assembler = new ClipboardAssembler();
    const outcome = assembler.accept(
      { t: 'clipboard', dir: 'to-phone', id: 'b', seq: 0, total: 2, enc: 'raw', bytes: SCREEN_CLIPBOARD_MAX_BYTES + 1, data: 'AAAA' },
      0,
    );
    expect(outcome).toEqual({ kind: 'rejected', refusal: 'too-large' });
  });

  it('caps the bytes it holds, whatever the frames claim', () => {
    const assembler = new ClipboardAssembler();
    const slice = toBase64(new Uint8Array(CLIPBOARD_CHUNK_BYTES * 2));
    let outcome;
    for (let seq = 0; seq < 17; seq += 1) {
      outcome = assembler.accept(
        { t: 'clipboard', dir: 'to-phone', id: 'flood', seq, total: 64, enc: 'raw', bytes: 100, data: slice },
        0,
      );
      if (outcome.kind !== 'waiting') break;
    }
    expect(outcome).toEqual({ kind: 'rejected', refusal: 'too-large' });
  });

  it('drops an unfinished transfer when a new id starts, or after 30 s', () => {
    const assembler = new ClipboardAssembler();
    const first = macFrames('a'.repeat(CLIPBOARD_CHUNK_BYTES + 1), { id: 'one' });
    const second = macFrames('b'.repeat(CLIPBOARD_CHUNK_BYTES + 1), { id: 'two' });
    assembler.accept(first[0]!, 0);
    assembler.accept(second[0]!, 0);
    // `one` is gone: its second slice starts a fresh transfer that never completes.
    expect(assembler.accept(first[1]!, 0).kind).toBe('waiting');

    const late = new ClipboardAssembler();
    late.accept(first[0]!, 0);
    expect(late.accept(first[1]!, CLIPBOARD_ASSEMBLY_TIMEOUT_MS + 1)).toEqual({
      kind: 'waiting',
      received: 1,
      total: 2,
    });
  });

  it('drops a transfer whose slices disagree about it', () => {
    const assembler = new ClipboardAssembler();
    const frames = macFrames('z'.repeat(CLIPBOARD_CHUNK_BYTES + 1));
    assembler.accept(frames[0]!, 0);
    expect(assembler.accept({ ...frames[1]!, bytes: 7 }, 0)).toEqual({
      kind: 'rejected',
      refusal: 'undecodable',
    });
  });
});

describe('a compression bomb', () => {
  it('reads the size a zstd frame header declares', () => {
    const codec = testZstd();
    const text = 'abc'.repeat(1000);
    expect(zstdFrameContentSize(new Uint8Array(codec.compress(text)))).toBe(3000);
    expect(zstdFrameContentSize(utf8Encode('not zstd at all'))).toBeUndefined();
  });

  it('is refused before the decompressor runs when the frame promises more than `bytes`', () => {
    const codec = testZstd();
    const bomb = new Uint8Array(codec.compress('a'.repeat(4 * SCREEN_CLIPBOARD_MAX_BYTES)));
    expect(bomb.length).toBeLessThan(SCREEN_CLIPBOARD_MAX_BYTES);
    expect(decodeClipboardPayload({ enc: 'zstd', bytes: 100, payload: bomb }, { codec })).toEqual({
      ok: false,
      refusal: 'undecodable',
    });
    expect(codec.decompressed).toBe(0);
  });

  it('refuses zstd bytes it has no decompressor for, rather than pasting rubbish', () => {
    const codec = testZstd();
    const payload = new Uint8Array(codec.compress('text text text text text'));
    expect(decodeClipboardPayload({ enc: 'zstd', bytes: 24, payload })).toEqual({
      ok: false,
      refusal: 'undecodable',
    });
  });

  it('refuses a raw transfer whose length is not what it declared', () => {
    expect(decodeClipboardPayload({ enc: 'raw', bytes: 3, payload: utf8Encode('four') })).toEqual({
      ok: false,
      refusal: 'undecodable',
    });
  });
});

describe('the data-channel frames', () => {
  it('reads a Mac → phone slice and nothing else', () => {
    const frame = { t: 'clipboard', dir: 'to-phone', id: 'm', seq: 0, total: 1, enc: 'zstd', bytes: 4, data: 'AAAA' };
    expect(parseClipboardFrame(frame)).toEqual(frame);
    expect(parseClipboardFrame({ ...frame, dir: 'to-mac' })).toBeUndefined();
    expect(parseClipboardFrame({ ...frame, enc: 'gzip' })).toBeUndefined();
    expect(parseClipboardFrame({ ...frame, seq: undefined })).toBeUndefined();
    expect(parseClipboardFrame({ ...frame, id: 'x'.repeat(65) })).toBeUndefined();
    expect(parseClipboardFrame({ t: 'tap', x: 0, y: 0 })).toBeUndefined();
    expect(parseClipboardFrame(null)).toBeUndefined();
  });

  it('keeps the concealed flag so the receiver can honour it', () => {
    expect(
      parseClipboardFrame({ t: 'clipboard', dir: 'to-phone', id: 'c', seq: 0, total: 1, enc: 'raw', bytes: 0, data: '', concealed: true }),
    ).toMatchObject({ concealed: true });
  });

  it('asks the Mac for its clipboard with a frame of its own', () => {
    expect(clipboardRequest()).toEqual({ t: 'clipboard-request' });
  });
});
