import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import type { PlanRecord } from '../store/models';
import { Block, type BlockActions } from '../mighty/Block';
import { PlanDocumentDialog } from './PlanDocument';
import { maximumPlanFeedbackBytes, planFeedbackState, planRecordPresentation } from './plan';

const t = translator([{
  'plan.outcome.revised': 'Changes requested', 'plan.outcome.approvedAuto': 'Approved (auto edit)', 'plan.outcome.approvedConfirm': 'Approved (confirm each)',
  'plan.outcome.cancelled': 'Cancelled', 'plan.history.feedback': 'Requested changes', 'plan.history.truncated': 'Only the beginning was kept.', 'plan.card.title': 'Plan',
}]);
const services = { bridge: { call: () => Promise.resolve(null) }, store: {}, t } as unknown as AppServices;
const record: PlanRecord = {
  id: 'revision-1', runId: 'r1', receivedAt: '2026-10-29T10:00:00Z', decidedAt: '2026-10-29T10:10:00Z',
  outcome: 'revised', plan: '# Release plan\n\n1. Publish the result', feedback: '먼저 테스트를 추가해 주세요.', planTruncated: true,
};

describe('plan feedback bytes', () => {
  it('rejects blank feedback, and trims before checking the exact ASCII boundary', () => {
    expect(planFeedbackState(' \n\t ')).toBe('empty');
    expect(planFeedbackState(' \n' + 'a'.repeat(maximumPlanFeedbackBytes) + '\t ')).toBe('valid');
    expect(planFeedbackState('a'.repeat(maximumPlanFeedbackBytes + 1))).toBe('tooLong');
  });

  it('counts Korean and supplementary emoji as UTF-8, not characters or UTF-16 code units', () => {
    expect(planFeedbackState('한'.repeat(5_461) + 'a')).toBe('valid');
    expect(planFeedbackState('한'.repeat(5_461) + 'ab')).toBe('tooLong');
    expect(planFeedbackState('🌱'.repeat(4_096))).toBe('valid');
    expect(planFeedbackState('🌱'.repeat(4_097))).toBe('tooLong');
  });
});

describe('answered plans', () => {
  it('keeps the decision, time, feedback and truncation with the plan in the graph', () => {
    const element = createElement(Block, {
      paneId: 'p', provider: 'claude', id: '2:r1:plan-record:revision-1', kind: 'planRecord', x: 0, y: 0, w: 320, h: 420,
      copy: undefined, extra: { planRecord: record }, visible: true, selected: false, expanded: true, running: false, actions: {} as BlockActions,
    });
    const html = renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, element));
    expect(html).toContain('Changes requested');
    expect(html).toContain(planRecordPresentation(record, t, 'en').time);
    expect(html).toContain('Release plan');
    expect(html).toContain('먼저 테스트를 추가해 주세요.');
    expect(html).toContain('Only the beginning was kept.');
    expect(html).toContain('data-testid="mighty-plan-expand"');
  });

  it('gives every outcome its own label and tone, and tolerates missing time', () => {
    for (const [outcome, title, tone] of [
      ['approvedAuto', 'Approved (auto edit)', 'done'], ['approvedConfirm', 'Approved (confirm each)', 'done'],
      ['revised', 'Changes requested', 'wait'], ['cancelled', 'Cancelled', 'stop'],
    ]) {
      const shown = planRecordPresentation({ ...record, outcome: outcome!, decidedAt: 'invalid' }, t, 'en');
      expect(shown).toMatchObject({ outcomeTitle: title, tone, time: '', headline: 'Release plan', feedback: record.feedback, truncated: true });
    }
  });

  it('keeps the warning and requested changes in the standalone document, too', () => {
    const element = createElement(PlanDocumentDialog, {
      title: 'Plan · Changes requested', subtitle: '10:10 AM', plan: record.plan, feedback: record.feedback, truncated: true, onClose: () => {},
    });
    const html = renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, element));
    expect(html).toContain('aria-modal="true"');
    expect(html).toContain('Plan · Changes requested');
    expect(html).toContain('10:10 AM');
    expect(html).toContain('Release plan');
    expect(html).toContain('먼저 테스트를 추가해 주세요.');
    expect(html).toContain('Only the beginning was kept.');
  });
});
