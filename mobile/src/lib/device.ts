import * as Device from 'expo-device';

/** Name shown on the desktop for this phone; `expo-device` is best-effort. */
export function clientDeviceName(): string {
  const model = Device.modelName?.trim();
  if (model) return model;
  const name = Device.deviceName?.trim();
  if (name) return name;
  return 'Phone';
}
