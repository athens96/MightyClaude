import { MAX_RUN_RESULT_FILES, type MobileResultFile } from '@/api/types';

/**
 * The files a Mighty run's final result names (`MobileMightyRun.resultFiles`). The host
 * finds them with the very rule the desktop's 결과에 나온 파일 panel uses and sends only
 * real files inside the workspace; the phone never reads a path out of the text itself.
 */

/** Rows the list shows before it asks to show the rest. */
export const RESULT_FILES_FOLDED = 5;
/** The longest path the file preview route takes, in UTF-8 bytes (docs/mobile-remote.md "파일"). */
export const MAX_RESULT_FILE_PATH_BYTES = 4096;

function utf8Bytes(text: string): number {
  let bytes = 0;
  for (const char of text) {
    const code = char.codePointAt(0) ?? 0;
    bytes += code < 0x80 ? 1 : code < 0x800 ? 2 : code < 0x10000 ? 3 : 4;
  }
  return bytes;
}

/**
 * A path the file preview route would take: relative, `/`-joined, with no empty, `.` or
 * `..` component, no NUL, no backslash, at most `MAX_RESULT_FILE_PATH_BYTES`.
 */
function previewablePath(path: string): boolean {
  if (path.length === 0 || path.includes('\0') || path.includes('\\') || utf8Bytes(path) > MAX_RESULT_FILE_PATH_BYTES) return false;
  return path.split('/').every((part) => part.length > 0 && part !== '.' && part !== '..');
}

/**
 * `resultFiles` as the host sent it: entries with a path the preview route takes, a line
 * only when it is a positive integer, each path once, at most `MAX_RUN_RESULT_FILES`.
 * Undefined for anything else or nothing left, so an older host and an empty list look alike.
 */
export function parseResultFiles(raw: unknown): MobileResultFile[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  const seen = new Set<string>();
  const files: MobileResultFile[] = [];
  for (const item of raw) {
    if (files.length >= MAX_RUN_RESULT_FILES) break;
    if (item === null || typeof item !== 'object' || Array.isArray(item)) continue;
    const { path, line } = item as Record<string, unknown>;
    if (typeof path !== 'string' || !previewablePath(path) || seen.has(path)) continue;
    seen.add(path);
    const file: MobileResultFile = { path };
    if (typeof line === 'number' && Number.isInteger(line) && line > 0) file.line = line;
    files.push(file);
  }
  return files.length > 0 ? files : undefined;
}

/**
 * What a row shows: the file's name (with `:line` when the result gave one) and the folder
 * it is in, relative to the workspace (`""` at the root), which tells same names apart.
 */
export function resultFileParts(file: MobileResultFile): { name: string; line: string; folder: string } {
  const slash = file.path.lastIndexOf('/');
  return {
    name: file.path.slice(slash + 1),
    line: file.line !== undefined ? `:${file.line}` : '',
    folder: slash > 0 ? file.path.slice(0, slash) : '',
  };
}

/** The rows shown: the first `RESULT_FILES_FOLDED` until the rest are asked for, and how many are held back. */
export function shownResultFiles(files: readonly MobileResultFile[], all: boolean): { shown: readonly MobileResultFile[]; hidden: number } {
  if (all || files.length <= RESULT_FILES_FOLDED) return { shown: files, hidden: 0 };
  return { shown: files.slice(0, RESULT_FILES_FOLDED), hidden: files.length - RESULT_FILES_FOLDED };
}

/** The workspace file preview a row opens: the same screen the files browser opens a file in. */
export function resultFileRoute(hostId: string, workspaceId: string, file: MobileResultFile) {
  return {
    pathname: '/host/[hostId]/workspace/[workspaceId]/file' as const,
    params: { hostId, workspaceId, path: file.path },
  };
}
