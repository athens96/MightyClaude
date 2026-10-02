import { ApiError, screenRejectReason, type MobileClient } from '@/api/client';
import type {
  ScreenCandidateType,
  ScreenDisplay,
  ScreenIceServer,
  ScreenMode,
  ScreenNetwork,
  ScreenSessionResponse,
} from '@/api/types';
import { decodableCodecs, type DecoderCapabilities } from '@/lib/screen-share/quality';
import {
  controlKeyFailureFor,
  controlKeyFingerprint,
  controlKeyStatus,
  controlKeySupported,
  createControlKey,
  forgetControlKey,
  mayEnrolControlKey,
  readControlPublicKey,
  signControlChallenge,
  type ControlKeyContext,
  type ControlKeyFailure,
  type ControlKeyStatus,
} from '@/lib/screen-share/control-key';
import {
  CLIPBOARD_ASSEMBLY_TIMEOUT_MS,
  ClipboardAssembler,
  DATA_CHANNEL_MAX_BYTES,
  clipboardFrames,
  clipboardRequest,
  decodeClipboardPayload,
  packClipboard,
  parseClipboardFrame,
  utf8Length,
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
  sessionEndSignal,
  type ScreenSignal,
} from '@/lib/screen-share/signalling';
import {
  initialScreenSessionState,
  isScreenSessionActive,
  mayRequestSession,
  screenSessionReducer,
  type ScreenEvent,
  type ScreenSessionState,
} from '@/lib/screen-share/session';
import { readStatsSample } from '@/lib/screen-share/stats';
import {
  FULL_REGION,
  clampZoomScale,
  zoomRegionFor,
  type ZoomRegion,
} from '@/lib/screen-share/zoom';

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

/** Why registering the control key with the Mac did not work. */
export type ControlKeyEnrolFailure =
  | 'control-key-present'
  | 'control-key-pending'
  | 'control-key-not-confirmed'
  | 'insufficient-grant'
  | 'device-not-allowed'
  | 'no-authenticator'
  | 'keystore'
  | 'failed';

export interface ControlKeySnapshot {
  status: ControlKeyStatus;
  /** This phone's key fingerprint, the one the Mac's user is asked to compare. */
  phoneFingerprint?: string;
  /** True while the Mac's user is being asked to confirm the fingerprint. */
  enrolling: boolean;
  enrolFailure?: ControlKeyEnrolFailure;
}

export interface ScreenShareSnapshot {
  session: ScreenSessionState;
  /** What an `RTCView` renders for the `screen` track; absent until it arrives. */
  streamUrl?: string;
  /** The `overview` track: the whole display, small, laid under a zoom. */
  overviewUrl?: string;
  /** The ICE path the video actually took: `host` on the same Wi-Fi, `relay` over TURN. */
  candidateType?: ScreenCandidateType;
  /** The region the Mac is asked to stream at full resolution. */
  zoom: ZoomRegion;
  /** The pinch scale behind `zoom`, kept so the next pinch starts from it. */
  zoomScale: number;
  /** The display the next session starts on, picked while nothing is running. */
  selectedDisplayId?: number;
  /** True while a request to the Mac is in flight. */
  busy: boolean;
  /** Set when reading the Mac's screen-share state failed. */
  loadFailed: boolean;
  /** Set when signing the control challenge failed on this phone. */
  controlFailure?: ControlStartFailure;
  controlKey: ControlKeySnapshot;
  /** Set when a clipboard transfer was refused. */
  clipboardRefusal?: ClipboardRefusal | 'no-reply';
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
  zstd?: ZstdCodec;
  readClipboard: () => Promise<string>;
  writeClipboard: (text: string) => Promise<void>;
}

export interface ScreenShareController {
  snapshot(): ScreenShareSnapshot;
  subscribe(listener: (snapshot: ScreenShareSnapshot) => void): () => void;
  /** Reads `/m1/screen-share/state` and where the control key stands. Never prompts. */
  refresh(): Promise<void>;
  /** Makes (if needed) and registers the control key; the Mac's user confirms it. */
  enrolControlKey(): Promise<void>;
  /** Only from rest (`idle` or `ended`); ignored while a session is starting or live. */
  start(mode: ScreenMode, displayId?: number): Promise<void>;
  stop(): void;
  /** Lets the deadlines fire and samples frames; called from a 1 s timer and from tests. */
  tick(): void;
  background(): void;
  foreground(): void;
  setZoom(scale: number, centre: NormalizedPoint): void;
  resetZoom(): void;
  tap(point: NormalizedPoint): boolean;
  rightClick(point: NormalizedPoint): void;
  drag(point: NormalizedPoint, phase: DragPhase): void;
  scroll(point: NormalizedPoint, delta: { dx: number; dy: number }): void;
  /** One committed string: a typed character, or a whole Korean syllable from the IME. */
  sendText(text: string): void;
  shortcut(combo: ScreenShortcut): void;
  /** Live: switches the Mac's capture. At rest: picks the display the next start uses. */
  selectDisplay(displayId: number): void;
  /** Manual button: this phone's clipboard to the Mac. */
  pasteToMac(): Promise<void>;
  /** Manual button: the Mac's clipboard to this phone. */
  copyFromMac(): void;
  /** The live peer connection's raw `getStats`, for the measurement overlay. */
  statsReport(): Promise<unknown>;
  dispose(): void;
}

/** How often a live session samples `getStats` to see whether frames still arrive. */
export const FRAME_PROBE_MS = 5_000;

const ENROL_FAILURES: readonly ControlKeyEnrolFailure[] = [
  'control-key-present',
  'control-key-pending',
  'control-key-not-confirmed',
  'insufficient-grant',
  'device-not-allowed',
];

function enrolFailureFor(error: unknown): ControlKeyEnrolFailure {
  if (error instanceof ApiError) {
    const code = error.code;
    return (ENROL_FAILURES as readonly string[]).includes(code ?? '')
      ? (code as ControlKeyEnrolFailure)
      : 'failed';
  }
  if (error !== null && typeof error === 'object' && 'code' in error) {
    const failure = controlKeyFailureFor(error);
    return failure === 'no-authenticator' ? 'no-authenticator' : 'keystore';
  }
  return 'failed';
}

function mainDisplayId(displays: readonly ScreenDisplay[]): number | undefined {
  const main = displays.find((display) => display.main);
  return (main ?? displays[0])?.displayId;
}

/** Settles with the request, or rejects as soon as the signal aborts. */
function untilAborted<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error('aborted'));
      return;
    }
    signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
    work.then(resolve, reject);
  });
}

export function createScreenShareController(deps: ScreenShareDeps): ScreenShareController {
  let snapshot: ScreenShareSnapshot = {
    session: initialScreenSessionState(deps.now()),
    zoom: { ...FULL_REGION },
    zoomScale: 1,
    busy: false,
    loadFailed: false,
    controlKey: {
      status: controlKeySupported(deps.controlKey) ? 'not-needed' : 'unsupported',
      enrolling: false,
    },
  };
  const listeners = new Set<(snapshot: ScreenShareSnapshot) => void>();
  let disposed = false;

  let peer: ScreenPeer | undefined;
  /** Bumped for every new and every closed peer: an old peer's callbacks see a stale token. */
  let peerToken = 0;
  /** False until the current offer is answered; remote candidates wait for it. */
  let remoteDescriptionReady = false;
  /** Candidates the Mac sent before we had answered its offer. */
  const pendingRemoteCandidates: ScreenSignal[] = [];

  /** Bumped per start; a reply for an older attempt is never applied. */
  let startAttempt = 0;
  let startAbort: AbortController | undefined;
  let inFlight = 0;

  /** This phone's control public key, as last read from the Keystore. */
  let phonePublicKey: string | undefined;

  /** Set by the 가져오기 button; a `to-phone` transfer is only written while it is. */
  let pendingCopyAt: number | undefined;
  const assembler = new ClipboardAssembler();
  let transferCount = 0;

  let lastFrameProbeAt = 0;
  let lastFramesDecoded = 0;

  function publish(next: Partial<ScreenShareSnapshot>): void {
    snapshot = { ...snapshot, ...next };
    for (const listener of listeners) listener(snapshot);
  }

  function setBusy(delta: number): void {
    inFlight = Math.max(0, inFlight + delta);
    publish({ busy: inFlight > 0 });
  }

  function publishKey(next: Partial<ControlKeySnapshot> = {}): void {
    const phoneFingerprint = phonePublicKey ? controlKeyFingerprint(phonePublicKey) : undefined;
    const status = controlKeyStatus({
      supported: controlKeySupported(deps.controlKey),
      grantIsControl: snapshot.session.allowed && snapshot.session.grant === 'control',
      phoneFingerprint,
      hostFingerprint: snapshot.session.controlKeyFingerprint,
    });
    const key: ControlKeySnapshot = { ...snapshot.controlKey, ...next, status };
    if (phoneFingerprint) key.phoneFingerprint = phoneFingerprint;
    else delete key.phoneFingerprint;
    publish({ controlKey: key });
  }

  function dispatch(event: ScreenEvent): void {
    const before = snapshot.session;
    const step = screenSessionReducer(before, event);
    for (const outgoing of step.outgoing) deps.client.sendScreenSignal(outgoing);
    if (step.closePeer) teardownPeer();
    if (!isScreenSessionActive(step.state)) {
      pendingCopyAt = undefined;
      assembler.reset();
    }
    publish({ session: step.state });
  }

  function teardownPeer(): void {
    peer?.close();
    peer = undefined;
    peerToken += 1;
    remoteDescriptionReady = false;
    pendingRemoteCandidates.length = 0;
    lastFramesDecoded = 0;
    publish({
      streamUrl: undefined,
      overviewUrl: undefined,
      zoom: { ...FULL_REGION },
      zoomScale: 1,
    });
  }

  function live(): boolean {
    return snapshot.session.phase === 'live' && peer !== undefined;
  }

  function sendData(payload: string): boolean {
    if (!peer) return false;
    return peer.sendData(payload);
  }

  function send(event: ScreenInputEvent | undefined): boolean {
    if (!event || !live()) return false;
    // Input only exists in a live control session. The Mac refuses it anyway; not
    // sending keeps a view-only session from looking as if it might work.
    if (!mayInject(snapshot.session.mode)) return false;
    const sent = sendData(encodeInputEvent(event));
    if (sent) dispatch({ kind: 'activity', at: deps.now() });
    return sent;
  }

  function ensurePeer(iceServers: ScreenIceServer[]): ScreenPeer {
    if (peer) return peer;
    peerToken += 1;
    const token = peerToken;
    const current = () => token === peerToken && !disposed;
    peer = deps.peerFactory(
      { iceServers },
      {
        onIceCandidate: (candidate) => {
          if (!current()) return;
          const sessionId = snapshot.session.sessionId;
          if (!sessionId) return;
          // Trickle: one candidate per frame, and an empty one to close the list.
          deps.client.sendScreenSignal(
            candidate === null
              ? { type: 'screen-ice', sessionId, candidate: '' }
              : iceSignal(sessionId, candidate),
          );
        },
        onStream: (streamUrl, track) => {
          if (!current()) return;
          if (track === 'overview') publish({ overviewUrl: streamUrl });
          else publish({ streamUrl });
          // A frame is activity: a screen that keeps moving never idles out.
          if (streamUrl) dispatch({ kind: 'activity', at: deps.now() });
        },
        onConnectionState: (state) => {
          if (!current()) return;
          if (state === 'connected') {
            dispatch({ kind: 'connected', at: deps.now() });
            void probeStats(token);
            return;
          }
          if (state === 'failed' || state === 'closed') {
            dispatch({ kind: 'peer-failed', at: deps.now() });
          }
        },
        onData: (message) => {
          if (!current()) return;
          receiveClipboard(message);
        },
      },
    );
    return peer;
  }

  /** Reads `getStats` once: the ICE path, and whether frames are still arriving. */
  async function probeStats(token: number): Promise<void> {
    const connection = peer;
    if (!connection) return;
    let report: unknown;
    try {
      report = await connection.stats();
    } catch {
      return;
    }
    if (token !== peerToken || disposed) return;
    const sample = readStatsSample(report, deps.now());
    if (sample.candidateType && sample.candidateType !== snapshot.candidateType) {
      publish({ candidateType: sample.candidateType });
    }
    if (sample.framesDecoded > lastFramesDecoded) {
      lastFramesDecoded = sample.framesDecoded;
      dispatch({ kind: 'activity', at: deps.now() });
    }
  }

  function receiveClipboard(message: unknown): void {
    const frame = parseClipboardFrame(message);
    if (!frame) return;
    // Only an answer to the 가져오기 button is ever written to this phone's clipboard.
    if (pendingCopyAt === undefined) return;
    const outcome = assembler.accept(frame, deps.now());
    if (outcome.kind === 'waiting') return;
    pendingCopyAt = undefined;
    if (outcome.kind === 'concealed') {
      publish({ clipboardRefusal: 'concealed', clipboardMoved: undefined });
      return;
    }
    if (outcome.kind === 'rejected') {
      publish({ clipboardRefusal: outcome.refusal, clipboardMoved: undefined });
      return;
    }
    const decoded = decodeClipboardPayload(
      { enc: outcome.enc, bytes: outcome.bytes, payload: outcome.payload },
      { codec: deps.zstd },
    );
    if (!decoded.ok) {
      publish({ clipboardRefusal: decoded.refusal, clipboardMoved: undefined });
      return;
    }
    void deps.writeClipboard(decoded.text).then(
      () => publish({ clipboardRefusal: undefined, clipboardMoved: 'to-phone' }),
      () => publish({ clipboardRefusal: 'undecodable', clipboardMoved: undefined }),
    );
  }

  async function handleOffer(signal: Extract<ScreenSignal, { type: 'screen-offer' }>): Promise<void> {
    if (signal.sessionId !== snapshot.session.sessionId) return;
    if (!isScreenSessionActive(snapshot.session)) return;
    dispatch({ kind: 'signal', signal, at: deps.now() });
    const connection = ensurePeer(snapshot.session.iceServers ?? []);
    const token = peerToken;
    remoteDescriptionReady = false;
    try {
      const options: { iceRestart: boolean; iceServers?: ScreenIceServer[] } = {
        iceRestart: signal.iceRestart,
      };
      if (snapshot.session.iceServers) options.iceServers = snapshot.session.iceServers;
      const sdp = await connection.answer(signal.sdp, options);
      if (token !== peerToken || disposed) return;
      deps.client.sendScreenSignal(answerSignal(signal.sessionId, sdp));
      remoteDescriptionReady = true;
      // Candidates that beat the answer are replayed now that there is a description.
      const queued = pendingRemoteCandidates.splice(0, pendingRemoteCandidates.length);
      for (const candidate of queued) await applyRemoteCandidate(candidate);
    } catch {
      if (token !== peerToken || disposed) return;
      dispatch({ kind: 'peer-failed', at: deps.now() });
    }
  }

  async function applyRemoteCandidate(signal: ScreenSignal): Promise<void> {
    if (signal.type !== 'screen-ice') return;
    if (signal.sessionId !== snapshot.session.sessionId) return;
    if (!peer || !remoteDescriptionReady) {
      pendingRemoteCandidates.push(signal);
      return;
    }
    const connection = peer;
    try {
      if (signal.candidate === '') await connection.endOfRemoteCandidates();
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
        await connection.addIceCandidate(candidate);
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
    if (signal.type === 'screen-grant') void syncControlKey();
  });

  /**
   * Where this phone's key stands, from the stored public key alone — no prompt. A grant
   * that is no longer `control` drops the key: the Mac drops its copy at the same moment.
   */
  async function syncControlKey(): Promise<void> {
    if (!controlKeySupported(deps.controlKey)) {
      publishKey();
      return;
    }
    phonePublicKey = await readControlPublicKey(deps.controlKey);
    if (disposed) return;
    if (phonePublicKey && snapshot.session.grant !== 'control') {
      await forgetControlKey(deps.controlKey);
      if (disposed) return;
      phonePublicKey = undefined;
    }
    publishKey();
  }

  async function readHostState(): Promise<boolean> {
    try {
      const state = await deps.client.screenShareState();
      if (disposed) return false;
      dispatch({ kind: 'host-state', state, at: deps.now() });
      publish({ loadFailed: false });
      await syncControlKey();
      return !disposed;
    } catch {
      if (!disposed) publish({ loadFailed: true });
      return false;
    }
  }

  async function refusedControl(failure: ControlStartFailure): Promise<void> {
    publish({ controlFailure: failure });
    dispatch({ kind: 'refused', reason: 'control-signature', at: deps.now() });
    if (failure === 'key-invalidated') {
      // The Keystore threw this key away for good. Dropping our marker turns the screen
      // to "register again"; nothing is enrolled until the user asks for it.
      await forgetControlKey(deps.controlKey);
      phonePublicKey = undefined;
      if (!disposed) publishKey();
    }
  }

  const api: ScreenShareController = {
    snapshot: () => snapshot,

    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    async refresh() {
      if (disposed) return;
      setBusy(1);
      try {
        await readHostState();
      } finally {
        if (!disposed) setBusy(-1);
      }
    },

    async enrolControlKey() {
      if (disposed || snapshot.controlKey.enrolling) return;
      if (!mayEnrolControlKey(snapshot.controlKey.status)) return;
      publishKey({ enrolling: true, enrolFailure: undefined });
      let fingerprint: string | undefined;
      try {
        // An existing key is registered again (the Mac forgot it); only a phone with no
        // key makes one. Neither step prompts — signing is the only thing that does.
        let publicKeyB64 = await readControlPublicKey(deps.controlKey);
        if (!publicKeyB64) publicKeyB64 = (await createControlKey(deps.controlKey)).publicKeyB64;
        if (disposed) return;
        phonePublicKey = publicKeyB64;
        publishKey({ enrolling: true });
        const reply = await deps.client.registerScreenControlKey(publicKeyB64);
        if (disposed) return;
        fingerprint = reply.fingerprint;
        publishKey({ enrolling: false });
      } catch (error) {
        if (disposed) return;
        publishKey({ enrolling: false, enrolFailure: enrolFailureFor(error) });
        return;
      }
      if (fingerprint) {
        publish({ session: { ...snapshot.session, controlKeyFingerprint: fingerprint } });
        publishKey();
      }
      await api.refresh();
    },

    async start(mode, displayId) {
      if (disposed || !mayRequestSession(snapshot.session)) return;
      const target =
        displayId ??
        snapshot.selectedDisplayId ??
        mainDisplayId(snapshot.session.displays) ??
        0;
      startAttempt += 1;
      const attempt = startAttempt;
      startAbort?.abort();
      const abort = new AbortController();
      startAbort = abort;
      // Stopped, killed, refused, timed out, superseded or disposed: nothing that comes
      // back for this attempt may bring the session back.
      const stale = () =>
        disposed ||
        attempt !== startAttempt ||
        abort.signal.aborted ||
        snapshot.session.phase !== 'starting';

      dispatch({ kind: 'requested', mode, displayId: target, at: deps.now() });
      publish({ controlFailure: undefined });
      setBusy(1);
      try {
        const decodes = decodableCodecs(deps.decoders());
        const network = await deps.network();
        if (stale()) return;
        const request: Parameters<MobileClient['startScreenShare']>[0] = {
          mode,
          displayId: target,
          network,
          decodes,
        };
        if (mode === 'control') {
          // A challenge is good for one attempt and two minutes, so each control start
          // reads a fresh one. Still no prompt: that comes with the signature.
          const fresh = await readHostState();
          if (!fresh || stale()) {
            if (!stale()) dispatch({ kind: 'refused', reason: 'failed', at: deps.now() });
            return;
          }
          if (snapshot.controlKey.status !== 'ready') {
            await refusedControl(
              snapshot.controlKey.status === 'unsupported' ? 'unsupported' : 'not-enrolled',
            );
            return;
          }
          const challenge = snapshot.session.controlChallengeB64;
          if (!challenge) {
            await refusedControl('no-challenge');
            return;
          }
          // The fingerprint (or PIN) prompt happens here, every control session.
          const signature = await signControlChallenge(deps.controlKey, challenge);
          if (stale()) return;
          if (!signature.ok) {
            await refusedControl(signature.failure);
            return;
          }
          request.controlSignatureB64 = signature.signatureB64;
        }

        dispatch({ kind: 'request-sent', at: deps.now() });
        const inflight = deps.client.startScreenShare(request);
        let session: ScreenSessionResponse;
        try {
          session = await untilAborted(inflight, abort.signal);
        } catch (error) {
          if (abort.signal.aborted) {
            // Stopped or left while the Mac was still answering: if it does open the
            // session, tell it at once that nobody is coming.
            inflight.then(
              (late) => deps.client.sendScreenSignal(sessionEndSignal(late.sessionId, 'user-stop')),
              () => undefined,
            );
            return;
          }
          if (stale()) return;
          dispatch({ kind: 'refused', reason: screenRejectReason(error) ?? 'failed', at: deps.now() });
          return;
        }
        if (stale()) {
          deps.client.sendScreenSignal(sessionEndSignal(session.sessionId, 'user-stop'));
          return;
        }
        dispatch({ kind: 'accepted', session, at: deps.now() });
        // The Mac answers with the codec it actually chose, which may not be the one this
        // phone would have picked; it is the Mac's call, so nothing here second-guesses it.
        ensurePeer(snapshot.session.iceServers ?? []);
      } finally {
        if (attempt === startAttempt) startAbort = undefined;
        if (!disposed) setBusy(-1);
      }
    },

    stop() {
      startAbort?.abort();
      dispatch({ kind: 'stop', at: deps.now() });
    },

    tick() {
      const at = deps.now();
      dispatch({ kind: 'tick', at });
      if (pendingCopyAt !== undefined && at - pendingCopyAt > CLIPBOARD_ASSEMBLY_TIMEOUT_MS) {
        pendingCopyAt = undefined;
        assembler.reset();
        publish({ clipboardRefusal: 'no-reply', clipboardMoved: undefined });
      }
      if (live() && at - lastFrameProbeAt >= FRAME_PROBE_MS) {
        lastFrameProbeAt = at;
        void probeStats(peerToken);
      }
    },

    background() {
      dispatch({ kind: 'background', at: deps.now() });
    },

    foreground() {
      dispatch({ kind: 'foreground', at: deps.now() });
    },

    setZoom(scale, centre) {
      const zoomScale = clampZoomScale(scale);
      const region = zoomRegionFor(zoomScale, centre);
      const displayId = snapshot.session.displayId;
      if (displayId === undefined || !live()) return;
      publish({ zoom: region, zoomScale: region.width < 1 ? zoomScale : 1 });
      // Zoom is a streaming request, not a crop: the Mac sends this region at full
      // resolution over the low-resolution overview, so terminal text stays readable.
      // A view-only phone may ask too; the Mac only follows it when nobody is driving.
      sendData(encodeInputEvent(zoomEvent(displayId, region)));
    },

    resetZoom() {
      api.setZoom(1, { x: 0.5, y: 0.5 });
    },

    tap(point) {
      const displayId = snapshot.session.displayId;
      if (displayId === undefined) return false;
      return send(tapEvent(displayId, point));
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
      send(textEvent(text));
    },

    shortcut(combo) {
      send(shortcutEvent(combo));
    },

    selectDisplay(displayId) {
      const phase = snapshot.session.phase;
      if (phase === 'live') {
        if (!peer) return;
        if (sendData(encodeInputEvent(displaySwitchEvent(displayId)))) {
          publish({ zoom: { ...FULL_REGION }, zoomScale: 1 });
        }
        return;
      }
      // Nothing to switch mid-start: the display is chosen before a session, or live.
      if (phase === 'idle' || phase === 'ended') publish({ selectedDisplayId: displayId });
    },

    async pasteToMac() {
      if (!live()) return;
      const connection = peer;
      const text = await deps.readClipboard();
      if (disposed || peer !== connection || !live()) return;
      const packed = packClipboard(text, { mode: snapshot.session.mode, codec: deps.zstd });
      if (!packed.ok) {
        publish({ clipboardRefusal: packed.refusal, clipboardMoved: undefined });
        return;
      }
      transferCount += 1;
      const id = `p${deps.now().toString(36)}-${transferCount}`;
      let sent = true;
      for (const frame of clipboardFrames(id, packed.packet)) {
        const message = JSON.stringify(frame);
        if (utf8Length(message) > DATA_CHANNEL_MAX_BYTES || !sendData(message)) {
          sent = false;
          break;
        }
      }
      if (!sent) {
        publish({ clipboardRefusal: 'not-sent', clipboardMoved: undefined });
        return;
      }
      publish({ clipboardRefusal: undefined, clipboardMoved: 'to-mac' });
      dispatch({ kind: 'activity', at: deps.now() });
    },

    copyFromMac() {
      if (!live()) return;
      if (!mayInject(snapshot.session.mode)) {
        publish({ clipboardRefusal: 'not-control', clipboardMoved: undefined });
        return;
      }
      if (!sendData(JSON.stringify(clipboardRequest()))) {
        publish({ clipboardRefusal: 'not-sent', clipboardMoved: undefined });
        return;
      }
      pendingCopyAt = deps.now();
      assembler.reset();
      publish({ clipboardRefusal: undefined, clipboardMoved: undefined });
    },

    async statsReport() {
      if (!peer) return undefined;
      try {
        return await peer.stats();
      } catch {
        return undefined;
      }
    },

    dispose() {
      if (disposed) return;
      startAbort?.abort();
      if (isScreenSessionActive(snapshot.session)) dispatch({ kind: 'stop', at: deps.now() });
      disposed = true;
      unsubscribeSignals();
      teardownPeer();
      listeners.clear();
    },
  };
  return api;
}
