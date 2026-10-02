import {
  screenRejectReason,
  type MobileClient,
} from '@/api/client';
import type {
  ScreenCandidateType,
  ScreenDisplay,
  ScreenIceServer,
  ScreenMode,
  ScreenNetwork,
} from '@/api/types';
import {
  decodableCodecs,
  preferredCodec,
  type DecoderCapabilities,
} from '@/lib/screen-share/quality';
import {
  enrollControlKey,
  hasControlKey,
  signControlChallenge,
  type ControlKeyContext,
  type ControlKeyFailure,
} from '@/lib/screen-share/control-key';
import {
  clipboardRequest,
  decodeClipboard,
  encodeClipboard,
  parseClipboardPayload,
  type ClipboardRefusal,
  type ZstdCodec,
} from '@/lib/screen-share/clipboard';
import {
  displaySwitchEvent,
  dragEvent,
  encodeInputEvent,
  mayInject,
  rightClickEvent,
  scrollEvent,
  shortcutEvent,
  tapEvent,
  textEvent,
  zoomEvent,
  type DragPhase,
  type NormalizedPoint,
  type ScreenInputEvent,
  type ScreenShortcut,
} from '@/lib/screen-share/input';
import type { ScreenPeer, ScreenPeerFactory } from '@/lib/screen-share/peer';
import {
  answerSignal,
  iceSignal,
  type ScreenSignal,
} from '@/lib/screen-share/signalling';
import {
  initialScreenSessionState,
  isScreenSessionActive,
  screenSessionReducer,
  type ScreenEvent,
  type ScreenSessionState,
} from '@/lib/screen-share/session';
import { FULL_REGION, zoomRegionFor, type ZoomRegion } from '@/lib/screen-share/zoom';

/**
 * Drives one host's screen-share session: asks the Mac for state, starts a session, turns
 * signalling into a peer connection, and hands input and the clipboard to the data channel.
 *
 * Nothing here is a permission check. Every grant, every refusal, every stop is the Mac's
 * call; this object mirrors the answers so the screen can say what is going on, and gives
 * up its own session when the Mac could not reach it.
 */

/** Why the phone could not sign a control challenge, as the screen puts it. */
export type ControlStartFailure = ControlKeyFailure | 'no-challenge';

export interface ScreenShareSnapshot {
  session: ScreenSessionState;
  /** What an `RTCView` renders; absent until the Mac's track arrives. */
  streamUrl?: string;
  /** The ICE path the video actually took: `host` on the same Wi-Fi, `relay` over TURN. */
  candidateType?: ScreenCandidateType;
  /** The region the Mac is asked to stream at full resolution. */
  zoom: ZoomRegion;
  /** True while a request to the Mac is in flight. */
  busy: boolean;
  /** Set when signing the control challenge failed on this phone. */
  controlFailure?: ControlStartFailure;
  /** Set when a clipboard transfer was refused. */
  clipboardRefusal?: ClipboardRefusal;
  /** Set after a successful clipboard transfer, so the screen can confirm it. */
  clipboardMoved?: 'to-mac' | 'to-phone';
}

export interface ScreenShareDeps {
  client: Pick<
    MobileClient,
    | 'screenShareState'
    | 'startScreenShare'
    | 'registerScreenControlKey'
    | 'onScreenSignal'
    | 'sendScreenSignal'
  >;
  peerFactory: ScreenPeerFactory;
  controlKey: ControlKeyContext;
  /** Phone clock in ms; injected so idle and background deadlines are testable. */
  now: () => number;
  /** Wi-Fi or mobile data, read when a session starts. */
  network: () => Promise<ScreenNetwork>;
  /** `RTCRtpReceiver.getCapabilities('video')`, or undefined where it is unavailable. */
  decoders: () => DecoderCapabilities | undefined;
  /** Absent on a phone paired before device ids: the Mac refuses it as `legacy-client`. */
  clientId?: string;
  zstd?: ZstdCodec;
  readClipboard: () => Promise<string>;
  writeClipboard: (text: string) => Promise<void>;
}

export interface ScreenShareController {
  snapshot(): ScreenShareSnapshot;
  subscribe(listener: (snapshot: ScreenShareSnapshot) => void): () => void;
  /** Reads `/m1/screen-share/state` and enrols the control key if the grant now allows it. */
  refresh(): Promise<void>;
  start(mode: ScreenMode, displayId?: number): Promise<void>;
  stop(): void;
  /** Lets the idle and background deadlines fire; called from a 1 s timer and from tests. */
  tick(at?: number): void;
  background(): void;
  foreground(): void;
  setZoom(scale: number, centre: NormalizedPoint): void;
  tap(point: NormalizedPoint): void;
  rightClick(point: NormalizedPoint): void;
  drag(point: NormalizedPoint, phase: DragPhase): void;
  scroll(point: NormalizedPoint, delta: { dx: number; dy: number }): void;
  /** One committed string: a typed character, or a whole Korean syllable from the IME. */
  sendText(text: string): void;
  shortcut(combo: ScreenShortcut): void;
  switchDisplay(displayId: number): void;
  /** Manual button: this phone's clipboard to the Mac. */
  pasteToMac(): Promise<void>;
  /** Manual button: the Mac's clipboard to this phone. */
  copyFromMac(): void;
  dispose(): void;
}

function mainDisplayId(displays: readonly ScreenDisplay[]): number | undefined {
  const main = displays.find((display) => display.main);
  return (main ?? displays[0])?.displayId;
}

export function createScreenShareController(deps: ScreenShareDeps): ScreenShareController {
  let snapshot: ScreenShareSnapshot = {
    session: initialScreenSessionState(deps.now()),
    zoom: { ...FULL_REGION },
    busy: false,
  };
  const listeners = new Set<(snapshot: ScreenShareSnapshot) => void>();
  let peer: ScreenPeer | undefined;
  let disposed = false;
  /** Candidates the Mac sent before we had answered its offer. */
  const pendingRemoteCandidates: ScreenSignal[] = [];

  function publish(next: Partial<ScreenShareSnapshot>): void {
    snapshot = { ...snapshot, ...next };
    for (const listener of listeners) listener(snapshot);
  }

  function dispatch(event: ScreenEvent): void {
    const step = screenSessionReducer(snapshot.session, event);
    for (const outgoing of step.outgoing) deps.client.sendScreenSignal(outgoing);
    if (step.closePeer) teardownPeer();
    publish({ session: step.state });
  }

  function teardownPeer(): void {
    peer?.close();
    peer = undefined;
    pendingRemoteCandidates.length = 0;
    publish({ streamUrl: undefined, zoom: { ...FULL_REGION } });
  }

  function send(event: ScreenInputEvent): void {
    if (!peer) return;
    const session = snapshot.session;
    // Input only exists in a live control session. The Mac refuses it anyway; not
    // sending keeps a view-only session from looking as if it might work.
    if (session.phase !== 'live' || !mayInject(session.mode)) return;
    peer.sendData(encodeInputEvent(event));
    dispatch({ kind: 'activity', at: deps.now() });
  }

  function ensurePeer(iceServers: ScreenIceServer[]): ScreenPeer {
    if (peer) return peer;
    peer = deps.peerFactory(
      { iceServers },
      {
        onIceCandidate: (candidate) => {
          const sessionId = snapshot.session.sessionId;
          if (!sessionId) return;
          // Trickle: one candidate per frame, and an empty one to close the list.
          deps.client.sendScreenSignal(
            candidate === null
              ? { type: 'screen-ice', sessionId, candidate: '' }
              : iceSignal(sessionId, candidate),
          );
        },
        onStream: (streamUrl) => {
          publish({ streamUrl });
          // A frame is activity: a screen that keeps moving never idles out.
          dispatch({ kind: 'activity', at: deps.now() });
        },
        onConnectionState: (state) => {
          if (state === 'connected') {
            dispatch({ kind: 'connected', at: deps.now() });
            void peer?.candidateType().then((candidateType) => {
              if (candidateType) publish({ candidateType });
            });
            return;
          }
          if (state === 'failed' || state === 'closed') {
            dispatch({ kind: 'peer-failed', at: deps.now() });
          }
        },
        onData: (message) => {
          const payload = parseClipboardPayload(message);
          if (!payload) return;
          const decoded = decodeClipboard(payload, { codec: deps.zstd });
          if (!decoded.ok) {
            publish({ clipboardRefusal: decoded.refusal });
            return;
          }
          void deps.writeClipboard(decoded.text).then(
            () => publish({ clipboardRefusal: undefined, clipboardMoved: 'to-phone' }),
            () => publish({ clipboardRefusal: 'undecodable' }),
          );
        },
      },
    );
    return peer;
  }

  async function handleOffer(signal: Extract<ScreenSignal, { type: 'screen-offer' }>): Promise<void> {
    if (signal.sessionId !== snapshot.session.sessionId) return;
    dispatch({ kind: 'signal', signal, at: deps.now() });
    const connection = ensurePeer(snapshot.session.iceServers ?? []);
    try {
      const sdp = await connection.answer(signal.sdp, { iceRestart: signal.iceRestart });
      deps.client.sendScreenSignal(answerSignal(signal.sessionId, sdp));
      // Candidates that beat the answer are replayed now that there is a description.
      const queued = pendingRemoteCandidates.splice(0, pendingRemoteCandidates.length);
      for (const candidate of queued) await applyRemoteCandidate(candidate);
    } catch {
      dispatch({ kind: 'peer-failed', at: deps.now() });
    }
  }

  async function applyRemoteCandidate(signal: ScreenSignal): Promise<void> {
    if (signal.type !== 'screen-ice') return;
    if (signal.sessionId !== snapshot.session.sessionId) return;
    if (!peer) {
      pendingRemoteCandidates.push(signal);
      return;
    }
    try {
      if (signal.candidate === '') await peer.endOfRemoteCandidates();
      else {
        const candidate: {
          candidate: string;
          sdpMid?: string;
          sdpMLineIndex?: number;
          usernameFragment?: string;
        } = { candidate: signal.candidate };
        if (signal.sdpMid !== undefined) candidate.sdpMid = signal.sdpMid;
        if (signal.sdpMLineIndex !== undefined) candidate.sdpMLineIndex = signal.sdpMLineIndex;
        if (signal.usernameFragment !== undefined) {
          candidate.usernameFragment = signal.usernameFragment;
        }
        await peer.addIceCandidate(candidate);
      }
    } catch {
      // A candidate the stack will not take is not fatal: ICE keeps trying the others.
    }
  }

  const unsubscribeSignals = deps.client.onScreenSignal((signal) => {
    if (disposed) return;
    if (signal.type === 'screen-offer') {
      void handleOffer(signal);
      return;
    }
    if (signal.type === 'screen-ice') {
      void applyRemoteCandidate(signal);
      return;
    }
    dispatch({ kind: 'signal', signal, at: deps.now() });
  });

  /** Enrols the biometric-gated key the first time the Mac grants control. */
  async function ensureControlKey(): Promise<void> {
    if (snapshot.session.grant !== 'control') return;
    if (await hasControlKey(deps.controlKey)) return;
    try {
      const enrolment = await enrollControlKey(deps.controlKey);
      await deps.client.registerScreenControlKey(enrolment.publicKeyB64);
    } catch {
      // Without a registered key the Mac refuses control with `control-signature`,
      // which the screen already explains; nothing is pretended here.
    }
  }

  return {
    snapshot: () => snapshot,

    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    async refresh() {
      publish({ busy: true });
      try {
        const state = await deps.client.screenShareState();
        dispatch({ kind: 'host-state', state, at: deps.now() });
        await ensureControlKey();
      } finally {
        publish({ busy: false });
      }
    },

    async start(mode, displayId) {
      const target = displayId ?? mainDisplayId(snapshot.session.displays) ?? 0;
      dispatch({ kind: 'requested', mode, displayId: target, at: deps.now() });
      publish({ busy: true, controlFailure: undefined });
      try {
        const decodes = decodableCodecs(deps.decoders());
        const network = await deps.network();
        const request: Parameters<MobileClient['startScreenShare']>[0] = {
          mode,
          displayId: target,
          network,
          decodes,
        };
        if (mode === 'control') {
          const challenge = snapshot.session.controlChallengeB64;
          if (!challenge) {
            publish({ controlFailure: 'no-challenge' });
            dispatch({ kind: 'refused', reason: 'control-signature', at: deps.now() });
            return;
          }
          // The fingerprint prompt happens here, every control session.
          const signature = await signControlChallenge(deps.controlKey, challenge);
          if (!signature.ok) {
            publish({ controlFailure: signature.failure });
            dispatch({ kind: 'refused', reason: 'control-signature', at: deps.now() });
            return;
          }
          request.controlSignatureB64 = signature.signatureB64;
        }
        const session = await deps.client.startScreenShare(request);
        dispatch({ kind: 'accepted', session, at: deps.now() });
        // The codec the Mac chose may differ from what we would have picked; it decides.
        ensurePeer(snapshot.session.iceServers ?? []);
        void preferredCodec(network, decodes);
      } catch (error) {
        const reason = screenRejectReason(error);
        dispatch({ kind: 'refused', reason: reason ?? 'device-not-allowed', at: deps.now() });
      } finally {
        publish({ busy: false });
      }
    },

    stop() {
      dispatch({ kind: 'stop', at: deps.now() });
    },

    tick(at) {
      dispatch({ kind: 'tick', at: at ?? deps.now() });
    },

    background() {
      dispatch({ kind: 'background', at: deps.now() });
    },

    foreground() {
      dispatch({ kind: 'foreground', at: deps.now() });
    },

    setZoom(scale, centre) {
      const region = zoomRegionFor(scale, centre);
      publish({ zoom: region });
      const displayId = snapshot.session.displayId;
      if (displayId === undefined || !peer) return;
      if (snapshot.session.phase !== 'live') return;
      // Zoom is a streaming request, not a crop: the Mac sends this region at full
      // resolution over the low-resolution overview, so terminal text stays readable.
      peer.sendData(encodeInputEvent(zoomEvent(displayId, region)));
    },

    tap(point) {
      const displayId = snapshot.session.displayId;
      if (displayId === undefined) return;
      send(tapEvent(displayId, point));
    },

    rightClick(point) {
      const displayId = snapshot.session.displayId;
      if (displayId === undefined) return;
      send(rightClickEvent(displayId, point));
    },

    drag(point, phase) {
      const displayId = snapshot.session.displayId;
      if (displayId === undefined) return;
      send(dragEvent(displayId, point, phase));
    },

    scroll(point, delta) {
      const displayId = snapshot.session.displayId;
      if (displayId === undefined) return;
      send(scrollEvent(displayId, point, delta));
    },

    sendText(text) {
      const event = textEvent(text);
      if (event) send(event);
    },

    shortcut(combo) {
      send(shortcutEvent(combo));
    },

    switchDisplay(displayId) {
      if (!peer || snapshot.session.phase !== 'live') return;
      peer.sendData(encodeInputEvent(displaySwitchEvent(displayId)));
      publish({ zoom: { ...FULL_REGION } });
    },

    async pasteToMac() {
      const session = snapshot.session;
      if (session.phase !== 'live' || !peer) return;
      const text = await deps.readClipboard();
      const encoded = encodeClipboard(text, { mode: session.mode, codec: deps.zstd });
      if (!encoded.ok) {
        publish({ clipboardRefusal: encoded.refusal, clipboardMoved: undefined });
        return;
      }
      peer.sendData(JSON.stringify(encoded.payload));
      publish({ clipboardRefusal: undefined, clipboardMoved: 'to-mac' });
      dispatch({ kind: 'activity', at: deps.now() });
    },

    copyFromMac() {
      const session = snapshot.session;
      if (session.phase !== 'live' || !peer) return;
      if (!mayInject(session.mode)) {
        publish({ clipboardRefusal: 'not-control', clipboardMoved: undefined });
        return;
      }
      peer.sendData(JSON.stringify(clipboardRequest()));
    },

    dispose() {
      disposed = true;
      unsubscribeSignals();
      if (isScreenSessionActive(snapshot.session)) dispatch({ kind: 'stop', at: deps.now() });
      teardownPeer();
      listeners.clear();
    },
  };
}
