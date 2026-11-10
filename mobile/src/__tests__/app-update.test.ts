import type { MobileAppUpdate } from '@/api/types';
import { CAPABILITIES } from '@/api/types';
import {
  appUpdateBanner,
  appUpdateBody,
  appUpdateConfirm,
  canCheckAppUpdate,
  parseAppUpdate,
} from '@/lib/app-update';

const base: MobileAppUpdate = { phase: 'idle', current: '1.0.0', running: 0 };

describe('parseAppUpdate', () => {
  it('reads a state and clamps progress', () => {
    expect(parseAppUpdate({ phase: 'downloading', current: '1', progress: 3, running: 2 })).toMatchObject({
      phase: 'downloading',
      progress: 1,
      running: 2,
    });
  });
  it('treats an unknown phase or junk as absent', () => {
    expect(parseAppUpdate({ phase: 'weird', current: '1', running: 0 })).toBeUndefined();
    expect(parseAppUpdate('x')).toBeUndefined();
    expect(parseAppUpdate(undefined)).toBeUndefined();
  });
  it('defaults a missing running to 0', () => {
    expect(parseAppUpdate({ phase: 'idle', current: '1' })?.running).toBe(0);
  });
});

describe('appUpdateBanner', () => {
  it('shows nothing for idle, upToDate and an absent state', () => {
    expect(appUpdateBanner(undefined)).toBeUndefined();
    expect(appUpdateBanner(base)).toBeUndefined();
    expect(appUpdateBanner({ ...base, phase: 'upToDate' })).toBeUndefined();
  });
  it('offers install for available and ready', () => {
    expect(appUpdateBanner({ ...base, phase: 'available', available: '2.0' })).toEqual({
      key: 'phone.appUpdate.available',
      params: { version: '2.0', current: '1.0.0' },
      action: 'install',
    });
    expect(appUpdateBanner({ ...base, phase: 'ready', available: '2.0' })?.action).toBe('install');
  });
  it('rounds the download percent', () => {
    expect(appUpdateBanner({ ...base, phase: 'downloading', progress: 0.456 })?.params).toEqual({ percent: 46 });
  });
  it('offers a check after a failure and none while working', () => {
    expect(appUpdateBanner({ ...base, phase: 'failed', error: 'boom' })).toMatchObject({
      key: 'phone.appUpdate.failed',
      params: { error: 'boom' },
      action: 'check',
    });
    expect(appUpdateBanner({ ...base, phase: 'checking' })?.action).toBeUndefined();
    expect(appUpdateBanner({ ...base, phase: 'installing' })?.key).toBe('phone.appUpdate.installing');
  });
});

describe('appUpdateConfirm', () => {
  it('names the running requests only when some run', () => {
    const update = { ...base, phase: 'available' as const, available: '2.0' };
    expect(appUpdateConfirm(update, 'Mac')).toEqual({
      key: 'phone.appUpdate.confirmIdle',
      params: { host: 'Mac', version: '2.0' },
    });
    expect(appUpdateConfirm({ ...update, running: 3 }, 'Mac')).toEqual({
      key: 'phone.appUpdate.confirmRunning',
      params: { host: 'Mac', version: '2.0', count: 3 },
    });
  });
});

describe('request and capability', () => {
  it('sends confirm only when true', () => {
    expect(appUpdateBody('check')).toEqual({ action: 'check' });
    expect(appUpdateBody('install', true)).toEqual({ action: 'install', confirm: true });
  });
  it('knows the capability and the menu check', () => {
    expect(CAPABILITIES).toContain('appUpdate');
    expect(canCheckAppUpdate(base)).toBe(true);
    expect(canCheckAppUpdate({ ...base, phase: 'installing' })).toBe(false);
  });
});
