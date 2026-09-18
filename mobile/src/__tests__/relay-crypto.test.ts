import {
  DIRECTION_CLIENT_TO_HOST,
  DIRECTION_HOST_TO_CLIENT,
  NONCE_LENGTH,
  RelayCipher,
  RelayCryptoError,
  TAG_LENGTH,
  buildNonce,
  bytesEqual,
  deriveSessionKey,
  fromBase64,
  generateHandshakeNonce,
  generateKeyPair,
  parseNonce,
  randomUuid,
  toBase64,
  toBase64Url,
  utf8Encode,
} from '@/api/relay/crypto';

/** Runs the docs/relay.md handshake between a client and a host in-process. */
function handshake() {
  const client = generateKeyPair();
  const host = generateKeyPair();
  const clientNonce = generateHandshakeNonce();
  const serverNonce = generateHandshakeNonce();

  const clientKey = deriveSessionKey({
    secretKey: client.secretKey,
    peerPublicKey: host.publicKey,
    clientNonce,
    serverNonce,
  });
  const hostKey = deriveSessionKey({
    secretKey: host.secretKey,
    peerPublicKey: client.publicKey,
    clientNonce,
    serverNonce,
  });
  return {
    clientKey,
    hostKey,
    clientCipher: new RelayCipher(clientKey, DIRECTION_CLIENT_TO_HOST, DIRECTION_HOST_TO_CLIENT),
    hostCipher: new RelayCipher(hostKey, DIRECTION_HOST_TO_CLIENT, DIRECTION_CLIENT_TO_HOST),
  };
}

describe('deriveSessionKey', () => {
  it('agrees on both sides and is 32 bytes', () => {
    const { clientKey, hostKey } = handshake();
    expect(clientKey).toHaveLength(32);
    expect(bytesEqual(clientKey, hostKey)).toBe(true);
  });

  it('changes when either handshake nonce changes', () => {
    const client = generateKeyPair();
    const host = generateKeyPair();
    const clientNonce = generateHandshakeNonce();
    const base = deriveSessionKey({
      secretKey: client.secretKey,
      peerPublicKey: host.publicKey,
      clientNonce,
      serverNonce: new Uint8Array(16),
    });
    const other = deriveSessionKey({
      secretKey: client.secretKey,
      peerPublicKey: host.publicKey,
      clientNonce,
      serverNonce: Uint8Array.from({ length: 16 }, () => 7),
    });
    expect(bytesEqual(base, other)).toBe(false);
  });

  it('rejects a public key that yields an all-zero shared secret', () => {
    const client = generateKeyPair();
    expect(() =>
      deriveSessionKey({
        secretKey: client.secretKey,
        peerPublicKey: new Uint8Array(32),
        clientNonce: new Uint8Array(16),
        serverNonce: new Uint8Array(16),
      }),
    ).toThrow(RelayCryptoError);
  });
});

describe('nonce layout', () => {
  it('is [direction 1B][0,0,0][counter 8B big-endian]', () => {
    expect(Array.from(buildNonce(DIRECTION_CLIENT_TO_HOST, 0n))).toEqual([
      1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ]);
    expect(Array.from(buildNonce(DIRECTION_HOST_TO_CLIENT, 258n))).toEqual([
      2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2,
    ]);
    expect(Array.from(buildNonce(DIRECTION_CLIENT_TO_HOST, 0xdeadbeefn))).toEqual([
      1, 0, 0, 0, 0, 0, 0, 0, 0xde, 0xad, 0xbe, 0xef,
    ]);
  });

  it('round-trips through parseNonce and rejects dirty reserved bytes', () => {
    expect(parseNonce(buildNonce(2, 42n))).toEqual({ direction: 2, counter: 42n });
    const dirty = buildNonce(1, 1n);
    dirty[2] = 9;
    expect(() => parseNonce(dirty)).toThrow(RelayCryptoError);
  });
});

describe('RelayCipher', () => {
  it('round-trips both directions and increments counters per frame', () => {
    const { clientCipher, hostCipher } = handshake();

    const first = clientCipher.seal(utf8Encode('hello'));
    const second = clientCipher.seal(utf8Encode('world'));
    expect(parseNonce(first.slice(0, NONCE_LENGTH))).toEqual({ direction: 1, counter: 0n });
    expect(parseNonce(second.slice(0, NONCE_LENGTH))).toEqual({ direction: 1, counter: 1n });
    expect(first).toHaveLength(NONCE_LENGTH + 'hello'.length + TAG_LENGTH);

    expect(Buffer.from(hostCipher.open(first)).toString()).toBe('hello');
    expect(Buffer.from(hostCipher.open(second)).toString()).toBe('world');

    const reply = hostCipher.sealJson({ id: 'a', status: 200, body: { protocol: 1 } });
    expect(parseNonce(reply.slice(0, NONCE_LENGTH))).toEqual({ direction: 2, counter: 0n });
    expect(clientCipher.openJson(reply)).toEqual({ id: 'a', status: 200, body: { protocol: 1 } });
  });

  it('rejects a replayed frame', () => {
    const { clientCipher, hostCipher } = handshake();
    const frame = clientCipher.sealJson({ type: 'ping' });
    expect(hostCipher.openJson(frame)).toEqual({ type: 'ping' });
    expect(() => hostCipher.open(frame)).toThrow(/증가하지 않았습니다/);
  });

  it('rejects a reordered (lower counter) frame', () => {
    const { clientCipher, hostCipher } = handshake();
    const first = clientCipher.seal(utf8Encode('1'));
    const second = clientCipher.seal(utf8Encode('2'));
    hostCipher.open(second);
    expect(() => hostCipher.open(first)).toThrow(RelayCryptoError);
  });

  it('rejects a frame sent in the wrong direction', () => {
    const { clientCipher } = handshake();
    const ownFrame = clientCipher.seal(utf8Encode('x'));
    expect(() => clientCipher.open(ownFrame)).toThrow(/방향/);
  });

  it('rejects a tampered ciphertext', () => {
    const { clientCipher, hostCipher } = handshake();
    const frame = clientCipher.seal(utf8Encode('payload'));
    frame[frame.length - 1] = (frame[frame.length - 1] ?? 0) ^ 0xff;
    expect(() => hostCipher.open(frame)).toThrow(/복호화/);
  });

  it('rejects a truncated frame', () => {
    const { hostCipher } = handshake();
    expect(() => hostCipher.open(new Uint8Array(10))).toThrow(RelayCryptoError);
  });
});

describe('base64 helpers', () => {
  it('round-trips standard and url-safe encodings', () => {
    const bytes = Uint8Array.from([0xfb, 0xff, 0x00, 0x10, 0x3e, 0x7c]);
    expect(toBase64(bytes)).toBe('+/8AED58');
    expect(toBase64Url(bytes)).toBe('-_8AED58');
    expect(Array.from(fromBase64(toBase64(bytes)))).toEqual(Array.from(bytes));
    expect(Array.from(fromBase64(toBase64Url(bytes)))).toEqual(Array.from(bytes));
  });

  it('pads standard base64 and omits padding in the url variant', () => {
    expect(toBase64(Uint8Array.from([1]))).toBe('AQ==');
    expect(toBase64Url(Uint8Array.from([1]))).toBe('AQ');
    expect(Array.from(fromBase64('AQ'))).toEqual([1]);
  });

  it('rejects non-base64 input', () => {
    expect(() => fromBase64('not*base64')).toThrow(RelayCryptoError);
  });
});

describe('randomUuid', () => {
  it('produces distinct v4 uuids', () => {
    const a = randomUuid();
    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(a).not.toBe(randomUuid());
  });
});
