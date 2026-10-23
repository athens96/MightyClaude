import { requireOptionalNativeModule } from 'expo';

interface MightyDeviceLockModule {
  isDeviceLocked(): boolean;
}

/** Locked unless the native module says otherwise: a missing module must never let a button act. */
export function isDeviceLocked(): boolean {
  const native = requireOptionalNativeModule<MightyDeviceLockModule>('MightyDeviceLock');
  if (!native) return true;
  try {
    // Only a plain `false` unlocks: an undefined or odd answer from the bridge counts as locked.
    return native.isDeviceLocked() !== false;
  } catch {
    return true;
  }
}
