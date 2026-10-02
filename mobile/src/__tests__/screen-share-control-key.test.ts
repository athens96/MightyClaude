import { toBase64, utf8Encode } from '@/api/relay/crypto';
import {
  controlKeyStorageKey,
  enrollControlKey,
  forgetControlKey,
  hasControlKey,
  signControlChallenge,
  verifyControlSignature,
  type SecureKeyStore,
  type SecureKeyStoreOptions,
} from '@/lib/screen-share/control-key';

/**
 * Starting control needs the phone's owner every session. The private key is sealed behind
 * a biometric prompt — reading it is what raises the fingerprint sheet — and the **Mac**
 * verifies the signature. A phone that merely checked a fingerprint locally would get
 * nowhere: without a signature the Mac answers `control-signature`.
 */

interface Recorded {
  key: string;
  options?: SecureKeyStoreOptions;
}

/** Stands in for `expo-secure-store`, and records how it was asked. */
function fakeStore(options: { denyRead?: boolean } = {}) {
  const items = new Map<string, string>();
  const reads: Recorded[] = [];
  const writes: Recorded[] = [];
  const store: SecureKeyStore = {
    async getItemAsync(key, opts) {
      reads.push({ key, ...(opts === undefined ? {} : { options: opts }) });
      // A dismissed fingerprint prompt is a rejected read, which is what the OS does.
      if (options.denyRead) throw new Error('user cancelled');
      return items.get(key) ?? null;
    },
    async setItemAsync(key, value, opts) {
      writes.push({ key, ...(opts === undefined ? {} : { options: opts }) });
      items.set(key, value);
    },
    async deleteItemAsync(key) {
      items.delete(key);
    },
  };
  return { store, items, reads, writes };
}

const CHALLENGE = toBase64(utf8Encode('screen-control-challenge:s1:1760000000'));

describe('enrolling the control key', () => {
  it('seals it behind a biometric prompt, under a key of this host’s own', async () => {
    const fake = fakeStore();
    const context = { hostId: 'h1', store: fake.store, prompt: '본인 확인' };
    const enrolment = await enrollControlKey(context);

    expect(fake.writes).toHaveLength(1);
    expect(fake.writes[0]?.key).toBe(controlKeyStorageKey('h1'));
    expect(fake.writes[0]?.options?.requireAuthentication).toBe(true);
    expect(fake.writes[0]?.options?.authenticationPrompt).toBe('본인 확인');
    // An uncompressed P-256 point: 65 bytes, which is what the Mac stores.
    expect(enrolment.publicKeyB64).toMatch(/^[A-Za-z0-9+/]+=*$/);
    expect(Buffer.from(enrolment.publicKeyB64, 'base64')).toHaveLength(65);
  });

  it('keeps one key per host, so unpairing one leaves the others alone', async () => {
    const fake = fakeStore();
    await enrollControlKey({ hostId: 'h1', store: fake.store, prompt: 'p' });
    await enrollControlKey({ hostId: 'h2', store: fake.store, prompt: 'p' });
    expect([...fake.items.keys()].sort()).toEqual([
      controlKeyStorageKey('h1'),
      controlKeyStorageKey('h2'),
    ]);
  });

  it('knows whether this host has a key yet', async () => {
    const fake = fakeStore();
    const context = { hostId: 'h1', store: fake.store, prompt: 'p' };
    expect(await hasControlKey(context)).toBe(false);
    await enrollControlKey(context);
    expect(await hasControlKey(context)).toBe(true);
    await forgetControlKey(context);
    expect(await hasControlKey(context)).toBe(false);
  });
});

describe('signing a control challenge', () => {
  it('produces a signature the Mac’s stored public key verifies', async () => {
    const fake = fakeStore();
    const context = { hostId: 'h1', store: fake.store, prompt: 'p' };
    const enrolment = await enrollControlKey(context);
    const signature = await signControlChallenge(context, CHALLENGE);

    expect(signature.ok).toBe(true);
    if (!signature.ok) return;
    expect(verifyControlSignature(enrolment.publicKeyB64, CHALLENGE, signature.signatureB64)).toBe(
      true,
    );
    // DER, as the contract says, not a bare 64-byte pair.
    expect(Buffer.from(signature.signatureB64, 'base64')[0]).toBe(0x30);
  });

  it('raises the biometric prompt on every signature, not once per install', async () => {
    const fake = fakeStore();
    const context = { hostId: 'h1', store: fake.store, prompt: '조작 확인' };
    await enrollControlKey(context);
    fake.reads.length = 0;
    await signControlChallenge(context, CHALLENGE);
    await signControlChallenge(context, CHALLENGE);
    expect(fake.reads).toHaveLength(2);
    for (const read of fake.reads) {
      expect(read.options?.requireAuthentication).toBe(true);
      expect(read.options?.authenticationPrompt).toBe('조작 확인');
    }
  });

  it('fails when the user dismisses the prompt, so no control session opens', async () => {
    const denied = fakeStore({ denyRead: true });
    const context = { hostId: 'h1', store: denied.store, prompt: 'p' };
    expect(await signControlChallenge(context, CHALLENGE)).toEqual({
      ok: false,
      failure: 'authentication-failed',
    });
  });

  it('fails when nothing has been enrolled for this host', async () => {
    const fake = fakeStore();
    expect(
      await signControlChallenge({ hostId: 'h9', store: fake.store, prompt: 'p' }, CHALLENGE),
    ).toEqual({ ok: false, failure: 'not-enrolled' });
  });

  it('fails on a stored value that is no longer a key', async () => {
    const fake = fakeStore();
    fake.items.set(controlKeyStorageKey('h1'), 'not-a-key');
    expect(
      await signControlChallenge({ hostId: 'h1', store: fake.store, prompt: 'p' }, CHALLENGE),
    ).toEqual({ ok: false, failure: 'corrupt' });
  });

  it('signs the challenge itself, so a signature for one challenge is useless for another', async () => {
    const fake = fakeStore();
    const context = { hostId: 'h1', store: fake.store, prompt: 'p' };
    const enrolment = await enrollControlKey(context);
    const signature = await signControlChallenge(context, CHALLENGE);
    expect(signature.ok).toBe(true);
    if (!signature.ok) return;
    const other = toBase64(utf8Encode('screen-control-challenge:s2:1760000099'));
    expect(verifyControlSignature(enrolment.publicKeyB64, other, signature.signatureB64)).toBe(false);
  });

  it('does not verify against another phone’s key', async () => {
    const fake = fakeStore();
    const mine = { hostId: 'h1', store: fake.store, prompt: 'p' };
    await enrollControlKey(mine);
    const signature = await signControlChallenge(mine, CHALLENGE);
    const stranger = await enrollControlKey({ hostId: 'h2', store: fake.store, prompt: 'p' });
    expect(signature.ok).toBe(true);
    if (!signature.ok) return;
    expect(verifyControlSignature(stranger.publicKeyB64, CHALLENGE, signature.signatureB64)).toBe(
      false,
    );
  });

  it('rejects rubbish rather than throwing', () => {
    expect(verifyControlSignature('not base64 !!', CHALLENGE, 'MA==')).toBe(false);
    expect(verifyControlSignature(toBase64(utf8Encode('short')), CHALLENGE, 'MA==')).toBe(false);
  });
});
