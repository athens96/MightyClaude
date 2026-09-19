import { CAPABILITIES, type HostInfo } from '@/api/types';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';

function info(capabilities: unknown): HostInfo {
  return {
    protocol: 1,
    hostId: 'h1',
    hostName: 'mac',
    appVersion: '1.0.0',
    platform: 'macOS',
    ...(capabilities === undefined ? {} : { capabilities: capabilities as string[] }),
  };
}

describe('parseCapabilities', () => {
  it('reads the names the contract lists', () => {
    expect(parseCapabilities(info(['queue', 'pane', 'history']))).toEqual([
      'queue',
      'pane',
      'history',
    ]);
    expect(parseCapabilities(info([...CAPABILITIES]))).toHaveLength(CAPABILITIES.length);
  });

  it('treats a host older than the extension as having none', () => {
    expect(parseCapabilities(info(undefined))).toEqual([]);
    expect(parseCapabilities(undefined)).toEqual([]);
  });

  it('knows "style", so the guided panel turns on only where the host has it', () => {
    expect(parseCapabilities(info(['mighty', 'style']))).toEqual(['mighty', 'style']);
    const older = parseCapabilities(info(['mighty']));
    expect(hasCapability(older, 'mighty')).toBe(true);
    expect(hasCapability(older, 'style')).toBe(false);
  });

  it('drops anything that is not a name we know', () => {
    expect(parseCapabilities(info(['queue', 'teleport', '', 7, null, { name: 'pane' }]))).toEqual([
      'queue',
    ]);
  });

  it('ignores a capabilities field that is not an array', () => {
    expect(parseCapabilities(info('queue,pane'))).toEqual([]);
    expect(parseCapabilities(info({ queue: true }))).toEqual([]);
  });

  it('keeps one entry per name', () => {
    expect(parseCapabilities(info(['queue', 'queue', 'pane']))).toEqual(['queue', 'pane']);
  });
});

describe('hasCapability', () => {
  it('is true only for a name the host actually advertised', () => {
    const capabilities = parseCapabilities(info(['queue', 'settings']));
    expect(hasCapability(capabilities, 'queue')).toBe(true);
    expect(hasCapability(capabilities, 'settings')).toBe(true);
    expect(hasCapability(capabilities, 'pane')).toBe(false);
  });

  it('is false while nothing has been fetched yet', () => {
    expect(hasCapability(undefined, 'queue')).toBe(false);
    expect(hasCapability([], 'queue')).toBe(false);
  });
});
