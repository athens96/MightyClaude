// The files pane's keys and source spans (WinUI MainWindow.Files.cs BuildFilesTree, SourceView), pure so they are tested.
import type { FileRow } from './types';

export type TreeAction =
  | { kind: 'select'; path: string }
  | { kind: 'toggle'; path: string }
  | { kind: 'reveal'; path: string }
  | { kind: 'none' };

/**
 * A key on the tree: up and down move the choice; right opens a closed folder, or goes to the first child of an open one;
 * left closes an open folder, or goes to the parent; Enter opens or closes a folder. While filtering the rows are a flat list
 * of results, so right and Enter reveal a folder in the tree and left does nothing.
 */
export function treeKey(rows: readonly FileRow[], selected: string | undefined, key: string, filtering: boolean): TreeAction {
  const index = rows.findIndex((row) => row.path === selected);
  const row = index >= 0 ? rows[index] : undefined;
  switch (key) {
    case 'ArrowDown': {
      const next = rows[index < 0 ? 0 : Math.min(rows.length - 1, index + 1)];
      return next && next.path !== selected ? { kind: 'select', path: next.path } : { kind: 'none' };
    }
    case 'ArrowUp': {
      const next = rows[index < 0 ? 0 : Math.max(0, index - 1)];
      return next && next.path !== selected ? { kind: 'select', path: next.path } : { kind: 'none' };
    }
    case 'ArrowRight': {
      if (!row?.dir) return { kind: 'none' };
      if (filtering) return { kind: 'reveal', path: row.path };
      if (!row.expanded) return { kind: 'toggle', path: row.path };
      const child = rows[index + 1];
      return child && child.depth > row.depth ? { kind: 'select', path: child.path } : { kind: 'none' };
    }
    case 'ArrowLeft':
      if (!row || filtering) return { kind: 'none' };
      if (row.dir && row.expanded) return { kind: 'toggle', path: row.path };
      return row.path.includes('/') ? { kind: 'select', path: row.path.slice(0, row.path.lastIndexOf('/')) } : { kind: 'none' };
    case 'Enter':
      if (!row?.dir) return { kind: 'none' };
      return filtering ? { kind: 'reveal', path: row.path } : { kind: 'toggle', path: row.path };
    default:
      return { kind: 'none' };
  }
}

export interface Span {
  text: string;
  /** keyword, string, comment or number; absent for plain text. */
  kind?: string;
}

const tokenKinds = ['keyword', 'string', 'comment', 'number'];

/** The text cut at the host's coloured spans (SourceHighlighter tokens, in order and not overlapping). */
export function sourceSpans(text: string, tokens: readonly number[] | undefined): Span[] {
  const spans: Span[] = [];
  let position = 0;
  for (let index = 0; tokens && index + 2 < tokens.length; index += 3) {
    const kind = tokens[index] ?? 3, start = tokens[index + 1] ?? 0, length = tokens[index + 2] ?? 0;
    if (start < position || start + length > text.length) continue;
    if (start > position) spans.push({ text: text.slice(position, start) });
    spans.push({ text: text.slice(start, start + length), kind: tokenKinds[kind] ?? 'number' });
    position = start + length;
  }
  if (position < text.length) spans.push({ text: text.slice(position) });
  return spans;
}

/** "1\n2\n…" for the line-number column. */
export function lineNumbers(count: number): string {
  return Array.from({ length: Math.max(1, count) }, (_, index) => String(index + 1)).join('\n');
}
