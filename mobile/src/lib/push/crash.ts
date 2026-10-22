import {
  deleteUnsentReports,
  getCrashlytics,
  recordError,
  setCrashlyticsCollectionEnabled,
} from '@react-native-firebase/crashlytics';

import { defaultErrorHandler } from '@/lib/push/default-error-handler';
import { pushSettings } from '@/lib/push/settings';

const NAME = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;
const FRAME = /^at [^()\n]{1,120} \([^()\n]{1,200}:\d+:\d+\)$/;

/**
 * What a crash report may say: the error's type and where it happened. The message is
 * dropped because it can quote a conversation, a command or a path (docs/mobile-remote.md
 * "Crashlytics"); a stack line that is not a plain frame is dropped for the same reason.
 */
export function scrubError(error: unknown): Error {
  const raw = error instanceof Error ? error : null;
  const name = raw !== null && NAME.test(raw.name) ? raw.name : 'Error';
  const frames = (raw?.stack ?? '')
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => FRAME.test(line))
    .slice(0, 50);
  const scrubbed = new Error(name);
  scrubbed.name = name;
  scrubbed.stack = [name, ...frames.map((frame) => `    ${frame}`)].join('\n');
  return scrubbed;
}

/**
 * Collection follows the setting (firebase.json keeps it off until the setting is read); turning
 * it off also deletes reports not yet sent. JS errors go out scrubbed, then to React Native's
 * handler — scrubbed too outside development, because a release build's fatal JS error becomes
 * a native crash that carries the message it was handed.
 */
export function installCrashReporting(): void {
  let crashlytics: ReturnType<typeof getCrashlytics>;
  try {
    crashlytics = getCrashlytics();
  } catch {
    return; // A build without Firebase.
  }
  let enabled = false;
  const apply = (): void => {
    const next = pushSettings.get().crashReports;
    enabled = next;
    void setCrashlyticsCollectionEnabled(crashlytics, next)
      .then(() => (next ? undefined : deleteUnsentReports(crashlytics)))
      .catch(() => undefined);
  };
  void pushSettings.ready.then(apply);
  pushSettings.subscribe(apply);
  ErrorUtils.setGlobalHandler((error, isFatal) => {
    const scrubbed = scrubError(error);
    if (enabled) {
      try {
        recordError(crashlytics, scrubbed);
      } catch {
        // The report is best effort; the error still goes on to React Native.
      }
    }
    defaultErrorHandler(__DEV__ ? error : scrubbed, isFatal);
  });
}
