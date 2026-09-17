import { buildBaseUrl } from '@/lib/pairing';
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

export interface HostCredentials {
  host: string;
  port: number;
  key: string;
}

export interface PollOptions {
  /** Only return once the server revision exceeds this value (or `wait` elapses). */
  since?: number;
  /** Seconds the server may hold the request open, clamped to 0..10. */
  wait?: number;
  signal?: AbortSignal;
}

/** An HTTP-level failure reported by the host with a `{ protocol, error }` body. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }

  /** 401 means the stored key no longer matches the host. */
  get needsRepair(): boolean {
    return this.status === 401;
  }
}

/** A transport failure: host unreachable, DNS failure, timeout, TLS error. */
export class NetworkError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'NetworkError';
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof Error && (error.name === 'AbortError' || error.name === 'CanceledError');
}

export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.needsRepair) return '재페어링 필요';
    return error.message;
  }
  if (error instanceof NetworkError) return '호스트에 연결할 수 없습니다.';
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

export interface MobileClient {
  readonly baseUrl: string;
  buildUrl(path: string): string;
  buildHeaders(withBody: boolean): Record<string, string>;
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

export function createClient(credentials: HostCredentials): MobileClient {
  const baseUrl = buildBaseUrl(credentials.host, credentials.port);

  const buildUrl = (path: string): string => `${baseUrl}${path}`;

  const buildHeaders = (withBody: boolean): Record<string, string> => {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${credentials.key}`,
      'x-mighty-mobile-version': '1',
      Accept: 'application/json',
    };
    if (withBody) headers['Content-Type'] = 'application/json';
    return headers;
  };

  async function request<T>(
    path: string,
    init: { method: 'GET' | 'POST'; body?: unknown; signal?: AbortSignal },
  ): Promise<T> {
    const hasBody = init.body !== undefined;
    let response: Response;
    try {
      response = await fetch(buildUrl(path), {
        method: init.method,
        headers: buildHeaders(hasBody),
        body: hasBody ? JSON.stringify(init.body) : undefined,
        signal: init.signal,
      });
    } catch (error) {
      if (isAbortError(error)) throw error;
      throw new NetworkError(error instanceof Error ? error.message : String(error));
    }

    const raw = await response.text();
    let parsed: unknown = null;
    if (raw.length > 0) {
      try {
        parsed = JSON.parse(raw) as unknown;
      } catch {
        parsed = null;
      }
    }

    if (!response.ok) {
      throw new ApiError(response.status, errorMessageFrom(parsed, response.status));
    }
    if (parsed === null) {
      throw new ApiError(response.status, '응답을 해석할 수 없습니다.');
    }
    return parsed as T;
  }

  return {
    baseUrl,
    buildUrl,
    buildHeaders,

    info: (signal) => request<HostInfo>('/m1/info', { method: 'GET', signal }),

    state: (options) =>
      request<MobileState>(`/m1/state${pollQuery(options)}`, {
        method: 'GET',
        signal: options?.signal,
      }),

    session: (sessionId, options) =>
      request<MobileSessionDetail>(
        `/m1/sessions/${encodeURIComponent(sessionId)}${pollQuery(options)}`,
        { method: 'GET', signal: options?.signal },
      ),

    submit: (sessionId, text, signal) => {
      if (byteLength(text) > MAX_TEXT_BYTES) {
        return Promise.reject(new ApiError(413, '메시지가 너무 깁니다 (최대 32KiB).'));
      }
      return request<SubmitResponse>(`/m1/sessions/${encodeURIComponent(sessionId)}/submit`, {
        method: 'POST',
        body: { text },
        signal,
      });
    },

    stop: (sessionId, signal) =>
      request<StopResponse>(`/m1/sessions/${encodeURIComponent(sessionId)}/stop`, {
        method: 'POST',
        body: {},
        signal,
      }),

    respondPermission: (sessionId, input, signal) =>
      request<OkResponse>(`/m1/sessions/${encodeURIComponent(sessionId)}/permission`, {
        method: 'POST',
        body: input,
        signal,
      }),

    answer: (sessionId, input, signal) =>
      request<OkResponse>(`/m1/sessions/${encodeURIComponent(sessionId)}/answers`, {
        method: 'POST',
        body: input,
        signal,
      }),

    createSession: (workspaceId, input, signal) =>
      request<CreateSessionResponse>(
        `/m1/workspaces/${encodeURIComponent(workspaceId)}/sessions`,
        { method: 'POST', body: input, signal },
      ),
  };
}
