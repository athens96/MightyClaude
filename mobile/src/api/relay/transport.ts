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
const HANDSHAKE_TIMEOUT_MS = 20_000;

export type RelayState = 'connecting' | 'handshaking' | 'ready' | 'closed';

export type RelayFailure =
  | 'host-offline'
  | 'host-not-attached'
  | 'unpaired'
  | 'too-many'
  | 'relay-unreachable'
  | 'protocol'
  | 'timeout'
  | 'closed';

const failureMessages: Record<RelayFailure, string> = {
  'host-offline': '호스트 오프라인',
  'host-not-attached': '호스트가 응답하지 않습니다',
  unpaired: '재페어링 필요',
  'too-many': '연결이 너무 많습니다',
  'relay-unreachable': '릴레이 연결 안 됨',
  protocol: '릴레이 프로토콜 오류',
  timeout: '응답 시간이 초과되었습니다',
  closed: '연결이 끊어졌습니다',
};

export function describeRelayFailure(failure: RelayFailure): string {
  return failureMessages[failure];
}

/** A relay/transport-level failure, as opposed to an `ApiError` from the host. */
export class RelayError extends Error {
  readonly failure: RelayFailure;
  readonly closeCode?: number;

  constructor(failure: RelayFailure, closeCode?: number, message?: string) {
    super(message ?? failureMessages[failure]);
    this.name = 'RelayError';
    this.failure = failure;
    this.closeCode = closeCode;
  }

  /** True when the stored pairing key no longer matches the host. */
  get needsRepair(): boolean {
    return this.failure === 'unpaired';
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
  pairingKey: string;
}

export interface RelayHostInfo {
  hostName: string;
  hostId: string;
  appVersion: string;
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

export interface RelayConnectionOptions {
  /** Shown on the desktop as the connected device. */
  clientName?: string;
  /** Disable to make a single attempt — used by the hosts-list reachability probe. */
  autoReconnect?: boolean;
  createSocket?: RelaySocketFactory;
  foreground?: ForegroundSignal;
  requestTimeoutMs?: number;
}

function defaultSocketFactory(url: string): RelaySocket {
  const Ctor = (globalThis as { WebSocket?: new (url: string) => RelaySocket }).WebSocket;
  if (!Ctor) throw new RelayError('relay-unreachable', undefined, 'WebSocket을 사용할 수 없습니다.');
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

  private reconnectDelayMs = RECONNECT_MIN_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private handshakeTimer: ReturnType<typeof setTimeout> | undefined;
  private pingTimer: ReturnType<typeof setInterval> | undefined;
  private lastPongAt = 0;
  private disposed = false;
  private authErrorSeen = false;
  private unsubscribeForeground: (() => void) | undefined;

  private readonly clientName: string;
  private readonly autoReconnect: boolean;
  private readonly createSocket: RelaySocketFactory;
  private readonly requestTimeoutMs: number;

  constructor(
    private readonly target: RelayTarget,
    options: RelayConnectionOptions = {},
  ) {
    this.clientName = options.clientName ?? 'Phone';
    this.autoReconnect = options.autoReconnect ?? true;
    this.createSocket = options.createSocket ?? defaultSocketFactory;
    this.requestTimeoutMs = options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
    if (options.foreground) {
      this.unsubscribeForeground = options.foreground.subscribe(() => this.reconnectNow());
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

  onNotify(listener: (event: RelayNotification) => void): () => void {
    this.notifyListeners.add(listener);
    return () => this.notifyListeners.delete(listener);
  }

  onStateChange(listener: (state: RelayState, failure?: RelayFailure) => void): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
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
      if (this.disposed || this.currentFailure === 'unpaired') {
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
    if (this.currentFailure === 'unpaired' || this.socket) return;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }
    this.reconnectDelayMs = RECONNECT_MIN_MS;
    this.connect();
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
  }

  // ---------------------------------------------------------------- internals

  private setState(state: RelayState, failure?: RelayFailure): void {
    this.currentState = state;
    this.currentFailure = failure;
    for (const listener of this.stateListeners) listener(state, failure);
  }

  private connect(): void {
    if (this.disposed) return;
    this.authErrorSeen = false;
    this.setState('connecting');

    const keyPair = generateKeyPair();
    const clientNonce = generateHandshakeNonce();
    const url = buildRelayUrl(this.target.relayUrl, this.target.serverId, randomUuid());

    let socket: RelaySocket;
    try {
      socket = this.createSocket(url);
    } catch (error) {
      this.handleClose(undefined, error instanceof RelayError ? error.failure : 'relay-unreachable');
      return;
    }
    this.socket = socket;
    socket.binaryType = 'arraybuffer';

    this.handshakeTimer = setTimeout(() => {
      if (this.currentState !== 'ready') this.dropSocket(socket, 'host-not-attached');
    }, HANDSHAKE_TIMEOUT_MS);

    socket.onopen = () => {
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

    socket.onmessage = (event) => {
      if (this.socket !== socket) return;
      try {
        this.handleMessage(event.data, keyPair.secretKey, clientNonce);
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
  }

  private handleMessage(data: unknown, secretKey: Uint8Array, clientNonce: Uint8Array): void {
    if (typeof data === 'string') {
      this.handleHandshake(data, secretKey, clientNonce);
      return;
    }
    const bytes = toBytes(data);
    if (!bytes) throw new RelayError('protocol', undefined, '알 수 없는 프레임 형식입니다.');
    const cipher = this.cipher;
    if (!cipher) throw new RelayError('protocol', undefined, '핸드셰이크 전 암호 프레임입니다.');
    let message: unknown;
    try {
      message = cipher.openJson(bytes);
    } catch (error) {
      throw new RelayError(
        'protocol',
        undefined,
        error instanceof RelayCryptoError ? error.message : '프레임을 열 수 없습니다.',
      );
    }
    this.handleEnvelope(message);
  }

  private handleHandshake(raw: string, secretKey: Uint8Array, clientNonce: Uint8Array): void {
    let parsed: { type?: string; serverKey?: string; nonce?: string };
    try {
      parsed = JSON.parse(raw) as typeof parsed;
    } catch {
      throw new RelayError('protocol', undefined, '핸드셰이크 응답을 해석할 수 없습니다.');
    }
    if (parsed.type !== 'ready' || !parsed.serverKey || !parsed.nonce) {
      throw new RelayError('protocol', undefined, '핸드셰이크 응답이 올바르지 않습니다.');
    }

    const serverKey = fromBase64(parsed.serverKey);
    if (!bytesEqual(serverKey, fromBase64(this.target.hostPublicKeyB64))) {
      throw new RelayError('unpaired', undefined, '호스트 공개키가 페어링 정보와 다릅니다.');
    }

    const key = deriveSessionKey({
      secretKey,
      peerPublicKey: serverKey,
      clientNonce,
      serverNonce: fromBase64(parsed.nonce),
    });
    this.cipher = new RelayCipher(key, DIRECTION_CLIENT_TO_HOST, DIRECTION_HOST_TO_CLIENT);
    this.sendEncrypted({
      type: 'auth',
      pairingKey: this.target.pairingKey,
      clientName: this.clientName,
    });
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
    };

    if (typeof envelope.id === 'string' && envelope.type === undefined) {
      const entry = this.pending.get(envelope.id);
      if (!entry) return; // stale response for an abandoned request
      this.settle(entry, { status: envelope.status ?? 0, body: envelope.body ?? null });
      return;
    }

    switch (envelope.type) {
      case 'auth_ok': {
        this.onAuthenticated({
          hostName: envelope.hostName ?? '',
          hostId: envelope.hostId ?? '',
          appVersion: envelope.appVersion ?? '',
        });
        return;
      }
      case 'auth_error': {
        this.authErrorSeen = true;
        throw new RelayError('unpaired');
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
      default:
        return;
    }
  }

  private onAuthenticated(info: RelayHostInfo): void {
    this.hostInfo = info;
    this.reconnectDelayMs = RECONNECT_MIN_MS;
    this.lastPongAt = Date.now();
    if (this.handshakeTimer) {
      clearTimeout(this.handshakeTimer);
      this.handshakeTimer = undefined;
    }
    this.setState('ready');
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
    if (this.handshakeTimer) {
      clearTimeout(this.handshakeTimer);
      this.handshakeTimer = undefined;
    }

    const failure: RelayFailure =
      forcedFailure ??
      (this.authErrorSeen ? 'unpaired' : undefined) ??
      mapCloseCode(code) ??
      (this.currentState === 'connecting' ? 'relay-unreachable' : 'closed');

    const error = new RelayError(failure, code);
    this.failWaiters(error);
    this.failRequests(error);

    if (this.disposed) {
      this.setState('closed', failure);
      return;
    }
    this.setState('closed', failure);

    // A rejected pairing key never fixes itself; everything else is worth retrying.
    if (!this.autoReconnect || failure === 'unpaired') return;
    const delay = this.reconnectDelayMs;
    this.reconnectDelayMs = Math.min(RECONNECT_MAX_MS, this.reconnectDelayMs * 2);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      this.connect();
    }, delay);
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
    const socket = this.socket;
    this.socket = undefined;
    this.cipher = undefined;
    this.stopHeartbeat();
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
