// iOS 27 stops an app at launch unless it adopts the scene life cycle, and Expo's prebuild
// template (57.0.34 included) still starts React Native from the app delegate into a window it
// makes itself. Expo ships the scene delegate (`EXExpoAppSceneDelegate`); this plugin wires it in:
// the Info.plist names it, the app delegate keeps creating the factory but no longer the window,
// and the scene delegate creates the window and starts React Native into it.
//
// The `factory.startReactNative(` text stays in the app delegate as a comment because
// @react-native-firebase/app anchors `FirebaseApp.configure()` on it, and this plugin can run
// before or after that one.
//
// The Podfile also opts react-native-firebase out of SPM: with the static frameworks this app
// builds (`expo-build-properties` `ios.useFrameworks: "static"`) each RNFB pod would embed its own
// Firebase copy, and `pod install` refuses that combination.
const { withAppDelegate, withInfoPlist, withPodfile } = require('expo/config-plugins');

const SCENE_DELEGATE = 'EXExpoAppSceneDelegate';
const PODFILE_LINE = '$RNFirebaseDisableSPM = true';

function withSceneManifest(config) {
  return withInfoPlist(config, (next) => {
    next.modResults.UIApplicationSceneManifest = {
      UIApplicationSupportsMultipleScenes: false,
      UISceneConfigurations: {
        UIWindowSceneSessionRoleApplication: [
          { UISceneConfigurationName: 'Default Configuration', UISceneDelegateClassName: SCENE_DELEGATE },
        ],
      },
    };
    return next;
  });
}

function withSceneAppDelegate(config) {
  return withAppDelegate(config, (next) => {
    if (next.modResults.language !== 'swift') {
      throw new Error('with-ios-scene-lifecycle: expected a Swift AppDelegate.');
    }
    let src = next.modResults.contents;
    if (!src.includes('ExpoReactNativeFactoryProvider')) {
      src = src.replace(
        /class AppDelegate: ExpoAppDelegate \{/,
        'class AppDelegate: ExpoAppDelegate, ExpoReactNativeFactoryProvider {',
      );
    }
    src = src.replace(/^[ \t]*window = UIWindow\(frame: UIScreen\.main\.bounds\)\n/m, '');
    src = src.replace(
      /^([ \t]*)factory\.startReactNative\(\s*withModuleName:[^)]*\)\n/m,
      '$1// EXExpoAppSceneDelegate creates the window and calls factory.startReactNative( into it.\n',
    );
    if (!src.includes('ExpoReactNativeFactoryProvider') || /window = UIWindow\(frame:/.test(src)
      || /^\s*factory\.startReactNative\(/m.test(src)) {
      throw new Error('with-ios-scene-lifecycle: the AppDelegate template changed; update this plugin.');
    }
    next.modResults.contents = src;
    return next;
  });
}

function withFirebaseWithoutSpm(config) {
  return withPodfile(config, (next) => {
    if (!next.modResults.contents.includes(PODFILE_LINE)) {
      next.modResults.contents = `${PODFILE_LINE}\n${next.modResults.contents}`;
    }
    return next;
  });
}

module.exports = function withIosSceneLifecycle(config) {
  return withFirebaseWithoutSpm(withSceneAppDelegate(withSceneManifest(config)));
};
