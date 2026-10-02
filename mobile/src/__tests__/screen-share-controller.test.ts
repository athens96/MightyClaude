import { ApiError } from '@/api/client';
import type {
  ScreenSessionRequest,
  ScreenSessionResponse,
  ScreenShareState,
} from '@/api/types';
import { toBase64, utf8Encode } from '@/api/relay/crypto';
import { CLIPBOARD_CHUNK_BYTES, type ZstdCodec } from '@/lib/screen-share/clipboard';
import { createScreenShareController, untilAborted } from '@/lib/screen-share/controller';
import { controlKeyAlias, controlKeyFingerprint, verifyControlSignature } from '@/lib/screen-share/control-key';
import type {
  ScreenIceCandidateInit,
  ScreenPeer,
  ScreenPeerCallbacks,
  ScreenPeerConfig,
} from '@/lib/screen-share/peer';
import { SCREEN_CONNECT_DEADLINE_MS } from '@/lib/screen-share/session';
import type { ScreenSignal } from '@/lib/screen-share/signalling';
import { fakeControlKey, type FakeControlKey } from './support/fake-control-key';
import { testZstd } from './support/test-zstd';

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

function accepted(input: ScreenSessionRequest, sessionId = 's1'): ScreenSessionResponse {
  return {
    sessionId,
    mode: input.mode,
    displayId: input.displayId,
    codec: 'H264',
    quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
  };
}

interface FakePeer extends ScreenPeer {
  callbacks: ScreenPeerCallbacks;
  config: ScreenPeerConfig;
  answers: { sdp: string; iceRestart: boolean; iceServers?: unknown }[];
  candidates: ScreenIceCandidateInit[];
  closed: number;
}

interface HarnessOptions {
  state?: ScreenShareState;
  refuse?: unknown;
  /** Hold the start reply until the test releases it. */
  holdStart?: boolean;
  native?: FakeControlKey;
  zstd?: ZstdCodec;
  /** The Mac's answer to a control key, when it is not a yes. */
  refuseEnrol?: ApiError;
}

function harness(options: HarnessOptions = {}) {
  const sent: ScreenSignal[] = [];
  const requests: ScreenSessionRequest[] = [];
  const registered: string[] = [];
  const dataFrames: string[] = [];
  const peers: FakePeer[] = [];
  const clipboard = { phone: 'from the phone', writes: 0 };
  const native = options.native ?? fakeControlKey();
  let state = options.state ?? hostState();
  let signalListener: ((signal: ScreenSignal) => void) | undefined;
  let clock = 0;
  let channelOpen = true;
  let framesDecoded = 0;
  const heldStarts: { resolve: (value: ScreenSessionResponse) => void; reject: (error: unknown) => void; input: ScreenSessionRequest }[] = [];

  const controller = createScreenShareController({
    client: {
      screenShareState: async () => state,
      startScreenShare: (input) => {
        requests.push(input);
        if (options.holdStart) {
          return new Promise<ScreenSessionResponse>((resolve, reject) => {
            heldStarts.push({ resolve, reject, input });
          });
        }
        if (options.refuse) return Promise.reject(options.refuse);
        return Promise.resolve(accepted(input));
      },
      registerScreenControlKey: async (publicKeyB64) => {
        if (options.refuseEnrol) throw options.refuseEnrol;
        registered.push(publicKeyB64);
        const fingerprint = controlKeyFingerprint(publicKeyB64) ?? '';
        state = { ...state, controlKeyFingerprint: fingerprint };
        return { fingerprint };
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
    peerFactory: (config, callbacks) => {
      const peer: FakePeer = {
        callbacks,
        config,
        answers: [],
        candidates: [],
        closed: 0,
        answer: async (offerSdp, answerOptions) => {
          peer.answers.push({ sdp: offerSdp, ...answerOptions });
          return `answer-for:${offerSdp}`;
        },
        addIceCandidate: async (candidate) => {
          peer.candidates.push(candidate);
        },
        endOfRemoteCandidates: async () => undefined,
        sendData: (payload) => {
          if (!channelOpen) return false;
          dataFrames.push(payload);
          return true;
        },
        stats: async () =>
          new Map<string, Record<string, unknown>>([
            ['T', { type: 'transport', selectedCandidatePairId: 'P' }],
            ['P', { type: 'candidate-pair', localCandidateId: 'L', currentRoundTripTime: 0.02 }],
            ['L', { type: 'local-candidate', candidateType: 'relay' }],
            ['V', { type: 'inbound-rtp', kind: 'video', trackIdentifier: 'screen-s1', framesDecoded, bytesReceived: 100 }],
          ]),
        close: () => {
          peer.closed += 1;
        },
      };
      peers.push(peer);
      return peer;
    },
    controlKey: { hostId: 'h1', native, prompt: { title: '본인 확인', cancel: '취소' } },
    now: () => clock,
    network: async () => 'wifi',
    decoders: () => ({ codecs: [{ mimeType: 'video/H264' }, { mimeType: 'video/VP9' }] }),
    zstd: options.zstd ?? testZstd(),
    readClipboard: async () => clipboard.phone,
    writeClipboard: async (text) => {
      clipboard.phone = text;
      clipboard.writes += 1;
    },
  });

  return {
    controller,
    native,
    sent,
    requests,
    registered,
    dataFrames,
    peers,
    clipboard,
    heldStarts,
    get state() {
      return state;
    },
    setState(next: ScreenShareState) {
      state = next;
    },
    setChannelOpen(open: boolean) {
      channelOpen = open;
    },
    setFramesDecoded(frames: number) {
      framesDecoded = frames;
    },
    peer: () => peers[peers.length - 1],
    signal: (signal: ScreenSignal) => signalListener?.(signal),
    advance: (ms: number) => {
      clock += ms;
    },
  };
}

type Harness = ReturnType<typeof harness>;

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

async function flush(times = 6): Promise<void> {
  for (let index = 0; index < times; index += 1) await Promise.resolve();
}

/** Registers a key so control may start, as a Mac user confirming it would. */
async function enrolled(h: Harness): Promise<void> {
  await h.controller.refresh();
  await h.controller.enrolControlKey();
  expect(h.controller.snapshot().controlKey.status).toBe('ready');
}

/** Takes a session all the way to live. */
async function goLive(h: Harness, mode: 'view' | 'control' = 'control'): Promise<void> {
  if (mode === 'control') await enrolled(h);
  else await h.controller.refresh();
  await h.controller.start(mode);
  h.signal({ ...OFFER, mode } as ScreenSignal);
  await flush();
  h.peer()?.callbacks.onConnectionState('connected');
  await flush();
  expect(h.controller.snapshot().session.phase).toBe('live');
}

describe('the control key lifecycle', () => {
  it('opens the screen without a prompt and without enrolling anything', async () => {
    const h = harness();
    await h.controller.refresh();
    expect(h.native.prompts).toEqual([]);
    expect(h.native.generated).toEqual([]);
    expect(h.registered).toEqual([]);
    expect(h.controller.snapshot().controlKey.status).toBe('missing');
  });

  it('enrols only when asked, and shows the fingerprint the Mac’s user will compare', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.enrolControlKey();
    expect(h.registered).toHaveLength(1);
    expect(Buffer.from(h.registered[0] ?? '', 'base64')).toHaveLength(65);
    const key = h.controller.snapshot().controlKey;
    expect(key.status).toBe('ready');
    expect(key.phoneFingerprint).toBe(controlKeyFingerprint(h.registered[0] ?? ''));
    expect(h.native.prompts).toEqual([]);
  });

  it('decides "has key" from fingerprints alone on the next visit', async () => {
    const native = fakeControlKey();
    const first = harness({ native });
    await enrolled(first);
    const second = harness({ native, state: first.state });
    await second.controller.refresh();
    expect(second.controller.snapshot().controlKey.status).toBe('ready');
    expect(native.prompts).toEqual([]);
  });

  it('registers the existing key again when the Mac forgot it, without making a new one', async () => {
    const native = fakeControlKey();
    await native.generate(controlKeyAlias('h1'));
    const h = harness({ native });
    await h.controller.refresh();
    expect(h.controller.snapshot().controlKey.status).toBe('host-missing');
    await h.controller.enrolControlKey();
    expect(native.generated).toHaveLength(1);
    expect(h.controller.snapshot().controlKey.status).toBe('ready');
  });

  it('does not send a key to a Mac that already holds another one', async () => {
    // The Mac would answer 409 control-key-present until its user removes the key.
    const h = harness({ state: hostState({ controlKeyFingerprint: 'AAAA-BBBB-CCCC-DDDD' }) });
    await h.controller.refresh();
    expect(h.controller.snapshot().controlKey.status).toBe('phone-missing');
    await h.controller.enrolControlKey();
    expect(h.registered).toEqual([]);
    expect(h.controller.snapshot().controlKey.enrolFailure).toBeUndefined();
    expect(h.controller.snapshot().controlKey.enrolling).toBe(false);
  });

  it('reports the Mac’s answer when it turns a key away', async () => {
    const h = harness({ refuseEnrol: new ApiError(409, 'HTTP 409', 'control-key-present') });
    await h.controller.refresh();
    expect(h.controller.snapshot().controlKey.status).toBe('missing');
    await h.controller.enrolControlKey();
    expect(h.controller.snapshot().controlKey.enrolFailure).toBe('control-key-present');
    expect(h.controller.snapshot().controlKey.enrolling).toBe(false);
  });

  it('says so when the Mac’s user is still looking at another request', async () => {
    const h = harness({ refuseEnrol: new ApiError(409, 'HTTP 409', 'control-key-pending') });
    await h.controller.refresh();
    await h.controller.enrolControlKey();
    expect(h.controller.snapshot().controlKey.enrolFailure).toBe('control-key-pending');
  });

  it('never enrols as a side effect of a cancelled prompt', async () => {
    const h = harness();
    await enrolled(h);
    h.native.nextPrompt = { code: 'E_AUTH_CANCELLED' };
    await h.controller.start('control');
    expect(h.controller.snapshot().controlFailure).toBe('authentication-failed');
    expect(h.registered).toHaveLength(1);
    expect(h.native.generated).toHaveLength(1);
    expect(h.requests).toEqual([]);
  });

  it('drops a key the Keystore invalidated, and waits for the user to register again', async () => {
    const h = harness();
    await enrolled(h);
    h.native.nextPrompt = { code: 'E_KEY_INVALIDATED' };
    await h.controller.start('control');
    expect(h.controller.snapshot().controlFailure).toBe('key-invalidated');
    expect(h.native.has(controlKeyAlias('h1'))).toBe(false);
    expect(h.native.generated).toHaveLength(1);
  });

  it('forgets the key when the grant leaves control', async () => {
    const h = harness();
    await enrolled(h);
    h.signal({ type: 'screen-grant', allowed: true, grant: 'view' });
    await flush();
    expect(h.native.removed).toContain(controlKeyAlias('h1'));
    expect(h.controller.snapshot().controlKey.status).toBe('not-needed');
  });

  it('is unsupported without a Keystore module, and never offers control there', async () => {
    const h = harness({ native: fakeControlKey({ supported: false }) });
    await h.controller.refresh();
    expect(h.controller.snapshot().controlKey.status).toBe('unsupported');
    await h.controller.start('control');
    expect(h.requests).toEqual([]);
    expect(h.controller.snapshot().controlFailure).toBe('unsupported');
  });
});

describe('starting a session', () => {
  it('tells the Mac the network and what this phone can decode, and never offers HEVC', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.requests[0]).toMatchObject({ mode: 'view', displayId: 1, network: 'wifi', decodes: ['H264', 'VP9'] });
    expect(h.requests[0]?.decodes).not.toContain('HEVC');
  });

  it('signs a fresh challenge for control, with a signature the Mac can verify', async () => {
    const h = harness();
    await enrolled(h);
    await h.controller.start('control');
    const signature = h.requests[0]?.controlSignatureB64;
    expect(verifyControlSignature(h.registered[0] ?? '', CHALLENGE, signature ?? '')).toBe(true);
    expect(h.native.prompts).toHaveLength(1);
  });

  it('sends no signature for a view session, and raises no prompt', async () => {
    const h = harness();
    await enrolled(h);
    await h.controller.start('view');
    expect(h.requests[0]?.controlSignatureB64).toBeUndefined();
    expect(h.native.prompts).toEqual([]);
  });

  it('refuses control with no challenge rather than asking the Mac without one', async () => {
    const h = harness();
    await enrolled(h);
    h.setState(hostState({ controlChallengeB64: undefined, controlKeyFingerprint: h.state.controlKeyFingerprint }));
    await h.controller.start('control');
    expect(h.requests).toEqual([]);
    expect(h.controller.snapshot().controlFailure).toBe('no-challenge');
  });

  it('keeps the Mac’s reason when the Mac refuses, including the new ones', async () => {
    for (const reason of ['device-not-allowed', 'lock-screen', 'session-stopped', 'concurrency-limit'] as const) {
      const h = harness({ refuse: new ApiError(403, 'HTTP 403', reason) });
      await h.controller.refresh();
      await h.controller.start('view');
      expect(h.controller.snapshot().session.refusal).toBe(reason);
    }
  });

  it('reads a failure that is not the Mac’s answer as a generic one', async () => {
    const h = harness({ refuse: new Error('socket closed') });
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.controller.snapshot().session.refusal).toBe('failed');
  });

  it('starts only from rest: never twice at once', async () => {
    const h = harness({ holdStart: true });
    await h.controller.refresh();
    void h.controller.start('view');
    await flush();
    await h.controller.start('view');
    expect(h.requests).toHaveLength(1);
  });

  it('hands the peer the TURN credentials the Mac forwarded, never a secret of its own', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    expect(h.peer()?.config.iceServers).toEqual([
      { urls: 'turn:relay.example:3478', username: '1:abc', credential: 'minted' },
    ]);
  });
});

describe('a reply that comes too late', () => {
  it('does not revive a start the user stopped, and tells the Mac to let it go', async () => {
    const h = harness({ holdStart: true });
    await h.controller.refresh();
    const starting = h.controller.start('view');
    await flush();
    h.controller.stop();
    await starting;
    h.heldStarts[0]?.resolve(accepted(h.heldStarts[0].input, 'late'));
    await flush();
    expect(h.controller.snapshot().session.phase).toBe('ended');
    expect(h.peers).toHaveLength(0);
    expect(h.sent).toContainEqual({ type: 'screen-session-end', sessionId: 'late', reason: 'user-stop' });
  });

  it('does not revive a start a kill ended, and the kill counts while starting', async () => {
    const h = harness({ holdStart: true });
    await h.controller.refresh();
    const starting = h.controller.start('view');
    await flush();
    h.signal({ type: 'screen-kill', sessionId: 'unknown-yet', reason: 'kill-switch' });
    expect(h.controller.snapshot().session.stopReason).toBe('kill-switch');
    h.heldStarts[0]?.resolve(accepted(h.heldStarts[0].input, 'late'));
    await starting;
    expect(h.controller.snapshot().session.phase).toBe('ended');
    expect(h.peers).toHaveLength(0);
    expect(h.sent).toContainEqual({ type: 'screen-session-end', sessionId: 'late', reason: 'user-stop' });
    // An offer for that session finds nobody listening.
    h.signal({ ...OFFER, sessionId: 'late' } as ScreenSignal);
    await flush();
    expect(h.peers).toHaveLength(0);
  });

  it('does nothing after dispose but tell the Mac', async () => {
    const h = harness({ holdStart: true });
    await h.controller.refresh();
    const starting = h.controller.start('view');
    await flush();
    const seen: string[] = [];
    h.controller.subscribe((snapshot) => seen.push(snapshot.session.phase));
    h.controller.dispose();
    await starting;
    h.heldStarts[0]?.resolve(accepted(h.heldStarts[0].input, 'late'));
    await flush();
    expect(h.peers).toHaveLength(0);
    expect(seen.filter((phase) => phase === 'connecting')).toEqual([]);
    expect(h.sent).toContainEqual({ type: 'screen-session-end', sessionId: 'late', reason: 'user-stop' });
  });

  it('gives up a start that never hears back', async () => {
    const h = harness({ holdStart: true });
    await h.controller.refresh();
    void h.controller.start('view');
    await flush();
    h.advance(SCREEN_CONNECT_DEADLINE_MS);
    h.controller.tick();
    expect(h.controller.snapshot().session.phase).toBe('ended');
    expect(h.controller.snapshot().session.stopReason).toBe('peer-failed');
  });
});

describe('signalling against the Mac', () => {
  it('answers the Mac’s offer, because the Mac is the one sending video', async () => {
    const h = harness();
    await goLive(h, 'view');
    expect(h.sent).toContainEqual({ type: 'screen-answer', sessionId: 's1', sdp: 'answer-for:v=0' });
  });

  it('applies renewed TURN servers with an ICE restart', async () => {
    const h = harness();
    await goLive(h, 'view');
    const renewed = [{ urls: 'turn:relay.example:3478', username: '2:abc', credential: 'renewed' }];
    h.signal({ type: 'screen-grant', sessionId: 's1', allowed: true, grant: 'control', iceServers: renewed });
    h.signal({ ...OFFER, mode: 'view', sdp: 'v=1', iceRestart: true } as ScreenSignal);
    await flush();
    expect(h.peer()?.answers[1]).toEqual({ sdp: 'v=1', iceRestart: true, iceServers: renewed });
  });

  it('holds the Mac’s candidates until the offer is answered', async () => {
    const h = harness();
    await h.controller.refresh();
    await h.controller.start('view');
    h.signal({ ...OFFER, mode: 'view' } as ScreenSignal);
    h.signal({ type: 'screen-ice', sessionId: 's1', candidate: 'early' });
    await flush();
    expect(h.peer()?.candidates).toEqual([{ candidate: 'early' }]);
  });

  it('trickles its own candidates one per frame, and closes the list with an empty one', async () => {
    const h = harness();
    await goLive(h, 'view');
    h.peer()?.callbacks.onIceCandidate({ candidate: 'c1', sdpMid: '0', sdpMLineIndex: 0 });
    h.peer()?.callbacks.onIceCandidate(null);
    expect(h.sent).toContainEqual({ type: 'screen-ice', sessionId: 's1', candidate: 'c1', sdpMid: '0', sdpMLineIndex: 0 });
    expect(h.sent).toContainEqual({ type: 'screen-ice', sessionId: 's1', candidate: '' });
  });

  it('reports which path the video took, so a TURN detour is visible', async () => {
    const h = harness();
    await goLive(h, 'view');
    expect(h.controller.snapshot().candidateType).toBe('relay');
  });

  it('keeps the two tracks apart: the overview never doubles as the screen', async () => {
    const h = harness();
    await goLive(h, 'view');
    h.peer()?.callbacks.onStream('url://screen', 'screen');
    h.peer()?.callbacks.onStream('url://overview', 'overview');
    expect(h.controller.snapshot().streamUrl).toBe('url://screen');
    expect(h.controller.snapshot().overviewUrl).toBe('url://overview');
  });

  it('ignores an old peer’s callbacks once a new session has its own', async () => {
    const h = harness();
    await goLive(h, 'view');
    const old = h.peer();
    h.controller.stop();
    await h.controller.start('view');
    h.signal({ ...OFFER, mode: 'view' } as ScreenSignal);
    await flush();
    expect(h.peers).toHaveLength(2);
    old?.callbacks.onConnectionState('failed');
    old?.callbacks.onStream('url://stale', 'screen');
    expect(h.controller.snapshot().session.phase).toBe('connecting');
    expect(h.controller.snapshot().streamUrl).toBeUndefined();
  });

  it('tells the Mac at once when the app goes to the background', async () => {
    const h = harness();
    await goLive(h, 'view');
    h.controller.background();
    expect(h.sent).toContainEqual({ type: 'screen-background', sessionId: 's1', background: true });
    h.controller.foreground();
    expect(h.sent).toContainEqual({ type: 'screen-background', sessionId: 's1', background: false });
  });
});

describe('input in a live session', () => {
  it('sends taps, right clicks, drags, scrolls, text and keys down the data channel', async () => {
    const h = harness();
    await goLive(h);
    h.dataFrames.length = 0;
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.controller.rightClick({ x: 0.1, y: 0.2 });
    h.controller.drag({ x: 0.3, y: 0.3 }, 'begin');
    h.controller.drag({ x: 0.4, y: 0.3 }, 'end');
    h.controller.scroll({ x: 0.4, y: 0.4 }, { dx: 0, dy: -0.1 });
    h.controller.sendText('안녕하세요');
    h.controller.shortcut('cmd+c');
    h.controller.shortcut('shift+cmd+z');
    h.controller.shortcut('cmd+cmd+c');
    h.controller.shortcut('f13');
    expect(h.dataFrames.map((frame) => JSON.parse(frame))).toEqual([
      { t: 'tap', displayId: 1, x: 0.5, y: 0.5, button: 'left' },
      { t: 'tap', displayId: 1, x: 0.1, y: 0.2, button: 'right' },
      { t: 'drag', displayId: 1, x: 0.3, y: 0.3, phase: 'begin' },
      { t: 'drag', displayId: 1, x: 0.4, y: 0.3, phase: 'end' },
      { t: 'scroll', displayId: 1, x: 0.4, y: 0.4, dx: 0, dy: -0.1 },
      { t: 'text', text: '안녕하세요' },
      { t: 'key', combo: 'cmd+c' },
      { t: 'key', combo: 'shift+cmd+z' },
    ]);
  });

  it('puts a marker id on a timed tap, and leaves off one the Mac would refuse', async () => {
    const h = harness({ state: hostState({ tapMarker: true }) });
    await goLive(h);
    expect(h.controller.snapshot().session.tapMarker).toBe(true);
    h.dataFrames.length = 0;
    h.controller.tap({ x: 0.25, y: 0.75 }, 'm1-a');
    h.controller.tap({ x: 0.25, y: 0.75 }, 'not ok');
    expect(h.dataFrames.map((frame) => JSON.parse(frame))).toEqual([
      { t: 'tap', displayId: 1, x: 0.25, y: 0.75, button: 'left', marker: 'm1-a' },
      { t: 'tap', displayId: 1, x: 0.25, y: 0.75, button: 'left' },
    ]);
  });

  it('hands the Mac’s marker echoes and scene phases to the measurement, not to the clipboard', async () => {
    const h = harness();
    await goLive(h);
    expect(h.controller.snapshot().session.tapMarker).toBeUndefined();
    const notes: unknown[] = [];
    const stop = h.controller.onMeasurementNote((note) => notes.push(note));
    h.peer()?.callbacks.onData({ t: 'marker', id: 'm1', shown: true });
    h.peer()?.callbacks.onData({ t: 'scene', phase: 'motion' });
    h.peer()?.callbacks.onData({ t: 'scene', phase: 'sideways' });
    stop();
    h.peer()?.callbacks.onData({ t: 'scene', phase: 'still' });
    await flush();
    expect(notes).toEqual([
      { t: 'marker', id: 'm1', shown: true },
      { t: 'scene', phase: 'motion' },
    ]);
    expect(h.clipboard.writes).toBe(0);
  });

  it('never sends text over 4096 UTF-8 bytes', async () => {
    const h = harness();
    await goLive(h);
    h.dataFrames.length = 0;
    h.controller.sendText('가'.repeat(1366));
    h.controller.sendText('가'.repeat(1365));
    expect(h.dataFrames).toHaveLength(1);
  });

  it('sends nothing at all from a view-only session', async () => {
    const h = harness({ state: hostState({ grant: 'view', controlChallengeB64: undefined }) });
    await goLive(h, 'view');
    h.dataFrames.length = 0;
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.controller.sendText('rm -rf /');
    expect(h.dataFrames).toEqual([]);
  });

  it('zooms in view mode too, and the next pinch starts from the last scale', async () => {
    const h = harness({ state: hostState({ grant: 'view', controlChallengeB64: undefined }) });
    await goLive(h, 'view');
    h.dataFrames.length = 0;
    h.controller.setZoom(2, { x: 0.5, y: 0.5 });
    expect(h.controller.snapshot().zoomScale).toBe(2);
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toEqual({
      t: 'zoom',
      displayId: 1,
      region: { x: 0.25, y: 0.25, width: 0.5, height: 0.5 },
    });
    h.controller.resetZoom();
    expect(h.controller.snapshot().zoom).toEqual({ x: 0, y: 0, width: 1, height: 1 });
    expect(h.controller.snapshot().zoomScale).toBe(1);
  });

  it('switches display only while live, and only picks one while at rest', async () => {
    const h = harness();
    await h.controller.refresh();
    h.controller.selectDisplay(2);
    expect(h.controller.snapshot().selectedDisplayId).toBe(2);
    await h.controller.start('view');
    expect(h.requests[0]?.displayId).toBe(2);
    // Connecting: neither switches nor restarts.
    h.dataFrames.length = 0;
    h.controller.selectDisplay(1);
    expect(h.dataFrames).toEqual([]);
    expect(h.requests).toHaveLength(1);
    h.signal({ ...OFFER, mode: 'view', displayId: 2 } as ScreenSignal);
    await flush();
    h.peer()?.callbacks.onConnectionState('connected');
    h.controller.selectDisplay(1);
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toEqual({ t: 'display', displayId: 1 });
  });

  it('counts input and frames as activity', async () => {
    const h = harness();
    await goLive(h);
    h.advance(599_000);
    h.controller.tap({ x: 0.5, y: 0.5 });
    h.advance(599_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.phase).toBe('live');
  });

  it('counts frames as activity: a screen that keeps changing never idles out', async () => {
    const h = harness();
    await goLive(h);
    h.advance(590_000);
    h.setFramesDecoded(10);
    h.controller.tick();
    await flush();
    h.advance(590_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.phase).toBe('live');
    h.advance(10_000);
    h.controller.tick();
    await flush();
    // No new frame since: the ceiling counts from the last one.
    expect(h.controller.snapshot().session.stopReason).toBe('idle-timeout');
  });
});

describe('the clipboard buttons', () => {
  it('sends this phone’s clipboard to the Mac in slices, and confirms only once all went', async () => {
    const h = harness();
    await goLive(h);
    h.clipboard.phone = 'x'.repeat(CLIPBOARD_CHUNK_BYTES * 2 + 5);
    h.dataFrames.length = 0;
    await h.controller.pasteToMac();
    const frames = h.dataFrames.map((frame) => JSON.parse(frame));
    expect(frames.length).toBeGreaterThanOrEqual(1);
    for (const frame of frames) {
      expect(frame).toMatchObject({ t: 'clipboard', dir: 'to-mac', total: frames.length, bytes: CLIPBOARD_CHUNK_BYTES * 2 + 5 });
      expect(typeof frame.id).toBe('string');
    }
    expect(h.controller.snapshot().clipboardMoved).toBe('to-mac');
  });

  it('does not claim success when the channel would not take it', async () => {
    const h = harness();
    await goLive(h);
    h.setChannelOpen(false);
    await h.controller.pasteToMac();
    expect(h.controller.snapshot().clipboardMoved).toBeUndefined();
    expect(h.controller.snapshot().clipboardRefusal).toBe('not-sent');
    h.controller.copyFromMac();
    expect(h.controller.snapshot().clipboardRefusal).toBe('not-sent');
  });

  it('asks for the Mac’s clipboard and lands the reassembled transfer on the phone', async () => {
    const zstd = testZstd();
    const h = harness({ zstd });
    await goLive(h);
    h.dataFrames.length = 0;
    h.controller.copyFromMac();
    expect(JSON.parse(h.dataFrames[0] ?? '{}')).toEqual({ t: 'clipboard-request' });

    const text = 'from mac! '.repeat(10_000);
    const payload = new Uint8Array(zstd.compress(text));
    const total = Math.ceil(payload.length / CLIPBOARD_CHUNK_BYTES);
    for (let seq = 0; seq < total; seq += 1) {
      h.peer()?.callbacks.onData({
        t: 'clipboard',
        dir: 'to-phone',
        id: 'm1',
        seq,
        total,
        enc: 'zstd',
        bytes: text.length,
        data: toBase64(payload.subarray(seq * CLIPBOARD_CHUNK_BYTES, (seq + 1) * CLIPBOARD_CHUNK_BYTES)),
      });
    }
    await flush();
    expect(h.clipboard.phone).toBe(text);
    expect(h.controller.snapshot().clipboardMoved).toBe('to-phone');
  });

  it('writes nothing the phone did not ask for', async () => {
    const h = harness();
    await goLive(h);
    const before = h.clipboard.phone;
    h.peer()?.callbacks.onData({
      t: 'clipboard',
      dir: 'to-phone',
      id: 'push',
      seq: 0,
      total: 1,
      enc: 'raw',
      bytes: 6,
      data: toBase64(utf8Encode('pushed')),
    });
    await flush();
    expect(h.clipboard.phone).toBe(before);
    expect(h.clipboard.writes).toBe(0);
  });

  it('never pastes an item the Mac marked concealed', async () => {
    const h = harness();
    await goLive(h);
    const before = h.clipboard.phone;
    h.controller.copyFromMac();
    h.peer()?.callbacks.onData({
      t: 'clipboard',
      dir: 'to-phone',
      id: 'c',
      seq: 0,
      total: 1,
      enc: 'raw',
      bytes: 0,
      data: '',
      concealed: true,
    });
    await flush();
    expect(h.clipboard.phone).toBe(before);
    expect(h.controller.snapshot().clipboardRefusal).toBe('concealed');
  });

  it('says so when the Mac never answers the request', async () => {
    const h = harness();
    await goLive(h);
    h.controller.copyFromMac();
    h.advance(31_000);
    h.controller.tick();
    expect(h.controller.snapshot().clipboardRefusal).toBe('no-reply');
  });

  it('refuses to move the clipboard from a view-only session', async () => {
    const h = harness({ state: hostState({ grant: 'view', controlChallengeB64: undefined }) });
    await goLive(h, 'view');
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
    await goLive(h);
    h.peer()?.callbacks.onStream('url://remote', 'screen');
    h.signal({ type: 'screen-kill', reason: 'kill-switch' });
    expect(h.peer()?.closed).toBeGreaterThan(0);
    expect(h.controller.snapshot().streamUrl).toBeUndefined();
    expect(h.controller.snapshot().session.stopReason).toBe('kill-switch');
  });

  it('closes the peer connection when the grant is withdrawn, with the relay silent', async () => {
    const h = harness();
    await goLive(h);
    h.signal({ type: 'screen-grant', allowed: false, grant: 'none' });
    expect(h.peer()?.closed).toBeGreaterThan(0);
    expect(h.controller.snapshot().session.stopReason).toBe('revoked');
  });

  it('ends a control session that was downgraded to view-only', async () => {
    const h = harness();
    await goLive(h);
    h.signal({ type: 'screen-grant', allowed: true, grant: 'view' });
    expect(h.controller.snapshot().session.stopReason).toBe('grant-downgrade');
  });

  it('sends no input after the session has ended', async () => {
    const h = harness();
    await goLive(h);
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
    await goLive(h);
    h.controller.background();
    h.advance(29_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.phase).toBe('live');
    h.advance(1_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.stopReason).toBe('background');
    expect(h.sent).toContainEqual({ type: 'screen-session-end', sessionId: 's1', reason: 'background' });
  });

  it('times a quiet control session out after 10 minutes', async () => {
    const h = harness();
    await goLive(h);
    h.advance(600_000);
    h.controller.tick();
    expect(h.controller.snapshot().session.stopReason).toBe('idle-timeout');
  });

  it('stops on 중지 and says so to the Mac', async () => {
    const h = harness();
    await goLive(h);
    h.controller.stop();
    expect(h.sent).toContainEqual({ type: 'screen-session-end', sessionId: 's1', reason: 'user-stop' });
  });

  it('gives up the session when the screen is left', async () => {
    const h = harness();
    await goLive(h);
    h.controller.dispose();
    expect(h.peer()?.closed).toBeGreaterThan(0);
    expect(h.sent).toContainEqual({ type: 'screen-session-end', sessionId: 's1', reason: 'user-stop' });
  });
});

describe('untilAborted', () => {
  it('removes its abort listener once the request settles, either way', async () => {
    const abort = new AbortController();
    const removed = jest.spyOn(abort.signal, 'removeEventListener');
    await expect(untilAborted(Promise.resolve(7), abort.signal)).resolves.toBe(7);
    await expect(untilAborted(Promise.reject(new Error('no')), abort.signal)).rejects.toThrow('no');
    expect(removed).toHaveBeenCalledTimes(2);
  });

  it('still rejects at once when the signal aborts first', async () => {
    const abort = new AbortController();
    const pending = untilAborted(new Promise<number>(() => undefined), abort.signal);
    abort.abort();
    await expect(pending).rejects.toThrow('aborted');
  });
});
