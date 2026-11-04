import { createElement, type ReactElement } from 'react';
import { Modal, Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import {
  BottomSheet,
  ConfirmDialog,
  DialogButton,
  FullScreenPanel,
  MessageSheet,
  PickerSheet,
  SheetRow,
  sheetDragCloses,
} from '@/components/sheets';
import { resetLanguage, t } from '@/lib/i18n';

jest.mock('react-native-safe-area-context', () => ({ useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }) }));

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

function render(element: ReactElement): ReactTestRenderer {
  let renderer: ReactTestRenderer | undefined;
  act(() => {
    renderer = TestRenderer.create(element);
  });
  return renderer!;
}

function texts(renderer: ReactTestRenderer): unknown[] {
  return renderer.root.findAllByType(Text).map((node) => node.props.children);
}

function backdrop(renderer: ReactTestRenderer): ReactTestInstance {
  return renderer.root.find((node) => node.props.testID === 'dialog-backdrop' && typeof node.props.onPress === 'function');
}

function modal(renderer: ReactTestRenderer): ReactTestInstance {
  return renderer.root.findByType(Modal);
}

const options = [
  { id: 'opus', label: 'Opus' },
  { id: 'sonnet', label: 'Sonnet' },
];

describe('the picker sheet', () => {
  function picker(onSelect: (id: string) => void, onClose: () => void, extra: object = {}) {
    return render(
      createElement(PickerSheet, {
        visible: true,
        title: '모델',
        options,
        selectedId: 'opus',
        busy: false,
        locked: false,
        onSelect,
        onClose,
        ...extra,
      }),
    );
  }

  it('closes on a pick, after handing the pick over', () => {
    const calls: string[] = [];
    const renderer = picker((id) => calls.push(`select:${id}`), () => calls.push('close'));
    const row = renderer.root.find((node) => node.type === SheetRow && node.props.label === 'Sonnet');
    act(() => row.props.onPress());
    expect(calls).toEqual(['select:sonnet', 'close']);
    act(() => renderer.unmount());
  });

  it('has no full-width close button: the backdrop and Android back close it', () => {
    let closes = 0;
    const renderer = picker(() => undefined, () => (closes += 1));
    expect(texts(renderer)).not.toContain(t('common.close'));
    expect(renderer.root.findAll((node) => node.type === DialogButton)).toHaveLength(0);
    act(() => backdrop(renderer).props.onPress());
    act(() => modal(renderer).props.onRequestClose());
    expect(closes).toBe(2);
    expect(backdrop(renderer).props.accessibilityLabel).toBe(t('dialog.close'));
    act(() => renderer.unmount());
  });

  it('marks the chosen row and locks the rows while the host is busy', () => {
    const renderer = picker(() => undefined, () => undefined, { busy: true });
    const rows = renderer.root.findAll((node) => node.type === SheetRow);
    expect(rows.map((row) => [row.props.label, row.props.selected, row.props.disabled])).toEqual([
      ['Opus', true, true],
      ['Sonnet', false, true],
    ]);
    act(() => renderer.unmount());
  });

  it('says it is loading with the shared words', () => {
    const renderer = picker(() => undefined, () => undefined, { loading: true });
    expect(texts(renderer)).toContain(t('dialog.loading'));
    expect(renderer.root.findAll((node) => node.type === SheetRow)).toHaveLength(0);
    act(() => renderer.unmount());
  });
});

describe('the bottom sheet', () => {
  it('puts the main action at the right end of its footer', () => {
    const renderer = render(
      createElement(BottomSheet, {
        visible: true,
        onClose: () => undefined,
        title: '새 창',
        footer: [
          createElement(DialogButton, { key: 'c', label: t('common.cancel'), onPress: () => undefined }),
          createElement(DialogButton, { key: 'p', label: '만들기', kind: 'primary', onPress: () => undefined }),
        ],
      }),
    );
    const buttons = renderer.root.findAll((node) => node.type === DialogButton);
    expect(buttons.map((button) => [button.props.label, button.props.kind ?? 'text'])).toEqual([
      [t('common.cancel'), 'text'],
      ['만들기', 'primary'],
    ]);
    act(() => renderer.unmount());
  });

  it('closes on a drag down past a quarter of its height, or a flick', () => {
    expect(sheetDragCloses(120, 0, 400)).toBe(true);
    expect(sheetDragCloses(80, 0, 400)).toBe(false);
    expect(sheetDragCloses(30, 1.2, 400)).toBe(true);
    expect(sheetDragCloses(0, 0, 400)).toBe(false);
  });
});

describe('the alert', () => {
  function alert(onConfirm: () => void, onCancel: () => void) {
    return render(
      createElement(ConfirmDialog, {
        visible: true,
        title: '창을 닫을까요?',
        description: '되돌릴 수 없습니다.',
        confirmLabel: '닫기',
        destructive: true,
        busy: false,
        onConfirm,
        onCancel,
      }),
    );
  }

  it('orders its text buttons [취소] [확인], the destructive one in the error colour', () => {
    const renderer = alert(() => undefined, () => undefined);
    const buttons = renderer.root.findAll((node) => node.type === DialogButton);
    expect(buttons.map((button) => [button.props.label, button.props.kind ?? 'text'])).toEqual([
      [t('common.cancel'), 'text'],
      ['닫기', 'danger'],
    ]);
    act(() => renderer.unmount());
  });

  it('cancels on the backdrop and on Android back, and confirms only on its button', () => {
    const calls: string[] = [];
    const renderer = alert(() => calls.push('confirm'), () => calls.push('cancel'));
    act(() => backdrop(renderer).props.onPress());
    act(() => modal(renderer).props.onRequestClose());
    const confirm = renderer.root.find((node) => node.type === DialogButton && node.props.label === '닫기');
    act(() => confirm.props.onPress());
    expect(calls).toEqual(['cancel', 'cancel', 'confirm']);
    act(() => renderer.unmount());
  });
});

describe('the full-screen panel', () => {
  it('closes from the ✕ on the left of its head, named by dialog.close', () => {
    let closes = 0;
    const renderer = render(
      createElement(FullScreenPanel, { visible: true, title: '사용량', onClose: () => (closes += 1) }, null),
    );
    const close = renderer.root.find(
      (node) => node.props.accessibilityLabel === t('dialog.close') && typeof node.props.onPress === 'function',
    );
    act(() => close.props.onPress());
    act(() => modal(renderer).props.onRequestClose());
    expect(closes).toBe(2);
    act(() => renderer.unmount());
  });

  it('is what the /usage answer opens, with no close button at the bottom', () => {
    const renderer = render(
      createElement(MessageSheet, { visible: true, title: '/usage', message: '42%', onClose: () => undefined }),
    );
    expect(renderer.root.findAllByType(FullScreenPanel)).toHaveLength(1);
    expect(texts(renderer)).not.toContain(t('common.close'));
    act(() => renderer.unmount());
  });
});
