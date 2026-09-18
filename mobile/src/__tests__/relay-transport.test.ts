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
} from '@/api/relay/crypto';
import {
  RelayConnection,
  RelayError,
  buildRelayUrl,
  mapCloseCode,
  type RelaySocket,
} from '@/api/relay/transport';
import { createClient } from '@/api/client';

const PAIRING_KEY = 'pairing-key-under-test';
const SERVER_ID = 'mac-studio.local';

interface FakeOptions {
  /** Close every client socket with this code instead of talking. */
  rejectWithCode?: number;
  pairingKey?: string;
  /** Sent right after `auth_ok`. */
  notifyAfterAuth?: { scope: string; revision: number };
}

interface FakeRelay {
  url: string;
  hostPublicKeyB64: string;
  /** Plaintext envelopes the fake host received after authentication. */
  received: unknown[];
  queries: URLSearchParams[];
  stop: () => Promise<void>;
}

/**
 * An in-process fake that plays both the Node relay (URL contract, close codes) and
 * the Swift host (handshake, auth, tunnelled m1 answers) using the real crypto module.
 */
async function startFakeRelay(options: FakeOptions = {}): Promise<FakeRelay> {
  const hostKeys = generateKeyPair();
  const server = new WebSocketServer({ port: 0 });
  const received: unknown[] = [];
  const queries: URLSearchParams[] = [];

  server.on('connection', (socket, request) => {
    const query = new URLSearchParams((request.url ?? '').split('?')[1] ?? '');
    queries.push(query);

    if (options.rejectWithCode) {
      socket.close(options.rejectWithCode, 'rejected');
      return;
    }
    if (query.get('role') !== 'client' || !query.get('connectionId') || query.get('v') !== '1') {
      socket.close(4400, 'bad query');
      return;
    }

    let cipher: RelayCipher | undefined;

    socket.on('message', (raw: RawData, isBinary: boolean) => {
      if (!isBinary) {
        const hello = JSON.parse(raw.toString()) as { type: string; clientKey: string; nonce: string };
        if (hello.type !== 'hello') {
          socket.close(4400, 'expected hello');
          return;
        }
        const serverNonce = generateHandshakeNonce();
        const key = deriveSessionKey({
          secretKey: hostKeys.secretKey,
          peerPublicKey: fromBase64(hello.clientKey),
          clientNonce: fromBase64(hello.nonce),
          serverNonce,
        });
        cipher = new RelayCipher(key, DIRECTION_HOST_TO_CLIENT, DIRECTION_CLIENT_TO_HOST);
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

      if (!cipher) return;
      const message = cipher.openJson(new Uint8Array(raw as Buffer)) as {
        type?: string;
        pairingKey?: string;
        id?: string;
        method?: string;
        path?: string;
        body?: unknown;
      };
      received.push(message);

      if (message.type === 'auth') {
        if (message.pairingKey !== (options.pairingKey ?? PAIRING_KEY)) {
          socket.send(cipher.sealJson({ type: 'auth_error', reason: 'pairing-key' }));
          // The Swift host cannot emit 4401, so it closes normally instead.
          setTimeout(() => socket.close(1008, 'unauthorized'), 5);
          return;
        }
        socket.send(
          cipher.sealJson({
            type: 'auth_ok',
            hostName: '유진 맥북',
            hostId: 'host-1',
            appVersion: '1.2.3',
          }),
        );
        if (options.notifyAfterAuth) {
          socket.send(cipher.sealJson({ type: 'notify', ...options.notifyAfterAuth }));
        }
        return;
      }

      if (message.type === 'ping') {
        socket.send(cipher.sealJson({ type: 'pong' }));
        return;
      }

      if (typeof message.id === 'string') {
        const missing = message.path?.includes('missing') ?? false;
        socket.send(
          cipher.sealJson({
            id: message.id,
            status: missing ? 404 : 200,
            body: missing
              ? { protocol: 1, error: '세션이 없습니다' }
              : {
                  protocol: 1,
                  revision: 7,
                  echo: { method: message.method, path: message.path, body: message.body },
                },
          }),
        );
      }
    });
  });

  await new Promise<void>((resolve) => server.once('listening', resolve));
  const { port } = server.address() as AddressInfo;

  return {
    url: `ws://127.0.0.1:${port}`,
    hostPublicKeyB64: toBase64(hostKeys.publicKey),
    received,
    queries,
    stop: () =>
      new Promise<void>((resolve) => {
        for (const client of server.clients) client.terminate();
        server.close(() => resolve());
      }),
  };
}

function nodeSocketFactory(url: string): RelaySocket {
  return new WebSocket(url) as unknown as RelaySocket;
}

function connect(relay: FakeRelay, overrides: Partial<{ pairingKey: string; hostPublicKeyB64: string; autoReconnect: boolean }> = {}) {
  return new RelayConnection(
    {
      serverId: SERVER_ID,
      relayUrl: relay.url,
      hostPublicKeyB64: overrides.hostPublicKeyB64 ?? relay.hostPublicKeyB64,
      pairingKey: overrides.pairingKey ?? PAIRING_KEY,
    },
    {
      clientName: 'Test Phone',
      autoReconnect: overrides.autoReconnect ?? false,
      createSocket: nodeSocketFactory,
      requestTimeoutMs: 5_000,
    },
  );
}

describe('buildRelayUrl', () => {
  it('builds the documented client query string', () => {
    expect(buildRelayUrl('wss://relay.example.com:8787/', 'sid:1', 'uuid-1')).toBe(
      'wss://relay.example.com:8787/ws?serverId=sid%3A1&role=client&connectionId=uuid-1&v=1',
    );
  });
});

describe('mapCloseCode', () => {
  it('maps the relay close codes to failures', () => {
    expect(mapCloseCode(4404)).toBe('host-offline');
    expect(mapCloseCode(4410)).toBe('host-offline');
    expect(mapCloseCode(4504)).toBe('host-not-attached');
    expect(mapCloseCode(4401)).toBe('unpaired');
    expect(mapCloseCode(4429)).toBe('too-many');
    expect(mapCloseCode(1000)).toBeUndefined();
  });
});

describe('RelayConnection against a fake host', () => {
  jest.setTimeout(15_000);

  it('handshakes, authenticates, tunnels a request and delivers a notify', async () => {
    const relay = await startFakeRelay({ notifyAfterAuth: { scope: 'state', revision: 12 } });
    const connection = connect(relay);
    const notifications: { scope: string; revision: number }[] = [];
    connection.onNotify((event) => notifications.push(event));

    const info = await connection.ready();
    expect(info).toEqual({ hostName: '유진 맥북', hostId: 'host-1', appVersion: '1.2.3' });
    expect(connection.state).toBe('ready');

    const query = relay.queries[0];
    expect(query?.get('serverId')).toBe(SERVER_ID);
    expect(query?.get('role')).toBe('client');
    expect(query?.get('v')).toBe('1');
    expect(query?.get('connectionId')).toMatch(/^[0-9a-f-]{36}$/);

    const response = await connection.request('GET', '/m1/state?since=3&wait=10');
    expect(response.status).toBe(200);
    expect(response.body).toMatchObject({
      protocol: 1,
      echo: { method: 'GET', path: '/m1/state?since=3&wait=10' },
    });

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(notifications).toEqual([{ scope: 'state', revision: 12 }]);

    expect(relay.received[0]).toEqual({
      type: 'auth',
      pairingKey: PAIRING_KEY,
      clientName: 'Test Phone',
    });

    connection.close();
    await relay.stop();
  });

  it('serves the m1 client surface over the tunnel', async () => {
    const relay = await startFakeRelay();
    const connection = connect(relay);
    await connection.ready();
    const client = createClient(connection);

    const submitted = await client.submit('a/b c', '안녕');
    expect(submitted).toMatchObject({
      echo: { method: 'POST', path: '/m1/sessions/a%2Fb%20c/submit', body: { text: '안녕' } },
    });

    const state = await client.state({ since: 3, wait: 99 });
    expect(state).toMatchObject({ echo: { path: '/m1/state?since=3&wait=10' } });

    await expect(client.session('missing')).rejects.toMatchObject({
      name: 'ApiError',
      status: 404,
      message: '세션이 없습니다',
    });

    connection.close();
    await relay.stop();
  });

  it('surfaces an encrypted auth_error as "재페어링 필요" and stops retrying', async () => {
    const relay = await startFakeRelay({ pairingKey: 'a-different-key' });
    const connection = connect(relay, { autoReconnect: true });

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(RelayError);
    expect((error as RelayError).failure).toBe('unpaired');
    expect((error as RelayError).message).toBe('재페어링 필요');
    expect((error as RelayError).needsRepair).toBe(true);

    await expect(connection.request('GET', '/m1/info')).rejects.toBeInstanceOf(RelayError);

    connection.close();
    await relay.stop();
  });

  it('rejects a host whose public key does not match the pairing record', async () => {
    const relay = await startFakeRelay();
    const connection = connect(relay, {
      hostPublicKeyB64: toBase64(generateKeyPair().publicKey),
    });

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('unpaired');

    connection.close();
    await relay.stop();
  });

  it('maps a 4404 close from the relay to "호스트 오프라인"', async () => {
    const relay = await startFakeRelay({ rejectWithCode: 4404 });
    const connection = connect(relay);

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('host-offline');
    expect((error as RelayError).message).toBe('호스트 오프라인');

    connection.close();
    await relay.stop();
  });

  it('reports "릴레이 연결 안 됨" when the relay itself refuses the socket', async () => {
    const connection = new RelayConnection(
      {
        serverId: SERVER_ID,
        relayUrl: 'ws://127.0.0.1:1',
        hostPublicKeyB64: toBase64(generateKeyPair().publicKey),
        pairingKey: PAIRING_KEY,
      },
      { autoReconnect: false, createSocket: nodeSocketFactory },
    );

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('relay-unreachable');
    expect((error as RelayError).message).toBe('릴레이 연결 안 됨');
    connection.close();
  });
});
