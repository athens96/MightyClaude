/* eslint-env node */
const { webcrypto } = require('node:crypto');

// `@noble/*` reaches for `globalThis.crypto.getRandomValues`.
if (!globalThis.crypto || typeof globalThis.crypto.getRandomValues !== 'function') {
  Object.defineProperty(globalThis, 'crypto', { value: webcrypto, configurable: true });
}

// Keep the suite free of native modules: expo-crypto is Node's webcrypto here.
jest.mock('expo-crypto', () => {
  const nodeCrypto = require('node:crypto').webcrypto;
  return {
    getRandomValues: (array) => nodeCrypto.getRandomValues(array),
    randomUUID: () => nodeCrypto.randomUUID(),
  };
});

jest.mock('expo-device', () => ({ modelName: 'Test Phone', deviceName: 'Test Phone' }));
