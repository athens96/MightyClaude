import { p256 } from '@noble/curves/p256';
import { sha256 } from '@noble/hashes/sha2';
import { fromBase64 } from '@/api/relay/crypto';

/**
 * Starting control needs the phone's owner, every session. The phone holds a P-256
 * signing key in the Android Keystore that cannot be exported and only signs behind a
 * biometric (or device PIN) prompt — the native half lives in `modules/control-key`.
 * Each control session the Mac hands out a one-shot challenge, the phone signs it, and
 * **the Mac verifies the signature** against the public key it stored when its user
 * confirmed this phone's fingerprint. A phone that only checked a fingerprint locally and
 * then said "yes" would not open a session: without a signature the Mac refuses with
 * `control-signature`.
 *
 * JavaScript never holds the private key. It sees the public key — which doubles as the
 * non-secret "this phone has a key" marker — and finished signatures.
 */

/** The prompt the OS shows over the fingerprint sheet. */
export interface ControlKeyPrompt {
  title: string;
  /** The button that backs out; only shown where the device PIN is not offered. */
  cancel: string;
}

/** The Keystore, as `modules/control-key` exposes it. */
export interface ControlKeyNative {
  isSupported(): boolean;
  /** The stored public key (X9.62, 65 bytes, base64), or null. Never prompts. */
  publicKey(alias: string): Promise<string | null>;
  /** Creates a fresh key under this alias and returns its public key. Never prompts. */
  generate(alias: string): Promise<string>;
  remove(alias: string): Promise<void>;
  /** Prompts, then returns a DER ECDSA signature over SHA-256(challenge), base64. */
  sign(alias: string, challengeB64: string, prompt: ControlKeyPrompt): Promise<string>;
}

/** Which host the key belongs to, and the Keystore it lives in. */
export interface ControlKeyContext {
  hostId: string;
  /** Absent where there is no Keystore module (iOS, tests that do not need one). */
  native: ControlKeyNative | undefined;
  prompt: ControlKeyPrompt;
}

export type ControlKeyFailure =
  /** No key has been enrolled on this phone for this host yet. */
  | 'not-enrolled'
  /** The user dismissed the prompt, or the OS refused it. */
  | 'authentication-failed'
  /** A new fingerprint or a removed screen lock made the Keystore drop the key. */
  | 'key-invalidated'
  /** The phone has no screen lock (or, before Android 11, no strong biometric). */
  | 'no-authenticator'
  /** This phone has no Keystore module: iOS, or a build without it. */
  | 'unsupported'
  /** Anything else the Keystore said no to. */
  | 'keystore';

export type ControlSignature =
  | { ok: true; signatureB64: string }
  | { ok: false; failure: ControlKeyFailure };

export interface ControlKeyEnrolment {
  /** Uncompressed P-256 point (65 bytes), base64. This is what the Mac stores. */
  publicKeyB64: string;
}

/** One Keystore alias per paired host. */
export function controlKeyAlias(hostId: string): string {
  return `mc.screenShare.controlKey.${hostId}`;
}

export function controlKeySupported(context: Pick<ControlKeyContext, 'native'>): boolean {
  try {
    return context.native?.isSupported() === true;
  } catch {
    return false;
  }
}

/** The error codes the Kotlin module rejects with, as the screen explains them. */
export function controlKeyFailureFor(error: unknown): ControlKeyFailure {
  const code =
    error !== null && typeof error === 'object' && 'code' in error
      ? (error as { code: unknown }).code
      : undefined;
  switch (code) {
    case 'E_NO_KEY':
      return 'not-enrolled';
    case 'E_AUTH_CANCELLED':
    case 'E_AUTH_FAILED':
      return 'authentication-failed';
    case 'E_KEY_INVALIDATED':
      return 'key-invalidated';
    case 'E_NO_AUTHENTICATOR':
      return 'no-authenticator';
    default:
      return 'keystore';
  }
}

/**
 * The fingerprint the Mac shows its user and returns in `controlKeyFingerprint`: the
 * first 8 bytes of SHA-256 over the 65-byte public key, upper-case hex in groups of four.
 */
export function controlKeyFingerprint(publicKeyB64: string): string | undefined {
  try {
    const key = fromBase64(publicKeyB64);
    if (key.length !== 65 || key[0] !== 0x04) return undefined;
    const hex = Array.from(sha256(key).slice(0, 8), (byte) =>
      byte.toString(16).padStart(2, '0'),
    )
      .join('')
      .toUpperCase();
    return hex.match(/.{4}/g)?.join('-');
  } catch {
    return undefined;
  }
}

/** This host's public key on this phone, or undefined. Never raises a prompt. */
export async function readControlPublicKey(
  context: Pick<ControlKeyContext, 'hostId' | 'native'>,
): Promise<string | undefined> {
  if (!controlKeySupported(context)) return undefined;
  try {
    const stored = await context.native?.publicKey(controlKeyAlias(context.hostId));
    return typeof stored === 'string' && stored.length > 0 ? stored : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Makes a fresh signing key for this host. Only an explicit enrolment calls this — never a
 * failed or cancelled prompt — and the public key then goes to the Mac, whose user has to
 * confirm the fingerprint before it is stored.
 */
export async function createControlKey(
  context: Pick<ControlKeyContext, 'hostId' | 'native'>,
): Promise<ControlKeyEnrolment> {
  if (!context.native || !controlKeySupported(context)) {
    throw Object.assign(new Error('unsupported'), { code: 'E_UNSUPPORTED' });
  }
  const publicKeyB64 = await context.native.generate(controlKeyAlias(context.hostId));
  return { publicKeyB64 };
}

/**
 * Signs one control challenge. The Keystore raises the fingerprint (or PIN) prompt here,
 * so a dismissed prompt comes back as `authentication-failed` and no session is started.
 */
export async function signControlChallenge(
  context: ControlKeyContext,
  challengeB64: string,
): Promise<ControlSignature> {
  if (!context.native || !controlKeySupported(context)) return { ok: false, failure: 'unsupported' };
  try {
    const signatureB64 = await context.native.sign(
      controlKeyAlias(context.hostId),
      challengeB64,
      context.prompt,
    );
    return { ok: true, signatureB64 };
  } catch (error) {
    return { ok: false, failure: controlKeyFailureFor(error) };
  }
}

/** Drops the key: the grant left `control`, the key died, or the user unpaired the host. */
export async function forgetControlKey(
  context: Pick<ControlKeyContext, 'hostId' | 'native'>,
): Promise<void> {
  if (!controlKeySupported(context)) return;
  try {
    await context.native?.remove(controlKeyAlias(context.hostId));
  } catch {
    // A key we cannot delete is a key the Keystore already lost.
  }
}

/**
 * Where this phone's key stands against the one the Mac holds, from the two fingerprints
 * alone — so opening the screen never asks for a fingerprint.
 */
export type ControlKeyStatus =
  /** No Keystore module on this phone. */
  | 'unsupported'
  /** The grant is not `control`, so no key is needed. */
  | 'not-needed'
  /** Neither side has a key yet. */
  | 'missing'
  /** The phone has a key the Mac forgot (or never stored): register it again. */
  | 'host-missing'
  /** The Mac stores a key this phone no longer has. */
  | 'phone-missing'
  /** Both have keys, and they differ. */
  | 'mismatch'
  /** Both sides hold the same key: control can start. */
  | 'ready';

export function controlKeyStatus(input: {
  supported: boolean;
  grantIsControl: boolean;
  phoneFingerprint: string | undefined;
  hostFingerprint: string | undefined;
}): ControlKeyStatus {
  if (!input.supported) return 'unsupported';
  if (!input.grantIsControl) return 'not-needed';
  const phone = input.phoneFingerprint?.toUpperCase();
  const host = input.hostFingerprint?.toUpperCase();
  if (!phone && !host) return 'missing';
  if (!host) return 'host-missing';
  if (!phone) return 'phone-missing';
  return phone === host ? 'ready' : 'mismatch';
}

/** The states in which the enrol button is offered: the Mac has no key, or another one. */
export function mayEnrolControlKey(status: ControlKeyStatus): boolean {
  return (
    status === 'missing' ||
    status === 'host-missing' ||
    status === 'phone-missing' ||
    status === 'mismatch'
  );
}

/**
 * Verifies a signature the way the Mac does: ECDSA P-256, DER, over SHA-256 of the
 * challenge bytes. The phone never relies on this to open a session — it exists so the
 * signature format can be proven end to end in tests.
 */
export function verifyControlSignature(
  publicKeyB64: string,
  challengeB64: string,
  signatureB64: string,
): boolean {
  try {
    return p256.verify(
      fromBase64(signatureB64),
      sha256(fromBase64(challengeB64)),
      fromBase64(publicKeyB64),
      { prehash: false, format: 'der' },
    );
  } catch {
    return false;
  }
}
