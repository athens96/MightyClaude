// The NativeSlot's reports (docs/tauri-architecture.md §3): where the slot is in device pixels, whether it can show, and whether
// something of the page covers it. Pure, so the rules are tested without a DOM; NativeSlot.tsx feeds them from the element.
import { slotBounds } from '../panes/browser/types';
import { overlapsAny, type Box } from '../shell/overlay';
import type { StoreState } from '../store/store';
import type { SurfaceKind } from './host';

/** A rectangle of the page in device pixels (CSS pixels × devicePixelRatio, rounded outwards to whole pixels). */
export interface SlotRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** What `surfaceBounds` says about one slot. The host shows its view at `rect` only while `visible && !occluded`. */
export interface SlotReport {
  rect: SlotRect;
  /** The pane is shown, the slot is in the document and not empty. */
  visible: boolean;
  /** A dialog, a host view the page draws, or a page menu, popover or in-page dialog over the slot: the native view must step aside. */
  occluded: boolean;
}

/** page → host: `surfaceBounds{paneId, surface, rect, visible, occluded}`. */
export interface SurfaceBoundsArgs extends SlotReport {
  paneId: string;
  surface: SurfaceKind;
}

/** page → host: `surfaceAction{paneId, surface, action}`; `snapshot` answers a PNG `data:` URL or null. */
export interface SurfaceActionArgs {
  paneId: string;
  surface: SurfaceKind;
  action: 'focus' | 'snapshot';
}

/**
 * Whether the page covers a slot: the rule the browser pane applies to its slot (src/panes/browser/BrowserView.tsx), for every
 * native view. A dialog of the host's flows, a host view the page draws (Settings, sheets) or the frame's own dialog covers every
 * slot; a page menu, popover or in-page dialog (src/shell/overlay.ts) covers the slots it overlaps.
 */
export function pageCovers(state: Pick<StoreState, 'dialogs' | 'natives' | 'shell' | 'overlays'>, slot: Box, overlays: () => readonly Box[]): boolean {
  if (state.dialogs.length > 0 || state.natives.length > 0 || (state.shell?.dialogOpen ?? false)) return true;
  return state.overlays > 0 && overlapsAny(slot, overlays());
}

/** The report for a slot's box (CSS pixels) at `ratio` device pixels per CSS pixel. */
export function slotReport(box: { left: number; top: number; width: number; height: number }, ratio: number, shown: boolean, covered: boolean): SlotReport {
  const { x, y, width, height, visible } = slotBounds(box, ratio, shown);
  return { rect: { x, y, width, height }, visible, occluded: covered };
}

/** A slot that is gone (the pane closed or moved away): the view hides. */
export const goneReport: SlotReport = { rect: { x: 0, y: 0, width: 0, height: 0 }, visible: false, occluded: false };

export const shows = (report: SlotReport | undefined): boolean => report !== undefined && report.visible && !report.occluded;

export function sameReport(a: SlotReport | undefined, b: SlotReport): boolean {
  return a !== undefined && a.visible === b.visible && a.occluded === b.occluded
    && a.rect.x === b.rect.x && a.rect.y === b.rect.y && a.rect.width === b.rect.width && a.rect.height === b.rect.height;
}

/** A view that was showing and is now covered while still on screen: its picture is taken first, so the slot can show it meanwhile. */
export function wantsSnapshot(previous: SlotReport | undefined, next: SlotReport): boolean {
  return shows(previous) && next.visible && next.occluded;
}

/**
 * One slot's reports to the host: only when something changed, the picture asked for before the view steps aside under the page
 * (the host handles calls in order, so it takes the picture while the view still shows), and a last report hiding the view when
 * the slot goes. `picture` shows a picture in the slot (undefined takes it away).
 */
export class SlotReporter {
  private last: SlotReport | undefined;

  constructor(
    private readonly paneId: string,
    private readonly surface: SurfaceKind,
    private readonly call: (method: string, args: SurfaceBoundsArgs | SurfaceActionArgs) => Promise<unknown>,
    private readonly picture: (url: string | undefined) => void,
  ) {}

  update(next: SlotReport): void {
    if (sameReport(this.last, next)) return;
    if (wantsSnapshot(this.last, next)) {
      void this.call('surfaceAction', { paneId: this.paneId, surface: this.surface, action: 'snapshot' }).then(
        // Only while the slot is still covered: a view that is back on top needs no picture.
        (url) => { if (typeof url === 'string' && url.startsWith('data:image/') && this.last === next) this.picture(url); },
        () => {},
      );
    }
    if (!(next.visible && next.occluded)) this.picture(undefined);
    this.last = next;
    void this.call('surfaceBounds', { paneId: this.paneId, surface: this.surface, ...next }).catch(() => {});
  }

  /** The slot is gone: the view hides (it closes when its pane leaves the state). */
  release(): void {
    this.last = undefined;
    this.picture(undefined);
    void this.call('surfaceBounds', { paneId: this.paneId, surface: this.surface, ...goneReport }).catch(() => {});
  }
}
