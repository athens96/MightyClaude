import { createSign } from 'node:crypto';
import { readFile } from 'node:fs/promises';

export type PushSendResult =
  | { readonly kind: 'sent' }
  | { readonly kind: 'invalid-token' }
  | { readonly kind: 'retry'; readonly retryAfterSecs: number }
  | { readonly kind: 'failed'; readonly status: number; readonly code?: string };

/** Delivers one data-only message to one FCM registration token. */
export interface PushSender {
  send(fcmToken: string, data: Readonly<Record<string, string>>): Promise<PushSendResult>;
}

export interface ServiceAccount {
  readonly projectId: string;
  readonly clientEmail: string;
  readonly privateKey: string;
  readonly tokenUri: string;
}

export type FetchLike = (
  url: string,
  init: { method: string; headers: Record<string, string>; body: string; signal?: AbortSignal },
) => Promise<{ status: number; headers: { get(name: string): string | null }; text(): Promise<string> }>;

const SCOPE = 'https://www.googleapis.com/auth/firebase.messaging';
const DEFAULT_TOKEN_URI = 'https://oauth2.googleapis.com/token';
const DEFAULT_RETRY_SECS = 60;
const FETCH_TIMEOUT_MS = 10_000;
/** FCM error codes are upper-case words; anything else is never written to a log. */
const ERROR_CODE_PATTERN = /^[A-Z_]{1,40}$/;

export function parseServiceAccount(text: string): ServiceAccount {
  let parsed: Record<string, unknown>;
  try {
    parsed = JSON.parse(text) as Record<string, unknown>;
  } catch {
    throw new Error('service account: not JSON');
  }
  const projectId = parsed['project_id'];
  const clientEmail = parsed['client_email'];
  const privateKey = parsed['private_key'];
  const tokenUri = parsed['token_uri'];
  if (typeof projectId !== 'string' || !/^[a-z0-9-]{1,64}$/.test(projectId)) throw new Error('service account: bad project_id');
  if (typeof clientEmail !== 'string' || !clientEmail.includes('@')) throw new Error('service account: bad client_email');
  if (typeof privateKey !== 'string' || !privateKey.includes('PRIVATE KEY')) throw new Error('service account: no private_key');
  return { projectId, clientEmail, privateKey, tokenUri: typeof tokenUri === 'string' ? tokenUri : DEFAULT_TOKEN_URI };
}

function base64url(value: string): string {
  return Buffer.from(value, 'utf8').toString('base64url');
}

interface FcmError {
  /** `error.status` first, then each `details[].errorCode`, upper-case words only. */
  readonly codes: string[];
  /** Whether the error is about the registration token itself (a field violation on `message.token` or a message naming it). */
  readonly namesToken: boolean;
}

function readError(body: string): FcmError {
  try {
    const parsed = JSON.parse(body) as {
      error?: {
        status?: unknown;
        message?: unknown;
        details?: Array<{ errorCode?: unknown; fieldViolations?: Array<{ field?: unknown }> }>;
      };
    };
    const codes: string[] = [];
    let namesToken = typeof parsed.error?.message === 'string' && /registration token/i.test(parsed.error.message);
    if (typeof parsed.error?.status === 'string' && ERROR_CODE_PATTERN.test(parsed.error.status)) codes.push(parsed.error.status);
    for (const detail of parsed.error?.details ?? []) {
      if (typeof detail.errorCode === 'string' && ERROR_CODE_PATTERN.test(detail.errorCode)) codes.push(detail.errorCode);
      for (const violation of detail.fieldViolations ?? []) {
        if (violation.field === 'message.token') namesToken = true;
      }
    }
    return { codes, namesToken };
  } catch {
    return { codes: [], namesToken: false };
  }
}

/** FCM HTTP v1 without firebase-admin: a self-signed RS256 assertion traded for an access token. */
export class FcmHttpV1Sender implements PushSender {
  readonly #account: ServiceAccount;
  readonly #fetch: FetchLike;
  readonly #now: () => number;
  readonly #log: (line: string) => void;
  #token: { readonly value: string; readonly expiresAt: number } | null = null;
  /** Whether the last access-token exchange failed; logged only when this changes. */
  #tokenFailing = false;

  constructor(
    account: ServiceAccount,
    fetchImpl: FetchLike = fetch as unknown as FetchLike,
    now: () => number = Date.now,
    log: (line: string) => void = () => {},
  ) {
    this.#account = account;
    this.#fetch = fetchImpl;
    this.#now = now;
    this.#log = log;
  }

  static async fromFile(path: string, fetchImpl?: FetchLike): Promise<FcmHttpV1Sender> {
    return new FcmHttpV1Sender(parseServiceAccount(await readFile(path, 'utf8')), fetchImpl);
  }

  async send(fcmToken: string, data: Readonly<Record<string, string>>): Promise<PushSendResult> {
    let access: string;
    try {
      access = await this.#accessToken();
    } catch {
      if (!this.#tokenFailing) this.#log('[push] FCM access token exchange failing');
      this.#tokenFailing = true;
      return { kind: 'retry', retryAfterSecs: DEFAULT_RETRY_SECS };
    }
    if (this.#tokenFailing) this.#log('[push] FCM access token exchange working again');
    this.#tokenFailing = false;
    let response: Awaited<ReturnType<FetchLike>>;
    let error: FcmError = { codes: [], namesToken: false };
    try {
      response = await this.#fetch(`https://fcm.googleapis.com/v1/projects/${this.#account.projectId}/messages:send`, {
        method: 'POST',
        headers: { authorization: `Bearer ${access}`, 'content-type': 'application/json' },
        body: JSON.stringify({ message: { token: fcmToken, data, android: { priority: 'HIGH', ttl: '3600s' } } }),
        signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
      });
      if (response.status === 200) return { kind: 'sent' };
      error = readError(await response.text());
    } catch {
      // A network failure or a timeout says nothing about the token; the caller may try again later.
      return { kind: 'retry', retryAfterSecs: DEFAULT_RETRY_SECS };
    }
    // Only a token FCM says is gone or malformed is forgotten. SENDER_ID_MISMATCH (a key from another
    // Firebase project than the app) or a bare 404 is the relay's own setup, so the handle is kept.
    if (error.codes.includes('UNREGISTERED')) return { kind: 'invalid-token' };
    if (error.codes.includes('INVALID_ARGUMENT') && error.namesToken) return { kind: 'invalid-token' };
    if (response.status === 401) this.#token = null;
    if (response.status === 429 || response.status >= 500) {
      const after = Number.parseInt(response.headers.get('retry-after') ?? '', 10);
      return { kind: 'retry', retryAfterSecs: Number.isFinite(after) && after > 0 ? after : DEFAULT_RETRY_SECS };
    }
    // The most specific code: a details errorCode over the general status.
    const code = error.codes.at(-1);
    return code === undefined ? { kind: 'failed', status: response.status } : { kind: 'failed', status: response.status, code };
  }

  async #accessToken(): Promise<string> {
    const now = this.#now();
    if (this.#token !== null && now < this.#token.expiresAt - 60_000) return this.#token.value;
    const iat = Math.floor(now / 1000);
    const header = base64url(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
    const claims = base64url(
      JSON.stringify({ iss: this.#account.clientEmail, scope: SCOPE, aud: this.#account.tokenUri, iat, exp: iat + 3600 }),
    );
    const signer = createSign('RSA-SHA256');
    signer.update(`${header}.${claims}`);
    const assertion = `${header}.${claims}.${signer.sign(this.#account.privateKey).toString('base64url')}`;
    const response = await this.#fetch(this.#account.tokenUri, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion }).toString(),
      signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
    });
    if (response.status !== 200) throw new Error(`token ${response.status}`);
    const parsed = JSON.parse(await response.text()) as { access_token?: unknown; expires_in?: unknown };
    if (typeof parsed.access_token !== 'string') throw new Error('token shape');
    const lifetime = typeof parsed.expires_in === 'number' ? parsed.expires_in : 3600;
    this.#token = { value: parsed.access_token, expiresAt: now + lifetime * 1000 };
    return parsed.access_token;
  }
}
