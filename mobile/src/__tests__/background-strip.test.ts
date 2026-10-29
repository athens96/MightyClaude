import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import type { MobileBackgroundWork } from '@/api/types';
import { BackgroundStrip } from '@/components/background-strip';
import { resetLanguage } from '@/lib/i18n';

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

const work: MobileBackgroundWork[] = [
  { id: 'w1', kind: 'run', title: 'npm test', status: 'running', startedAt: '2026-10-28T10:00:00Z', command: 'npm test --watch', outputTail: ['one', 'two', 'three'] },
  { id: 'w2', kind: 'agent', title: '', status: 'running', startedAt: '2026-10-28T10:00:05Z', command: 'Look into it' },
];

function texts(renderer: ReactTestRenderer): string[] {
  return renderer.root.findAllByType(Text).map((node: ReactTestInstance) => [node.props.children].flat(3).join(''));
}

function pressable(renderer: ReactTestRenderer, label: RegExp): ReactTestInstance {
  const found = renderer.root.findAll(
    (node) => typeof node.props.onPress === 'function' && node.findAllByType(Text).some((text) => label.test([text.props.children].flat(3).join(''))),
  );
  return found[found.length - 1]!;
}

describe('the background work strip', () => {
  let renderer: ReactTestRenderer | undefined;
  afterEach(() => {
    act(() => renderer?.unmount());
    renderer = undefined;
  });
  const mount = (items: MobileBackgroundWork[], done?: number) => {
    act(() => {
      renderer = TestRenderer.create(createElement(BackgroundStrip, { work: items, done }));
    });
    return renderer!;
  };

  it('draws nothing when nothing runs and nothing ended', () => {
    expect(mount([]).toJSON()).toBeNull();
    expect(mount([], 0).toJSON()).toBeNull();
  });

  it('is one collapsed row with both counts, and opens to the running rows', () => {
    const view = mount(work, 1);
    expect(texts(view)).toEqual(['▸', '백그라운드 작업', '끝남 1개 · 작업 중 2개']);
    act(() => pressable(view, /작업 중 2개/).props.onPress());
    const open = texts(view);
    expect(open[0]).toBe('▾');
    expect(open).toContain('npm test');
    // A row with no title is named for its kind; the folded row shows its last output line.
    expect(open).toContain('하위 에이전트');
    expect(open).toContain('three');
    expect(open).not.toContain('npm test --watch');
    act(() => pressable(view, /작업 중 2개/).props.onPress());
    expect(texts(view)).toEqual(['▸', '백그라운드 작업', '끝남 1개 · 작업 중 2개']);
  });

  it('shows the running count alone for a host that sends no finished count', () => {
    expect(texts(mount(work))).toEqual(['▸', '백그라운드 작업', '작업 중 2개']);
  });

  it('stays once all ended, with nothing to open', () => {
    const view = mount([], 3);
    expect(texts(view)).toEqual(['백그라운드 작업', '끝남 3개 · 작업 중 0개']);
    expect(view.root.findAll((node) => typeof node.props.onPress === 'function')).toHaveLength(0);
  });

  it('opens a row to its command and last lines', () => {
    const view = mount(work);
    act(() => pressable(view, /작업 중 2개/).props.onPress());
    act(() => pressable(view, /^npm test$/).props.onPress());
    const open = texts(view);
    expect(open).toContain('npm test --watch');
    expect(open).toEqual(expect.arrayContaining(['one', 'two', 'three']));
  });
});
