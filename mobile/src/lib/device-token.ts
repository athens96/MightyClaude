import { toBase64Url } from '@/api/relay/crypto';
import { randomBytes } from '@/api/relay/random';
import { t } from '@/lib/i18n';

/**
 * Device tokens (docs/relay.md, the device-token section). The first connection
 * authenticates with the pairing key and this install's `clientId`; the host answers
 * `auth_ok` with a token,
 * once. From then on the token alone authenticates and the pairing key is forgotten, so
 * a Mac that releases this device can invalidate it without touching the others. A host
 * that never sends a token keeps working on the pairing key exactly as before.
 *
 * Pure on purpose: nothing here reads the socket or the keychain, and nothing here is
 * ever logged.
 */

/** `clientId` length in bytes, before base64url. */
export const CLIENT_ID_BYTES = 16;

/** A fresh per-install identifier: 16 random bytes, base64url, no padding. */
export function generateClientId(): string {
  return toBase64Url(randomBytes(CLIENT_ID_BYTES));
}

export interface DeviceAuthState {
  /** Absent only on a build that has not generated one yet. */
  clientId?: string;
  /** Dropped once a token is held. */
  pairingKey?: string;
  deviceToken?: string;
}

export interface AuthFrame {
  type: 'auth';
  clientName: string;
  clientId?: string;
  pairingKey?: string;
  deviceToken?: string;
}

/**
 * The encrypted `auth` frame for the state we are in, or undefined when there is nothing
 * to authenticate with. A token wins over a pairing key: once the host has issued one,
 * the key may already have been replaced on the Mac.
 */
export function authFrameFor(
  state: DeviceAuthState,
  clientName: string,
): AuthFrame | undefined {
  if (state.deviceToken && state.clientId) {
    return {
      type: 'auth',
      clientId: state.clientId,
      deviceToken: state.deviceToken,
      clientName,
    };
  }
  if (!state.pairingKey) return undefined;
  const frame: AuthFrame = { type: 'auth', pairingKey: state.pairingKey, clientName };
  // Without a clientId the host treats us as an old app and issues no token.
  if (state.clientId) frame.clientId = state.clientId;
  return frame;
}

/**
 * The state after `auth_ok`. A host that sent no token leaves everything as it was (and
 * returns the same object, so callers can tell nothing changed); a token replaces the
 * pairing key, which the Mac regenerates the moment it releases a device.
 */
export function afterAuthOk(
  state: DeviceAuthState,
  deviceToken: string | undefined,
): DeviceAuthState {
  if (!deviceToken || deviceToken === state.deviceToken) return state;
  // A token is only usable alongside the clientId it was issued for: without one there
  // is nothing to present it with, and dropping the key would lock this host out.
  if (!state.clientId) return state;
  return { clientId: state.clientId, deviceToken };
}

/**
 * The `auth_error.reason` vocabulary both sides share (docs/relay.md, the device-token section).
 * `unknown` stands for a word this build has never heard of, including `malformed`'s
 * neighbours a later host may add.
 */
export type AuthErrorReason =
  | 'pairing-key'
  | 'device-revoked'
  | 'device-conflict'
  | 'device-limit'
  | 'legacy-refused'
  | 'malformed'
  | 'unknown';

export interface AuthRejection {
  reason: AuthErrorReason;
  /**
   * The stored secrets are worthless and the host has to be paired again. Only the two
   * reasons that say so may delete anything: a word we do not understand must never
   * cost the user their pairing.
   */
  final: boolean;
  /** Mint a host-specific `clientId` and try the pairing key once more. */
  retryWithNewClientId: boolean;
  /** What the banner says. */
  message: string;
}

/** Generic wording for everything that is neither final nor worth a special sentence. */
const REFUSED_MESSAGE = 'phone.relay.failure.authRefused';

/** `messageKey` is a locale key, read when the rejection is made. */
type Rejection = Omit<AuthRejection, 'reason' | 'message'> & { messageKey: string };

const REJECTIONS: Record<AuthErrorReason, Rejection> = {
  'pairing-key': {
    final: true,
    retryWithNewClientId: false,
    messageKey: 'phone.hosts.reachability.unauthorized',
  },
  'device-revoked': {
    final: true,
    retryWithNewClientId: false,
    messageKey: 'phone.relay.failure.deviceRevoked',
  },
  'device-conflict': {
    final: false,
    retryWithNewClientId: true,
    messageKey: 'phone.relay.failure.deviceConflict',
  },
  'device-limit': {
    final: false,
    retryWithNewClientId: false,
    messageKey: 'phone.relay.failure.deviceLimit',
  },
  // This app always sends a `clientId`, so a host that refuses old apps is not talking
  // about us; it is just one more answer worth retrying.
  'legacy-refused': { final: false, retryWithNewClientId: false, messageKey: REFUSED_MESSAGE },
  malformed: { final: false, retryWithNewClientId: false, messageKey: REFUSED_MESSAGE },
  unknown: { final: false, retryWithNewClientId: false, messageKey: REFUSED_MESSAGE },
};

const KNOWN_REASONS: readonly string[] = Object.keys(REJECTIONS).filter(
  (reason) => reason !== 'unknown',
);

/** Reads an `auth_error.reason`; anything we do not know becomes the harmless one. */
export function authRejectionFor(reason: unknown): AuthRejection {
  const name: AuthErrorReason =
    typeof reason === 'string' && KNOWN_REASONS.includes(reason)
      ? (reason as AuthErrorReason)
      : 'unknown';
  const { messageKey, ...rejection } = REJECTIONS[name];
  return { reason: name, ...rejection, message: t(messageKey) };
}

/** Reads the `deviceToken` out of an `auth_ok` envelope, if the host sent one. */
export function deviceTokenFrom(message: { deviceToken?: unknown }): string | undefined {
  const token = message.deviceToken;
  return typeof token === 'string' && token.length > 0 ? token : undefined;
}
