import { ApiError } from '@/api/client';
import { antigravityAnswerBody, type AntigravityDecision } from '@/lib/antigravity-permission';
import type { PushAction, PushNotificationData, PushOutcome } from '@/lib/push/notification';

/** `POST /m1/sessions/{id}/permission`; `always` only for an Antigravity card. */
export interface PermissionBody {
  requestId: string;
  runId: string;
  allow: boolean;
  always?: boolean;
}

/** The buttons each card kind offers; 항상 허용 never on a card whose actions are once-only. */
const OFFERED: Record<string, readonly PushAction[]> = {
  claude: ['allow', 'deny'],
  antigravity: ['once', 'always', 'deny'],
  antigravityOnce: ['once', 'deny'],
};

/** The answer a button stands for, or null when the notification never offered it. */
export function answerBody(data: PushNotificationData, action: PushAction): PermissionBody | null {
  if (data.kind !== 'permission' || !data.requestId || !data.runId || !data.buttons) return null;
  if (!(OFFERED[data.buttons] ?? []).includes(action)) return null;
  if (data.buttons === 'claude') return { requestId: data.requestId, runId: data.runId, allow: action === 'allow' };
  return antigravityAnswerBody({ id: data.requestId, runId: data.runId }, action as AntigravityDecision);
}

export interface PushActionDeps {
  isDeviceLocked(): boolean;
  respond(storageId: string, sessionId: string, body: PermissionBody): Promise<void>;
  stillPending(storageId: string, sessionId: string, requestId: string): Promise<boolean>;
}

/**
 * Answers from a notification button; the host still decides (the same checks as the app's own
 * card). Nothing is sent while the phone is locked. A 409 is "already handled" only when the
 * request is gone from the host; otherwise the reason has to be read in the app.
 */
export async function answerFromNotification(deps: PushActionDeps, data: PushNotificationData, action: PushAction): Promise<PushOutcome> {
  const body = answerBody(data, action);
  if (body === null) return 'failed';
  if (deps.isDeviceLocked()) return 'locked';
  try {
    await deps.respond(data.storageId, data.sessionId, body);
    return body.allow ? 'allowed' : 'denied';
  } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 409) return 'failed';
    try {
      return (await deps.stillPending(data.storageId, data.sessionId, body.requestId)) ? 'failed' : 'already';
    } catch {
      return 'failed';
    }
  }
}
