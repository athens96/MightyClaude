import { fromBase64 } from '@/api/relay/crypto';
import {
  DEFAULT_CHUNK_SIZE,
  MAX_ATTACHMENTS,
  MAX_ATTACHMENTS_TOTAL_BYTES,
  MAX_ATTACHMENT_BYTES,
  MAX_CHUNK_BYTES,
} from '@/api/types';
import {
  CHUNK_RETRY_DELAYS_MS,
  MAX_CHUNK_ATTEMPTS,
  acceptFiles,
  chunkSizeFor,
  encodeChunk,
  formatBytes,
  isRetryableUploadError,
  planChunks,
  uploadAttachments,
  type PickedFile,
  type UploadProgress,
  type UploadTransport,
} from '@/lib/uploads';

function file(name: string, size: number, uri = `file:///${name}`): PickedFile {
  return { uri, name, size };
}

interface Recorded {
  call: string;
  uploadId?: string;
  index?: number;
  bytes?: number;
}

function fakeTransport(
  overrides: {
    chunkSize?: number;
    failOnChunk?: number;
    /** Fails `/complete` for this upload id; `true` fails the first one. */
    failOnComplete?: boolean | string;
    /** Status put on the chunk failure, as `ApiError` carries one. */
    chunkStatus?: number;
    /** Attempts that fail before the chunk goes through; unset means every attempt. */
    chunkFailures?: number;
    /**
     * The host takes this chunk but its answer never arrives, so the app sends it again
     * and the host — which keeps strict order — answers 409 from then on.
     */
    lostAnswerOnChunk?: number;
    /** Host's running byte count on every receipt; off by default. */
    reportReceived?: boolean;
    /** Byte count the host claims, whatever it actually received. */
    receivedOverride?: number;
    /** `attachment.id` the host answers `/complete` with. */
    attachmentIdPrefix?: string;
  } = {},
) {
  const log: Recorded[] = [];
  let next = 0;
  let chunkAttempts = 0;
  let received = 0;
  const transport: UploadTransport = {
    createUpload: (_sessionId, input) => {
      next += 1;
      received = 0;
      log.push({ call: 'create', uploadId: `u${next}`, bytes: input.size });
      return Promise.resolve({
        uploadId: `u${next}`,
        chunkSize: overrides.chunkSize ?? DEFAULT_CHUNK_SIZE,
      });
    },
    uploadChunk: (uploadId, index, dataBase64) => {
      const bytes = fromBase64(dataBase64).length;
      log.push({ call: 'chunk', uploadId, index, bytes });
      if (overrides.lostAnswerOnChunk === index) {
        chunkAttempts += 1;
        if (chunkAttempts === 1) {
          // Taken, then the answer is lost on the way back.
          received += bytes;
          return Promise.reject(hostError('연결이 끊겼습니다'));
        }
        return Promise.reject(hostError('이미 받은 청크입니다', 409));
      }
      if (overrides.failOnChunk === index) {
        chunkAttempts += 1;
        if (overrides.chunkFailures === undefined || chunkAttempts <= overrides.chunkFailures) {
          return Promise.reject(hostError('청크 실패', overrides.chunkStatus));
        }
      }
      received += bytes;
      if (!overrides.reportReceived) return Promise.resolve(undefined);
      return Promise.resolve({ received: overrides.receivedOverride ?? received });
    },
    completeUpload: (uploadId) => {
      log.push({ call: 'complete', uploadId });
      const fails =
        overrides.failOnComplete === true || overrides.failOnComplete === uploadId;
      if (fails) return Promise.reject(new Error('크기가 다릅니다'));
      if (!overrides.attachmentIdPrefix) return Promise.resolve(undefined);
      return Promise.resolve({
        attachment: { id: `${overrides.attachmentIdPrefix}${uploadId}` },
      });
    },
    cancelUpload: (uploadId) => {
      log.push({ call: 'cancel', uploadId });
      return Promise.resolve(undefined);
    },
  };
  return { transport, log };
}

/** A failure shaped like the `ApiError` the client raises from a host answer. */
function hostError(message: string, status?: number): Error {
  const error = new Error(message);
  if (status !== undefined) Object.assign(error, { status });
  return error;
}

/** Waits nothing, so a backoff costs no real time; records what it was asked to wait. */
function fakeSleep() {
  const waits: number[] = [];
  return {
    waits,
    sleep: (ms: number) => {
      waits.push(ms);
      return Promise.resolve();
    },
  };
}

/** Deterministic bytes, so a slice can be checked against its offset. */
const readSlice = (_uri: string, offset: number, length: number) =>
  Promise.resolve(Uint8Array.from({ length }, (_, index) => (offset + index) % 256));

describe('formatBytes', () => {
  it('reads sizes the way a chip shows them', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(900)).toBe('900 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB');
    expect(formatBytes(-1)).toBe('0 B');
  });
});

describe('chunkSizeFor', () => {
  it('falls back, floors and never exceeds the size the contract documents', () => {
    expect(chunkSizeFor(undefined)).toBe(DEFAULT_CHUNK_SIZE);
    expect(chunkSizeFor(0)).toBe(DEFAULT_CHUNK_SIZE);
    expect(chunkSizeFor(Number.NaN)).toBe(DEFAULT_CHUNK_SIZE);
    expect(chunkSizeFor(1024.9)).toBe(1024);
    expect(chunkSizeFor(1024 * 1024)).toBe(DEFAULT_CHUNK_SIZE);
    expect(chunkSizeFor(MAX_CHUNK_BYTES)).toBe(DEFAULT_CHUNK_SIZE);
    expect(DEFAULT_CHUNK_SIZE).toBe(196_608);
    // The clamp is what keeps one chunk's base64 under the 300 KiB body limit.
    expect(Math.ceil(DEFAULT_CHUNK_SIZE / 3) * 4).toBeLessThanOrEqual(300 * 1024);
  });
});

describe('planChunks', () => {
  it('walks the file in order and leaves the remainder to the last chunk', () => {
    expect(planChunks(250, 100)).toEqual([
      { index: 0, offset: 0, length: 100 },
      { index: 1, offset: 100, length: 100 },
      { index: 2, offset: 200, length: 50 },
    ]);
  });

  it('makes one chunk when the size divides exactly and none for an empty file', () => {
    expect(planChunks(200, 100)).toEqual([
      { index: 0, offset: 0, length: 100 },
      { index: 1, offset: 100, length: 100 },
    ]);
    expect(planChunks(40, 100)).toEqual([{ index: 0, offset: 0, length: 40 }]);
    expect(planChunks(0, 100)).toEqual([]);
  });

  it('uses the documented chunk size for a real attachment', () => {
    const plans = planChunks(MAX_ATTACHMENT_BYTES, DEFAULT_CHUNK_SIZE);
    expect(plans).toHaveLength(Math.ceil(MAX_ATTACHMENT_BYTES / DEFAULT_CHUNK_SIZE));
    expect(plans.at(-1)).toEqual({
      index: plans.length - 1,
      offset: (plans.length - 1) * DEFAULT_CHUNK_SIZE,
      length: MAX_ATTACHMENT_BYTES - (plans.length - 1) * DEFAULT_CHUNK_SIZE,
    });
  });
});

describe('encodeChunk', () => {
  it('base64-encodes arbitrary binary bytes, padding included', () => {
    expect(encodeChunk(Uint8Array.from([0, 1, 2]))).toBe('AAEC');
    expect(encodeChunk(Uint8Array.from([0xff, 0x00]))).toBe('/wA=');
    expect(encodeChunk(Uint8Array.from([0xfb]))).toBe('+w==');
    const binary = Uint8Array.from({ length: 256 }, (_, index) => index);
    expect(fromBase64(encodeChunk(binary))).toEqual(binary);
    expect(encodeChunk(new Uint8Array(0))).toBe('');
  });
});

describe('acceptFiles', () => {
  it('keeps everything that fits', () => {
    const result = acceptFiles([file('a.png', 10)], [file('b.png', 20)]);
    expect(result.files.map((entry) => entry.name)).toEqual(['a.png', 'b.png']);
    expect(result.error).toBeUndefined();
  });

  it('ignores a file that is already attached', () => {
    const existing = file('a.png', 10);
    const result = acceptFiles([existing], [existing]);
    expect(result.files).toHaveLength(1);
  });

  it('refuses a file whose size is 0 or unknown, and names it', () => {
    const result = acceptFiles([], [file('empty.txt', 0), file('nan.txt', Number.NaN)]);
    expect(result.files).toHaveLength(0);
    expect(result.error).toContain('empty.txt');
    expect(result.error).toContain('nan.txt');
    expect(result.error).toContain('읽을 수 없거나 비어 있습니다');
  });

  it('refuses a file over 5 MB and names it', () => {
    const result = acceptFiles([], [file('big.mov', MAX_ATTACHMENT_BYTES + 1)]);
    expect(result.files).toHaveLength(0);
    expect(result.error).toContain('big.mov');
    expect(result.error).toContain('5.0 MB');
  });

  it('stops at eight files', () => {
    const current = Array.from({ length: MAX_ATTACHMENTS }, (_, index) =>
      file(`f${index}.txt`, 1),
    );
    const result = acceptFiles(current, [file('extra.txt', 1)]);
    expect(result.files).toHaveLength(MAX_ATTACHMENTS);
    expect(result.error).toContain(`${MAX_ATTACHMENTS}개`);
  });

  it('stops at 8 MB in total, taking what still fits', () => {
    const current = [file('a.bin', MAX_ATTACHMENT_BYTES)];
    const room = MAX_ATTACHMENTS_TOTAL_BYTES - MAX_ATTACHMENT_BYTES;
    const result = acceptFiles(current, [file('b.bin', room), file('c.bin', 1024)]);
    expect(result.files.map((entry) => entry.name)).toEqual(['a.bin', 'b.bin']);
    expect(result.error).toContain('c.bin');
    expect(result.error).toContain('8.0 MB');
  });
});

describe('uploadAttachments', () => {
  it('opens, chunks in order and completes every file', async () => {
    const { transport, log } = fakeTransport({ chunkSize: 100, attachmentIdPrefix: 'att-' });
    const progress: UploadProgress[] = [];
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250), file('b.bin', 40)],
      onProgress: (entry) => progress.push(entry),
    });

    // `submit` carries what `/complete` named, not the upload ids.
    expect(outcome).toEqual({
      ok: true,
      uploadIds: ['u1', 'u2'],
      attachmentIds: ['att-u1', 'att-u2'],
    });
    expect(log).toEqual([
      { call: 'create', uploadId: 'u1', bytes: 250 },
      { call: 'chunk', uploadId: 'u1', index: 0, bytes: 100 },
      { call: 'chunk', uploadId: 'u1', index: 1, bytes: 100 },
      { call: 'chunk', uploadId: 'u1', index: 2, bytes: 50 },
      { call: 'complete', uploadId: 'u1' },
      { call: 'create', uploadId: 'u2', bytes: 40 },
      { call: 'chunk', uploadId: 'u2', index: 0, bytes: 40 },
      { call: 'complete', uploadId: 'u2' },
    ]);
    expect(progress.at(-1)).toEqual({ file: 1, sentBytes: 40, totalBytes: 40 });
  });

  it('sends a chunk again after a backoff and carries on where it was', async () => {
    const { transport, log } = fakeTransport({
      chunkSize: 100,
      failOnChunk: 1,
      chunkStatus: 429,
      chunkFailures: 1,
    });
    const { sleep, waits } = fakeSleep();
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250)],
      sleep,
    });

    expect(outcome).toEqual({ ok: true, uploadIds: ['u1'], attachmentIds: ['u1'] });
    expect(waits).toEqual([CHUNK_RETRY_DELAYS_MS[0]]);
    // Chunk 1 went out twice; the chunks around it once each, in order.
    expect(
      log.filter((entry) => entry.call === 'chunk').map((entry) => entry.index),
    ).toEqual([0, 1, 1, 2]);
    expect(log.filter((entry) => entry.call === 'cancel')).toHaveLength(0);
  });

  it('gives up after three tries and cancels every upload it opened', async () => {
    const { transport, log } = fakeTransport({ chunkSize: 100, failOnChunk: 1, chunkStatus: 503 });
    const { sleep, waits } = fakeSleep();
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250)],
      sleep,
    });

    expect(outcome).toEqual({ ok: false, cancelled: false, error: 'a.bin: 청크 실패' });
    expect(waits).toHaveLength(MAX_CHUNK_ATTEMPTS - 1);
    expect(log.filter((entry) => entry.call === 'chunk' && entry.index === 1)).toHaveLength(
      MAX_CHUNK_ATTEMPTS,
    );
    expect(log.at(-1)).toEqual({ call: 'cancel', uploadId: 'u1' });
  });

  it('does not retry an answer that will never change', async () => {
    const { transport, log } = fakeTransport({ chunkSize: 100, failOnChunk: 0, chunkStatus: 413 });
    const { sleep, waits } = fakeSleep();
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250)],
      sleep,
    });

    expect(outcome).toEqual({ ok: false, cancelled: false, error: 'a.bin: 청크 실패' });
    expect(waits).toEqual([]);
    expect(log.filter((entry) => entry.call === 'chunk')).toHaveLength(1);
    expect(log.at(-1)).toEqual({ call: 'cancel', uploadId: 'u1' });
  });

  it('knows which host answers are worth sending again', () => {
    expect(isRetryableUploadError(hostError('끊김'))).toBe(true);
    expect(isRetryableUploadError(hostError('너무 잦습니다', 429))).toBe(true);
    expect(isRetryableUploadError(hostError('바쁩니다', 503))).toBe(true);
    expect(isRetryableUploadError(hostError('한도 초과', 413))).toBe(false);
    expect(isRetryableUploadError(hostError('크기가 다릅니다', 400))).toBe(false);
    expect(isRetryableUploadError(hostError('없는 업로드', 404))).toBe(false);
  });

  it('cancels the finished files too when a later one fails', async () => {
    // The first file completes; the second one's `/complete` is what fails, so the
    // unwind has to give back an upload the host had already sealed.
    const { transport, log } = fakeTransport({ chunkSize: 100, failOnComplete: 'u2' });
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 10), file('b.bin', 10)],
    });

    expect(outcome).toEqual({ ok: false, cancelled: false, error: 'b.bin: 크기가 다릅니다' });
    expect(log.filter((entry) => entry.call === 'complete').map((entry) => entry.uploadId)).toEqual([
      'u1',
      'u2',
    ]);
    expect(log.filter((entry) => entry.call === 'cancel').map((entry) => entry.uploadId)).toEqual([
      'u1',
      'u2',
    ]);
  });

  it('takes a 409 on a retry as "already received" and carries on', async () => {
    // Under strict ordering the host answers 409 for a chunk it has already taken, which
    // is exactly what a re-sent chunk looks like after a dropped answer.
    const { transport, log } = fakeTransport({
      chunkSize: 100,
      lostAnswerOnChunk: 1,
      reportReceived: true,
    });
    const { sleep, waits } = fakeSleep();
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250)],
      sleep,
    });

    expect(outcome).toEqual({ ok: true, uploadIds: ['u1'], attachmentIds: ['u1'] });
    expect(waits).toEqual([CHUNK_RETRY_DELAYS_MS[0]]);
    expect(log.filter((entry) => entry.call === 'chunk').map((entry) => entry.index)).toEqual([
      0, 1, 1, 2,
    ]);
    // The running check still adds up afterwards, so the file is sealed, not cancelled.
    expect(log.at(-1)).toEqual({ call: 'complete', uploadId: 'u1' });
    expect(log.filter((entry) => entry.call === 'cancel')).toHaveLength(0);
  });

  it('keeps a first-attempt 409 fatal', async () => {
    const { transport, log } = fakeTransport({ chunkSize: 100, failOnChunk: 1, chunkStatus: 409 });
    const { sleep, waits } = fakeSleep();
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250)],
      sleep,
    });

    expect(outcome).toEqual({ ok: false, cancelled: false, error: 'a.bin: 청크 실패' });
    expect(waits).toEqual([]);
    expect(log.filter((entry) => entry.call === 'chunk' && entry.index === 1)).toHaveLength(1);
    expect(log.at(-1)).toEqual({ call: 'cancel', uploadId: 'u1' });
  });

  it('stops when the host says it received something other than what went out', async () => {
    const { transport, log } = fakeTransport({
      chunkSize: 100,
      reportReceived: true,
      receivedOverride: 40,
    });
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 250)],
    });

    expect(outcome).toEqual({
      ok: false,
      cancelled: false,
      error: 'a.bin: 호스트가 받은 크기가 보낸 크기와 다릅니다.',
    });
    expect(log.filter((entry) => entry.call === 'complete')).toHaveLength(0);
    expect(log.at(-1)).toEqual({ call: 'cancel', uploadId: 'u1' });
  });

  it('refuses a file that reads short of the size it was opened with', async () => {
    const { transport, log } = fakeTransport({ chunkSize: 100 });
    const outcome = await uploadAttachments({
      transport,
      // The file shrank between the pick and the send: chunk 1 reads nothing.
      readSlice: (uri, offset, length) =>
        readSlice(uri, offset, offset >= 100 ? 0 : length),
      sessionId: 's1',
      files: [file('a.bin', 250)],
    });

    expect(outcome).toEqual({
      ok: false,
      cancelled: false,
      error: 'a.bin: 파일을 읽는 중 크기가 달라졌습니다.',
    });
    expect(log.filter((entry) => entry.call === 'chunk')).toHaveLength(1);
    expect(log.at(-1)).toEqual({ call: 'cancel', uploadId: 'u1' });
  });

  it('stops between chunks when the user cancels', async () => {
    const { transport, log } = fakeTransport({ chunkSize: 100 });
    let sent = 0;
    const outcome = await uploadAttachments({
      transport,
      readSlice,
      sessionId: 's1',
      files: [file('a.bin', 1000)],
      onProgress: () => {
        sent += 1;
      },
      isCancelled: () => sent > 2,
    });

    expect(outcome.ok).toBe(false);
    expect(outcome.ok === false && outcome.cancelled).toBe(true);
    expect(log.filter((entry) => entry.call === 'complete')).toHaveLength(0);
    expect(log.at(-1)).toEqual({ call: 'cancel', uploadId: 'u1' });
  });

  it('reads the bytes the plan asked for, one chunk at a time', async () => {
    const { transport } = fakeTransport({ chunkSize: 64 });
    const reads: Array<{ offset: number; length: number }> = [];
    await uploadAttachments({
      transport,
      readSlice: (uri, offset, length) => {
        reads.push({ offset, length });
        return readSlice(uri, offset, length);
      },
      sessionId: 's1',
      files: [file('a.bin', 150)],
    });
    expect(reads).toEqual([
      { offset: 0, length: 64 },
      { offset: 64, length: 64 },
      { offset: 128, length: 22 },
    ]);
  });
});
