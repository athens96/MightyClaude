import { requireOptionalNativeModule } from 'expo';

interface MightyDeviceLockModule {
  isDeviceLocked(): boolean;
}

/** Locked unless the native module says otherwise: a missing module must never let a button act. */
export function isDeviceLocked(): boolean {
  const native = requireOptionalNativeModule<MightyDeviceLockModule>('MightyDeviceLock');
  if (!native) return true;
  try {
    return native.isDeviceLocked();
  } catch {
    return true;
  }
}
