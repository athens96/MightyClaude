// A pane tab's move, on pointer events. HTML5 drag-and-drop runs as an OS (OLE) drag, and in the WinUI window's WebView2
// (composition hosting) that drag finds no drop target, so every spot shows "no drop" and nothing lands. The press is the
// page's own instead: past the slack it follows the pointer, the group or tab under it is the target, the release docks it
// (DockSession), and Esc, a cancelled or lost pointer, a released button or a release on no target cancels (docs/pane-layout.md).
import { dragSlack, dropZone, type DropEdge } from './geometry';

export interface Box { left: number; top: number; width: number; height: number }

/** What lies under the pointer: a tab's handle, the rest of a group's strip, the rest of a group, or nothing a tab can land on. */
export type TabDragHit =
  | { kind: 'tab'; workspaceId: string; groupId: string; tabId: string; index: number; box: Box }
  | { kind: 'strip'; workspaceId: string; groupId: string }
  | { kind: 'group'; workspaceId: string; groupId: string; box: Box }
  | null;

/** Where the tab would land: a group's edge (a split) or its middle (a merge), with a strip index when over a tab. */
export interface TabDragTarget {
  groupId: string;
  edge: DropEdge;
  index?: number;
  /** The tab whose side shows the landing mark. */
  tabId?: string;
  side?: 'left' | 'right';
}

/** The target of a hit for a tab of this workspace: over a tab its half picks the strip index, elsewhere in a strip it joins
 *  the end, over the rest of a group its zone picks the edge. */
export function resolveTabDrop(hit: TabDragHit, x: number, y: number, workspaceId: string): TabDragTarget | null {
  if (!hit || hit.workspaceId !== workspaceId) return null;
  if (hit.kind === 'strip') return { groupId: hit.groupId, edge: 'center' };
  if (hit.kind === 'tab') {
    const side = x - hit.box.left > hit.box.width / 2 ? 'right' : 'left';
    return { groupId: hit.groupId, edge: 'center', index: hit.index + (side === 'right' ? 1 : 0), tabId: hit.tabId, side };
  }
  return { groupId: hit.groupId, edge: dropZone(x - hit.box.left, y - hit.box.top, hit.box.width, hit.box.height) };
}

/** The host call's arguments for a landing (MainWindow.WebShell.Shell.cs dockSession). */
export function dockArguments(sessionId: string, workspaceId: string, target: TabDragTarget) {
  return { sessionId, workspaceId, groupId: target.groupId, edge: target.edge, ...(target.index !== undefined ? { index: target.index } : {}) };
}

/** How near a strip's end the pointer scrolls an overflowing strip, and how far one move scrolls it. */
export const stripScrollEdge = 16;
export const stripScrollStep = 8;

/** The scroll one move over a strip from `left` to `right` asks for: back near its start, on near its end, else none. */
export function stripScroll(x: number, left: number, right: number): number {
  if (right - left <= 2 * stripScrollEdge) return 0;
  if (x < left + stripScrollEdge) return -stripScrollStep;
  if (x > right - stripScrollEdge) return stripScrollStep;
  return 0;
}

export interface TabPress {
  phase: 'pressed' | 'dragging';
  startX: number;
  startY: number;
  x: number;
  y: number;
  target: TabDragTarget | null;
}

export type TabPressEvent =
  /** `buttons` is the pointer's held buttons; without the primary one the release was lost (outside the page). */
  | { type: 'move'; x: number; y: number; buttons: number; target: TabDragTarget | null }
  /** The target under the release point itself, not the last move's. */
  | { type: 'up'; target: TabDragTarget | null }
  | { type: 'cancel' };

/** What a step leaves: the press still going, or its end (a click, a landing, or nothing). */
export type TabPressStep =
  | { press: TabPress; end?: undefined }
  | { press: null; end: 'click' | 'cancel' }
  | { press: null; end: 'drop'; target: TabDragTarget };

export function startTabPress(x: number, y: number): TabPress {
  return { phase: 'pressed', startX: x, startY: y, x, y, target: null };
}

/** One step of a press: within the slack it is still a click; past it a drag that lands on release over a target. */
export function stepTabPress(press: TabPress, event: TabPressEvent): TabPressStep {
  if (event.type === 'cancel') return { press: null, end: 'cancel' };
  if (event.type === 'up') {
    if (press.phase === 'pressed') return { press: null, end: 'click' };
    return event.target ? { press: null, end: 'drop', target: event.target } : { press: null, end: 'cancel' };
  }
  if ((event.buttons & 1) === 0) return { press: null, end: 'cancel' };
  const passed = press.phase === 'dragging' || Math.max(Math.abs(event.x - press.startX), Math.abs(event.y - press.startY)) >= dragSlack;
  if (!passed) return { press: { ...press, x: event.x, y: event.y } };
  return { press: { ...press, phase: 'dragging', x: event.x, y: event.y, target: event.target } };
}
