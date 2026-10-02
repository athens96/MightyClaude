import {
  SCREEN_BACKGROUND_GRACE_SECONDS,
  type ScreenCodec,
  SCREEN_IDLE_TIMEOUT_CONTROL_SECONDS,
  SCREEN_IDLE_TIMEOUT_VIEW_SECONDS,
  type ScreenDisplay,
  type ScreenEndReason,
  type ScreenGrant,
  type ScreenIceServer,
  type ScreenKillReason,
  type ScreenMode,
  type ScreenQuality,
  type ScreenRejectReason,
  type ScreenShareState,
  type ScreenSessionResponse,
} from '@/api/types';
import {
  backgroundSignal,
  killHitsSession,
  sessionEndSignal,
  type ScreenSignal,
} from '@/lib/screen-share/signalling';

/**
 * The phone's side of one screen-share session, as a pure reducer. The Mac is the one
 * that enforces every rule — it stops capture and closes the peer connection itself —
 * so this machine exists to keep the screen honest about what is happening and to let
 * the phone drop its own session when the Mac could not reach it.
 */

export type ScreenPhase = 'idle' | 'starting' | 'connecting' | 'live' | 'ended';

/** Why nothing is on screen even though the grant is in place. */
export type ScreenHold = 'screen-permission' | 'lock-screen' | 'secure-input';

/** How a session finished: our own reasons, the Mac's, or a kill. */
export type ScreenStopReason = ScreenEndReason | ScreenKillReason;

/** Why a start did not happen: the Mac's own reason, or `failed` when it gave none. */
export type ScreenRefusal = ScreenRejectReason | 'failed';

/**
 * How long `starting` (from the moment the request leaves) and `connecting` may each last.
 * The Mac gives up on a connection after 30 s; this is the phone's own ceiling on top of
 * that, so a reply or an offer that never comes cannot leave the screen spinning.
 */
export const SCREEN_CONNECT_DEADLINE_MS = 45_000;

export interface ScreenSessionState {
  phase: ScreenPhase;
  /** The Mac's allow-list flag for this phone. */
  allowed: boolean;
  grant: ScreenGrant;
  /** The mode that was asked for, and once live the mode the Mac granted. */
  mode: ScreenMode;
  sessionId?: string;
  displayId?: number;
  /** The codec the Mac chose. */
  codec?: ScreenCodec;
  quality?: ScreenQuality;
  displays: ScreenDisplay[];
  controlChallengeB64?: string;
  iceServers?: ScreenIceServer[];
  /** Why the picture is held back rather than ended for good. */
  hold?: ScreenHold;
  stopReason?: ScreenStopReason;
  /** Why the Mac refused the last request. */
  refusal?: ScreenRefusal;
  /** The fingerprint of the control key the Mac stores for this phone, if any. */
  controlKeyFingerprint?: string;
  idleTimeoutSeconds: number;
  /** Phone clock, ms, when `starting` or `connecting` began; the deadline counts from it. */
  phaseStartedAt: number;
  /** Phone clock, ms. Reset by any input or any frame. */
  lastActivityAt: number;
  /** Phone clock, ms, from the moment the app left the foreground. */
  backgroundSince?: number;
}

export type ScreenEvent =
  /** `GET /m1/screen-share/state` came back. */
  | { kind: 'host-state'; state: ScreenShareState; at: number }
  /** The phone asked for a session. */
  | { kind: 'requested'; mode: ScreenMode; displayId: number; at: number }
  /** The request left for the Mac (after any fingerprint prompt). */
  | { kind: 'request-sent'; at: number }
  /** `POST /m1/screen-share/sessions` was accepted. */
  | { kind: 'accepted'; session: ScreenSessionResponse; at: number }
  /** The Mac refused with a reason, or the request failed without one. */
  | { kind: 'refused'; reason: ScreenRefusal; at: number }
  /** One decrypted signalling frame. */
  | { kind: 'signal'; signal: ScreenSignal; at: number }
  /** The peer connection reached `connected`. */
  | { kind: 'connected'; at: number }
  /** A frame arrived, or the user touched the screen. */
  | { kind: 'activity'; at: number }
  /** The peer connection failed or closed under us. */
  | { kind: 'peer-failed'; at: number }
  /** The user tapped 중지. */
  | { kind: 'stop'; at: number }
  | { kind: 'background'; at: number }
  | { kind: 'foreground'; at: number }
  /** A timer tick; the only thing that lets idle and background deadlines fire. */
  | { kind: 'tick'; at: number };

export interface ScreenTransition {
  state: ScreenSessionState;
  /** Frames to put on the E2EE channel, in order. */
  outgoing: ScreenSignal[];
  /** True when the peer connection and its data channel must be torn down now. */
  closePeer: boolean;
}

export function initialScreenSessionState(at = 0): ScreenSessionState {
  return {
    phase: 'idle',
    allowed: false,
    grant: 'none',
    mode: 'view',
    displays: [],
    idleTimeoutSeconds: SCREEN_IDLE_TIMEOUT_VIEW_SECONDS,
    lastActivityAt: at,
    phaseStartedAt: at,
  };
}

/** A new session may only be asked for from rest: never while one is starting or live. */
export function mayRequestSession(state: ScreenSessionState): boolean {
  return state.phase === 'idle' || state.phase === 'ended';
}

/** True while a session is worth tearing down. */
export function isScreenSessionActive(state: ScreenSessionState): boolean {
  return state.phase === 'starting' || state.phase === 'connecting' || state.phase === 'live';
}

/** The contract's idle ceiling for a mode, unless the Mac named its own. */
export function idleTimeoutSecondsFor(mode: ScreenMode, hostValue?: number): number {
  if (typeof hostValue === 'number' && Number.isFinite(hostValue) && hostValue > 0) {
    return Math.floor(hostValue);
  }
  return mode === 'control'
    ? SCREEN_IDLE_TIMEOUT_CONTROL_SECONDS
    : SCREEN_IDLE_TIMEOUT_VIEW_SECONDS;
}

/** A lock screen or a secure input field holds the picture back; it is not a failure. */
export function holdFor(
  reason: ScreenStopReason | ScreenRefusal | undefined,
): ScreenHold | undefined {
  if (reason === 'lock-screen' || reason === 'secure-input') return reason;
  if (reason === 'screen-permission') return reason;
  return undefined;
}

/** The reasons that mean "the Mac itself stopped everything", not "we hung up". */
export function wasStoppedByHost(reason: ScreenStopReason | undefined): boolean {
  return (
    reason === 'revoked' ||
    reason === 'grant-downgrade' ||
    reason === 'rekey-pairing' ||
    reason === 'kill-switch' ||
    reason === 'lock-screen' ||
    reason === 'secure-input' ||
    reason === 'concurrency-limit'
  );
}

function unchanged(state: ScreenSessionState): ScreenTransition {
  return { state, outgoing: [], closePeer: false };
}

/** Ends the session locally and, when we can still speak, tells the Mac why. */
function end(
  state: ScreenSessionState,
  reason: ScreenStopReason,
  options: { notify: boolean } = { notify: false },
): ScreenTransition {
  const outgoing: ScreenSignal[] = [];
  const active = isScreenSessionActive(state);
  if (options.notify && active && state.sessionId && isOwnReason(reason)) {
    outgoing.push(sessionEndSignal(state.sessionId, reason));
  }
  const next: ScreenSessionState = {
    ...state,
    phase: 'ended',
    stopReason: reason,
    backgroundSince: undefined,
  };
  const hold = holdFor(reason);
  if (hold) next.hold = hold;
  else delete next.hold;
  return { state: next, outgoing, closePeer: true };
}

/** The three reasons the phone itself may report with `screen-session-end`. */
function isOwnReason(reason: ScreenStopReason): reason is ScreenEndReason {
  return reason === 'user-stop' || reason === 'background' || reason === 'peer-failed';
}

function applyHostState(
  state: ScreenSessionState,
  host: ScreenShareState,
  at: number,
): ScreenTransition {
  const next: ScreenSessionState = {
    ...state,
    allowed: host.allowed,
    grant: host.grant,
    displays: host.displays,
    idleTimeoutSeconds: idleTimeoutSecondsFor(state.mode, host.idleTimeoutSeconds),
  };
  if (host.controlChallengeB64) next.controlChallengeB64 = host.controlChallengeB64;
  else delete next.controlChallengeB64;
  if (host.controlKeyFingerprint) next.controlKeyFingerprint = host.controlKeyFingerprint;
  else delete next.controlKeyFingerprint;
  if (host.iceServers) next.iceServers = host.iceServers;
  // A state read during a session must not shorten or lengthen the live session's ceiling.
  if (isScreenSessionActive(state)) next.idleTimeoutSeconds = state.idleTimeoutSeconds;
  return applyGrant(next, host.allowed, host.grant);
}

/**
 * A grant that no longer covers the live session ends it. Withdrawal and a device coming
 * off the allow-list read as `revoked`; a control session whose grant fell back to
 * view-only reads as `grant-downgrade`, the same words the Mac uses.
 */
function applyGrant(
  state: ScreenSessionState,
  allowed: boolean,
  grant: ScreenGrant,
): ScreenTransition {
  if (!isScreenSessionActive(state)) return unchanged(state);
  if (!allowed || grant === 'none') return end(state, 'revoked');
  if (state.mode === 'control' && grant !== 'control') return end(state, 'grant-downgrade');
  return unchanged(state);
}

export function screenSessionReducer(
  state: ScreenSessionState,
  event: ScreenEvent,
): ScreenTransition {
  switch (event.kind) {
    case 'host-state':
      return applyHostState(state, event.state, event.at);

    case 'requested': {
      if (!mayRequestSession(state)) return unchanged(state);
      const next: ScreenSessionState = {
        ...state,
        phase: 'starting',
        mode: event.mode,
        displayId: event.displayId,
        idleTimeoutSeconds: idleTimeoutSecondsFor(event.mode),
        lastActivityAt: event.at,
        phaseStartedAt: event.at,
        backgroundSince: undefined,
      };
      delete next.stopReason;
      delete next.refusal;
      delete next.hold;
      delete next.sessionId;
      return { state: next, outgoing: [], closePeer: false };
    }

    case 'request-sent': {
      if (state.phase !== 'starting') return unchanged(state);
      return { state: { ...state, phaseStartedAt: event.at }, outgoing: [], closePeer: false };
    }

    case 'accepted': {
      // A reply for a start the user stopped, or one a kill ended, revives nothing.
      if (state.phase !== 'starting') return unchanged(state);
      const next: ScreenSessionState = {
        ...state,
        phase: 'connecting',
        sessionId: event.session.sessionId,
        mode: event.session.mode,
        displayId: event.session.displayId,
        codec: event.session.codec,
        quality: event.session.quality,
        idleTimeoutSeconds: idleTimeoutSecondsFor(event.session.mode),
        lastActivityAt: event.at,
        phaseStartedAt: event.at,
      };
      return { state: next, outgoing: [], closePeer: false };
    }

    case 'refused': {
      if (state.phase !== 'starting') return unchanged(state);
      const next: ScreenSessionState = {
        ...state,
        phase: 'idle',
        refusal: event.reason,
      };
      delete next.sessionId;
      // A missing Screen Recording grant reads as "Mac에서 승인 필요", and a lock screen
      // or a password field as the pause it is, rather than as a flat no.
      const hold = holdFor(event.reason);
      if (hold) next.hold = hold;
      else delete next.hold;
      return { state: next, outgoing: [], closePeer: true };
    }

    case 'connected': {
      if (!isScreenSessionActive(state)) return unchanged(state);
      return {
        state: { ...state, phase: 'live', lastActivityAt: event.at },
        outgoing: [],
        closePeer: false,
      };
    }

    case 'activity': {
      if (!isScreenSessionActive(state)) return unchanged(state);
      return { state: { ...state, lastActivityAt: event.at }, outgoing: [], closePeer: false };
    }

    case 'peer-failed': {
      if (!isScreenSessionActive(state)) return unchanged(state);
      return end(state, 'peer-failed', { notify: true });
    }

    case 'stop': {
      if (!isScreenSessionActive(state)) return unchanged(state);
      return end(state, 'user-stop', { notify: true });
    }

    case 'background': {
      if (!isScreenSessionActive(state) || state.backgroundSince !== undefined) {
        return unchanged(state);
      }
      // The Mac keeps the 30 s rule, so it hears about the background at once.
      return {
        state: { ...state, backgroundSince: event.at },
        outgoing: state.sessionId ? [backgroundSignal(state.sessionId, true)] : [],
        closePeer: false,
      };
    }

    case 'foreground': {
      if (state.backgroundSince === undefined) return unchanged(state);
      const next = { ...state };
      delete next.backgroundSince;
      const outgoing =
        isScreenSessionActive(state) && state.sessionId
          ? [backgroundSignal(state.sessionId, false)]
          : [];
      return { state: next, outgoing, closePeer: false };
    }

    case 'tick': {
      if (!isScreenSessionActive(state)) return unchanged(state);
      // A reply or an offer that never comes must not leave the screen spinning.
      if (
        (state.phase === 'starting' || state.phase === 'connecting') &&
        event.at - state.phaseStartedAt >= SCREEN_CONNECT_DEADLINE_MS
      ) {
        return end(state, 'peer-failed', { notify: true });
      }
      // 30 s in the background ends the session even if the user never comes back.
      if (state.backgroundSince !== undefined) {
        const away = event.at - state.backgroundSince;
        if (away >= SCREEN_BACKGROUND_GRACE_SECONDS * 1000) {
          return end(state, 'background', { notify: true });
        }
      }
      // 10 min driving, 30 min watching, counted from the last frame or touch.
      const idle = event.at - state.lastActivityAt;
      if (state.phase === 'live' && idle >= state.idleTimeoutSeconds * 1000) {
        return end(state, 'idle-timeout', { notify: true });
      }
      return unchanged(state);
    }

    case 'signal':
      return applySignal(state, event.signal, event.at);

    default:
      return unchanged(state);
  }
}

function applySignal(
  state: ScreenSessionState,
  signal: ScreenSignal,
  at: number,
): ScreenTransition {
  switch (signal.type) {
    case 'screen-grant': {
      const next: ScreenSessionState = {
        ...state,
        allowed: signal.allowed,
        grant: signal.grant,
      };
      if (signal.controlChallengeB64) next.controlChallengeB64 = signal.controlChallengeB64;
      else delete next.controlChallengeB64;
      if (signal.iceServers) next.iceServers = signal.iceServers;
      if (signal.displays) next.displays = signal.displays;
      return applyGrant(next, signal.allowed, signal.grant);
    }

    case 'screen-kill': {
      // While starting the phone has no session id yet, so any kill is taken as ours:
      // the Mac only sends one for a session it opened for this phone.
      const pending = state.phase === 'starting' && state.sessionId === undefined;
      if (!pending && !killHitsSession(signal, state.sessionId)) return unchanged(state);
      // The kill is a courtesy note: the Mac has already stopped capture and closed
      // the connection. We only make the screen say the same thing.
      if (!isScreenSessionActive(state)) {
        const next: ScreenSessionState = { ...state, phase: 'ended', stopReason: signal.reason };
        const hold = holdFor(signal.reason);
        if (hold) next.hold = hold;
        return { state: next, outgoing: [], closePeer: true };
      }
      return end(state, signal.reason);
    }

    case 'screen-session-end': {
      if (signal.sessionId !== state.sessionId) return unchanged(state);
      if (!isScreenSessionActive(state)) return unchanged(state);
      return end(state, signal.reason);
    }

    case 'screen-offer': {
      if (signal.sessionId !== state.sessionId) return unchanged(state);
      if (!isScreenSessionActive(state)) return unchanged(state);
      return {
        state: {
          ...state,
          // The mode comes from `accepted`; an offer may only narrow it to view.
          mode: signal.mode === 'view' ? 'view' : state.mode,
          displayId: signal.displayId,
          codec: signal.codec,
          quality: signal.quality,
          lastActivityAt: at,
        },
        outgoing: [],
        closePeer: false,
      };
    }

    default:
      return unchanged(state);
  }
}

/** Runs a list of events in order; handy for screens and for tests. */
export function runScreenEvents(
  state: ScreenSessionState,
  events: readonly ScreenEvent[],
): ScreenTransition {
  let current = state;
  const outgoing: ScreenSignal[] = [];
  let closePeer = false;
  for (const event of events) {
    const step = screenSessionReducer(current, event);
    current = step.state;
    outgoing.push(...step.outgoing);
    closePeer = closePeer || step.closePeer;
  }
  return { state: current, outgoing, closePeer };
}
