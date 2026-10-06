import type { MobilePermission, PlanDecisionKind } from '@/api/types';
import { t } from '@/lib/i18n';

/** The most a change request may carry, as the host's Core checks it (ClaudePlanMode.maximumFeedbackBytes). */
export const MAXIMUM_FEEDBACK_BYTES = 16_384;

/** A plan approval the phone can answer with the plan card: it carries its plan text. */
export function isPlanRequest(permission: MobilePermission): boolean {
  return typeof permission.plan === 'string' && permission.plan.trim().length > 0;
}

/** The first pending plan, if any; the screen docks one card for it. */
export function pendingPlan(permissions: readonly MobilePermission[]): MobilePermission | undefined {
  return permissions.find(isPlanRequest);
}

function utf8Bytes(text: string): number {
  return new TextEncoder().encode(text).length;
}

/** 수정 요청 sends only what the host accepts: words, trimmed, within the bound. */
export function canSendRevise(feedback: string): boolean {
  const text = feedback.trim();
  return text.length > 0 && utf8Bytes(text) <= MAXIMUM_FEEDBACK_BYTES;
}

/** Words the change request box holds that are still too long to send. */
export function feedbackTooLong(feedback: string): boolean {
  return feedback.trim().length > 0 && !canSendRevise(feedback);
}

/** The body of `POST …/plan` for one answer; feedback goes with revise only, trimmed. */
export function planAnswerBody(
  permission: Pick<MobilePermission, 'id' | 'runId'>,
  decision: PlanDecisionKind,
  feedback?: string,
): { requestId: string; runId: string; decision: PlanDecisionKind; feedback?: string } {
  const body = { requestId: permission.id, runId: permission.runId, decision };
  return decision === 'revise' ? { ...body, feedback: (feedback ?? '').trim() } : body;
}

/** "HH:mm" in the phone's own time zone; empty when unreadable. */
export function timeText(iso: string | undefined, timeZone?: string): string {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone }).format(date);
}

/** "14:05 받음", or nothing when the host did not say. */
export function receivedText(iso: string | undefined, timeZone?: string): string {
  const time = timeText(iso, timeZone);
  return time ? t('plan.card.received', { time }) : '';
}

/** What the screen says once an answer went through. */
export function answeredToast(decision: PlanDecisionKind): string {
  switch (decision) {
    case 'approveAutoEdit':
      return t('plan.outcome.approvedAuto');
    case 'approveConfirmEach':
      return t('plan.outcome.approvedConfirm');
    case 'revise':
      return t('plan.outcome.revised');
    case 'cancel':
      return t('plan.outcome.cancelled');
  }
}
