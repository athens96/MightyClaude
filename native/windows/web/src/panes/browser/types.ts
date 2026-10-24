// A browser pane's state as the host reads it (MainWindow.WebShell.Panes.cs, from Core's BrowserHistory): the page draws the
// toolbar and an empty slot, and the host lays its own WebView2 (the workspace's browser profile) over the slot.

export interface BrowserState {
  /** ready: the browser is live; disabled: the setting is off; missing: no WebView2 runtime; failed: it could not start. */
  status: 'starting' | 'ready' | 'disabled' | 'missing' | 'failed';
  url?: string;
  canGoBack: boolean;
  canGoForward: boolean;
  /** While the runtime installs, and what the install said when it ended. */
  installing?: boolean;
  installMessage?: string;
  installFailed?: boolean;
}

/** The slot in device pixels of the page (CSS pixels × devicePixelRatio), as the `browserBounds` method takes it. */
export interface SlotBounds {
  x: number;
  y: number;
  width: number;
  height: number;
  visible: boolean;
}

/** The slot's rectangle in device pixels, whole pixels covering it; hidden (or empty) when it cannot show. */
export function slotBounds(rect: { left: number; top: number; width: number; height: number }, ratio: number, visible: boolean): SlotBounds {
  const x = Math.round(rect.left * ratio);
  const y = Math.round(rect.top * ratio);
  const width = Math.max(0, Math.round((rect.left + rect.width) * ratio) - x);
  const height = Math.max(0, Math.round((rect.top + rect.height) * ratio) - y);
  return { x, y, width, height, visible: visible && width > 0 && height > 0 };
}

export function sameBounds(a: SlotBounds | undefined, b: SlotBounds): boolean {
  return a !== undefined && a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height && a.visible === b.visible;
}
