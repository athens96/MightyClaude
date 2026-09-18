import type { SubmitOptions, SubmitResponse } from '@/api/types';
import { sendWithAttachments, type SendClient } from '@/lib/send';
import type { UploadOutcome } from '@/lib/uploads';

interface Recorded {
  call: 'submit' | 'cancel';
  text?: string;
  options?: SubmitOptions;
  uploadId?: string;
}

function fakeClient(overrides: { submitError?: Error; accepted?: string } = {}) {
  const log: Recorded[] = [];
  const client: SendClient = {
    submit: (_sessionId, text, options) => {
      log.push({ call: 'submit', text, options });
      if (overrides.submitError) return Promise.reject(overrides.submitError);
      return Promise.resolve({
        protocol: 1,
        accepted: overrides.accepted ?? 'started',
      } as SubmitResponse);
    },
    cancelUpload: (uploadId) => {
      log.push({ call: 'cancel', uploadId });
      return Promise.resolve(undefined);
    },
  };
  return { client, log };
}

const uploaded: UploadOutcome = {
  ok: true,
  uploadIds: ['u1', 'u2'],
  attachmentIds: ['att-1', 'att-2'],
};

describe('sendWithAttachments', () => {
  it('sends the text alone when nothing is attached', async () => {
    const { client, log } = fakeClient({ accepted: 'queued' });
    const result = await sendWithAttachments({
      client,
      sessionId: 's1',
      text: '안녕',
      mode: 'queue',
    });

    expect(result).toEqual({ ok: true, accepted: 'queued' });
    expect(log).toEqual([{ call: 'submit', text: '안녕', options: { mode: 'queue' } }]);
  });

  it('submits the ids /complete answered with, once the upload is through', async () => {
    const { client, log } = fakeClient();
    const result = await sendWithAttachments({
      client,
      sessionId: 's1',
      text: '이거 봐 줘',
      upload: () => Promise.resolve(uploaded),
    });

    expect(result).toEqual({ ok: true, accepted: 'started' });
    expect(log).toEqual([
      { call: 'submit', text: '이거 봐 줘', options: { attachments: ['att-1', 'att-2'] } },
    ]);
  });

  it('gives every upload of the attempt back when submit fails', async () => {
    // Otherwise a few failed sends fill the pane's 16 upload slots and the next attempt
    // cannot even open one.
    const { client, log } = fakeClient({ submitError: new Error('대기열이 가득 찼습니다') });
    const result = await sendWithAttachments({
      client,
      sessionId: 's1',
      text: '이거 봐 줘',
      upload: () => Promise.resolve(uploaded),
    });

    expect(result).toEqual({
      ok: false,
      cancelled: false,
      error: '대기열이 가득 찼습니다',
    });
    expect(log.filter((entry) => entry.call === 'cancel').map((entry) => entry.uploadId)).toEqual([
      'u1',
      'u2',
    ]);
  });

  it('still reports the refusal when the unwind itself fails', async () => {
    const { client } = fakeClient({ submitError: new Error('대기열이 가득 찼습니다') });
    const cancelled: string[] = [];
    const result = await sendWithAttachments({
      client: {
        submit: client.submit,
        cancelUpload: (uploadId) => {
          cancelled.push(uploadId);
          // An upload the host already forgot answers 404.
          return Promise.reject(new Error('없는 업로드'));
        },
      },
      sessionId: 's1',
      text: '이거 봐 줘',
      upload: () => Promise.resolve(uploaded),
    });

    expect(cancelled).toEqual(['u1', 'u2']);
    expect(result).toEqual({ ok: false, cancelled: false, error: '대기열이 가득 찼습니다' });
  });

  it('never submits when the upload failed, and cancels nothing itself', async () => {
    // The run has already cancelled what it opened.
    const { client, log } = fakeClient();
    const result = await sendWithAttachments({
      client,
      sessionId: 's1',
      text: '이거 봐 줘',
      upload: () =>
        Promise.resolve({ ok: false, cancelled: false, error: 'a.png: 한도 초과' }),
    });

    expect(result).toEqual({ ok: false, cancelled: false, error: 'a.png: 한도 초과' });
    expect(log).toEqual([]);
  });

  it('reports a cancelled upload as cancelled, without submitting', async () => {
    const { client, log } = fakeClient();
    const result = await sendWithAttachments({
      client,
      sessionId: 's1',
      text: '이거 봐 줘',
      upload: () =>
        Promise.resolve({ ok: false, cancelled: true, error: '첨부 업로드를 취소했습니다.' }),
    });

    expect(result).toEqual({
      ok: false,
      cancelled: true,
      error: '첨부 업로드를 취소했습니다.',
    });
    expect(log).toEqual([]);
  });

  it('leaves `attachments` out when the upload produced no ids', async () => {
    const { client, log } = fakeClient();
    await sendWithAttachments({
      client,
      sessionId: 's1',
      text: '안녕',
      upload: () => Promise.resolve({ ok: true, uploadIds: [], attachmentIds: [] }),
    });
    expect(log).toEqual([{ call: 'submit', text: '안녕', options: {} }]);
  });
});
