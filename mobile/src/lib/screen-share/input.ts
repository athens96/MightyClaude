import type { ScreenMode } from '@/api/types';
import type { ZoomRegion } from '@/lib/screen-share/zoom';

/**
 * What the phone sends down the peer connection's data channel. None of this touches the
 * relay, so the relay's frame limits do not apply — but the Mac is still the one that
 * decides whether to inject anything, and it refuses everything while the grant is only
 * `view`, while the screen is locked, and while secure input is on.
 *
 * The coordinate contract is `displayId` plus normalized 0–1 x/y, which the Mac maps onto
 * `CGDisplayBounds`. Text is sent as a committed string — Korean is composed on the phone
 * and only the finished text goes out, never per-jamo key events.
 */

/** Longest single committed string; a paste that size goes through the clipboard instead. */
export const MAX_INPUT_TEXT_BYTES = 4096;

export type PointerButton = 'left' | 'right';
export type DragPhase = 'begin' | 'move' | 'end';
/**
 * One key combination in the contract's grammar: lower case, `+`-joined, zero to four
 * distinct modifiers (`ctrl`, `opt`, `shift`, `cmd`) and then exactly one named key.
 */
export type ScreenShortcut = string;

export const KEY_MODIFIERS = ['ctrl', 'opt', 'shift', 'cmd'] as const;
/** The closed list of key names the Mac accepts. */
export const KEY_NAMES: readonly string[] = [
  ...'abcdefghijklmnopqrstuvwxyz'.split(''),
  ...'0123456789'.split(''),
  'return',
  'tab',
  'space',
  'backspace',
  'delete',
  'escape',
  'left',
  'right',
  'up',
  'down',
  'home',
  'end',
  'pageup',
  'pagedown',
];

export interface NormalizedPoint {
  x: number;
  y: number;
}

export type ScreenInputEvent =
  | { t: 'tap'; displayId: number; x: number; y: number; button: PointerButton; marker?: string }
  | { t: 'drag'; displayId: number; x: number; y: number; phase: DragPhase }
  | { t: 'scroll'; displayId: number; x: number; y: number; dx: number; dy: number }
  | { t: 'text'; text: string }
  | { t: 'key'; combo: ScreenShortcut }
  | { t: 'zoom'; displayId: number; region: ZoomRegion }
  | { t: 'display'; displayId: number };

function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.min(1, Math.max(0, value));
}

function utf8Length(text: string): number {
  let bytes = 0;
  for (const char of text) {
    const code = char.codePointAt(0) ?? 0;
    if (code < 0x80) bytes += 1;
    else if (code < 0x800) bytes += 2;
    else if (code < 0x10000) bytes += 3;
    else bytes += 4;
  }
  return bytes;
}

/** True only while the Mac granted control; a view-only session sends no input at all. */
export function mayInject(mode: ScreenMode): boolean {
  return mode === 'control';
}

/** The marker ids the Mac accepts on a `tap`: 1–32 of `[A-Za-z0-9_-]`. */
export const TAP_MARKER_ID = /^[A-Za-z0-9_-]{1,32}$/;

/**
 * A left or right click. `marker` asks a Mac that advertises `tapMarker` to draw its
 * latency marker at the point and echo the id; a malformed id is left off rather than
 * sent, since the Mac would drop the whole tap.
 */
export function tapEvent(
  displayId: number,
  point: NormalizedPoint,
  button: PointerButton = 'left',
  marker?: string,
): ScreenInputEvent {
  const event: Extract<ScreenInputEvent, { t: 'tap' }> = {
    t: 'tap',
    displayId,
    x: clamp01(point.x),
    y: clamp01(point.y),
    button,
  };
  if (marker !== undefined && TAP_MARKER_ID.test(marker)) event.marker = marker;
  return event;
}

/** A long press is the right button: the Mac posts a right click at that point. */
export function rightClickEvent(displayId: number, point: NormalizedPoint): ScreenInputEvent {
  return tapEvent(displayId, point, 'right');
}

export function dragEvent(
  displayId: number,
  point: NormalizedPoint,
  phase: DragPhase,
): ScreenInputEvent {
  return { t: 'drag', displayId, x: clamp01(point.x), y: clamp01(point.y), phase };
}

/** Two-finger scroll; `dx`/`dy` are normalized deltas, positive down and right. */
export function scrollEvent(
  displayId: number,
  point: NormalizedPoint,
  delta: { dx: number; dy: number },
): ScreenInputEvent {
  return {
    t: 'scroll',
    displayId,
    x: clamp01(point.x),
    y: clamp01(point.y),
    dx: Number.isFinite(delta.dx) ? delta.dx : 0,
    dy: Number.isFinite(delta.dy) ? delta.dy : 0,
  };
}

/**
 * One committed string. English text arrives a character at a time from the keyboard and
 * Korean arrives whole once the IME commits a syllable; both travel the same way, so the
 * Mac never has to guess at a composition.
 */
export function textEvent(text: string): ScreenInputEvent | undefined {
  if (text.length === 0) return undefined;
  if (utf8Length(text) > MAX_INPUT_TEXT_BYTES) return undefined;
  return { t: 'text', text };
}

/** True for a combo the Mac's `key` grammar accepts; anything else is never sent. */
export function isKeyCombo(combo: string): boolean {
  const parts = combo.split('+');
  const key = parts.pop();
  if (key === undefined || !KEY_NAMES.includes(key)) return false;
  if (parts.length > KEY_MODIFIERS.length) return false;
  const seen = new Set<string>();
  for (const modifier of parts) {
    if (!(KEY_MODIFIERS as readonly string[]).includes(modifier) || seen.has(modifier)) return false;
    seen.add(modifier);
  }
  return true;
}

export function shortcutEvent(combo: ScreenShortcut): ScreenInputEvent | undefined {
  return isKeyCombo(combo) ? { t: 'key', combo } : undefined;
}

/** Asks the Mac to stream this region at full resolution over the overview layer. */
export function zoomEvent(displayId: number, region: ZoomRegion): ScreenInputEvent {
  return {
    t: 'zoom',
    displayId,
    region: {
      x: clamp01(region.x),
      y: clamp01(region.y),
      width: clamp01(region.width),
      height: clamp01(region.height),
    },
  };
}

/**
 * Asks the Mac to capture another display mid-session. The Mac answers with a fresh
 * `iceRestart` offer, and falls back to the main display if the one asked for is gone.
 */
export function displaySwitchEvent(displayId: number): ScreenInputEvent {
  return { t: 'display', displayId };
}

/** Input is never compressed: a pointer move must not wait on a compressor. */
export function encodeInputEvent(event: ScreenInputEvent): string {
  return JSON.stringify(event);
}

/**
 * A label for the session log and for any diagnostics. Keystrokes are never part of it —
 * the text of a `text` event and the target of a shortcut stay in memory only.
 */
export function describeInputEvent(event: ScreenInputEvent): string {
  switch (event.t) {
    case 'tap':
      return event.button === 'right' ? 'tap:right' : 'tap:left';
    case 'drag':
      return `drag:${event.phase}`;
    case 'scroll':
      return 'scroll';
    case 'text':
      // Length only. The characters themselves are never written anywhere.
      return `text:${event.text.length}`;
    case 'key':
      return 'key';
    case 'zoom':
      return 'zoom';
    case 'display':
      return `display:${event.displayId}`;
    default:
      return 'input';
  }
}
