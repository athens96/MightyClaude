import { ApiError, byteLength, describeError } from '@/api/client';
import type { FileEntry, FileEntryKind, FilePreview } from '@/api/types';
import { t } from '@/lib/i18n';

/**
 * Pure helpers for the read-only files screens (docs/file-pane.md "휴대폰"). Paths are
 * the host's: `/`-separated under the workspace root, `""` for the root itself, and
 * always sent back exactly as the host listed them.
 */

/** Above this the Mac renders markdown as source; the phone follows it. */
export const MAX_MARKDOWN_RENDER_BYTES = 131_072;

export function isFolder(kind: FileEntryKind | string): boolean {
  return kind === 'folder' || kind === 'symlink-folder';
}

export function isSymlink(kind: FileEntryKind | string): boolean {
  return kind === 'symlink-folder' || kind === 'symlink-file';
}

/** The folder above `path`; the root's parent is the root. */
export function parentPath(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash < 0 ? '' : path.slice(0, slash);
}

/**
 * Where a back action on the folder screen goes: the parent folder while below the
 * root, for a plain back (header button, iOS swipe, Android back button — `GO_BACK`,
 * or `POP` of this one screen); `undefined` lets the action leave the screen.
 */
export function backTarget(path: string, action: { type: string; payload?: object }): string | undefined {
  if (!path) return undefined;
  const count = (action.payload as { count?: unknown } | undefined)?.count;
  const plainBack = action.type === 'GO_BACK' || (action.type === 'POP' && (count === undefined || count === 1));
  return plainBack ? parentPath(path) : undefined;
}

/** The last component, or `""` for the root. */
export function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

export interface Crumb {
  label: string;
  path: string;
}

/** The root (labelled with the workspace name) and every folder down to `path`. */
export function breadcrumbs(path: string, rootLabel: string): Crumb[] {
  const crumbs: Crumb[] = [{ label: rootLabel, path: '' }];
  if (!path) return crumbs;
  const parts = path.split('/');
  parts.forEach((part, index) => {
    crumbs.push({ label: displayName(part), path: parts.slice(0, index + 1).join('/') });
  });
  return crumbs;
}

/**
 * A Mac file name may be stored decomposed (NFD), which some phones draw as loose
 * jamo; it is shown composed. The path sent back to the host is never changed.
 */
export function displayName(name: string): string {
  return name.normalize('NFC');
}

/** The entries whose name holds the trimmed query, case and composition ignored. */
export function filterEntries(entries: readonly FileEntry[], query: string): FileEntry[] {
  const needle = query.trim().normalize('NFC').toLowerCase();
  if (!needle) return [...entries];
  return entries.filter((entry) => displayName(entry.name).toLowerCase().includes(needle));
}

/** The host's ISO-8601 date in the phone's own format, or `""` when absent or unreadable. */
export function formatModified(iso: string | undefined, locale?: string): string {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  try {
    return date.toLocaleString(locale);
  } catch {
    return date.toISOString();
  }
}

export type FileView =
  | { kind: 'source'; text: string; language: string }
  | { kind: 'markdown'; text: string; renderable: boolean }
  | { kind: 'image'; uri: string }
  | { kind: 'unsupported' };

/**
 * Which viewer draws a preview. A reply missing what its `type` promises (or a type
 * this app does not know) is shown as unsupported rather than trusted.
 */
export function viewFor(preview: FilePreview): FileView {
  switch (preview.type) {
    case 'source':
      return typeof preview.text === 'string'
        ? { kind: 'source', text: preview.text, language: preview.language ?? 'plain' }
        : { kind: 'unsupported' };
    case 'markdown':
      return typeof preview.text === 'string'
        ? {
            kind: 'markdown',
            text: preview.text,
            renderable: byteLength(preview.text) <= MAX_MARKDOWN_RENDER_BYTES,
          }
        : { kind: 'unsupported' };
    case 'image':
      return typeof preview.data === 'string' &&
        preview.data.length > 0 &&
        (preview.mime === 'image/jpeg' || preview.mime === 'image/png')
        ? { kind: 'image', uri: `data:${preview.mime};base64,${preview.data}` }
        : { kind: 'unsupported' };
    default:
      return { kind: 'unsupported' };
  }
}

/** Why a file is shown as unsupported, in the phone's words. */
export function unsupportedReason(preview: FilePreview): string {
  switch (preview.reason) {
    case 'notRegularFile':
      return t('phone.files.unsupported.notRegularFile');
    case 'tooLarge':
      return t('phone.files.unsupported.tooLarge');
    case 'undecodable':
      return t('phone.files.unsupported.undecodable');
    default:
      return t('phone.files.unsupported.binary');
  }
}

/** A refused file request in the phone's words; anything else as the app describes it. */
export function describeFileError(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'workspaceNotFound':
        return t('phone.files.error.workspaceNotFound');
      case 'notFound':
        return t('phone.files.error.notFound');
      case 'outsideWorkspace':
        return t('phone.files.error.outsideWorkspace');
      case 'notReadable':
        return t('phone.files.error.notReadable');
      case 'notDirectory':
        return t('phone.files.error.notDirectory');
      case 'badPath':
        // 414: the whole request path is over what the host's tunnel takes.
        return error.status === 414 ? t('phone.files.error.pathTooLong') : t('phone.files.error.badPath');
    }
  }
  return describeError(error);
}
