// The WinUI forms the host draws through the page (MightyClaude.WinUI/WebNative.cs, MainWindow.WebShell.Settings.cs): Settings,
// the setup screen, the sheets and the dialogs of the window's flows. A form arrives as a tree of nodes read off its controls;
// every input goes back to the control it came from (`nativeAction`), so what it does stays the host's C#.

/** A token's CSS variable name without the dashes (`ink2`, `err-text`, `card-raised`). */
export type Ink = string;

interface Base {
  /** The control's automation id, kept for the smoke and for tests. */
  aid?: string;
  help?: string;
  margin?: [number, number, number, number];
  ha?: 'start' | 'center' | 'end';
  va?: 'start' | 'center' | 'end';
  w?: number;
  h?: number;
  maxW?: number;
  maxH?: number;
  minW?: number;
  dim?: boolean;
  disabled?: boolean;
  /** In a grid: its cell. */
  row?: number;
  col?: number;
  rowSpan?: number;
  colSpan?: number;
}

export type NativeNode = Base & (
  | { t: 'text'; text: string; size?: number; weight?: 'bold' | 'medium'; mono?: boolean; ink?: Ink; nowrap?: boolean; selectable?: boolean }
  | { t: 'glyph'; glyph: string; size?: number; ink?: Ink | null }
  | { t: 'image'; src: string }
  | { t: 'dot'; fill: Ink; size: number }
  | { t: 'spinner' }
  | { t: 'progress'; value: number; max: number; indeterminate: boolean }
  | { t: 'rule'; fill: Ink; vertical: boolean }
  | { t: 'link'; id: number; label: string }
  | { t: 'check'; id: number; on: boolean; label: string }
  | { t: 'radio'; id: number; on: boolean; label: string }
  | { t: 'switch'; id: number; on: boolean; name: string }
  | { t: 'segmented'; name: string; options: { id: number; label: string; on: boolean }[] }
  | { t: 'select'; id: number; index: number; name: string; items: string[] }
  | { t: 'field'; id: number; text: string; placeholder?: string; multiline?: boolean; readonly?: boolean; password?: boolean; name?: string; mono?: boolean }
  | { t: 'slider'; id: number; value: number; min: number; max: number; step: number; name: string }
  | { t: 'button'; id: number; name: string; label?: string; c?: NativeNode[]; accent?: boolean; destructive?: boolean }
  | { t: 'menu'; id: number; name: string; label?: string; c?: NativeNode[]; items: MenuItem[] }
  | { t: 'expander'; id: number; open: boolean; header: NativeNode | null; c: NativeNode[] | null }
  | { t: 'info'; title?: string; message?: string; severity: string }
  | { t: 'list'; id: number; index: number; items: NativeNode[] }
  | { t: 'stack'; dir: 'h' | 'v'; gap?: number; pad?: [number, number, number, number]; fill?: Ink; c: NativeNode[] }
  | { t: 'grid'; cols: string[] | null; rows: string[] | null; gap: [number, number]; pad?: [number, number, number, number]; fill?: Ink; c: NativeNode[] }
  | { t: 'box'; fill?: Ink; line?: [number, number, number, number]; lineInk?: Ink | null; pad?: [number, number, number, number]; radius?: number; c: NativeNode[] }
);

export interface MenuItem {
  id?: number;
  label?: string;
  sep?: boolean;
  checked?: boolean;
  disabled?: boolean;
  items?: MenuItem[];
}

export interface NativeSection {
  title?: string | null;
  beta?: boolean;
  node: NativeNode | null;
}

export interface NativeButton {
  id: 'primary' | 'secondary' | 'close';
  label: string;
  disabled: boolean;
  accent: boolean;
}

/** One view: Settings (with its categories), the setup screen, a dialog (with its buttons) or the companion popover. */
export interface NativeView {
  kind: 'settings' | 'setup' | 'dialog' | 'companion';
  title: string;
  subtitle?: string;
  close?: string;
  nav?: { id: string; title: string; glyph: string }[];
  selected?: string;
  sections: NativeSection[];
  footer?: NativeNode | null;
  buttons?: NativeButton[];
  width?: number | null;
  height?: number | null;
}

export type NativePlace = 'surface' | 'dialog' | 'popover';

export interface NativeOpen {
  id: string;
  place: NativePlace;
  view: NativeView;
}

/** The text setting (TextAppearance): the scale of every size and the families chosen, null for the design's own. */
export interface TextSetting {
  scale: number;
  family?: string | null;
  transcriptFamily?: string | null;
  codeFamily?: string | null;
}

export type NativePush =
  | { kind: 'native'; id: string; place?: NativePlace; view: NativeView | null }
  | ({ kind: 'text' } & TextSetting);

/** The open views after a push: a view keeps its place in the order when it is drawn again, a new one goes on top. */
export function applyNative(open: readonly NativeOpen[], message: Extract<NativePush, { kind: 'native' }>): readonly NativeOpen[] {
  if (!message.view) return open.some((item) => item.id === message.id) ? open.filter((item) => item.id !== message.id) : open;
  const next: NativeOpen = { id: message.id, place: message.place ?? 'surface', view: message.view };
  const at = open.findIndex((item) => item.id === message.id);
  if (at < 0) return [...open, next];
  const copy = open.slice();
  copy[at] = next;
  return copy;
}

/** A node's CSS: its margin, alignment and sizes as the WinUI control set them. */
export function boxStyle(node: Base): Record<string, string | number> {
  const style: Record<string, string | number> = {};
  if (node.margin) style.margin = edges(node.margin);
  if (node.w !== undefined) style.width = node.w;
  if (node.h !== undefined) style.height = node.h;
  if (node.maxW !== undefined) style.maxWidth = node.maxW;
  if (node.maxH !== undefined) { style.maxHeight = node.maxH; style.overflow = 'auto'; }
  if (node.minW !== undefined) style.minWidth = node.minW;
  if (node.ha) { style.justifySelf = node.ha; style.alignSelf = node.ha === 'center' ? 'center' : node.ha === 'end' ? 'flex-end' : 'flex-start'; }
  if (node.dim) style.opacity = 0.5;
  if (node.row !== undefined) style.gridRow = `${node.row + 1} / span ${node.rowSpan ?? 1}`;
  if (node.col !== undefined) style.gridColumn = `${node.col + 1} / span ${node.colSpan ?? 1}`;
  if (node.va && node.row !== undefined) style.alignSelf = node.va;
  return style;
}

/** WinUI's left, top, right, bottom as CSS's top, right, bottom, left. */
export function edges(value: readonly [number, number, number, number]): string {
  const [left, top, right, bottom] = value;
  return `${top}px ${right}px ${bottom}px ${left}px`;
}

/** A token name as its CSS variable. */
export function ink(name: Ink | null | undefined): string | undefined {
  return name ? `var(--${name})` : undefined;
}

/** The text of a node and all under it, for a label read aloud or searched. */
export function nodeText(node: NativeNode | null | undefined): string {
  if (!node) return '';
  switch (node.t) {
    case 'text': return node.text;
    case 'button': case 'menu': return node.label ?? (node.c ?? []).map(nodeText).join(' ');
    case 'link': case 'check': case 'radio': return node.label;
    case 'stack': case 'grid': case 'box': return node.c.map(nodeText).filter(Boolean).join(' ');
    case 'expander': return [nodeText(node.header), ...(node.c ?? []).map(nodeText)].join(' ');
    case 'list': return node.items.map(nodeText).join(' ');
    default: return '';
  }
}
