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
import { KEY_SEPARATOR } from '@/lib/keys';
import {
  ENTRY_PAGE_SIZE,
  MAX_ENTRY_PAGE,
  MAX_TEXT_BYTES,
  MAX_TITLE_LENGTH,
  MAX_WAIT_SECONDS,
  type CommandResponse,
  type CommandsResponse,
  type CreateSessionResponse,
  type EntriesPage,
  type HostInfo,
  type MessageCommandAction,
  type MobileSessionDetail,
  type MobileState,
  type OkResponse,
  type Provider,
  type QuestionAnswers,
  type SessionKind,
  type SettingsPatch,
  type StopResponse,
  type SubmitOptions,
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

/** True when the host does not know the pane or item any more (closed, evicted). */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
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

function clampLimit(limit: number | undefined): number {
  if (limit === undefined || !Number.isFinite(limit)) return ENTRY_PAGE_SIZE;
  return Math.min(MAX_ENTRY_PAGE, Math.max(1, Math.floor(limit)));
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

export interface EntriesOptions {
  /** Ask for entries older than this one. */
  before: string;
  /** 1..100; defaults to the app's page size. */
  limit?: number;
  signal?: AbortSignal;
}

export interface MobileClient {
  /** Fires when the host says a scope changed, so pending polls can re-issue. */
  onNotify(listener: (event: RelayNotification) => void): () => void;
  info(signal?: AbortSignal): Promise<HostInfo>;
  state(options?: PollOptions): Promise<MobileState>;
  session(sessionId: string, options?: PollOptions): Promise<MobileSessionDetail>;
  submit(
    sessionId: string,
    text: string,
    options?: SubmitOptions,
    signal?: AbortSignal,
  ): Promise<SubmitResponse>;
  stop(sessionId: string, signal?: AbortSignal): Promise<StopResponse>;
  /** Drops one queued request ("queue"). */
  removeQueued(sessionId: string, itemId: string, signal?: AbortSignal): Promise<OkResponse>;
  /** Starts the first queued request on an idle pane ("queue"). */
  runNext(sessionId: string, signal?: AbortSignal): Promise<OkResponse>;
  /** Renames the pane ("pane"); the title is trimmed to 1..80 characters. */
  rename(sessionId: string, title: string, signal?: AbortSignal): Promise<OkResponse>;
  /** Closes the pane ("pane"); irreversible. */
  close(sessionId: string, signal?: AbortSignal): Promise<OkResponse>;
  /** One page of older log entries ("history"). */
  entries(sessionId: string, options: EntriesOptions): Promise<EntriesPage>;
  /** Writes one or more pane settings ("settings"). */
  updateSettings(
    sessionId: string,
    patch: SettingsPatch,
    signal?: AbortSignal,
  ): Promise<OkResponse>;
  /** Slash commands offered for this pane ("commands"). */
  commands(sessionId: string, signal?: AbortSignal): Promise<CommandsResponse>;
  /** Runs one of the host-side commands ("commands"). */
  runCommand(
    sessionId: string,
    action: MessageCommandAction,
    signal?: AbortSignal,
  ): Promise<CommandResponse>;
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

    submit: (sessionId, text, options, signal) => {
      if (byteLength(text) > MAX_TEXT_BYTES) {
        return Promise.reject(new ApiError(413, '메시지가 너무 깁니다 (최대 32KiB).'));
      }
      // `mode` and `attachments` stay out of the body unless asked for, so older
      // hosts keep seeing exactly the request they saw before.
      const body: Record<string, unknown> = { text };
      if (options?.mode) body.mode = options.mode;
      if (options?.attachments?.length) body.attachments = options.attachments;
      return request<SubmitResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/submit`,
        body,
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

    removeQueued: (sessionId, itemId, signal) =>
      request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/queue/${encodeURIComponent(itemId)}/remove`,
        {},
        signal,
      ),

    runNext: (sessionId, signal) =>
      request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/queue/run-next`,
        {},
        signal,
      ),

    rename: (sessionId, title, signal) => {
      const trimmed = title.trim();
      if (trimmed.length === 0 || trimmed.length > MAX_TITLE_LENGTH) {
        return Promise.reject(new ApiError(400, `이름은 1~${MAX_TITLE_LENGTH}자여야 합니다.`));
      }
      return request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/rename`,
        { title: trimmed },
        signal,
      );
    },

    close: (sessionId, signal) =>
      request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/close`,
        {},
        signal,
      ),

    entries: (sessionId, options) => {
      const params = new URLSearchParams();
      params.set('before', options.before);
      params.set('limit', String(clampLimit(options.limit)));
      return request<EntriesPage>(
        'GET',
        `/m1/sessions/${encodeURIComponent(sessionId)}/entries?${params.toString()}`,
        undefined,
        options.signal,
      );
    },

    updateSettings: (sessionId, patch, signal) => {
      const body = Object.fromEntries(
        Object.entries(patch).filter(([, value]) => value !== undefined),
      );
      if (Object.keys(body).length === 0) {
        return Promise.reject(new ApiError(400, '변경할 설정이 없습니다.'));
      }
      return request<OkResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/settings`,
        body,
        signal,
      );
    },

    commands: (sessionId, signal) =>
      request<CommandsResponse>(
        'GET',
        `/m1/sessions/${encodeURIComponent(sessionId)}/commands`,
        undefined,
        signal,
      ),

    runCommand: (sessionId, action, signal) =>
      request<CommandResponse>(
        'POST',
        `/m1/sessions/${encodeURIComponent(sessionId)}/command`,
        { action },
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
  ].join(KEY_SEPARATOR);
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
