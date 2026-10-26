// The phone app takes the release's version from the repository's VERSION file (app-version.js).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const { releaseVersion, versionCode, withReleaseVersion, VERSION_FILE } = require('../../app-version.js') as {
  releaseVersion: (file?: string) => string | null;
  versionCode: (version: string) => number;
  withReleaseVersion: <T extends object>(config: T, version: string | null) => T & { version?: string; android?: object; ios?: object; name?: string };
  VERSION_FILE: string;
};

describe('app version', () => {
  it('reads the repository VERSION', () => {
    const expected = fs.readFileSync(VERSION_FILE, 'utf8').trim();
    expect(releaseVersion()).toBe(expected);
    expect(expected).toMatch(/^\d+\.\d+\.\d+$/);
  });

  it('gives null for a missing or malformed file', () => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'app-version-'));
    const file = path.join(folder, 'VERSION');
    expect(releaseVersion(file)).toBeNull();
    fs.writeFileSync(file, 'v0.4\n');
    expect(releaseVersion(file)).toBeNull();
    fs.writeFileSync(file, '0.4.43\n');
    expect(releaseVersion(file)).toBe('0.4.43');
    fs.rmSync(folder, { recursive: true, force: true });
  });

  it('turns a version into an always-growing versionCode above the old 2', () => {
    expect(versionCode('0.4.43')).toBe(4043);
    expect(versionCode('0.4.100')).toBeGreaterThan(versionCode('0.4.99'));
    expect(versionCode('0.5.0')).toBeGreaterThan(versionCode('0.4.999'));
    expect(versionCode('1.0.0')).toBeGreaterThan(versionCode('0.999.999'));
    expect(versionCode('0.4.43')).toBeGreaterThan(2);
    expect(() => versionCode('0.4')).toThrow();
    expect(() => versionCode('0.1000.0')).toThrow();
  });

  it('applies the version, versionCode and build number, keeping the rest', () => {
    const base = { name: 'Mighty Claude', version: '1.1.0', android: { package: 'x.y', versionCode: 2 }, ios: { buildNumber: '2', supportsTablet: true } };
    const config = withReleaseVersion(base, '0.4.43');
    expect(config.version).toBe('0.4.43');
    expect(config.android).toEqual({ package: 'x.y', versionCode: 4043 });
    expect(config.ios).toEqual({ buildNumber: '4043', supportsTablet: true });
    expect(config.name).toBe('Mighty Claude');
    expect(withReleaseVersion(base, null)).toBe(base);
  });
});
