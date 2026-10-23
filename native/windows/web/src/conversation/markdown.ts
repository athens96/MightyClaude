// The conversation's Markdown, the subset and the limits of the WinUI transcript (AgentTranscript.cs Markdown/Parse/Spans,
// spec: .omc/plans/agent-pane-spec.md §2): a pure function from text to a small node tree, memoized per text. No HTML is
// produced here and nothing is fetched; the view draws the nodes as React elements.
import { parseQuestionnaire, type Question } from './questionnaire';
import { localPath, utf8Length, webUrl } from './references';

export type Inline =
  | { type: 'text'; text: string }
  | { type: 'code'; text: string }
  | { type: 'strong' | 'em' | 'strongEm' | 'strike'; children: Inline[] }
  /** `web`: http, https or mailto, drawn `label (url)` in the accent; `local`: a file, label and `(path)` underlined. */
  | { type: 'link'; kind: 'web' | 'local'; target: string; children: Inline[] }
  | { type: 'autolink'; url: string }
  /**
   * A picture: `data` (a data: image, drawn), `remote` (never fetched: its link and `images.remote`), `local` (a file the
   * host may show: its alt and its path as a reference), `refused` (`images.refused`), `alt` (where pictures are not drawn).
   */
  | { type: 'image'; kind: 'data' | 'remote' | 'local' | 'refused' | 'alt'; alt: string; source: string };

export type Block =
  | { type: 'paragraph'; inline: Inline[] }
  | { type: 'heading'; level: number; inline: Inline[] }
  | { type: 'code'; language: string | null; text: string }
  | { type: 'questionnaire'; questions: Question[] }
  | { type: 'quote'; children: Block[] }
  | { type: 'list'; ordered: boolean; items: ListItem[] }
  | { type: 'rule' }
  | { type: 'table'; align: Align[]; rows: Inline[][][] }
  /** Lines drawn as written, nothing in them read as Markdown: a table too large, or a source past the limits. */
  | { type: 'source'; lines: string[] };

export type Align = 'left' | 'center' | 'right';

export interface ListItem {
  ordinal: number;
  task: 'checked' | 'open' | null;
  children: Block[];
}

/** Longer sources are shown as their lines (AgentTranscript.MaximumRenderBytes). */
export const maximumRenderBytes = 131_072;
export const maximumDepth = 8;
export const maximumTableColumns = 63;
export const maximumTableCells = 2_000;
/** How long one source may take before it is shown as its lines (AgentTranscript.MarkdownBudget). */
export const markdownBudgetMs = 250;

const fence = /^(\s{0,3})(`{3,}|~{3,})\s*([^`]*)$/;
const heading = /^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$/;
const thematicBreak = /^\s{0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$/;
const listItem = /^(\s*)([-*+]|\d{1,9}[.)])(?:\s+(.*))?$/;
const tableDelimiter = /^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$/;
// An escaped mark, a picture, code, bold italic, bold, italic, strikethrough, a link, an address in angle brackets.
const inlinePattern = new RegExp(
  String.raw`(\\[\\` + '`' + String.raw`*_{}\[\]()#+\-.!~|>])|(!\[[^\]\r\n]*\]\([^)\r\n]+\))|(` + '``' + String.raw`[^\r\n]+?` + '``|`[^`\\r\\n]+`' + String.raw`)|(\*\*\*[^*\r\n]+\*\*\*)` +
    String.raw`|(\*\*[^\r\n]+?\*\*|(?<![\p{L}\p{N}])__[^\r\n]+?__(?![\p{L}\p{N}]))|((?<!\*)\*(?![\s*])[^*\r\n]+\*|(?<![\p{L}\p{N}_])_(?![\s_])[^_\r\n]+_(?![\p{L}\p{N}_]))` +
    String.raw`|(~~[^~\r\n]+~~)|(\[[^\]\r\n]+\]\([^)\r\n]+\))|(<(?:https?:\/\/|mailto:)[^>\s]+>)`,
  'gu',
);
const picturePattern = /!\[([^\]\n]{0,500})\]\(\s*(?:<([^>\n]+)>|([^)\s]+))(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)/u;
const dataImage = /^data:image\/(?:png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]+$/i;

class OutOfTime extends Error {}

/** What one source may still spend: its table cells and its time. */
interface Budget {
  cells: number;
  deadline: number;
}

function spend(budget: Budget): void {
  if (performance.now() > budget.deadline) throw new OutOfTime();
}

export function splitLines(text: string): string[] {
  return text.replace(/\r\n?/g, '\n').split('\n');
}

const blank = (line: string) => line.trim().length === 0;

function indentOf(line: string): number {
  let width = 0;
  for (const c of line) {
    if (c === ' ') width++;
    else if (c === '\t') width += 4;
    else break;
  }
  return width;
}

function unindent(line: string, width: number): string {
  let at = 0;
  while (at < line.length && width > 0 && (line[at] === ' ' || line[at] === '\t')) {
    width -= line[at] === '\t' ? 4 : 1;
    at++;
  }
  return line.slice(at);
}

function interrupts(line: string): boolean {
  const item = listItem.exec(line);
  return fence.test(line) || heading.test(line) || thematicBreak.test(line) || line.trimStart().startsWith('>') || (item !== null && item[3] !== undefined);
}

const isWhite = (c: string) => /\s/.test(c);
const isAsciiLetter = (c: string | undefined) => c !== undefined && /^[A-Za-z]$/.test(c);

/**
 * AgentTranscript.InPath: whether the word round `at` is a Windows path (a drive, or a backslash before a letter, a digit or a
 * dot-name), whose backslashes all stay instead of being read as Markdown escapes.
 */
export function inPath(text: string, at: number): boolean {
  let start = at, end = at;
  while (start > 0 && !isWhite(text[start - 1]!)) start--;
  while (end < text.length && !isWhite(text[end]!)) end++;
  const run = text.slice(start, end);
  const head = run.replace(/^["'([<]+/, '');
  let path = head.length >= 3 && isAsciiLetter(head[0]) && head[1] === ':' && head[2] === '\\' && !(head.length > 3 && head[3] === '\\');
  for (let index = 0; !path && index + 1 < run.length; index++) {
    if (run[index] !== '\\') continue;
    if (run[index + 1] === '\\' && index > run.length - head.length) {
      index++;
      continue;
    }
    const next = run[index + 1]!;
    path = /[\p{L}\p{N}]/u.test(next) || (next === '.' && index + 2 < run.length && /\p{L}/u.test(run[index + 2]!));
  }
  return path;
}

/** A table row's cells; an escaped pipe is a pipe in its cell, but for the backslash of a Windows path. */
function cells(row: string): string[] {
  let text = row.trim();
  if (text.startsWith('|')) text = text.slice(1);
  if (text.endsWith('|') && !text.endsWith('\\|')) text = text.slice(0, -1);
  const pipes = (cell: string) => {
    if (!cell.includes('\\|')) return cell;
    let words = '';
    for (let at = 0; at < cell.length; at++) if (cell[at] !== '\\' || at + 1 === cell.length || cell[at + 1] !== '|' || inPath(cell, at)) words += cell[at];
    return words;
  };
  return text.split(/(?<!\\)\|/).map((cell) => pipes(cell.trim()));
}

function tableStarts(lines: readonly string[], i: number): boolean {
  const next = lines[i + 1];
  return lines[i]!.includes('|') && next !== undefined && next.includes('-') && tableDelimiter.test(next) && cells(lines[i]!).length === cells(next).length;
}

/** The blocks with their words still raw: the parse of AgentTranscript.Parse. */
type Raw =
  | { type: 'paragraph'; text: string }
  | { type: 'heading'; level: number; text: string }
  | { type: 'code'; language: string | null; text: string }
  | { type: 'quote'; children: Raw[] }
  | { type: 'list'; ordered: boolean; items: { ordinal: number; children: Raw[] }[] }
  | { type: 'rule' }
  | { type: 'table'; align: Align[]; rows: string[][] }
  | { type: 'source'; lines: string[] };

function parseBlocks(lines: readonly string[], depth: number, budget: Budget): Raw[] {
  const blocks: Raw[] = [];
  let i = 0;
  while (i < lines.length) {
    spend(budget);
    const line = lines[i]!;
    if (blank(line)) {
      i++;
      continue;
    }
    const opened = fence.exec(line);
    if (opened) {
      // A fence left open still is a code block, so an answer reads while it streams.
      const mark = opened[2]!;
      const code: string[] = [];
      i++;
      while (i < lines.length && !(lines[i]!.trimStart().startsWith(mark) && lines[i]!.trim().split(mark[0]!).join('').length === 0)) code.push(unindent(lines[i++]!, opened[1]!.length));
      if (i < lines.length) i++;
      const info = opened[3]!.trim();
      blocks.push({ type: 'code', language: info.length === 0 ? null : info, text: code.join('\n').replace(/\n+$/, '') });
      continue;
    }
    const title = heading.exec(line);
    if (title) {
      blocks.push({ type: 'heading', level: title[1]!.length, text: title[2]! });
      i++;
      continue;
    }
    if (thematicBreak.test(line)) {
      blocks.push({ type: 'rule' });
      i++;
      continue;
    }
    if (depth < maximumDepth && line.trimStart().startsWith('>')) {
      const inner: string[] = [];
      for (; i < lines.length && lines[i]!.trimStart().startsWith('>'); i++) {
        const rest = lines[i]!.trimStart().slice(1);
        inner.push(rest.startsWith(' ') ? rest.slice(1) : rest);
      }
      blocks.push({ type: 'quote', children: parseBlocks(inner, depth + 1, budget) });
      continue;
    }
    if (depth < maximumDepth && listItem.test(line)) {
      const [list, next] = parseList(lines, i, depth, budget);
      blocks.push(list);
      i = next;
      continue;
    }
    if (tableStarts(lines, i)) {
      const head = i;
      const align = cells(lines[i + 1]!).map((cell): Align => (cell.startsWith(':') && cell.endsWith(':') ? 'center' : cell.endsWith(':') ? 'right' : 'left'));
      i += 2;
      while (i < lines.length && !blank(lines[i]!) && lines[i]!.includes('|')) i++;
      // Every row has the head's columns, so the table's cells are known before one is read.
      const count = align.length * (i - head - 1);
      if (align.length > maximumTableColumns || count > budget.cells) {
        blocks.push({ type: 'source', lines: lines.slice(head, i) });
        continue;
      }
      budget.cells -= count;
      const rows = [cells(line)];
      for (let row = head + 2; row < i; row++) rows.push(cells(lines[row]!));
      blocks.push({ type: 'table', align, rows });
      continue;
    }
    let paragraph = '';
    let hard = false;
    // A paragraph also ends over a table's head row.
    for (; i < lines.length && !blank(lines[i]!) && (paragraph.length === 0 || (!interrupts(lines[i]!) && !tableStarts(lines, i))); i++) {
      spend(budget);
      if (paragraph.length > 0) paragraph += hard ? '\n' : ' ';
      // A backslash at a line's end breaks the line there, but the one a Windows path ends in is the path's own.
      const words = lines[i]!.trim();
      const path = words.endsWith('\\') && inPath(words, words.length - 1);
      hard = lines[i]!.endsWith('  ') || (lines[i]!.endsWith('\\') && !path);
      paragraph += path ? words : words.replace(/\\+$/, '');
    }
    blocks.push({ type: 'paragraph', text: paragraph });
  }
  return blocks;
}

function parseList(lines: readonly string[], start: number, depth: number, budget: Budget): [Raw, number] {
  let i = start;
  const opening = listItem.exec(lines[i]!)!;
  const margin = indentOf(lines[i]!);
  const ordered = /\d/.test(opening[2]![0]!);
  let ordinal = ordered ? Number.parseInt(opening[2]!.slice(0, -1), 10) : 1;
  const items: { ordinal: number; children: Raw[] }[] = [];
  const sibling = (line: string) => {
    const item = listItem.exec(line);
    const indent = indentOf(line);
    return item !== null && indent >= margin && indent <= margin + 1 && /\d/.test(item[2]![0]!) === ordered;
  };
  while (i < lines.length && sibling(lines[i]!)) {
    const item = listItem.exec(lines[i]!)!;
    const inner = margin + item[2]!.length + 1;
    const content = [item[3] ?? ''];
    i++;
    while (i < lines.length) {
      spend(budget);
      const line = lines[i]!;
      if (blank(line)) {
        // Blank lines stay in the item only when an indented line follows them.
        let next = i + 1;
        while (next < lines.length && blank(lines[next]!)) next++;
        if (next >= lines.length || indentOf(lines[next]!) < inner) break;
        for (; i < next; i++) content.push('');
        continue;
      }
      if (indentOf(line) >= inner) {
        content.push(unindent(line, inner));
        i++;
        continue;
      }
      if (listItem.test(line) || interrupts(line)) break;
      content.push(line.trim());
      i++;
    }
    items.push({ ordinal: ordinal++, children: parseBlocks(content, depth + 1, budget) });
    let after = i;
    while (after < lines.length && blank(lines[after]!)) after++;
    if (after < lines.length && sibling(lines[after]!)) i = after;
  }
  return [{ type: 'list', ordered, items }, i];
}

/** A picture's alt and source (AgentMarkdownImages.Pattern), or null when the words are not one. */
export function pictureOf(value: string): { alt: string; source: string } | null {
  const match = picturePattern.exec(value);
  const source = match?.[2] ?? match?.[3] ?? '';
  return match && source.length > 0 ? { alt: match[1]!, source } : null;
}

function image(value: string, pictures: boolean): Inline {
  const picture = pictureOf(value);
  if (!picture) return { type: 'text', text: value };
  const alt = picture.alt.trim();
  if (!pictures) return { type: 'image', kind: 'alt', alt: picture.alt, source: picture.source };
  const source = picture.source;
  const kind = dataImage.test(source) ? 'data' : /^https?:\/\//i.test(source) ? 'remote' : localPath(source) !== null ? 'local' : 'refused';
  return { type: 'image', kind, alt, source };
}

/** AgentTranscript.Spans: the inline marks of one paragraph, a heading or a cell. */
export function parseInline(text: string, pictures = true, depth = 0): Inline[] {
  const out: Inline[] = [];
  let position = 0;
  let pending = '';
  const flush = () => {
    if (pending.length > 0) out.push({ type: 'text', text: pending });
    pending = '';
  };
  const nested = (inner: string) => (depth < maximumDepth ? parseInline(inner, pictures, depth + 1) : [{ type: 'text' as const, text: inner }]);
  for (const match of text.matchAll(inlinePattern)) {
    // No escape is read inside a Windows path: its backslashes stay.
    if (match[1] !== undefined && inPath(text, match.index)) continue;
    pending += text.slice(position, match.index);
    const value = match[0];
    position = match.index + value.length;
    if (match[1] !== undefined) {
      pending += value.slice(1);
      continue;
    }
    flush();
    if (match[2] !== undefined) out.push(image(value, pictures));
    else if (match[3] !== undefined) out.push({ type: 'code', text: value.startsWith('``') ? value.slice(2, -2).trim() : value.slice(1, -1) });
    else if (match[4] !== undefined) out.push({ type: 'strongEm', children: nested(value.slice(3, -3)) });
    else if (match[5] !== undefined) out.push({ type: 'strong', children: nested(value.slice(2, -2)) });
    else if (match[6] !== undefined) out.push({ type: 'em', children: nested(value.slice(1, -1)) });
    else if (match[7] !== undefined) out.push({ type: 'strike', children: nested(value.slice(2, -2)) });
    else if (match[9] !== undefined) out.push({ type: 'autolink', url: value.slice(1, -1) });
    else {
      const split = value.indexOf('](');
      const label = value.slice(1, split);
      const link = value.slice(split + 2, -1).trim();
      const local = localPath(link);
      if (webUrl(link) !== null) out.push({ type: 'link', kind: 'web', target: link, children: nested(label) });
      else if (local !== null) out.push({ type: 'link', kind: 'local', target: local, children: nested(label) });
      else out.push(...nested(label));
    }
  }
  pending += text.slice(position);
  flush();
  return out;
}

function build(blocks: readonly Raw[], pictures: boolean): Block[] {
  return blocks.map((block): Block => {
    switch (block.type) {
      case 'paragraph':
        return { type: 'paragraph', inline: parseInline(block.text, pictures) };
      case 'heading':
        return { type: 'heading', level: block.level, inline: parseInline(block.text, pictures) };
      case 'code': {
        const questions = parseQuestionnaire(block.text);
        return questions ? { type: 'questionnaire', questions } : block;
      }
      case 'quote':
        return { type: 'quote', children: build(block.children, pictures) };
      case 'list':
        return {
          type: 'list',
          ordered: block.ordered,
          items: block.items.map((item) => {
            const first = item.children[0];
            const mark = first?.type === 'paragraph' && first.text.length >= 4 ? first.text.slice(0, 4).toLowerCase() : '';
            if (first?.type === 'paragraph' && (mark === '[x] ' || mark === '[ ] ')) {
              const rest: Raw[] = [{ type: 'paragraph', text: first.text.slice(4) }, ...item.children.slice(1)];
              return { ordinal: item.ordinal, task: mark === '[x] ' ? 'checked' : 'open', children: build(rest, pictures) };
            }
            return { ordinal: item.ordinal, task: null, children: build(item.children, pictures) };
          }),
        };
      case 'table':
        return { type: 'table', align: block.align, rows: block.rows.map((row) => block.align.map((_, column) => parseInline(row[column] ?? '', false))) };
      case 'rule':
      case 'source':
        return block;
    }
  });
}

/** Markdown as nodes; past the size or time limit, the source's lines as they are. */
export function parseMarkdown(text: string, pictures = true, budgetMs = markdownBudgetMs): Block[] {
  const lines = splitLines(text);
  if (utf8Length(text) > maximumRenderBytes) return [{ type: 'source', lines }];
  try {
    const budget: Budget = { cells: maximumTableCells, deadline: performance.now() + budgetMs };
    return build(parseBlocks(lines, 0, budget), pictures);
  } catch (error) {
    if (error instanceof OutOfTime) return [{ type: 'source', lines }];
    throw error;
  }
}

const cache = new Map<string, Block[]>();
const cacheSize = 512;

/** {@link parseMarkdown} remembered per text: a settled entry is parsed once, also when it scrolls out and back in. */
export function markdown(text: string): Block[] {
  const known = cache.get(text);
  if (known) {
    cache.delete(text);
    cache.set(text, known);
    return known;
  }
  const blocks = parseMarkdown(text);
  cache.set(text, blocks);
  if (cache.size > cacheSize) cache.delete(cache.keys().next().value as string);
  return blocks;
}
