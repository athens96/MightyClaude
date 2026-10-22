import { ApiError } from '@/api/client';
import { resetLanguage, t } from '@/lib/i18n';
import { answerFromNotification, type PushActionDeps } from '@/lib/push/actions';
import type { PushMessage } from '@/lib/push/message';
import {
  actionsFor,
  CHANNEL_ATTENTION,
  CHANNEL_WORK,
  notificationData,
  notificationFor,
  outcomeUpdate,
  sessionPath,
  type PushNotificationData,
} from '@/lib/push/notification';

beforeAll(() => resetLanguage('ko'));

const HANDLE = 'h'.repeat(43);
const message = (overrides: Partial<PushMessage>): PushMessage => ({
  v: 1, id: 'request:run-1:req-1', kind: 'permission', hostId: 'host-1', hostName: 'Test Mac', workspace: 'demo',
  title: 'Claude 1', summary: 'git status', sessionId: 'pane-1', at: '2026-10-23T01:02:03Z',
  permission: { requestId: 'req-1', runId: 'run-1', buttons: 'claude' }, ...overrides,
});

describe('notificationFor', () => {
  it('draws a permission request with allow and deny on the attention channel', () => {
    const spec = notificationFor(message({}), 'host-a', HANDLE);
    expect(spec.id).toBe(`${HANDLE}:request:run-1:req-1`);
    expect(spec.channelId).toBe(CHANNEL_ATTENTION);
    expect(spec.title).toBe(t('phone.push.title.permission', { title: 'Claude 1' }));
    expect(spec.body).toBe('git status');
    expect(spec.subText).toBe('Test Mac · demo');
    expect(spec.actions).toEqual([{ id: 'allow', title: t('permission.allow') }, { id: 'deny', title: t('permission.deny') }]);
    expect(notificationData(spec.data)).toEqual({ storageId: 'host-a', sessionId: 'pane-1', kind: 'permission', requestId: 'req-1', runId: 'run-1', buttons: 'claude' });
  });

  it('gives finished, failed and sign-in one status slot per session, with a fallback text', () => {
    const done = notificationFor(message({ id: 'run:pane-1:1:completed', kind: 'completed', summary: '' }), 'host-a', HANDLE);
    const lost = notificationFor(message({ id: 'signin:pane-1:1', kind: 'signin', summary: '' }), 'host-a', HANDLE);
    expect(done.id).toBe(`${HANDLE}:pane-1:status`);
    expect(lost.id).toBe(done.id);
    expect(done.channelId).toBe(CHANNEL_WORK);
    expect(done.body).toBe(t('phone.push.body.completed'));
    expect(done.actions).toEqual([]);
  });

  it('offers Antigravity choices the card allows and none for a question', () => {
    expect(actionsFor('antigravity').map((a) => a.id)).toEqual(['once', 'always', 'deny']);
    expect(actionsFor('antigravityOnce').map((a) => a.id)).toEqual(['once', 'deny']);
    expect(actionsFor('antigravity')[1]?.title).toBe(t('antigravity.permission.allowAlways'));
    expect(actionsFor('none')).toEqual([]);
    expect(notificationFor(message({ kind: 'question', permission: undefined }), 'host-a', HANDLE).actions).toEqual([]);
  });

  it('opens the session the notification is about', () => {
    expect(sessionPath({ storageId: 'host a', sessionId: 'pane/1', kind: 'question' })).toBe('/host/host%20a/session/pane%2F1');
    expect(notificationData({ storageId: 'a' })).toBeNull();
  });

  it('says what happened after a button', () => {
    expect(outcomeUpdate('allowed')).toEqual({ body: t('phone.push.outcome.allowed'), keepActions: false, opensApp: false });
    expect(outcomeUpdate('already')).toEqual({ body: t('phone.push.outcome.already'), keepActions: false, opensApp: false });
    expect(outcomeUpdate('locked')).toEqual({ body: t('phone.push.outcome.locked'), keepActions: true, opensApp: false });
    expect(outcomeUpdate('failed')).toEqual({ body: t('phone.push.outcome.failed'), keepActions: false, opensApp: true });
  });
});

describe('answerFromNotification', () => {
  const claude: PushNotificationData = { storageId: 'host-a', sessionId: 'pane-1', kind: 'permission', requestId: 'req-1', runId: 'run-1', buttons: 'claude' };
  const agy: PushNotificationData = { ...claude, requestId: 'agy-1', buttons: 'antigravity' };

  function deps(overrides: Partial<PushActionDeps> = {}): PushActionDeps & { sent: unknown[] } {
    const sent: unknown[] = [];
    return {
      sent,
      isDeviceLocked: () => false,
      respond: async (_storage, _session, body) => void sent.push(body),
      stillPending: async () => false,
      ...overrides,
    };
  }

  it('answers with the existing permission route', async () => {
    const d = deps();
    expect(await answerFromNotification(d, claude, 'allow')).toBe('allowed');
    expect(await answerFromNotification(d, claude, 'deny')).toBe('denied');
    expect(await answerFromNotification(d, agy, 'always')).toBe('allowed');
    expect(d.sent).toEqual([
      { requestId: 'req-1', runId: 'run-1', allow: true },
      { requestId: 'req-1', runId: 'run-1', allow: false },
      { requestId: 'agy-1', runId: 'run-1', allow: true, always: true },
    ]);
  });

  it('does nothing while the phone is locked', async () => {
    const d = deps({ isDeviceLocked: () => true });
    expect(await answerFromNotification(d, claude, 'allow')).toBe('locked');
    expect(d.sent).toEqual([]);
  });

  it('refuses a button the notification did not offer', async () => {
    const d = deps();
    expect(await answerFromNotification(d, { ...agy, buttons: 'antigravityOnce' }, 'always')).toBe('failed');
    expect(await answerFromNotification(d, claude, 'once')).toBe('failed');
    expect(await answerFromNotification(d, { ...claude, kind: 'question' }, 'allow')).toBe('failed');
    expect(d.sent).toEqual([]);
  });

  it('calls a 409 already handled only when the request is gone', async () => {
    const conflict = async (): Promise<void> => { throw new ApiError(409, 'not pending'); };
    expect(await answerFromNotification(deps({ respond: conflict, stillPending: async () => false }), claude, 'allow')).toBe('already');
    expect(await answerFromNotification(deps({ respond: conflict, stillPending: async () => true }), claude, 'allow')).toBe('failed');
    expect(await answerFromNotification(deps({ respond: conflict, stillPending: async () => { throw new Error('offline'); } }), claude, 'allow')).toBe('failed');
    expect(await answerFromNotification(deps({ respond: async () => { throw new Error('timeout'); } }), claude, 'allow')).toBe('failed');
  });
});
