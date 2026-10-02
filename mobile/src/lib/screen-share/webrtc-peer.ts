import type * as WebRTC from 'react-native-webrtc';
import type RTCDataChannel from 'react-native-webrtc/lib/typescript/RTCDataChannel';
import type { ScreenIceServer } from '@/api/types';
import type { DecoderCapabilities } from '@/lib/screen-share/quality';
import type {
  ScreenIceCandidateInit,
  ScreenPeer,
  ScreenPeerCallbacks,
  ScreenPeerConfig,
} from '@/lib/screen-share/peer';

/**
 * `react-native-webrtc` behind the `ScreenPeer` port. The phone only ever answers: the Mac
 * sends the video and therefore makes the offer, and the Mac opens the data channel that
 * carries input and the clipboard.
 *
 * This file is the only one that touches the native module, and it loads it lazily: the
 * iOS build leaves `react-native-webrtc` out of autolinking (`react-native.config.js`), and
 * importing it there would throw at startup.
 */

/** The channel label the Mac opens for input, clipboard and zoom requests. */
export const SCREEN_DATA_CHANNEL = 'screen-control';

function webrtc(): typeof WebRTC {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  return require('react-native-webrtc') as typeof WebRTC;
}

/** What this phone can decode, for the `decodes` field of a session request. */
export function videoDecoderCapabilities(): DecoderCapabilities | undefined {
  try {
    return webrtc().RTCRtpReceiver.getCapabilities('video') as DecoderCapabilities;
  } catch {
    return undefined;
  }
}

function rtcIceServers(servers: ScreenIceServer[]) {
  return servers.map((server) => ({
    urls: server.urls,
    ...(server.username === undefined ? {} : { username: server.username }),
    ...(server.credential === undefined ? {} : { credential: server.credential }),
  }));
}

export function createWebrtcScreenPeer(
  config: ScreenPeerConfig,
  callbacks: ScreenPeerCallbacks,
): ScreenPeer {
  const { RTCIceCandidate, RTCPeerConnection, RTCSessionDescription } = webrtc();
  const configuration = {
    iceServers: rtcIceServers(config.iceServers),
    bundlePolicy: 'max-bundle' as const,
    rtcpMuxPolicy: 'require' as const,
  };
  const connection = new RTCPeerConnection(configuration);

  let channel: RTCDataChannel | undefined;

  // The published package ships no typings for its event-target shim, so the `on*`
  // setters are used rather than `addEventListener`, and each event is read through the
  // shape we actually need.
  connection.ontrack = ((event: { streams?: { id?: string; toURL(): string }[] }) => {
    const stream = event.streams?.[0];
    if (!stream) return;
    // The Mac names its streams `screen` and `overview`; anything else is the screen.
    const track = stream.id === 'overview' ? 'overview' : 'screen';
    callbacks.onStream(stream.toURL(), track);
  }) as never;

  connection.onicecandidate = ((event: { candidate?: { toJSON(): unknown } | null }) => {
    // A null candidate is the end of the list, which goes out as an empty `screen-ice`.
    const candidate = event.candidate;
    callbacks.onIceCandidate(candidate ? (candidate.toJSON() as ScreenIceCandidateInit) : null);
  }) as never;

  connection.onconnectionstatechange = (() => {
    const state = connection.connectionState;
    if (state === 'connected') callbacks.onConnectionState('connected');
    else if (state === 'failed') callbacks.onConnectionState('failed');
    else if (state === 'closed') callbacks.onConnectionState('closed');
    else callbacks.onConnectionState('connecting');
  }) as never;

  // The Mac opens `screen-control`; we only listen for it.
  connection.ondatachannel = ((event: { channel: RTCDataChannel }) => {
    const opened = event.channel;
    if (opened.label !== SCREEN_DATA_CHANNEL) return;
    channel = opened;
    opened.onmessage = ((message: { data?: unknown }) => {
      const data = message.data;
      if (typeof data !== 'string') return;
      let parsed: unknown;
      try {
        parsed = JSON.parse(data);
      } catch {
        // A frame we cannot read is dropped; the data channel stays usable.
        return;
      }
      callbacks.onData(parsed);
    }) as never;
  }) as never;

  return {
    async answer(offerSdp, options) {
      // A TURN credential renewal arrives as `screen-grant` with new servers and then an
      // `iceRestart` offer: the new servers must be in place before the offer is applied,
      // or the restarted ICE gathers against expired credentials.
      if (options.iceRestart && options.iceServers) {
        connection.setConfiguration({
          ...configuration,
          iceServers: rtcIceServers(options.iceServers),
        });
      }
      await connection.setRemoteDescription(
        new RTCSessionDescription({ type: 'offer', sdp: offerSdp }),
      );
      const answer = await connection.createAnswer();
      await connection.setLocalDescription(answer);
      return connection.localDescription?.sdp ?? (answer as { sdp?: string }).sdp ?? '';
    },

    async addIceCandidate(candidate) {
      await connection.addIceCandidate(new RTCIceCandidate(candidate));
    },

    async endOfRemoteCandidates() {
      await connection.addIceCandidate(new RTCIceCandidate({ candidate: '', sdpMid: '' }));
    },

    sendData(payload) {
      if (!channel || channel.readyState !== 'open') return false;
      try {
        channel.send(payload);
        return true;
      } catch {
        return false;
      }
    },

    stats: () => connection.getStats(),

    close() {
      try {
        channel?.close();
      } catch {
        // Already gone with the connection.
      }
      channel = undefined;
      connection.close();
    },
  };
}
