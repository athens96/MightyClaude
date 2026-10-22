import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const appJson = require('../../app.json') as { expo: { android: { permissions: string[]; blockedPermissions: string[] }; plugins: unknown[] } };
const makeConfig = require('../../app.config.js') as (input: { config: typeof appJson.expo }) => typeof appJson.expo & {
  android: { googleServicesFile?: string };
};

const saved = { ...process.env };
afterEach(() => {
  process.env = { ...saved };
});

describe('push build config', () => {
  it('asks for notifications and lets headless tasks hold a wake lock', () => {
    expect(appJson.expo.android.permissions).toContain('android.permission.POST_NOTIFICATIONS');
    expect(appJson.expo.android.blockedPermissions).not.toContain('android.permission.WAKE_LOCK');
  });

  it('leaves the config alone without a google-services file outside a build', () => {
    delete process.env['GOOGLE_SERVICES_JSON'];
    delete process.env['EAS_BUILD'];
    const config = makeConfig({ config: appJson.expo });
    expect(config.android.googleServicesFile).toBeUndefined();
    expect(config.plugins).toEqual(appJson.expo.plugins);
  });

  it('adds the file and the Firebase plugins from GOOGLE_SERVICES_JSON', () => {
    const file = join(mkdtempSync(join(tmpdir(), 'gs-')), 'google-services.json');
    writeFileSync(file, '{}');
    process.env['GOOGLE_SERVICES_JSON'] = file;
    const config = makeConfig({ config: appJson.expo });
    expect(config.android.googleServicesFile).toBe(file);
    expect(config.plugins).toEqual([...appJson.expo.plugins, '@react-native-firebase/app', '@react-native-firebase/crashlytics']);
  });

  it('stops an EAS build that has no google-services file', () => {
    delete process.env['GOOGLE_SERVICES_JSON'];
    process.env['EAS_BUILD'] = 'true';
    expect(() => makeConfig({ config: appJson.expo })).toThrow(/GOOGLE_SERVICES_JSON/);
  });

  it('stops when the variable points at nothing', () => {
    process.env['GOOGLE_SERVICES_JSON'] = join(tmpdir(), 'missing-google-services.json');
    expect(() => makeConfig({ config: appJson.expo })).toThrow(/missing file/);
  });
});
