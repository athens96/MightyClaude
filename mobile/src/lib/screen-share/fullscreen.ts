import type { Rect, Size } from '@/lib/screen-share/geometry';

/**
 * Full screen on the remote screen view: the header, the phone's status bar and Android's
 * navigation bar step aside and the picture takes the whole display. The way out is a small
 * translucent overlay — leave full screen, stop the session — that a tap or an edge swipe
 * brings back and that fades again on its own. Back leaves full screen before it leaves the
 * screen, and a session that is over hands the bars back.
 *
 * The rule under all of it: a touch meant for the phone must never reach the Mac as a click.
 */

/** How long the overlay stays after it was last brought up. */
export const OVERLAY_HIDE_MS = 3000;

/**
 * After the overlay fades its old place keeps catching taps this long, so a finger already
 * on its way to Stop or the way out brings the overlay back instead of clicking the Mac.
 */
export const OVERLAY_GRACE_MS = 700;

/** How far around the overlay's old place the grace catcher reaches. */
export const GRACE_PAD = 8;

/** Watching, a one-finger pan that starts this close to the display's edge is an edge swipe. */
export const EDGE_SWIPE_BAND = 24;

/**
 * Controlling, the band is much thinner: title bars, the menu bar and the Dock sit at the
 * picture's edges, and a drag that starts there has to reach the Mac.
 */
export const CONTROL_EDGE_BAND = 10;

/** How long touches wait for the stage to be re-measured after a full-screen switch. */
export const LAYOUT_PENDING_MAX_MS = 500;

export interface FullScreenState {
  on: boolean;
  /** The overlay with the way out (and Stop) is showing. */
  overlay: boolean;
  /**
   * Bumped every time the overlay is brought up, even when it already was, so the
   * auto-hide timer starts over from the latest tap or swipe.
   */
  revealed: number;
  /** The overlay just faded and its old place still only brings it back (`OVERLAY_GRACE_MS`). */
  grace: boolean;
}

export const FULL_SCREEN_OFF: FullScreenState = {
  on: false,
  overlay: false,
  revealed: 0,
  grace: false,
};

export type FullScreenEvent =
  /** The expand button. The overlay shows at once, so the way out is seen first. */
  | { type: 'enter' }
  /** The collapse button, back, or a session that is over. */
  | { type: 'exit' }
  /** A tap on a bar or an edge swipe: bring the overlay up (again). */
  | { type: 'reveal' }
  /** A tap while only watching: up if it was down, down if it was up. */
  | { type: 'toggleOverlay' }
  /** The auto-hide timer ran out. */
  | { type: 'hideOverlay' }
  /** The grace after a fade ran out. */
  | { type: 'graceOver' };

export function fullScreenReducer(state: FullScreenState, event: FullScreenEvent): FullScreenState {
  const shown = (): FullScreenState => ({
    on: true,
    overlay: true,
    revealed: state.revealed + 1,
    grace: false,
  });
  const faded = (): FullScreenState => ({ ...state, overlay: false, grace: true });
  switch (event.type) {
    case 'enter':
      return state.on ? state : shown();
    case 'exit':
      return state.on ? FULL_SCREEN_OFF : state;
    case 'reveal':
      return state.on ? shown() : state;
    case 'toggleOverlay':
      if (!state.on) return state;
      return state.overlay ? faded() : shown();
    case 'hideOverlay':
      return state.on && state.overlay ? faded() : state;
    case 'graceOver':
      return state.grace ? { ...state, grace: false } : state;
  }
}

/** Full screen is offered only while a picture is actually arriving. */
export function mayEnterFullScreen(input: { live: boolean; hasStream: boolean }): boolean {
  return input.live && input.hasStream;
}

/**
 * Full screen lasts as long as the session does: once it is over (stopped, refused, the
 * 30 s background rule, the Mac's kill switch) the bars come back and the panel with the
 * reason and the start buttons is in view again.
 */
export function shouldLeaveFullScreen(state: FullScreenState, sessionActive: boolean): boolean {
  return state.on && !sessionActive;
}

/**
 * Whether the overlay is drawn. It is pinned up while the picture is not live (a session
 * still connecting or reconnecting), so the way out and Stop never hide behind a black
 * screen that takes no taps.
 */
export function overlayVisible(state: FullScreenState, live: boolean): boolean {
  return state.on && (state.overlay || !live);
}

/** Whether the auto-hide timer should be running. */
export function overlayAutoHides(state: FullScreenState, live: boolean): boolean {
  return state.on && state.overlay && live;
}

/** Whether the overlay's old place is still catching taps after a fade. */
export function graceActive(state: FullScreenState, live: boolean): boolean {
  return state.on && state.grace && !overlayVisible(state, live);
}

/** The area the grace catcher covers: the overlay's last place, a little larger. */
export function graceArea(overlay: Rect, pad = GRACE_PAD): Rect {
  return {
    x: overlay.x - pad,
    y: overlay.y - pad,
    width: overlay.width + pad * 2,
    height: overlay.height + pad * 2,
  };
}

/** What Android's back button does: leave full screen first, the screen only after. */
export function backAction(state: FullScreenState): 'exitFullScreen' | 'leaveScreen' {
  return state.on ? 'exitFullScreen' : 'leaveScreen';
}

/** What the navigation header and the system bars should be in this state. */
export function systemChrome(state: FullScreenState): {
  headerShown: boolean;
  statusBarHidden: boolean;
  navigationBarHidden: boolean;
} {
  return {
    headerShown: !state.on,
    statusBarHidden: state.on,
    navigationBarHidden: state.on,
  };
}

function inside(point: { x: number; y: number }, rect: Rect): boolean {
  return (
    point.x >= rect.x &&
    point.x <= rect.x + rect.width &&
    point.y >= rect.y &&
    point.y <= rect.y + rect.height
  );
}

/** True when a point lies within the edge band of the stage (the whole display here). */
export function nearStageEdge(point: { x: number; y: number }, stage: Size, band = EDGE_SWIPE_BAND): boolean {
  return (
    point.x <= band ||
    point.y <= band ||
    point.x >= stage.width - band ||
    point.y >= stage.height - band
  );
}

/** True when the picture does not fill the stage: there are black bars to tap. */
export function hasLetterbox(stage: Size, content: Rect): boolean {
  return content.width < stage.width - 1 || content.height < stage.height - 1;
}

/** Which hint the overlay shows for bringing it back. */
export function fullScreenHint(input: {
  controlling: boolean;
  letterbox: boolean;
}): 'view' | 'controlBars' | 'controlEdge' {
  if (!input.controlling) return 'view';
  return input.letterbox ? 'controlBars' : 'controlEdge';
}

/**
 * Who a touch belongs to: the overlay, the Mac, or nobody.
 *
 * - Between a full-screen switch and the stage's next layout the picture's place is not
 *   known, so every touch is dropped (`ignore`) rather than mapped against the old frame.
 * - Right after the overlay fades, a touch on its old place only brings it back.
 * - Watching, a tap only ever toggles the overlay: there is nothing to click.
 * - Controlling, a tap on the picture is a click and stays one — the Mac's menu bar and
 *   Dock sit at the edges — while a tap on a letterbox bar brings up the overlay.
 * - A one-finger pan that starts on a bar, or in the edge band (thin while controlling), is
 *   an edge swipe and brings up the overlay; anywhere else it is the Mac's drag.
 *
 * Outside full screen every touch is the Mac's, as before.
 */
export function fullScreenTouch(input: {
  fullScreen: boolean;
  kind: 'tap' | 'pan';
  controlling: boolean;
  point: { x: number; y: number };
  stage: Size;
  content: Rect;
  layoutPending?: boolean;
  grace?: Rect | undefined;
}): 'toggleOverlay' | 'revealOverlay' | 'input' | 'ignore' {
  if (input.layoutPending) return 'ignore';
  if (!input.fullScreen) return 'input';
  if (input.grace && inside(input.point, input.grace)) return 'revealOverlay';
  const onPicture = inside(input.point, input.content);
  if (input.kind === 'tap') {
    if (!input.controlling) return 'toggleOverlay';
    return onPicture ? 'input' : 'revealOverlay';
  }
  const band = input.controlling ? CONTROL_EDGE_BAND : EDGE_SWIPE_BAND;
  return !onPicture || nearStageEdge(input.point, input.stage, band) ? 'revealOverlay' : 'input';
}
