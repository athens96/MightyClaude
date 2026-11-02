import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestRenderer } from 'react-test-renderer';
import type { FilePreview, HostInfo } from '@/api/types';
import { htmlNavigation } from '@/lib/html-preview';
import { resetLanguage } from '@/lib/i18n';
import { useLiveStore } from '@/store/live';

let mockParams: Record<string, string> = {};
let mockClient: unknown;
jest.mock('expo-router', () => ({ Stack: { Screen: () => null }, useLocalSearchParams: () => mockParams }));
jest.mock('react-native-safe-area-context', () => ({ useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }) }));
jest.mock('@/store/live', () => {
  const actual = jest.requireActual('@/store/live');
  return { ...actual, useHostClient: () => mockClient };
});

jest.mock('react-native-webview', () => ({ WebView: 'WebView' }));

import FileScreen from '../../app/host/[hostId]/workspace/[workspaceId]/file';

const preview: FilePreview = {
  protocol: 1,
  workspaceId: 'w',
  path: 'a.txt',
  name: 'a.txt',
  type: 'source',
  language: 'plain',
  text: 'one\nTWO\n',
  size: 8,
  changes: { base: 'run', hunks: [{ oldStart: 1, oldLines: 2, newStart: 1, newLines: 2, lines: [{ op: ' ', text: 'one' }, { op: '-', text: 'two' }, { op: '+', text: 'TWO' }] }], added: 1, removed: 1, truncated: false, unchanged: false },
} as unknown as FilePreview;

const flush = () => act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
const shown = (view: ReactTestRenderer) => view.root.findAllByType(Text).map((n) => [n.props.children].flat(3).filter((p) => typeof p === 'string').join(''));

let mounted: ReactTestRenderer | undefined;
afterEach(() => {
  act(() => mounted?.unmount());
  mounted = undefined;
});

describe('the file screen 변경사항', () => {
  beforeAll(() => resetLanguage('ko'));
  afterAll(() => resetLanguage());
  beforeEach(() => {
    useLiveStore.setState({ capabilities: {} });
    mockParams = { hostId: 'h', workspaceId: 'w', path: 'a.txt', session: 'pane', run: 'run-1' };
  });

  function makeClient(capabilities: string[]) {
    const filePreview = jest.fn(async (_w: string, _p: string, _s: unknown, changes?: unknown) => (changes ? preview : { ...preview, changes: undefined }));
    const listeners: ((state: string) => void)[] = [];
    const info = jest.fn(async () => ({ protocol: 1, hostId: 'h', hostName: 'pc', appVersion: '0.4.48', platform: 'win32', capabilities }) as HostInfo);
    mockClient = { filePreview, info, onStateChange: (listener: (state: string) => void) => { listeners.push(listener); return () => undefined; } };
    return { filePreview, info, emit: (state: string) => listeners.forEach((listener) => listener(state)) };
  }

  it('asks for the changes and shows the toggle on a host listing fileChanges', async () => {
    const { filePreview } = makeClient(['files', 'fileChanges']);
    let view: ReactTestRenderer | undefined;
    await act(async () => { view = TestRenderer.create(createElement(FileScreen)); mounted = view; });
    await flush(); await flush();
    expect(filePreview).toHaveBeenLastCalledWith('w', 'a.txt', expect.anything(), { session: 'pane', run: 'run-1' });
    expect(shown(view!)).toEqual(expect.arrayContaining(['변경사항', '전체 보기']));
  });

  it('learns fileChanges from a host that was updated while the app kept running, once the tunnel is back', async () => {
    // The app met this host before it offered fileChanges and cached that list.
    useLiveStore.setState({ capabilities: { h: ['files'] } });
    const { filePreview, emit } = makeClient(['files', 'fileChanges']);
    let view: ReactTestRenderer | undefined;
    await act(async () => { view = TestRenderer.create(createElement(FileScreen)); mounted = view; });
    await flush();
    expect(shown(view!)).not.toContain('변경사항');
    act(() => emit('closed'));
    act(() => emit('ready'));
    await flush(); await flush();
    expect(filePreview).toHaveBeenLastCalledWith('w', 'a.txt', expect.anything(), { session: 'pane', run: 'run-1' });
    expect(shown(view!)).toEqual(expect.arrayContaining(['변경사항', '전체 보기']));
  });
});

describe('an HTML file on the file screen', () => {
  beforeAll(() => resetLanguage('ko'));
  afterAll(() => resetLanguage());
  beforeEach(() => {
    useLiveStore.setState({ capabilities: { h: ['files', 'fileChanges'] } });
    mockParams = { hostId: 'h', workspaceId: 'w', path: 'a.html', session: 'pane', run: 'run-1' };
  });

  const page = { ...preview, path: 'a.html', name: 'a.html', language: 'html', text: '<p>TWO</p>\\n' } as FilePreview;
  const webViews = (view: ReactTestRenderer) => view.root.findAll((node) => (node.type as unknown) === 'WebView');
  const press = (view: ReactTestRenderer, label: string) =>
    act(() => view.root.findAll((node) => typeof node.props.onPress === 'function' && node.findAllByType(Text).some((t) => t.props.children === label))[0]!.props.onPress());

  it('opens on its changes, renders the page for 전체 보기, and shows the source on the 소스 chip', async () => {
    const filePreview = jest.fn(async () => page);
    mockClient = { filePreview, info: async () => ({}), onStateChange: () => () => undefined };
    let view: ReactTestRenderer | undefined;
    await act(async () => { view = TestRenderer.create(createElement(FileScreen)); mounted = view; });
    await flush(); await flush();
    expect(webViews(view!)).toHaveLength(0);
    press(view!, '전체 보기');
    expect(webViews(view!)).toHaveLength(1);
    expect(webViews(view!)[0]!.props.source).toEqual({ html: '<p>TWO</p>\\n', baseUrl: 'about:blank' });
    expect(shown(view!)).toEqual(expect.arrayContaining(['미리보기', '소스']));
    press(view!, '소스');
    expect(webViews(view!)).toHaveLength(0);
  });

  it('renders a page with no changes straight away and never leaves the file', async () => {
    mockClient = { filePreview: async () => ({ ...page, changes: undefined }), info: async () => ({}), onStateChange: () => () => undefined };
    let view: ReactTestRenderer | undefined;
    await act(async () => { view = TestRenderer.create(createElement(FileScreen)); mounted = view; });
    await flush(); await flush();
    expect(webViews(view!)).toHaveLength(1);
    expect(webViews(view!)[0]!.props.allowFileAccess).toBe(false);
    expect(htmlNavigation('about:blank', true)).toBe('stay');
    expect(htmlNavigation('about:blank#top', true)).toBe('stay');
    expect(htmlNavigation('https://example.com/x', true)).toBe('browser');
    expect(htmlNavigation('https://example.com/x', false)).toBe('refuse');
    expect(htmlNavigation('file:///etc/passwd', true)).toBe('refuse');
    expect(htmlNavigation('intent://x', true)).toBe('refuse');
    expect(htmlNavigation('javascript:alert(1)', undefined)).toBe('refuse');
  });
});
