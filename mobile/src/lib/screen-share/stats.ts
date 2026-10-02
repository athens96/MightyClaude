import type { ScreenCandidateType } from '@/api/types';

/**
 * `RTCPeerConnection.getStats()` read into the few numbers the screen shows: round trip,
 * jitter, decode time, freezes, frames, bytes and the ICE path that won. All of it stays
 * on the phone (docs/relay.md) — nothing here is ever sent to the relay or the Mac.
 */

export type StatsEntries = Record<string, Record<string, unknown>>;

/** `getStats` hands back a Map on the device and a plain object in some shims. */
export function statsEntries(report: unknown): StatsEntries {
  const entries: StatsEntries = {};
  if (report instanceof Map) {
    for (const [id, value] of report.entries()) {
      if (value !== null && typeof value === 'object') {
        entries[String(id)] = value as Record<string, unknown>;
      }
    }
  } else if (report !== null && typeof report === 'object') {
    for (const [id, value] of Object.entries(report as Record<string, unknown>)) {
      if (value !== null && typeof value === 'object') {
        entries[id] = value as Record<string, unknown>;
      }
    }
  }
  return entries;
}

const CANDIDATE_TYPES: readonly ScreenCandidateType[] = ['host', 'srflx', 'prflx', 'relay'];

function num(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

/** The candidate pair the transport is using, by its own pointer when it has one. */
function selectedPair(entries: StatsEntries): Record<string, unknown> | undefined {
  const values = Object.values(entries);
  const transport = values.find((value) => value.type === 'transport');
  const pointer = transport?.selectedCandidatePairId;
  if (typeof pointer === 'string' && entries[pointer]) return entries[pointer];
  return (
    values.find((value) => value.type === 'candidate-pair' && value.selected === true) ??
    values.find(
      (value) =>
        value.type === 'candidate-pair' && value.nominated === true && value.state === 'succeeded',
    ) ??
    values.find((value) => value.type === 'candidate-pair' && value.state === 'succeeded')
  );
}

/** Which kind of path won: `host` on the same Wi-Fi, `relay` over TURN. */
export function selectedCandidateType(entries: StatsEntries): ScreenCandidateType | undefined {
  const localId = selectedPair(entries)?.localCandidateId;
  const local = typeof localId === 'string' ? entries[localId] : undefined;
  const type = local?.candidateType;
  return typeof type === 'string' && (CANDIDATE_TYPES as readonly string[]).includes(type)
    ? (type as ScreenCandidateType)
    : undefined;
}

function inboundVideo(entries: StatsEntries): Record<string, unknown>[] {
  return Object.values(entries).filter(
    (value) => value.type === 'inbound-rtp' && (value.kind ?? value.mediaType) === 'video',
  );
}

/**
 * The `screen` track's receiver: its track id starts with `screen` (the Mac names them
 * `screen-<session>` and `overview-<session>`); failing that, the busiest video receiver.
 */
function primaryVideo(entries: StatsEntries): Record<string, unknown> | undefined {
  const video = inboundVideo(entries);
  const named = video.find(
    (value) => typeof value.trackIdentifier === 'string' && value.trackIdentifier.startsWith('screen'),
  );
  if (named) return named;
  return video.sort((a, b) => (num(b.framesDecoded) ?? 0) - (num(a.framesDecoded) ?? 0))[0];
}

/** One reading of the peer connection, in the units the overlay shows. */
export interface ScreenStatsSample {
  /** Phone clock, ms. */
  at: number;
  rttMs?: number;
  jitterMs?: number;
  /** Frames the `screen` track has decoded so far. */
  framesDecoded: number;
  /** Seconds spent decoding them, cumulative. */
  totalDecodeTimeS?: number;
  freezeCount?: number;
  totalFreezesDurationS?: number;
  /** Video bytes received on both tracks, cumulative. */
  bytesReceived: number;
  frameWidth?: number;
  frameHeight?: number;
  candidateType?: ScreenCandidateType;
}

export function readStatsSample(report: unknown, at: number): ScreenStatsSample {
  const entries = statsEntries(report);
  const pair = selectedPair(entries);
  const primary = primaryVideo(entries);
  const sample: ScreenStatsSample = {
    at,
    framesDecoded: num(primary?.framesDecoded) ?? 0,
    bytesReceived: inboundVideo(entries).reduce(
      (sum, value) => sum + (num(value.bytesReceived) ?? 0),
      0,
    ),
  };
  const rtt = num(pair?.currentRoundTripTime);
  if (rtt !== undefined) sample.rttMs = rtt * 1000;
  const jitter = num(primary?.jitter);
  if (jitter !== undefined) sample.jitterMs = jitter * 1000;
  const decode = num(primary?.totalDecodeTime);
  if (decode !== undefined) sample.totalDecodeTimeS = decode;
  const freezes = num(primary?.freezeCount);
  if (freezes !== undefined) sample.freezeCount = freezes;
  const frozen = num(primary?.totalFreezesDuration);
  if (frozen !== undefined) sample.totalFreezesDurationS = frozen;
  const width = num(primary?.frameWidth);
  const height = num(primary?.frameHeight);
  if (width !== undefined && height !== undefined) {
    sample.frameWidth = width;
    sample.frameHeight = height;
  }
  const candidateType = selectedCandidateType(entries);
  if (candidateType) sample.candidateType = candidateType;
  return sample;
}

/**
 * The p-th percentile (0–100) by linear interpolation between the closest ranks — the
 * same rule as NumPy's default. `undefined` for an empty list.
 */
export function percentile(values: readonly number[], p: number): number | undefined {
  const sorted = values.filter((value) => Number.isFinite(value)).sort((a, b) => a - b);
  if (sorted.length === 0) return undefined;
  const clamped = Math.min(100, Math.max(0, p));
  const rank = (clamped / 100) * (sorted.length - 1);
  const low = Math.floor(rank);
  const high = Math.ceil(rank);
  const lower = sorted[low] ?? 0;
  const upper = sorted[high] ?? lower;
  return lower + (upper - lower) * (rank - low);
}

/** Frames per second at or above which an interval counts as "the screen is moving". */
export const ACTIVE_FPS_THRESHOLD = 2;

export interface BitrateSummary {
  /** Mean kbps over intervals where the screen was moving. */
  activeKbps?: number;
  /** Mean kbps over intervals where it was still (the Mac sends next to nothing). */
  idleKbps?: number;
  activeSeconds: number;
  idleSeconds: number;
}

/**
 * Splits a series of samples into active and idle intervals by the frame rate inside each
 * one, and averages the bitrate of each kind over its total time. The Mac sends no frames
 * for a still screen apart from one refresh every 5 s, so idle should read close to zero.
 * A counter that went backwards (a renegotiation) ends the interval without counting it.
 */
export function bitrateSummary(
  samples: readonly Pick<ScreenStatsSample, 'at' | 'bytesReceived' | 'framesDecoded'>[],
): BitrateSummary {
  let activeBytes = 0;
  let activeMs = 0;
  let idleBytes = 0;
  let idleMs = 0;
  for (let index = 1; index < samples.length; index += 1) {
    const before = samples[index - 1];
    const after = samples[index];
    if (!before || !after) continue;
    const ms = after.at - before.at;
    const bytes = after.bytesReceived - before.bytesReceived;
    const frames = after.framesDecoded - before.framesDecoded;
    if (ms <= 0 || bytes < 0 || frames < 0) continue;
    if ((frames * 1000) / ms >= ACTIVE_FPS_THRESHOLD) {
      activeBytes += bytes;
      activeMs += ms;
    } else {
      idleBytes += bytes;
      idleMs += ms;
    }
  }
  const summary: BitrateSummary = { activeSeconds: activeMs / 1000, idleSeconds: idleMs / 1000 };
  // bytes × 8 / ms = kbit/s.
  if (activeMs > 0) summary.activeKbps = (activeBytes * 8) / activeMs;
  if (idleMs > 0) summary.idleKbps = (idleBytes * 8) / idleMs;
  return summary;
}

/** Mean decode time per frame, in ms, between two samples. */
export function decodeMsPerFrame(
  first: Pick<ScreenStatsSample, 'framesDecoded' | 'totalDecodeTimeS'>,
  last: Pick<ScreenStatsSample, 'framesDecoded' | 'totalDecodeTimeS'>,
): number | undefined {
  if (first.totalDecodeTimeS === undefined || last.totalDecodeTimeS === undefined) return undefined;
  const frames = last.framesDecoded - first.framesDecoded;
  if (frames <= 0) return undefined;
  return ((last.totalDecodeTimeS - first.totalDecodeTimeS) * 1000) / frames;
}
