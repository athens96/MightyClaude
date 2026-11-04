// The popup standard's kind D (.omc/plans/popup-standard.md): the run settings, the session details, the usage, the composer's
// menus, the structure list and the companion popover. One look (`.popover` in dialogs/dialog.css: radius 10, the popover shadow,
// a line in the dark theme, at most 60% of the window) and one rule: a press outside it or Esc closes it, and for a popover with
// [Cancel] [Apply] that close is the cancel.
import { useEffect, useRef } from 'react';

/** Closes a popover on a press outside it or on Esc; the ref goes on the popover (or on what holds it and its anchor). */
export function usePopover<T extends HTMLElement = HTMLDivElement>(close: () => void, open = true) {
  const ref = useRef<T>(null);
  const latest = useRef(close);
  latest.current = close;
  useEffect(() => {
    if (!open) return undefined;
    const press = (event: PointerEvent) => { if (ref.current && !ref.current.contains(event.target as Node)) latest.current(); };
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape' && !event.isComposing) latest.current(); };
    document.addEventListener('pointerdown', press, true);
    document.addEventListener('keydown', key);
    return () => { document.removeEventListener('pointerdown', press, true); document.removeEventListener('keydown', key); };
  }, [open]);
  return ref;
}
