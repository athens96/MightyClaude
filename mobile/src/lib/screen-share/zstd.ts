import type { ZstdCodec } from '@/lib/screen-share/clipboard';

/**
 * zstd for the clipboard, from `react-native-zstd` when the build has it. A build without
 * the native module is not broken: `encodeClipboard` tags its payload `raw` instead, so
 * the Mac reads the tag and the paste still lands — it just costs more bytes.
 */

let resolved: ZstdCodec | undefined | null = null;

export function nativeZstd(): ZstdCodec | undefined {
  if (resolved !== null) return resolved;
  try {
    // Required lazily: loading the module touches Nitro, which only exists on a device.
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const module = require('react-native-zstd') as Partial<ZstdCodec>;
    resolved =
      typeof module.compress === 'function' && typeof module.decompress === 'function'
        ? (module as ZstdCodec)
        : undefined;
  } catch {
    resolved = undefined;
  }
  return resolved;
}

/** Only the tests reach for this, to prove both the compressed and the raw path. */
export function resetZstdForTests(codec?: ZstdCodec): void {
  resolved = codec ?? null;
}
