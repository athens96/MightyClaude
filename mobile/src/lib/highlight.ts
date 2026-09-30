/**
 * The Mac files pane's highlighter (`MightyCore/SourceHighlighter.swift`) ported
 * line for line: one pass, comments, strings, numbers and keywords, in UTF-16
 * offsets (which is what a JS string index already is). Both sides are checked
 * against `native/contracts/fixtures/source-highlight.json`.
 */

export type SourceLanguage =
  | 'swift'
  | 'c'
  | 'javascript'
  | 'python'
  | 'kotlin'
  | 'java'
  | 'go'
  | 'rust'
  | 'shell'
  | 'json'
  | 'yaml'
  | 'toml'
  | 'xml'
  | 'html'
  | 'css'
  | 'sql'
  | 'gradle'
  | 'dockerfile'
  | 'makefile'
  | 'plain';

export type TokenKind = 'keyword' | 'string' | 'comment' | 'number';

export interface SourceToken {
  kind: TokenKind;
  location: number;
  length: number;
}

/** Only this many UTF-16 units are scanned, as on the Mac. */
export const MAX_HIGHLIGHT_UNITS = 400_000;

/** Longer lines make the source view wrap instead of scrolling sideways, as on the Mac. */
export const WRAP_THRESHOLD = 5_000;

interface Rules {
  lineComments: string[];
  blockComment?: [string, string];
  quotes: string[];
  multilineQuotes: string[];
  tripleQuotes: boolean;
  keywords: Set<string>;
  caseInsensitive: boolean;
}

function words(text: string): Set<string> {
  return new Set(text.split(' ').filter((word) => word.length > 0));
}

function rules(partial: Partial<Rules>): Rules {
  return {
    lineComments: [],
    quotes: ['"', "'"],
    multilineQuotes: [],
    tripleQuotes: false,
    keywords: new Set(),
    caseInsensitive: false,
    ...partial,
  };
}

const slash = ['//'];
const cBlock: [string, string] = ['/*', '*/'];

function rulesFor(language: SourceLanguage): Rules {
  switch (language) {
    case 'swift':
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        quotes: ['"'],
        tripleQuotes: true,
        keywords: words(
          'actor as associatedtype async await break case catch class continue default defer deinit do else enum extension fallthrough false fileprivate final for func guard if import in init inout internal is lazy let mutating nil nonisolated open operator override private protocol public repeat rethrows return self Self some static struct subscript super switch throw throws true try typealias var weak where while any',
        ),
      });
    case 'c':
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        keywords: words(
          'auto bool break case char class const constexpr continue default delete do double else enum explicit extern false float for friend goto if inline int long namespace new nullptr operator private protected public register return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while NULL nil YES NO self super id',
        ),
      });
    case 'javascript':
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        quotes: ['"', "'", '`'],
        multilineQuotes: ['`'],
        keywords: words(
          'abstract as async await break case catch class const continue debugger declare default delete do else enum export extends false finally for from function get if implements import in instanceof interface keyof let new null of private protected public readonly return set static super switch this throw true try type typeof undefined var void while yield',
        ),
      });
    case 'python':
      return rules({
        lineComments: ['#'],
        tripleQuotes: true,
        keywords: words(
          'False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return self try while with yield match case',
        ),
      });
    case 'kotlin':
    case 'gradle':
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        tripleQuotes: true,
        keywords: words(
          'abstract annotation as break by catch class companion const constructor continue data do else enum false final finally for fun if import in init inline interface internal is lateinit null object open operator override package private protected public return sealed super suspend this throw true try typealias val var when while def apply plugins dependencies implementation',
        ),
      });
    case 'java':
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        tripleQuotes: true,
        keywords: words(
          'abstract boolean break byte case catch char class const continue default do double else enum extends false final finally float for if implements import instanceof int interface long native new null package private protected public record return short static super switch synchronized this throw throws transient true try var void volatile while',
        ),
      });
    case 'go':
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        quotes: ['"', "'", '`'],
        multilineQuotes: ['`'],
        keywords: words(
          'break case chan const continue default defer else fallthrough false for func go goto if import interface iota map nil package range return select struct switch true type var',
        ),
      });
    case 'rust':
      // '\'' is also a lifetime marker, so only double-quoted strings.
      return rules({
        lineComments: slash,
        blockComment: cBlock,
        quotes: ['"'],
        multilineQuotes: ['"'],
        keywords: words(
          'as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while',
        ),
      });
    case 'shell':
    case 'dockerfile':
    case 'makefile': {
      const keywords = words(
        'if then else elif fi for while until do done case esac in function return export local readonly source echo exit set unset',
      );
      if (language === 'dockerfile') {
        for (const word of words(
          'from run cmd label expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell as',
        ))
          keywords.add(word);
      }
      if (language === 'makefile') {
        for (const word of words('ifeq ifneq ifdef ifndef endif include define endef override'))
          keywords.add(word);
      }
      return rules({
        lineComments: ['#'],
        quotes: ['"', "'"],
        multilineQuotes: ['"', "'"],
        keywords,
        caseInsensitive: language === 'dockerfile',
      });
    }
    case 'json':
      return rules({ quotes: ['"'], keywords: words('true false null') });
    case 'yaml':
      return rules({ lineComments: ['#'], keywords: words('true false null yes no on off') });
    case 'toml':
      return rules({ lineComments: ['#'], tripleQuotes: true, keywords: words('true false') });
    case 'xml':
    case 'html':
      return rules({ blockComment: ['<!--', '-->'], quotes: ['"'] });
    case 'css':
      return rules({ blockComment: cBlock, keywords: words('important inherit initial unset none auto') });
    case 'sql':
      return rules({
        lineComments: ['--'],
        blockComment: cBlock,
        quotes: ["'", '"'],
        keywords: words(
          'add all alter and as asc begin between by case check column commit constraint create database default delete desc distinct drop else end exists foreign from group having if in index inner insert into is join key left like limit not null on or order outer primary references right rollback select set table then union unique update values view when where with',
        ),
        caseInsensitive: true,
      });
    case 'plain':
      return rules({ quotes: [] });
  }
}

const LANGUAGES = new Set<string>([
  'swift', 'c', 'javascript', 'python', 'kotlin', 'java', 'go', 'rust', 'shell', 'json',
  'yaml', 'toml', 'xml', 'html', 'css', 'sql', 'gradle', 'dockerfile', 'makefile', 'plain',
]);

/** The host's `language`, or `plain` for one this app does not know. */
export function sourceLanguage(value: string | undefined): SourceLanguage {
  return value !== undefined && LANGUAGES.has(value) ? (value as SourceLanguage) : 'plain';
}

const isDigit = (unit: number) => unit >= 0x30 && unit <= 0x39;
const isIdentifierStart = (unit: number) =>
  (unit >= 0x41 && unit <= 0x5a) || (unit >= 0x61 && unit <= 0x7a) || unit === 0x5f;
const isIdentifier = (unit: number) => isIdentifierStart(unit) || isDigit(unit);
const isSpace = (unit: number) => unit === 0x20 || unit === 0x09 || unit === 0x0a || unit === 0x0d;

export function highlight(
  text: string,
  language: SourceLanguage,
  maximumUnits: number = MAX_HIGHLIGHT_UNITS,
): SourceToken[] {
  if (language === 'plain') return [];
  const rule = rulesFor(language);
  const source = text.length > maximumUnits ? text.slice(0, maximumUnits) : text;
  const count = source.length;
  const quotes = new Set(rule.quotes.map((quote) => quote.charCodeAt(0)));
  const multiline = new Set(rule.multilineQuotes.map((quote) => quote.charCodeAt(0)));
  const result: SourceToken[] = [];
  const newline = 0x0a;
  const backslash = 0x5c;
  const doubleQuote = 0x22;
  let index = 0;

  const matches = (pattern: string, position: number) =>
    position + pattern.length <= count && source.startsWith(pattern, position);
  const find = (pattern: string, from: number) => {
    const found = source.indexOf(pattern, from);
    return found < 0 ? undefined : found;
  };
  const lineEnd = (from: number) => {
    const found = source.indexOf('\n', from);
    return found < 0 ? count : found;
  };
  const add = (kind: TokenKind, start: number, end: number) => {
    if (end > start) result.push({ kind, location: start, length: end - start });
  };

  scan: while (index < count) {
    const unit = source.charCodeAt(index);
    if (rule.blockComment && matches(rule.blockComment[0], index)) {
      const [open, close] = rule.blockComment;
      const found = find(close, index + open.length);
      const end = found === undefined ? count : found + close.length;
      add('comment', index, end);
      index = end;
      continue;
    }
    for (const marker of rule.lineComments) {
      if (!matches(marker, index)) continue;
      // "#" starts a comment only at a word boundary ("$#", "a#b" do not).
      if (marker === '#' && index > 0 && !isSpace(source.charCodeAt(index - 1))) break;
      const end = lineEnd(index);
      add('comment', index, end);
      index = end;
      continue scan;
    }
    if (rule.tripleQuotes && unit === doubleQuote && matches('"""', index)) {
      const found = find('"""', index + 3);
      const end = found === undefined ? count : found + 3;
      add('string', index, end);
      index = end;
      continue;
    }
    if (quotes.has(unit)) {
      let cursor = index + 1;
      while (cursor < count) {
        const current = source.charCodeAt(cursor);
        if (current === backslash) {
          cursor += 2;
          continue;
        }
        if (current === unit) {
          cursor += 1;
          break;
        }
        if (current === newline && !multiline.has(unit)) break;
        cursor += 1;
      }
      const end = Math.min(cursor, count);
      add('string', index, end);
      index = end;
      continue;
    }
    if (isDigit(unit) && (index === 0 || !isIdentifier(source.charCodeAt(index - 1)))) {
      let cursor = index + 1;
      while (
        cursor < count &&
        (isIdentifier(source.charCodeAt(cursor)) ||
          (source.charCodeAt(cursor) === 0x2e &&
            cursor + 1 < count &&
            isDigit(source.charCodeAt(cursor + 1))))
      )
        cursor += 1;
      add('number', index, cursor);
      index = cursor;
      continue;
    }
    if (isIdentifierStart(unit)) {
      let cursor = index + 1;
      while (cursor < count && isIdentifier(source.charCodeAt(cursor))) cursor += 1;
      const word = source.slice(index, cursor);
      if (rule.keywords.has(rule.caseInsensitive ? word.toLowerCase() : word)) {
        add('keyword', index, cursor);
      }
      index = cursor;
      continue;
    }
    index += 1;
  }
  return result;
}

/** A run of one line: plain text or one token's kind. */
export interface Segment {
  text: string;
  kind?: TokenKind;
}

/**
 * The text cut into lines where the Mac's text view breaks them (\n, \r, \r\n as one,
 * U+2028, U+2029), each line cut into runs by the tokens. A token that spans lines
 * (a block comment, a multi-line string) colours its part of every line.
 */
export function splitLines(text: string, tokens: readonly SourceToken[]): Segment[][] {
  const lines: Segment[][] = [];
  let lineStart = 0;
  let tokenIndex = 0;
  const pushLine = (start: number, end: number) => {
    const segments: Segment[] = [];
    let cursor = start;
    while (tokenIndex < tokens.length) {
      const token = tokens[tokenIndex]!;
      const tokenEnd = token.location + token.length;
      if (tokenEnd <= cursor) {
        tokenIndex += 1;
        continue;
      }
      if (token.location >= end) break;
      const from = Math.max(token.location, cursor);
      if (from > cursor) segments.push({ text: text.slice(cursor, from) });
      const to = Math.min(tokenEnd, end);
      segments.push({ text: text.slice(from, to), kind: token.kind });
      cursor = to;
      if (tokenEnd > end) break;
      tokenIndex += 1;
    }
    if (cursor < end) segments.push({ text: text.slice(cursor, end) });
    lines.push(segments);
  };
  for (let index = 0; index < text.length; index += 1) {
    const unit = text.charCodeAt(index);
    if (unit === 0x0d || unit === 0x0a || unit === 0x2028 || unit === 0x2029) {
      pushLine(lineStart, index);
      if (unit === 0x0d && text.charCodeAt(index + 1) === 0x0a) index += 1;
      lineStart = index + 1;
    }
  }
  pushLine(lineStart, text.length);
  return lines;
}

/** The longest line in UTF-16 units, counted like `splitLines` breaks them. */
export function longestLine(lines: readonly Segment[][]): number {
  let longest = 0;
  for (const line of lines) {
    let length = 0;
    for (const segment of line) length += segment.text.length;
    longest = Math.max(longest, length);
  }
  return longest;
}

/** Wide glyphs (Hangul, CJK, full-width forms, surrogate pairs) take two columns. */
function isWide(unit: number): boolean {
  return (
    (unit >= 0x1100 && unit <= 0x115f) ||
    (unit >= 0x2e80 && unit <= 0xa4cf) ||
    (unit >= 0xac00 && unit <= 0xd7a3) ||
    (unit >= 0xf900 && unit <= 0xfaff) ||
    (unit >= 0xfe30 && unit <= 0xfe4f) ||
    (unit >= 0xff00 && unit <= 0xff60) ||
    (unit >= 0xffe0 && unit <= 0xffe6) ||
    (unit >= 0xd800 && unit <= 0xdbff)
  );
}

/** The widest line in monospaced columns, a wide glyph counting two, a tab four. */
export function widestColumns(lines: readonly Segment[][]): number {
  let widest = 0;
  for (const line of lines) {
    let columns = 0;
    for (const segment of line) {
      for (let index = 0; index < segment.text.length; index += 1) {
        const unit = segment.text.charCodeAt(index);
        if (unit >= 0xdc00 && unit <= 0xdfff) continue;
        columns += unit === 0x09 ? 4 : isWide(unit) ? 2 : 1;
      }
    }
    widest = Math.max(widest, columns);
  }
  return widest;
}
