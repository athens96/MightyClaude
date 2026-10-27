// The diagram's camera, follow, zoom/wheel, dot grid and activity outline rules, ported from Core/MightyGraphCamera.cs,
// MightyGraphFollow.cs, MightyGraphViewModel.cs, MightyGraphDotGrid.cs and MightyGraphActivity.cs. Pure.
// screen = node × zoom + camera.
import type { MightyGraphRun } from '../store/models';
import { CentreX, HistoryNodeID, ExecutionSuffix, ImagesSuffix, PlanRecordSuffix, PendingNodeID, WorkKind, maxY, nodeId, type Layout, type Rect, type Size } from './layout';
import { ease } from '../theme/tokens';

export interface Point { x: number; y: number }

// ── MightyGraphCamera ──
export const isAuxiliaryId = (id: string) =>
  id === PendingNodeID || id === HistoryNodeID || id.endsWith(':result-files') || id.includes(':' + ExecutionSuffix) || id.includes(':' + ImagesSuffix) || id.includes(':' + PlanRecordSuffix);

export const showsTop = (cameraY: number, zoom: number, top: number, slack = 8) =>
  Number.isFinite(zoom) && zoom > 0 && Number.isFinite(cameraY) && Number.isFinite(top) && -cameraY / zoom <= top + slack;

export function cameraOffset(frame: Rect, viewport: Size, zoom: number, alignTop: boolean, alignBottom = false): Point {
  if (alignBottom) return revealOffset(frame, viewport, zoom);
  return {
    x: (viewport.w - frame.w * zoom) / 2 - frame.x * zoom,
    y: (alignTop ? 16 : Math.max(16, (viewport.h - frame.h * zoom) / 2)) - frame.y * zoom,
  };
}

export function revealOffset(frame: Rect, viewport: Size, zoom: number): Point {
  const top = cameraOffset(frame, viewport, zoom, true);
  return { x: top.x, y: Math.max(top.y, viewport.h - 16 - maxY(frame) * zoom) };
}

export type Anchor = { kind: 'hold' } | { kind: 'reaim'; nodeID: string; alignTop: boolean };
const hold: Anchor = { kind: 'hold' };
const reaim = (nodeID: string, alignTop: boolean): Anchor => ({ kind: 'reaim', nodeID, alignTop });

export function reaimAnchor(newestRunID: string | null, selectedNodeID: string | null, layoutNodeIDs: ReadonlySet<string>): Anchor {
  if (selectedNodeID !== null && !isAuxiliaryId(selectedNodeID) && layoutNodeIDs.has(selectedNodeID)) return reaim(selectedNodeID, false);
  if (newestRunID === null) return layoutNodeIDs.has(PendingNodeID) ? reaim(PendingNodeID, true) : hold;
  const newest = nodeId(newestRunID, 'request');
  return layoutNodeIDs.has(newest) ? reaim(newest, true) : hold;
}

export function trimAnchor(previousRunIDs: readonly string[], runIDs: readonly string[], selectedNodeID: string | null, layoutNodeIDs: ReadonlySet<string>): Anchor {
  if (runIDs.length === 0) return previousRunIDs.length === 0 ? hold : reaimAnchor(null, selectedNodeID, layoutNodeIDs);
  const last = runIDs[runIDs.length - 1]!;
  const previousSet = new Set(previousRunIDs);
  const sameSet = previousSet.size === new Set(runIDs).size && runIDs.every((id) => previousSet.has(id));
  if (last !== (previousRunIDs[previousRunIDs.length - 1] ?? null) || sameSet) return hold;
  if (runIDs.length > previousRunIDs.length && runIDs.slice(runIDs.length - previousRunIDs.length).every((id, i) => id === previousRunIDs[i])) return hold;
  return reaimAnchor(last, selectedNodeID, layoutNodeIDs);
}

export function resizeAnchor(fittedResultID: string | null, targetID: string | null, targetAlignTop: boolean, frames: Readonly<Record<string, Rect>>): Anchor {
  const fitted = fittedResultID !== null ? frames[fittedResultID] : undefined;
  if (fittedResultID === null || !fitted) return hold;
  const target = targetID !== null ? frames[targetID] : undefined;
  if (targetID !== null && targetID !== fittedResultID && target && target.y > fitted.y) return reaim(targetID, targetAlignTop);
  return reaim(fittedResultID, true);
}

export function lostFrameIndex(previousFrames: readonly Rect[], currentFrames: readonly Rect[], camera: Point, viewport: Size, zoom: number): number | null {
  if (!Number.isFinite(zoom) || zoom <= 0 || !Number.isFinite(camera.x) || !Number.isFinite(camera.y) || viewport.w <= 0 || viewport.h <= 0
    || !Number.isFinite(viewport.w) || !Number.isFinite(viewport.h) || currentFrames.length === 0) return null;
  const vx = -camera.x / zoom, vy = -camera.y / zoom, vw = viewport.w / zoom, vh = viewport.h / zoom;
  const shown = (f: Rect, atLeast: number): number | null => {
    const ix = Math.max(f.x, vx), iy = Math.max(f.y, vy);
    const iw = Math.min(f.x + f.w, vx + vw) - ix;
    const ih = Math.min(f.y + f.h, vy + vh) - iy;
    if (iw <= 0 || ih <= 0) return null;
    const least = atLeast / zoom;
    if (iw < Math.min(least, f.w) || ih < Math.min(least, f.h)) return null;
    return iw * ih;
  };
  let best: number | null = null, bestArea = -1;
  previousFrames.forEach((f, i) => { const a = shown(f, 24); if (a !== null && a > bestArea) { best = i; bestArea = a; } });
  if (best === null) return null;
  return currentFrames.some((f) => shown(f, 4) !== null) ? null : best;
}

export const trimToken = (sequence: number, id: string) => 'trim:' + sequence + ':' + id;

export function admittedCamera(targetToken: string | null, consumedToken: string | null, targetCamera: Point | null, current: Point, requested: Point): Point | null {
  if (targetToken === null || targetToken === consumedToken) return requested;
  if (!targetCamera) return null;
  return { x: targetCamera.x + requested.x - current.x, y: targetCamera.y + requested.y - current.y };
}

/**
 * The camera `progress` (0–1 of motion.scroll) of the way from `from` to `to` along the standard easing: an own send moves the
 * diagram to its request this way (natural submit B2); under Reduce Motion the caller sets `to` at once.
 */
export function cameraTween(from: Point, to: Point, progress: number): Point {
  const p = ease(Math.min(1, Math.max(0, progress)));
  return { x: from.x + (to.x - from.x) * p, y: from.y + (to.y - from.y) * p };
}

// ── MightyGraphFollow ──
export const FollowMargin = 16;
export const FollowSlack = 36;

export function followTargetID(layout: Layout, runs: readonly MightyGraphRun[]): string | null {
  const last = runs[runs.length - 1];
  if (!last) return layout.nodes.find((n) => n.kind === 'draft')?.id ?? null;
  const newest = nodeId(last.id, '');
  let best: Layout['nodes'][number] | null = null;
  for (const node of layout.nodes) {
    const flow = node.kind === 'request' || node.kind === WorkKind || node.kind === 'result' || node.kind === 'plan' || node.kind === 'agent';
    if (flow && node.id.startsWith(newest) && maxY(node.frame) >= (best ? maxY(best.frame) : -Infinity)) best = node;
  }
  return best?.id ?? null;
}

const valid = (camera: Point, viewport: Size, zoom: number) =>
  Number.isFinite(zoom) && zoom > 0 && Number.isFinite(camera.x) && Number.isFinite(camera.y) && viewport.w > 0 && viewport.h > 0 && Number.isFinite(viewport.w) && Number.isFinite(viewport.h);

export function isAtNewest(target: Rect, camera: Point, viewport: Size, zoom: number): boolean {
  if (!valid(camera, viewport, zoom)) return false;
  const bottom = camera.y + maxY(target) * zoom;
  return bottom >= 0 && bottom <= viewport.h - FollowMargin + FollowSlack;
}

export const centredX = (viewport: Size, zoom: number) => viewport.w / 2 - CentreX * zoom;
export const followCamera = (target: Rect, viewport: Size, zoom: number, x: number, gap = FollowMargin): Point => ({ x, y: viewport.h - gap - maxY(target) * zoom });

export function followOffset(frame: Rect, viewport: Size, zoom: number, alignTop: boolean, alignBottom: boolean, follows: boolean): Point {
  return follows ? followCamera(frame, viewport, zoom, centredX(viewport, zoom)) : cameraOffset(frame, viewport, zoom, alignTop, alignBottom);
}

export type RunChange = 'none' | 'first' | 'follow' | 'trim';

export function runsChanged(previous: readonly string[] | null, current: readonly string[], following: boolean): RunChange {
  if (previous === null) return 'first';
  if (previous.length === current.length && previous.every((id, i) => id === current[i])) return 'none';
  if (current.length > 0 && (previous[previous.length - 1] ?? null) !== current[current.length - 1]) return 'follow';
  return following ? 'none' : 'trim';
}

/** The diagram camera's follow state between layout passes (MightyGraphFollow.Watch). */
export class FollowWatch {
  following = true;
  private targetID: string | null = null;
  private gap: number | null = null;
  private judging = false;

  start(): void { this.following = true; this.targetID = null; this.gap = null; this.judging = false; }
  stop(): void { this.following = false; this.targetID = null; this.gap = null; this.judging = false; }
  userChangedLayout(): void { if (this.following) this.judging = true; }

  moved(targetID: string | null, target: Rect | null, camera: Point, viewport: Size, zoom: number): void {
    if (targetID === null || !target || !isAtNewest(target, camera, viewport, zoom)) { this.stop(); return; }
    this.following = true;
    this.targetID = targetID;
    this.gap = viewport.h - (camera.y + maxY(target) * zoom);
  }

  observe(targetID: string | null, target: Rect | null, camera: Point, viewport: Size, zoom: number): Point | null {
    if (!this.following || targetID === null || !target || !valid(camera, viewport, zoom)) return null;
    if (this.judging) {
      this.judging = false;
      this.moved(targetID, target, camera, viewport, zoom);
      return null;
    }
    const kept = targetID === this.targetID ? this.gap ?? FollowMargin : FollowMargin;
    this.targetID = targetID;
    this.gap = kept;
    const next = followCamera(target, viewport, zoom, camera.x, kept);
    return Math.abs(next.y - camera.y) > 0.5 ? next : null;
  }
}

// ── zoom and wheel (MightyGraphViewModel) ──
export const ZoomMin = 0.5;
export const ZoomMax = 1.5;
export const ZoomStep = 0.1;
export const ZoomDefault = 1;
const round1 = (v: number) => Math.round(v * 10) / 10;
export const zoomIn = (current: number) => Math.min(ZoomMax, round1(current + ZoomStep));
export const zoomOut = (current: number) => Math.max(ZoomMin, round1(current - ZoomStep));
export const zoomLabel = (zoom: number) => Math.round(zoom * 100) + '%';

export function zoomAnchored(pan: Point, viewport: Size, previous: number, zoom: number): Point {
  if (!Number.isFinite(previous) || previous <= 0 || !Number.isFinite(zoom) || zoom <= 0) return pan;
  const cx = viewport.w / 2, cy = viewport.h / 2;
  return { x: cx - ((cx - pan.x) * zoom) / previous, y: cy - ((cy - pan.y) * zoom) / previous };
}

export const wheelScrollTarget = (selected: string | null, hovered: string | null, hoveredIsAuxiliary: boolean) =>
  hovered !== null && (hovered === selected || hoveredIsAuxiliary) ? hovered : null;

/** How far one wheel event pans: a tilt pans −delta x, Shift with the wheel +delta x, else +delta y (WinUI wheel delta sign). */
export function wheelPan(delta: number, horizontalWheel: boolean, shift: boolean): Point {
  if (horizontalWheel) return { x: -delta, y: 0 };
  if (shift) return { x: delta, y: 0 };
  return { x: 0, y: delta };
}

// ── dot grid ──
export const DotSpacing = 18;
export const dotStep = (zoom: number) => DotSpacing * zoom;
export const dotsShown = (zoom: number) => dotStep(zoom) >= 6;
export const dotRadius = (zoom: number) => Math.max(0.6, zoom);
export function dotFirst(offset: number, step: number): number {
  if (!(step > 0) || !Number.isFinite(offset)) return 0;
  const first = offset % step;
  return first < 0 ? first + step : first;
}

// ── activity outline ──
export type Outline = 'marching' | 'solid' | 'waiting' | 'none';
export const Dash = [9, 7] as const;
export const ActivityPeriodMs = 1600;
export const OutlineWidth = 2;
export const CornerRadius = 12;

export function outlineOf(blockStatus: string, animations: boolean): Outline {
  if (blockStatus === 'waiting') return 'waiting';
  if (['completed', 'error', 'failed', 'stopped', 'cancelled', 'interrupted'].includes(blockStatus)) return 'none';
  return animations ? 'marching' : 'solid';
}

export function activityPhase(elapsedMs: number, animations: boolean): number {
  if (!animations) return 0;
  const cycle = elapsedMs % ActivityPeriodMs;
  return (cycle < 0 ? cycle + ActivityPeriodMs : cycle) / ActivityPeriodMs;
}

export function perimeter(width: number, height: number, cornerRadius: number): number {
  const r = Math.max(0, Math.min(cornerRadius, Math.min(width / 2, height / 2)));
  return 2 * (width + height) - 8 * r + 2 * Math.PI * r;
}

/** C# MidpointRounding.AwayFromZero. */
const roundAway = (v: number) => Math.sign(v) * Math.round(Math.abs(v));

export function dashFitting(length: number): number[] {
  const period = Dash[0] + Dash[1];
  if (length <= 0) return [...Dash];
  const fitted = length / Math.max(1, roundAway(length / period));
  return Dash.map((d) => (d * fitted) / period);
}

export const dashOffset = (phase: number, dash: readonly number[]) => -phase * dash.reduce((s, d) => s + d, 0);

export const dashForCard = (width: number, height: number, cornerRadius = CornerRadius) =>
  dashFitting(perimeter(Math.max(0, width - OutlineWidth), Math.max(0, height - OutlineWidth), cornerRadius - 1));

export const barHeight = (index: number, phase: number) => 4 + (8 * (Math.sin((phase + index / 5) * 2 * Math.PI) + 1)) / 2;
