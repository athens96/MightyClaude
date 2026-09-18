import { describeError } from '@/api/client';
import type { SubmitMode, SubmitOptions, SubmitResponse } from '@/api/types';
import type { UploadOutcome } from '@/lib/uploads';

/**
 * Upload → submit → unwind, in one place so the unwind can be tested. The host keeps at
 * most 16 open uploads per pane and holds a finished one for ten minutes, so a `submit`
 * that fails after the files went up must give them back: without that, three failed
 * sends fill the pane's slots and the fourth attempt cannot even open an upload.
 *
 * Pure: the client is injected, nothing here touches React or the store.
 */

export interface SendClient {
  submit(sessionId: string, text: string, options?: SubmitOptions): Promise<SubmitResponse>;
  cancelUpload(uploadId: string): Promise<unknown>;
}

export interface SendRequest {
  client: SendClient;
  sessionId: string;
  text: string;
  mode?: SubmitMode;
  /** Runs the chunked upload; left out when nothing is attached. */
  upload?: () => Promise<UploadOutcome>;
}

export type SendResult =
  | { ok: true; accepted: string }
  | { ok: false; error: string; cancelled: boolean };

export async function sendWithAttachments(request: SendRequest): Promise<SendResult> {
  let uploadIds: string[] = [];
  const options: SubmitOptions = {};
  if (request.mode) options.mode = request.mode;

  if (request.upload) {
    const outcome = await request.upload();
    // A failed or cancelled run has already cancelled every upload it opened.
    if (!outcome.ok) return { ok: false, error: outcome.error, cancelled: outcome.cancelled };
    uploadIds = outcome.uploadIds;
    if (outcome.attachmentIds.length > 0) options.attachments = outcome.attachmentIds;
  }

  try {
    const result = await request.client.submit(request.sessionId, request.text, options);
    return { ok: true, accepted: result.accepted };
  } catch (error) {
    // Best effort: an upload the host already forgot answers 404, which changes nothing.
    await Promise.all(
      uploadIds.map((uploadId) => request.client.cancelUpload(uploadId).catch(() => undefined)),
    );
    return { ok: false, error: describeError(error), cancelled: false };
  }
}
