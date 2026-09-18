import {
  RelayConnection,
  RelayError,
  describeRelayFailure,
  type RelayHostInfo,
  type RelayNotification,
  type RelayState,
} from '@/api/relay/transport';
import { appForeground } from '@/api/relay/foreground';
import { clientDeviceName } from '@/lib/device';
import {
  MAX_TEXT_BYTES,
  MAX_WAIT_SECONDS,
  type CreateSessionResponse,
  type HostInfo,
  type MobileSessionDetail,
  type MobileState,
  type OkResponse,
  type Provider,
  type QuestionAnswers,
  type SessionKind,
  type StopResponse,
  type SubmitResponse,
} from '@/api/types';

/** Everything needed to open an encrypted tunnel to one paired desktop. */
export interface HostCredentials {
  serverId: string;
  relayUrl: string;
  hostPublicKeyB64: string;
  pairingKey: string;
}

export interface PollOptions {
  /** Only return once the server revision exceeds this value (or `wait` elapses). */
  since?: number;
  /** Seconds the server may hold the request open, clamped to 0..10. */
  wait?: number;
  signal?: AbortSignal;
}

/** An m1-level failure reported by the host with a `{ protocol, error }` body. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }

  /** 401 means the stored pairing key no longer matches the host. */
  get needsRepair(): boolean {
    return this.status === 401;
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof Error && (error.name === 'AbortError' || error.name === 'CanceledError');
}

/** True when the caller should send the user back through pairing. */
export function needsRepair(error: unknown): boolean {
  if (error instanceof ApiError) return error.needsRepair;
  if (error instanceof RelayError) return error.needsRepair;
  return false;
}

export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.needsRepair) return '재페어링 필요';
    return error.message;
  }
  if (error instanceof RelayError) return describeRelayFailure(error.failure);
  if (error instanceof Error) return error.message;
  return '알 수 없는 오류';
}

function clampWait(wait: number | undefined): number {
  if (wait === undefined) return 0;
  if (!Number.isFinite(wait)) return 0;
  return Math.min(MAX_WAIT_SECONDS, Math.max(0, Math.floor(wait)));
}

function pollQuery(options: PollOptions | undefined): string {
  const params = new URLSearchParams();
  if (options?.since !== undefined) params.set('since', String(options.since));
  params.set('wait', String(clampWait(options?.wait)));
  return `?${params.toString()}`;
}

function errorMessageFrom(body: unknown, status: number): string {
  if (body !== null && typeof body === 'object' && 'error' in body) {
    const value = (body as { error: unknown }).error;
    if (typeof value === 'string' && value.length > 0) return value;
  }
  return `HTTP ${status}`;
}

export function byteLength(text: string): number {
  let bytes = 0;
  for (const char of text) {
    const code = char.codePointAt(0) ?? 0;
    if (code < 0x80) bytes += 1;
    else if (code < 0x800) bytes += 2;
    else if (code < 0x10000) bytes += 3;
    else bytes += 4;
  }
  return bytes;
}

/** The part of `RelayConnection` the client needs; keeps tests free of sockets. */
export interface RelayChannel {
  request(
    method: 'GET' | 'POST',
    path: string,
    body?: unknown,
    timeoutMs?: number,
  ): Promise<{ status: number; body: unknown }>;
  onNotify(listener: (event: RelayNotification) => void): () => void;
  onStateChange?(listener: (state: RelayState, failure?: string) => void): () => void;
  ready?(): Promise<RelayHostInfo>;
}

export interface MobileClient {
  /** Fires when the host says a scope changed, so pending polls can re-issue. */
  onNotify(listener: (event: RelayNotification) => void): () => void;
  info(signal?: AbortSignal): Promise<HostInfo>;
  state(options?: PollOptions): Promise<MobileState>;
  session(sessionId: string, options?: PollOptions): Promise<MobileSessionDetail>;
  submit(sessionId: string, text: string, signal?: AbortSignal): Promise<SubmitResponse>;
  stop(sessionId: string, signal?: AbortSignal): Promise<StopResponse>;
  respondPermission(
    sessionId: string,
    input: { requestId: string; runId: string; allow: boolean },
    signal?: AbortSignal,
  ): Promise<OkResponse>;
  answer(
    sessionId: string,
    input: { requestId: string; runId: string; answers: QuestionAnswers },
    signal?: AbortSignal,
  ): Promise<OkResponse>;
  createSession(
    workspaceId: string,
    input: { kind: SessionKind; provider?: Provider },
    signal?: AbortSignal,
  ): Promise<CreateSessionResponse>;
}

class AbortedError extends Error {
  constructor() {
    super('요청이 취소되었습니다.');
    this.name = 'AbortError';
  }
}

/**
 * Wraps a relay channel in the m1 REST surface the screens already use. Each call
 * becomes one `{id, method, path, body}` frame and resolves from `{id, status, body}`.
 */
export function createClient(channel: RelayChannel): MobileClient {
  async function request<T>(
    method: 'GET' | 'POST',
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    if (signal?.aborted) throw new AbortedError();
    const inflight = channel.request(method, path, body);
    // The host answers the abandoned request at its own pace; we simply stop waiting.
    const response = signal
      ? await Promise.race([
          inflight,
          new Promise<never>((_, reject) => {
            signal.addEventListener('abort', () => reject(new AbortedError()), { once: true });
          }),
        ])
      : await inflight;

    if (response.status < 200 || response.status >= 300) {
      throw new ApiError(response.status, errorMessageFrom(response.body, response.status));
    }
    if (response.body === null || response.body === undefined) {
      throw new ApiError(response.status, '응답을 해석할 수 없습니다.');
    }
    return response.body as T;
  }

  return {
    onNotify: (listener) => channel.onNotify(listener),

    info: (signal) => request<HostInfo>('GET', '/m1/info', undefined, signal),

    state: (options) =>
      request<MobileState>('GET', `/m1/state${pollQuery(options)}`, undefined, options?.signal),

    session: (sessionId, options) =>
      request<MobileSessionDetail>(
        'GET',
        `/m1/sessions/${encodeURIComponent(sessionId)}${pollQuery(options)}`,
        undefined,
        options?.signal,
      ),

    submit: (sessionId, text, signal) => {
      if (byteLength(text) > MAX_TEXT_BYTES) {
        return Promise.reject(new ApiError(413, '메시지가 너무 깁니다 (최대 32KiB).'));
      }
      return request<SubmitResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/submit`,
        { text },
        signal,
      );
    },

    stop: (sessionId, signal) =>
      request<StopResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/stop`,
        {},
        signal,
      ),

    respondPermission: (sessionId, input, signal) =>
      request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/permission`,
        input,
        signal,
      ),

    answer: (sessionId, input, signal) =>
      request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/answers`,
        input,
        signal,
      ),

    createSession: (workspaceId, input, signal) =>
      request<CreateSessionResponse>(
        'POST',
        `/m1/workspaces/${encodeURIComponent(workspaceId)}/sessions`,
        input,
        signal,
      ),
  };
}

// --------------------------------------------------------------- shared pool

interface PoolEntry {
  fingerprint: string;
  connection: RelayConnection;
  client: MobileClient;
  refs: number;
}

const pool = new Map<string, PoolEntry>();

function fingerprintOf(credentials: HostCredentials): string {
  return [
    credentials.serverId,
    credentials.relayUrl,
    credentials.hostPublicKeyB64,
    credentials.pairingKey,
  ].join(' ');
}

export interface HostLease {
  client: MobileClient;
  connection: RelayConnection;
  release: () => void;
}

/**
 * One shared, reference-counted connection per host: every mounted screen leases the
 * same tunnel and the socket closes once the last one releases it.
 */
export function acquireHostClient(hostId: string, credentials: HostCredentials): HostLease {
  const fingerprint = fingerprintOf(credentials);
  let entry = pool.get(hostId);
  if (entry && entry.fingerprint !== fingerprint) {
    entry.connection.close();
    pool.delete(hostId);
    entry = undefined;
  }
  if (!entry) {
    const connection = new RelayConnection(credentials, {
      clientName: clientDeviceName(),
      foreground: appForeground,
    });
    entry = { fingerprint, connection, client: createClient(connection), refs: 0 };
    pool.set(hostId, entry);
  }
  entry.refs += 1;
  const leased = entry;
  let released = false;
  return {
    client: leased.client,
    connection: leased.connection,
    release: () => {
      if (released) return;
      released = true;
      leased.refs -= 1;
      if (leased.refs > 0) return;
      if (pool.get(hostId) === leased) pool.delete(hostId);
      leased.connection.close();
    },
  };
}

/** Drops a host's shared connection, e.g. after unpairing. */
export function closeHostClient(hostId: string): void {
  const entry = pool.get(hostId);
  if (!entry) return;
  pool.delete(hostId);
  entry.connection.close();
}

/**
 * One-shot reachability check for the hosts list: connect, handshake, authenticate,
 * then hang up. Rejects with a `RelayError` carrying the reason.
 */
export async function probeHost(credentials: HostCredentials): Promise<RelayHostInfo> {
  const connection = new RelayConnection(credentials, {
    clientName: clientDeviceName(),
    autoReconnect: false,
  });
  try {
    return await connection.ready();
  } finally {
    connection.close();
  }
}

export { RelayError };
