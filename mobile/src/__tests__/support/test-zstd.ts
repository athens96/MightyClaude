import * as zlib from 'node:zlib';
import { utf8Decode, utf8Encode } from '@/api/relay/crypto';
import type { ZstdCodec } from '@/lib/screen-share/clipboard';

/**
 * A zstd codec for the tests. Node's own zstd (22.15+) when it has one, so the frames are
 * real; otherwise a stand-in that writes a genuine zstd frame header (magic, single
 * segment, 4-byte content size) around a run-length body.
 */

type NodeZstd = {
  zstdCompressSync?: (data: Uint8Array) => Uint8Array;
  zstdDecompressSync?: (data: Uint8Array) => Uint8Array;
};

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

function headerFramed(): ZstdCodec {
  return {
    compress(data) {
      const plain = utf8Encode(data);
      const runs: number[] = [];
      for (let index = 0; index < plain.length; ) {
        const byte = plain[index] ?? 0;
        let length = 1;
        while (length < 255 && plain[index + length] === byte) length += 1;
        runs.push(length, byte);
        index += length;
      }
      const out = new Uint8Array(9 + runs.length);
      out.set([0x28, 0xb5, 0x2f, 0xfd, 0xa0], 0);
      new DataView(out.buffer).setUint32(5, plain.length, true);
      out.set(runs, 9);
      return toArrayBuffer(out);
    },
    decompress(data) {
      const bytes = new Uint8Array(data);
      const body: number[] = [];
      for (let index = 9; index + 1 < bytes.length; index += 2) {
        for (let count = 0; count < (bytes[index] ?? 0); count += 1) body.push(bytes[index + 1] ?? 0);
      }
      return utf8Decode(Uint8Array.from(body));
    },
  };
}

export function testZstd(): ZstdCodec & { decompressed: number } {
  const node = zlib as unknown as NodeZstd;
  const inner: ZstdCodec =
    node.zstdCompressSync && node.zstdDecompressSync
      ? {
          compress: (data) => toArrayBuffer(node.zstdCompressSync!(utf8Encode(data))),
          decompress: (data) => utf8Decode(node.zstdDecompressSync!(new Uint8Array(data))),
        }
      : headerFramed();
  const codec = {
    decompressed: 0,
    compress: (data: string, level?: number) => inner.compress(data, level),
    decompress: (data: ArrayBuffer) => {
      codec.decompressed += 1;
      return inner.decompress(data);
    },
  };
  return codec;
}
