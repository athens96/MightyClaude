import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import { createClient, type RelayChannel } from '@/api/client';
import type { MobileAntigravityAction, MobilePermission } from '@/api/types';
import { AntigravityPermissionCard } from '@/components/antigravity-permission-card';
import { PermissionCard } from '@/components/permission-card';
import { Button } from '@/components/ui';
import {
  ANTIGRAVITY_PERMISSION_TOOL,
  antigravityActions,
  antigravityAnswerBody,
  antigravityAnsweredToast,
  sendAntigravityAnswer,
  type AntigravityDecision,
} from '@/lib/antigravity-permission';
import { resetLanguage, t } from '@/lib/i18n';

/**
 * The phone samples of the file the Mac (`AntigravityPermissionTests`) and Windows read:
 * the card as one MobilePermission and the four answer bodies (docs/mobile-remote.md
 * "Antigravity 권한 카드").
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/antigravity-permissions.json');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  mobile: {
    permission: MobilePermission;
    answers: { body: { requestId: string; runId: string; allow: boolean; always?: boolean }; decision?: AntigravityDecision; status?: number }[];
  };
};
const card = fixture.mobile.permission;

/** A card no rule can allow: the host says `canAllow: false` and no action carries a rule. */
const noRuleCard: MobilePermission = {
  ...card,
  canAllow: false,
  antigravity: { actions: [{ kind: 'other', action: 'notebook_execution', displayName: 'NotebookExecution' }] },
};

const claudeCard: MobilePermission = {
  id: 'p-1',
  runId: 'run-1',
  toolName: 'Bash',
  title: '명령 실행',
  fields: [{ label: 'command', value: 'ls -la' }],
  summary: 'ls -la',
  canAllow: true,
};

function channelAnswering(status: number, body: unknown, calls: { method: string; path: string; body?: unknown }[]): RelayChannel {
  return {
    request: (method, path, sent) => {
      calls.push({ method, path, body: sent });
      return Promise.resolve({ status, body });
    },
    onNotify: () => () => undefined,
  };
}

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

describe('reading an Antigravity card', () => {
  it('reads the shared sample: every refused action, its kind, target and rule', () => {
    expect(card.toolName).toBe(ANTIGRAVITY_PERMISSION_TOOL);
    expect(antigravityActions(card)).toEqual([
      {
        kind: 'command',
        action: 'command',
        displayName: 'RunCommand',
        target: "curl -s https://example.com | grep -i '<title>'",
        rule: "command(curl -s https://example.com | grep -i '<title>')",
      },
      { kind: 'other', action: 'notebook_execution', displayName: 'NotebookExecution' },
    ]);
  });

  it('reads an unknown kind as other and drops what it cannot read', () => {
    const odd = {
      ...card,
      antigravity: {
        actions: [
          { kind: 'mcp-tool', action: 'mcp', displayName: 'CallTool', target: '', rule: 'mcp(s/t)' },
          { kind: 'command', displayName: 'RunCommand' },
          'nope',
          { kind: 'file', action: 'write_file' },
        ],
      },
    } as unknown as MobilePermission;
    expect(antigravityActions(odd)).toEqual([
      { kind: 'other', action: 'mcp', displayName: 'CallTool', rule: 'mcp(s/t)' },
      { kind: 'file', action: 'write_file', displayName: 'write_file' },
    ]);
  });

  it('leaves every other request alone, and draws an unreadable card as a plain one', () => {
    expect(antigravityActions(claudeCard)).toBeUndefined();
    expect(antigravityActions({ ...claudeCard, antigravity: card.antigravity })).toBeUndefined();
    const { antigravity: _dropped, ...withoutPayload } = card;
    expect(antigravityActions(withoutPayload)).toBeUndefined();
    expect(antigravityActions({ ...card, antigravity: { actions: [] } })).toBeUndefined();
    expect(antigravityActions({ ...card, antigravity: { actions: 'x' } } as unknown as MobilePermission)).toBeUndefined();
  });
});

describe('answering an Antigravity card', () => {
  it('builds the three answer bodies the shared sample lists', () => {
    for (const answer of fixture.mobile.answers.filter((item) => item.decision !== undefined)) {
      expect(antigravityAnswerBody(card, answer.decision!)).toEqual(answer.body);
    }
    expect(fixture.mobile.answers.map((item) => item.decision).filter(Boolean)).toEqual(['once', 'always', 'deny']);
    // The 400 sample (always without allow) is one the phone never builds.
    const refused = fixture.mobile.answers.find((item) => item.status === 400)!;
    for (const decision of ['once', 'always', 'deny'] as const) {
      expect(antigravityAnswerBody(card, decision)).not.toEqual(refused.body);
    }
  });

  it('posts each answer to the permission route', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    const client = createClient(channelAnswering(200, { protocol: 1, ok: true }, calls));
    for (const decision of ['once', 'always', 'deny'] as const) {
      expect(await sendAntigravityAnswer(client, 's 1', card, decision)).toEqual({ ok: true });
    }
    expect(calls).toEqual(
      fixture.mobile.answers
        .filter((item) => item.decision !== undefined)
        .map((item) => ({ method: 'POST', path: '/m1/sessions/s%201/permission', body: item.body })),
    );
    expect(antigravityAnsweredToast('once')).toBe(t('phone.session.toast.allowed'));
    expect(antigravityAnsweredToast('always')).toBe(t('phone.session.toast.allowed'));
    expect(antigravityAnsweredToast('deny')).toBe(t('phone.session.toast.denied'));
  });

  it("hands back the host's reason for a 409, so the card stays and shows it", async () => {
    const reason = t('antigravity.settings.error.notJSON', { path: '~/.gemini/antigravity-cli/settings.json' });
    const calls: { method: string; path: string; body?: unknown }[] = [];
    const client = createClient(channelAnswering(409, { protocol: 1, error: reason }, calls));
    expect(await sendAntigravityAnswer(client, 's1', card, 'always')).toEqual({ ok: false, reason });
    expect(calls).toHaveLength(1);
  });

  it('a Claude card still answers with allow alone', async () => {
    const calls: { method: string; path: string; body?: unknown }[] = [];
    await createClient(channelAnswering(200, { protocol: 1, ok: true }, calls)).respondPermission('s1', {
      requestId: claudeCard.id,
      runId: claudeCard.runId,
      allow: true,
    });
    expect(calls).toEqual([{ method: 'POST', path: '/m1/sessions/s1/permission', body: { requestId: 'p-1', runId: 'run-1', allow: true } }]);
  });
});

describe('the Antigravity card', () => {
  function render(
    permission: MobilePermission,
    onAnswer: (decision: AntigravityDecision) => void,
    error?: string,
  ): ReactTestRenderer {
    const actions = antigravityActions(permission) as MobileAntigravityAction[];
    let renderer: ReactTestRenderer | undefined;
    act(() => {
      renderer = TestRenderer.create(
        createElement(AntigravityPermissionCard, { permission, actions, busy: false, error, onAnswer }),
      );
    });
    return renderer!;
  }
  function labels(renderer: ReactTestRenderer): string[] {
    return renderer.root.findAllByType(Button).map((node) => node.props.label as string);
  }
  function texts(renderer: ReactTestRenderer): unknown[] {
    return renderer.root.findAllByType(Text).map((node) => node.props.children);
  }
  function button(renderer: ReactTestRenderer, label: string): ReactTestInstance {
    const found = renderer.root.findAll((node) => node.type === Button && node.props.label === label);
    if (found.length === 0) throw new Error(`no button ${label}`);
    return found[0]!;
  }

  it('lists every action with its kind, its command in full and its rule', () => {
    const renderer = render(card, () => undefined);
    const drawn = texts(renderer);
    expect(drawn).toContain(card.title);
    expect(drawn).toContain(t('antigravity.permission.subtitle'));
    expect(drawn).toContain(`${t('antigravity.permission.kind.command')} · RunCommand`);
    expect(drawn).toContain("curl -s https://example.com | grep -i '<title>'");
    expect(drawn).toContain(t('antigravity.permission.rule', { rule: "command(curl -s https://example.com | grep -i '<title>')" }));
    expect(drawn).toContain(`${t('antigravity.permission.kind.other')} · NotebookExecution`);
    expect(drawn).toContain(t('antigravity.permission.noRule'));
    expect(drawn).toContain(t('antigravity.permission.scopeNote'));
    act(() => renderer.unmount());
  });

  it("answers with each button's own decision", () => {
    const answers: AntigravityDecision[] = [];
    const renderer = render(card, (decision) => answers.push(decision));
    expect(labels(renderer)).toEqual([
      t('antigravity.permission.deny'),
      t('antigravity.permission.allowAlways'),
      t('permission.allowOnce'),
    ]);
    expect(labels(renderer)).toEqual(['거절', '항상 허용', '이번만 허용']);
    for (const label of ['이번만 허용', '항상 허용', '거절']) act(() => button(renderer, label).props.onPress());
    expect(answers).toEqual(['once', 'always', 'deny']);
    act(() => renderer.unmount());
  });

  it('offers no allow button when no rule can allow it, only the guidance', () => {
    const renderer = render(noRuleCard, () => undefined);
    expect(labels(renderer)).toEqual([t('antigravity.permission.deny')]);
    const drawn = texts(renderer);
    expect(drawn).toContain(t('antigravity.permission.noRule'));
    expect(drawn).not.toContain(t('antigravity.permission.scopeNote'));
    act(() => renderer.unmount());
  });

  it("keeps its buttons and shows the host's reason after a refusal", () => {
    const reason = t('antigravity.settings.error.allowNotArray', { path: '~/.gemini/antigravity-cli/settings.json' });
    const renderer = render(card, () => undefined, reason);
    const error = renderer.root.findAll((node) => node.type === Text && node.props.testID === 'antigravity-permission-error');
    expect(error.map((node) => node.props.children)).toEqual([reason]);
    expect(labels(renderer)).toHaveLength(3);
    act(() => renderer.unmount());
    const clean = render(card, () => undefined);
    expect(clean.root.findAll((node) => node.type === Text && node.props.testID === 'antigravity-permission-error')).toHaveLength(0);
    act(() => clean.unmount());
  });

  it('leaves the Claude card as it was: its tool, fields and allow/deny', () => {
    const decisions: boolean[] = [];
    let renderer: ReactTestRenderer | undefined;
    act(() => {
      renderer = TestRenderer.create(
        createElement(PermissionCard, { permission: claudeCard, busy: false, onDecide: (allow: boolean) => decisions.push(allow) }),
      );
    });
    const drawn = renderer!.root.findAllByType(Text).map((node) => node.props.children);
    expect(drawn).toEqual(expect.arrayContaining(['Bash', '명령 실행', 'command', 'ls -la']));
    expect(renderer!.root.findAllByType(Button).map((node) => node.props.label)).toEqual([t('permission.deny'), t('permission.allow')]);
    for (const node of renderer!.root.findAllByType(Button)) act(() => node.props.onPress());
    expect(decisions).toEqual([false, true]);
    act(() => renderer!.unmount());
  });
});
