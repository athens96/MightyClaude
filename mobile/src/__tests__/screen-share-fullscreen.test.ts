import {
  backAction,
  CONTROL_EDGE_BAND,
  EDGE_SWIPE_BAND,
  FULL_SCREEN_OFF,
  fullScreenHint,
  fullScreenReducer,
  fullScreenTouch,
  GRACE_PAD,
  graceActive,
  graceArea,
  hasLetterbox,
  mayEnterFullScreen,
  nearStageEdge,
  overlayAutoHides,
  overlayVisible,
  shouldLeaveFullScreen,
  systemChrome,
  type FullScreenEvent,
  type FullScreenState,
} from '@/lib/screen-share/fullscreen';
import { pictureRect } from '@/lib/screen-share/geometry';
import { FULL_REGION } from '@/lib/screen-share/zoom';

/**
 * Full screen hides the header, the status bar and Android's navigation bar; the way out is
 * a translucent overlay a tap or an edge swipe brings back, and back leaves full screen
 * before it leaves the screen.
 */

const run = (events: FullScreenEvent[], from: FullScreenState = FULL_SCREEN_OFF) =>
  events.reduce(fullScreenReducer, from);

describe('the full-screen state', () => {
  it('enters with the overlay up so the way out is seen first', () => {
    expect(run([{ type: 'enter' }])).toMatchObject({ on: true, overlay: true });
  });

  it('does nothing on a second enter', () => {
    const on = run([{ type: 'enter' }]);
    expect(fullScreenReducer(on, { type: 'enter' })).toBe(on);
  });

  it('lets the overlay fade, then brings it back on reveal', () => {
    expect(run([{ type: 'enter' }, { type: 'hideOverlay' }])).toMatchObject({ on: true, overlay: false });
    expect(run([{ type: 'enter' }, { type: 'hideOverlay' }, { type: 'reveal' }])).toMatchObject({
      on: true,
      overlay: true,
    });
  });

  it('starts the auto-hide over on every reveal, even with the overlay already up', () => {
    const shown = run([{ type: 'enter' }]);
    const again = fullScreenReducer(shown, { type: 'reveal' });
    expect(again.overlay).toBe(true);
    expect(again.revealed).toBeGreaterThan(shown.revealed);
    // Fading does not count as a reveal.
    expect(fullScreenReducer(again, { type: 'hideOverlay' }).revealed).toBe(again.revealed);
  });

  it('toggles the overlay on a tap while watching', () => {
    const shown = run([{ type: 'enter' }]);
    const hidden = fullScreenReducer(shown, { type: 'toggleOverlay' });
    expect(hidden.overlay).toBe(false);
    expect(fullScreenReducer(hidden, { type: 'toggleOverlay' }).overlay).toBe(true);
  });

  it('exits to the plain view, overlay and all', () => {
    expect(run([{ type: 'enter' }, { type: 'exit' }])).toEqual(FULL_SCREEN_OFF);
  });

  it('ignores overlay events outside full screen', () => {
    for (const type of ['reveal', 'toggleOverlay', 'hideOverlay', 'exit'] as const) {
      expect(fullScreenReducer(FULL_SCREEN_OFF, { type })).toBe(FULL_SCREEN_OFF);
    }
  });

  it('returns the same state when nothing changes, so React does not re-render', () => {
    const hidden = run([{ type: 'enter' }, { type: 'hideOverlay' }]);
    expect(fullScreenReducer(hidden, { type: 'hideOverlay' })).toBe(hidden);
  });
});

describe('when full screen is offered and when it ends', () => {
  it('is offered only while a live picture is arriving', () => {
    expect(mayEnterFullScreen({ live: true, hasStream: true })).toBe(true);
    expect(mayEnterFullScreen({ live: true, hasStream: false })).toBe(false);
    expect(mayEnterFullScreen({ live: false, hasStream: true })).toBe(false);
  });

  it('ends with the session (stop, 30 s background, kill switch), handing the bars back', () => {
    const on = run([{ type: 'enter' }]);
    expect(shouldLeaveFullScreen(on, false)).toBe(true);
    expect(shouldLeaveFullScreen(on, true)).toBe(false);
    expect(shouldLeaveFullScreen(FULL_SCREEN_OFF, false)).toBe(false);
    expect(systemChrome(fullScreenReducer(on, { type: 'exit' }))).toEqual({
      headerShown: true,
      statusBarHidden: false,
      navigationBarHidden: false,
    });
  });

  it('hides the header, the status bar and the navigation bar while on', () => {
    expect(systemChrome(run([{ type: 'enter' }]))).toEqual({
      headerShown: false,
      statusBarHidden: true,
      navigationBarHidden: true,
    });
    expect(systemChrome(FULL_SCREEN_OFF)).toEqual({
      headerShown: true,
      statusBarHidden: false,
      navigationBarHidden: false,
    });
  });
});

describe('the overlay with the way out and Stop', () => {
  const shown = run([{ type: 'enter' }]);
  const faded = fullScreenReducer(shown, { type: 'hideOverlay' });

  it('shows when up and hides when faded, while live', () => {
    expect(overlayVisible(shown, true)).toBe(true);
    expect(overlayVisible(faded, true)).toBe(false);
  });

  it('is pinned up while the picture is not live, so Stop never hides behind black', () => {
    expect(overlayVisible(faded, false)).toBe(true);
    expect(overlayAutoHides(shown, false)).toBe(false);
  });

  it('auto-hides only while up, live and in full screen', () => {
    expect(overlayAutoHides(shown, true)).toBe(true);
    expect(overlayAutoHides(faded, true)).toBe(false);
    expect(overlayAutoHides(FULL_SCREEN_OFF, true)).toBe(false);
  });

  it('never shows outside full screen', () => {
    expect(overlayVisible(FULL_SCREEN_OFF, true)).toBe(false);
    expect(overlayVisible(FULL_SCREEN_OFF, false)).toBe(false);
  });
});

describe('Android back', () => {
  it('leaves full screen first, overlay up or not', () => {
    const shown = run([{ type: 'enter' }]);
    expect(backAction(shown)).toBe('exitFullScreen');
    expect(backAction(fullScreenReducer(shown, { type: 'hideOverlay' }))).toBe('exitFullScreen');
  });

  it('leaves the screen once full screen is off', () => {
    expect(backAction(FULL_SCREEN_OFF)).toBe('leaveScreen');
    expect(backAction(run([{ type: 'enter' }, { type: 'exit' }]))).toBe('leaveScreen');
  });
});

describe('who a touch belongs to in full screen', () => {
  // A 16:10 Mac on a 914×411 dp display: bars left and right.
  const stage = { width: 914, height: 411 };
  const content = pictureRect({ stage, video: { width: 1280, height: 800 }, region: FULL_REGION });
  const middle = { x: stage.width / 2, y: stage.height / 2 };
  const onBar = { x: content.x / 2, y: stage.height / 2 };
  const pictureTopEdge = { x: stage.width / 2, y: 2 };

  it('is always the Mac’s outside full screen', () => {
    for (const kind of ['tap', 'pan'] as const) {
      for (const point of [middle, onBar, pictureTopEdge]) {
        expect(
          fullScreenTouch({ fullScreen: false, kind, controlling: true, point, stage, content }),
        ).toBe('input');
      }
    }
  });

  it('toggles the overlay on any tap while watching', () => {
    for (const point of [middle, onBar, pictureTopEdge]) {
      expect(
        fullScreenTouch({ fullScreen: true, kind: 'tap', controlling: false, point, stage, content }),
      ).toBe('toggleOverlay');
    }
  });

  it('keeps a tap on the picture a click while controlling, even at the menu bar', () => {
    expect(
      fullScreenTouch({ fullScreen: true, kind: 'tap', controlling: true, point: middle, stage, content }),
    ).toBe('input');
    expect(
      fullScreenTouch({
        fullScreen: true,
        kind: 'tap',
        controlling: true,
        point: pictureTopEdge,
        stage,
        content,
      }),
    ).toBe('input');
  });

  it('brings the overlay up on a tap on a letterbox bar while controlling', () => {
    expect(
      fullScreenTouch({ fullScreen: true, kind: 'tap', controlling: true, point: onBar, stage, content }),
    ).toBe('revealOverlay');
  });

  it('treats a pan from the edge band as an edge swipe, and any other pan as the Mac’s', () => {
    for (const controlling of [true, false]) {
      for (const point of [
        { x: 1, y: 200 },
        { x: stage.width - 1, y: 200 },
        { x: 400, y: 1 },
        { x: 400, y: stage.height - 1 },
      ]) {
        expect(
          fullScreenTouch({ fullScreen: true, kind: 'pan', controlling, point, stage, content }),
        ).toBe('revealOverlay');
      }
      expect(
        fullScreenTouch({ fullScreen: true, kind: 'pan', controlling, point: middle, stage, content }),
      ).toBe('input');
    }
  });

  it('measures the edge band against the stage', () => {
    expect(nearStageEdge({ x: EDGE_SWIPE_BAND, y: 200 }, stage)).toBe(true);
    expect(nearStageEdge({ x: EDGE_SWIPE_BAND + 1, y: 200 }, stage)).toBe(false);
    expect(nearStageEdge({ x: 400, y: stage.height - EDGE_SWIPE_BAND }, stage)).toBe(true);
  });
});

describe('the grace after the overlay fades', () => {
  const stage = { width: 914, height: 411 };
  const content = pictureRect({ stage, video: { width: 1280, height: 800 }, region: FULL_REGION });
  // The overlay sat top right, over the picture's top-right corner.
  const overlay = { x: 600, y: 16, width: 140, height: 60 };
  const faded = run([{ type: 'enter' }, { type: 'hideOverlay' }]);

  it('starts when the overlay fades and ends on its own timer or a reveal', () => {
    expect(faded.grace).toBe(true);
    expect(graceActive(faded, true)).toBe(true);
    expect(fullScreenReducer(faded, { type: 'graceOver' }).grace).toBe(false);
    expect(fullScreenReducer(faded, { type: 'reveal' }).grace).toBe(false);
    expect(fullScreenReducer(faded, { type: 'exit' }).grace).toBe(false);
  });

  it('is not needed while the overlay is up or pinned up', () => {
    expect(graceActive(run([{ type: 'enter' }]), true)).toBe(false);
    expect(graceActive(faded, false)).toBe(false);
    expect(graceActive(FULL_SCREEN_OFF, true)).toBe(false);
  });

  it('covers the overlay’s old place and a little around it', () => {
    expect(graceArea(overlay)).toEqual({
      x: overlay.x - GRACE_PAD,
      y: overlay.y - GRACE_PAD,
      width: overlay.width + GRACE_PAD * 2,
      height: overlay.height + GRACE_PAD * 2,
    });
  });

  it('turns a controlling tap on the old Stop button into a reveal, never a click', () => {
    const onStop = { x: 700, y: 40 };
    // On the picture: without grace this would be a click on the Mac.
    expect(
      fullScreenTouch({ fullScreen: true, kind: 'tap', controlling: true, point: onStop, stage, content }),
    ).toBe('input');
    expect(
      fullScreenTouch({
        fullScreen: true,
        kind: 'tap',
        controlling: true,
        point: onStop,
        stage,
        content,
        grace: graceArea(overlay),
      }),
    ).toBe('revealOverlay');
    // Just inside the padding still counts; well outside it is a click again.
    const pad = { x: overlay.x - GRACE_PAD + 1, y: 40 };
    expect(
      fullScreenTouch({
        fullScreen: true,
        kind: 'tap',
        controlling: true,
        point: pad,
        stage,
        content,
        grace: graceArea(overlay),
      }),
    ).toBe('revealOverlay');
    expect(
      fullScreenTouch({
        fullScreen: true,
        kind: 'tap',
        controlling: true,
        point: { x: 457, y: 205 },
        stage,
        content,
        grace: graceArea(overlay),
      }),
    ).toBe('input');
  });

  it('turns a drag from the old overlay place into a reveal too', () => {
    expect(
      fullScreenTouch({
        fullScreen: true,
        kind: 'pan',
        controlling: true,
        point: { x: 700, y: 40 },
        stage,
        content,
        grace: graceArea(overlay),
      }),
    ).toBe('revealOverlay');
  });
});

describe('the control-mode edge band', () => {
  const stage = { width: 914, height: 411 };
  const letterboxed = pictureRect({ stage, video: { width: 1280, height: 800 }, region: FULL_REGION });
  // A picture that fills the display: no bars at all.
  const filled = { x: 0, y: 0, width: 914, height: 411 };
  const pan = (point: { x: number; y: number }, content = letterboxed) =>
    fullScreenTouch({ fullScreen: true, kind: 'pan', controlling: true, point, stage, content });

  it('gives a drag from a title bar or the Dock to the Mac', () => {
    // 20 dp down from the top: a window's title bar, inside the old 24 dp band.
    expect(pan({ x: 457, y: 20 }, filled)).toBe('input');
    // The Dock along the bottom.
    expect(pan({ x: 457, y: 411 - 20 }, filled)).toBe('input');
    expect(pan({ x: 457, y: CONTROL_EDGE_BAND + 1 }, filled)).toBe('input');
  });

  it('takes only a pan from a letterbox bar or the thin outer band as an edge swipe', () => {
    expect(pan({ x: letterboxed.x / 2, y: 200 })).toBe('revealOverlay');
    expect(pan({ x: 457, y: CONTROL_EDGE_BAND }, filled)).toBe('revealOverlay');
    expect(pan({ x: 914 - 2, y: 200 }, filled)).toBe('revealOverlay');
  });

  it('keeps the wider band while only watching', () => {
    expect(
      fullScreenTouch({
        fullScreen: true,
        kind: 'pan',
        controlling: false,
        point: { x: 457, y: EDGE_SWIPE_BAND - 2 },
        stage,
        content: filled,
      }),
    ).toBe('revealOverlay');
  });
});

describe('touches while the stage is being re-measured', () => {
  const stage = { width: 914, height: 411 };
  const content = { x: 0, y: 0, width: 914, height: 411 };

  it('are dropped, in or out of full screen, so nothing is mapped against the old frame', () => {
    for (const fullScreen of [true, false]) {
      for (const kind of ['tap', 'pan'] as const) {
        expect(
          fullScreenTouch({
            fullScreen,
            kind,
            controlling: true,
            point: { x: 457, y: 205 },
            stage,
            content,
            layoutPending: true,
          }),
        ).toBe('ignore');
      }
    }
  });
});

describe('the hint for bringing the overlay back', () => {
  const stage = { width: 914, height: 411 };

  it('knows when there are bars to tap', () => {
    expect(hasLetterbox(stage, pictureRect({ stage, video: { width: 1280, height: 800 } }))).toBe(true);
    expect(hasLetterbox(stage, { x: 0, y: 0, width: 914, height: 411 })).toBe(false);
  });

  it('points at the bars only when there are some', () => {
    expect(fullScreenHint({ controlling: false, letterbox: true })).toBe('view');
    expect(fullScreenHint({ controlling: true, letterbox: true })).toBe('controlBars');
    expect(fullScreenHint({ controlling: true, letterbox: false })).toBe('controlEdge');
  });
});
