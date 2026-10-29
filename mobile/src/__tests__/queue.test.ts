import { readFileSync } from 'fs';
import { resolve } from 'path';
import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import type { QueuedItem } from '@/api/types';
import { QueuedList } from '@/components/queued-list';
import { resetLanguage, t } from '@/lib/i18n';
import { runNowOf, runNowReasonKey } from '@/lib/queue';

type Wire = { runNow: string; runNowReason?: string } | null;
const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/queue-run-now.json'), 'utf8'),
) as { cases: { name: string; wire: Wire }[] };

describe('runNowOf', () => {
  it.each(fixture.cases.map((c) => [c.name, c.wire] as const))('%s', (_name, wire) => {
    const read = runNowOf({ id: 'q', text: 'x', ...(wire ?? {}) } as never);
    if (wire === null) expect(read).toBeUndefined();
    else expect(read).toEqual({ kind: wire.runNow, ...(wire.runNowReason ? { reason: wire.runNowReason } : {}) });
  });

  it('shows no button for anything else', () => {
    for (const junk of ['steer', 'agentMenu', 'hidden', '', 1, null, {}]) {
      expect(runNowOf({ id: 'q', text: 'x', runNow: junk } as never)).toBeUndefined();
    }
  });

  it('reads an unknown or missing reason as the current task', () => {
    expect(runNowOf({ id: 'q', text: 'x', runNow: 'unavailable', runNowReason: 'later' } as never)).toEqual({
      kind: 'unavailable',
      reason: 'turn',
    });
    expect(runNowOf({ id: 'q', text: 'x', runNow: 'unavailable' } as never)).toEqual({ kind: 'unavailable', reason: 'turn' });
  });

  it('words each reason with the shared keys', () => {
    expect(runNowReasonKey('turn')).toBe('queue.runNowUnavailableHelp');
    expect(runNowReasonKey('held')).toBe('queue.runNowAfterHeldHelp');
    expect(runNowReasonKey('update')).toBe('queue.runNowUpdateHelp');
    expect(runNowReasonKey('agent')).toBe('phone.queue.runNowAgentOnComputer');
  });
});

describe('the queued list', () => {
  beforeAll(() => resetLanguage('ko'));
  afterAll(() => resetLanguage());

  let renderer: ReactTestRenderer | undefined;
  afterEach(() => {
    act(() => renderer?.unmount());
    renderer = undefined;
  });

  const mount = (items: QueuedItem[], onRunNow = jest.fn(), onExplain = jest.fn()) => {
    act(() => {
      renderer = TestRenderer.create(
        createElement(QueuedList, { items, manageable: true, busy: false, onRemove: jest.fn(), onRunNow, onExplain }),
      );
    });
    return renderer!;
  };

  /** The pressable of each button labelled 바로 실행 (the innermost node that takes the press, one per button). */
  const runNowButtons = (view: ReactTestRenderer): ReactTestInstance[] => {
    const found = view.root.findAll(
      (node) =>
        typeof node.props.onPress === 'function' &&
        node.props.accessibilityRole === 'button' &&
        node.findAllByType(Text).some((text) => [text.props.children].flat(3).join('') === t('queue.runNow')),
    );
    const holds = (outer: ReactTestInstance, inner: ReactTestInstance) => {
      for (let node = inner.parent; node; node = node.parent) if (node === outer) return true;
      return false;
    };
    return found.filter((node) => !found.some((other) => other !== node && holds(node, other)));
  };

  it('offers 바로 실행 only on rows the host gave one, and none from an old host', () => {
    const view = mount([
      { id: 'a', text: 'first', runNow: 'worktree' },
      { id: 'b', text: 'second' },
      { id: 'c', text: 'third', runNow: 'here' },
    ]);
    const buttons = runNowButtons(view);
    expect(buttons).toHaveLength(2);
    expect(buttons.map((button) => button.props.accessibilityLabel)).toEqual([
      t('phone.queue.runNowLabel', { text: 'first' }),
      t('phone.queue.runNowLabel', { text: 'third' }),
    ]);
    act(() => renderer?.unmount());
    expect(runNowButtons(mount([{ id: 'old', text: 'from an old host' }]))).toHaveLength(0);
  });

  it('runs an active row', () => {
    const onRunNow = jest.fn();
    const item: QueuedItem = { id: 'a', text: 'first', runNow: 'worktree' };
    const view = mount([item], onRunNow);
    const [button] = runNowButtons(view);
    expect(button!.props.accessibilityState).toMatchObject({ disabled: false });
    expect(button!.props.accessibilityHint).toBe(t('phone.queue.runNowWorktreeHint'));
    act(() => button!.props.onPress());
    expect(onRunNow).toHaveBeenCalledWith(item);
  });

  it('dims a row that must wait, and a tap says why instead of asking the host', () => {
    const onRunNow = jest.fn();
    const onExplain = jest.fn();
    const view = mount([{ id: 'a', text: 'first', runNow: 'unavailable', runNowReason: 'agent' }], onRunNow, onExplain);
    const [button] = runNowButtons(view);
    expect(button!.props.accessibilityState).toMatchObject({ disabled: true });
    expect(button!.props.accessibilityHint).toBe(t('phone.queue.runNowAgentOnComputer'));
    act(() => button!.props.onPress());
    expect(onRunNow).not.toHaveBeenCalled();
    expect(onExplain).toHaveBeenCalledWith(t('phone.queue.runNowAgentOnComputer'));
  });
});
