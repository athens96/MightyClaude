// The Expo config is app.json plus what a public repository must not hold.
// GOOGLE_SERVICES_JSON names the Firebase Android config kept outside git (the release tooling
// sets it, docs/mobile-remote.md "푸시 알림"). Without it the config is app.json alone, which keeps
// jest, typecheck and `expo config` working; an EAS build without it stops instead of shipping an
// app whose push and crash reports silently do nothing.
const fs = require('node:fs');

module.exports = ({ config }) => {
  const file = process.env.GOOGLE_SERVICES_JSON;
  if (!file) {
    if (process.env.EAS_BUILD) throw new Error('GOOGLE_SERVICES_JSON is not set: point it at google-services.json before building.');
    return config;
  }
  if (!fs.existsSync(file)) throw new Error(`GOOGLE_SERVICES_JSON points at a missing file: ${file}`);
  return {
    ...config,
    android: { ...config.android, googleServicesFile: file },
    plugins: [...config.plugins, '@react-native-firebase/app', '@react-native-firebase/crashlytics'],
  };
};
