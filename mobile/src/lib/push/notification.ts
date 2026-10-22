import { t } from '@/lib/i18n';
import { PUSH_BUTTONS, PUSH_KINDS, type PushButtons, type PushKind, type PushMessage } from '@/lib/push/message';

export const CHANNEL_ATTENTION = 'attention';
export const CHANNEL_WORK = 'work';

const ATTENTION: readonly PushKind[] = ['permission', 'question', 'plan'];

// Literal keys, so the locale checker sees every one in use.
const TITLE: Record<PushKind, string> = {
  completed: 'phone.push.title.completed',
  failed: 'phone.push.title.failed',
  permission: 'phone.push.title.permission',
  question: 'phone.push.title.question',
  plan: 'phone.push.title.plan',
  signin: 'phone.push.title.signin',
};
const BODY: Record<PushKind, string> = {
  completed: 'phone.push.body.completed',
  failed: 'phone.push.body.failed',
  permission: 'phone.push.body.permission',
  question: 'phone.push.body.question',
  plan: 'phone.push.body.plan',
  signin: 'phone.push.body.signin',
};

export type PushAction = 'allow' | 'deny' | 'once' | 'always';

/** What a notification carries for its tap and its buttons (all strings: Android extras). */
export interface PushNotificationData {
  storageId: string;
  sessionId: string;
  kind: PushKind;
  requestId?: string;
  runId?: string;
  buttons?: PushButtons;
}

export interface PushNotificationSpec {
  id: string;
  title: string;
  body: string;
  subText: string;
  channelId: string;
  actions: Array<{ id: PushAction; title: string }>;
  data: Record<string, string>;
}

export function channelName(id: string): string {
  return id === CHANNEL_ATTENTION ? t('phone.push.channel.attention') : t('phone.push.channel.work');
}

export function actionsFor(buttons: PushButtons | undefined): Array<{ id: PushAction; title: string }> {
  switch (buttons) {
    case 'claude':
      return [{ id: 'allow', title: t('permission.allow') }, { id: 'deny', title: t('permission.deny') }];
    case 'antigravity':
      return [
        { id: 'once', title: t('permission.allowOnce') },
        { id: 'always', title: t('antigravity.permission.allowAlways') },
        { id: 'deny', title: t('antigravity.permission.deny') },
      ];
    case 'antigravityOnce':
      return [{ id: 'once', title: t('permission.allowOnce') }, { id: 'deny', title: t('antigravity.permission.deny') }];
    default:
      return [];
  }
}

export function notificationFor(message: PushMessage, storageId: string, handle: string): PushNotificationSpec {
  const attention = ATTENTION.includes(message.kind);
  const data: Record<string, string> = { storageId, sessionId: message.sessionId, kind: message.kind };
  if (message.permission) {
    data['requestId'] = message.permission.requestId;
    data['runId'] = message.permission.runId;
    data['buttons'] = message.permission.buttons;
  }
  return {
    id: attention ? `${handle}:${message.id}` : `${handle}:${message.sessionId}:status`,
    title: t(TITLE[message.kind], { title: message.title }),
    body: message.summary !== '' ? message.summary : t(BODY[message.kind]),
    subText: message.workspace !== '' ? `${message.hostName} · ${message.workspace}` : message.hostName,
    channelId: attention ? CHANNEL_ATTENTION : CHANNEL_WORK,
    actions: message.kind === 'permission' ? actionsFor(message.permission?.buttons) : [],
    data,
  };
}

export function notificationData(data: Record<string, unknown> | undefined): PushNotificationData | null {
  if (!data) return null;
  const { storageId, sessionId, kind, requestId, runId, buttons } = data;
  if (typeof storageId !== 'string' || typeof sessionId !== 'string' || !PUSH_KINDS.includes(kind as PushKind)) return null;
  const parsed: PushNotificationData = { storageId, sessionId, kind: kind as PushKind };
  if (typeof requestId === 'string') parsed.requestId = requestId;
  if (typeof runId === 'string') parsed.runId = runId;
  if (PUSH_BUTTONS.includes(buttons as PushButtons)) parsed.buttons = buttons as PushButtons;
  return parsed;
}

export function sessionPath(data: PushNotificationData): string {
  return `/host/${encodeURIComponent(data.storageId)}/session/${encodeURIComponent(data.sessionId)}`;
}

export type PushOutcome = 'allowed' | 'denied' | 'already' | 'locked' | 'failed';

const OUTCOME: Record<PushOutcome, string> = {
  allowed: 'phone.push.outcome.allowed',
  denied: 'phone.push.outcome.denied',
  already: 'phone.push.outcome.already',
  locked: 'phone.push.outcome.locked',
  failed: 'phone.push.outcome.failed',
};

export function outcomeUpdate(outcome: PushOutcome): { body: string; keepActions: boolean; opensApp: boolean } {
  return { body: t(OUTCOME[outcome]), keepActions: outcome === 'locked', opensApp: outcome === 'failed' };
}
