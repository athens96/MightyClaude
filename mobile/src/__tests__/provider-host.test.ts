import { hostRunsAntigravity, parseAppVersion } from '@/lib/provider-host';

describe('which CLI a host runs for gemini panes', () => {
  it('reads an app version', () => {
    expect(parseAppVersion('0.4.23')).toEqual([0, 4, 23]);
    expect(parseAppVersion('0.4.28 (801)')).toEqual([0, 4, 28]);
    expect(parseAppVersion('v1.2')).toEqual([1, 2, 0]);
    expect(parseAppVersion('')).toBeUndefined();
    expect(parseAppVersion('dev')).toBeUndefined();
    expect(parseAppVersion(undefined)).toBeUndefined();
  });

  it('runs Antigravity on a Mac from 0.4.23', () => {
    expect(hostRunsAntigravity({ platform: 'darwin', appVersion: '0.4.22' })).toBe(false);
    expect(hostRunsAntigravity({ platform: 'darwin', appVersion: '0.4.23' })).toBe(true);
    expect(hostRunsAntigravity({ platform: 'darwin', appVersion: '0.5.0' })).toBe(true);
    expect(hostRunsAntigravity({ platform: 'darwin', appVersion: '0.3.99' })).toBe(false);
  });

  it('runs Antigravity on Windows from 0.4.28', () => {
    expect(hostRunsAntigravity({ platform: 'win32', appVersion: '0.4.27' })).toBe(false);
    expect(hostRunsAntigravity({ platform: 'win32', appVersion: '0.4.28' })).toBe(true);
    expect(hostRunsAntigravity({ platform: 'win32', appVersion: '1.0.0' })).toBe(true);
  });

  it('takes a host it cannot place to be current', () => {
    expect(hostRunsAntigravity(undefined)).toBe(true);
    expect(hostRunsAntigravity({})).toBe(true);
    expect(hostRunsAntigravity({ platform: 'darwin', appVersion: 'dev' })).toBe(true);
    expect(hostRunsAntigravity({ platform: 'win32' })).toBe(true);
    expect(hostRunsAntigravity({ platform: 'linux', appVersion: '0.1.0' })).toBe(true);
  });
});
