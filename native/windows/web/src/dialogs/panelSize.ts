// A panel's size (popup standard kind C, .omc/plans/popup-standard.md decision 12): its default, the least it shrinks to, what the
// window leaves it, and the last size the person gave each kind of panel (plugins, plan document, pictures, settings…), kept in
// the page's own storage, which lives in the profile's WebView2 data like the rest of the page's state.

export interface Size {
  width: number;
  height: number;
}

/** What a dialog leaves clear of the window's edges on every side (the WinUI sheet's SheetMargin). */
export const WindowMargin = 20;

/** The panel's size inside the window: at least `min`, at most what the window leaves (which wins over `min`). */
export function clampSize(size: Size, min: Size, room: Size): Size {
  const most = { width: Math.max(0, room.width - 2 * WindowMargin), height: Math.max(0, room.height - 2 * WindowMargin) };
  const clamp = (value: number, least: number, highest: number) => Math.round(Math.min(highest, Math.max(least, value)));
  return { width: clamp(size.width, Math.min(min.width, most.width), most.width), height: clamp(size.height, Math.min(min.height, most.height), most.height) };
}

/** The size that fits the window (the header's double-click). */
export function fitSize(room: Size): Size {
  return { width: Math.max(0, room.width - 2 * WindowMargin), height: Math.max(0, room.height - 2 * WindowMargin) };
}

/**
 * The size a drag of the corner grip gives: the panel stays centred in the window, so the corner moves half of what the size
 * does, and the size changes by twice the pointer's travel to keep the grip under the pointer.
 */
export function dragSize(start: Size, dx: number, dy: number, min: Size, room: Size): Size {
  return clampSize({ width: start.width + 2 * dx, height: start.height + 2 * dy }, min, room);
}

const key = (panel: string) => 'mighty.panelSize.' + panel;

/** The size last given to this kind of panel, or null (none kept, unreadable, or no storage). */
export function loadSize(panel: string, storage: Pick<Storage, 'getItem'> | null = safeStorage()): Size | null {
  try {
    const value: unknown = JSON.parse(storage?.getItem(key(panel)) ?? 'null');
    if (typeof value !== 'object' || value === null) return null;
    const { width, height } = value as Record<string, unknown>;
    return typeof width === 'number' && typeof height === 'number' && width > 0 && height > 0 && Number.isFinite(width) && Number.isFinite(height) ? { width, height } : null;
  } catch {
    return null;
  }
}

export function saveSize(panel: string, size: Size, storage: Pick<Storage, 'setItem'> | null = safeStorage()): void {
  try { storage?.setItem(key(panel), JSON.stringify({ width: Math.round(size.width), height: Math.round(size.height) })); } catch { /* a full or refused store keeps the default */ }
}

function safeStorage(): Storage | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage; } catch { return null; }
}

/** A form sheet's width (kind B): the standard's S 360, M 460 or L 560 nearest above what the form asked for. */
export function sheetWidth(asked: number | null | undefined, widths: { s: number; m: number; l: number }): number {
  if (!asked) return widths.m;
  return asked <= widths.s ? widths.s : asked <= widths.m ? widths.m : widths.l;
}
