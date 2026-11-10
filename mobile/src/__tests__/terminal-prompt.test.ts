import { createElement } from 'react';
import TestRenderer, { act, type ReactTestInstance } from 'react-test-renderer';
import type { TerminalPrompt } from '@/api/types';
import { TerminalPromptCard } from '@/components/terminal-prompt-card';
import { Button } from '@/components/ui';
import { resetLanguage, t } from '@/lib/i18n';
import { isMarkedOption, terminalPromptBody, yesIsPrimary } from '@/lib/terminal-prompt';

const base = { id: 'h:10', handle: 'h', text: 'Continue? (Y/n)', options: [] as string[] };
const yesNo: TerminalPrompt = { ...base, kind: 'yesNo', defaultYes: true };
const secret: TerminalPrompt = { ...base, kind: 'secret', text: 'Password:' };
const text: TerminalPrompt = { ...base, kind: 'text', text: 'Name:' };
const choice: TerminalPrompt = { ...base, kind: 'choice', text: '? Pick', options: ['❯ a', '  b'] };

describe('terminalPromptBody', () => {
  it('builds the body per kind and action', () => {
    expect(terminalPromptBody(yesNo, 'yes')).toEqual({ promptId: 'h:10', answer: 'yes' });
    expect(terminalPromptBody(yesNo, 'no')).toEqual({ promptId: 'h:10', answer: 'no' });
    expect(terminalPromptBody(text, 'enter')).toEqual({ promptId: 'h:10', answer: 'enter' });
    expect(terminalPromptBody(text, 'text', 'bob')).toEqual({ promptId: 'h:10', answer: 'text', text: 'bob' });
    expect(terminalPromptBody(secret, 'text', 'pw')).toEqual({ promptId: 'h:10', answer: 'text', text: 'pw' });
    for (const a of ['up', 'down', 'enter', 'space'] as const) {
      expect(terminalPromptBody(choice, a)).toEqual({ promptId: 'h:10', answer: a });
    }
  });

  it('refuses answers the kind does not take, and empty or oversized text', () => {
    expect(terminalPromptBody(yesNo, 'up')).toBeNull();
    expect(terminalPromptBody(secret, 'enter')).toBeNull();
    expect(terminalPromptBody(choice, 'yes')).toBeNull();
    expect(terminalPromptBody(text, 'text', '')).toBeNull();
    expect(terminalPromptBody(secret, 'text')).toBeNull();
    expect(terminalPromptBody(text, 'text', 'a'.repeat(4096))).not.toBeNull();
    expect(terminalPromptBody(text, 'text', 'a'.repeat(4097))).toBeNull();
    expect(terminalPromptBody(text, 'text', '가'.repeat(1366))).toBeNull();
  });

  it('knows the marked option and the primary side', () => {
    expect(isMarkedOption('❯ a')).toBe(true);
    expect(isMarkedOption('  › b')).toBe(true);
    expect(isMarkedOption('  b')).toBe(false);
    expect(yesIsPrimary({})).toBe(true);
    expect(yesIsPrimary({ defaultYes: false })).toBe(false);
  });
});

function render(prompt: TerminalPrompt, busy = false) {
  const onAnswer = jest.fn();
  let r!: TestRenderer.ReactTestRenderer;
  act(() => {
    r = TestRenderer.create(createElement(TerminalPromptCard, { prompt, busy, onAnswer }));
  });
  const buttons = () => r.root.findAllByType(Button);
  const press = (label: string) => {
    const b = buttons().find((x: ReactTestInstance) => x.props.label === label);
    act(() => b!.props.onPress());
  };
  return { r, onAnswer, buttons, press };
}

describe('TerminalPromptCard', () => {
  beforeEach(() => resetLanguage());

  it('yes/no marks the default side primary and answers', () => {
    const { buttons, press, onAnswer } = render({ ...yesNo, defaultYes: false });
    const no = buttons().find((b) => b.props.label === `${t('terminalPrompt.no')} (${t('terminalPrompt.default')})`);
    expect(no?.props.tone).toBe('primary');
    press(t('terminalPrompt.yes'));
    expect(onAnswer).toHaveBeenCalledWith('yes');
  });

  it('secret hides the entry, sends the text and clears the field', () => {
    const { r, press, onAnswer } = render(secret);
    const input = () => r.root.findByProps({ testID: 'terminal-prompt-input' });
    expect(input().props.secureTextEntry).toBe(true);
    act(() => input().props.onChangeText('hunter2'));
    press(t('terminalPrompt.send'));
    expect(onAnswer).toHaveBeenCalledWith('text', 'hunter2');
    expect(input().props.value).toBe('');
  });

  it('text offers Enter only; choice moves and selects; busy disables', () => {
    const t1 = render(text);
    t1.press(t('terminalPrompt.enterOnly'));
    expect(t1.onAnswer).toHaveBeenCalledWith('enter');
    const c = render(choice);
    c.press(t('terminalPrompt.down'));
    c.press(t('terminalPrompt.select'));
    expect(c.onAnswer.mock.calls).toEqual([['down'], ['enter']]);
    expect(c.r.root.findAllByProps({ testID: 'terminal-prompt-marked' }).length).toBeGreaterThan(0);
    const b = render(yesNo, true);
    expect(b.buttons().every((x) => x.props.disabled)).toBe(true);
  });
});
