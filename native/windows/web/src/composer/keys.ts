// The editor's Enter (MainWindow.cs PreviewKeyDown, SubmitKeyAllowed): no send during an IME composition, with Shift (a new
// line), or with Alt or the Windows key; a held, repeating Enter does not send again; Ctrl+Enter is the second send.

export interface EnterKey {
  key: string;
  isComposing: boolean;
  keyCode: number;
  shiftKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  ctrlKey: boolean;
  repeat: boolean;
}

/** `send` / `second`: send (the second send with Ctrl); `swallow`: a repeat, kept from the editor; `pass`: the editor's own. */
export type EnterAction = 'send' | 'second' | 'swallow' | 'pass';

export function enterAction(event: EnterKey): EnterAction {
  if (event.key !== 'Enter') return 'pass';
  // keyCode 229 is the IME's own Enter, which some IMEs send after composition ends.
  if (event.isComposing || event.keyCode === 229) return 'pass';
  if (event.shiftKey || event.altKey || event.metaKey) return 'pass';
  if (event.repeat) return 'swallow';
  return event.ctrlKey ? 'second' : 'send';
}

/** The editor's text limit (the WinUI TextBox's MaxLength). */
export const maximumDraft = 100_000;
