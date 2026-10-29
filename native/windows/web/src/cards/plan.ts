import type { Translate } from '../i18n/locale';
import { entryTime } from '../conversation/format';
import type { PlanRecord as StoredPlanRecord } from '../store/models';
import type { PlanRecord } from './types';

/** ClaudePlanMode.MaximumFeedbackBytes: the trimmed UTF-8 payload, not JavaScript's UTF-16 length. */
export const maximumPlanFeedbackBytes = 16_384;

export function planFeedbackState(feedback: string): 'empty' | 'valid' | 'tooLong' {
  const text = feedback.trim();
  if (text.length === 0) return 'empty';
  return new TextEncoder().encode(text).length > maximumPlanFeedbackBytes ? 'tooLong' : 'valid';
}

/** The same record presentation the host supplies to the history strip, for records carried by the graph snapshot. */
export function planRecordPresentation(record: StoredPlanRecord, t: Translate, language: string): PlanRecord {
  const outcome = ['approvedAuto', 'approvedConfirm', 'revised'].includes(record.outcome) ? record.outcome : 'cancelled';
  const received = entryTime(record.receivedAt, language);
  return {
    id: record.id,
    outcomeTitle: t('plan.outcome.' + outcome),
    tone: outcome === 'revised' ? 'wait' : outcome === 'cancelled' ? 'stop' : 'done',
    time: entryTime(record.decidedAt, language),
    received: received ? t('plan.card.received', { time: received }) : '',
    headline: record.plan.split(/\r?\n/).find((line) => line.trim().length > 0)?.trim().replace(/^#+\s*/, '') ?? '',
    plan: record.plan,
    truncated: record.planTruncated === true,
    feedback: record.feedback,
  };
}
