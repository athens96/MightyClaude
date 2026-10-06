import { createElement } from 'react';
import { Text, TextInput } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import { createClient, type RelayChannel } from '@/api/client';
import type { MobilePermission, PlanDecisionKind } from '@/api/types';
import { PlanCard } from '@/components/plan-card';
import { Button } from '@/components/ui';
import { resetLanguage, t } from '@/lib/i18n';
import {
  MAXIMUM_FEEDBACK_BYTES,
  answeredToast,
  canSendRevise,
  feedbackTooLong,
  isPlanRequest,
  pendingPlan,
  planAnswerBody,
  receivedText,
  timeText,
} from '@/lib/plan';

// The phone's Markdown component is what draws the plan; here it only shows what it was given.
jest.mock('@/components/assistant-markdown', () => {
  const react = jest.requireActual<typeof import('react')>('react');
  const native = jest.requireActual<typeof import('react-native')>('react-native');
  return {
    AssistantMarkdown: ({ text }: { text: string }) => react.createElement(native.Text, { testID: 'markdown' }, text),
  };
});

const PLAN = '# 로그인 고치기\n\n1. 원인 찾기\n2. 테스트 추가';
const plan: MobilePermission = {
  id: 'plan-1',
  runId: 'run-1',
  toolName: 'ExitPlanMode',
  title: '계획',
  fields: [],
  summary: 'plan',
  canAllow: false,
  plan: PLAN,
  receivedAt: '2027-01-15T08:05:00.000Z',
};
const bash: MobilePermission = { id: 'p', runId: 'run-1', toolName: 'Bash', title: '명령 실행', fields: [], summary: 'ls', canAllow: true };

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

describe('plan approvals on the phone', () => {
  it('knows a plan request by its plan text and picks the first one', () => {
    expect(isPlanRequest(plan)).toBe(true);
    expect(isPlanRequest(bash)).toBe(false);
    expect(isPlanRequest({ ...plan, plan: '  \n ' })).toBe(false);
    expect(pendingPlan([bash, plan, { ...plan, id: 'plan-2' }])?.id).toBe('plan-1');
    expect(pendingPlan([bash])).toBeUndefined();
  });

  it('sends a change request only with words, within the bound the host keeps', () => {
    expect(canSendRevise('')).toBe(false);
    expect(canSendRevise(' \n\t ')).toBe(false);
    expect(canSendRevise(' 테스트 먼저 ')).toBe(true);
    expect(canSendRevise('a'.repeat(MAXIMUM_FEEDBACK_BYTES))).toBe(true);
    expect(canSendRevise('a'.repeat(MAXIMUM_FEEDBACK_BYTES + 1))).toBe(false);
    // Bytes, not characters: three-byte Hangul passes the bound sooner.
    expect(canSendRevise('가'.repeat(Math.floor(MAXIMUM_FEEDBACK_BYTES / 3) + 1))).toBe(false);
    expect(feedbackTooLong('')).toBe(false);
    expect(feedbackTooLong('  ')).toBe(false);
    expect(feedbackTooLong('짧은 요청')).toBe(false);
    expect(feedbackTooLong('a'.repeat(MAXIMUM_FEEDBACK_BYTES + 1))).toBe(true);
  });

  it('builds each answer body, feedback with revise only', () => {
    expect(planAnswerBody(plan, 'approveAutoEdit')).toEqual({ requestId: 'plan-1', runId: 'run-1', decision: 'approveAutoEdit' });
    expect(planAnswerBody(plan, 'cancel', 'ignored')).toEqual({ requestId: 'plan-1', runId: 'run-1', decision: 'cancel' });
    expect(planAnswerBody(plan, 'revise', '  테스트 먼저 ')).toEqual({ requestId: 'plan-1', runId: 'run-1', decision: 'revise', feedback: '테스트 먼저' });
  });

  it('reads times and outcomes from the shared locale keys', () => {
    expect(timeText('2027-01-15T08:05:00.000Z', 'UTC')).toBe('08:05');
    expect(timeText('nope')).toBe('');
    expect(receivedText('2027-01-15T08:05:00.000Z', 'Asia/Seoul')).toBe('17:05 받음');
    expect(receivedText(undefined)).toBe('');
    const toasts = (['approveAutoEdit', 'approveConfirmEach', 'revise', 'cancel'] as PlanDecisionKind[]).map(answeredToast);
    expect(toasts).toEqual(['승인 · 자동 편집', '승인 · 매번 확인', '수정 요청함', '취소됨']);
  });

  it('posts the answer to the plan route', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    const channel: RelayChannel = {
      request: (method, path, body) => {
        calls.push({ method, path, body });
        return Promise.resolve({ status: 200, body: { protocol: 1, ok: true } });
      },
      onNotify: () => () => undefined,
    };
    await createClient(channel).answerPlan('s 1', planAnswerBody(plan, 'revise', '테스트 먼저'));
    expect(calls).toEqual([
      { method: 'POST', path: '/m1/sessions/s%201/plan', body: { requestId: 'plan-1', runId: 'run-1', decision: 'revise', feedback: '테스트 먼저' } },
    ]);
  });
});

describe('the plan card', () => {
  function render(onAnswer: (decision: PlanDecisionKind, feedback?: string) => void, busy = false): ReactTestRenderer {
    let renderer: ReactTestRenderer | undefined;
    act(() => {
      renderer = TestRenderer.create(createElement(PlanCard, { permission: plan, busy, maxBodyHeight: 300, onAnswer }));
    });
    return renderer!;
  }
  function button(renderer: ReactTestRenderer, label: string): ReactTestInstance {
    const found = renderer.root.findAll((node) => node.type === Button && node.props.label === label);
    if (found.length === 0) throw new Error(`no button ${label}`);
    return found[0]!;
  }
  function press(renderer: ReactTestRenderer, label: string) {
    act(() => button(renderer, label).props.onPress());
  }

  it('shows the plan through the Markdown component, its title and when it came', () => {
    const renderer = render(() => undefined);
    const markdown = renderer.root.findAll((node) => node.type === Text && node.props.testID === 'markdown');
    expect(markdown.map((node) => node.props.children)).toEqual([PLAN]);
    const texts = renderer.root.findAllByType(Text).map((node) => node.props.children);
    expect(texts).toContain(t('plan.card.title'));
    expect(texts.some((value) => typeof value === 'string' && value.endsWith('받음'))).toBe(true);
    act(() => renderer.unmount());
  });

  it("answers with each button's own decision", () => {
    const answers: [PlanDecisionKind, string | undefined][] = [];
    const renderer = render((decision, feedback) => answers.push([decision, feedback]));
    press(renderer, t('plan.card.approveAuto'));
    press(renderer, t('plan.card.approveConfirm'));
    press(renderer, t('plan.card.cancel'));
    expect(answers).toEqual([
      ['approveAutoEdit', undefined],
      ['approveConfirmEach', undefined],
      ['cancel', undefined],
    ]);
    act(() => renderer.unmount());
  });

  it('opens a text box for 수정 요청 and sends only once there are words', () => {
    const answers: [PlanDecisionKind, string | undefined][] = [];
    const renderer = render((decision, feedback) => answers.push([decision, feedback]));
    expect(renderer.root.findAllByType(TextInput)).toHaveLength(0);
    press(renderer, t('plan.card.revise'));
    const input = renderer.root.findByType(TextInput);
    expect(button(renderer, t('plan.card.reviseSend')).props.disabled).toBe(true);
    act(() => input.props.onChangeText('   '));
    expect(button(renderer, t('plan.card.reviseSend')).props.disabled).toBe(true);
    expect(renderer.root.findAll((node) => node.type === Text && node.props.testID === 'plan-revise-too-long')).toHaveLength(0);
    act(() => input.props.onChangeText('a'.repeat(MAXIMUM_FEEDBACK_BYTES + 1)));
    expect(button(renderer, t('plan.card.reviseSend')).props.disabled).toBe(true);
    expect(renderer.root.findAll((node) => node.type === Text && node.props.testID === 'plan-revise-too-long')).toHaveLength(1);
    act(() => input.props.onChangeText('테스트를 먼저 써 주세요'));
    expect(button(renderer, t('plan.card.reviseSend')).props.disabled).toBe(false);
    press(renderer, t('plan.card.reviseSend'));
    expect(answers).toEqual([['revise', '테스트를 먼저 써 주세요']]);
    act(() => renderer.unmount());
  });
});
