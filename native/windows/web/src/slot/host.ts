// What the Rust host says it draws natively (docs/tauri-architecture.md §3): its `host` push, sent when the page calls `ready`,
// before the snapshot. The C# host never sends one, so a page without it keeps the C# protocol (`browserBounds`); every use of
// this module is behind `import.meta.env.MODE === 'tauri'`, so the C# build holds none of it.
import { useSyncExternalStore } from 'react';

export type Drawing = 'web' | 'native';
export type SurfaceKind = 'terminal' | 'browser';

export interface HostCapabilities {
  platform: string;
  /** Who draws each kind of pane: the page (`web`, xterm.js) or the platform over the page's NativeSlot (`native`). */
  surfaces: Record<SurfaceKind, Drawing>;
}

/** host → page: `{kind:'host', platform, surfaces:{terminal, browser}}`. */
export interface HostPush extends HostCapabilities {
  kind: 'host';
}

export function isHostPush(message: { kind: string }): message is HostPush {
  return message.kind === 'host';
}

/** The push as the page keeps it: anything but `native` is the page's to draw. */
export function readCapabilities(message: { platform?: unknown; surfaces?: unknown }): HostCapabilities {
  const surfaces = (typeof message.surfaces === 'object' && message.surfaces !== null ? message.surfaces : {}) as Record<string, unknown>;
  const drawing = (kind: SurfaceKind): Drawing => (surfaces[kind] === 'native' ? 'native' : 'web');
  return { platform: typeof message.platform === 'string' ? message.platform : '', surfaces: { terminal: drawing('terminal'), browser: drawing('browser') } };
}

// The capabilities slice: one value for the page's life (a reload starts the page over), listeners told when it changes.
let current: HostCapabilities | undefined;
const listeners = new Set<() => void>();

export function applyHostPush(message: HostPush): void {
  current = readCapabilities(message);
  for (const listener of listeners) listener();
}

/** The host's capabilities, or undefined while it has said nothing (always, under the C# host). */
export function hostCapabilities(): HostCapabilities | undefined {
  return current;
}

/** Whether the platform draws this pane's terminal itself (the Mac's Ghostty), so the page reserves a slot instead of xterm.js. */
export function nativeTerminal(): boolean {
  return current?.surfaces.terminal === 'native';
}

export function subscribeHost(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useHostCapabilities(): HostCapabilities | undefined {
  return useSyncExternalStore(subscribeHost, hostCapabilities);
}

/** Tests only: forget the push. */
export function resetHostCapabilities(): void {
  current = undefined;
}
