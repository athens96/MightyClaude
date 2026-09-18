import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { x25519 } from '@noble/curves/ed25519';
import {
  DIRECTION_CLIENT_TO_HOST,
  DIRECTION_HOST_TO_CLIENT,
  RelayCipher,
  deriveSessionKey,
  fromBase64,
  toBase64,
  utf8Decode,
  utf8Encode,
} from '@/api/relay/crypto';

/**
 * Vectors produced by the Swift host (native/macos/Tests/MightyCoreTests/RelayInteropVectorTests.swift).
 * Same keys and nonces must give the same session key and byte-identical frames,
 * so a mismatch here means the two implementations would not talk to each other.
 */
interface Vectors {
  hostSecretKeyB64: string;
  hostPublicKeyB64: string;
  clientSecretKeyB64: string;
  clientPublicKeyB64: string;
  clientNonceB64: string;
  serverNonceB64: string;
  sessionKeyB64: string;
  hostToClientFramesB64: string[];
  hostToClientPlaintexts: string[];
  clientToHostFramesB64: string[];
  clientToHostPlaintexts: string[];
}

const fixturePath = resolve(__dirname, '../../../native/contracts/relay-vectors.json');
const vectors = JSON.parse(readFileSync(fixturePath, 'utf8')) as Vectors;

describe('relay interop with the Swift host', () => {
  const hostSecret = fromBase64(vectors.hostSecretKeyB64);
  const clientSecret = fromBase64(vectors.clientSecretKeyB64);
  const clientNonce = fromBase64(vectors.clientNonceB64);
  const serverNonce = fromBase64(vectors.serverNonceB64);

  it('derives the same public keys and session key', () => {
    expect(toBase64(x25519.getPublicKey(hostSecret))).toBe(vectors.hostPublicKeyB64);
    expect(toBase64(x25519.getPublicKey(clientSecret))).toBe(vectors.clientPublicKeyB64);
    const fromClient = deriveSessionKey({
      secretKey: clientSecret,
      peerPublicKey: fromBase64(vectors.hostPublicKeyB64),
      clientNonce,
      serverNonce,
    });
    const fromHost = deriveSessionKey({
      secretKey: hostSecret,
      peerPublicKey: fromBase64(vectors.clientPublicKeyB64),
      clientNonce,
      serverNonce,
    });
    expect(toBase64(fromClient)).toBe(vectors.sessionKeyB64);
    expect(toBase64(fromHost)).toBe(vectors.sessionKeyB64);
  });

  it('opens frames the Swift host sealed, in order', () => {
    const key = fromBase64(vectors.sessionKeyB64);
    const client = new RelayCipher(key, DIRECTION_CLIENT_TO_HOST, DIRECTION_HOST_TO_CLIENT);
    vectors.hostToClientFramesB64.forEach((frame, index) => {
      expect(utf8Decode(client.open(fromBase64(frame)))).toBe(vectors.hostToClientPlaintexts[index]);
    });
    // Replaying the first frame after the second is rejected.
    expect(() => client.open(fromBase64(vectors.hostToClientFramesB64[0]!))).toThrow();
  });

  it('seals byte-identical frames to the Swift client vectors', () => {
    const key = fromBase64(vectors.sessionKeyB64);
    const client = new RelayCipher(key, DIRECTION_CLIENT_TO_HOST, DIRECTION_HOST_TO_CLIENT);
    vectors.clientToHostPlaintexts.forEach((plaintext, index) => {
      expect(toBase64(client.seal(utf8Encode(plaintext)))).toBe(vectors.clientToHostFramesB64[index]);
    });
  });
});
