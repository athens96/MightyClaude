import type { ScreenIceServer } from '@/api/types';

/**
 * The slice of WebRTC the screen uses, as a port. `react-native-webrtc` fills it on the
 * device (see `webrtc-peer.ts`); tests fill it with a fake, so every rule the Mac enforces
 * can be exercised without a radio.
 *
 * The phone is the answering side: the Mac sends the video, so the Mac makes the offer.
 */

export interface ScreenIceCandidateInit {
  candidate: string;
  sdpMid?: string | null;
  sdpMLineIndex?: number | null;
  usernameFragment?: string | null;
}

export type ScreenPeerConnectionState = 'connecting' | 'connected' | 'failed' | 'closed';

export interface ScreenPeerCallbacks {
  /** One locally gathered candidate, or `null` for "that was the last one". */
  onIceCandidate(candidate: ScreenIceCandidateInit | null): void;
  /** The URL an `RTCView` renders, or `undefined` once the track is gone. */
  onStream(streamUrl: string | undefined): void;
  onConnectionState(state: ScreenPeerConnectionState): void;
  /** One decoded data-channel frame, already `JSON.parse`d. */
  onData(message: unknown): void;
}

export interface ScreenPeer {
  /** Takes the Mac's offer and answers it; the SDP it returns goes back as `screen-answer`. */
  answer(offerSdp: string, options: { iceRestart: boolean }): Promise<string>;
  addIceCandidate(candidate: ScreenIceCandidateInit): Promise<void>;
  /** Marks the end of the Mac's candidates. */
  endOfRemoteCandidates(): Promise<void>;
  /** Sends one data-channel frame; false when the channel is not open. */
  sendData(payload: string): boolean;
  /** The ICE path in use, once `getStats` has something to say. */
  candidateType(): Promise<import('@/api/types').ScreenCandidateType | undefined>;
  close(): void;
}

export interface ScreenPeerConfig {
  iceServers: ScreenIceServer[];
}

export type ScreenPeerFactory = (
  config: ScreenPeerConfig,
  callbacks: ScreenPeerCallbacks,
) => ScreenPeer;
