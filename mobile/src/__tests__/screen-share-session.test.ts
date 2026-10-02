import {
  SCREEN_BACKGROUND_GRACE_SECONDS,
  SCREEN_IDLE_TIMEOUT_CONTROL_SECONDS,
  SCREEN_IDLE_TIMEOUT_VIEW_SECONDS,
  type ScreenShareState,
} from '@/api/types';
import {
  holdFor,
  idleTimeoutSecondsFor,
  initialScreenSessionState,
  isScreenSessionActive,
  runScreenEvents,
  screenSessionReducer,
  wasStoppedByHost,
  type ScreenEvent,
  type ScreenSessionState,
} from '@/lib/screen-share/session';

/**
 * The phone's half of the safety rules. The Mac enforces all of them itself — it stops
 * capture, blocks injection and closes the peer connection within a second even with the
 * relay down — so what is checked here is that the phone drops its own session in the same
 * breath and never shows a picture it has no right to.
 */

const SECOND = 1000;

function hostState(overrides: Partial<ScreenShareState> = {}): ScreenShareState {
  return {
    allowed: true,
    grant: 'control',
    isBeta: true,
    displays: [{ displayId: 1, width: 1920, height: 1080, main: true }],
    controlChallengeB64: 'Y2hhbGxlbmdl',
    idleTimeoutSeconds: SCREEN_IDLE_TIMEOUT_CONTROL_SECONDS,
    ...overrides,
  };
}

/** A live session in the given mode, at t=0. */
function live(mode: 'view' | 'control', overrides: Partial<ScreenShareState> = {}): ScreenSessionState {
  const events: ScreenEvent[] = [
    { kind: 'host-state', state: hostState(overrides), at: 0 },
    { kind: 'requested', mode, displayId: 1, at: 0 },
    {
      kind: 'accepted',
      at: 0,
      session: {
        sessionId: 's1',
        mode,
        displayId: 1,
        codec: 'H264',
        quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
      },
    },
    { kind: 'connected', at: 0 },
  ];
  const result = runScreenEvents(initialScreenSessionState(0), events);
  expect(result.state.phase).toBe('live');
  return result.state;
}

describe('a newly paired phone', () => {
  it('starts with no allow-list flag and no grant', () => {
    const state = initialScreenSessionState();
    expect(state.allowed).toBe(false);
    expect(state.grant).toBe('none');
    expect(state.phase).toBe('idle');
    expect(isScreenSessionActive(state)).toBe(false);
  });

  it('takes the Mac allow-list flag, grant, displays and TURN servers as given', () => {
    const { state } = screenSessionReducer(initialScreenSessionState(), {
      kind: 'host-state',
      at: 0,
      state: hostState({ iceServers: [{ urls: 'turn:relay.example:3478' }] }),
    });
    expect(state.allowed).toBe(true);
    expect(state.grant).toBe('control');
    expect(state.displays).toHaveLength(1);
    expect(state.iceServers).toEqual([{ urls: 'turn:relay.example:3478' }]);
    expect(state.controlChallengeB64).toBe('Y2hhbGxlbmdl');
  });
});

describe('revocation', () => {
  it('ends a live session the moment the Mac takes the phone off the allow-list', () => {
    const step = screenSessionReducer(live('view'), {
      kind: 'signal',
      at: 5 * SECOND,
      signal: { type: 'screen-grant', allowed: false, grant: 'view' },
    });
    expect(step.state.phase).toBe('ended');
    expect(step.state.stopReason).toBe('revoked');
    expect(step.closePeer).toBe(true);
    expect(wasStoppedByHost(step.state.stopReason)).toBe(true);
  });

  it('ends a live session when the grant is withdrawn altogether', () => {
    const step = screenSessionReducer(live('control'), {
      kind: 'signal',
      at: SECOND,
      signal: { type: 'screen-grant', allowed: true, grant: 'none' },
    });
    expect(step.state.stopReason).toBe('revoked');
    expect(step.closePeer).toBe(true);
  });

  it('ends a control session that was downgraded to view-only, in the Mac’s words', () => {
    const step = screenSessionReducer(live('control'), {
      kind: 'signal',
      at: SECOND,
      signal: { type: 'screen-grant', allowed: true, grant: 'view' },
    });
    expect(step.state.phase).toBe('ended');
    expect(step.state.stopReason).toBe('grant-downgrade');
    expect(step.state.grant).toBe('view');
  });

  it('leaves a view session alone when control alone was downgraded', () => {
    const step = screenSessionReducer(live('view'), {
      kind: 'signal',
      at: SECOND,
      signal: { type: 'screen-grant', allowed: true, grant: 'view' },
    });
    expect(step.state.phase).toBe('live');
    expect(step.closePeer).toBe(false);
  });

  it('reads the same withdrawal from a state refresh, not only from a push', () => {
    const step = screenSessionReducer(live('control'), {
      kind: 'host-state',
      at: SECOND,
      state: hostState({ allowed: false, grant: 'none' }),
    });
    expect(step.state.stopReason).toBe('revoked');
  });

  it('tells the Mac nothing: the Mac already stopped, and may be unreachable', () => {
    const step = screenSessionReducer(live('view'), {
      kind: 'signal',
      at: SECOND,
      signal: { type: 'screen-grant', allowed: false, grant: 'none' },
    });
    expect(step.outgoing).toEqual([]);
  });
});

describe('a kill from the Mac', () => {
  it('ends the session for every reason the contract lists', () => {
    for (const reason of [
      'revoked',
      'grant-downgrade',
      'rekey-pairing',
      'kill-switch',
      'concurrency-limit',
    ] as const) {
      const step = screenSessionReducer(live('control'), {
        kind: 'signal',
        at: SECOND,
        signal: { type: 'screen-kill', reason, sessionId: 's1' },
      });
      expect(step.state.phase).toBe('ended');
      expect(step.state.stopReason).toBe(reason);
      expect(step.closePeer).toBe(true);
      expect(step.state.hold).toBeUndefined();
    }
  });

  it('shows the lock screen and secure input as a hold, not as a plain failure', () => {
    for (const reason of ['lock-screen', 'secure-input'] as const) {
      const step = screenSessionReducer(live('control'), {
        kind: 'signal',
        at: SECOND,
        signal: { type: 'screen-kill', reason },
      });
      expect(step.state.hold).toBe(reason);
      expect(holdFor(reason)).toBe(reason);
      expect(step.closePeer).toBe(true);
    }
  });

  it('ignores a kill aimed at another session of this phone', () => {
    const step = screenSessionReducer(live('view'), {
      kind: 'signal',
      at: SECOND,
      signal: { type: 'screen-kill', reason: 'kill-switch', sessionId: 'other' },
    });
    expect(step.state.phase).toBe('live');
    expect(step.closePeer).toBe(false);
  });

  it('takes a kill with no sessionId as "everything this phone holds"', () => {
    const step = screenSessionReducer(live('view'), {
      kind: 'signal',
      at: SECOND,
      signal: { type: 'screen-kill', reason: 'kill-switch' },
    });
    expect(step.state.stopReason).toBe('kill-switch');
  });
});

describe('idle timeout', () => {
  it('is 10 minutes driving and 30 minutes watching', () => {
    expect(idleTimeoutSecondsFor('control')).toBe(600);
    expect(idleTimeoutSecondsFor('view')).toBe(1800);
    expect(SCREEN_IDLE_TIMEOUT_CONTROL_SECONDS).toBe(600);
    expect(SCREEN_IDLE_TIMEOUT_VIEW_SECONDS).toBe(1800);
  });

  it('takes the Mac’s own number when it sends one', () => {
    expect(idleTimeoutSecondsFor('control', 120)).toBe(120);
    expect(idleTimeoutSecondsFor('control', 0)).toBe(600);
    expect(idleTimeoutSecondsFor('view', Number.NaN)).toBe(1800);
  });

  it('ends a control session after 10 quiet minutes', () => {
    const state = live('control');
    const before = screenSessionReducer(state, { kind: 'tick', at: 599 * SECOND });
    expect(before.state.phase).toBe('live');
    const after = screenSessionReducer(state, { kind: 'tick', at: 600 * SECOND });
    expect(after.state.phase).toBe('ended');
    expect(after.state.stopReason).toBe('idle-timeout');
    // `idle-timeout` is the Mac's word in the contract, and the Mac keeps its own idle
    // clock, so the phone simply lets go rather than claiming a reason that is not its
    // to send.
    expect(after.outgoing).toEqual([]);
  });

  it('gives a view-only session 30 minutes, not 10', () => {
    const state = live('view', { grant: 'view', idleTimeoutSeconds: SCREEN_IDLE_TIMEOUT_VIEW_SECONDS });
    expect(screenSessionReducer(state, { kind: 'tick', at: 1000 * SECOND }).state.phase).toBe('live');
    expect(
      screenSessionReducer(state, { kind: 'tick', at: 1800 * SECOND }).state.stopReason,
    ).toBe('idle-timeout');
  });

  it('is reset by a frame or a touch, so a session in use never times out', () => {
    const state = live('control');
    const busy = runScreenEvents(state, [
      { kind: 'tick', at: 500 * SECOND },
      { kind: 'activity', at: 590 * SECOND },
      { kind: 'tick', at: 1000 * SECOND },
    ]);
    expect(busy.state.phase).toBe('live');
    expect(
      screenSessionReducer(busy.state, { kind: 'tick', at: 1190 * SECOND }).state.stopReason,
    ).toBe('idle-timeout');
  });

  it('does not fire on a session that never connected', () => {
    const starting = runScreenEvents(initialScreenSessionState(0), [
      { kind: 'host-state', state: hostState(), at: 0 },
      { kind: 'requested', mode: 'control', displayId: 1, at: 0 },
    ]);
    const ticked = screenSessionReducer(starting.state, { kind: 'tick', at: 3600 * SECOND });
    expect(ticked.state.phase).toBe('starting');
  });
});

describe('going to the background', () => {
  it('ends the session 30 seconds after the app leaves the foreground', () => {
    const away = runScreenEvents(live('view'), [{ kind: 'background', at: 10 * SECOND }]);
    expect(away.state.backgroundSince).toBe(10 * SECOND);
    const early = screenSessionReducer(away.state, { kind: 'tick', at: 39 * SECOND });
    expect(early.state.phase).toBe('live');
    const late = screenSessionReducer(away.state, {
      kind: 'tick',
      at: (10 + SCREEN_BACKGROUND_GRACE_SECONDS) * SECOND,
    });
    expect(late.state.phase).toBe('ended');
    expect(late.state.stopReason).toBe('background');
    expect(late.outgoing).toEqual([
      { type: 'screen-session-end', sessionId: 's1', reason: 'background' },
    ]);
  });

  it('keeps the session when the user comes back inside the 30 seconds', () => {
    const result = runScreenEvents(live('view'), [
      { kind: 'background', at: 10 * SECOND },
      { kind: 'foreground', at: 20 * SECOND },
      { kind: 'tick', at: 100 * SECOND },
    ]);
    expect(result.state.phase).toBe('live');
    expect(result.state.backgroundSince).toBeUndefined();
  });
});

describe('the user and the peer', () => {
  it('ends on 중지 and says so', () => {
    const step = screenSessionReducer(live('control'), { kind: 'stop', at: SECOND });
    expect(step.state.stopReason).toBe('user-stop');
    expect(step.outgoing).toEqual([
      { type: 'screen-session-end', sessionId: 's1', reason: 'user-stop' },
    ]);
    expect(step.closePeer).toBe(true);
  });

  it('ends when the peer connection fails', () => {
    const step = screenSessionReducer(live('view'), { kind: 'peer-failed', at: SECOND });
    expect(step.state.stopReason).toBe('peer-failed');
    expect(wasStoppedByHost(step.state.stopReason)).toBe(false);
  });

  it('takes the Mac’s own session-end reasons', () => {
    for (const reason of ['idle-timeout', 'peer-left', 'display-gone'] as const) {
      const step = screenSessionReducer(live('view'), {
        kind: 'signal',
        at: SECOND,
        signal: { type: 'screen-session-end', sessionId: 's1', reason },
      });
      expect(step.state.stopReason).toBe(reason);
      expect(step.outgoing).toEqual([]);
    }
  });

  it('does nothing twice: a second stop on an ended session changes nothing', () => {
    const ended = screenSessionReducer(live('view'), { kind: 'stop', at: SECOND }).state;
    const again = screenSessionReducer(ended, { kind: 'stop', at: 2 * SECOND });
    expect(again.state).toBe(ended);
    expect(again.outgoing).toEqual([]);
  });
});

describe('the refusals the Mac sends back', () => {
  it('shows a missing Screen Recording grant as a hold the user can fix on the Mac', () => {
    const step = screenSessionReducer(live('view'), {
      kind: 'refused',
      at: SECOND,
      reason: 'screen-permission',
    });
    expect(step.state.phase).toBe('idle');
    expect(step.state.refusal).toBe('screen-permission');
    expect(step.state.hold).toBe('screen-permission');
  });

  it('keeps every other refusal as a plain no', () => {
    for (const reason of [
      'legacy-client',
      'device-not-allowed',
      'insufficient-grant',
      'control-signature',
      'concurrency-limit',
    ] as const) {
      const step = screenSessionReducer(live('view'), { kind: 'refused', at: SECOND, reason });
      expect(step.state.refusal).toBe(reason);
      expect(step.state.hold).toBeUndefined();
    }
  });
});

describe('an offer for a display switch', () => {
  it('moves the session onto the display and quality the Mac named', () => {
    const step = screenSessionReducer(live('control'), {
      kind: 'signal',
      at: SECOND,
      signal: {
        type: 'screen-offer',
        sessionId: 's1',
        sdp: 'v=0',
        mode: 'control',
        displayId: 7,
        codec: 'VP9',
        quality: { width: 1280, height: 720, fps: 15, maxBitrateKbps: 1000 },
        iceRestart: true,
      },
    });
    expect(step.state.displayId).toBe(7);
    expect(step.state.quality).toEqual({ width: 1280, height: 720, fps: 15, maxBitrateKbps: 1000 });
  });

  it('ignores an offer for a session this phone is not in', () => {
    const state = live('view');
    const step = screenSessionReducer(state, {
      kind: 'signal',
      at: SECOND,
      signal: {
        type: 'screen-offer',
        sessionId: 'other',
        sdp: 'v=0',
        mode: 'view',
        displayId: 9,
        codec: 'H264',
        quality: { width: 640, height: 480, fps: 5, maxBitrateKbps: 300 },
        iceRestart: false,
      },
    });
    expect(step.state).toBe(state);
  });
});
