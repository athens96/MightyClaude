import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import type { FileChanges } from '@/api/types';
import { DiffView } from '@/components/diff-view';
import { baseCaption, changesRequest, diffRows, gutterDigits, hunkHeader, showsChanges } from '@/lib/file-changes';
import { resetLanguage } from '@/lib/i18n';

const fixture = JSON.parse(readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/file-diff.json'), 'utf8')) as {
  cases: { name: string; expect: { hunks?: FileChanges['hunks'] } | null }[];
};

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

const changes = (patch: Partial<FileChanges>): FileChanges => ({
  base: 'run',
  hunks: [],
  added: 0,
  removed: 0,
  truncated: false,
  unchanged: false,
  ...patch,
});

describe('the preview 변경사항 rows', () => {
  it('number each hunk from its own starts: context on both sides, removed on the old, added on the new', () => {
    const rows = diffRows({
      hunks: [
        { oldStart: 1, oldLines: 3, newStart: 1, newLines: 3, lines: [{ op: ' ', text: 'a' }, { op: '-', text: 'b' }, { op: '+', text: 'B' }, { op: ' ', text: 'c' }] },
        { oldStart: 98, oldLines: 2, newStart: 98, newLines: 3, lines: [{ op: ' ', text: 'x' }, { op: '+', text: 'y' }, { op: ' ', text: 'z' }] },
      ],
    });
    expect(rows).toEqual([
      { kind: 'hunk', text: '@@ -1,3 +1,3 @@' },
      { kind: 'context', old: 1, new: 1, text: 'a' },
      { kind: 'removed', old: 2, text: 'b' },
      { kind: 'added', new: 2, text: 'B' },
      { kind: 'context', old: 3, new: 3, text: 'c' },
      { kind: 'hunk', text: '@@ -98,2 +98,3 @@' },
      { kind: 'context', old: 98, new: 98, text: 'x' },
      { kind: 'added', new: 99, text: 'y' },
      { kind: 'context', old: 99, new: 100, text: 'z' },
    ]);
    expect(gutterDigits(rows)).toBe(3);
    expect(gutterDigits([])).toBe(1);
  });

  it('start a created file at 0 on its empty side and mark a line without a line break', () => {
    const hunk = { oldStart: 0, oldLines: 0, newStart: 1, newLines: 1, lines: [{ op: '+' as const, text: 'end', noNewline: true }] };
    expect(hunkHeader(hunk)).toBe('@@ -0,0 +1,1 @@');
    expect(diffRows({ hunks: [hunk] })).toEqual([
      { kind: 'hunk', text: '@@ -0,0 +1,1 @@' },
      { kind: 'added', new: 1, text: 'end' },
      { kind: 'noNewline', text: '' },
    ]);
  });

  it('number every shared fixture case to its hunk counts', () => {
    for (const item of fixture.cases) {
      const hunks = item.expect?.hunks ?? [];
      for (const hunk of hunks) {
        const rows = diffRows({ hunks: [hunk] });
        const old = rows.flatMap((row) => (row.old === undefined ? [] : [row.old]));
        const next = rows.flatMap((row) => (row.new === undefined ? [] : [row.new]));
        expect(old).toHaveLength(hunk.oldLines);
        expect(next).toHaveLength(hunk.newLines);
        if (old.length > 0) expect(old[old.length - 1]).toBe(hunk.oldStart + hunk.oldLines - 1);
        if (next.length > 0) expect(next[next.length - 1]).toBe(hunk.newStart + hunk.newLines - 1);
      }
    }
  });

  it('name the base: a created file, the request, HEAD with or without its commit', () => {
    expect(baseCaption({ base: 'head', created: true, headSha: 'abc1234' })).toBe('새 파일');
    expect(baseCaption({ base: 'run' })).toBe('이번 요청 전과 비교');
    expect(baseCaption({ base: 'head', headSha: 'abc1234' })).toBe('마지막 커밋(HEAD abc1234)과 비교');
    expect(baseCaption({ base: 'head' })).toBe('마지막 커밋과 비교');
  });

  it('offer the toggle only for changes that changed something', () => {
    const hunk = { oldStart: 1, oldLines: 1, newStart: 1, newLines: 1, lines: [{ op: '-' as const, text: 'a' }, { op: '+' as const, text: 'b' }] };
    expect(showsChanges(undefined)).toBe(false);
    expect(showsChanges(changes({ unchanged: true }))).toBe(false);
    expect(showsChanges(changes({}))).toBe(false);
    expect(showsChanges(changes({ hunks: [hunk], truncated: true }))).toBe(true);
  });
});

describe('the preview 변경사항 request', () => {
  it('asks only a host listing fileChanges, with the pane and run the row came from', () => {
    expect(changesRequest(false, { session: 's1', run: 'r1' })).toBeUndefined();
    expect(changesRequest(true, {})).toEqual({});
    expect(changesRequest(true, { session: 's1', run: 'r1' })).toEqual({ session: 's1', run: 'r1' });
    expect(changesRequest(true, { session: ['s1', 's2'], run: '' })).toEqual({});
  });
});

describe('the DiffView', () => {
  let renderer: ReactTestRenderer | undefined;
  afterEach(() => {
    act(() => renderer?.unmount());
    renderer = undefined;
  });

  function texts(view: ReactTestRenderer): string[] {
    return view.root.findAllByType(Text).map((node: ReactTestInstance) => [node.props.children].flat(3).filter((part) => typeof part === 'string' || typeof part === 'number').join(''));
  }

  it('draws the base, the counts, the truncated note and the rows with their marks', () => {
    const value = changes({
      base: 'head',
      headSha: 'abc1234',
      added: 9000,
      removed: 1,
      truncated: true,
      hunks: [{ oldStart: 4, oldLines: 1, newStart: 4, newLines: 1, lines: [{ op: '-', text: 'old', noNewline: true }, { op: '+', text: 'new' }] }],
    });
    act(() => {
      renderer = TestRenderer.create(createElement(DiffView, { changes: value }));
    });
    const shown = texts(renderer!);
    expect(shown).toEqual(expect.arrayContaining(['마지막 커밋(HEAD abc1234)과 비교', '+9000', '−1', '변경이 많아 앞부분만 표시합니다.', '@@ -4,1 +4,1 @@', 'old', 'new', '\\ 파일 끝에 줄바꿈 없음']));
  });
});
