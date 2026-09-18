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
  type AuthLease,
  type RelaySocket,
} from '@/api/relay/transport';
import { createClient } from '@/api/client';
import type { DeviceAuthState } from '@/lib/device-token';
import { createKeyedMutex } from '@/lib/keyed-mutex';

const PAIRING_KEY = 'pairing-key-under-test';
const SERVER_ID = 'mac-studio.local';

interface FakeOptions {
  /** Close every client socket with this code instead of talking. */
  rejectWithCode?: number;
  pairingKey?: string;
  /** Sent right after `auth_ok`. */
  notifyAfterAuth?: { scope: string; revision: number };
  /**
   * Issued once, to a client that presented a `clientId` with the pairing key — the
   * "기기 토큰" frame of docs/relay.md. Left out, the fake plays an older host.
   */
  issueDeviceToken?: string;
  /**
   * Tokens the fake recognises before it has issued any; anything else is answered
   * "device-revoked". A token the fake hands out is registered too, the way the Mac
   * writes it into `devices.json`.
   */
  acceptTokens?: string[];
  /** These ids already hold a token on this Mac, so the pairing key earns a conflict. */
  conflictingClientIds?: string[];
  /** Refuses every `auth` with this reason, whatever it carried. */
  rejectReason?: string;
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
  /** The Mac's `devices.json`: a token stays valid until the device is released. */
  const knownTokens = new Set(options.acceptTokens ?? []);

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
        clientId?: string;
        deviceToken?: string;
        id?: string;
        method?: string;
        path?: string;
        body?: unknown;
      };
      received.push(message);

      if (message.type === 'auth') {
        const reject = (reason: string) => {
          socket.send(cipher!.sealJson({ type: 'auth_error', reason }));
          // The Swift host cannot emit 4401, so it closes normally instead.
          setTimeout(() => socket.close(1008, 'unauthorized'), 5);
        };
        const accept = (deviceToken?: string) => {
          const body: Record<string, unknown> = {
            type: 'auth_ok',
            hostName: '유진 맥북',
            hostId: 'host-1',
            appVersion: '1.2.3',
          };
          if (deviceToken) {
            body.deviceToken = deviceToken;
            knownTokens.add(deviceToken);
          }
          socket.send(cipher!.sealJson(body));
          if (options.notifyAfterAuth) {
            socket.send(cipher!.sealJson({ type: 'notify', ...options.notifyAfterAuth }));
          }
        };

        if (options.rejectReason) {
          reject(options.rejectReason);
          return;
        }
        if (message.deviceToken !== undefined) {
          if (!message.clientId || !knownTokens.has(message.deviceToken)) {
            reject('device-revoked');
            return;
          }
          accept();
          return;
        }
        if (message.pairingKey !== (options.pairingKey ?? PAIRING_KEY)) {
          reject('pairing-key');
          return;
        }
        // The Mac already has a device under this id, so it refuses to make a second.
        if (message.clientId && (options.conflictingClientIds ?? []).includes(message.clientId)) {
          reject('device-conflict');
          return;
        }
        // A token is only issued to an app that identified itself.
        accept(message.clientId ? options.issueDeviceToken : undefined);
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

  let stopped = false;
  const fake: FakeRelay = {
    url: `ws://127.0.0.1:${port}`,
    hostPublicKeyB64: toBase64(hostKeys.publicKey),
    received,
    queries,
    stop: () =>
      new Promise<void>((resolve) => {
        if (stopped) {
          resolve();
          return;
        }
        stopped = true;
        for (const client of server.clients) client.terminate();
        server.close(() => resolve());
      }),
  };
  started.push(fake);
  return fake;
}

/**
 * Everything a test opened. A failed expectation throws before the test's own cleanup
 * runs, and a listening `WebSocketServer` is exactly the handle that would then keep
 * jest alive after the run, so the teardown happens here instead of per test.
 */
const started: FakeRelay[] = [];
const opened: RelayConnection[] = [];

afterEach(async () => {
  for (const connection of opened.splice(0)) connection.close();
  for (const relay of started.splice(0)) await relay.stop();
});

function nodeSocketFactory(url: string): RelaySocket {
  return new WebSocket(url) as unknown as RelaySocket;
}

interface ConnectOverrides {
  pairingKey?: string;
  hostPublicKeyB64?: string;
  autoReconnect?: boolean;
  clientId?: string;
  deviceToken?: string;
  /** Omits the pairing key entirely, as a phone that already holds a token would. */
  tokenOnly?: boolean;
  onDeviceToken?: (deviceToken: string) => void | Promise<void>;
  authorize?: (state: DeviceAuthState) => Promise<AuthLease>;
  mintClientId?: () => Promise<string | undefined>;
}

function connect(relay: FakeRelay, overrides: ConnectOverrides = {}) {
  return track(
    new RelayConnection(
      {
        serverId: SERVER_ID,
        relayUrl: relay.url,
        hostPublicKeyB64: overrides.hostPublicKeyB64 ?? relay.hostPublicKeyB64,
        pairingKey: overrides.tokenOnly ? undefined : (overrides.pairingKey ?? PAIRING_KEY),
        clientId: overrides.clientId,
        deviceToken: overrides.deviceToken,
      },
      {
        clientName: 'Test Phone',
        autoReconnect: overrides.autoReconnect ?? false,
        createSocket: nodeSocketFactory,
        requestTimeoutMs: 5_000,
        onDeviceToken: overrides.onDeviceToken,
        authorize: overrides.authorize,
        mintClientId: overrides.mintClientId,
      },
    ),
  );
}

/** Closed by the teardown above, whatever the test does with it. */
function track(connection: RelayConnection): RelayConnection {
  opened.push(connection);
  return connection;
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

  it('keeps sending the pairing key alone when no clientId has been generated', async () => {
    const relay = await startFakeRelay({ issueDeviceToken: 'token-1' });
    const connection = connect(relay);
    const info = await connection.ready();

    expect(relay.received[0]).toEqual({
      type: 'auth',
      pairingKey: PAIRING_KEY,
      clientName: 'Test Phone',
    });
    expect(info.deviceToken).toBeUndefined();
    expect(connection.deviceToken).toBeUndefined();

    connection.close();
    await relay.stop();
  });

  it('receives a device token on the first connection and reports it once', async () => {
    const relay = await startFakeRelay({ issueDeviceToken: 'token-1' });
    const issued: string[] = [];
    const connection = connect(relay, {
      clientId: 'client-1',
      onDeviceToken: (token) => {
        issued.push(token);
      },
    });

    const info = await connection.ready();
    expect(relay.received[0]).toEqual({
      type: 'auth',
      pairingKey: PAIRING_KEY,
      clientId: 'client-1',
      clientName: 'Test Phone',
    });
    expect(info.deviceToken).toBe('token-1');
    expect(issued).toEqual(['token-1']);
    // From here on the tunnel authenticates by token, not by the key it was paired with.
    expect(connection.deviceToken).toBe('token-1');

    connection.close();
    await relay.stop();
  });

  it('authenticates with the token alone once it holds one', async () => {
    const relay = await startFakeRelay({ acceptTokens: ['token-1'] });
    const connection = connect(relay, {
      clientId: 'client-1',
      deviceToken: 'token-1',
      tokenOnly: true,
    });

    await connection.ready();
    expect(relay.received[0]).toEqual({
      type: 'auth',
      clientId: 'client-1',
      deviceToken: 'token-1',
      clientName: 'Test Phone',
    });

    connection.close();
    await relay.stop();
  });

  it('migrates an already paired host on its next connect', async () => {
    // The phone still has only the pairing key; the host now knows tokens.
    const relay = await startFakeRelay({ issueDeviceToken: 'token-2' });
    const issued: string[] = [];
    const first = connect(relay, {
      clientId: 'client-1',
      onDeviceToken: (token) => {
        issued.push(token);
      },
    });
    await first.ready();
    first.close();

    const second = connect(relay, {
      clientId: 'client-1',
      deviceToken: issued[0],
      tokenOnly: true,
    });
    await second.ready();

    expect(issued).toEqual(['token-2']);
    expect(relay.received[0]).toMatchObject({ pairingKey: PAIRING_KEY, clientId: 'client-1' });
    expect(relay.received[1]).toEqual({
      type: 'auth',
      clientId: 'client-1',
      deviceToken: 'token-2',
      clientName: 'Test Phone',
    });

    second.close();
    await relay.stop();
  });

  it('surfaces a released device with its own message and stops retrying', async () => {
    const relay = await startFakeRelay({ acceptTokens: [] });
    const connection = connect(relay, {
      clientId: 'client-1',
      deviceToken: 'token-gone',
      tokenOnly: true,
      autoReconnect: true,
    });

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(RelayError);
    expect((error as RelayError).failure).toBe('device-revoked');
    expect((error as RelayError).message).toBe(
      '이 기기의 연결이 Mac에서 해제되었습니다. 다시 페어링하세요.',
    );
    expect((error as RelayError).needsRepair).toBe(true);

    await expect(connection.request('GET', '/m1/info')).rejects.toBeInstanceOf(RelayError);

    connection.close();
    await relay.stop();
  });

  it('answers device-conflict with a new clientId and gets in on the second try', async () => {
    const relay = await startFakeRelay({
      conflictingClientIds: ['client-shared'],
      issueDeviceToken: 'token-3',
    });
    const minted: string[] = [];
    const connection = connect(relay, {
      clientId: 'client-shared',
      mintClientId: () => {
        minted.push('client-own');
        return Promise.resolve('client-own');
      },
    });

    const info = await connection.ready();
    expect(minted).toEqual(['client-own']);
    expect(relay.received[0]).toMatchObject({ type: 'auth', clientId: 'client-shared' });
    expect(relay.received[1]).toMatchObject({ type: 'auth', clientId: 'client-own' });
    expect(connection.clientId).toBe('client-own');
    expect(info.deviceToken).toBe('token-3');

    connection.close();
    await relay.stop();
  });

  it('gives up after one retry, without ever calling the host final', async () => {
    const relay = await startFakeRelay({
      conflictingClientIds: ['client-shared', 'client-own'],
    });
    const minted: string[] = [];
    const connection = connect(relay, {
      clientId: 'client-shared',
      mintClientId: () => {
        minted.push('client-own');
        return Promise.resolve('client-own');
      },
    });

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(RelayError);
    expect((error as RelayError).failure).toBe('device-conflict');
    // Not final: the stored secrets stay exactly where they are.
    expect((error as RelayError).needsRepair).toBe(false);
    expect((error as RelayError).message).toBe(
      '이 기기가 Mac에 이미 등록되어 있습니다. 잠시 후 다시 시도합니다.',
    );
    expect(minted).toEqual(['client-own']);

    connection.close();
    await relay.stop();
  });

  it('keeps the secrets when the Mac says its device list is full', async () => {
    const relay = await startFakeRelay({ rejectReason: 'device-limit' });
    const connection = connect(relay, { clientId: 'client-1' });

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('device-limit');
    expect((error as RelayError).needsRepair).toBe(false);
    expect((error as RelayError).message).toContain('기기 목록이 가득 찼거나');

    connection.close();
    await relay.stop();
  });

  it('keeps the secrets for a reason it has never heard of', async () => {
    const relay = await startFakeRelay({ rejectReason: 'teapot' });
    const connection = connect(relay, { clientId: 'client-1' });

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('auth-refused');
    expect((error as RelayError).needsRepair).toBe(false);
    expect((error as RelayError).message).toBe(
      '호스트가 인증을 거절했습니다. 잠시 후 다시 시도합니다.',
    );

    connection.close();
    await relay.stop();
  });

  it('spends the pairing key once when two connections dial the same host at once', async () => {
    // Without the gate the hosts-list probe and a screen's tunnel both authenticate with
    // the key, the Mac mints two tokens, and the one stored first is lost.
    const relay = await startFakeRelay({ issueDeviceToken: 'token-4' });
    const mutex = createKeyedMutex();
    const stored: { token?: string } = {};

    const authorize = async (state: DeviceAuthState): Promise<AuthLease> => {
      const release = await mutex.acquire('mac');
      const auth: DeviceAuthState = {};
      if (state.clientId) auth.clientId = state.clientId;
      // Whoever queued behind the first one finds a token waiting and uses it.
      if (stored.token) auth.deviceToken = stored.token;
      else if (state.pairingKey) auth.pairingKey = state.pairingKey;
      return { auth, release };
    };
    const onDeviceToken = (token: string) => {
      stored.token = token;
      return Promise.resolve();
    };

    const first = connect(relay, { clientId: 'client-1', authorize, onDeviceToken });
    const second = connect(relay, { clientId: 'client-1', authorize, onDeviceToken });
    await Promise.all([first.ready(), second.ready()]);

    const auths = relay.received.filter(
      (message) => (message as { type?: string }).type === 'auth',
    ) as Array<{ pairingKey?: string; deviceToken?: string }>;
    expect(auths).toHaveLength(2);
    expect(auths.filter((message) => message.pairingKey !== undefined)).toHaveLength(1);
    expect(auths.filter((message) => message.deviceToken === 'token-4')).toHaveLength(1);
    expect(stored.token).toBe('token-4');
    expect(mutex.isHeld('mac')).toBe(false);

    first.close();
    second.close();
    await relay.stop();
  });

  it('lets the key go even when the host refuses it, so nobody waits forever', async () => {
    const relay = await startFakeRelay({ pairingKey: 'a-different-key' });
    const mutex = createKeyedMutex();
    const authorize = async (state: DeviceAuthState): Promise<AuthLease> => ({
      auth: state,
      release: await mutex.acquire('mac'),
    });

    const connection = connect(relay, { clientId: 'client-1', authorize });
    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('unpaired');
    expect(mutex.isHeld('mac')).toBe(false);

    connection.close();
    await relay.stop();
  });

  it('reports "릴레이 연결 안 됨" when the relay itself refuses the socket', async () => {
    const connection = track(
      new RelayConnection(
        {
          serverId: SERVER_ID,
          relayUrl: 'ws://127.0.0.1:1',
          hostPublicKeyB64: toBase64(generateKeyPair().publicKey),
          pairingKey: PAIRING_KEY,
        },
        { autoReconnect: false, createSocket: nodeSocketFactory },
      ),
    );

    const error = await connection.ready().catch((caught: unknown) => caught);
    expect((error as RelayError).failure).toBe('relay-unreachable');
    expect((error as RelayError).message).toBe('릴레이 연결 안 됨');
    connection.close();
  });
});
