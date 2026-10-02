import { CAPABILITIES, type Capability } from '@/api/types';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';
import {
  grantedModes,
  mayStartScreenShare,
  screenShareAvailable,
  screenShareRefusal,
  screenShareVisibility,
} from '@/lib/screen-share/availability';

/**
 * Who sees 화면 보기 at all, and who may ask for what. The gate is in two halves: the
 * entry point exists only on an Android phone talking to a Mac that advertises
 * `screenShare`, and inside the screen the Mac's allow-list and grant decide. Both halves
 * are mirrors of the Mac's own rules — the Mac still refuses anything it does not like.
 */

const ANDROID = 'android';
const WITH: Capability[] = ['queue', 'pane', 'screenShare'];
const WITHOUT: Capability[] = ['queue', 'pane', 'files'];

describe('the capability itself', () => {
  it('is part of the contract vocabulary, so the phone keeps it from /m1/info', () => {
    expect(CAPABILITIES).toContain('screenShare');
    expect(parseCapabilities({ capabilities: ['queue', 'screenShare'] })).toEqual([
      'queue',
      'screenShare',
    ]);
    expect(hasCapability(parseCapabilities({ capabilities: ['screenShare'] }), 'screenShare')).toBe(
      true,
    );
  });

  it('is dropped from a host that sends a name this build does not know', () => {
    expect(parseCapabilities({ capabilities: ['screenshare', 'screen-share'] })).toEqual([]);
  });
});

describe('the entry point is hidden', () => {
  it('on iOS, where the beta does not go — so the iOS build has nothing to break', () => {
    const visibility = screenShareVisibility({ platform: 'ios', capabilities: WITH });
    expect(visibility.visible).toBe(false);
    expect(visibility.hiddenBecause).toBe('platform');
    expect(screenShareAvailable({ platform: 'ios', capabilities: WITH })).toBe(false);
  });

  it('on web, and on any platform that is not this beta’s', () => {
    expect(screenShareAvailable({ platform: 'web', capabilities: WITH })).toBe(false);
    expect(screenShareAvailable({ platform: 'windows', capabilities: WITH })).toBe(false);
  });

  it('on a Mac older than the feature, which advertises no screenShare', () => {
    const visibility = screenShareVisibility({ platform: ANDROID, capabilities: WITHOUT });
    expect(visibility.visible).toBe(false);
    expect(visibility.hiddenBecause).toBe('capability');
  });

  it('while nothing has been read from /m1/info yet', () => {
    expect(screenShareVisibility({ platform: ANDROID, capabilities: undefined }).visible).toBe(false);
    expect(screenShareVisibility({ platform: ANDROID, capabilities: [] }).visible).toBe(false);
  });
});

describe('the entry point is shown', () => {
  it('only on an Android phone talking to a Mac that advertises screenShare', () => {
    const visibility = screenShareVisibility({ platform: ANDROID, capabilities: WITH });
    expect(visibility.visible).toBe(true);
    expect(visibility.hiddenBecause).toBeUndefined();
  });
});

describe('grant gating', () => {
  it('refuses a phone that has no device id: the Mac cannot tell it apart', () => {
    expect(screenShareRefusal({ allowed: true, grant: 'control', mode: 'view' })).toBe(
      'legacy-client',
    );
    expect(
      screenShareRefusal({ allowed: true, grant: 'control', mode: 'view', clientId: '' }),
    ).toBe('legacy-client');
  });

  it('refuses a newly paired phone, which starts off the allow-list', () => {
    expect(
      screenShareRefusal({ allowed: false, grant: 'none', mode: 'view', clientId: 'c1' }),
    ).toBe('device-not-allowed');
    // Being on the allow-list is not a grant: both are needed, and both are the Mac's.
    expect(
      screenShareRefusal({ allowed: false, grant: 'control', mode: 'view', clientId: 'c1' }),
    ).toBe('device-not-allowed');
  });

  it('refuses viewing with no grant at all', () => {
    expect(screenShareRefusal({ allowed: true, grant: 'none', mode: 'view', clientId: 'c1' })).toBe(
      'insufficient-grant',
    );
  });

  it('refuses control on a view-only grant, and allows viewing on it', () => {
    expect(
      screenShareRefusal({ allowed: true, grant: 'view', mode: 'control', clientId: 'c1' }),
    ).toBe('insufficient-grant');
    expect(
      screenShareRefusal({ allowed: true, grant: 'view', mode: 'view', clientId: 'c1' }),
    ).toBeUndefined();
  });

  it('allows both modes on a control grant', () => {
    expect(mayStartScreenShare({ allowed: true, grant: 'control', mode: 'view', clientId: 'c1' })).toBe(true);
    expect(
      mayStartScreenShare({ allowed: true, grant: 'control', mode: 'control', clientId: 'c1' }),
    ).toBe(true);
  });

  it('offers exactly the modes the grant covers', () => {
    expect(grantedModes('none')).toEqual([]);
    expect(grantedModes('view')).toEqual(['view']);
    expect(grantedModes('control')).toEqual(['control', 'view']);
  });
});
