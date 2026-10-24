// The window's shortcuts (spec section 8), owned by the page since the host clears its accelerators in web mode. Pure: a key
// in, the action it asks for out. The caller ignores them while a dialog is open.

export type ShortcutAction =
  | 'openFolder' | 'newPane' | 'search' | 'toggleSidebar' | 'newShell' | 'settings' | 'files' | 'help'
  | 'textLarger' | 'textSmaller' | 'textReset';

export interface ShortcutKey {
  key: string;
  code: string;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  metaKey: boolean;
}

export function shortcutAction(event: ShortcutKey): ShortcutAction | null {
  if (event.altKey || event.metaKey) return null;
  if (!event.ctrlKey) return event.key === 'F1' && !event.shiftKey ? 'help' : null;
  if (event.shiftKey) {
    if (event.code === 'KeyE') return 'files';
    // Ctrl++ on the main keyboard is Ctrl+Shift+=.
    return event.code === 'Equal' ? 'textLarger' : null;
  }
  switch (event.code) {
    case 'KeyO': return 'openFolder';
    case 'KeyN': return 'newPane';
    case 'KeyK': return 'search';
    case 'KeyB': return 'toggleSidebar';
    case 'KeyT': return 'newShell';
    case 'Comma': return 'settings';
    case 'Equal': case 'NumpadAdd': return 'textLarger';
    case 'Minus': case 'NumpadSubtract': return 'textSmaller';
    case 'Digit0': case 'Numpad0': return 'textReset';
    default: return null;
  }
}
