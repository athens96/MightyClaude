import { SCREEN_SIGNAL_MAX_BYTES } from '@/api/types';
import {
  ScreenSignalTooLarge,
  answerSignal,
  backgroundSignal,
  encodeScreenSignal,
  endOfCandidatesSignal,
  iceSignal,
  isScreenSignalType,
  killHitsSession,
  looksLikeScreenSignal,
  parseScreenSignal,
  screenSignalFits,
  sessionEndSignal,
} from '@/lib/screen-share/signalling';

/**
 * The signalling contract of docs/relay.md, read the way the phone reads it: only the
 * message types this build knows, only bodies that hold up, and nothing that would
 * overflow the plaintext budget the relay's frame limits leave us.
 */

const OFFER = {
  type: 'screen-offer',
  sessionId: 's1',
  sdp: 'v=0',
  mode: 'view',
  displayId: 1,
  codec: 'H264',
  quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
  iceRestart: false,
};

describe('parseScreenSignal', () => {
  it('reads the Mac offer with its codec and quality ceiling', () => {
    expect(parseScreenSignal(OFFER)).toEqual({
      type: 'screen-offer',
      sessionId: 's1',
      sdp: 'v=0',
      mode: 'view',
      displayId: 1,
      codec: 'H264',
      quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
      iceRestart: false,
    });
  });

  it('keeps iceRestart, which is how a display switch and new TURN credentials arrive', () => {
    const restarted = parseScreenSignal({ ...OFFER, iceRestart: true });
    expect(restarted).toMatchObject({ type: 'screen-offer', iceRestart: true });
  });

  it('refuses an offer that is missing anything the phone needs', () => {
    expect(parseScreenSignal({ ...OFFER, sdp: '' })).toBeUndefined();
    expect(parseScreenSignal({ ...OFFER, codec: 'HEVC' })).toBeUndefined();
    expect(parseScreenSignal({ ...OFFER, mode: 'drive' })).toBeUndefined();
    expect(parseScreenSignal({ ...OFFER, displayId: 'main' })).toBeUndefined();
    expect(parseScreenSignal({ ...OFFER, quality: { width: 1920 } })).toBeUndefined();
    expect(parseScreenSignal({ ...OFFER, sessionId: '' })).toBeUndefined();
  });

  it('reads one ICE candidate, and reads an empty one as the end of the list', () => {
    expect(
      parseScreenSignal({
        type: 'screen-ice',
        sessionId: 's1',
        candidate: 'candidate:1 1 udp 2130706431 192.168.0.9 55000 typ host',
        sdpMid: '0',
        sdpMLineIndex: 0,
        usernameFragment: 'abcd',
      }),
    ).toEqual({
      type: 'screen-ice',
      sessionId: 's1',
      candidate: 'candidate:1 1 udp 2130706431 192.168.0.9 55000 typ host',
      sdpMid: '0',
      sdpMLineIndex: 0,
      usernameFragment: 'abcd',
    });
    expect(parseScreenSignal({ type: 'screen-ice', sessionId: 's1', candidate: '' })).toEqual({
      type: 'screen-ice',
      sessionId: 's1',
      candidate: '',
    });
    expect(parseScreenSignal({ type: 'screen-ice', sessionId: 's1' })).toBeUndefined();
  });

  it('reads a grant, with the TURN servers and displays the Mac may send with it', () => {
    expect(
      parseScreenSignal({
        type: 'screen-grant',
        allowed: true,
        grant: 'control',
        controlChallengeB64: 'Y2g=',
        iceServers: [
          { urls: 'turn:relay.example:3478', username: '1:abc', credential: 'secretish' },
          { urls: ['stun:relay.example:3478'] },
          { urls: [] },
          'nonsense',
        ],
        displays: [{ displayId: 1, width: 1920, height: 1080, main: true }, { width: 800 }],
      }),
    ).toEqual({
      type: 'screen-grant',
      allowed: true,
      grant: 'control',
      controlChallengeB64: 'Y2g=',
      iceServers: [
        { urls: 'turn:relay.example:3478', username: '1:abc', credential: 'secretish' },
        { urls: ['stun:relay.example:3478'] },
      ],
      displays: [{ displayId: 1, width: 1920, height: 1080, main: true }],
    });
  });

  it('takes a grant with no sessionId: the Mac reports changes outside a session too', () => {
    const signal = parseScreenSignal({ type: 'screen-grant', allowed: false, grant: 'none' });
    expect(signal).toEqual({ type: 'screen-grant', allowed: false, grant: 'none' });
    expect(signal && 'sessionId' in signal).toBe(false);
  });

  it('refuses a grant whose allow-list flag or grant word is not one of the three', () => {
    expect(parseScreenSignal({ type: 'screen-grant', allowed: 'yes', grant: 'view' })).toBeUndefined();
    expect(parseScreenSignal({ type: 'screen-grant', allowed: true, grant: 'admin' })).toBeUndefined();
  });

  it('reads every kill reason the contract lists, and no other', () => {
    for (const reason of [
      'revoked',
      'grant-downgrade',
      'rekey-pairing',
      'kill-switch',
      'lock-screen',
      'secure-input',
      'concurrency-limit',
    ]) {
      expect(parseScreenSignal({ type: 'screen-kill', reason })).toEqual({
        type: 'screen-kill',
        reason,
      });
    }
    expect(parseScreenSignal({ type: 'screen-kill', reason: 'because' })).toBeUndefined();
  });

  it('reads the session-end reasons both sides may send', () => {
    for (const reason of ['user-stop', 'background', 'peer-failed', 'idle-timeout', 'peer-left', 'display-gone']) {
      expect(parseScreenSignal({ type: 'screen-session-end', sessionId: 's1', reason })).toEqual({
        type: 'screen-session-end',
        sessionId: 's1',
        reason,
      });
    }
    expect(
      parseScreenSignal({ type: 'screen-session-end', sessionId: 's1', reason: 'bored' }),
    ).toBeUndefined();
  });

  it('drops a type this build does not know, so a newer Mac never breaks the tunnel', () => {
    expect(parseScreenSignal({ type: 'screen-teleport', sessionId: 's1' })).toBeUndefined();
    expect(parseScreenSignal({ type: 'notify', scope: 'state' })).toBeUndefined();
    expect(parseScreenSignal(undefined)).toBeUndefined();
    expect(parseScreenSignal('screen-offer')).toBeUndefined();
    expect(parseScreenSignal([OFFER])).toBeUndefined();
  });

  it('still recognises an unknown screen-… name as this feature talking', () => {
    expect(looksLikeScreenSignal({ type: 'screen-teleport' })).toBe(true);
    expect(looksLikeScreenSignal({ type: 'notify' })).toBe(false);
    expect(isScreenSignalType('screen-teleport')).toBe(false);
    expect(isScreenSignalType('screen-offer')).toBe(true);
  });
});

describe('killHitsSession', () => {
  it('hits every session this phone holds when the Mac names none', () => {
    expect(killHitsSession({ type: 'screen-kill', reason: 'kill-switch' }, 's1')).toBe(true);
    expect(killHitsSession({ type: 'screen-kill', reason: 'kill-switch' }, undefined)).toBe(true);
  });

  it('hits only the session the Mac named', () => {
    const kill = { type: 'screen-kill', reason: 'revoked', sessionId: 's1' } as const;
    expect(killHitsSession(kill, 's1')).toBe(true);
    expect(killHitsSession(kill, 's2')).toBe(false);
  });
});

describe('encodeScreenSignal', () => {
  it('builds the two frames the phone sends', () => {
    expect(JSON.parse(encodeScreenSignal(answerSignal('s1', 'v=0')))).toEqual({
      type: 'screen-answer',
      sessionId: 's1',
      sdp: 'v=0',
    });
    expect(JSON.parse(encodeScreenSignal(sessionEndSignal('s1', 'background')))).toEqual({
      type: 'screen-session-end',
      sessionId: 's1',
      reason: 'background',
    });
  });

  it('sends one candidate per frame, and an empty one to close the list', () => {
    expect(
      iceSignal('s1', { candidate: 'c', sdpMid: '0', sdpMLineIndex: 0, usernameFragment: null }),
    ).toEqual({ type: 'screen-ice', sessionId: 's1', candidate: 'c', sdpMid: '0', sdpMLineIndex: 0 });
    expect(endOfCandidatesSignal('s1')).toEqual({
      type: 'screen-ice',
      sessionId: 's1',
      candidate: '',
    });
  });

  it('refuses a frame over the 64 KiB plaintext budget, well inside the relay limits', () => {
    const huge = answerSignal('s1', 'a'.repeat(SCREEN_SIGNAL_MAX_BYTES));
    expect(screenSignalFits(huge)).toBe(false);
    expect(() => encodeScreenSignal(huge)).toThrow(ScreenSignalTooLarge);
    const ordinary = answerSignal('s1', 'v=0\r\n'.repeat(200));
    expect(screenSignalFits(ordinary)).toBe(true);
    expect(encodeScreenSignal(ordinary).length).toBeLessThan(SCREEN_SIGNAL_MAX_BYTES);
  });

  it('counts bytes, not characters, so Korean in an SDP cannot slip past the budget', () => {
    const korean = answerSignal('s1', '가'.repeat(SCREEN_SIGNAL_MAX_BYTES / 3));
    expect(screenSignalFits(korean)).toBe(false);
  });

  it('round-trips everything it encodes', () => {
    const frames = [
      answerSignal('s1', 'v=0'),
      endOfCandidatesSignal('s1'),
      sessionEndSignal('s1', 'user-stop'),
    ];
    for (const frame of frames) {
      expect(parseScreenSignal(JSON.parse(encodeScreenSignal(frame)))).toEqual(frame);
    }
  });
});

describe('screen-background', () => {
  it('reads and writes the app’s foreground state for one session', () => {
    expect(parseScreenSignal({ type: 'screen-background', sessionId: 's1', background: true })).toEqual({
      type: 'screen-background',
      sessionId: 's1',
      background: true,
    });
    expect(parseScreenSignal({ type: 'screen-background', sessionId: 's1' })).toBeUndefined();
    expect(parseScreenSignal({ type: 'screen-background', background: false })).toBeUndefined();
    expect(backgroundSignal('s1', false)).toEqual({ type: 'screen-background', sessionId: 's1', background: false });
    expect(screenSignalFits(backgroundSignal('s1', true))).toBe(true);
  });
});
