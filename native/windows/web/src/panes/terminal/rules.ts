// The terminal page's rules (Assets/Terminal/terminal.js), pure so they are tested: the surfaces, the font, the clipboard keys.

/**
 * The Mac's terminal surface (native/macos/Sources/MightyClaude/AppStore+Terminals.swift:63-74): Ghostty's Afterglow on
 * #202020 with #DEDCD8 text and an #E1AB91 cursor by night, its Alabaster on #FAF9F6 by day.
 */
export const terminalThemes = {
  dark: {
    background: '#202020', foreground: '#dedcd8', cursor: '#e1ab91', cursorAccent: '#202020', selectionBackground: '#303030',
    black: '#151515', red: '#ac4142', green: '#7e8e50', yellow: '#e5b567', blue: '#6c99bb', magenta: '#9f4e85', cyan: '#7dd6cf', white: '#d0d0d0',
    brightBlack: '#505050', brightRed: '#ac4142', brightGreen: '#7e8e50', brightYellow: '#e5b567', brightBlue: '#6c99bb', brightMagenta: '#9f4e85', brightCyan: '#7dd6cf', brightWhite: '#f5f5f5',
  },
  light: {
    background: '#faf9f6', foreground: '#000000', cursor: '#007acc', cursorAccent: '#faf9f6', selectionBackground: '#bfdbfe',
    black: '#000000', red: '#aa3731', green: '#448c27', yellow: '#cb9000', blue: '#325cc0', magenta: '#7a3e9d', cyan: '#0083b2', white: '#f7f7f7',
    brightBlack: '#777777', brightRed: '#f05050', brightGreen: '#60cb00', brightYellow: '#ffbc5d', brightBlue: '#007acc', brightMagenta: '#e64ce6', brightCyan: '#00aacb', brightWhite: '#f7f7f7',
  },
} as const;

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
