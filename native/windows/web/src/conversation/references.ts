// The file a click in the conversation names, and which link destinations are local files: MightyClaude.Core's
// ReferencePreview.TextTargetAt and ResultFiles.LocalPath, rule for rule. The host still resolves the path against the
// workspace before anything opens (ReferencePreview.Resolve).

export interface ReferenceTarget {
  path: string;
  line: number | null;
}

const referencePattern = /(?<![\p{L}\p{N}_])((?:[A-Za-z]:[\\/]|\.{1,2}[\\/]|\/)?(?:[\p{L}\p{N}_.@-]+[\\/])*[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,12})(?::(\d{1,6}))?/gu;
const destinationPattern = /^[^()<>\r\n]+\.[A-Za-z0-9]{1,12}(?::\d{1,6})?$/;

function lastIndexOfAny(text: string, marks: string, from: number): number {
  for (let index = Math.min(from, text.length - 1); index >= 0; index--) if (marks.includes(text[index]!)) return index;
  return -1;
}

function indexOfAny(text: string, marks: string, from: number): number {
  for (let index = Math.max(0, from); index < text.length; index++) if (marks.includes(text[index]!)) return index;
  return -1;
}

/** ReferencePreview.TextTargetAt: the file-like words under `position` of `text`, only on the line it falls in. */
export function textTargetAt(text: string, position: number): ReferenceTarget | null {
  if (position < 0 || position >= text.length) return null;
  const before = lastIndexOfAny(text, '\r\n', position);
  const start = before < 0 ? 0 : before + 1;
  const after = indexOfAny(text, '\r\n', position);
  const end = after < 0 ? text.length : after;
  if (end - start > 16_384) return null;
  const line = text.slice(start, end);
  const at = position - start;
  // Markdown destinations are shown in parentheses by the renderer: keep the spaces of those names.
  const open = line.lastIndexOf('(', at);
  const close = line.indexOf(')', at);
  if (open >= 0 && close > open && at > open) {
    const candidate = line.slice(open + 1, close);
    if (!candidate.includes('://') && destinationPattern.test(candidate)) {
      const colon = candidate.lastIndexOf(':');
      const number = colon > 1 ? candidate.slice(colon + 1) : '';
      if (colon > 1 && /^\d+$/.test(number)) return { path: candidate.slice(0, colon), line: Number(number) };
      return { path: candidate, line: null };
    }
  }
  for (const match of line.matchAll(referencePattern)) {
    const index = match.index;
    if (at < index || at >= index + match[0].length) continue;
    // Never read a URL's suffix as a local file.
    const tokenBefore = lastIndexOfAny(line, ' \t', index);
    const tokenAfter = indexOfAny(line, ' \t', index);
    if (line.slice(tokenBefore < 0 ? 0 : tokenBefore + 1, tokenAfter < 0 ? line.length : tokenAfter).includes('://')) return null;
    return { path: match[1]!, line: match[2] === undefined ? null : Number(match[2]) };
  }
  return null;
}

/** UTF-8 length, for the byte limits the host's rules count in. */
export function utf8Length(value: string): number {
  let bytes = 0;
  for (const character of value) {
    const code = character.codePointAt(0) ?? 0;
    bytes += code < 0x80 ? 1 : code < 0x800 ? 2 : code < 0x10000 ? 3 : 4;
  }
  return bytes;
}

/** ResultFiles.LocalPath: a relative or file: link destination as a path, or null for any other scheme or a host. */
export function localPath(destination: string): string | null {
  if (destination.length === 0) return null;
  const scheme = /^([A-Za-z][A-Za-z0-9+.-]*):/.exec(destination);
  const drive = destination.length >= 3 && /^[A-Za-z]:[/\\]/.test(destination);
  if (scheme && !drive && scheme[1]!.toLowerCase() !== 'file') return null;
  let value = destination;
  if (scheme && !drive) {
    if (!value.toLowerCase().startsWith('file://')) return null;
    value = value.slice('file://'.length);
    const slash = value.indexOf('/');
    if (slash !== 0) return null; // none, or a host: not a local path
  }
  let clean: string;
  try {
    clean = decodeURIComponent(value);
  } catch {
    clean = value;
  }
  if (/^\/[A-Za-z]:\//.test(clean)) clean = clean.slice(1);
  if (clean.length === 0 || clean.includes('://') || clean.includes('\0')) return null;
  return utf8Length(clean) <= 1024 ? clean : null;
}

/** The link destinations that open outside the app (AgentTranscript: http, https, mailto), as an absolute URL, else null. */
export function webUrl(destination: string): string | null {
  try {
    const url = new URL(destination);
    return url.protocol === 'http:' || url.protocol === 'https:' || url.protocol === 'mailto:' ? destination : null;
  } catch {
    return null;
  }
}
