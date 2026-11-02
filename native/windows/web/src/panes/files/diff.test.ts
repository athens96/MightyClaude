import { describe, expect, it } from 'vitest';
import { baseCaption, diffRows, gutterDigits, hunkHeader, showsChanges } from './diff';
import type { FileChanges } from './types';
import fixture from '@tokens/file-diff.json';

const changes = (patch: Partial<FileChanges>): FileChanges => ({ base: 'run', hunks: [], added: 0, removed: 0, truncated: false, unchanged: false, ...patch });

describe('변경사항 rows (DiffView)', () => {
  it('numbers each hunk from its own starts: context on both sides, removed on the old, added on the new', () => {
    const rows = diffRows({
      hunks: [
        { oldStart: 1, oldLines: 3, newStart: 1, newLines: 3, lines: [{ op: ' ', text: 'a' }, { op: '-', text: 'b' }, { op: '+', text: 'B' }, { op: ' ', text: 'c' }] },
        { oldStart: 98, oldLines: 2, newStart: 98, newLines: 3, lines: [{ op: ' ', text: 'x' }, { op: '+', text: 'y' }, { op: ' ', text: 'z' }] },
      ],
    });
    expect(rows).toEqual([
      { kind: 'hunk', old: null, new: null, text: '@@ -1,3 +1,3 @@' },
      { kind: 'context', old: 1, new: 1, text: 'a' },
      { kind: 'removed', old: 2, new: null, text: 'b' },
      { kind: 'added', old: null, new: 2, text: 'B' },
      { kind: 'context', old: 3, new: 3, text: 'c' },
      { kind: 'hunk', old: null, new: null, text: '@@ -98,2 +98,3 @@' },
      { kind: 'context', old: 98, new: 98, text: 'x' },
      { kind: 'added', old: null, new: 99, text: 'y' },
      { kind: 'context', old: 99, new: 100, text: 'z' },
    ]);
    expect(gutterDigits(rows)).toBe(3);
  });

  it('a created file starts its empty old side at 0 and numbers only the new side', () => {
    const rows = diffRows({ hunks: [{ oldStart: 0, oldLines: 0, newStart: 1, newLines: 2, lines: [{ op: '+', text: 'one' }, { op: '+', text: 'two' }] }] });
    expect(rows.map((r) => [r.kind, r.old, r.new])).toEqual([['hunk', null, null], ['added', null, 1], ['added', null, 2]]);
    expect(rows[0]!.text).toBe(hunkHeader({ oldStart: 0, oldLines: 0, newStart: 1, newLines: 2, lines: [] }));
    expect(rows[0]!.text).toBe('@@ -0,0 +1,2 @@');
    expect(gutterDigits([])).toBe(1);
  });

  it('marks a line without a line break with a row under it', () => {
    const rows = diffRows({ hunks: [{ oldStart: 1, oldLines: 1, newStart: 1, newLines: 1, lines: [{ op: '-', text: 'end', noNewline: true }, { op: '+', text: 'end' }] }] });
    expect(rows.map((r) => r.kind)).toEqual(['hunk', 'removed', 'noNewline', 'added']);
    expect(rows[2]).toEqual({ kind: 'noNewline', old: null, new: null, text: '' });
  });

  it('names the base: a created file, the run, HEAD with or without its commit', () => {
    expect(baseCaption({ base: 'run', created: true })).toBe('created');
    expect(baseCaption({ base: 'head', created: true, headSha: 'abc1234' })).toBe('created');
    expect(baseCaption({ base: 'run' })).toBe('run');
    expect(baseCaption({ base: 'head', headSha: 'abc1234' })).toBe('head');
    expect(baseCaption({ base: 'head' })).toBe('headPlain');
  });

  it('offers the toggle only for a change set that changed something, truncated or not', () => {
    const hunk = { oldStart: 1, oldLines: 1, newStart: 1, newLines: 1, lines: [{ op: '-' as const, text: 'a' }, { op: '+' as const, text: 'b' }] };
    expect(showsChanges(null)).toBe(false);
    expect(showsChanges(undefined)).toBe(false);
    expect(showsChanges(changes({ unchanged: true }))).toBe(false);
    expect(showsChanges(changes({ hunks: [] }))).toBe(false);
    expect(showsChanges(changes({ hunks: [hunk], truncated: true, added: 9000, removed: 1 }))).toBe(true);
  });

  it('numbers every shared fixture case to its hunk counts', () => {
    for (const item of fixture.cases) {
      const hunks = (item.expect as { hunks?: FileChanges['hunks'] } | null)?.hunks ?? [];
      for (const hunk of hunks) {
        const rows = diffRows({ hunks: [hunk] });
        const old = rows.filter((r) => r.old !== null).map((r) => r.old);
        const next = rows.filter((r) => r.new !== null).map((r) => r.new);
        expect(old.length, item.name).toBe(hunk.oldLines);
        expect(next.length, item.name).toBe(hunk.newLines);
        if (old.length) expect(old.at(-1), item.name).toBe(hunk.oldStart + hunk.oldLines - 1);
        if (next.length) expect(next.at(-1), item.name).toBe(hunk.newStart + hunk.newLines - 1);
      }
    }
  });
});
