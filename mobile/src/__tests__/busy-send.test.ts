import { createElement } from 'react';
import TestRenderer, { act, type ReactTestRenderer } from 'react-test-renderer';
import { Composer } from '@/components/composer';
import { resetLanguage, t } from '@/lib/i18n';
import { busySendChoices } from '@/lib/send';

// What the round button offers for a draft while the pane is running: the draft always
// queues as the next request; nothing joins the running turn any more.
describe('busySendChoices', () => {
  it('offers only queue on a host with submit modes', () => {
    expect(busySendChoices({ submitModes: true })).toEqual(['queue']);
  });

  it('leaves the choice to a host without submit modes', () => {
    expect(busySendChoices({ submitModes: false })).toEqual(['send']);
  });
});

// The composer itself, drawn on a running pane with a draft: queue only, whatever an older
// host still says in `canSteer`.
describe('the running composer', () => {
  beforeAll(() => resetLanguage('ko'));
  afterAll(() => resetLanguage());

  function render(): { labels: string[]; sent: (string | undefined)[] } {
    const sent: (string | undefined)[] = [];
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
          // An older host's summary can still say true; the composer no longer reads it.
          ...({ canSteer: true } as object),
          commands: [],
          onSend: (_text: string, mode?: string) => {
            sent.push(mode);
            return Promise.resolve(true);
          },
          onStop: () => undefined,
          onCommand: () => undefined,
        }),
      );
    });
    const buttons = renderer!.root.findAll(
      (node) =>
        typeof node.type === 'string' &&
        node.props.accessibilityRole === 'button' &&
        typeof node.props.accessibilityLabel === 'string',
    );
    const labels = buttons.map((node) => node.props.accessibilityLabel as string);
    // The pressable itself (not the view it draws) carries the press handler.
    const queue = renderer!.root.find(
      (node) => typeof node.props.onPress === 'function' && node.props.accessibilityLabel === t('queue.add'),
    );
    act(() => queue.props.onPress());
    act(() => renderer!.unmount());
    return { labels, sent };
  }

  it('offers only "대기열에 추가", never "바로 전달", even when the host says it can steer', () => {
    const { labels } = render();
    expect(labels).not.toContain('바로 전달');
    expect(labels.filter((label) => label === t('queue.add'))).toHaveLength(1);
  });

  it('sends the draft with mode "queue"', () => {
    expect(render().sent).toEqual(['queue']);
  });
});
