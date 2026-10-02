import { p256 } from '@noble/curves/p256';
import { sha256 } from '@noble/hashes/sha2';
import { fromBase64, toBase64 } from '@/api/relay/crypto';
import type { ControlKeyNative, ControlKeyPrompt } from '@/lib/screen-share/control-key';

/**
 * Stands in for `modules/control-key`: the private key stays inside this object exactly as
 * it stays inside the Keystore, and every signature goes through a "prompt" the test
 * controls. The signature is what `SHA256withECDSA` produces on Android: ECDSA over
 * SHA-256(challenge), DER encoded.
 */
export interface FakeControlKey extends ControlKeyNative {
  /** Every prompt raised, in order. */
  prompts: ControlKeyPrompt[];
  generated: string[];
  removed: string[];
  /** What the next prompt does; `undefined` lets it succeed. */
  nextPrompt: { code: string } | undefined;
  has(alias: string): boolean;
}

export function fakeControlKey(options: { supported?: boolean } = {}): FakeControlKey {
  const keys = new Map<string, Uint8Array>();
  const fake: FakeControlKey = {
    prompts: [],
    generated: [],
    removed: [],
    nextPrompt: undefined,
    has: (alias) => keys.has(alias),
    isSupported: () => options.supported ?? true,
    async publicKey(alias) {
      const secret = keys.get(alias);
      return secret ? toBase64(p256.getPublicKey(secret, false)) : null;
    },
    async generate(alias) {
      const secret = p256.utils.randomSecretKey();
      keys.set(alias, secret);
      fake.generated.push(alias);
      return toBase64(p256.getPublicKey(secret, false));
    },
    async remove(alias) {
      keys.delete(alias);
      fake.removed.push(alias);
    },
    async sign(alias, challengeB64, prompt) {
      const secret = keys.get(alias);
      if (!secret) throw Object.assign(new Error('no key'), { code: 'E_NO_KEY' });
      fake.prompts.push(prompt);
      const outcome = fake.nextPrompt;
      fake.nextPrompt = undefined;
      if (outcome) throw Object.assign(new Error(outcome.code), { code: outcome.code });
      const signature = p256.sign(sha256(fromBase64(challengeB64)), secret, { prehash: false });
      return toBase64(signature.toBytes('der'));
    },
  };
  return fake;
}
