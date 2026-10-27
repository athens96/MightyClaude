// The Expo config is app.json with the release version from ../VERSION (app-version.js), plus
// what a public repository must not hold.
// GOOGLE_SERVICES_JSON (Android) and GOOGLE_SERVICE_INFO_PLIST (iOS) name the Firebase configs kept
// outside git (the release tooling sets them, docs/mobile-remote.md "푸시 알림"). Without them the config
// is app.json (versioned), which keeps jest, typecheck and `expo config` working; an EAS build without
// its platform's file stops instead of shipping an app whose push and crash reports silently do nothing.
const fs = require('node:fs');
const { releaseVersion, withReleaseVersion } = require('./app-version');

module.exports = ({ config: base }) => {
  // A release build must carry the release's version; jest and typecheck keep app.json's when VERSION is absent.
  const version = releaseVersion();
  if (!version && process.env.EAS_BUILD) throw new Error('VERSION is missing or malformed: the phone app takes its version from it.');
  const config = withReleaseVersion(base, version);
  const androidFile = process.env.GOOGLE_SERVICES_JSON;
  const iosFile = process.env.GOOGLE_SERVICE_INFO_PLIST;
  if (process.env.EAS_BUILD) {
    const platform = process.env.EAS_BUILD_PLATFORM;
    if (platform !== 'ios' && !androidFile) throw new Error('GOOGLE_SERVICES_JSON is not set: point it at google-services.json before building.');
    if (platform === 'ios' && !iosFile) throw new Error('GOOGLE_SERVICE_INFO_PLIST is not set: point it at GoogleService-Info.plist before building.');
  }
  if (!androidFile && !iosFile) return config;
  for (const [name, file] of [['GOOGLE_SERVICES_JSON', androidFile], ['GOOGLE_SERVICE_INFO_PLIST', iosFile]]) {
    if (file && !fs.existsSync(file)) throw new Error(`${name} points at a missing file: ${file}`);
  }
  return {
    ...config,
    android: androidFile ? { ...config.android, googleServicesFile: androidFile } : config.android,
    ios: iosFile ? { ...config.ios, googleServicesFile: iosFile } : config.ios,
    // iOS links static frameworks (app.json), which React Native Firebase supports only through CocoaPods, not SPM.
    plugins: [...config.plugins, ['@react-native-firebase/app', { ios: { disableSPM: true } }], '@react-native-firebase/crashlytics'],
  };
};
