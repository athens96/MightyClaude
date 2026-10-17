import { createServer, type AddressInfo, type Server, type Socket } from 'node:net';
import { WebSocketServer, WebSocket, type RawData } from 'ws';
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
import { RelayConnection, type RelaySocket } from '@/api/relay/transport';
import {
  isDialableDirectEndpoint,
  isTailnetName,
  isTailscaleIPv4,
  isTailscaleIPv6,
  normalizeDirectEndpoint,
  parseDirectParam,
  sanitizeDirectEndpoints,
} from '@/lib/direct';
import { formatPairingUrl, parsePairingUrl } from '@/lib/pairing';
import { resetLanguage } from '@/lib/i18n';
import { credentialsFor, parseIndex } from '@/store/hosts';

jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(async () => null),
  setItemAsync: jest.fn(async () => undefined),
  deleteItemAsync: jest.fn(async () => undefined),
}));

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

const PAIRING_KEY = 'pairing-key-under-test';
const SERVER_ID = 'a'.repeat(64);
const PUBLIC_KEY_URL = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';

describe('Tailscale endpoints', () => {
  it('accepts the CGNAT block and its edges only', () => {
    expect(isTailscaleIPv4('100.64.0.0')).toBe(true);
    expect(isTailscaleIPv4('100.127.255.255')).toBe(true);
    expect(isTailscaleIPv4('100.101.102.103')).toBe(true);
    expect(isTailscaleIPv4('100.63.255.255')).toBe(false);
    expect(isTailscaleIPv4('100.128.0.0')).toBe(false);
    expect(isTailscaleIPv4('192.168.0.5')).toBe(false);
    expect(isTailscaleIPv4('127.0.0.1')).toBe(false);
    expect(isTailscaleIPv4('100.064.0.1')).toBe(false);
    expect(isTailscaleIPv4('100.64.0.256')).toBe(false);
  });

  it('accepts only the fd7a:115c:a1e0::/48 prefix for IPv6', () => {
    expect(isTailscaleIPv6('fd7a:115c:a1e0::1')).toBe(true);
    expect(isTailscaleIPv6('FD7A:115C:A1E0:ab12:4843:cd96:6258:b240')).toBe(true);
    expect(isTailscaleIPv6('fd7a:115c:a1e1::1')).toBe(false);
    expect(isTailscaleIPv6('fd7a:115c:a1df:ffff::1')).toBe(false);
    expect(isTailscaleIPv6('fe80::1')).toBe(false);
    expect(isTailscaleIPv6('::1')).toBe(false);
    expect(isTailscaleIPv6('fd7a:115c:a1e0::1::2')).toBe(false);
  });

  it('accepts MagicDNS names under ts.net only', () => {
    expect(isTailnetName('studio.tail1234.ts.net')).toBe(true);
    expect(isTailnetName('ts.net')).toBe(false);
    expect(isTailnetName('studio.example.com')).toBe(false);
    expect(isTailnetName('studio.ts.net.evil.com')).toBe(false);
  });

  it('normalises endpoints and drops everything outside the tailnet', () => {
    expect(normalizeDirectEndpoint('100.101.102.103:43139')).toBe('100.101.102.103:43139');
    expect(normalizeDirectEndpoint('[FD7A:115C:A1E0::1]:43139')).toBe('[fd7a:115c:a1e0::1]:43139');
    expect(normalizeDirectEndpoint('Studio.Tail1234.ts.net:43139')).toBe('studio.tail1234.ts.net:43139');
    expect(normalizeDirectEndpoint('fd7a:115c:a1e0::1:43139')).toBeUndefined();
    expect(normalizeDirectEndpoint('192.168.0.5:43139')).toBeUndefined();
    expect(normalizeDirectEndpoint('127.0.0.1:43139')).toBeUndefined();
    expect(normalizeDirectEndpoint('100.101.102.103')).toBeUndefined();
    expect(normalizeDirectEndpoint('100.101.102.103:70000')).toBeUndefined();
    expect(sanitizeDirectEndpoints(['100.64.0.1:1', 7, '100.64.0.1:1', 'evil.com:1'])).toEqual(['100.64.0.1:1']);
    expect(sanitizeDirectEndpoints('100.64.0.1:1')).toEqual([]);
    expect(parseDirectParam(Array.from({ length: 12 }, (_, i) => `100.64.0.${i}:43139`).join(','))).toHaveLength(8);
    expect(isDialableDirectEndpoint('100.101.102.103:43139')).toBe(true);
    expect(isDialableDirectEndpoint('[fd7a:115c:a1e0::1]:43139')).toBe(true);
    expect(isDialableDirectEndpoint('studio.tail1234.ts.net:43139')).toBe(false);
    expect(isDialableDirectEndpoint('127.0.0.1:43139')).toBe(false);
  });
});

describe('pairing QR with and without direct', () => {
  const base = `mightyclaude://pair?v=2&sid=${SERVER_ID}&pk=${PUBLIC_KEY_URL}&relay=wss%3A%2F%2Frelay.example.com&key=${'k'.repeat(43)}&name=Studio`;

  it('reads a QR without direct exactly as before', () => {
    const parsed = parsePairingUrl(base);
    expect(parsed.ok).toBe(true);
    if (parsed.ok) expect(parsed.value.directEndpoints).toBeUndefined();
  });

  it('reads the percent-encoded direct list, keeping only Tailscale endpoints', () => {
    const direct = encodeURIComponent('100.101.102.103:43139,[fd7a:115c:a1e0::1]:43139,studio.tail1234.ts.net:43139,192.168.0.5:43139');
    const parsed = parsePairingUrl(`${base}&direct=${direct}`);
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    expect(parsed.value.directEndpoints).toEqual([
      '100.101.102.103:43139',
      '[fd7a:115c:a1e0::1]:43139',
      'studio.tail1234.ts.net:43139',
    ]);
    // Round trip: the canonical string carries the same list.
    expect(parsePairingUrl(formatPairingUrl(parsed.value))).toEqual(parsed);
  });

  it('pairs over the relay when direct holds nothing usable', () => {
    const parsed = parsePairingUrl(`${base}&direct=${encodeURIComponent('10.0.0.2:43139')}`);
    expect(parsed.ok).toBe(true);
    if (parsed.ok) expect(parsed.value.directEndpoints).toBeUndefined();
  });

  it('reads the exact string the Mac writes', () => {
    // MobilePairingOffer.url percent-encodes `:`, `,`, `[` and `]` in `direct`.
    const parsed = parsePairingUrl(`${base}&direct=100.101.102.103%3A43139%2C%5Bfd7a%3A115c%3Aa1e0%3A%3A1%5D%3A43139`);
    expect(parsed.ok && parsed.value.directEndpoints).toEqual(['100.101.102.103:43139', '[fd7a:115c:a1e0::1]:43139']);
  });
});

describe('stored hosts', () => {
  const old = {
    id: 'sid',
    name: 'Studio',
    serverId: 'sid',
    relayUrl: 'wss://relay.example.com',
    hostPublicKeyB64: 'AAAA',
    hostId: 'host-1',
    appVersion: '0.4.29',
    pairedAt: '2026-10-01T00:00:00.000Z',
  };

  it('keeps a host saved before the direct path, with no endpoints', () => {
    const [host] = parseIndex(JSON.stringify([old]));
    expect(host).toEqual(old);
    expect(host?.directEndpoints).toBeUndefined();
  });

  it('keeps stored endpoints and cuts a damaged list down to the valid ones', () => {
    const hosts = parseIndex(
      JSON.stringify([
        { ...old, directEndpoints: ['100.64.0.1:43139', 'evil.example.com:80'] },
        { ...old, id: 'two', serverId: 'two', directEndpoints: 'not a list' },
      ]),
    );
    expect(hosts[0]?.directEndpoints).toEqual(['100.64.0.1:43139']);
    expect(hosts[1]).toBeDefined();
    expect(hosts[1]?.directEndpoints).toBeUndefined();
  });

  it('hands the stored endpoints to the connection, and none for an old host', () => {
    const secrets = { deviceToken: 'token' };
    expect(credentialsFor(old, secrets, 'client').directEndpoints).toBeUndefined();
    expect(credentialsFor({ ...old, directEndpoints: ['100.64.0.1:43139'] }, secrets, 'client').directEndpoints).toEqual([
      '100.64.0.1:43139',
    ]);
  });
});

// ------------------------------------------------------------------ route selection

interface FakeHost {
  /** `100.64.0.1:port`: a Tailscale address the socket factory below sends to loopback. */
  endpoint: string;
  /** The request target of every socket it accepted. */
  paths: string[];
  /** `ws://127.0.0.1:port`, the same server playing the relay. */
  url: string;
  hostPublicKeyB64: string;
  connections: number;
  /** Drops every open socket, the way a Mac that lost its network would. */
  dropAll: () => void;
  stop: () => Promise<void>;
}

const TAILNET_STAND_IN = '100.64.0.1';
const servers: { stop: () => Promise<void> }[] = [];
const opened: RelayConnection[] = [];

afterEach(async () => {
  dialled.splice(0);
  for (const connection of opened.splice(0)) connection.close();
  for (const server of servers.splice(0)) await server.stop();
});

/** A Mac answering the handshake and auth itself; the same server stands in for a relay. */
async function startFakeHost(
  options: {
    keys?: ReturnType<typeof generateKeyPair>;
    authOk?: Record<string, unknown>;
    /** `silent` never answers `auth`; `refuse` answers it the way a Mac refuses a direct socket. */
    auth?: 'ok' | 'silent' | 'refuse';
  } = {},
): Promise<FakeHost> {
  const hostKeys = options.keys ?? generateKeyPair();
  const server = new WebSocketServer({ port: 0 });
  const fake = { connections: 0, paths: [] as string[] };
  server.on('connection', (socket, request) => {
    fake.connections += 1;
    fake.paths.push(request.url ?? '');
    let cipher: RelayCipher | undefined;
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
        socket.send(JSON.stringify({ type: 'ready', v: 1, serverKey: toBase64(hostKeys.publicKey), nonce: toBase64(serverNonce) }));
        return;
      }
      if (!cipher) return;
      const message = cipher.openJson(new Uint8Array(raw as Buffer)) as { type?: string; id?: string };
      if (message.type === 'auth') {
        if (options.auth === 'silent') return;
        if (options.auth === 'refuse') {
          socket.send(cipher.sealJson({ type: 'auth_error', reason: 'unauthorized' }));
          setTimeout(() => socket.close(1000), 5);
          return;
        }
        socket.send(cipher.sealJson({ type: 'auth_ok', hostName: 'Studio', hostId: 'host-1', appVersion: '1.0.0', ...options.authOk }));
      } else if (message.type === 'ping') {
        socket.send(cipher.sealJson({ type: 'pong' }));
      } else if (typeof message.id === 'string') {
        socket.send(cipher.sealJson({ id: message.id, status: 200, body: { protocol: 1 } }));
      }
    });
  });
  await new Promise<void>((resolve) => server.once('listening', resolve));
  const { port } = server.address() as AddressInfo;
  const stop = () =>
    new Promise<void>((resolve) => {
      for (const client of server.clients) client.terminate();
      server.close(() => resolve());
    });
  servers.push({ stop });
  return Object.assign(fake, {
    endpoint: `${TAILNET_STAND_IN}:${port}`,
    url: `ws://127.0.0.1:${port}`,
    hostPublicKeyB64: toBase64(hostKeys.publicKey),
    dropAll: () => {
      for (const client of server.clients) client.terminate();
    },
    stop,
  });
}

/** Accepts TCP and never answers the WebSocket upgrade: a direct endpoint that hangs. */
async function startSilentEndpoint(): Promise<string> {
  const sockets = new Set<Socket>();
  const server: Server = createServer((socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  servers.push({
    stop: () =>
      new Promise<void>((resolve) => {
        for (const socket of sockets) socket.destroy();
        server.close(() => resolve());
      }),
  });
  return `${TAILNET_STAND_IN}:${(server.address() as AddressInfo).port}`;
}

/** A port nothing listens on: bound once, then let go. */
async function closedEndpoint(): Promise<string> {
  const server = createServer();
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as AddressInfo;
  await new Promise<void>((resolve) => server.close(() => resolve()));
  return `${TAILNET_STAND_IN}:${port}`;
}

/** Every URL the phone dialled, as it asked for it. */
const dialled: string[] = [];

/**
 * The phone dials only Tailscale addresses, so the tests hand it this one and the
 * factory sends it to loopback, where the fakes listen.
 */
function nodeSocketFactory(url: string): RelaySocket {
  dialled.push(url);
  return new WebSocket(url.replace(`ws://${TAILNET_STAND_IN}:`, 'ws://127.0.0.1:')) as unknown as RelaySocket;
}

function connect(
  relay: { url: string; hostPublicKeyB64: string },
  directEndpoints: string[],
  extra: { autoReconnect?: boolean; onDirectEndpoints?: (endpoints: string[]) => void } = {},
): RelayConnection {
  const connection = new RelayConnection(
    {
      serverId: SERVER_ID,
      relayUrl: relay.url,
      hostPublicKeyB64: relay.hostPublicKeyB64,
      pairingKey: PAIRING_KEY,
      directEndpoints,
    },
    {
      clientName: 'Test Phone',
      autoReconnect: extra.autoReconnect ?? false,
      createSocket: nodeSocketFactory,
      requestTimeoutMs: 5_000,
      directTimeoutMs: 300,
      directAuthTimeoutMs: 400,
      onDirectEndpoints: extra.onDirectEndpoints,
    },
  );
  opened.push(connection);
  return connection;
}

async function waitFor(condition: () => boolean, timeoutMs = 5_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) throw new Error('condition not met in time');
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}

describe('route selection', () => {
  jest.setTimeout(15_000);

  it('uses the direct endpoint when it opens, and never dials the relay', async () => {
    const keys = generateKeyPair();
    const mac = await startFakeHost({ keys });
    const relay = await startFakeHost({ keys });
    const connection = connect(relay, [mac.endpoint]);
    await connection.ready();
    expect(connection.route).toBe('direct');
    expect(mac.connections).toBe(1);
    expect(relay.connections).toBe(0);
    // The direct URL names neither the server nor a connection.
    expect(dialled).toEqual([`ws://${mac.endpoint}/ws?v=1`]);
    expect(mac.paths).toEqual(['/ws?v=1']);
    const response = await connection.request('GET', '/m1/info');
    expect(response.status).toBe(200);
  });

  it('falls back to the relay when the direct endpoint never opens', async () => {
    const keys = generateKeyPair();
    const relay = await startFakeHost({ keys });
    const silent = await startSilentEndpoint();
    const started = Date.now();
    const connection = connect(relay, [silent]);
    await connection.ready();
    expect(connection.route).toBe('relay');
    expect(relay.connections).toBe(1);
    expect(Date.now() - started).toBeGreaterThanOrEqual(250);
  });

  it('goes to the relay at once when every direct endpoint refuses', async () => {
    const keys = generateKeyPair();
    const relay = await startFakeHost({ keys });
    const connection = connect(relay, [await closedEndpoint()]);
    await connection.ready();
    expect(connection.route).toBe('relay');
  });

  it('takes the relay, and keeps the pairing, when a direct address answers with another key', async () => {
    const relay = await startFakeHost();
    const stranger = await startFakeHost();
    const connection = connect(relay, [stranger.endpoint]);
    await connection.ready();
    expect(connection.route).toBe('relay');
    expect(stranger.connections).toBe(1);
    expect(connection.failure).toBeUndefined();
  });

  it('reconnects direct-first after a direct tunnel drops', async () => {
    const keys = generateKeyPair();
    const mac = await startFakeHost({ keys });
    const relay = await startFakeHost({ keys });
    const connection = connect(relay, [mac.endpoint], { autoReconnect: true });
    await connection.ready();
    expect(connection.route).toBe('direct');
    mac.dropAll();
    await waitFor(() => connection.state !== 'ready');
    await waitFor(() => connection.state === 'ready', 8_000);
    expect(connection.route).toBe('direct');
    expect(mac.connections).toBe(2);
    expect(relay.connections).toBe(0);
  });

  it('reconnects over the relay when the direct endpoint is gone after a drop', async () => {
    const keys = generateKeyPair();
    const mac = await startFakeHost({ keys });
    const relay = await startFakeHost({ keys });
    const connection = connect(relay, [mac.endpoint], { autoReconnect: true });
    await connection.ready();
    await mac.stop();
    await waitFor(() => connection.state !== 'ready');
    await waitFor(() => connection.state === 'ready', 8_000);
    expect(connection.route).toBe('relay');
    expect(relay.connections).toBe(1);
  });

  it('takes the relay when a direct socket opens but auth_ok does not come in time', async () => {
    const keys = generateKeyPair();
    const mac = await startFakeHost({ keys, auth: 'silent' });
    const relay = await startFakeHost({ keys });
    const started = Date.now();
    const connection = connect(relay, [mac.endpoint]);
    await connection.ready();
    expect(connection.route).toBe('relay');
    expect(mac.connections).toBe(1);
    expect(Date.now() - started).toBeGreaterThanOrEqual(350);
  });

  it('hears a direct refusal out over the relay, keeping the secrets', async () => {
    const keys = generateKeyPair();
    const mac = await startFakeHost({ keys, auth: 'refuse' });
    const relay = await startFakeHost({ keys });
    const connection = connect(relay, [mac.endpoint]);
    await connection.ready();
    expect(connection.route).toBe('relay');
    expect(connection.failure).toBeUndefined();
  });

  it('never dials a MagicDNS name, only the addresses beside it', async () => {
    const keys = generateKeyPair();
    const mac = await startFakeHost({ keys });
    const relay = await startFakeHost({ keys });
    const connection = connect(relay, ['studio.tail1234.ts.net:43139', mac.endpoint]);
    await connection.ready();
    expect(connection.route).toBe('direct');
    expect(dialled.some((url) => url.includes('ts.net'))).toBe(false);

    const nameOnly = connect(relay, ['studio.tail1234.ts.net:43139']);
    await nameOnly.ready();
    expect(nameOnly.route).toBe('relay');
  });

  it('learns the endpoints a host with the direct capability lists in auth_ok', async () => {
    const relay = await startFakeHost({
      authOk: { capabilities: ['queue', 'direct'], direct: ['100.101.102.103:43139', '10.0.0.1:1'] },
    });
    const learned: string[][] = [];
    const connection = connect(relay, [], { onDirectEndpoints: (endpoints) => learned.push(endpoints) });
    const info = await connection.ready();
    expect(info.direct).toEqual(['100.101.102.103:43139']);
    expect(learned).toEqual([['100.101.102.103:43139']]);
  });

  it('ignores a direct list from a host that does not advertise the capability', async () => {
    const relay = await startFakeHost({ authOk: { capabilities: ['queue'], direct: ['100.101.102.103:43139'] } });
    const learned: string[][] = [];
    const connection = connect(relay, [], { onDirectEndpoints: (endpoints) => learned.push(endpoints) });
    const info = await connection.ready();
    expect(info.direct).toBeUndefined();
    expect(learned).toEqual([]);
  });
});
