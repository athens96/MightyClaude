import {
  RTCIceCandidate,
  RTCPeerConnection,
  RTCRtpReceiver,
  RTCSessionDescription,
} from 'react-native-webrtc';
import type RTCDataChannel from 'react-native-webrtc/lib/typescript/RTCDataChannel';
import type { ScreenCandidateType } from '@/api/types';
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
 * This file is the only one that touches the native module, so everything else — the
 * session rules, the signalling, the clipboard — runs under plain Node in the tests.
 */

/** The channel label the Mac opens for input, clipboard and zoom requests. */
export const SCREEN_DATA_CHANNEL = 'mc-screen';

const CANDIDATE_TYPES: readonly ScreenCandidateType[] = ['host', 'srflx', 'prflx', 'relay'];

function asCandidateType(value: unknown): ScreenCandidateType | undefined {
  return typeof value === 'string' && (CANDIDATE_TYPES as readonly string[]).includes(value)
    ? (value as ScreenCandidateType)
    : undefined;
}

/** What this phone can decode, for the `decodes` field of a session request. */
export function videoDecoderCapabilities(): DecoderCapabilities | undefined {
  try {
    return RTCRtpReceiver.getCapabilities('video') as DecoderCapabilities;
  } catch {
    return undefined;
  }
}

/** Walks `getStats` for the pair in use and reports which kind of path won. */
async function selectedCandidateType(
  connection: RTCPeerConnection,
): Promise<ScreenCandidateType | undefined> {
  try {
    const report: unknown = await connection.getStats();
    const entries: Record<string, Record<string, unknown>> = {};
    if (report instanceof Map) {
      for (const [id, value] of report.entries()) {
        entries[String(id)] = value as Record<string, unknown>;
      }
    } else if (report !== null && typeof report === 'object') {
      Object.assign(entries, report as Record<string, Record<string, unknown>>);
    }
    const pair = Object.values(entries).find(
      (value) =>
        value.type === 'candidate-pair' && (value.selected === true || value.state === 'succeeded'),
    );
    const localId = pair?.localCandidateId;
    const local = typeof localId === 'string' ? entries[localId] : undefined;
    return asCandidateType(local?.candidateType);
  } catch {
    return undefined;
  }
}

export function createWebrtcScreenPeer(
  config: ScreenPeerConfig,
  callbacks: ScreenPeerCallbacks,
): ScreenPeer {
  const connection = new RTCPeerConnection({
    iceServers: config.iceServers.map((server) => ({
      urls: server.urls,
      ...(server.username === undefined ? {} : { username: server.username }),
      ...(server.credential === undefined ? {} : { credential: server.credential }),
    })),
    bundlePolicy: 'max-bundle',
    rtcpMuxPolicy: 'require',
  });

  let channel: RTCDataChannel | undefined;

  // The published package ships no typings for its event-target shim, so the `on*`
  // setters are used rather than `addEventListener`, and each event is read through the
  // shape we actually need.
  connection.ontrack = ((event: { streams?: { toURL(): string }[] }) => {
    const stream = event.streams?.[0];
    callbacks.onStream(stream ? stream.toURL() : undefined);
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

  // The Mac opens the channel; we only listen for it.
  connection.ondatachannel = ((event: { channel: RTCDataChannel }) => {
    const opened = event.channel;
    channel = opened;
    opened.onmessage = ((message: { data?: unknown }) => {
      const data = message.data;
      if (typeof data !== 'string') return;
      try {
        callbacks.onData(JSON.parse(data));
      } catch {
        // A frame we cannot read is dropped; the data channel stays usable.
      }
    }) as never;
  }) as never;

  return {
    async answer(offerSdp, options) {
      await connection.setRemoteDescription(new RTCSessionDescription({ type: 'offer', sdp: offerSdp }));
      // An ICE restart arrives as a fresh offer with new ufrags, so answering it is the
      // whole job — there is nothing to restart on the answering side.
      void options.iceRestart;
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
      channel.send(payload);
      return true;
    },

    candidateType: () => selectedCandidateType(connection),

    close() {
      try {
        channel?.close();
      } catch {
        // Already gone with the connection.
      }
      channel = undefined;
      connection.close();
      callbacks.onStream(undefined);
    },
  };
}
