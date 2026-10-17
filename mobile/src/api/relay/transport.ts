import {
  DIRECTION_CLIENT_TO_HOST,
  DIRECTION_HOST_TO_CLIENT,
  RelayCipher,
  RelayCryptoError,
  bytesEqual,
  deriveSessionKey,
  fromBase64,
  generateHandshakeNonce,
  generateKeyPair,
  randomUuid,
  toBase64,
} from '@/api/relay/crypto';
import {
  afterAuthOk,
  authFrameFor,
  authRejectionFor,
  deviceTokenFrom,
  type AuthErrorReason,
  type AuthRejection,
  type DeviceAuthState,
} from '@/lib/device-token';
import { isDialableDirectEndpoint, sanitizeDirectEndpoints, type ConnectionRoute } from '@/lib/direct';
import { t } from '@/lib/i18n';

/** Wire version of both the relay query string and the handshake envelopes. */
export const RELAY_WIRE_VERSION = 1;
/** Concurrent tunnelled requests allowed by docs/relay.md. */
export const MAX_IN_FLIGHT = 8;
export const PING_INTERVAL_MS = 20_000;
export const PONG_DEADLINE_MS = 60_000;
export const RECONNECT_MIN_MS = 1_500;
export const RECONNECT_MAX_MS = 30_000;
/** Covers a 10 s long poll plus slack on a slow mobile link. */
export const DEFAULT_REQUEST_TIMEOUT_MS = 45_000;
/**
 * How long a tunnel that still looks open gets to show life when the app returns. Any
 * frame counts, not just the pong: on a slow uplink the ping can queue behind upload
 * chunks, and dropping a live tunnel fails the requests already on it.
 */
export const FOREGROUND_PROBE_MS = 10_000;
const HANDSHAKE_TIMEOUT_MS = 20_000;
/**
 * How long the Mac's Tailscale endpoints get to open before the relay is dialled
 * instead. All of them are tried at once, so this is the whole wait, not one per endpoint.
 */
export const DIRECT_TIMEOUT_MS = 1_800;
/**
 * How long a direct socket that opened gets to reach `auth_ok`. Over Tailscale that takes
 * a round trip or two; anything slower is better served by the relay, taken at once.
 */
export const DIRECT_AUTH_TIMEOUT_MS = 3_000;

export type RelayState = 'connecting' | 'handshaking' | 'ready' | 'closed';

export type RelayFailure =
  | 'host-offline'
  | 'host-not-attached'
  | 'unpaired'
  | 'device-revoked'
  | 'device-conflict'
  | 'device-limit'
  | 'auth-refused'
  | 'too-many'
  | 'relay-unreachable'
  | 'protocol'
  | 'timeout'
  | 'closed';

/** Locale keys, read when a failure is described. */
const failureMessageKeys: Record<RelayFailure, string> = {
  'host-offline': 'phone.hosts.reachability.offline',
  'host-not-attached': 'phone.relay.failure.hostNotAttached',
  unpaired: 'phone.hosts.reachability.unauthorized',
  'device-revoked': 'phone.relay.failure.deviceRevoked',
  'device-conflict': 'phone.relay.failure.deviceConflict',
  'device-limit': 'phone.relay.failure.deviceLimit',
  'auth-refused': 'phone.relay.failure.authRefused',
  'too-many': 'phone.relay.failure.tooMany',
  'relay-unreachable': 'phone.hosts.reachability.relayOffline',
  protocol: 'phone.relay.failure.protocol',
  timeout: 'phone.relay.failure.timeout',
  closed: 'phone.relay.failure.closed',
};

/**
 * The shared `auth_error.reason` vocabulary, as transport failures. Only the two final
 * ones end the connection for good; everything else — including a word this build has
 * never seen — keeps the stored secrets and the normal retry.
 */
const authFailures: Record<AuthErrorReason, RelayFailure> = {
  'pairing-key': 'unpaired',
  'device-revoked': 'device-revoked',
  'device-conflict': 'device-conflict',
  'device-limit': 'device-limit',
  'legacy-refused': 'auth-refused',
  malformed: 'auth-refused',
  unknown: 'auth-refused',
};

export function describeRelayFailure(failure: RelayFailure): string {
  return t(failureMessageKeys[failure]);
}

/** A rejected key or a released device: neither fixes itself, both need pairing again. */
export function isFinalFailure(failure: RelayFailure | undefined): boolean {
  return failure === 'unpaired' || failure === 'device-revoked';
}

/**
 * The banner for a connection that will not open again on its own. A released device
 * says what happened in its own message; a rejected key only says "re-pairing required", so
 * that case adds which secret went stale.
 */
export function describeRepairNeeded(failureMessage: string | undefined): string {
  if (failureMessage && failureMessage !== describeRelayFailure('unpaired')) return failureMessage;
  return t('phone.relay.repairStaleKey');
}

/** A relay/transport-level failure, as opposed to an `ApiError` from the host. */
export class RelayError extends Error {
  readonly failure: RelayFailure;
  readonly closeCode?: number;

  constructor(failure: RelayFailure, closeCode?: number, message?: string) {
    super(message ?? describeRelayFailure(failure));
    this.name = 'RelayError';
    this.failure = failure;
    this.closeCode = closeCode;
  }

  /** True when the stored pairing key or device token no longer opens this host. */
  get needsRepair(): boolean {
    return isFinalFailure(this.failure);
  }
}

/**
 * Close codes emitted by the Node relay. The host cannot send custom codes
 * (URLSessionWebSocketTask limitation), so a rejected pairing key arrives as an
 * encrypted `auth_error` message followed by a normal close instead of 4401.
 */
export function mapCloseCode(code: number | undefined): RelayFailure | undefined {
  switch (code) {
    case 4404:
    case 4410:
      return 'host-offline';
    case 4504:
      return 'host-not-attached';
    case 4401:
      return 'unpaired';
    case 4429:
      return 'too-many';
    case 4413:
      return 'protocol';
    default:
      return undefined;
  }
}

export interface RelayTarget {
  serverId: string;
  /** `ws://host:port` or `wss://host:port`, without a path. */
  relayUrl: string;
  /** Base64 X25519 public key captured at pairing time. */
  hostPublicKeyB64: string;
  /** Absent once this host has issued a device token. */
  pairingKey?: string;
  /** This install's id, sent so the host can register the device. */
  clientId?: string;
  /** Per-host device token; authenticates on its own. */
  deviceToken?: string;
  /**
   * The Mac's Tailscale endpoints (`host:port`). When there are any, each connect opens
   * them first and uses the relay only if none answers (docs/relay.md, "Tailscale 직접 연결").
   */
  directEndpoints?: string[];
}

export interface RelayHostInfo {
  hostName: string;
  hostId: string;
  appVersion: string;
  /** Sent once, on the connection that registered this device. */
  deviceToken?: string;
  /** The endpoints a host with the `direct` capability listed in its `auth_ok`. */
  direct?: string[];
  /** How the tunnel reached the host; filled in by `probeHost`, read `route` otherwise. */
  route?: ConnectionRoute;
}

export interface RelayNotification {
  /** `state` or `session:<id>`. */
  scope: string;
  revision: number;
}

export interface RelayResponse {
  status: number;
  body: unknown;
}

/** The subset of the WebSocket API the transport uses, so tests can inject `ws`. */
export interface RelaySocket {
  binaryType: string;
  send(data: string | ArrayBufferView | ArrayBuffer): void;
  close(code?: number, reason?: string): void;
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  onerror: ((event: unknown) => void) | null;
  onclose: ((event: { code?: number; reason?: string }) => void) | null;
}

export type RelaySocketFactory = (url: string) => RelaySocket;

/** Fires whenever the app comes to the foreground, so we can reconnect at once. */
export interface ForegroundSignal {
  subscribe(listener: () => void): () => void;
}

/** The right to spend a host's pairing key, and the freshest state to spend it with. */
export interface AuthLease {
  auth: DeviceAuthState;
  /** Called once the host has answered, or the attempt was abandoned. */
  release: () => void;
}

export interface RelayConnectionOptions {
  /** Shown on the desktop as the connected device. */
  clientName?: string;
  /** Disable to make a single attempt — used by the hosts-list reachability probe. */
  autoReconnect?: boolean;
  createSocket?: RelaySocketFactory;
  foreground?: ForegroundSignal;
  requestTimeoutMs?: number;
  /** Overrides `FOREGROUND_PROBE_MS`. */
  probeTimeoutMs?: number;
  /**
   * Called once, with the token the host issued, so it can be stored per host. A promise
   * is awaited before the pairing-key lease is let go, so the next connection in line
   * reads the stored token instead of spending the key a second time.
   */
  onDeviceToken?: (deviceToken: string) => void | Promise<void>;
  /**
   * Guards the one pairing-key authentication a host may have in flight. Given the state
   * this connection was built with, it answers with the state to actually authenticate
   * with — a token another connection has since adopted wins — and the release.
   */
  authorize?: (state: DeviceAuthState) => Promise<AuthLease>;
  /**
   * Answer to `device-conflict`: mints and stores a `clientId` for this host alone, used
   * for exactly one more attempt with the pairing key.
   */
  mintClientId?: () => Promise<string | undefined>;
  /** Overrides `DIRECT_TIMEOUT_MS`. */
  directTimeoutMs?: number;
  /** Overrides `DIRECT_AUTH_TIMEOUT_MS`. */
  directAuthTimeoutMs?: number;
  /** Called with the endpoints a host lists in `auth_ok`, so they can be stored. */
  onDirectEndpoints?: (endpoints: string[]) => void;
  /** Called each time the tunnel is ready, with the way it reached the host. */
  onRoute?: (route: ConnectionRoute) => void;
}

function defaultSocketFactory(url: string): RelaySocket {
  const Ctor = (globalThis as { WebSocket?: new (url: string) => RelaySocket }).WebSocket;
  if (!Ctor) throw new RelayError('relay-unreachable', undefined, t('phone.relay.noWebSocket'));
  return new Ctor(url);
}

function toBytes(data: unknown): Uint8Array | undefined {
  if (data instanceof Uint8Array) return data;
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (ArrayBuffer.isView(data)) {
    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  }
  return undefined;
}

/**
 * A direct data socket: the Mac knows only itself, so the URL names no server and no
 * connection — a tailnet observer learns nothing it could replay at the relay.
 */
export function buildDirectUrl(endpoint: string): string {
  return `ws://${endpoint}/ws?v=${RELAY_WIRE_VERSION}`;
}

export function buildRelayUrl(relayUrl: string, serverId: string, connectionId: string): string {
  const base = relayUrl.trim().replace(/\/+$/, '');
  const params = new URLSearchParams({
    serverId,
    role: 'client',
    connectionId,
    v: String(RELAY_WIRE_VERSION),
  });
  return `${base}/ws?${params.toString()}`;
}

interface PendingRequest {
  id: string;
  payload: { id: string; method: string; path: string; body?: unknown };
  timeoutMs: number;
  resolve: (value: RelayResponse) => void;
  reject: (error: Error) => void;
  timer?: ReturnType<typeof setTimeout>;
}

interface Waiter {
  resolve: (info: RelayHostInfo) => void;
  reject: (error: Error) => void;
}

/**
 * One end-to-end encrypted tunnel to a paired host, through the relay.
 *
 * Opens `wss://<relay>/ws?serverId=…&role=client&connectionId=…&v=1`, runs the
 * plaintext `hello`/`ready` handshake, derives the session key, authenticates with
 * the pairing key, and then exchanges encrypted `{id, method, path, body}` requests
 * and `{id, status, body}` responses. Reconnects with backoff and, when a
 * `ForegroundSignal` is supplied, immediately when the app becomes active.
 */
export class RelayConnection {
  private socket: RelaySocket | undefined;
  private cipher: RelayCipher | undefined;
  private currentState: RelayState = 'closed';
  private currentFailure: RelayFailure | undefined;
  private hostInfo: RelayHostInfo | undefined;

  private readonly pending = new Map<string, PendingRequest>();
  private readonly queue: PendingRequest[] = [];
  private readonly readyWaiters: Waiter[] = [];
  private readonly notifyListeners = new Set<(event: RelayNotification) => void>();
  private readonly stateListeners = new Set<(state: RelayState, failure?: RelayFailure) => void>();
  /** Decrypted envelopes that are not m1 responses: screen-share signalling, mostly. */
  private readonly messageListeners = new Set<(message: Record<string, unknown>) => void>();

  private reconnectDelayMs = RECONNECT_MIN_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private handshakeTimer: ReturnType<typeof setTimeout> | undefined;
  private pingTimer: ReturnType<typeof setInterval> | undefined;
  private probeTimer: ReturnType<typeof setTimeout> | undefined;
  private lastPongAt = 0;
  /** Counts every frame received, so a probe can tell whether the tunnel is alive. */
  private frames = 0;
  private disposed = false;
  private authFailure: RelayFailure | undefined;
  private authRejection: AuthRejection | undefined;
  private unsubscribeForeground: (() => void) | undefined;
  /** Held from the moment the pairing key goes out until the host has answered. */
  private authLease: (() => void) | undefined;
  /** `device-conflict` buys exactly one more attempt, under a fresh id. */
  private clientIdRetried = false;
  /** Moves from the pairing key to the device token the moment the host issues one. */
  private auth: DeviceAuthState;
  /** The Mac's Tailscale endpoints; replaced by what the host says in `auth_ok`. */
  private directEndpoints: string[];
  /** Which way the current socket went, and whether it has got as far as `auth_ok`. */
  private attemptRoute: ConnectionRoute = 'relay';
  private socketReady = false;
  /** Abandons the direct race in progress, if there is one. */
  private cancelRace: (() => void) | undefined;

  private readonly clientName: string;
  private readonly autoReconnect: boolean;
  private readonly createSocket: RelaySocketFactory;
  private readonly requestTimeoutMs: number;
  private readonly probeTimeoutMs: number;
  private readonly onDeviceToken: ((deviceToken: string) => void | Promise<void>) | undefined;
  private readonly authorize: ((state: DeviceAuthState) => Promise<AuthLease>) | undefined;
  private readonly mintClientId: (() => Promise<string | undefined>) | undefined;
  private readonly directTimeoutMs: number;
  private readonly directAuthTimeoutMs: number;
  private readonly onDirectEndpoints: ((endpoints: string[]) => void) | undefined;
  private readonly onRoute: ((route: ConnectionRoute) => void) | undefined;

  constructor(
    private readonly target: RelayTarget,
    options: RelayConnectionOptions = {},
  ) {
    this.clientName = options.clientName ?? 'Phone';
    this.autoReconnect = options.autoReconnect ?? true;
    this.createSocket = options.createSocket ?? defaultSocketFactory;
    this.requestTimeoutMs = options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
    this.probeTimeoutMs = options.probeTimeoutMs ?? FOREGROUND_PROBE_MS;
    this.onDeviceToken = options.onDeviceToken;
    this.authorize = options.authorize;
    this.mintClientId = options.mintClientId;
    this.directTimeoutMs = options.directTimeoutMs ?? DIRECT_TIMEOUT_MS;
    this.directAuthTimeoutMs = options.directAuthTimeoutMs ?? DIRECT_AUTH_TIMEOUT_MS;
    this.onDirectEndpoints = options.onDirectEndpoints;
    this.onRoute = options.onRoute;
    this.directEndpoints = [...(target.directEndpoints ?? [])];
    this.auth = {
      clientId: target.clientId,
      pairingKey: target.pairingKey,
      deviceToken: target.deviceToken,
    };
    if (options.foreground) {
      this.unsubscribeForeground = options.foreground.subscribe(() => this.onForeground());
    }
    this.connect();
  }

  get state(): RelayState {
    return this.currentState;
  }

  get failure(): RelayFailure | undefined {
    return this.currentFailure;
  }

  get info(): RelayHostInfo | undefined {
    return this.hostInfo;
  }

  /** The token this tunnel authenticates with, once the host has issued one. */
  get deviceToken(): string | undefined {
    return this.auth.deviceToken;
  }

  /** The id this tunnel introduces itself with, after any `device-conflict` retry. */
  get clientId(): string | undefined {
    return this.auth.clientId;
  }

  /** How the open tunnel reached the host; undefined while it is not open. */
  get route(): ConnectionRoute | undefined {
    return this.currentState === 'ready' ? this.attemptRoute : undefined;
  }

  onNotify(listener: (event: RelayNotification) => void): () => void {
    this.notifyListeners.add(listener);
    return () => this.notifyListeners.delete(listener);
  }

  onStateChange(listener: (state: RelayState, failure?: RelayFailure) => void): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  /**
   * Every decrypted envelope this build does not answer itself. The screen-share
   * signalling of docs/relay.md rides here: the relay forwards the frames sealed, so
   * neither the SDP nor an ICE candidate nor a kill is readable by it.
   */
  onMessage(listener: (message: Record<string, unknown>) => void): () => void {
    this.messageListeners.add(listener);
    return () => this.messageListeners.delete(listener);
  }

  /**
   * Sends one encrypted message that is not an m1 request — it has a `type` and no reply.
   * Returns false when the tunnel is not open, which the caller treats as "the Mac did
   * not hear this": for a kill or a session end that is enough, because the Mac stops on
   * its own and the picture simply dies.
   */
  send(message: Record<string, unknown>): boolean {
    if (this.disposed || this.currentState !== 'ready') return false;
    if (!this.socket || !this.cipher) return false;
    this.sendEncrypted(message);
    return true;
  }

  /** Resolves with the host's `auth_ok` payload once the tunnel is usable. */
  ready(): Promise<RelayHostInfo> {
    if (this.currentState === 'ready' && this.hostInfo) return Promise.resolve(this.hostInfo);
    if (this.disposed) {
      return Promise.reject(new RelayError(this.currentFailure ?? 'closed'));
    }
    return new Promise<RelayHostInfo>((resolve, reject) => {
      this.readyWaiters.push({ resolve, reject });
    });
  }

  /** Sends one tunnelled m1 request; at most 8 are in flight, the rest queue. */
  request(
    method: 'GET' | 'POST',
    path: string,
    body?: unknown,
    timeoutMs?: number,
  ): Promise<RelayResponse> {
    const id = randomUuid();
    return new Promise<RelayResponse>((resolve, reject) => {
      if (this.disposed || isFinalFailure(this.currentFailure)) {
        reject(new RelayError(this.currentFailure ?? 'closed'));
        return;
      }
      const entry: PendingRequest = {
        id,
        payload: body === undefined ? { id, method, path } : { id, method, path, body },
        timeoutMs: timeoutMs ?? this.requestTimeoutMs,
        resolve,
        reject,
      };
      // The deadline starts when the caller asks, not when the frame finally goes out,
      // so a request waiting behind the 8-in-flight cap cannot hang forever.
      entry.timer = setTimeout(() => this.expire(entry), entry.timeoutMs);
      this.queue.push(entry);
      this.pump();
    });
  }

  /** Cancels the backoff timer and dials again right away. */
  reconnectNow(): void {
    if (this.disposed || !this.autoReconnect) return;
    if (isFinalFailure(this.currentFailure) || this.socket || this.cancelRace) return;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }
    this.reconnectDelayMs = RECONNECT_MIN_MS;
    this.connect();
  }

  /**
   * The app is back. A tunnel that is down redials at once; one that still looks open
   * is asked for a pong first. A socket the OS froze or cut while the app was in the
   * background can stay "open" here long after the relay dropped it, and everything
   * sent into it — the screen's refresh, a tap on Stop — would wait out its deadline.
   */
  private onForeground(): void {
    if (this.socket && this.currentState === 'ready') this.probe();
    else this.reconnectNow();
  }

  private probe(): void {
    const socket = this.socket;
    if (!socket || this.probeTimer) return;
    const framesBefore = this.frames;
    this.sendEncrypted({ type: 'ping' });
    this.probeTimer = setTimeout(() => {
      this.probeTimer = undefined;
      if (this.socket !== socket || this.frames !== framesBefore) return;
      // Dead: drop it and dial again now rather than after the backoff delay.
      this.dropSocket(socket, 'closed');
      this.reconnectNow();
    }, this.probeTimeoutMs);
  }

  close(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribeForeground?.();
    this.unsubscribeForeground = undefined;
    this.teardown(new RelayError('closed'));
    this.setState('closed');
    this.notifyListeners.clear();
    this.stateListeners.clear();
    this.messageListeners.clear();
  }

  // ---------------------------------------------------------------- internals

  private setState(state: RelayState, failure?: RelayFailure): void {
    this.currentState = state;
    this.currentFailure = failure;
    for (const listener of this.stateListeners) listener(state, failure);
  }

  /**
   * Opens the next socket: the Mac's Tailscale endpoints first, all at once, and the relay
   * when none of them opens within `directTimeoutMs` or every one has already failed.
   * `relayOnly` skips the race — a direct socket that opened but went nowhere comes here.
   */
  private connect(relayOnly = false): void {
    if (this.disposed) return;
    this.authFailure = undefined;
    this.authRejection = undefined;
    this.setState('connecting');

    const connectionId = randomUuid();
    // Only address literals are raced: a MagicDNS name would be looked up through
    // whatever DNS the phone has, which without Tailscale running is the public one.
    const direct = relayOnly ? [] : this.directEndpoints.filter(isDialableDirectEndpoint);
    if (direct.length === 0) {
      this.dial(buildRelayUrl(this.target.relayUrl, this.target.serverId, connectionId));
      return;
    }
    this.raceDirect(direct.map(buildDirectUrl));
  }

  /** Dials the relay; the handshake starts once the socket opens. */
  private dial(url: string): void {
    let socket: RelaySocket;
    try {
      socket = this.createSocket(url);
    } catch (error) {
      this.attemptRoute = 'relay';
      this.handleClose(undefined, error instanceof RelayError ? error.failure : 'relay-unreachable');
      return;
    }
    this.attach(socket, 'relay', false);
  }

  /**
   * Opens every direct endpoint at once and keeps the first that opens. Only the socket
   * opening is raced: whether it is the right Mac is the handshake's job, and a socket
   * that fails it falls back to the relay (`handleClose`).
   */
  private raceDirect(urls: string[]): void {
    const sockets: RelaySocket[] = [];
    let settled = false;
    let failed = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const detach = (socket: RelaySocket) => {
      socket.onopen = null;
      socket.onmessage = null;
      socket.onclose = null;
      // Closing a socket that is still opening reports an error; it is ours and expected.
      socket.onerror = () => undefined;
    };
    const finish = (winner: RelaySocket | undefined, abandon = false) => {
      if (settled) return;
      settled = true;
      if (timer) clearTimeout(timer);
      this.cancelRace = undefined;
      for (const socket of sockets) {
        if (socket === winner) continue;
        detach(socket);
        try {
          socket.close(1000, 'client');
        } catch {
          // ignore
        }
      }
      if (abandon || this.disposed) return;
      if (winner) this.attach(winner, 'direct', true);
      else this.connect(true);
    };
    for (const url of urls) {
      let socket: RelaySocket;
      try {
        socket = this.createSocket(url);
      } catch {
        failed += 1;
        continue;
      }
      socket.binaryType = 'arraybuffer';
      socket.onopen = () => finish(socket);
      socket.onerror = () => undefined;
      socket.onclose = () => {
        failed += 1;
        if (failed >= urls.length) finish(undefined);
      };
      sockets.push(socket);
    }
    if (failed >= urls.length) {
      finish(undefined);
      return;
    }
    this.cancelRace = () => finish(undefined, true);
    timer = setTimeout(() => finish(undefined), this.directTimeoutMs);
  }

  /**
   * Takes `socket` as this tunnel's and runs the handshake on it. A direct socket arrives
   * already open, so its `hello` goes out at once.
   */
  private attach(socket: RelaySocket, route: ConnectionRoute, opened: boolean): void {
    this.socket = socket;
    this.attemptRoute = route;
    this.socketReady = false;
    socket.binaryType = 'arraybuffer';
    const keyPair = generateKeyPair();
    const clientNonce = generateHandshakeNonce();

    // A direct socket has a short deadline of its own, after which the relay is taken.
    this.handshakeTimer = setTimeout(
      () => {
        if (this.currentState !== 'ready') this.dropSocket(socket, 'host-not-attached');
      },
      route === 'direct' ? this.directAuthTimeoutMs : HANDSHAKE_TIMEOUT_MS,
    );

    const hello = () => {
      if (this.socket !== socket) return;
      this.setState('handshaking');
      socket.send(
        JSON.stringify({
          type: 'hello',
          v: RELAY_WIRE_VERSION,
          clientKey: toBase64(keyPair.publicKey),
          nonce: toBase64(clientNonce),
        }),
      );
    };
    socket.onopen = hello;

    socket.onmessage = (event) => {
      if (this.socket !== socket) return;
      this.frames += 1;
      try {
        this.handleMessage(event.data, keyPair.secretKey, clientNonce, socket);
      } catch (error) {
        const failure = error instanceof RelayError ? error.failure : 'protocol';
        this.dropSocket(socket, failure);
      }
    };

    socket.onerror = () => {
      if (this.socket !== socket) return;
      // `onclose` always follows; remember that we never reached the host.
      if (this.currentState === 'connecting') this.currentFailure = 'relay-unreachable';
    };

    socket.onclose = (event) => {
      if (this.socket !== socket) return;
      this.handleClose(event?.code);
    };

    if (opened) hello();
  }

  private handleMessage(
    data: unknown,
    secretKey: Uint8Array,
    clientNonce: Uint8Array,
    socket: RelaySocket,
  ): void {
    if (typeof data === 'string') {
      this.handleHandshake(data, secretKey, clientNonce, socket);
      return;
    }
    const bytes = toBytes(data);
    if (!bytes) throw new RelayError('protocol', undefined, t('phone.relay.unknownFrame'));
    const cipher = this.cipher;
    if (!cipher) throw new RelayError('protocol', undefined, t('phone.relay.cipherBeforeHandshake'));
    let message: unknown;
    try {
      message = cipher.openJson(bytes);
    } catch (error) {
      throw new RelayError(
        'protocol',
        undefined,
        error instanceof RelayCryptoError ? error.message : t('phone.relay.cannotOpenFrame'),
      );
    }
    this.handleEnvelope(message);
  }

  private handleHandshake(
    raw: string,
    secretKey: Uint8Array,
    clientNonce: Uint8Array,
    socket: RelaySocket,
  ): void {
    let parsed: { type?: string; serverKey?: string; nonce?: string };
    try {
      parsed = JSON.parse(raw) as typeof parsed;
    } catch {
      throw new RelayError('protocol', undefined, t('phone.relay.handshakeUnreadable'));
    }
    if (parsed.type !== 'ready' || !parsed.serverKey || !parsed.nonce) {
      throw new RelayError('protocol', undefined, t('phone.relay.handshakeInvalid'));
    }

    const serverKey = fromBase64(parsed.serverKey);
    if (!bytesEqual(serverKey, fromBase64(this.target.hostPublicKeyB64))) {
      // Over the relay the serverId names this Mac, so another key means the pairing is
      // stale. A direct endpoint is only an address, and it may now be someone else's:
      // that is a reason to take the relay, never to drop the pairing.
      if (this.attemptRoute === 'direct') throw new RelayError('protocol', undefined, t('phone.relay.hostKeyMismatch'));
      throw new RelayError('unpaired', undefined, t('phone.relay.hostKeyMismatch'));
    }

    const key = deriveSessionKey({
      secretKey,
      peerPublicKey: serverKey,
      clientNonce,
      serverNonce: fromBase64(parsed.nonce),
    });
    this.cipher = new RelayCipher(key, DIRECTION_CLIENT_TO_HOST, DIRECTION_HOST_TO_CLIENT);
    this.authenticate(socket);
  }

  /**
   * Sends the `auth` frame. A token authenticates on its own and goes out at once; a
   * pairing key waits its turn, because the host answers it with a device token exactly
   * once and two racing connections would mint two, of which one is thrown away.
   */
  private authenticate(socket: RelaySocket): void {
    const frame = authFrameFor(this.auth, this.clientName);
    if (!frame) {
      throw new RelayError('unpaired', undefined, t('phone.relay.noCredentials'));
    }
    if (frame.pairingKey === undefined || !this.authorize) {
      this.sendEncrypted(frame);
      return;
    }
    void this.authorize(this.auth).then(
      (lease) => {
        if (this.socket !== socket) {
          lease.release();
          return;
        }
        this.auth = lease.auth;
        this.authLease = lease.release;
        // Another connection may have adopted a token while we queued; then this is a
        // token authentication and the key is never spent at all.
        const next = authFrameFor(this.auth, this.clientName);
        if (!next) {
          this.releaseAuthLease();
          this.dropSocket(socket, 'unpaired');
          return;
        }
        this.sendEncrypted(next);
      },
      () => {
        if (this.socket === socket) this.dropSocket(socket, 'closed');
      },
    );
  }

  private releaseAuthLease(): void {
    const release = this.authLease;
    this.authLease = undefined;
    release?.();
  }

  private handleEnvelope(message: unknown): void {
    if (message === null || typeof message !== 'object') return;
    const envelope = message as {
      type?: string;
      id?: string;
      status?: number;
      body?: unknown;
      scope?: string;
      revision?: number;
      hostName?: string;
      hostId?: string;
      appVersion?: string;
      deviceToken?: unknown;
      reason?: unknown;
      capabilities?: unknown;
      direct?: unknown;
    };

    if (typeof envelope.id === 'string' && envelope.type === undefined) {
      const entry = this.pending.get(envelope.id);
      if (!entry) return; // stale response for an abandoned request
      this.settle(entry, { status: envelope.status ?? 0, body: envelope.body ?? null });
      return;
    }

    switch (envelope.type) {
      case 'auth_ok': {
        const deviceToken = deviceTokenFrom(envelope);
        const info: RelayHostInfo = {
          hostName: envelope.hostName ?? '',
          hostId: envelope.hostId ?? '',
          appVersion: envelope.appVersion ?? '',
        };
        if (deviceToken) info.deviceToken = deviceToken;
        // Read only from a host that says it has the direct path: it is how a phone paired
        // before the Mac learned its Tailscale addresses finds them, and how it forgets
        // them once the Mac stops listening.
        if (Array.isArray(envelope.capabilities) && envelope.capabilities.includes('direct')) {
          const direct = sanitizeDirectEndpoints(envelope.direct);
          this.directEndpoints = direct;
          info.direct = direct;
          this.onDirectEndpoints?.(direct);
        }
        this.onAuthenticated(info, deviceToken);
        return;
      }
      case 'auth_error': {
        // The host closes right after; remember what it said, in its own words. Only
        // "pairing-key" and "device-revoked" end this host for good — a reason we do
        // not recognise is treated as a hiccup, never as a reason to drop secrets.
        const rejection = authRejectionFor(envelope.reason);
        this.authRejection = rejection;
        this.authFailure = authFailures[rejection.reason];
        throw new RelayError(this.authFailure, undefined, rejection.message);
      }
      case 'notify': {
        if (typeof envelope.scope !== 'string') return;
        const event: RelayNotification = {
          scope: envelope.scope,
          revision: typeof envelope.revision === 'number' ? envelope.revision : 0,
        };
        for (const listener of this.notifyListeners) listener(event);
        return;
      }
      case 'pong': {
        this.lastPongAt = Date.now();
        return;
      }
      case 'ping': {
        this.sendEncrypted({ type: 'pong' });
        return;
      }
      default: {
        // A type this build has no handler for goes to whoever is listening, and is
        // dropped in silence when nobody is — which is what the contract asks of both
        // sides so that an older phone or an older Mac never breaks the connection.
        if (typeof envelope.type !== 'string' || this.messageListeners.size === 0) return;
        const payload = message as Record<string, unknown>;
        for (const listener of this.messageListeners) {
          // One listener that throws must not take the tunnel (or the others) with it.
          try {
            listener(payload);
          } catch {
            // Dropped: the message was for that listener alone.
          }
        }
        return;
      }
    }
  }

  private onAuthenticated(info: RelayHostInfo, deviceToken: string | undefined): void {
    const next = afterAuthOk(this.auth, deviceToken);
    let stored: Promise<void> | undefined;
    if (next !== this.auth) {
      // From here on this tunnel — and every later one — authenticates by token.
      this.auth = next;
      if (deviceToken) {
        const result = this.onDeviceToken?.(deviceToken);
        if (result && typeof result.then === 'function') stored = result;
      }
    }
    // The key stays leased until the token is stored, so whoever is waiting behind us
    // reads the token rather than asking the host for a second one.
    if (stored) {
      void stored.then(
        () => this.releaseAuthLease(),
        () => this.releaseAuthLease(),
      );
    } else {
      this.releaseAuthLease();
    }
    this.hostInfo = info;
    this.socketReady = true;
    this.reconnectDelayMs = RECONNECT_MIN_MS;
    this.lastPongAt = Date.now();
    if (this.handshakeTimer) {
      clearTimeout(this.handshakeTimer);
      this.handshakeTimer = undefined;
    }
    this.setState('ready');
    this.onRoute?.(this.attemptRoute);
    const waiters = this.readyWaiters.splice(0, this.readyWaiters.length);
    for (const waiter of waiters) waiter.resolve(info);
    this.startHeartbeat();
    this.pump();
  }

  private startHeartbeat(): void {
    this.stopHeartbeat();
    this.pingTimer = setInterval(() => {
      if (this.currentState !== 'ready') return;
      if (Date.now() - this.lastPongAt > PONG_DEADLINE_MS) {
        const socket = this.socket;
        if (socket) this.dropSocket(socket, 'closed');
        return;
      }
      this.sendEncrypted({ type: 'ping' });
    }, PING_INTERVAL_MS);
  }

  private stopHeartbeat(): void {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = undefined;
    }
  }

  private stopProbe(): void {
    if (this.probeTimer) {
      clearTimeout(this.probeTimer);
      this.probeTimer = undefined;
    }
  }

  private sendEncrypted(value: unknown): void {
    const socket = this.socket;
    const cipher = this.cipher;
    if (!socket || !cipher) return;
    socket.send(cipher.sealJson(value));
  }

  private pump(): void {
    if (this.currentState !== 'ready') return;
    while (this.queue.length > 0 && this.pending.size < MAX_IN_FLIGHT) {
      const entry = this.queue.shift();
      if (!entry) break;
      this.pending.set(entry.id, entry);
      this.sendEncrypted(entry.payload);
    }
  }

  private expire(entry: PendingRequest): void {
    this.pending.delete(entry.id);
    const queued = this.queue.indexOf(entry);
    if (queued >= 0) this.queue.splice(queued, 1);
    entry.reject(new RelayError('timeout'));
    this.pump();
  }

  private settle(entry: PendingRequest, response: RelayResponse): void {
    if (entry.timer) clearTimeout(entry.timer);
    this.pending.delete(entry.id);
    entry.resolve(response);
    this.pump();
  }

  /** Closes the socket ourselves; `onclose` then runs the normal failure path. */
  private dropSocket(socket: RelaySocket, failure: RelayFailure): void {
    this.currentFailure = failure;
    try {
      socket.close(1000, 'client');
    } catch {
      // ignore
    }
    if (this.socket === socket) this.handleClose(undefined, failure);
  }

  private handleClose(code?: number, forcedFailure?: RelayFailure): void {
    const socket = this.socket;
    if (socket) {
      socket.onopen = null;
      socket.onmessage = null;
      socket.onerror = null;
      socket.onclose = null;
    }
    this.socket = undefined;
    this.cipher = undefined;
    this.stopHeartbeat();
    this.stopProbe();
    if (this.handshakeTimer) {
      clearTimeout(this.handshakeTimer);
      this.handshakeTimer = undefined;
    }

    const failure: RelayFailure =
      forcedFailure ??
      this.authFailure ??
      mapCloseCode(code) ??
      (this.currentState === 'connecting' ? 'relay-unreachable' : 'closed');

    const rejection = this.authRejection;
    this.authRejection = undefined;
    this.releaseAuthLease();

    // A direct socket that never got as far as `auth_ok` — the Mac's address now answers
    // with another key, the handshake stalled past its deadline, the listener dropped it,
    // or the Mac refused it — settles nothing. A Mac refuses a direct socket with one
    // generic word, so the reason that counts (a revoked device, a stale key, a clientId
    // conflict) is heard over the relay, which is taken at once; nobody waiting hears of
    // the direct attempt.
    const wasReady = this.socketReady;
    this.socketReady = false;
    if (this.attemptRoute === 'direct' && !wasReady && !this.disposed) {
      this.connect(true);
      return;
    }

    // `device-conflict` means this Mac already holds a token for our id: introduce
    // ourselves under a fresh one, once. Nobody waiting is told anything yet.
    if (rejection?.retryWithNewClientId && this.beginClientIdRetry()) return;

    const error = new RelayError(failure, code, rejection?.message);
    this.failWaiters(error);
    this.failRequests(error);

    if (this.disposed) {
      this.setState('closed', failure);
      return;
    }
    this.setState('closed', failure);

    // A rejected key or a released device never fixes itself; the rest is worth retrying.
    if (!this.autoReconnect || isFinalFailure(failure)) return;
    const delay = this.reconnectDelayMs;
    this.reconnectDelayMs = Math.min(RECONNECT_MAX_MS, this.reconnectDelayMs * 2);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      this.connect();
    }, delay);
  }

  /**
   * Starts the one retry a `device-conflict` earns. Answers false — and the caller then
   * reports the failure as usual — when there is nothing to retry with: no minter, no
   * pairing key to present the new id alongside, or one retry already spent.
   */
  private beginClientIdRetry(): boolean {
    if (this.disposed || this.clientIdRetried) return false;
    if (!this.mintClientId || !this.auth.pairingKey) return false;
    this.clientIdRetried = true;
    this.setState('connecting');
    void this.mintClientId().then(
      (clientId) => {
        if (this.disposed) return;
        if (!clientId) {
          this.giveUp('device-conflict');
          return;
        }
        this.auth = { ...this.auth, clientId };
        this.connect();
      },
      () => {
        if (!this.disposed) this.giveUp('device-conflict');
      },
    );
    return true;
  }

  /** Ends a retry that could not even be started. */
  private giveUp(failure: RelayFailure): void {
    const error = new RelayError(failure);
    this.failWaiters(error);
    this.failRequests(error);
    this.setState('closed', failure);
  }

  private failWaiters(error: Error): void {
    const waiters = this.readyWaiters.splice(0, this.readyWaiters.length);
    for (const waiter of waiters) waiter.reject(error);
  }

  private failRequests(error: Error): void {
    for (const entry of this.pending.values()) {
      if (entry.timer) clearTimeout(entry.timer);
      entry.reject(error);
    }
    this.pending.clear();
    const queued = this.queue.splice(0, this.queue.length);
    for (const entry of queued) entry.reject(error);
  }

  private teardown(error: Error): void {
    this.cancelRace?.();
    const socket = this.socket;
    this.socket = undefined;
    this.cipher = undefined;
    this.releaseAuthLease();
    this.stopHeartbeat();
    this.stopProbe();
    if (this.handshakeTimer) clearTimeout(this.handshakeTimer);
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.handshakeTimer = undefined;
    this.reconnectTimer = undefined;
    this.failWaiters(error);
    this.failRequests(error);
    if (socket) {
      socket.onopen = null;
      socket.onmessage = null;
      socket.onerror = null;
      socket.onclose = null;
      try {
        socket.close(1000, 'client');
      } catch {
        // ignore
      }
    }
  }
}
