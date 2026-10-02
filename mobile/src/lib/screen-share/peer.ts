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

/**
 * The Mac's two video tracks, by stream id: `screen` is what the user reads (the whole
 * display, or the zoomed region at full quality) and `overview` is the low-resolution whole
 * display it lays under a zoom.
 */
export type ScreenTrack = 'screen' | 'overview';

export interface ScreenPeerCallbacks {
  /** One locally gathered candidate, or `null` for "that was the last one". */
  onIceCandidate(candidate: ScreenIceCandidateInit | null): void;
  /** The URL an `RTCView` renders for one of the two tracks, or `undefined` once it is gone. */
  onStream(streamUrl: string | undefined, track: ScreenTrack): void;
  onConnectionState(state: ScreenPeerConnectionState): void;
  /** One decoded data-channel frame, already `JSON.parse`d. */
  onData(message: unknown): void;
}

export interface ScreenPeer {
  /**
   * Takes the Mac's offer and answers it; the SDP it returns goes back as `screen-answer`.
   * On an ICE restart the renewed servers are applied before the offer is.
   */
  answer(
    offerSdp: string,
    options: { iceRestart: boolean; iceServers?: ScreenIceServer[] },
  ): Promise<string>;
  addIceCandidate(candidate: ScreenIceCandidateInit): Promise<void>;
  /** Marks the end of the Mac's candidates. */
  endOfRemoteCandidates(): Promise<void>;
  /** Sends one data-channel frame; false when the channel is not open. */
  sendData(payload: string): boolean;
  /** The raw `getStats` report; `stats.ts` reads it. */
  stats(): Promise<unknown>;
  close(): void;
}

export interface ScreenPeerConfig {
  iceServers: ScreenIceServer[];
}

export type ScreenPeerFactory = (
  config: ScreenPeerConfig,
  callbacks: ScreenPeerCallbacks,
) => ScreenPeer;
