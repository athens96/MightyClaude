import {
  CELLULAR_CEILING,
  CELLULAR_MAX_FPS,
  CELLULAR_MIN_FPS,
  RELAY_QUOTA_KBPS,
  WIFI_CEILING,
  capForCandidate,
  decodableCodecs,
  networkFor,
  preferredCodec,
  qualityCeiling,
  requestedQuality,
} from '@/lib/screen-share/quality';
import {
  FULL_REGION,
  MAX_ZOOM_SCALE,
  MIN_ZOOM_SCALE,
  clampZoomScale,
  isZoomed,
  overviewSize,
  pointInRegion,
  zoomRegionFor,
} from '@/lib/screen-share/zoom';
import {
  MAX_INPUT_TEXT_BYTES,
  describeInputEvent,
  displaySwitchEvent,
  dragEvent,
  encodeInputEvent,
  mayInject,
  rightClickEvent,
  scrollEvent,
  isKeyCombo,
  shortcutEvent,
  tapEvent,
  textEvent,
  zoomEvent,
} from '@/lib/screen-share/input';

/**
 * What the phone asks for and what it sends: a quality ceiling per network, the codecs it
 * can actually decode, a zoom that streams a region rather than magnifying a shrunk frame,
 * and input as `displayId` plus normalized 0–1 coordinates.
 */

describe('the quality ceiling the phone asks for', () => {
  it('is 1080p30 at about 6 Mbps on Wi-Fi', () => {
    expect(qualityCeiling('wifi')).toEqual({
      width: 1920,
      height: 1080,
      fps: 30,
      maxBitrateKbps: 6000,
    });
    expect(WIFI_CEILING.maxBitrateKbps).toBe(6000);
  });

  it('is 720p15 at about 1 Mbps on mobile data, inside a 5–15 fps band', () => {
    expect(qualityCeiling('cellular')).toEqual({
      width: 1280,
      height: 720,
      fps: 15,
      maxBitrateKbps: 1000,
    });
    expect(CELLULAR_CEILING.fps).toBe(CELLULAR_MAX_FPS);
    expect(CELLULAR_MIN_FPS).toBe(5);
  });

  it('drops to the TURN session quota on a relayed path, keeping the resolution', () => {
    const capped = capForCandidate(qualityCeiling('wifi'), 'relay');
    expect(capped.maxBitrateKbps).toBe(RELAY_QUOTA_KBPS);
    expect(capped.width).toBe(1920);
    expect(requestedQuality('wifi', 'relay').maxBitrateKbps).toBe(2000);
  });

  it('leaves a direct path alone, and never raises a cap that is already lower', () => {
    expect(capForCandidate(qualityCeiling('wifi'), 'host')).toEqual(WIFI_CEILING);
    expect(capForCandidate(qualityCeiling('wifi'), 'srflx')).toEqual(WIFI_CEILING);
    expect(capForCandidate(qualityCeiling('cellular'), 'relay').maxBitrateKbps).toBe(1000);
    expect(requestedQuality('cellular')).toEqual(CELLULAR_CEILING);
  });

  it('reads the radio the phone is on, and treats anything unclear as Wi-Fi’s ceiling', () => {
    expect(networkFor('CELLULAR')).toBe('cellular');
    expect(networkFor('WIFI')).toBe('wifi');
    expect(networkFor('ETHERNET')).toBe('wifi');
    expect(networkFor(undefined)).toBe('wifi');
  });
});

describe('the codecs the phone tells the Mac it can decode', () => {
  it('reads them from getCapabilities, in the order the Mac should prefer', () => {
    expect(
      decodableCodecs({
        codecs: [
          { mimeType: 'video/AV1' },
          { mimeType: 'video/VP9' },
          { mimeType: 'video/H264' },
          { mimeType: 'video/VP8' },
        ],
      }),
    ).toEqual(['H264', 'VP9', 'AV1']);
  });

  it('never offers HEVC, whatever the phone reports', () => {
    expect(decodableCodecs({ codecs: [{ mimeType: 'video/H265' }, { mimeType: 'video/HEVC' }] })).toEqual([
      'H264',
    ]);
  });

  it('always claims H.264: hardware decoding for it is the baseline and the fallback', () => {
    expect(decodableCodecs(undefined)).toEqual(['H264']);
    expect(decodableCodecs({ codecs: [] })).toEqual(['H264']);
    expect(decodableCodecs({ codecs: [{ mimeType: 'video/VP9' }] })).toEqual(['H264', 'VP9']);
  });

  it('expects H.264 on Wi-Fi, where latency beats bitrate', () => {
    expect(preferredCodec('wifi', ['H264', 'VP9', 'AV1'])).toBe('H264');
  });

  it('expects VP9 or AV1 on mobile data, but only when the phone can decode them', () => {
    expect(preferredCodec('cellular', ['H264', 'VP9', 'AV1'])).toBe('AV1');
    expect(preferredCodec('cellular', ['H264', 'VP9'])).toBe('VP9');
    expect(preferredCodec('cellular', ['H264'])).toBe('H264');
  });
});

describe('zoom', () => {
  it('asks for the whole display until the pinch is worth a region', () => {
    expect(zoomRegionFor(1, { x: 0.5, y: 0.5 })).toEqual(FULL_REGION);
    expect(zoomRegionFor(MIN_ZOOM_SCALE - 0.01, { x: 0.5, y: 0.5 })).toEqual(FULL_REGION);
    expect(isZoomed(FULL_REGION)).toBe(false);
  });

  it('centres the region on the point the user pinched', () => {
    const region = zoomRegionFor(2, { x: 0.5, y: 0.5 });
    expect(region).toEqual({ x: 0.25, y: 0.25, width: 0.5, height: 0.5 });
    expect(isZoomed(region)).toBe(true);
  });

  it('keeps the region inside the display when the user pans to an edge', () => {
    const topLeft = zoomRegionFor(4, { x: 0, y: 0 });
    expect(topLeft).toEqual({ x: 0, y: 0, width: 0.25, height: 0.25 });
    const bottomRight = zoomRegionFor(4, { x: 1, y: 1 });
    expect(bottomRight).toEqual({ x: 0.75, y: 0.75, width: 0.25, height: 0.25 });
  });

  it('stops at 8×, where a phone shows a few characters', () => {
    expect(clampZoomScale(100)).toBe(MAX_ZOOM_SCALE);
    expect(clampZoomScale(0.2)).toBe(1);
    expect(clampZoomScale(Number.NaN)).toBe(1);
    expect(zoomRegionFor(100, { x: 0.5, y: 0.5 }).width).toBeCloseTo(1 / MAX_ZOOM_SCALE);
  });

  it('turns a touch inside the zoomed view back into a point on the display', () => {
    const region = { x: 0.25, y: 0.5, width: 0.5, height: 0.25 };
    expect(pointInRegion(region, { x: 0, y: 0 })).toEqual({ x: 0.25, y: 0.5 });
    expect(pointInRegion(region, { x: 1, y: 1 })).toEqual({ x: 0.75, y: 0.75 });
    expect(pointInRegion(region, { x: 0.5, y: 0.5 })).toEqual({ x: 0.5, y: 0.625 });
    expect(pointInRegion(FULL_REGION, { x: 2, y: -1 })).toEqual({ x: 1, y: 0 });
  });

  it('keeps the overview layer small: the whole display, at no real cost', () => {
    expect(overviewSize({ width: 1920, height: 1080 })).toEqual({ width: 640, height: 360 });
    expect(overviewSize({ width: 640, height: 480 })).toEqual({ width: 640, height: 480 });
    expect(overviewSize({ width: 0, height: 0 })).toEqual({ width: 0, height: 0 });
  });
});

describe('the input the phone sends', () => {
  it('only exists in a control session', () => {
    expect(mayInject('control')).toBe(true);
    expect(mayInject('view')).toBe(false);
  });

  it('carries displayId and normalized 0–1 coordinates, clamped', () => {
    expect(tapEvent(3, { x: 0.5, y: 0.25 })).toEqual({
      t: 'tap',
      displayId: 3,
      x: 0.5,
      y: 0.25,
      button: 'left',
    });
    expect(tapEvent(3, { x: 2, y: -1 })).toMatchObject({ x: 1, y: 0 });
    expect(tapEvent(3, { x: Number.NaN, y: 0.5 })).toMatchObject({ x: 0 });
  });

  it('sends a long press as a right click', () => {
    expect(rightClickEvent(1, { x: 0.1, y: 0.2 })).toEqual({
      t: 'tap',
      displayId: 1,
      x: 0.1,
      y: 0.2,
      button: 'right',
    });
  });

  it('sends a drag in three phases and a scroll with normalized deltas', () => {
    expect(dragEvent(1, { x: 0.1, y: 0.2 }, 'begin')).toMatchObject({ t: 'drag', phase: 'begin' });
    expect(dragEvent(1, { x: 0.1, y: 0.2 }, 'end')).toMatchObject({ phase: 'end' });
    expect(scrollEvent(1, { x: 0.5, y: 0.5 }, { dx: 0, dy: -0.25 })).toEqual({
      t: 'scroll',
      displayId: 1,
      x: 0.5,
      y: 0.5,
      dx: 0,
      dy: -0.25,
    });
    expect(scrollEvent(1, { x: 0.5, y: 0.5 }, { dx: Number.NaN, dy: 1 })).toMatchObject({ dx: 0 });
  });

  it('sends text as one committed string, English or Korean alike', () => {
    expect(textEvent('ls -la')).toEqual({ t: 'text', text: 'ls -la' });
    // The IME finishes the syllable on the phone; only the finished text goes out.
    expect(textEvent('안녕하세요')).toEqual({ t: 'text', text: '안녕하세요' });
    expect(textEvent('')).toBeUndefined();
    expect(textEvent('가'.repeat(MAX_INPUT_TEXT_BYTES))).toBeUndefined();
  });

  it('sends key combos in the contract’s grammar, and nothing outside it', () => {
    expect(shortcutEvent('cmd+c')).toEqual({ t: 'key', combo: 'cmd+c' });
    expect(shortcutEvent('cmd+v')).toEqual({ t: 'key', combo: 'cmd+v' });
    expect(shortcutEvent('shift+cmd+z')).toEqual({ t: 'key', combo: 'shift+cmd+z' });
    expect(shortcutEvent('opt+left')).toEqual({ t: 'key', combo: 'opt+left' });
    expect(shortcutEvent('ctrl+opt+shift+cmd+pagedown')).toBeDefined();
    expect(shortcutEvent('return')).toEqual({ t: 'key', combo: 'return' });
    // Upper case, an unknown key, a doubled modifier, a modifier alone, a key code.
    expect(shortcutEvent('CMD+C')).toBeUndefined();
    expect(shortcutEvent('cmd+f13')).toBeUndefined();
    expect(shortcutEvent('cmd+cmd+c')).toBeUndefined();
    expect(shortcutEvent('cmd')).toBeUndefined();
    expect(shortcutEvent('cmd+0x24')).toBeUndefined();
    expect(shortcutEvent('')).toBeUndefined();
    expect(isKeyCombo('cmd+c+v')).toBe(false);
  });

  it('asks for a zoom region and a display switch on the same channel', () => {
    expect(zoomEvent(2, { x: 0.25, y: 0.25, width: 0.5, height: 0.5 })).toEqual({
      t: 'zoom',
      displayId: 2,
      region: { x: 0.25, y: 0.25, width: 0.5, height: 0.5 },
    });
    expect(displaySwitchEvent(9)).toEqual({ t: 'display', displayId: 9 });
  });

  it('is sent uncompressed, as plain JSON: a pointer move waits on nothing', () => {
    expect(JSON.parse(encodeInputEvent(tapEvent(1, { x: 0.5, y: 0.5 })))).toEqual({
      t: 'tap',
      displayId: 1,
      x: 0.5,
      y: 0.5,
      button: 'left',
    });
  });

  it('never puts a keystroke in a label, only its length', () => {
    expect(describeInputEvent(textEvent('rm -rf /') as never)).toBe('text:8');
    expect(describeInputEvent(textEvent('비밀번호') as never)).not.toContain('비밀');
    expect(describeInputEvent(shortcutEvent('cmd+v')!)).toBe('key');
    expect(describeInputEvent(tapEvent(1, { x: 0, y: 0 }, 'right'))).toBe('tap:right');
    expect(describeInputEvent(displaySwitchEvent(4))).toBe('display:4');
  });
});
