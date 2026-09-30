import { readFileSync } from 'fs';
import { resolve } from 'path';
import {
  MAX_HIGHLIGHT_UNITS,
  highlight,
  longestLine,
  sourceLanguage,
  splitLines,
  widestColumns,
  type SourceLanguage,
  type SourceToken,
} from '@/lib/highlight';

/**
 * The Mac's `SourceHighlighter` is held to the same file
 * (`MobileWorkspaceFilesTests.theHighlighterMatchesThePhonesPortOnTheSharedFixture`),
 * so a mismatch here means the phone colours a file differently from the Mac.
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/source-highlight.json');
const cases = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  language: SourceLanguage;
  text: string;
  tokens: SourceToken[];
}[];

describe('the highlighter port', () => {
  it('matches the shared fixture for every language', () => {
    expect(cases.length).toBeGreaterThanOrEqual(20);
    expect(new Set(cases.map((item) => item.language)).size).toBe(20);
    for (const item of cases) {
      expect({ language: item.language, text: item.text, tokens: highlight(item.text, item.language) }).toEqual(item);
    }
  });

  it('counts offsets in UTF-16 units, as the Mac does', () => {
    const text = '"😀" 1';
    expect(highlight(text, 'json')).toEqual([
      { kind: 'string', location: 0, length: 4 },
      { kind: 'number', location: 5, length: 1 },
    ]);
  });

  it('stops at the scan limit', () => {
    const text = `${' '.repeat(MAX_HIGHLIGHT_UNITS)}let`;
    expect(highlight(text, 'swift')).toEqual([]);
    expect(highlight('let x', 'swift', 3)).toEqual([{ kind: 'keyword', location: 0, length: 3 }]);
  });

  it('falls back to plain for a language it does not know', () => {
    expect(sourceLanguage('swift')).toBe('swift');
    expect(sourceLanguage('cobol')).toBe('plain');
    expect(sourceLanguage(undefined)).toBe('plain');
    expect(highlight('let x = 1', sourceLanguage('cobol'))).toEqual([]);
  });
});

describe('splitting into lines', () => {
  it('breaks where the Mac text view does and keeps a trailing empty line', () => {
    const lines = splitLines('a\nb\r\nc\rd e f\n', []);
    expect(lines.map((line) => line.map((segment) => segment.text).join(''))).toEqual(['a', 'b', 'c', 'd', 'e', 'f', '']);
    expect(splitLines('', [])).toEqual([[]]);
  });

  it('colours each line’s part of a token that spans lines', () => {
    const text = 'x /* one\ntwo */ y';
    const lines = splitLines(text, highlight(text, 'swift'));
    expect(lines).toEqual([
      [{ text: 'x ' }, { text: '/* one', kind: 'comment' }],
      [{ text: 'two */', kind: 'comment' }, { text: ' y' }],
    ]);
  });

  it('puts several tokens of one line in order with the plain text between', () => {
    const text = 'let a = "s" // c';
    expect(splitLines(text, highlight(text, 'swift'))).toEqual([
      [
        { text: 'let', kind: 'keyword' },
        { text: ' a = ' },
        { text: '"s"', kind: 'string' },
        { text: ' ' },
        { text: '// c', kind: 'comment' },
      ],
    ]);
  });

  it('measures the longest line in units and the widest in columns', () => {
    const lines = splitLines('ab\n한글한\n\t😀', []);
    expect(longestLine(lines)).toBe(3);
    expect(widestColumns(lines)).toBe(6);
  });
});
