// The phone app carries the release's version (the repository's VERSION file, as the Mac and
// Windows apps do), so the build a phone runs can be told apart from the screen.
const fs = require('node:fs');
const path = require('node:path');

const VERSION_FILE = path.join(__dirname, '..', 'VERSION');
const PATTERN = /^(\d+)\.(\d+)\.(\d+)$/;

/** The release version in VERSION ("0.4.43"), or null when the file is missing or malformed. */
function releaseVersion(file = VERSION_FILE) {
  if (!fs.existsSync(file)) return null;
  const value = fs.readFileSync(file, 'utf8').trim();
  return PATTERN.test(value) ? value : null;
}

/** Android's versionCode for a release: always growing with the version (0.4.43 → 4043). */
function versionCode(version) {
  const match = PATTERN.exec(version);
  if (!match) throw new Error(`not a release version: ${version}`);
  const [major, minor, patch] = match.slice(1).map(Number);
  if (minor > 999 || patch > 999) throw new Error(`version part over 999: ${version}`);
  return major * 1_000_000 + minor * 1_000 + patch;
}

/** The Expo config with the release version, Android versionCode and iOS build number applied. */
function withReleaseVersion(config, version) {
  if (!version) return config;
  const code = versionCode(version);
  return {
    ...config,
    version,
    android: { ...config.android, versionCode: code },
    ios: { ...config.ios, buildNumber: String(code) },
  };
}

module.exports = { releaseVersion, versionCode, withReleaseVersion, VERSION_FILE };
