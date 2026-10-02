import { Platform } from 'react-native';
import { requireOptionalNativeModule } from 'expo';
import type { ControlKeyNative } from '@/lib/screen-share/control-key';

/**
 * The local Expo module in `modules/control-key` (Android only). It keeps the control key
 * in the Android Keystore and hands JavaScript nothing but the public key and finished
 * signatures. iOS has no such module — the feature is hidden there — so this returns
 * `undefined` and every caller reads that as "unsupported".
 */

interface MightyControlKeyModule {
  isSupported(): boolean;
  publicKey(alias: string): Promise<string | null>;
  generate(alias: string): Promise<string>;
  remove(alias: string): Promise<void>;
  sign(alias: string, challengeB64: string, title: string, cancel: string): Promise<string>;
}

let cached: ControlKeyNative | undefined | null = null;

export function nativeControlKey(): ControlKeyNative | undefined {
  if (cached !== null) return cached;
  const module =
    Platform.OS === 'android'
      ? requireOptionalNativeModule<MightyControlKeyModule>('MightyControlKey')
      : null;
  cached = module
    ? {
        isSupported: () => module.isSupported(),
        publicKey: (alias) => module.publicKey(alias),
        generate: (alias) => module.generate(alias),
        remove: (alias) => module.remove(alias),
        sign: (alias, challengeB64, prompt) =>
          module.sign(alias, challengeB64, prompt.title, prompt.cancel),
      }
    : undefined;
  return cached;
}
