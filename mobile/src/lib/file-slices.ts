import { File, FileMode } from 'expo-file-system';
import type { SliceReader } from '@/lib/uploads';

/**
 * Ranged reads for the attachment uploader. A file handle is opened per chunk, its
 * cursor moved to the range and only that range read, so a 5 MB attachment never sits
 * in one JavaScript string: at most one chunk (192 KiB) plus its base64 is alive.
 *
 * The handle is injected so the sequencing can be tested without a file system; the
 * exported reader binds the real `expo-file-system` one.
 */

/**
 * The part of an `expo-file-system` handle a ranged read needs. `offset` is nullable
 * there — a handle the platform could not seek reports null — so it is nullable here.
 */
export interface SliceHandle {
  offset: number | null;
  readBytes(length: number): Uint8Array;
  close(): void;
}

/** Opens `uri` for reading; throws when the file is not there. */
export type OpenSlice = (uri: string) => SliceHandle;

/**
 * A reader over one way of opening files. A short read at the end of the file is
 * answered as-is: the uploader compares it against the length it planned and refuses
 * the file by name, which is the only place that knows which file it was.
 */
export function makeSliceReader(open: OpenSlice): SliceReader {
  // `async` on purpose: a file that will not open must come back as a rejection, not as
  // a synchronous throw from inside the uploader's loop.
  return async (uri, offset, length) => {
    const handle = open(uri);
    try {
      handle.offset = offset;
      return handle.readBytes(length);
    } finally {
      handle.close();
    }
  };
}

const openFileSlice: OpenSlice = (uri) => new File(uri).open(FileMode.ReadOnly);

export const readFileSlice: SliceReader = makeSliceReader(openFileSlice);

/**
 * The size on disk, which is what the host will actually receive. Never throws: a URI
 * the file system cannot open answers 0, and the caller refuses the file by name.
 */
export function fileSizeOf(uri: string): number {
  try {
    const file = new File(uri);
    if (!file.exists) return 0;
    const size = file.size;
    return typeof size === 'number' && Number.isFinite(size) && size > 0 ? size : 0;
  } catch {
    return 0;
  }
}

/** The file's own name, used when a picked asset carries none. Never throws. */
export function fileNameOf(uri: string, fallback: string): string {
  try {
    const name = new File(uri).name;
    return typeof name === 'string' && name.length > 0 ? name : fallback;
  } catch {
    return fallback;
  }
}
