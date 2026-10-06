/**
 * The `◆ <state> → next: <actions>` breadcrumb an Ouroboros reply ends with, read into
 * at most four suggestions the user can drop into the composer. The same contract is
 * implemented in `native/macos/Sources/MightyCore/NextActions.swift`, and both are held
 * to `native/contracts/fixtures/next-actions.json` (see `native/contracts/README.md`).
 */

export interface NextAction {
  /** The whole option as written, also the button's accessibility label. */
  label: string;
  /** What the composer receives: the command inside the option, or the label. */
  fill: string;
}

export const MAX_NEXT_ACTIONS = 4;

const MARKERS = ['→ next:', '-> next:'];
/**
 * Tried in this order at each position, so `, or` wins over the ` or` inside it.
 * An em dash splits only when `or`/`또는` follows it; otherwise it starts a note.
 */
const SEPARATORS = [', 또는 ', ', or ', ' — 또는 ', ' — or ', ' 또는 ', ' or '];
/** A comma list is a choice only when it says so at its end. */
const CHOICE_SUFFIXES = [' 중에서 선택', ' 중 선택', ' 중 하나'];
/**
 * The only characters trimmed or taken as a gap. Matching is literal code point by
 * code point with no Unicode normalization, so a decomposed `또는` is not a separator.
 */
const WHITESPACE = new Set([' ', '\t', '\r', '\n', '\u3000']);
/** The Korean "or" a reply may open an alternative with; matched, never shown. */
const KOREAN_OR = '또는';
const LEADING_ALTERNATIVE = new RegExp(`^(?:${KOREAN_OR}|[Oo][Rr])[ \\t\\r\\n\\u3000]+`);
/** `(?![\s\S])` is the end of the text in both regex engines, unlike `$`. */
const COMMAND = /^(?:ooo(?:[ \t]|(?![\s\S]))|\/[A-Za-z][A-Za-z0-9_-]*(?::[A-Za-z0-9_-]+)?(?:[ \t]|(?![\s\S])))/;

function trim(text: string): string {
  const chars = [...text];
  let start = 0;
  let end = chars.length;
  while (start < end && WHITESPACE.has(chars[start] ?? '')) start += 1;
  while (end > start && WHITESPACE.has(chars[end - 1] ?? '')) end -= 1;
  return chars.slice(start, end).join('');
}

/** Only ASCII letters fold, so both platforms compare the same characters. */
function asciiLower(char: string): string {
  return char >= 'A' && char <= 'Z' ? String.fromCharCode(char.charCodeAt(0) + 32) : char;
}

function matchesAt(chars: string[], index: number, separator: string): boolean {
  const wanted = [...separator];
  if (index + wanted.length > chars.length) return false;
  return wanted.every((char, offset) => asciiLower(chars[index + offset] ?? '') === char);
}

/** Splits outside backtick code spans and parentheses. */
function splitTopLevel(text: string, separators: string[]): string[] {
  const chars = [...text];
  const parts: string[] = [];
  let current = '';
  let inCode = false;
  let depth = 0;
  let index = 0;
  while (index < chars.length) {
    const char = chars[index] ?? '';
    if (!inCode && depth === 0) {
      const separator = separators.find((candidate) => matchesAt(chars, index, candidate));
      if (separator) {
        parts.push(current);
        current = '';
        index += [...separator].length;
        continue;
      }
    }
    if (char === '`') inCode = !inCode;
    else if (!inCode && char === '(') depth += 1;
    else if (!inCode && char === ')') depth = Math.max(0, depth - 1);
    current += char;
    index += 1;
  }
  parts.push(current);
  return parts;
}

/** Backtick pairs in order; an empty pair or an unclosed backtick yields nothing. */
function codeSpans(text: string): string[] {
  const spans: string[] = [];
  let open: string | undefined;
  for (const char of text) {
    if (char !== '`') {
      if (open !== undefined) open += char;
    } else if (open === undefined) {
      open = '';
    } else {
      if (open.length > 0) spans.push(open);
      open = undefined;
    }
  }
  return spans;
}

/** The command a button should fill: the first command-like code span, or a lone span. */
function fillFor(label: string): string {
  const spans = codeSpans(label);
  const command = spans.find((span) => COMMAND.test(trim(span)));
  if (command !== undefined) return trim(command);
  const only = spans[0];
  if (spans.length === 1 && only !== undefined && trim(label) === `\`${only}\``) return trim(only);
  return label;
}

/** The text after `next:` on the last breadcrumb line, if there is one. */
export function breadcrumbNext(text: string): string | undefined {
  if (!text.includes('◆')) return undefined;
  const lines = text.split('\n');
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    const line = trim(lines[index] ?? '');
    if (!line.startsWith('◆')) continue;
    const found = MARKERS.map((marker) => ({ marker, at: line.indexOf(marker) }))
      .filter((hit) => hit.at >= 0)
      .sort((a, b) => a.at - b.at)[0];
    if (found) return trim(line.slice(found.at + found.marker.length));
  }
  return undefined;
}

/** The suggestions in a reply's breadcrumb; empty when it has none. */
export function parseNextActions(text: string): NextAction[] {
  const next = breadcrumbNext(text);
  if (!next) return [];
  let list = next;
  const suffix = CHOICE_SUFFIXES.find((candidate) => list.endsWith(candidate));
  if (suffix) list = list.slice(0, list.length - suffix.length);
  let options = splitTopLevel(list, SEPARATORS);
  if (suffix) options = options.flatMap((option) => splitTopLevel(option, [',']));
  return options
    .map((option) => trim(trim(option).replace(LEADING_ALTERNATIVE, '')))
    .filter((option) => option.length > 0)
    .map((label) => ({ label, fill: fillFor(label) }))
    .filter((action) => trim(action.fill).length > 0)
    .slice(0, MAX_NEXT_ACTIONS);
}

/** The label as a button shows and reads it: backticks are markup, not text. */
export function nextActionText(action: NextAction): string {
  const plain = action.label.replace(/`/g, '');
  return trim(plain).length > 0 ? plain : action.label;
}

/**
 * A fill never loses the draft: a blank draft is replaced by the fill, anything else
 * keeps its text and gets the fill on a new line after it (no extra line when it
 * already ends with one).
 */
export function fillDraft(draft: string, fill: string): string {
  if (trim(draft).length === 0) return fill;
  return draft.endsWith('\n') ? `${draft}${fill}` : `${draft}\n${fill}`;
}

/**
 * The reply whose suggestions are on offer: the last assistant entry with no user entry
 * after it. The caller still hides them while the pane is running.
 */
export function latestNextActions(
  entries: readonly { id: string; kind: string; text: string }[],
): { entryId: string; actions: NextAction[] } | undefined {
  for (let index = entries.length - 1; index >= 0; index -= 1) {
    const entry = entries[index];
    if (!entry || entry.kind === 'user') return undefined;
    if (entry.kind !== 'assistant') continue;
    const actions = parseNextActions(entry.text);
    return actions.length > 0 ? { entryId: entry.id, actions } : undefined;
  }
  return undefined;
}
