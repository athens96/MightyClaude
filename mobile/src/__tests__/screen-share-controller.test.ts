import { ApiError } from '@/api/client';
import type { ScreenSessionRequest, ScreenShareState } from '@/api/types';
import { toBase64, utf8Decode, utf8Encode } from '@/api/relay/crypto';
import { createScreenShareController } from '@/lib/screen-share/controller';
import { verifyControlSignature, type SecureKeyStore } from '@/lib/screen-share/control-key';
import type { ScreenPeer, ScreenPeerCallbacks, ScreenPeerConfig } from '@/lib/screen-share/peer';
import type { ScreenSignal } from '@/lib/screen-share/signalling';
import type { ZstdCodec } from '@/lib/screen-share/clipboard';

/**
 * The whole phone side, end to end, against a fake Mac: a session is asked for, the Mac's
 * offer is answered, candidates trickle both ways, input and the clipboard go down the data
 * channel, and the Mac's revocations and kills take the picture away. No radio involved.
 */

const CHALLENGE = toBase64(utf8Encode('screen-control-challenge:s1:1760000000'));

function hostState(overrides: Partial<ScreenShareState> = {}): ScreenShareState {
  return {
    allowed: true,
    grant: 'control',
    isBeta: true,
    displays: [
      { displayId: 1, width: 1920, height: 1080, main: true },
      { displayId: 2, width: 1280, height: 800, main: false },
    ],
    controlChallengeB64: CHALLENGE,
    iceServers: [{ urls: 'turn:relay.example:3478', username: '1:abc', credential: 'minted' }],
    idleTimeoutSeconds: 600,
    ...overrides,
  };
}

function fakeStore(): SecureKeyStore {
  const items = new Map<string, string>();
  return {
    getItemAsync: async (key) => items.get(key) ?? null,
    setItemAsync: async (key, value) => void items.set(key, value),
    deleteItemAsync: async (key) => void items.delete(key),
  };
}

function fakeZstd(): ZstdCodec {
  return {
    compress: (data) => {
      const body = utf8Encode(data);
      return body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength) as ArrayBuffer;
    },
    decompress: (data) => utf8Decode(new Uint8Array(data)),
  };
}

function harness(options: { state?: ScreenShareState; refuse?: ApiError } = {}) {
  const sent: ScreenSignal[] = [];
  const requests: ScreenSessionRequest[] = [];
  const registered: string[] = [];
  const dataFrames: string[] = [];
  const peerConfigs: ScreenPeerConfig[] = [];
  const clipboard = { phone: 'from the phone' };
  let signalListener: ((signal: ScreenSignal) => void) | undefined;
  let callbacks: ScreenPeerCallbacks | undefined;
  let closed = 0;
  let clock = 0;

  const peer: ScreenPeer = {
    answer: async (offerSdp) => `answer-for:${offerSdp}`,
    addIceCandidate: async () => undefined,
    endOfRemoteCandidates: async () => undefined,
    sendData: (payload) => {
      dataFrames.push(payload);
      return true;
    },
    candidateType: async () => 'relay',
    close: () => {
      closed += 1;
    },
  };

  const controller = createScreenShareController({
    client: {
      screenShareState: async () => options.state ?? hostState(),
      startScreenShare: async (input) => {
        requests.push(input);
        if (options.refuse) throw options.refuse;
        return {
          sessionId: 's1',
          mode: input.mode,
          displayId: input.displayId,
          codec: 'H264',
          quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
        };
      },
      registerScreenControlKey: async (publicKeyB64) => {
        registered.push(publicKeyB64);
        return { protocol: 1, ok: true };
      },
      onScreenSignal: (listener) => {
        signalListener = listener;
        return () => {
          signalListener = undefined;
        };
      },
      sendScreenSignal: (signal) => {
        sent.push(signal);
        return true;
      },
    },
    peerFactory: (config, cb) => {
      peerConfigs.push(config);
      callbacks = cb;
      return peer;
    },
    controlKey: { hostId: 'h1', store: fakeStore(), prompt: '본인 확인' },
    now: () => clock,
    network: async () => 'wifi',
    decoders: () => ({ codecs: [{ mimeType: 'video/H264' }, { mimeType: 'video/VP9' }] }),
    clientId: 'c1',
    zstd: fakeZstd(),
    readClipboard: async () => clipboard.phone,
    writeClipboard: async (text) => {
      clipboard.phone = text;
    },
  });

  return {
    controller,
    sent,
    requests,
    registered,
    dataFrames,
    peerConfigs,
    clipboard,
    get closed() {
      return closed;
    },
    signal: (signal: ScreenSignal) => signalListener?.(signal),
    peerCallbacks: () => callbacks,
    advance: (ms: number) => {
      clock += ms;
    },
    at: () => clock,
  };
}

const OFFER: ScreenSignal = {
  type: 'screen-offer',
  sessionId: 's1',
  sdp: 'v=0',
  mode: 'control',
  displayId: 1,
  codec: 'H264',
  quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
  iceRestart: false,
};

/** Takes a control session all the way to live. */
async function liveControl(h: ReturnType<typeof harness>) {
  await h.controller.refresh();
  await h.controller.start('control');
  h.signal(OFFER);
  await Promise.resolve();
  await Promise.resolve();
  h.peerCallbacks()?.onConnectionState('connected');
  await Promise.resolve();
}

describe('refreshing from the Mac', () => {
  it('enrols the biometric control key the first time the Mac grants control', async () => {
    const h = harness();
    await h.controller.refresh();
    expect(h.registered).toHaveLength(1);
    expect(Buffer.from(h.registered[0] ?? '', 'base64')).toHaveLength(65);
    // A second refresh reuses the key the Keystore already holds.
    await h.controller.refresh();
    expect(h.registered).toHaveLength(1);
  });

  it('enrols nothing while the grant is only view', async () => {
    const h = harness({ state: hostState({ grant: 'view', controlChallengeB64: undefined }) });
    await h.controller.refresh();
    expect(h.registered).toEqual([]);
    expect(h.controller.snapshot().session.grant).toBe('view');
  });
});

describe('starting a session', () => {
  it('tells the Mac the network and what this phone can decode, and never offers HEVC', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.requests[0]).toMatchObject({
      mode: 'view',
      displayId: 1,
      network: 'wifi',
      decodes: ['H264', 'VP9'],
    });
    expect(h.requests[0]?.decodes).not.toContain('HEVC');
  });

  it('defaults to the main display', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.requests[0]?.displayId).toBe(1);
  });

  it('signs the Mac’s challenge for control, with a signature the Mac can verify', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('control');
    const signature = h.requests[0]?.controlSignatureB64;
    expect(signature).toBeDefined();
    expect(verifyControlSignature(h.registered[0] ?? '', CHALLENGE, signature ?? '')).toBe(true);
  });

  it('sends no signature for a view session: it needs none', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.requests[0]?.controlSignatureB64).toBeUndefined();
  });

  it('refuses control with no challenge rather than asking the Mac without one', async () => {
    const h = harness({ state: hostState({ controlChallengeB64: undefined }) });
    await h.controller.refresh();
    await h.controller.start('control');
    expect(h.requests).toEqual([]);
    expect(h.controller.snapshot().controlFailure).toBe('no-challenge');
    expect(h.controller.snapshot().session.refusal).toBe('control-signature');
  });

  it('keeps the Mac’s reason when the Mac refuses', async () => {
    const h = harness({
      refuse: new ApiError(403, 'HTTP 403', 'device-not-allowed'),
    });
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.controller.snapshot().session.refusal).toBe('device-not-allowed');
  });

  it('shows a missing Screen Recording grant as the hold the user can clear on the Mac', async () => {
    const h = harness({ refuse: new ApiError(403, 'HTTP 403', 'screen-permission') });
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.controller.snapshot().session.hold).toBe('screen-permission');
  });

  it('hands the peer the TURN credentials the Mac forwarded, never a secret of its own', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.peerConfigs[0]?.iceServers).toEqual([
      { urls: 'turn:relay.example:3478', username: '1:abc', credential: 'minted' },
    ]);
  });
});

describe('signalling against the Mac', () => {
  it('answers the Mac’s offer, because the Mac is the one sending video', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('control');
    h.signal(OFFER);
    await Promise.resolve();
    await Promise.resolve();
    expect(h.sent).toContainEqual({
      type: 'screen-answer',
      sessionId: 's1',
      sdp: 'answer-for:v=0',
    });
  });

  it('trickles its own candidates one per frame, and closes the list with an empty one', async () => {
    const h = harness();
    await liveControl(h);
    h.peerCallbacks()?.onIceCandidate({ candidate: 'c1', sdpMid: '0', sdpMLineIndex: 0 });
    h.peerCallbacks()?.onIceCandidate(null);
    expect(h.sent).toContainEqual({
      type: 'screen-ice',
      sessionId: 's1',
      candidate: 'c1',
      sdpMid: '0',
      sdpMLineIndex: 0,
    });
    expect(h.sent).toContainEqual({ type: 'screen-ice', sessionId: 's1', candidate: '' });
  });

  it('reports which path the video took, so a TURN detour is visible', async () => {
    const h = harness();
    await liveControl(h);
    expect(h.controller.snapshot().candidateType).toBe('relay');
  });
});

describe('input in a live session', () => {
  it('sends taps, right clicks, drags, scrolls, text and ⌘C/⌘V down the data channel', async () => {
    const h = harness();
    await liveControl(h);
    h.dataFrames.length = 0;
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.controller.rightClick({ x: 0.1, y: 0.2 });
    h.controller.drag({ x: 0.3, y: 0.3 }, 'begin');
    h.controller.scroll({ x: 0.4, y: 0.4 }, { dx: 0, dy: -0.1 });
    h.controller.sendText('안녕하세요');
    h.controller.shortcut('cmd+c');
    h.controller.shortcut('cmd+v');
    const frames = h.dataFrames.map((frame) => JSON.parse(frame));
    expect(frames).toEqual([
      { t: 'tap', displayId: 1, x: 0.5, y: 0.5, button: 'left' },
      { t: 'tap', displayId: 1, x: 0.1, y: 0.2, button: 'right' },
      { t: 'drag', displayId: 1, x: 0.3, y: 0.3, phase: 'begin' },
      { t: 'scroll', displayId: 1, x: 0.4, y: 0.4, dx: 0, dy: -0.1 },
      { t: 'text', text: '안녕하세요' },
      { t: 'key', combo: 'cmd+c' },
      { t: 'key', combo: 'cmd+v' },
    ]);
  });

  it('sends nothing at all from a view-only session', async () => {
    const h = harness({ state: hostState({ grant: 'view', controlChallengeB64: undefined }) });
    await h.controller.refresh();
    await h.controller.start('view');
    h.signal({ ...OFFER, mode: 'view' } as ScreenSignal);
    await Promise.resolve();
    await Promise.resolve();
    h.peerCallbacks()?.onConnectionState('connected');
    h.dataFrames.length = 0;
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.controller.sendText('rm -rf /');
    expect(h.dataFrames).toEqual([]);
  });

  it('asks the Mac to stream only the zoomed region', async () => {
    const h = harness();
    await liveControl(h);
    h.dataFrames.length = 0;
    h.controller.setZoom(2, { x: 0.5, y: 0.5 });
    expect(h.controller.snapshot().zoom).toEqual({ x: 0.25, y: 0.25, width: 0.5, height: 0.5 });
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toEqual({
      t: 'zoom',
      displayId: 1,
      region: { x: 0.25, y: 0.25, width: 0.5, height: 0.5 },
    });
  });

  it('switches display in session, and clears the zoom with it', async () => {
    const h = harness();
    await liveControl(h);
    h.controller.setZoom(4, { x: 0.5, y: 0.5 });
    h.dataFrames.length = 0;
    h.controller.switchDisplay(2);
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toEqual({ t: 'display', displayId: 2 });
    expect(h.controller.snapshot().zoom).toEqual({ x: 0, y: 0, width: 1, height: 1 });
  });

  it('keeps a session in use alive: input counts as activity', async () => {
    const h = harness();
    await liveControl(h);
    h.advance(599_000);
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.advance(599_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.phase).toBe('live');
  });
});

describe('the clipboard buttons', () => {
  it('sends this phone’s clipboard to the Mac, compressed before encryption', async () => {
    const h = harness();
    await liveControl(h);
    h.dataFrames.length = 0;
    await h.controller.pasteToMac();
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toMatchObject({
      t: 'clipboard',
      dir: 'to-mac',
      enc: 'zstd',
    });
    expect(h.controller.snapshot().clipboardMoved).toBe('to-mac');
  });

  it('asks for the Mac’s clipboard and lands it on the phone', async () => {
    const h = harness();
    await liveControl(h);
    h.dataFrames.length = 0;
    h.controller.copyFromMac();
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toEqual({ t: 'clipboard-request' });

    h.peerCallbacks()?.onData({
      t: 'clipboard',
      dir: 'to-phone',
      enc: 'zstd',
      bytes: 9,
      data: toBase64(utf8Encode('from mac!')),
    });
    await Promise.resolve();
    await Promise.resolve();
    expect(h.clipboard.phone).toBe('from mac!');
    expect(h.controller.snapshot().clipboardMoved).toBe('to-phone');
  });

  it('never pastes an item the Mac marked concealed', async () => {
    const h = harness();
    await liveControl(h);
    const before = h.clipboard.phone;
    h.peerCallbacks()?.onData({
      t: 'clipboard',
      dir: 'to-phone',
      enc: 'raw',
      bytes: 6,
      data: toBase64(utf8Encode('secret')),
      concealed: true,
    });
    await Promise.resolve();
    expect(h.clipboard.phone).toBe(before);
    expect(h.controller.snapshot().clipboardRefusal).toBe('concealed');
  });

  it('refuses to move the clipboard from a view-only session', async () => {
    const h = harness({ state: hostState({ grant: 'view', controlChallengeB64: undefined }) });
    await h.controller.refresh();
    await h.controller.start('view');
    h.signal({ ...OFFER, mode: 'view' } as ScreenSignal);
    await Promise.resolve();
    await Promise.resolve();
    h.peerCallbacks()?.onConnectionState('connected');
    h.dataFrames.length = 0;
    await h.controller.pasteToMac();
    h.controller.copyFromMac();
    expect(h.dataFrames).toEqual([]);
    expect(h.controller.snapshot().clipboardRefusal).toBe('not-control');
  });
});

describe('when the Mac takes it away', () => {
  it('closes the peer connection on a kill, and stops showing a picture', async () => {
    const h = harness();
    await liveControl(h);
    h.peerCallbacks()?.onStream('url://remote');
    expect(h.controller.snapshot().streamUrl).toBe('url://remote');
    h.signal({ type: 'screen-kill', reason: 'kill-switch' });
    expect(h.closed).toBeGreaterThan(0);
    expect(h.controller.snapshot().streamUrl).toBeUndefined();
    expect(h.controller.snapshot().session.stopReason).toBe('kill-switch');
  });

  it('closes the peer connection when the grant is withdrawn, with the relay silent', async () => {
    const h = harness();
    await liveControl(h);
    h.signal({ type: 'screen-grant', allowed: false, grant: 'none' });
    expect(h.closed).toBeGreaterThan(0);
    expect(h.controller.snapshot().session.phase).toBe('ended');
    expect(h.controller.snapshot().session.stopReason).toBe('revoked');
  });

  it('ends a control session that was downgraded to view-only', async () => {
    const h = harness();
    await liveControl(h);
    h.signal({ type: 'screen-grant', allowed: true, grant: 'view' });
    expect(h.controller.snapshot().session.stopReason).toBe('grant-downgrade');
    expect(h.closed).toBeGreaterThan(0);
  });

  it('sends no input after the session has ended', async () => {
    const h = harness();
    await liveControl(h);
    h.signal({ type: 'screen-kill', reason: 'revoked' });
    h.dataFrames.length = 0;
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.controller.sendText('still there?');
    expect(h.dataFrames).toEqual([]);
  });
});

describe('the phone letting go on its own', () => {
  it('ends the session 30 s after the app goes to the background, and tells the Mac', async () => {
    const h = harness();
    await liveControl(h);
    h.controller.background();
    h.advance(29_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.phase).toBe('live');
    h.advance(1_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.stopReason).toBe('background');
    expect(h.sent).toContainEqual({
      type: 'screen-session-end',
      sessionId: 's1',
      reason: 'background',
    });
    expect(h.closed).toBeGreaterThan(0);
  });

  it('times a quiet control session out after 10 minutes', async () => {
    const h = harness();
    await liveControl(h);
    h.advance(600_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.stopReason).toBe('idle-timeout');
  });

  it('stops on 중지 and says so to the Mac', async () => {
    const h = harness();
    await liveControl(h);
    h.controller.stop();
    expect(h.sent).toContainEqual({
      type: 'screen-session-end',
      sessionId: 's1',
      reason: 'user-stop',
    });
  });

  it('gives up the session when the screen is left', async () => {
    const h = harness();
    await liveControl(h);
    h.controller.dispose();
    expect(h.closed).toBeGreaterThan(0);
    expect(h.sent).toContainEqual({
      type: 'screen-session-end',
      sessionId: 's1',
      reason: 'user-stop',
    });
  });
});
