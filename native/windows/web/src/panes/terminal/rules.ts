// The terminal page's rules (Assets/Terminal/terminal.js), pure so they are tested: the surfaces, the font, the clipboard keys.
import { terminalPalette } from '../../theme/tokens';

/**
 * xterm's palettes by theme, from the design tokens (windowsOnly.terminal): the Mac's terminal surface
 * (native/macos/Sources/MightyClaude/AppStore+Terminals.swift:63-66), Ghostty's Afterglow on #202020 with #DEDCD8 text and an
 * #E1AB91 cursor by night, its Alabaster on #FAF9F6 by day.
 */
export const terminalThemes = { dark: terminalPalette('dark'), light: terminalPalette('light') } as const;

export const defaultFamily = 'Cascadia Mono, Consolas, monospace';

/** The app's code font first, then the stand-ins; a name that is empty or too long is not used. */
export function terminalFamily(family: string | null | undefined): string {
  return typeof family === 'string' && family.length > 0 && family.length <= 200 ? '"' + family.replace(/["\\]/g, '') + '", ' + defaultFamily : defaultFamily;
}

/** The text setting's size when it is one a terminal can take (12 at 100%). */
export function terminalSize(size: number | undefined): number | null {
  return typeof size === 'number' && size >= 6 && size <= 48 ? size : null;
}

export interface KeyLike {
  type: string;
  code: string;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  isComposing: boolean;
}

/**
 * Ctrl+C copies when text is chosen (or with Shift, which never interrupts), Ctrl+V pastes; anything else goes to the shell.
 * Physical codes, so they also work while the Korean layout is chosen.
 */
export function clipboardKey(event: KeyLike, hasSelection: boolean): 'copy' | 'paste' | null {
  if (!event.ctrlKey || event.altKey || event.type !== 'keydown' || event.isComposing) return null;
  if (event.code === 'KeyC' && (event.shiftKey || hasSelection)) return 'copy';
  if (event.code === 'KeyV') return 'paste';
  return null;
}

/** A base64 chunk as its bytes (xterm decodes UTF-8 across chunks itself). */
export function base64Bytes(data: string): Uint8Array {
  const text = atob(data);
  const bytes = new Uint8Array(text.length);
  for (let index = 0; index < text.length; index++) bytes[index] = text.charCodeAt(index);
  return bytes;
}
