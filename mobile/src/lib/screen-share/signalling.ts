import {
  SCREEN_KILL_REASONS,
  SCREEN_SIGNAL_MAX_BYTES,
  type ScreenCodec,
  type ScreenDisplay,
  type ScreenEndReason,
  type ScreenGrant,
  type ScreenIceServer,
  type ScreenKillReason,
  type ScreenMode,
  type ScreenQuality,
} from '@/api/types';

/**
 * The screen-share signalling messages of docs/relay.md, as they travel inside the
 * existing E2EE channel. The relay forwards these frames byte for byte, so nothing
 * here is readable or changeable by it; this module only turns them into values the
 * app can trust, and refuses anything it does not recognise.
 */

export interface ScreenOfferSignal {
  type: 'screen-offer';
  sessionId: string;
  sdp: string;
  mode: ScreenMode;
  displayId: number;
  codec: ScreenCodec;
  quality: ScreenQuality;
  /** True when the Mac re-offers after a display switch or new TURN credentials. */
  iceRestart: boolean;
}

export interface ScreenAnswerSignal {
  type: 'screen-answer';
  sessionId: string;
  sdp: string;
}

export interface ScreenIceSignal {
  type: 'screen-ice';
  sessionId: string;
  /** An empty string means "no more candidates". */
  candidate: string;
  sdpMid?: string;
  sdpMLineIndex?: number;
  usernameFragment?: string;
}

export interface ScreenSessionEndSignal {
  type: 'screen-session-end';
  sessionId: string;
  reason: ScreenEndReason;
}

/** The app left (`true`) or came back to (`false`) the foreground; the Mac keeps the 30 s rule. */
export interface ScreenBackgroundSignal {
  type: 'screen-background';
  sessionId: string;
  background: boolean;
}

export interface ScreenGrantSignal {
  type: 'screen-grant';
  /** Absent when the Mac reports a change outside any session. */
  sessionId?: string;
  allowed: boolean;
  grant: ScreenGrant;
  controlChallengeB64?: string;
  iceServers?: ScreenIceServer[];
  displays?: ScreenDisplay[];
}

export interface ScreenKillSignal {
  type: 'screen-kill';
  /** Absent means every session this phone holds. */
  sessionId?: string;
  reason: ScreenKillReason;
}

/** Everything the Mac may send us, plus the frames we send back. */
export type ScreenSignal =
  | ScreenOfferSignal
  | ScreenAnswerSignal
  | ScreenIceSignal
  | ScreenSessionEndSignal
  | ScreenBackgroundSignal
  | ScreenGrantSignal
  | ScreenKillSignal;

export type ScreenSignalType = ScreenSignal['type'];

const TYPES: readonly ScreenSignalType[] = [
  'screen-offer',
  'screen-answer',
  'screen-ice',
  'screen-session-end',
  'screen-background',
  'screen-grant',
  'screen-kill',
];

const MODES: readonly ScreenMode[] = ['view', 'control'];
const CODECS: readonly ScreenCodec[] = ['H264', 'VP9', 'AV1'];
const GRANTS: readonly ScreenGrant[] = ['none', 'view', 'control'];
const END_REASONS: readonly ScreenEndReason[] = [
  'user-stop',
  'background',
  'peer-failed',
  'idle-timeout',
  'peer-left',
  'display-gone',
];

/** True for an envelope this build understands; anything else is dropped in silence. */
export function isScreenSignalType(type: unknown): type is ScreenSignalType {
  return typeof type === 'string' && (TYPES as readonly string[]).includes(type);
}

/** True for any `screen-…` name, including one a newer Mac invented. */
export function looksLikeScreenSignal(message: unknown): boolean {
  if (message === null || typeof message !== 'object') return false;
  const type = (message as { type?: unknown }).type;
  return typeof type === 'string' && type.startsWith('screen-');
}

function record(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function count(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T | undefined {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value)
    ? (value as T)
    : undefined;
}

function quality(value: unknown): ScreenQuality | undefined {
  const raw = record(value);
  if (!raw) return undefined;
  const width = count(raw.width);
  const height = count(raw.height);
  const fps = count(raw.fps);
  const maxBitrateKbps = count(raw.maxBitrateKbps);
  if (width === undefined || height === undefined) return undefined;
  if (fps === undefined || maxBitrateKbps === undefined) return undefined;
  return { width, height, fps, maxBitrateKbps };
}

function displays(value: unknown): ScreenDisplay[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const parsed: ScreenDisplay[] = [];
  for (const entry of value) {
    const raw = record(entry);
    const displayId = count(raw?.displayId);
    const width = count(raw?.width);
    const height = count(raw?.height);
    if (raw === undefined || displayId === undefined) continue;
    if (width === undefined || height === undefined) continue;
    parsed.push({ displayId, width, height, main: raw.main === true });
  }
  return parsed;
}

function iceServers(value: unknown): ScreenIceServer[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const parsed: ScreenIceServer[] = [];
  for (const entry of value) {
    const raw = record(entry);
    if (!raw) continue;
    const urls = raw.urls;
    const list = typeof urls === 'string' ? urls : Array.isArray(urls) ? urls.filter((url) => typeof url === 'string') : undefined;
    if (list === undefined || (Array.isArray(list) && list.length === 0)) continue;
    const server: ScreenIceServer = { urls: list as string | string[] };
    const username = text(raw.username);
    const credential = text(raw.credential);
    if (username !== undefined) server.username = username;
    if (credential !== undefined) server.credential = credential;
    parsed.push(server);
  }
  return parsed;
}

/**
 * Reads one decrypted envelope. A message that is not screen-share signalling, or one
 * whose body does not hold up, returns `undefined` so the caller can ignore it — which
 * is what the contract asks of both sides when the peer is a newer build.
 */
export function parseScreenSignal(message: unknown): ScreenSignal | undefined {
  const raw = record(message);
  if (!raw || !isScreenSignalType(raw.type)) return undefined;
  const sessionId = text(raw.sessionId);

  switch (raw.type) {
    case 'screen-offer': {
      const sdp = text(raw.sdp);
      const mode = oneOf(raw.mode, MODES);
      const displayId = count(raw.displayId);
      const codec = oneOf(raw.codec, CODECS);
      const parsedQuality = quality(raw.quality);
      if (!sessionId || !sdp || !mode || displayId === undefined) return undefined;
      if (!codec || !parsedQuality) return undefined;
      return {
        type: 'screen-offer',
        sessionId,
        sdp,
        mode,
        displayId,
        codec,
        quality: parsedQuality,
        iceRestart: raw.iceRestart === true,
      };
    }

    case 'screen-answer': {
      const sdp = text(raw.sdp);
      if (!sessionId || !sdp) return undefined;
      return { type: 'screen-answer', sessionId, sdp };
    }

    case 'screen-ice': {
      // `candidate: ""` is the end-of-candidates marker, so an empty string is valid
      // here while a missing or non-string field is not.
      if (!sessionId || typeof raw.candidate !== 'string') return undefined;
      const signal: ScreenIceSignal = { type: 'screen-ice', sessionId, candidate: raw.candidate };
      const sdpMid = text(raw.sdpMid);
      const sdpMLineIndex = count(raw.sdpMLineIndex);
      const usernameFragment = text(raw.usernameFragment);
      if (sdpMid !== undefined) signal.sdpMid = sdpMid;
      if (sdpMLineIndex !== undefined) signal.sdpMLineIndex = sdpMLineIndex;
      if (usernameFragment !== undefined) signal.usernameFragment = usernameFragment;
      return signal;
    }

    case 'screen-session-end': {
      const reason = oneOf(raw.reason, END_REASONS);
      if (!sessionId || !reason) return undefined;
      return { type: 'screen-session-end', sessionId, reason };
    }

    case 'screen-background': {
      if (!sessionId || typeof raw.background !== 'boolean') return undefined;
      return { type: 'screen-background', sessionId, background: raw.background };
    }

    case 'screen-grant': {
      const grant = oneOf(raw.grant, GRANTS);
      if (!grant || typeof raw.allowed !== 'boolean') return undefined;
      const signal: ScreenGrantSignal = { type: 'screen-grant', allowed: raw.allowed, grant };
      if (sessionId !== undefined) signal.sessionId = sessionId;
      const challenge = text(raw.controlChallengeB64);
      if (challenge !== undefined) signal.controlChallengeB64 = challenge;
      const servers = iceServers(raw.iceServers);
      if (servers !== undefined) signal.iceServers = servers;
      const screens = displays(raw.displays);
      if (screens !== undefined) signal.displays = screens;
      return signal;
    }

    case 'screen-kill': {
      const reason = oneOf(raw.reason, SCREEN_KILL_REASONS);
      if (!reason) return undefined;
      const signal: ScreenKillSignal = { type: 'screen-kill', reason };
      if (sessionId !== undefined) signal.sessionId = sessionId;
      return signal;
    }

    default:
      return undefined;
  }
}

/** A `screen-kill` with no `sessionId` applies to every session this phone holds. */
export function killHitsSession(signal: ScreenKillSignal, sessionId: string | undefined): boolean {
  if (signal.sessionId === undefined) return true;
  return signal.sessionId === sessionId;
}

/** Raised when a frame would not fit the plaintext budget the contract sets. */
export class ScreenSignalTooLarge extends Error {
  readonly bytes: number;

  constructor(bytes: number) {
    super(`시그널링 메시지가 너무 큽니다 (${bytes} 바이트).`);
    this.name = 'ScreenSignalTooLarge';
    this.bytes = bytes;
  }
}

function utf8Length(text: string): number {
  let bytes = 0;
  for (const char of text) {
    const code = char.codePointAt(0) ?? 0;
    if (code < 0x80) bytes += 1;
    else if (code < 0x800) bytes += 2;
    else if (code < 0x10000) bytes += 3;
    else bytes += 4;
  }
  return bytes;
}

/**
 * Serialises one outgoing frame. The 64 KiB ceiling keeps every frame far inside the
 * relay's 1 MiB limit and its 4 MiB buffer, and ICE candidates go out one per frame
 * rather than batched, so nothing we send can reach it in normal use.
 */
export function encodeScreenSignal(signal: ScreenSignal): string {
  const text = JSON.stringify(signal);
  const bytes = utf8Length(text);
  if (bytes > SCREEN_SIGNAL_MAX_BYTES) throw new ScreenSignalTooLarge(bytes);
  return text;
}

/** True when this frame would be accepted by `encodeScreenSignal`. */
export function screenSignalFits(signal: ScreenSignal): boolean {
  return utf8Length(JSON.stringify(signal)) <= SCREEN_SIGNAL_MAX_BYTES;
}

export function answerSignal(sessionId: string, sdp: string): ScreenAnswerSignal {
  return { type: 'screen-answer', sessionId, sdp };
}

export function iceSignal(
  sessionId: string,
  candidate: { candidate: string; sdpMid?: string | null; sdpMLineIndex?: number | null; usernameFragment?: string | null },
): ScreenIceSignal {
  const signal: ScreenIceSignal = { type: 'screen-ice', sessionId, candidate: candidate.candidate };
  if (candidate.sdpMid) signal.sdpMid = candidate.sdpMid;
  if (typeof candidate.sdpMLineIndex === 'number') signal.sdpMLineIndex = candidate.sdpMLineIndex;
  if (candidate.usernameFragment) signal.usernameFragment = candidate.usernameFragment;
  return signal;
}

/** The trickle terminator: one frame with an empty candidate. */
export function endOfCandidatesSignal(sessionId: string): ScreenIceSignal {
  return { type: 'screen-ice', sessionId, candidate: '' };
}

export function backgroundSignal(sessionId: string, background: boolean): ScreenBackgroundSignal {
  return { type: 'screen-background', sessionId, background };
}

export function sessionEndSignal(sessionId: string, reason: ScreenEndReason): ScreenSessionEndSignal {
  return { type: 'screen-session-end', sessionId, reason };
}
