import {
  composerRunning,
  isFreshAnswer,
  relayCameBack,
  returnedToForeground,
  stopVerdict,
} from '@/lib/resync';

describe('returnedToForeground', () => {
  it('fires when the app comes back from the background or an interruption', () => {
    expect(returnedToForeground('background', 'active')).toBe(true);
    // Control Center, the notification shade, Face ID: not a return from the background.
    expect(returnedToForeground('inactive', 'active')).toBe(false);
    expect(returnedToForeground(null, 'active')).toBe(false);
  });

  it('stays quiet while the app leaves or was already in front', () => {
    expect(returnedToForeground('active', 'background')).toBe(false);
    expect(returnedToForeground('active', 'inactive')).toBe(false);
    expect(returnedToForeground('active', 'active')).toBe(false);
  });
});

describe('relayCameBack', () => {
  it('fires once a tunnel that was down is usable again', () => {
    expect(relayCameBack('closed', 'ready')).toBe(true);
    expect(relayCameBack('connecting', 'ready')).toBe(true);
    expect(relayCameBack('handshaking', 'ready')).toBe(true);
  });

  it('ignores the steps on the way and a first state with nothing before it', () => {
    expect(relayCameBack(undefined, 'ready')).toBe(false);
    expect(relayCameBack('ready', 'ready')).toBe(false);
    expect(relayCameBack('ready', 'closed')).toBe(false);
    expect(relayCameBack('closed', 'connecting')).toBe(false);
  });
});

describe('isFreshAnswer', () => {
  it('takes the first answer of a cycle as the whole state', () => {
    expect(isFreshAnswer(undefined, 3)).toBe(true);
  });

  it('takes a revision that went backwards as a host that started counting again', () => {
    expect(isFreshAnswer(57, 2)).toBe(true);
  });

  it('reads the next step and a timed-out wait as ordinary answers', () => {
    expect(isFreshAnswer(3, 4)).toBe(false);
    expect(isFreshAnswer(3, 3)).toBe(false);
  });
});

describe('stopVerdict', () => {
  it('reads stopped: false as a run the phone missed the end of', () => {
    expect(stopVerdict({ stopped: false })).toEqual({ kind: 'notRunning' });
  });

  it('reads stopped: true, or a host that never sent the field, as a stop sent', () => {
    expect(stopVerdict({ stopped: true })).toEqual({ kind: 'requested' });
    expect(stopVerdict({})).toEqual({ kind: 'requested' });
  });

  it('passes a refusal through in the words it came with', () => {
    expect(stopVerdict({ failure: '실행 창을 찾을 수 없습니다.' })).toEqual({
      kind: 'failed',
      message: '실행 창을 찾을 수 없습니다.',
    });
  });
});

describe('composerRunning', () => {
  it('follows the status while the host has not said otherwise', () => {
    expect(composerRunning('running', 8, undefined)).toBe(true);
    expect(composerRunning('idle', 8, undefined)).toBe(false);
    expect(composerRunning(undefined, undefined, undefined)).toBe(false);
  });

  it('goes back to send once the host says the run it showed is over', () => {
    const verdict = stopVerdict({ stopped: false });
    const settled = verdict.kind === 'notRunning' ? 8 : undefined;
    expect(composerRunning('running', 8, settled)).toBe(false);
  });

  it('lets a newer detail speak for itself again', () => {
    expect(composerRunning('running', 9, 8)).toBe(true);
    expect(composerRunning('completed', 9, 8)).toBe(false);
  });
});
