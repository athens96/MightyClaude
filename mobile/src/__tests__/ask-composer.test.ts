import { createElement } from 'react';
import TestRenderer, { act, type ReactTestRenderer } from 'react-test-renderer';
import { Composer } from '@/components/composer';
import { resetLanguage, t } from '@/lib/i18n';

// The composer's "send to" pill on an idle pane with a draft: it says which CLI is chosen, and
// nothing is sent while a new choice is being saved (the host could still wrap it for the old one).
describe('the composer with the send-to switch', () => {
  beforeAll(() => resetLanguage('ko'));
  afterAll(() => resetLanguage());

  function render(ask: { name?: string; busy: boolean }): { sent: string[]; sendDisabled: boolean; pillValue: unknown; warning: string[] } {
    const sent: string[] = [];
    let renderer: ReactTestRenderer | undefined;
    act(() => {
      renderer = TestRenderer.create(
        createElement(Composer, {
          text: '왜 느린지 봐줘',
          onChangeText: () => undefined,
          disabled: false,
          terminal: false,
          running: false,
          sending: false,
          submitModes: true,
          commands: [],
          ask: { ...ask, onOpen: () => undefined },
          onSend: (text: string) => {
            sent.push(text);
          },
          onStop: () => undefined,
          onCommand: () => undefined,
        }),
      );
    });
    const pressable = (label: string) =>
      renderer!.root.find((node) => typeof node.props.onPress === 'function' && node.props.accessibilityLabel === label);
    const send = pressable(t('phone.composer.send'));
    const pill = pressable(t('composer.ask.menuLabel'));
    const sendDisabled = send.props.disabled === true;
    const pillValue: unknown = pill.props.accessibilityValue;
    const warning = renderer!.root
      .findAll((node) => (node.type as unknown) === 'Text' && typeof node.props.children === 'string' && node.props.children.startsWith('⚠'))
      .map((node) => node.props.children as string);
    act(() => send.props.onPress());
    act(() => renderer!.unmount());
    return { sent, sendDisabled, pillValue, warning };
  }

  it('names the chosen CLI to a screen reader, and Claude when none is chosen', () => {
    expect(render({ name: 'Codex', busy: false }).pillValue).toEqual({ text: 'Codex' });
    expect(render({ busy: false }).pillValue).toEqual({ text: t('composer.ask.claude') });
  });

  it('sends nothing while a choice is being saved', () => {
    const saving = render({ name: 'Codex', busy: true });
    expect(saving.sendDisabled).toBe(true);
    expect(saving.sent).toEqual([]);
  });

  it('sends once the choice is saved', () => {
    expect(render({ name: 'Codex', busy: false }).sent).toEqual(['왜 느린지 봐줘']);
  });

  it('warns that a chosen CLI runs without approvals, and says nothing for Claude', () => {
    expect(render({ name: 'Codex', busy: false }).warning).toEqual([t('composer.ask.unsandboxed', { name: 'Codex' })]);
    expect(render({ busy: false }).warning).toEqual([]);
  });
});
