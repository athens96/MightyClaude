import {
  buildBaseUrl,
  formatPairingUrl,
  pairingFingerprint,
  parsePairingUrl,
} from '@/lib/pairing';

describe('parsePairingUrl', () => {
  it('parses a full pairing url and percent-decodes the name', () => {
    const result = parsePairingUrl(
      'mightyclaude://pair?v=1&host=100.64.1.2&port=43138&key=abc123&name=%EC%9C%A0%EC%A7%84%20%EB%A7%A5%EB%B6%81',
    );
    expect(result).toEqual({
      ok: true,
      value: { host: '100.64.1.2', port: 43138, key: 'abc123', name: '유진 맥북' },
    });
  });

  it('defaults the port and falls back to the host as the name', () => {
    const result = parsePairingUrl('mightyclaude://pair?v=1&host=100.64.1.2&key=k');
    expect(result).toEqual({
      ok: true,
      value: { host: '100.64.1.2', port: 43138, key: 'k', name: '100.64.1.2' },
    });
  });

  it('accepts a missing version but rejects an unknown one', () => {
    expect(parsePairingUrl('mightyclaude://pair?host=h&key=k').ok).toBe(true);
    expect(parsePairingUrl('mightyclaude://pair?v=2&host=h&key=k')).toEqual({
      ok: false,
      error: '지원하지 않는 페어링 버전입니다 (v=2).',
    });
  });

  it('rejects non-pairing input', () => {
    expect(parsePairingUrl('https://example.com?host=h&key=k').ok).toBe(false);
    expect(parsePairingUrl('mightyclaude://pair').ok).toBe(false);
    expect(parsePairingUrl('').ok).toBe(false);
  });

  it('rejects a missing host or key', () => {
    expect(parsePairingUrl('mightyclaude://pair?v=1&key=k').ok).toBe(false);
    expect(parsePairingUrl('mightyclaude://pair?v=1&host=h').ok).toBe(false);
  });

  it('rejects out-of-range and non-numeric ports', () => {
    expect(parsePairingUrl('mightyclaude://pair?host=h&key=k&port=0').ok).toBe(false);
    expect(parsePairingUrl('mightyclaude://pair?host=h&key=k&port=70000').ok).toBe(false);
    expect(parsePairingUrl('mightyclaude://pair?host=h&key=k&port=abc').ok).toBe(false);
  });

  it('round-trips through formatPairingUrl', () => {
    const payload = { host: '100.64.1.2', port: 9000, key: 'k e y', name: '내 맥북' };
    const parsed = parsePairingUrl(formatPairingUrl(payload));
    expect(parsed).toEqual({ ok: true, value: payload });
  });
});

describe('buildBaseUrl', () => {
  it('builds an http origin', () => {
    expect(buildBaseUrl('100.64.1.2', 43138)).toBe('http://100.64.1.2:43138');
  });

  it('brackets bare IPv6 literals', () => {
    expect(buildBaseUrl('fd7a::1', 43138)).toBe('http://[fd7a::1]:43138');
    expect(buildBaseUrl('[fd7a::1]', 43138)).toBe('http://[fd7a::1]:43138');
  });
});

describe('pairingFingerprint', () => {
  it('is case-insensitive on the host', () => {
    expect(pairingFingerprint('MyHost.local', 1)).toBe(pairingFingerprint('myhost.local', 1));
  });
});
