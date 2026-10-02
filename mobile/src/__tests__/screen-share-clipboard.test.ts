import { SCREEN_CLIPBOARD_MAX_BYTES } from '@/api/types';
import { fromBase64, toBase64, utf8Decode, utf8Encode } from '@/api/relay/crypto';
import {
  ZSTD_LEVEL,
  clipboardRequest,
  decodeClipboard,
  encodeClipboard,
  isConcealed,
  parseClipboardPayload,
  type ZstdCodec,
} from '@/lib/screen-share/clipboard';

/**
 * The clipboard: both directions, manual only, at most 1 MB, compressed before it is
 * encrypted, and never a concealed pasteboard item. Each payload is one clipboard read
 * compressed on its own, so nothing a secret shares a compressor with can leak its length.
 */

/** A stand-in for `react-native-zstd`: real framing, trivial "compression". */
function fakeZstd(): ZstdCodec & { levels: number[] } {
  const levels: number[] = [];
  return {
    levels,
    compress(data, level) {
      levels.push(level ?? -1);
      const body = utf8Encode(data);
      const framed = new Uint8Array(body.length + 4);
      framed.set([0x28, 0xb5, 0x2f, 0xfd], 0);
      framed.set(body, 4);
      return framed.buffer.slice(0, framed.length) as ArrayBuffer;
    },
    decompress(data) {
      const bytes = new Uint8Array(data);
      if (bytes.length < 4) throw new Error('not a zstd frame');
      return utf8Decode(bytes.subarray(4));
    },
  };
}

describe('sending the phone clipboard to the Mac', () => {
  it('needs the control grant: a view-only session cannot inject anything', () => {
    expect(encodeClipboard('hello', { mode: 'view', codec: fakeZstd() })).toEqual({
      ok: false,
      refusal: 'not-control',
    });
  });

  it('compresses with zstd before anything is encrypted, and says so in the payload', () => {
    const codec = fakeZstd();
    const result = encodeClipboard('터미널에 붙여넣을 글', { mode: 'control', codec });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.payload.enc).toBe('zstd');
    expect(result.payload.dir).toBe('to-mac');
    expect(codec.levels).toEqual([ZSTD_LEVEL]);
    // The tag is what the Mac reads; the bytes are the compressor's frame.
    expect([...fromBase64(result.payload.data).subarray(0, 4)]).toEqual([0x28, 0xb5, 0x2f, 0xfd]);
    expect(decodeClipboard(result.payload, { codec })).toEqual({
      ok: true,
      text: '터미널에 붙여넣을 글',
    });
  });

  it('falls back to a raw payload when the build has no zstd, and tags it raw', () => {
    const result = encodeClipboard('plain', { mode: 'control' });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.payload.enc).toBe('raw');
    expect(utf8Decode(fromBase64(result.payload.data))).toBe('plain');
    expect(decodeClipboard(result.payload)).toEqual({ ok: true, text: 'plain' });
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
    const result = encodeClipboard('still works', { mode: 'control', codec: broken });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.payload.enc).toBe('raw');
  });

  it('refuses an empty clipboard and one over 1 MB', () => {
    expect(encodeClipboard('', { mode: 'control' })).toEqual({ ok: false, refusal: 'empty' });
    const tooBig = 'a'.repeat(SCREEN_CLIPBOARD_MAX_BYTES + 1);
    expect(encodeClipboard(tooBig, { mode: 'control' })).toEqual({
      ok: false,
      refusal: 'too-large',
    });
    expect(SCREEN_CLIPBOARD_MAX_BYTES).toBe(1024 * 1024);
  });

  it('measures the limit in bytes, so Korean cannot slip past it', () => {
    const korean = '가'.repeat(SCREEN_CLIPBOARD_MAX_BYTES / 3 + 1);
    expect(korean.length).toBeLessThan(SCREEN_CLIPBOARD_MAX_BYTES);
    expect(encodeClipboard(korean, { mode: 'control' })).toEqual({
      ok: false,
      refusal: 'too-large',
    });
  });

  it('takes a clipboard exactly at the limit', () => {
    const exact = 'a'.repeat(SCREEN_CLIPBOARD_MAX_BYTES);
    expect(encodeClipboard(exact, { mode: 'control' }).ok).toBe(true);
  });
});

describe('reading the Mac clipboard on the phone', () => {
  it('skips an item the Mac marked concealed, so a password is never pasted here', () => {
    const payload = {
      t: 'clipboard' as const,
      dir: 'to-phone' as const,
      enc: 'raw' as const,
      bytes: 6,
      data: toBase64(utf8Encode('secret')),
      concealed: true,
    };
    expect(isConcealed(payload)).toBe(true);
    expect(decodeClipboard(payload)).toEqual({ ok: false, refusal: 'concealed' });
  });

  it('refuses a payload that claims more than the limit', () => {
    expect(
      decodeClipboard({
        t: 'clipboard',
        dir: 'to-phone',
        enc: 'raw',
        bytes: SCREEN_CLIPBOARD_MAX_BYTES + 1,
        data: toBase64(utf8Encode('short')),
      }),
    ).toEqual({ ok: false, refusal: 'too-large' });
  });

  it('refuses zstd bytes it has no decompressor for, rather than pasting rubbish', () => {
    const codec = fakeZstd();
    const sent = encodeClipboard('text', { mode: 'control', codec });
    expect(sent.ok).toBe(true);
    if (!sent.ok) return;
    expect(decodeClipboard(sent.payload)).toEqual({ ok: false, refusal: 'undecodable' });
  });

  it('refuses a frame whose bytes are not what the tag promised', () => {
    const codec = fakeZstd();
    expect(
      decodeClipboard(
        { t: 'clipboard', dir: 'to-phone', enc: 'zstd', bytes: 3, data: toBase64(utf8Encode('x')) },
        { codec },
      ),
    ).toEqual({ ok: false, refusal: 'undecodable' });
  });

  it('refuses an empty payload', () => {
    expect(
      decodeClipboard({ t: 'clipboard', dir: 'to-phone', enc: 'raw', bytes: 0, data: '' }),
    ).toEqual({ ok: false, refusal: 'empty' });
  });
});

describe('the data-channel frames', () => {
  it('reads a clipboard payload and nothing else', () => {
    expect(
      parseClipboardPayload({ t: 'clipboard', dir: 'to-phone', enc: 'zstd', bytes: 4, data: 'AAAA' }),
    ).toEqual({ t: 'clipboard', dir: 'to-phone', enc: 'zstd', bytes: 4, data: 'AAAA' });
    expect(parseClipboardPayload({ t: 'tap', x: 0, y: 0 })).toBeUndefined();
    expect(parseClipboardPayload({ t: 'clipboard', enc: 'gzip', data: 'AAAA' })).toBeUndefined();
    expect(parseClipboardPayload({ t: 'clipboard', enc: 'raw' })).toBeUndefined();
    expect(parseClipboardPayload(null)).toBeUndefined();
  });

  it('keeps the concealed flag so the receiver can honour it', () => {
    expect(
      parseClipboardPayload({
        t: 'clipboard',
        dir: 'to-phone',
        enc: 'raw',
        bytes: 1,
        data: 'AA==',
        concealed: true,
      }),
    ).toMatchObject({ concealed: true });
  });

  it('asks the Mac for its clipboard with a frame of its own', () => {
    expect(clipboardRequest()).toEqual({ t: 'clipboard-request' });
  });
});
