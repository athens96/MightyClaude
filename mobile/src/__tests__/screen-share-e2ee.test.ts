import { AddressInfo } from 'node:net';
import { WebSocket, WebSocketServer, type RawData } from 'ws';
import {
  DIRECTION_CLIENT_TO_HOST,
  DIRECTION_HOST_TO_CLIENT,
  RelayCipher,
  deriveSessionKey,
  fromBase64,
  generateHandshakeNonce,
  generateKeyPair,
  toBase64,
  utf8Decode,
} from '@/api/relay/crypto';
import { RelayConnection, type RelaySocket } from '@/api/relay/transport';
import { createClient } from '@/api/client';
import type { ScreenSignal } from '@/lib/screen-share/signalling';

/**
 * Screen-share signalling rides the channel that was already there. This is the proof that
 * it is sealed on the way: the bytes the relay forwards are ciphertext — no SDP, no ICE
 * candidate, no kill reason is readable in them — and the phone still reads the frames.
 */

const PAIRING_KEY = 'pairing-key-under-test';
const SERVER_ID = 'mac-studio.local';

interface Fake {
  url: string;
  hostPublicKeyB64: string;
  /** Exactly what crossed the wire after the handshake, undecrypted. */
  wire: Buffer[];
  /** Plaintext the fake host read out of those frames. */
  received: Record<string, unknown>[];
  /** Seals one host→client frame. */
  push: (message: Record<string, unknown>) => void;
  stop: () => Promise<void>;
}

async function startFake(): Promise<Fake> {
  const hostKeys = generateKeyPair();
  const server = new WebSocketServer({ port: 0 });
  const wire: Buffer[] = [];
  const received: Record<string, unknown>[] = [];
  let cipher: RelayCipher | undefined;
  let live: WebSocket | undefined;

  server.on('connection', (socket) => {
    live = socket;
    socket.on('message', (raw: RawData, isBinary: boolean) => {
      if (!isBinary) {
        const hello = JSON.parse(raw.toString()) as { clientKey: string; nonce: string };
        const serverNonce = generateHandshakeNonce();
        cipher = new RelayCipher(
          deriveSessionKey({
            secretKey: hostKeys.secretKey,
            peerPublicKey: fromBase64(hello.clientKey),
            clientNonce: fromBase64(hello.nonce),
            serverNonce,
          }),
          DIRECTION_HOST_TO_CLIENT,
          DIRECTION_CLIENT_TO_HOST,
        );
        socket.send(
          JSON.stringify({
            type: 'ready',
            v: 1,
            serverKey: toBase64(hostKeys.publicKey),
            nonce: toBase64(serverNonce),
          }),
        );
        return;
      }
      const frame = raw as Buffer;
      wire.push(Buffer.from(frame));
      if (!cipher) return;
      const message = cipher.openJson(new Uint8Array(frame)) as Record<string, unknown>;
      received.push(message);
      if (message.type === 'auth') {
        socket.send(
          cipher.sealJson({ type: 'auth_ok', hostName: 'mac', hostId: 'h1', appVersion: '1.0.0' }),
        );
      }
    });
  });

  await new Promise<void>((resolve) => server.once('listening', resolve));
  const { port } = server.address() as AddressInfo;
  return {
    url: `ws://127.0.0.1:${port}`,
    hostPublicKeyB64: toBase64(hostKeys.publicKey),
    wire,
    received,
    push: (message) => {
      if (cipher && live) live.send(cipher.sealJson(message));
    },
    stop: () =>
      new Promise<void>((resolve) => {
        for (const client of server.clients) client.terminate();
        server.close(() => resolve());
      }),
  };
}

const fakes: Fake[] = [];
const opened: RelayConnection[] = [];

afterEach(async () => {
  for (const connection of opened.splice(0)) connection.close();
  for (const fake of fakes.splice(0)) await fake.stop();
});

function nodeSocketFactory(url: string): RelaySocket {
  return new WebSocket(url) as unknown as RelaySocket;
}

async function connect() {
  const fake = await startFake();
  fakes.push(fake);
  const connection = new RelayConnection(
    {
      serverId: SERVER_ID,
      relayUrl: fake.url,
      hostPublicKeyB64: fake.hostPublicKeyB64,
      pairingKey: PAIRING_KEY,
      clientId: 'c1',
    },
    { createSocket: nodeSocketFactory, autoReconnect: false },
  );
  opened.push(connection);
  await connection.ready();
  return { fake, connection, client: createClient(connection) };
}

/** Lets the socket's own callbacks run. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 20));

const OFFER = {
  type: 'screen-offer',
  sessionId: 's1',
  sdp: 'v=0\r\na=sendonly\r\n',
  mode: 'control',
  displayId: 1,
  codec: 'H264',
  quality: { width: 1920, height: 1080, fps: 30, maxBitrateKbps: 6000 },
  iceRestart: false,
};

describe('signalling inside the E2EE channel', () => {
  it('reaches the phone from the Mac, offer and candidate alike', async () => {
    const { fake, client } = await connect();
    const seen: ScreenSignal[] = [];
    client.onScreenSignal((signal) => seen.push(signal));

    fake.push(OFFER);
    fake.push({ type: 'screen-ice', sessionId: 's1', candidate: 'candidate:1 typ host', sdpMid: '0' });
    fake.push({ type: 'screen-kill', reason: 'kill-switch' });
    await settle();

    expect(seen.map((signal) => signal.type)).toEqual([
      'screen-offer',
      'screen-ice',
      'screen-kill',
    ]);
    expect(seen[0]).toMatchObject({ sdp: 'v=0\r\na=sendonly\r\n', codec: 'H264' });
  });

  it('leaves the relay nothing to read: the SDP never appears in the bytes it forwards', async () => {
    const { fake, client } = await connect();
    const before = fake.wire.length;
    expect(client.sendScreenSignal({ type: 'screen-answer', sessionId: 's1', sdp: 'v=0\r\nsecretish' })).toBe(
      true,
    );
    await settle();

    const frames = fake.wire.slice(before);
    expect(frames.length).toBeGreaterThan(0);
    for (const frame of frames) {
      const asText = frame.toString('utf8');
      expect(asText).not.toContain('screen-answer');
      expect(asText).not.toContain('secretish');
      expect(asText).not.toContain('v=0');
    }
    // The Mac, which holds the key, reads it perfectly.
    expect(fake.received).toContainEqual({
      type: 'screen-answer',
      sessionId: 's1',
      sdp: 'v=0\r\nsecretish',
    });
  });

  it('seals a session end and an ICE candidate the same way', async () => {
    const { fake, client } = await connect();
    client.sendScreenSignal({ type: 'screen-ice', sessionId: 's1', candidate: '' });
    client.sendScreenSignal({ type: 'screen-session-end', sessionId: 's1', reason: 'user-stop' });
    await settle();
    expect(fake.received).toContainEqual({ type: 'screen-ice', sessionId: 's1', candidate: '' });
    expect(fake.received).toContainEqual({
      type: 'screen-session-end',
      sessionId: 's1',
      reason: 'user-stop',
    });
    for (const frame of fake.wire) {
      expect(utf8Decode(new Uint8Array(frame))).not.toContain('screen-');
    }
  });

  it('drops a screen-… type this build does not know, and keeps the tunnel', async () => {
    const { fake, client, connection } = await connect();
    const seen: ScreenSignal[] = [];
    client.onScreenSignal((signal) => seen.push(signal));

    fake.push({ type: 'screen-teleport', sessionId: 's1' });
    fake.push({ type: 'screen-offer', sessionId: 's1', sdp: '' });
    fake.push(OFFER);
    await settle();

    expect(seen).toHaveLength(1);
    expect(seen[0]?.type).toBe('screen-offer');
    // Neither the unknown name nor the malformed offer cost us the connection.
    expect(connection.state).toBe('ready');
    expect(client.sendScreenSignal({ type: 'screen-answer', sessionId: 's1', sdp: 'v=0' })).toBe(true);
  });

  it('is unsendable once the tunnel is closed, so the phone knows the Mac did not hear it', async () => {
    const { connection, client } = await connect();
    connection.close();
    expect(client.sendScreenSignal({ type: 'screen-session-end', sessionId: 's1', reason: 'user-stop' })).toBe(
      false,
    );
  });

  it('refuses to put a frame over the plaintext budget on the channel at all', async () => {
    const { client } = await connect();
    expect(
      client.sendScreenSignal({
        type: 'screen-answer',
        sessionId: 's1',
        sdp: 'a'.repeat(70 * 1024),
      }),
    ).toBe(false);
  });
});
