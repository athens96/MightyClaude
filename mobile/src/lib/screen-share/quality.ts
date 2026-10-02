import type {
  ScreenCandidateType,
  ScreenCodec,
  ScreenNetwork,
  ScreenQuality,
} from '@/api/types';

/**
 * What the phone tells the Mac it can take: the codecs it can decode and the network it
 * is on. The Mac picks the codec and the real ceiling — these are the limits the phone
 * asks for, so a Mac that is hot or busy may still send less.
 */

/** Wi-Fi: up to 1080p30 at about 6 Mbps. */
export const WIFI_CEILING: ScreenQuality = {
  width: 1920,
  height: 1080,
  fps: 30,
  maxBitrateKbps: 6000,
};
/** Mobile data: up to 720p15 at about 1 Mbps, so a long session stays cheap. */
export const CELLULAR_CEILING: ScreenQuality = {
  width: 1280,
  height: 720,
  fps: 15,
  maxBitrateKbps: 1000,
};
/** coturn's per-session quota; a relayed path never asks for more than this. */
export const RELAY_QUOTA_KBPS = 2000;
/** Mobile data stays inside this band however the Mac adapts. */
export const CELLULAR_MIN_FPS = 5;
export const CELLULAR_MAX_FPS = 15;

export function qualityCeiling(network: ScreenNetwork): ScreenQuality {
  return network === 'cellular' ? { ...CELLULAR_CEILING } : { ...WIFI_CEILING };
}

/**
 * On a TURN relay path the sender is capped to the session quota, so asking for 6 Mbps
 * would only buy loss. Resolution is kept (terminal text first) and the bitrate drops.
 */
export function capForCandidate(
  quality: ScreenQuality,
  candidateType: ScreenCandidateType | undefined,
): ScreenQuality {
  if (candidateType !== 'relay') return quality;
  if (quality.maxBitrateKbps <= RELAY_QUOTA_KBPS) return quality;
  return { ...quality, maxBitrateKbps: RELAY_QUOTA_KBPS };
}

/** The ceiling this phone asks for, network and ICE path together. */
export function requestedQuality(
  network: ScreenNetwork,
  candidateType?: ScreenCandidateType,
): ScreenQuality {
  return capForCandidate(qualityCeiling(network), candidateType);
}

/** `Network.NetworkStateType` (and anything else) reduced to the two words the Mac takes. */
export function networkFor(type: string | undefined): ScreenNetwork {
  return type === 'CELLULAR' ? 'cellular' : 'wifi';
}

/** The shape `RTCRtpReceiver.getCapabilities('video')` returns, as much as we read. */
export interface DecoderCapabilities {
  codecs?: { mimeType?: string }[];
}

const DECODABLE: readonly { mime: string; codec: ScreenCodec }[] = [
  { mime: 'video/h264', codec: 'H264' },
  { mime: 'video/vp9', codec: 'VP9' },
  { mime: 'video/av1', codec: 'AV1' },
];

/**
 * The codecs this phone can decode, in the order the Mac should prefer. H.264 is always
 * claimed: hardware decoding for it is the beta's baseline, and it is the fallback the
 * Mac returns to under CPU or thermal pressure. HEVC is never offered.
 */
export function decodableCodecs(capabilities: DecoderCapabilities | undefined): ScreenCodec[] {
  const mimes = (capabilities?.codecs ?? [])
    .map((codec) => codec.mimeType?.toLowerCase())
    .filter((mime): mime is string => typeof mime === 'string');
  const found: ScreenCodec[] = [];
  for (const entry of DECODABLE) {
    if (mimes.includes(entry.mime)) found.push(entry.codec);
  }
  if (!found.includes('H264')) found.unshift('H264');
  return found;
}

/**
 * The codec the phone expects, given what it can decode and where it is. VP9/AV1 only
 * come into play on mobile data, where their bitrate saving pays for the extra work;
 * on Wi-Fi hardware H.264 wins on latency. The Mac has the last word, and answers with
 * the codec it actually chose.
 */
export function preferredCodec(network: ScreenNetwork, decodes: readonly ScreenCodec[]): ScreenCodec {
  if (network === 'cellular') {
    if (decodes.includes('AV1')) return 'AV1';
    if (decodes.includes('VP9')) return 'VP9';
  }
  return 'H264';
}
