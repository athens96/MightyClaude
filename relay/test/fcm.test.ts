import { createVerify, generateKeyPairSync } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import { FcmHttpV1Sender, parseServiceAccount, type FetchLike } from '../src/fcm.ts';

// A throwaway key made for this run; no real credential is ever in the repository.
const { privateKey, publicKey } = generateKeyPairSync('rsa', {
  modulusLength: 2048,
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
  publicKeyEncoding: { type: 'spki', format: 'pem' },
});

const ACCOUNT_JSON = JSON.stringify({
  type: 'service_account',
  project_id: 'demo-project',
  client_email: 'relay@demo-project.iam.gserviceaccount.com',
  private_key: privateKey,
  token_uri: 'https://oauth2.googleapis.com/token',
});

interface Call {
  url: string;
  init: { method: string; headers: Record<string, string>; body: string };
}

function reply(status: number, body: unknown, headers: Record<string, string> = {}) {
  return {
    status,
    headers: { get: (name: string) => headers[name.toLowerCase()] ?? null },
    text: async () => JSON.stringify(body),
  };
}

function fake(answers: Array<ReturnType<typeof reply>>, calls: Call[]): FetchLike {
  return async (url, init) => {
    calls.push({ url, init });
    const next = answers.shift();
    if (next === undefined) throw new Error('unexpected fetch');
    return next;
  };
}

const TOKEN_REPLY = reply(200, { access_token: 'ya29.test', expires_in: 3600, token_type: 'Bearer' });

describe('parseServiceAccount', () => {
  it('reads the four fields it needs', () => {
    expect(parseServiceAccount(ACCOUNT_JSON)).toEqual({
      projectId: 'demo-project',
      clientEmail: 'relay@demo-project.iam.gserviceaccount.com',
      privateKey,
      tokenUri: 'https://oauth2.googleapis.com/token',
    });
  });

  it('refuses a file without a private key', () => {
    expect(() => parseServiceAccount('{"project_id":"x","client_email":"a@b"}')).toThrow(/service account/);
  });
});

describe('FcmHttpV1Sender', () => {
  it('signs an RS256 assertion, then sends a high-priority data-only message', async () => {
    const calls: Call[] = [];
    const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(200, { name: 'm/1' })], calls), () => 1_000_000);
    expect(await sender.send('device-token', { v: '1', h: 'H', p: 'P' })).toEqual({ kind: 'sent' });

    const tokenCall = calls[0]!;
    expect(tokenCall.url).toBe('https://oauth2.googleapis.com/token');
    const form = new URLSearchParams(tokenCall.init.body);
    expect(form.get('grant_type')).toBe('urn:ietf:params:oauth:grant-type:jwt-bearer');
    const [header, claims, signature] = form.get('assertion')!.split('.');
    expect(JSON.parse(Buffer.from(header!, 'base64url').toString())).toEqual({ alg: 'RS256', typ: 'JWT' });
    expect(JSON.parse(Buffer.from(claims!, 'base64url').toString())).toEqual({
      iss: 'relay@demo-project.iam.gserviceaccount.com',
      scope: 'https://www.googleapis.com/auth/firebase.messaging',
      aud: 'https://oauth2.googleapis.com/token',
      iat: 1000,
      exp: 4600,
    });
    const verify = createVerify('RSA-SHA256');
    verify.update(`${header}.${claims}`);
    expect(verify.verify(publicKey, Buffer.from(signature!, 'base64url'))).toBe(true);

    const sendCall = calls[1]!;
    expect(sendCall.url).toBe('https://fcm.googleapis.com/v1/projects/demo-project/messages:send');
    expect(sendCall.init.headers['authorization']).toBe('Bearer ya29.test');
    expect(JSON.parse(sendCall.init.body)).toEqual({
      message: { token: 'device-token', data: { v: '1', h: 'H', p: 'P' }, android: { priority: 'HIGH', ttl: '3600s' } },
    });
  });

  it('reuses the access token until a minute before it expires', async () => {
    const calls: Call[] = [];
    let now = 0;
    const sender = new FcmHttpV1Sender(
      parseServiceAccount(ACCOUNT_JSON),
      fake([TOKEN_REPLY, reply(200, {}), reply(200, {}), TOKEN_REPLY, reply(200, {})], calls),
      () => now,
    );
    await sender.send('t', { v: '1' });
    now = 3_500_000;
    await sender.send('t', { v: '1' });
    now = 3_550_000;
    await sender.send('t', { v: '1' });
    expect(calls.map((c) => c.url.includes('oauth2'))).toEqual([true, false, false, true, false]);
  });

  it('maps a gone token, a bad token and another project to invalid-token', async () => {
    for (const answer of [
      reply(404, { error: { status: 'NOT_FOUND', details: [{ errorCode: 'UNREGISTERED' }] } }),
      reply(400, { error: { status: 'INVALID_ARGUMENT' } }),
      reply(403, { error: { status: 'PERMISSION_DENIED', details: [{ errorCode: 'SENDER_ID_MISMATCH' }] } }),
    ]) {
      const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, answer], []), () => 0);
      expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'invalid-token' });
    }
  });

  it('asks to retry on 429 and 5xx, honouring Retry-After', async () => {
    const limited = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(429, {}, { 'retry-after': '120' })], []), () => 0);
    expect(await limited.send('t', { v: '1' })).toEqual({ kind: 'retry', retryAfterSecs: 120 });
    const down = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(503, {})], []), () => 0);
    expect(await down.send('t', { v: '1' })).toEqual({ kind: 'retry', retryAfterSecs: 60 });
  });

  it('asks to retry when the network fails instead of throwing', async () => {
    const answers = [TOKEN_REPLY];
    const offline: FetchLike = async () => {
      const next = answers.shift();
      if (next === undefined) throw new Error('offline');
      return next;
    };
    const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), offline, () => 0);
    expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'retry', retryAfterSecs: 60 });
  });

  it('drops the cached access token after a 401', async () => {
    const calls: Call[] = [];
    const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(401, {}), TOKEN_REPLY, reply(200, {})], calls), () => 0);
    expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'failed', status: 401 });
    expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'sent' });
    expect(calls.filter((c) => c.url.includes('oauth2'))).toHaveLength(2);
  });
});
