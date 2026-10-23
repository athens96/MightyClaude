// Dropped and pasted files, read in the page and sent to the host as base64. The host checks them again with
// AttachmentSupport's rules (MainWindow.WebShell.Composer.cs WebAddAttachments); the page refuses early only what the
// host would refuse anyway, with the same message keys (C/AttachmentSupport.cs:21-23, W/AttachmentInput.cs).

export const maximumCount = 8;
export const maximumFileBytes = 5 * 1024 * 1024;
export const maximumTotalBytes = 8 * 1024 * 1024;

export interface FileLike {
  name: string;
  size: number;
}

/** The locale key of the first limit broken by adding `files` to `present` attachments of `presentBytes`, or null. */
export function attachmentRefusal(files: readonly FileLike[], present: number, presentBytes: number): string | null {
  if (files.length === 0) return null;
  if (present + files.length > maximumCount) return 'composer.attachment.tooMany';
  if (files.some((file) => file.size > maximumFileBytes)) return 'composer.attachment.fileTooLarge';
  if (presentBytes + files.reduce((sum, file) => sum + file.size, 0) > maximumTotalBytes) return 'attachments.error.totalTooLarge';
  return null;
}

/** A size as the chips say it (ByteLabel, MainWindow.cs): bytes, whole KB, MB with one decimal; decimal units. */
export function byteLabel(bytes: number): string {
  if (bytes < 1000) return `${bytes} bytes`;
  if (bytes < 1_000_000) return `${Math.round(bytes / 1000)} KB`;
  return `${Number((bytes / 1_000_000).toFixed(1))} MB`;
}

/** The bytes of an ArrayBuffer as base64, in chunks so a 5 MB file does not overflow the call stack. */
export function toBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let at = 0; at < bytes.length; at += 0x8000) binary += String.fromCharCode(...bytes.subarray(at, at + 0x8000));
  return btoa(binary);
}
