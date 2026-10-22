import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { utf8Decode, utf8Encode } from '@/api/relay/crypto';
import { parsePushMessage } from '@/lib/push/message';
import { openPush, pushKey } from '@/lib/push/seal';

/**
 * The same file `MobilePushSealTests.swift` and the Windows `MobilePushVerification` read:
 * the phone must open exactly what a host seals, and nothing else.
 */
interface SealFixture {
  deviceToken: string;
  clientId: string;
  handle: string;
  keyB64: string;
  plaintext: string;
  envelope: string;
  tampered: string[];
  otherHandle: string;
}

const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/push.json'), 'utf8'),
) as { seal: SealFixture };
const seal = fixture.seal;

describe('push seal (native/contracts/fixtures/push.json)', () => {
  it('derives the key the host derives from its token hash', () => {
    expect(Buffer.from(pushKey(seal.deviceToken, seal.clientId)).toString('base64')).toBe(seal.keyB64);
  });

  it('opens the host envelope and reads the message', () => {
    const opened = openPush(seal.envelope, pushKey(seal.deviceToken, seal.clientId), seal.handle);
    expect(opened).not.toBeNull();
    expect(utf8Decode(opened!)).toBe(seal.plaintext);
    const expected = JSON.parse(seal.plaintext) as Record<string, unknown>;
    expect(expected['kind']).toBe('permission');
    expect(parsePushMessage(opened!)).toEqual(expected);
  });

  it('refuses a tampered envelope, another handle, another key and an oversized one', () => {
    const key = pushKey(seal.deviceToken, seal.clientId);
    expect(seal.tampered.length).toBeGreaterThan(0);
    for (const tampered of seal.tampered) expect(openPush(tampered, key, seal.handle)).toBeNull();
    expect(openPush(seal.envelope, key, seal.otherHandle)).toBeNull();
    expect(openPush(seal.envelope, pushKey('another-token', seal.clientId), seal.handle)).toBeNull();
    expect(openPush(seal.envelope, pushKey(seal.deviceToken, 'another-client'), seal.handle)).toBeNull();
    expect(openPush('A'.repeat(3073), key, seal.handle)).toBeNull();
    expect(openPush('not base64url!', key, seal.handle)).toBeNull();
    expect(openPush(seal.envelope.replace(/-/g, '+').replace(/_/g, '/'), key, seal.handle)).toBeNull();
    expect(openPush('', key, seal.handle)).toBeNull();
    expect(openPush(seal.envelope.slice(0, 38), key, seal.handle)).toBeNull();
  });
});

describe('parsePushMessage', () => {
  const base = {
    v: 1, id: 'run:p:1:completed', kind: 'completed', hostId: 'h', hostName: 'H',
    workspace: 'w', title: 't', summary: '', sessionId: 'p', at: '2026-10-23T01:02:03Z',
  };
  const permission = { requestId: 'r', runId: 'u', buttons: 'antigravityOnce' };
  const parse = (value: unknown) => parsePushMessage(utf8Encode(JSON.stringify(value)));

  it('reads a message without buttons', () => {
    expect(parse(base)).toEqual(base);
    expect(parse({ ...base, kind: 'signin', id: 'signin:p:1' })).toEqual({ ...base, kind: 'signin', id: 'signin:p:1' });
  });

  it('reads a permission with its buttons and ignores fields it does not know', () => {
    expect(parse({ ...base, kind: 'permission', permission, extra: 'x' })).toEqual({ ...base, kind: 'permission', permission });
  });

  it('counts length in characters, as the host clips', () => {
    expect(parse({ ...base, title: '\u{1F600}'.repeat(80) })).not.toBeNull();
    expect(parse({ ...base, title: '\u{1F600}'.repeat(81) })).toBeNull();
  });

  it('drops what it cannot show safely', () => {
    expect(parse({ ...base, kind: 'unknown' })).toBeNull();
    expect(parse({ ...base, v: 2 })).toBeNull();
    expect(parse({ ...base, v: '1' })).toBeNull();
    expect(parse({ ...base, sessionId: undefined })).toBeNull();
    expect(parse({ ...base, sessionId: '' })).toBeNull();
    expect(parse({ ...base, id: '' })).toBeNull();
    expect(parse({ ...base, hostName: 7 })).toBeNull();
    expect(parse({ ...base, title: 'x'.repeat(400) })).toBeNull();
    expect(parse({ ...base, summary: 'x'.repeat(201) })).toBeNull();
    expect(parse({ ...base, kind: 'permission', permission: { ...permission, buttons: 'later' } })).toBeNull();
    expect(parse({ ...base, kind: 'permission', permission: { ...permission, runId: 3 } })).toBeNull();
    expect(parse({ ...base, kind: 'permission', permission: null })).toBeNull();
    expect(parse({ ...base, kind: 'permission' })).toBeNull();
    expect(parse({ ...base, permission })).toBeNull();
    expect(parse([base])).toBeNull();
    expect(parse(null)).toBeNull();
    expect(parsePushMessage(utf8Encode('{not json'))).toBeNull();
    expect(parsePushMessage(Uint8Array.from([0xff, 0xfe]))).toBeNull();
  });
});
