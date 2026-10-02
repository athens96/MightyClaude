import { ApiError, createClient, screenRejectReason } from '@/api/client';
import type { RelayChannel } from '@/api/client';
import type { RelayNotification } from '@/api/relay/transport';
import { SCREEN_REJECT_REASONS } from '@/api/types';

/**
 * The screen-share half of the m1 surface: the state the Mac keeps for this phone, the
 * session request, and the public half of the control key. The paths are the contract's.
 */

interface Recorded {
  method: 'GET' | 'POST';
  path: string;
  body?: unknown;
}

function fakeChannel(response: { status: number; body: unknown }) {
  const calls: Recorded[] = [];
  const sent: Record<string, unknown>[] = [];
  const messageListeners = new Set<(message: Record<string, unknown>) => void>();
  const channel: RelayChannel = {
    request: (method, path, body) => {
      calls.push({ method, path, body });
      return Promise.resolve(response);
    },
    onNotify: (_listener: (event: RelayNotification) => void) => () => undefined,
    onMessage: (listener) => {
      messageListeners.add(listener);
      return () => messageListeners.delete(listener);
    },
    send: (message) => {
      sent.push(message);
      return true;
    },
  };
  return {
    channel,
    calls,
    sent,
    push: (message: Record<string, unknown>) => messageListeners.forEach((l) => l(message)),
  };
}

const STATE = {
  status: 200,
  body: {
    protocol: 1,
    screenShare: {
      allowed: true,
      grant: 'control',
      isBeta: true,
      displays: [{ displayId: 1, width: 1920, height: 1080, main: true }],
      controlChallengeB64: 'Y2g=',
      iceServers: [{ urls: 'turn:relay.example:3478', username: '1:abc', credential: 'minted' }],
      idleTimeoutSeconds: 600,
    },
  },
};

describe('the screen-share routes', () => {
  it('reads this phone’s allow-list flag, grant, displays and TURN servers', async () => {
    const fake = fakeChannel(STATE);
    const client = createClient(fake.channel);
    const state = await client.screenShareState();
    expect(fake.calls[0]).toEqual({
      method: 'GET',
      path: '/m1/screen-share/state',
      body: undefined,
    });
    expect(state).toMatchObject({ allowed: true, grant: 'control', isBeta: true });
    expect(state.iceServers?.[0]?.credential).toBe('minted');
  });

  it('asks for a session with the body the contract names', async () => {
    const fake = fakeChannel({
      status: 200,
      body: {
        protocol: 1,
        sessionId: 's1',
        mode: 'control',
        displayId: 1,
        codec: 'H264',
        quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
      },
    });
    const client = createClient(fake.channel);
    const session = await client.startScreenShare({
      mode: 'control',
      displayId: 1,
      controlSignatureB64: 'MEQCIA==',
      network: 'cellular',
      decodes: ['H264', 'VP9'],
    });
    expect(fake.calls[0]).toEqual({
      method: 'POST',
      path: '/m1/screen-share/sessions',
      body: {
        mode: 'control',
        displayId: 1,
        controlSignatureB64: 'MEQCIA==',
        network: 'cellular',
        decodes: ['H264', 'VP9'],
      },
    });
    expect(session.sessionId).toBe('s1');
  });

  it('registers the public half of the control key, and never the private half', async () => {
    const fake = fakeChannel({ status: 200, body: { protocol: 1, ok: true } });
    const client = createClient(fake.channel);
    await client.registerScreenControlKey('BASE64PUBLIC');
    expect(fake.calls[0]).toEqual({
      method: 'POST',
      path: '/m1/screen-share/control-key',
      body: { publicKeyB64: 'BASE64PUBLIC' },
    });
  });

  it('reads the reason out of a 403 the Mac sends', async () => {
    for (const reason of SCREEN_REJECT_REASONS) {
      const fake = fakeChannel({ status: 403, body: { protocol: 1, error: { reason } } });
      const client = createClient(fake.channel);
      await expect(client.startScreenShare({ mode: 'view', displayId: 1, network: 'wifi', decodes: ['H264'] }))
        .rejects.toBeInstanceOf(ApiError);
      const error = await client
        .startScreenShare({ mode: 'view', displayId: 1, network: 'wifi', decodes: ['H264'] })
        .catch((caught: unknown) => caught);
      expect(screenRejectReason(error)).toBe(reason);
    }
  });

  it('calls a reason it does not know no reason at all', () => {
    expect(screenRejectReason(new ApiError(403, 'HTTP 403', 'because-i-said-so'))).toBeUndefined();
    expect(screenRejectReason(new ApiError(500, 'HTTP 500'))).toBeUndefined();
    expect(screenRejectReason(new Error('nope'))).toBeUndefined();
  });

  it('passes signalling through the channel, and only signalling', () => {
    const fake = fakeChannel(STATE);
    const client = createClient(fake.channel);
    const seen: string[] = [];
    client.onScreenSignal((signal) => seen.push(signal.type));

    fake.push({ type: 'screen-kill', reason: 'revoked' });
    fake.push({ type: 'notify', scope: 'state', revision: 1 });
    fake.push({ type: 'screen-teleport' });
    expect(seen).toEqual(['screen-kill']);

    expect(client.sendScreenSignal({ type: 'screen-answer', sessionId: 's1', sdp: 'v=0' })).toBe(true);
    expect(fake.sent).toEqual([{ type: 'screen-answer', sessionId: 's1', sdp: 'v=0' }]);
  });

  it('cannot send signalling on a channel that has no plain-message path', () => {
    const fake = fakeChannel(STATE);
    const bare: RelayChannel = { request: fake.channel.request, onNotify: fake.channel.onNotify };
    const client = createClient(bare);
    expect(client.sendScreenSignal({ type: 'screen-answer', sessionId: 's1', sdp: 'v=0' })).toBe(false);
    // An older transport simply never reports signalling; nothing throws.
    expect(typeof client.onScreenSignal(() => undefined)).toBe('function');
  });
});
