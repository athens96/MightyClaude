import { fromBase64 } from '@/api/relay/crypto';
import {
  CLIENT_ID_BYTES,
  afterAuthOk,
  authFrameFor,
  authRejectionFor,
  deviceTokenFrom,
  generateClientId,
  type DeviceAuthState,
} from '@/lib/device-token';
import {
  CLIENT_ID_KEY,
  DEVICE_TOKEN_PREFIX,
  HOST_CLIENT_ID_PREFIX,
  PAIRING_KEY_PREFIX,
  adoptDeviceToken,
  canAuthenticate,
  ensureClientId,
  forgetHost,
  forgetHostSecrets,
  mintHostClientId,
  readHostClientId,
  readHostSecrets,
  writeHostSecrets,
  type SecretStore,
} from '@/lib/host-secrets';

function memoryStore(initial: Record<string, string> = {}) {
  const values = new Map<string, string>(Object.entries(initial));
  const store: SecretStore = {
    getItemAsync: (key) => Promise.resolve(values.get(key) ?? null),
    setItemAsync: (key, value) => {
      values.set(key, value);
      return Promise.resolve();
    },
    deleteItemAsync: (key) => {
      values.delete(key);
      return Promise.resolve();
    },
  };
  return { store, values };
}

describe('generateClientId', () => {
  it('makes 16 random bytes as unpadded base64url', () => {
    const id = generateClientId();
    expect(fromBase64(id)).toHaveLength(CLIENT_ID_BYTES);
    expect(id).toMatch(/^[A-Za-z0-9_-]{22}$/);
    expect(generateClientId()).not.toBe(id);
  });
});

describe('authFrameFor', () => {
  it('sends the pairing key with the clientId on the first connection', () => {
    expect(authFrameFor({ clientId: 'c1', pairingKey: 'k1' }, '유진 아이폰')).toEqual({
      type: 'auth',
      clientId: 'c1',
      pairingKey: 'k1',
      clientName: '유진 아이폰',
    });
  });

  it('sends the token alone once one is held', () => {
    expect(
      authFrameFor({ clientId: 'c1', pairingKey: 'k1', deviceToken: 't1' }, 'Phone'),
    ).toEqual({ type: 'auth', clientId: 'c1', deviceToken: 't1', clientName: 'Phone' });
  });

  it('falls back to the pairing key when the install has no clientId yet', () => {
    expect(authFrameFor({ pairingKey: 'k1' }, 'Phone')).toEqual({
      type: 'auth',
      pairingKey: 'k1',
      clientName: 'Phone',
    });
    // A token without a clientId cannot be presented; the key is what is left.
    expect(authFrameFor({ pairingKey: 'k1', deviceToken: 't1' }, 'Phone')).toEqual({
      type: 'auth',
      pairingKey: 'k1',
      clientName: 'Phone',
    });
  });

  it('has nothing to send when every secret is gone', () => {
    expect(authFrameFor({ clientId: 'c1' }, 'Phone')).toBeUndefined();
    expect(authFrameFor({}, 'Phone')).toBeUndefined();
  });
});

describe('afterAuthOk', () => {
  const paired: DeviceAuthState = { clientId: 'c1', pairingKey: 'k1' };

  it('adopts the token and forgets the pairing key', () => {
    expect(afterAuthOk(paired, 't1')).toEqual({ clientId: 'c1', deviceToken: 't1' });
  });

  it('leaves a host that issued no token exactly as it was', () => {
    expect(afterAuthOk(paired, undefined)).toBe(paired);
    expect(afterAuthOk(paired, '')).toBe(paired);
  });

  it('is a no-op when the host repeats the token we already hold', () => {
    const withToken: DeviceAuthState = { clientId: 'c1', deviceToken: 't1' };
    expect(afterAuthOk(withToken, 't1')).toBe(withToken);
  });

  it('keeps the pairing key when there is no clientId to present a token with', () => {
    const legacy: DeviceAuthState = { pairingKey: 'k1' };
    expect(afterAuthOk(legacy, 't1')).toBe(legacy);
  });
});

describe('auth_error reasons', () => {
  it('ends the host for good on the two reasons that mean the secret is worthless', () => {
    for (const reason of ['pairing-key', 'device-revoked'] as const) {
      const rejection = authRejectionFor(reason);
      expect(rejection.reason).toBe(reason);
      expect(rejection.final).toBe(true);
      expect(rejection.retryWithNewClientId).toBe(false);
      expect(rejection.message.length).toBeGreaterThan(0);
    }
    expect(authRejectionFor('device-revoked').message).toContain('Mac에서 해제');
    expect(authRejectionFor('pairing-key').message).toBe('재페어링 필요');
  });

  it('answers a clientId collision with one retry under a new id', () => {
    const rejection = authRejectionFor('device-conflict');
    expect(rejection).toMatchObject({
      reason: 'device-conflict',
      final: false,
      retryWithNewClientId: true,
    });
  });

  it('keeps the secrets on a full device list and says what to do about it', () => {
    const rejection = authRejectionFor('device-limit');
    expect(rejection).toMatchObject({
      reason: 'device-limit',
      final: false,
      retryWithNewClientId: false,
    });
    expect(rejection.message).toBe(
      'Mac의 기기 목록이 가득 찼거나 등록이 잠시 제한되었습니다. Mac 설정에서 쓰지 않는 기기를 해제한 뒤 다시 시도하세요.',
    );
  });

  it('treats "legacy-refused" as a plain error, since this app always sends a clientId', () => {
    expect(authRejectionFor('legacy-refused')).toMatchObject({
      reason: 'legacy-refused',
      final: false,
      retryWithNewClientId: false,
    });
  });

  it('never destroys credentials for a reason it does not understand', () => {
    const unknowns: unknown[] = ['malformed', 'teapot', '', undefined, null, 42, { reason: 'x' }];
    for (const reason of unknowns) {
      const rejection = authRejectionFor(reason);
      expect(rejection.final).toBe(false);
      expect(rejection.retryWithNewClientId).toBe(false);
      expect(rejection.message).toBe('호스트가 인증을 거절했습니다. 잠시 후 다시 시도합니다.');
    }
    expect(authRejectionFor('malformed').reason).toBe('malformed');
    expect(authRejectionFor('teapot').reason).toBe('unknown');
  });

  it('reads a token out of auth_ok only when it is a non-empty string', () => {
    expect(deviceTokenFrom({ deviceToken: 't1' })).toBe('t1');
    expect(deviceTokenFrom({ deviceToken: '' })).toBeUndefined();
    expect(deviceTokenFrom({ deviceToken: 7 })).toBeUndefined();
    expect(deviceTokenFrom({})).toBeUndefined();
  });
});

describe('client id storage', () => {
  it('generates one on first use and keeps it afterwards', async () => {
    const { store, values } = memoryStore();
    const first = await ensureClientId(store, () => 'made-up-id');
    expect(first).toBe('made-up-id');
    expect(values.get(CLIENT_ID_KEY)).toBe('made-up-id');
    expect(await ensureClientId(store, () => 'another')).toBe('made-up-id');
  });
});

describe('host secret storage', () => {
  it('stores a pairing key for a host that issued no token', async () => {
    const { store, values } = memoryStore();
    await writeHostSecrets(store, 'mac', { pairingKey: 'k1' });
    expect(values.get(`${PAIRING_KEY_PREFIX}mac`)).toBe('k1');
    expect(values.has(`${DEVICE_TOKEN_PREFIX}mac`)).toBe(false);
    expect(await readHostSecrets(store, 'mac')).toEqual({ pairingKey: 'k1' });
  });

  it('drops the pairing key the moment a token is stored', async () => {
    const { store, values } = memoryStore({ [`${PAIRING_KEY_PREFIX}mac`]: 'k1' });
    await writeHostSecrets(store, 'mac', { pairingKey: 'k1', deviceToken: 't1' });
    expect(values.has(`${PAIRING_KEY_PREFIX}mac`)).toBe(false);
    expect(values.get(`${DEVICE_TOKEN_PREFIX}mac`)).toBe('t1');
  });

  it('migrates an already paired host on its next connect', async () => {
    const { store } = memoryStore({ [`${PAIRING_KEY_PREFIX}mac`]: 'k1' });
    expect(await readHostSecrets(store, 'mac')).toEqual({ pairingKey: 'k1' });
    await adoptDeviceToken(store, 'mac', 't1');
    expect(await readHostSecrets(store, 'mac')).toEqual({ deviceToken: 't1' });
  });

  it('forgets both secrets when the host is removed', async () => {
    const { store, values } = memoryStore({
      [`${PAIRING_KEY_PREFIX}mac`]: 'k1',
      [`${DEVICE_TOKEN_PREFIX}mac`]: 't1',
    });
    await forgetHost(store, 'mac');
    expect(values.size).toBe(0);
    expect(await readHostSecrets(store, 'mac')).toEqual({});
  });

  it('knows whether a host can still be reached', () => {
    expect(canAuthenticate({ pairingKey: 'k1' })).toBe(true);
    expect(canAuthenticate({ deviceToken: 't1' })).toBe(true);
    expect(canAuthenticate({})).toBe(false);
  });

});

describe('host-specific clientId', () => {
  it('has none until a host asks for one', async () => {
    const { store } = memoryStore();
    expect(await readHostClientId(store, 'mac')).toBeUndefined();
  });

  it('mints and stores one per host, replacing an earlier override', async () => {
    const { store, values } = memoryStore();
    expect(await mintHostClientId(store, 'mac', () => 'id-1')).toBe('id-1');
    expect(values.get(`${HOST_CLIENT_ID_PREFIX}mac`)).toBe('id-1');
    expect(await readHostClientId(store, 'mac')).toBe('id-1');

    expect(await mintHostClientId(store, 'mac', () => 'id-2')).toBe('id-2');
    expect(await readHostClientId(store, 'mac')).toBe('id-2');
    // Another host keeps the shared id; only the colliding one has its own.
    expect(await readHostClientId(store, 'other')).toBeUndefined();
  });

  it('is a real 16-byte identifier when nothing is injected', async () => {
    const { store } = memoryStore();
    const minted = await mintHostClientId(store, 'mac');
    expect(fromBase64(minted)).toHaveLength(CLIENT_ID_BYTES);
    expect(minted).toMatch(/^[A-Za-z0-9_-]{22}$/);
  });

  it('survives dropping the secrets and goes only when the host does', async () => {
    const { store, values } = memoryStore({
      [`${PAIRING_KEY_PREFIX}mac`]: 'k1',
      [`${DEVICE_TOKEN_PREFIX}mac`]: 't1',
      [`${HOST_CLIENT_ID_PREFIX}mac`]: 'id-1',
    });
    // A refused secret is dropped; the id stays, so re-pairing shows the same device.
    await forgetHostSecrets(store, 'mac');
    expect(await readHostSecrets(store, 'mac')).toEqual({});
    expect(await readHostClientId(store, 'mac')).toBe('id-1');

    await forgetHost(store, 'mac');
    expect(values.size).toBe(0);
  });
});
