// react-native-webrtc declares a MediaProjectionService (foregroundServiceType
// "mediaProjection") for screen *capture*. This app only ever receives a Mac's screen, so
// the service is removed from the merged manifest rather than shipped unused.
const { AndroidConfig, withAndroidManifest } = require('expo/config-plugins');

const SERVICE = 'com.oney.WebRTCModule.MediaProjectionService';
const TOOLS = 'http://schemas.android.com/tools';

module.exports = function withNoMediaProjection(config) {
  return withAndroidManifest(config, (next) => {
    const manifest = next.modResults.manifest;
    manifest.$ = { ...manifest.$, 'xmlns:tools': TOOLS };
    const application = AndroidConfig.Manifest.getMainApplicationOrThrow(next.modResults);
    const services = (application.service ?? []).filter(
      (service) => service.$?.['android:name'] !== SERVICE,
    );
    services.push({ $: { 'android:name': SERVICE, 'tools:node': 'remove' } });
    application.service = services;
    return next;
  });
};
