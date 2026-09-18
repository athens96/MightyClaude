import { toBase64 } from '@/api/relay/crypto';
import {
  LEGACY_PAIRING_ERROR,
  describeRelayTarget,
  formatPairingUrl,
  normalizeRelayUrl,
  pairingFingerprint,
  parsePairingUrl,
  type PairingPayload,
} from '@/lib/pairing';

const publicKey = toBase64(Uint8Array.from({ length: 32 }, (_, index) => index));
const publicKeyUrl = publicKey.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

const payload: PairingPayload = {
  serverId: 'mac-studio.local',
  relayUrl: 'wss://relay.example.com:8787',
  hostPublicKeyB64: publicKey,
  pairingKey: 'pairing-key-32bytes',
  name: '유진 맥북',
};

function url(overrides: Record<string, string> = {}): string {
  const params = new URLSearchParams({
    v: '2',
    sid: payload.serverId,
    pk: publicKeyUrl,
    relay: payload.relayUrl,
    key: payload.pairingKey,
    name: payload.name,
    ...overrides,
  });
  return `mightyclaude://pair?${params.toString()}`;
}

describe('parsePairingUrl (v2)', () => {
  it('parses every field and normalizes the key to standard base64', () => {
    expect(parsePairingUrl(url())).toEqual({ ok: true, value: payload });
  });

  it('falls back to the serverId as the name', () => {
    const result = parsePairingUrl(url({ name: '' }));
    expect(result).toMatchObject({ ok: true, value: { name: payload.serverId } });
  });

  it('accepts a ws:// relay and strips a trailing slash', () => {
    expect(parsePairingUrl(url({ relay: 'ws://192.168.0.5:8787/' }))).toMatchObject({
      ok: true,
      value: { relayUrl: 'ws://192.168.0.5:8787' },
    });
  });

  it('round-trips through formatPairingUrl', () => {
    expect(parsePairingUrl(formatPairingUrl(payload))).toEqual({ ok: true, value: payload });
  });

  it('emits a url-safe, unpadded public key', () => {
    expect(formatPairingUrl(payload)).toContain(`pk=${encodeURIComponent(publicKeyUrl)}`);
  });
});

describe('parsePairingUrl (rejections)', () => {
  it('rejects the v1 Tailscale pairing string with an upgrade hint', () => {
    expect(
      parsePairingUrl('mightyclaude://pair?v=1&host=100.64.1.2&port=43138&key=abc&name=Mac'),
    ).toEqual({ ok: false, error: LEGACY_PAIRING_ERROR });
  });

  it('rejects a versionless v1 string (host/port form)', () => {
    expect(parsePairingUrl('mightyclaude://pair?host=100.64.1.2&key=abc')).toEqual({
      ok: false,
      error: LEGACY_PAIRING_ERROR,
    });
  });

  it('rejects an unknown version', () => {
    expect(parsePairingUrl(url({ v: '3' }))).toEqual({
      ok: false,
      error: '지원하지 않는 페어링 버전입니다 (v=3).',
    });
  });

  it('rejects non-pairing input', () => {
    expect(parsePairingUrl('https://example.com?sid=a').ok).toBe(false);
    expect(parsePairingUrl('mightyclaude://pair').ok).toBe(false);
    expect(parsePairingUrl('').ok).toBe(false);
  });

  it('rejects missing or malformed fields', () => {
    expect(parsePairingUrl(url({ sid: '' })).ok).toBe(false);
    expect(parsePairingUrl(url({ sid: '-bad id' })).ok).toBe(false);
    expect(parsePairingUrl(url({ relay: '' })).ok).toBe(false);
    expect(parsePairingUrl(url({ relay: 'http://relay.example.com' })).ok).toBe(false);
    expect(parsePairingUrl(url({ key: '' })).ok).toBe(false);
  });

  it('rejects a public key that is not 32 bytes', () => {
    expect(parsePairingUrl(url({ pk: toBase64(new Uint8Array(31)) }))).toMatchObject({
      ok: false,
      error: expect.stringContaining('31B'),
    });
    expect(parsePairingUrl(url({ pk: '!!!' })).ok).toBe(false);
  });
});

describe('normalizeRelayUrl', () => {
  it('defaults a bare host to wss and lowercases the scheme', () => {
    expect(normalizeRelayUrl('relay.example.com:8787')).toBe('wss://relay.example.com:8787');
    expect(normalizeRelayUrl('WSS://relay.example.com')).toBe('wss://relay.example.com');
  });

  it('rejects paths and non-websocket schemes', () => {
    expect(normalizeRelayUrl('wss://relay.example.com/ws')).toBeUndefined();
    expect(normalizeRelayUrl('http://relay.example.com')).toBeUndefined();
    expect(normalizeRelayUrl('   ')).toBeUndefined();
  });
});

describe('identity helpers', () => {
  it('uses the serverId as the fingerprint', () => {
    expect(pairingFingerprint('mac-studio.local')).toBe('mac-studio.local');
  });

  it('describes the relay target without the scheme', () => {
    expect(describeRelayTarget('wss://relay.example.com:8787', 'sid-1')).toBe(
      'relay.example.com:8787 · sid-1',
    );
  });
});
