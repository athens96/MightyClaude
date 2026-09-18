import { toBase64 } from '@/api/relay/crypto';
import {
  DEFAULT_CHUNK_SIZE,
  MAX_ATTACHMENTS,
  MAX_ATTACHMENTS_TOTAL_BYTES,
  MAX_ATTACHMENT_BYTES,
} from '@/api/types';

/**
 * Attachment limits and chunk planning. Pure: the Mac's limits are checked here before a
 * single byte goes out, and the upload is driven through injected transport and reader
 * functions so the sequencing can be tested without a socket or a file system.
 */

export interface PickedFile {
  /** `file://` (or `content://` on Android) location the reader can open. */
  uri: string;
  name: string;
  size: number;
  mimeType?: string;
}

export interface ChunkPlan {
  index: number;
  offset: number;
  length: number;
}

/** `12 KB`, `4.2 MB` — the size shown on a chip. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb < 10 ? kb.toFixed(1) : Math.round(kb)} KB`;
  const mb = kb / 1024;
  return `${mb < 10 ? mb.toFixed(1) : Math.round(mb)} MB`;
}

/**
 * The host's `chunkSize`, clamped to something we can actually send. The contract puts
 * it at 192 KiB and the chunk route's own body limit leaves no room above that, so a
 * larger number from a host is taken as a mistake rather than an invitation; a zero or
 * missing value falls back to the documented size.
 */
export function chunkSizeFor(declared: number | undefined): number {
  if (declared === undefined || !Number.isFinite(declared) || declared < 1) {
    return DEFAULT_CHUNK_SIZE;
  }
  return Math.min(DEFAULT_CHUNK_SIZE, Math.floor(declared));
}

/** Byte ranges for one file, in order from 0; the last one is whatever is left. */
export function planChunks(size: number, chunkSize: number): ChunkPlan[] {
  if (!Number.isFinite(size) || size <= 0) return [];
  const step = chunkSizeFor(chunkSize);
  const plans: ChunkPlan[] = [];
  for (let offset = 0; offset < size; offset += step) {
    plans.push({ index: plans.length, offset, length: Math.min(step, size - offset) });
  }
  return plans;
}

/** Base64 for one chunk; binary bytes included, padded, standard alphabet. */
export function encodeChunk(bytes: Uint8Array): string {
  return toBase64(bytes);
}

export interface LimitResult {
  files: PickedFile[];
  /** Korean reason the rejected files were left out; absent when all of them fit. */
  error?: string;
}

/**
 * Adds newly picked files to the ones already attached, keeping the request inside the
 * Mac's limits: at most eight files, 5 MB each and 8 MB together. Files that do not fit
 * are dropped and named in the message, rather than failing the whole pick.
 */
export function acceptFiles(
  current: readonly PickedFile[],
  incoming: readonly PickedFile[],
): LimitResult {
  const files = [...current];
  let total = files.reduce((sum, file) => sum + file.size, 0);
  const tooBig: string[] = [];
  const overflow: string[] = [];
  const unreadable: string[] = [];
  let tooMany = 0;

  for (const file of incoming) {
    if (files.some((entry) => entry.uri === file.uri)) continue;
    // Size 0 means the file is empty or we could not read it at all; either way the
    // host would answer 400 after a round trip, so it is refused here by name.
    if (!Number.isFinite(file.size) || file.size <= 0) {
      unreadable.push(file.name);
      continue;
    }
    if (file.size > MAX_ATTACHMENT_BYTES) {
      tooBig.push(file.name);
      continue;
    }
    if (files.length >= MAX_ATTACHMENTS) {
      tooMany += 1;
      continue;
    }
    if (total + file.size > MAX_ATTACHMENTS_TOTAL_BYTES) {
      overflow.push(file.name);
      continue;
    }
    files.push(file);
    total += file.size;
  }

  const reasons: string[] = [];
  if (unreadable.length > 0) {
    reasons.push(`${unreadable.join(', ')}: 파일을 읽을 수 없거나 비어 있습니다.`);
  }
  if (tooBig.length > 0) {
    reasons.push(
      `${tooBig.join(', ')}: 파일 하나는 ${formatBytes(MAX_ATTACHMENT_BYTES)}까지 보낼 수 있습니다.`,
    );
  }
  if (tooMany > 0) {
    reasons.push(`한 번에 파일 ${MAX_ATTACHMENTS}개까지 붙일 수 있습니다.`);
  }
  if (overflow.length > 0) {
    reasons.push(
      `${overflow.join(', ')}: 첨부 합계는 ${formatBytes(MAX_ATTACHMENTS_TOTAL_BYTES)}까지입니다.`,
    );
  }
  return reasons.length > 0 ? { files, error: reasons.join(' ') } : { files };
}

/** `{ protocol, ok, received }`; an older host may leave `received` out. */
export interface ChunkReceiptLike {
  received?: unknown;
}

/** `{ protocol, attachment: { id, name, size } }`. */
export interface CompletedUploadLike {
  attachment?: { id?: unknown };
}

/** The `/uploads…` calls the runner needs; `MobileClient` satisfies this shape. */
export interface UploadTransport {
  createUpload(
    sessionId: string,
    input: { name: string; size: number; mimeType?: string },
  ): Promise<{ uploadId: string; chunkSize: number }>;
  uploadChunk(
    uploadId: string,
    index: number,
    dataBase64: string,
  ): Promise<ChunkReceiptLike | undefined>;
  completeUpload(uploadId: string): Promise<CompletedUploadLike | undefined>;
  cancelUpload(uploadId: string): Promise<unknown>;
}

/** Reads `length` bytes from `offset`; never asked for more than one chunk at a time. */
export type SliceReader = (uri: string, offset: number, length: number) => Promise<Uint8Array>;

/** Tries one chunk gets, the first one included, before the run gives up. */
export const MAX_CHUNK_ATTEMPTS = 3;
/** Waits before the second and third try. */
export const CHUNK_RETRY_DELAYS_MS = [500, 1500];

/**
 * The host's status, when the failure carries one (`ApiError`). Read by shape rather
 * than by class so this module stays free of the transport.
 */
function statusOf(error: unknown): number | undefined {
  if (error === null || typeof error !== 'object') return undefined;
  const status = (error as { status?: unknown }).status;
  return typeof status === 'number' ? status : undefined;
}

/**
 * Whether sending the same chunk again could ever work. A refused body, an upload the
 * host has already forgotten and a size it will not take are answers, not hiccups; too
 * many requests and a host that is busy are worth waiting out, and so is a failure with
 * no status at all (a dropped frame).
 */
export function isRetryableUploadError(error: unknown): boolean {
  const status = statusOf(error);
  if (status === undefined) return true;
  return status === 429 || status >= 500;
}

/** The host's running byte count, when the receipt carries a usable one. */
export function receivedBytesOf(receipt: ChunkReceiptLike | undefined): number | undefined {
  const received = receipt?.received;
  return typeof received === 'number' && Number.isFinite(received) ? received : undefined;
}

/** The id `/complete` gave the finished attachment, when the host sent one. */
export function attachmentIdOf(completed: CompletedUploadLike | undefined): string | undefined {
  const id = completed?.attachment?.id;
  return typeof id === 'string' && id.length > 0 ? id : undefined;
}

const realSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export interface UploadProgress {
  /** Index in the list being uploaded. */
  file: number;
  sentBytes: number;
  totalBytes: number;
}

export interface UploadRun {
  transport: UploadTransport;
  readSlice: SliceReader;
  sessionId: string;
  files: readonly PickedFile[];
  onProgress?: (progress: UploadProgress) => void;
  /** Polled between chunks; true stops the run and cancels what was started. */
  isCancelled?: () => boolean;
  /** The backoff wait; replaced in tests so a retry costs no real time. */
  sleep?: (ms: number) => Promise<void>;
}

export type UploadOutcome =
  | {
      ok: true;
      /** Every upload this run opened, in order; what a later failure has to cancel. */
      uploadIds: string[];
      /** The ids `/complete` answered with — what `submit.attachments` carries. */
      attachmentIds: string[];
    }
  | { ok: false; error: string; cancelled: boolean };

class CancelledUpload extends Error {
  constructor() {
    super('첨부 업로드를 취소했습니다.');
    this.name = 'CancelledUpload';
  }
}

/**
 * Uploads every file in order: open, send the chunks from 0, complete. A chunk that
 * fails for a reason that could pass is sent again, up to `MAX_CHUNK_ATTEMPTS` times
 * with a growing wait; anything else — and any cancel — stops at once and cancels every
 * upload this run opened, so the host is not left holding a half-written file for ten
 * minutes. The picked files stay on screen, so the caller can simply send again.
 */
export async function uploadAttachments(run: UploadRun): Promise<UploadOutcome> {
  const opened: string[] = [];
  const attachmentIds: string[] = [];
  const cancelled = () => run.isCancelled?.() === true;
  const sleep = run.sleep ?? realSleep;

  /**
   * One chunk, retried while the host's answer says it is worth retrying. A chunk the
   * host already took answers 409 under its strict ordering, so on a *retry* that means
   * "I have it" and the run moves on; on the first attempt it is a real refusal.
   * Answers with the host's running byte count when it sent one.
   */
  const sendChunk = async (
    uploadId: string,
    index: number,
    dataBase64: string,
  ): Promise<number | undefined> => {
    for (let attempt = 1; ; attempt += 1) {
      try {
        return receivedBytesOf(await run.transport.uploadChunk(uploadId, index, dataBase64));
      } catch (error) {
        if (attempt > 1 && statusOf(error) === 409) return undefined;
        if (attempt >= MAX_CHUNK_ATTEMPTS || !isRetryableUploadError(error)) throw error;
        await sleep(CHUNK_RETRY_DELAYS_MS[attempt - 1] ?? 0);
        if (cancelled()) throw new CancelledUpload();
      }
    }
  };

  /** The file the run is on, so a refusal can say which one the host turned down. */
  let current: string | undefined;

  try {
    for (let index = 0; index < run.files.length; index += 1) {
      const file = run.files[index];
      if (!file) continue;
      if (cancelled()) throw new CancelledUpload();
      current = file.name;

      const ticket = await run.transport.createUpload(run.sessionId, {
        name: file.name,
        size: file.size,
        mimeType: file.mimeType,
      });
      opened.push(ticket.uploadId);

      run.onProgress?.({ file: index, sentBytes: 0, totalBytes: file.size });
      let sent = 0;
      for (const chunk of planChunks(file.size, ticket.chunkSize)) {
        if (cancelled()) throw new CancelledUpload();
        const bytes = await run.readSlice(file.uri, chunk.offset, chunk.length);
        // The size was declared from the file on disk; a short read means the file
        // changed under us, and sending it anyway would only earn a 400 at `/complete`.
        if (bytes.length !== chunk.length) {
          // The file's name is added below, with every other refusal's.
          throw new Error('파일을 읽는 중 크기가 달라졌습니다.');
        }
        const received = await sendChunk(ticket.uploadId, chunk.index, encodeChunk(bytes));
        sent += chunk.length;
        if (received !== undefined && received !== sent) {
          throw new Error('호스트가 받은 크기가 보낸 크기와 다릅니다.');
        }
        run.onProgress?.({ file: index, sentBytes: sent, totalBytes: file.size });
      }

      if (cancelled()) throw new CancelledUpload();
      const completed = await run.transport.completeUpload(ticket.uploadId);
      // The host names the attachment; an older one that answers nothing usable leaves
      // the upload id, which is what `submit` took before `/complete` reported an id.
      attachmentIds.push(attachmentIdOf(completed) ?? ticket.uploadId);
    }
    return { ok: true, uploadIds: opened, attachmentIds };
  } catch (error) {
    // Best effort: an upload the host already forgot answers 404, which changes nothing.
    await Promise.all(
      opened.map((uploadId) => run.transport.cancelUpload(uploadId).catch(() => undefined)),
    );
    if (error instanceof CancelledUpload) {
      return { ok: false, cancelled: true, error: error.message };
    }
    // The host's own words (413 한도 초과, 429 너무 잦음, 400 크기 불일치) with the file
    // they were said about, which is the only part the host cannot know to mention.
    const reason = error instanceof Error && error.message ? error.message : '첨부를 보내지 못했습니다.';
    return { ok: false, cancelled: false, error: current ? `${current}: ${reason}` : reason };
  }
}
