import { p256 } from '@noble/curves/p256';
import { sha256 } from '@noble/hashes/sha2';
import { fromBase64, toBase64 } from '@/api/relay/crypto';

/**
 * Starting control needs the phone's owner, every session. The phone holds a P-256
 * signing key sealed by the Android Keystore behind a biometric (or device PIN) prompt:
 * reading it is what asks for the fingerprint, and the key is enrolled the moment the Mac
 * grants control. Each control session the Mac hands out a one-shot challenge, the phone
 * signs it with that key, and **the Mac verifies the signature** against the public key it
 * stored when it granted control. A phone that only checked a fingerprint locally and then
 * said "yes" would not open a session: without a signature the Mac refuses with
 * `control-signature`.
 */

/** Where the sealed private key lives, one per paired host. */
export function controlKeyStorageKey(hostId: string): string {
  return `mc.screenShare.controlKey.${hostId}`;
}

/**
 * The store the key is kept in. `expo-secure-store` satisfies it; passing
 * `requireAuthentication` is what moves the item behind an Android Keystore key with
 * `setUserAuthenticationRequired`, so every read raises the biometric prompt.
 */
export interface SecureKeyStore {
  getItemAsync(key: string, options?: SecureKeyStoreOptions): Promise<string | null>;
  setItemAsync(key: string, value: string, options?: SecureKeyStoreOptions): Promise<void>;
  deleteItemAsync(key: string, options?: SecureKeyStoreOptions): Promise<void>;
}

export interface SecureKeyStoreOptions {
  requireAuthentication?: boolean;
  authenticationPrompt?: string;
  keychainService?: string;
}

export interface ControlKeyEnrolment {
  /** Uncompressed P-256 point (65 bytes), base64. This is what the Mac stores. */
  publicKeyB64: string;
}

export type ControlKeyFailure =
  /** No key has been enrolled on this phone for this host yet. */
  | 'not-enrolled'
  /** The user dismissed the fingerprint prompt, or the OS refused. */
  | 'authentication-failed'
  /** The stored value is not a key any more. */
  | 'corrupt';

export type ControlSignature =
  | { ok: true; signatureB64: string }
  | { ok: false; failure: ControlKeyFailure };

/** The prompt text and the host the key belongs to. */
export interface ControlKeyContext {
  hostId: string;
  store: SecureKeyStore;
  /** Shown by the OS over the fingerprint sheet. */
  prompt: string;
}

function storeOptions(context: ControlKeyContext): SecureKeyStoreOptions {
  return {
    requireAuthentication: true,
    authenticationPrompt: context.prompt,
    keychainService: 'mightyclaude.screenShare',
  };
}

/**
 * Makes the signing key for this host and seals it. Called when the Mac grants control;
 * the public key goes to the Mac, which keeps it as `controlKeyPublic`.
 */
export async function enrollControlKey(context: ControlKeyContext): Promise<ControlKeyEnrolment> {
  const secret = p256.utils.randomSecretKey();
  const publicKey = p256.getPublicKey(secret, false);
  await context.store.setItemAsync(
    controlKeyStorageKey(context.hostId),
    toBase64(secret),
    storeOptions(context),
  );
  return { publicKeyB64: toBase64(publicKey) };
}

/** True once this host has a key on this phone. Does not raise the biometric prompt. */
export async function hasControlKey(context: ControlKeyContext): Promise<boolean> {
  try {
    const stored = await context.store.getItemAsync(
      controlKeyStorageKey(context.hostId),
      storeOptions(context),
    );
    return typeof stored === 'string' && stored.length > 0;
  } catch {
    return false;
  }
}

/**
 * Signs one control challenge. Reading the key is what prompts for the fingerprint, so a
 * dismissed prompt comes back as `authentication-failed` and no session is started.
 */
export async function signControlChallenge(
  context: ControlKeyContext,
  challengeB64: string,
): Promise<ControlSignature> {
  let stored: string | null;
  try {
    stored = await context.store.getItemAsync(
      controlKeyStorageKey(context.hostId),
      storeOptions(context),
    );
  } catch {
    return { ok: false, failure: 'authentication-failed' };
  }
  if (stored === null || stored.length === 0) return { ok: false, failure: 'not-enrolled' };

  try {
    const secret = fromBase64(stored);
    const challenge = fromBase64(challengeB64);
    // ECDSA over SHA-256 of the challenge bytes, DER encoded — what the Mac verifies.
    const signature = p256.sign(sha256(challenge), secret, { prehash: false });
    return { ok: true, signatureB64: toBase64(signature.toBytes('der')) };
  } catch {
    return { ok: false, failure: 'corrupt' };
  }
}

/** Drops the key: the grant is gone, or the user unpaired the host. */
export async function forgetControlKey(context: ControlKeyContext): Promise<void> {
  try {
    await context.store.deleteItemAsync(controlKeyStorageKey(context.hostId), storeOptions(context));
  } catch {
    // A key we cannot delete is a key the OS already lost with its Keystore entry.
  }
}

/**
 * Verifies a signature the way the Mac does. The phone never relies on this to open a
 * session — it exists so the enrolment can be proven end to end in tests, and so a
 * mismatched key is caught here rather than as an opaque `control-signature` refusal.
 */
export function verifyControlSignature(
  publicKeyB64: string,
  challengeB64: string,
  signatureB64: string,
): boolean {
  try {
    return p256.verify(fromBase64(signatureB64), sha256(fromBase64(challengeB64)), fromBase64(publicKeyB64), {
      prehash: false,
      format: 'der',
    });
  } catch {
    return false;
  }
}
