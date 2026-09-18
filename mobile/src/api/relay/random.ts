import * as ExpoCrypto from 'expo-crypto';

/** Cryptographically strong random bytes, backed by `expo-crypto`. */
export function randomBytes(length: number): Uint8Array {
  return ExpoCrypto.getRandomValues(new Uint8Array(length));
}

/**
 * `@noble/*` reaches for `globalThis.crypto.getRandomValues`, which React Native does
 * not provide. Call this once at app start so the libraries have a source of entropy.
 */
export function installCryptoPolyfill(): void {
  const current = (globalThis as { crypto?: { getRandomValues?: unknown } }).crypto;
  if (current && typeof current.getRandomValues === 'function') return;
  const shim = {
    ...(current ?? {}),
    getRandomValues: <T extends ArrayBufferView>(array: T): T =>
      ExpoCrypto.getRandomValues(array as unknown as Uint8Array) as unknown as T,
  };
  Object.defineProperty(globalThis, 'crypto', {
    value: shim,
    configurable: true,
    writable: true,
  });
}
