import { createHash, createSign, generateKeyPairSync } from 'node:crypto';
import { toBase64, utf8Encode } from '@/api/relay/crypto';
import {
  controlKeyAlias,
  controlKeyFailureFor,
  controlKeyFingerprint,
  controlKeyStatus,
  createControlKey,
  forgetControlKey,
  mayEnrolControlKey,
  readControlPublicKey,
  signControlChallenge,
  verifyControlSignature,
  type ControlKeyContext,
} from '@/lib/screen-share/control-key';
import { fakeControlKey } from './support/fake-control-key';

/**
 * Starting control needs the phone's owner every session. The private key lives in the
 * Android Keystore and never reaches JavaScript; the **Mac** verifies the signature. A
 * phone that merely checked a fingerprint locally would get nowhere: without a signature
 * the Mac answers `control-signature`.
 */

const CHALLENGE = toBase64(utf8Encode('screen-control-challenge:s1:1760000000'));
const PROMPT = { title: '본인 확인', cancel: '취소' };

function context(native = fakeControlKey(), hostId = 'h1'): ControlKeyContext {
  return { hostId, native, prompt: PROMPT };
}

/** The Mac's fingerprint, computed independently of the code under test. */
function macFingerprint(publicKeyB64: string): string {
  const hex = createHash('sha256')
    .update(Buffer.from(publicKeyB64, 'base64'))
    .digest('hex')
    .slice(0, 16)
    .toUpperCase();
  return (hex.match(/.{4}/g) ?? []).join('-');
}

describe('the control key never leaves the Keystore', () => {
  it('hands JavaScript only a 65-byte X9.62 public key, under one alias per host', async () => {
    const native = fakeControlKey();
    const enrolment = await createControlKey(context(native));
    const point = Buffer.from(enrolment.publicKeyB64, 'base64');
    expect(point).toHaveLength(65);
    expect(point[0]).toBe(0x04);
    expect(native.generated).toEqual([controlKeyAlias('h1')]);
    expect(await readControlPublicKey(context(native))).toBe(enrolment.publicKeyB64);
  });

  it('keeps one key per host, so unpairing one leaves the others alone', async () => {
    const native = fakeControlKey();
    await createControlKey(context(native, 'h1'));
    await createControlKey(context(native, 'h2'));
    await forgetControlKey(context(native, 'h1'));
    expect(await readControlPublicKey(context(native, 'h1'))).toBeUndefined();
    expect(await readControlPublicKey(context(native, 'h2'))).toBeDefined();
  });

  it('reading the public key raises no prompt', async () => {
    const native = fakeControlKey();
    await createControlKey(context(native));
    await readControlPublicKey(context(native));
    expect(native.prompts).toEqual([]);
  });
});

describe('the fingerprint the Mac shows its user', () => {
  it('is the first 8 bytes of SHA-256 over the public key, in four groups of four', async () => {
    const { publicKeyB64 } = await createControlKey(context());
    const fingerprint = controlKeyFingerprint(publicKeyB64);
    expect(fingerprint).toMatch(/^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$/);
    expect(fingerprint).toBe(macFingerprint(publicKeyB64));
  });

  it('refuses anything that is not an uncompressed P-256 point', () => {
    expect(controlKeyFingerprint(toBase64(new Uint8Array(33).fill(2)))).toBeUndefined();
    expect(controlKeyFingerprint('not base64 at all')).toBeUndefined();
  });
});

describe('where the key stands, without a prompt', () => {
  const base = { supported: true, grantIsControl: true };

  it('is ready only when both sides hold the same key', () => {
    expect(controlKeyStatus({ ...base, phoneFingerprint: 'AAAA', hostFingerprint: 'aaaa' })).toBe('ready');
    expect(controlKeyStatus({ ...base, phoneFingerprint: 'AAAA', hostFingerprint: 'BBBB' })).toBe('mismatch');
  });

  it('tells the missing cases apart', () => {
    expect(controlKeyStatus({ ...base, phoneFingerprint: undefined, hostFingerprint: undefined })).toBe('missing');
    expect(controlKeyStatus({ ...base, phoneFingerprint: 'AAAA', hostFingerprint: undefined })).toBe('host-missing');
    expect(controlKeyStatus({ ...base, phoneFingerprint: undefined, hostFingerprint: 'AAAA' })).toBe('phone-missing');
  });

  it('needs no key without the control grant, and has none where there is no Keystore', () => {
    expect(controlKeyStatus({ supported: true, grantIsControl: false, phoneFingerprint: 'A', hostFingerprint: 'A' })).toBe('not-needed');
    expect(controlKeyStatus({ supported: false, grantIsControl: true, phoneFingerprint: undefined, hostFingerprint: undefined })).toBe('unsupported');
  });

  it('offers enrolment only when the Mac has no key, or another one', () => {
    expect(mayEnrolControlKey('missing')).toBe(true);
    expect(mayEnrolControlKey('host-missing')).toBe(true);
    // The Mac already holds a key and refuses another until its user removes it.
    expect(mayEnrolControlKey('phone-missing')).toBe(false);
    expect(mayEnrolControlKey('mismatch')).toBe(false);
    expect(mayEnrolControlKey('ready')).toBe(false);
    expect(mayEnrolControlKey('not-needed')).toBe(false);
    expect(mayEnrolControlKey('unsupported')).toBe(false);
  });
});

describe('signing the Mac’s challenge', () => {
  it('produces the DER ECDSA signature over SHA-256(challenge) the Mac verifies', async () => {
    const native = fakeControlKey();
    const { publicKeyB64 } = await createControlKey(context(native));
    const signature = await signControlChallenge(context(native), CHALLENGE);
    expect(signature.ok).toBe(true);
    if (!signature.ok) return;
    expect(verifyControlSignature(publicKeyB64, CHALLENGE, signature.signatureB64)).toBe(true);
    expect(native.prompts).toEqual([PROMPT]);
  });

  it('accepts a signature made the way Android makes it (SHA256withECDSA)', () => {
    // An independent signer: Node's OpenSSL, the same algorithm the Keystore runs.
    const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    const jwk = publicKey.export({ format: 'jwk' });
    const point = Buffer.concat([
      Buffer.from([0x04]),
      Buffer.from(jwk.x ?? '', 'base64url'),
      Buffer.from(jwk.y ?? '', 'base64url'),
    ]);
    const signer = createSign('SHA256');
    signer.update(Buffer.from(CHALLENGE, 'base64'));
    const der = signer.sign(privateKey);
    expect(verifyControlSignature(point.toString('base64'), CHALLENGE, der.toString('base64'))).toBe(true);
    // The same signature over another challenge is worthless.
    const other = toBase64(utf8Encode('screen-control-challenge:s2:1760000001'));
    expect(verifyControlSignature(point.toString('base64'), other, der.toString('base64'))).toBe(false);
  });

  it('comes back as authentication-failed when the prompt is dismissed', async () => {
    const native = fakeControlKey();
    await createControlKey(context(native));
    native.nextPrompt = { code: 'E_AUTH_CANCELLED' };
    expect(await signControlChallenge(context(native), CHALLENGE)).toEqual({
      ok: false,
      failure: 'authentication-failed',
    });
  });

  it('says not-enrolled when this phone holds no key for the host', async () => {
    expect(await signControlChallenge(context(), CHALLENGE)).toEqual({
      ok: false,
      failure: 'not-enrolled',
    });
  });

  it('says unsupported where there is no Keystore module, as on iOS', async () => {
    const ios: ControlKeyContext = { hostId: 'h1', native: undefined, prompt: PROMPT };
    expect(await signControlChallenge(ios, CHALLENGE)).toEqual({ ok: false, failure: 'unsupported' });
    expect(await readControlPublicKey(ios)).toBeUndefined();
    await expect(createControlKey(ios)).rejects.toThrow();
  });

  it('maps each Keystore error code to a failure the screen can explain', () => {
    expect(controlKeyFailureFor({ code: 'E_KEY_INVALIDATED' })).toBe('key-invalidated');
    expect(controlKeyFailureFor({ code: 'E_NO_AUTHENTICATOR' })).toBe('no-authenticator');
    expect(controlKeyFailureFor({ code: 'E_AUTH_FAILED' })).toBe('authentication-failed');
    expect(controlKeyFailureFor({ code: 'E_NO_KEY' })).toBe('not-enrolled');
    expect(controlKeyFailureFor(new Error('anything'))).toBe('keystore');
  });
});
