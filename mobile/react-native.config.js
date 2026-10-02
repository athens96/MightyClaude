// The remote screen is Android-only in this beta. Its native libraries are kept out of the
// iOS build entirely, so a missing pod or an iOS-incompatible release can never break it:
// react-native-webrtc, react-native-zstd, and react-native-nitro-modules (which only
// react-native-zstd uses). The JavaScript side loads them lazily and never on iOS.
module.exports = {
  dependencies: {
    'react-native-webrtc': { platforms: { ios: null } },
    'react-native-zstd': { platforms: { ios: null } },
    'react-native-nitro-modules': { platforms: { ios: null } },
  },
};
