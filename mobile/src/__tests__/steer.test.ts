import { createElement } from 'react';
import TestRenderer, { act, type ReactTestRenderer } from 'react-test-renderer';
import { Composer } from '@/components/composer';
import { resetLanguage, t } from '@/lib/i18n';
import { busySendChoices } from '@/lib/send';

// What the round buttons offer for a draft while the pane is running. The host says per pane
// whether a follow-up can join the running turn (`canSteer`); only Claude panes can.
describe('busySendChoices', () => {
  it('offers steer and queue when the host says the pane can steer', () => {
    expect(busySendChoices({ submitModes: true, canSteer: true, attachments: 0 })).toEqual([
      'steer',
      'queue',
    ]);
  });

  it('offers only queue when the host says the pane cannot steer (Codex, Antigravity)', () => {
    expect(busySendChoices({ submitModes: true, canSteer: false, attachments: 0 })).toEqual([
      'queue',
    ]);
  });

  it('keeps steering offered when an older host leaves the field out', () => {
    expect(busySendChoices({ submitModes: true, attachments: 0 })).toEqual(['steer', 'queue']);
  });

  it('never offers steer for a draft carrying files', () => {
    expect(busySendChoices({ submitModes: true, canSteer: true, attachments: 2 })).toEqual(['queue']);
    expect(busySendChoices({ submitModes: true, attachments: 1 })).toEqual(['queue']);
  });

  it('leaves the choice to a host without submit modes', () => {
    expect(busySendChoices({ submitModes: false, canSteer: false, attachments: 0 })).toEqual([
      'send',
    ]);
    expect(busySendChoices({ submitModes: false, attachments: 0 })).toEqual(['send']);
  });
});

// The composer itself, drawn on a running pane with a draft: steer shows only where the host allows it.
describe('the running composer', () => {
  beforeAll(() => resetLanguage('ko'));
  afterAll(() => resetLanguage());

  function labels(canSteer: boolean | undefined): string[] {
    let renderer: ReactTestRenderer | undefined;
    act(() => {
      renderer = TestRenderer.create(
        createElement(Composer, {
          text: '다음엔 테스트도',
          onChangeText: () => undefined,
          disabled: false,
          terminal: false,
          running: true,
          sending: false,
          submitModes: true,
          canSteer,
          commands: [],
          onSend: () => Promise.resolve(true),
          onStop: () => undefined,
          onCommand: () => undefined,
        }),
      );
    });
    const found = renderer!.root
      .findAll(
        (node) =>
          typeof node.type === 'string' &&
          node.props.accessibilityRole === 'button' &&
          typeof node.props.accessibilityLabel === 'string',
      )
      .map((node) => node.props.accessibilityLabel as string);
    act(() => renderer!.unmount());
    return found;
  }

  it('has no "바로 전달" button when the host says the pane cannot steer', () => {
    expect(t('phone.composer.steer')).toBe('바로 전달');
    const shown = labels(false);
    expect(shown).not.toContain('바로 전달');
    expect(shown).toContain(t('queue.add'));
  });

  it('has one when the host says it can, or says nothing', () => {
    for (const canSteer of [true, undefined]) {
      const shown = labels(canSteer);
      expect(shown.filter((label) => label === '바로 전달')).toHaveLength(1);
      expect(shown).toContain(t('queue.add'));
    }
  });
});
