import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import { MAX_RUN_RESULT_FILES, type MobileResultFile } from '@/api/types';
import { MightyRunList } from '@/components/mighty-blocks';
import { ResultFiles } from '@/components/result-files';
import { resetLanguage } from '@/lib/i18n';
import { normalizeMighty } from '@/lib/mighty';
import {
  RESULT_FILES_FOLDED,
  parseResultFiles,
  resultFileParts,
  resultFileRoute,
  shownResultFiles,
} from '@/lib/result-files';

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

function texts(renderer: ReactTestRenderer): string[] {
  return renderer.root.findAllByType(Text).map((node: ReactTestInstance) => [node.props.children].flat(3).filter((part) => typeof part === 'string' || typeof part === 'number').join(''));
}

function pressable(renderer: ReactTestRenderer, label: RegExp): ReactTestInstance {
  const found = renderer.root.findAll(
    (node) =>
      typeof node.props.onPress === 'function' &&
      node.findAllByType(Text).some((text) => label.test([text.props.children].flat(3).filter((part) => typeof part === 'string').join(''))),
  );
  return found[found.length - 1]!;
}

describe('parsing resultFiles', () => {
  it('keeps workspace-relative paths with a positive line', () => {
    expect(
      parseResultFiles([
        { path: 'docs/note.md' },
        { path: 'src/app.ts', line: 12 },
        { path: 'README.md', line: 0 },
        { path: 'a/b.txt', line: 2.5 },
        { path: 'c.txt', line: '3' },
      ]),
    ).toEqual([
      { path: 'docs/note.md' },
      { path: 'src/app.ts', line: 12 },
      { path: 'README.md' },
      { path: 'a/b.txt' },
      { path: 'c.txt' },
    ]);
  });

  it('drops what the file preview would refuse, and repeats', () => {
    expect(
      parseResultFiles([
        null,
        'docs/note.md',
        { path: 42 },
        { path: '' },
        { path: '/etc/passwd' },
        { path: '../outside.txt' },
        { path: 'docs/../x.txt' },
        { path: 'docs/./x.txt' },
        { path: 'docs//x.txt' },
        { path: 'docs/' },
        { path: 'C:\\x.txt' },
        { path: 'a\0b.txt' },
        { path: 'x'.repeat(4097) },
        { path: 'ok.txt' },
        { path: 'ok.txt', line: 4 },
      ]),
    ).toEqual([{ path: 'ok.txt' }]);
  });

  it('is absent for anything but a non-empty list, and capped', () => {
    expect(parseResultFiles(undefined)).toBeUndefined();
    expect(parseResultFiles({ path: 'a/b.txt' })).toBeUndefined();
    expect(parseResultFiles([])).toBeUndefined();
    expect(parseResultFiles([{ path: '..' }])).toBeUndefined();
    const many = Array.from({ length: 80 }, (_, index) => ({ path: `many/f${index}.txt` }));
    const parsed = parseResultFiles(many);
    expect(parsed).toHaveLength(MAX_RUN_RESULT_FILES);
    expect(parsed?.[0]).toEqual({ path: 'many/f0.txt' });
  });

  it('rides on the run through normalizeMighty, and an older host sends none', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        { id: 'old', input: 'a', status: 'completed', blocks: [] },
        { id: 'new', input: 'b', status: 'completed', blocks: [], result: 'wrote src/app.ts', resultFiles: [{ path: 'src/app.ts', line: 3 }] },
        { id: 'bad', input: 'c', status: 'completed', blocks: [], result: 'x', resultFiles: 'src/app.ts' },
      ],
    });
    expect(mighty?.runs[0]?.resultFiles).toBeUndefined();
    expect(mighty?.runs[1]?.resultFiles).toEqual([{ path: 'src/app.ts', line: 3 }]);
    expect(mighty?.runs[2]?.resultFiles).toBeUndefined();
  });
});

describe('result file rows', () => {
  it('show the name, the line and the folder that tells same names apart', () => {
    expect(resultFileParts({ path: 'src/lib/index.ts', line: 7 })).toEqual({ name: 'index.ts', line: ':7', folder: 'src/lib' });
    expect(resultFileParts({ path: 'README.md' })).toEqual({ name: 'README.md', line: '', folder: '' });
  });

  it('fold a long list to its first rows', () => {
    const files = Array.from({ length: 8 }, (_, index) => ({ path: `f${index}.txt` }));
    expect(shownResultFiles(files, false)).toEqual({ shown: files.slice(0, RESULT_FILES_FOLDED), hidden: 3 });
    expect(shownResultFiles(files, true)).toEqual({ shown: files, hidden: 0 });
    expect(shownResultFiles(files.slice(0, RESULT_FILES_FOLDED), false).hidden).toBe(0);
  });

  it('open the workspace file preview the files browser opens', () => {
    expect(resultFileRoute('h1', 'w1', { path: 'docs/note.md', line: 3 })).toEqual({
      pathname: '/host/[hostId]/workspace/[workspaceId]/file',
      params: { hostId: 'h1', workspaceId: 'w1', path: 'docs/note.md' },
    });
  });
});

describe('the result files list', () => {
  let renderer: ReactTestRenderer | undefined;
  afterEach(() => {
    act(() => renderer?.unmount());
    renderer = undefined;
  });
  const files: MobileResultFile[] = [
    { path: 'src/a/index.ts', line: 12 },
    { path: 'src/b/index.ts' },
    { path: 'README.md' },
    { path: 'docs/one.md' },
    { path: 'docs/two.md' },
    { path: 'docs/three.md' },
    { path: 'docs/four.md' },
  ];
  const mount = (items: MobileResultFile[], onOpen?: (file: MobileResultFile) => void) => {
    act(() => {
      renderer = TestRenderer.create(createElement(ResultFiles, { files: items, onOpen }));
    });
    return renderer!;
  };

  it('draws nothing for no files', () => {
    expect(mount([]).toJSON()).toBeNull();
  });

  it('titles itself as the desktop panel with a count, and folds past five rows', () => {
    const view = mount(files, jest.fn());
    const shown = texts(view);
    expect(shown.slice(0, 2)).toEqual(['결과에 나온 파일', '7']);
    expect(shown).toContain('index.ts');
    expect(shown).toContain(':12');
    expect(shown).toContain('src/a');
    expect(shown).toContain('src/b');
    expect(shown).toContain('README.md');
    expect(shown).toContain('two.md');
    expect(shown).not.toContain('three.md');
    expect(shown).toContain('2개 더 보기');
    act(() => pressable(view, /더 보기/).props.onPress());
    expect(texts(view)).toEqual(expect.arrayContaining(['three.md', 'four.md', '접기']));
    act(() => pressable(view, /^접기$/).props.onPress());
    expect(texts(view)).not.toContain('four.md');
  });

  it('opens the file a row names', () => {
    const onOpen = jest.fn();
    const view = mount(files.slice(0, 2), onOpen);
    expect(texts(view)).not.toContain('2개 더 보기');
    const row = view.root.findAll((node) => node.props.accessibilityLabel === 'src/b/index.ts 열기' && typeof node.props.onPress === 'function');
    act(() => row[row.length - 1]!.props.onPress());
    expect(onOpen).toHaveBeenCalledWith({ path: 'src/b/index.ts' });
  });

  it('only reads its rows when the host serves no files', () => {
    const view = mount(files.slice(0, 2));
    expect(view.root.findAll((node) => typeof node.props.accessibilityLabel === 'string' && node.props.accessibilityLabel.endsWith(' 열기'))).toHaveLength(0);
    expect(texts(view)).toEqual(expect.arrayContaining(['index.ts', ':12', 'src/a']));
  });

  it('hangs under the result card of the run that carries it', () => {
    const onOpenFile = jest.fn();
    const runs = normalizeMighty({
      style: 'cli',
      runs: [{ id: 'r1', input: 'go', status: 'completed', blocks: [{ id: 'r1:main', kind: 'main', title: '요청 1', status: 'completed' }], result: 'wrote docs/note.md', resultFiles: [{ path: 'docs/note.md' }] }],
    })!.runs;
    act(() => {
      renderer = TestRenderer.create(createElement(MightyRunList, { runs, onOpenFile }));
    });
    const shown = texts(renderer!);
    const card = shown.findIndex((text) => text.includes('최종 결과'));
    expect(card).toBeGreaterThanOrEqual(0);
    expect(shown.indexOf('결과에 나온 파일')).toBeGreaterThan(card);
    const row = renderer!.root.findAll((node) => node.props.accessibilityLabel === 'docs/note.md 열기' && typeof node.props.onPress === 'function');
    act(() => row[row.length - 1]!.props.onPress());
    expect(onOpenFile).toHaveBeenCalledWith({ path: 'docs/note.md' });
  });
});
