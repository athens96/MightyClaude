import {
  deleteUnsentReports,
  getCrashlytics,
  recordError,
  setCrashlyticsCollectionEnabled,
} from '@react-native-firebase/crashlytics';
import rejectionTracking from 'promise/setimmediate/rejection-tracking';

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
 * it off also deletes reports not yet sent.
 *
 * A fatal JS error goes to React Native's handler scrubbed (outside development): a release build
 * turns it into a native crash carrying the message it was handed, and that crash is the report.
 * It is not also recorded here, because `recordError` finishes asynchronously and the process is
 * gone before it would. A non-fatal JS error and an unhandled rejection are recorded scrubbed.
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
  const record = (error: unknown): void => {
    if (!enabled) return;
    try {
      recordError(crashlytics, scrubError(error));
    } catch {
      // The report is best effort.
    }
  };
  void pushSettings.ready.then(apply);
  pushSettings.subscribe(apply);
  // Crashlytics' own tracker, set up by getCrashlytics() above, sends a rejection's raw message;
  // enabling this one turns that one off (the tracker holds one set of options).
  rejectionTracking.enable({ allRejections: true, onUnhandled: (_id, error) => record(error) });
  ErrorUtils.setGlobalHandler((error, isFatal) => {
    if (!isFatal) record(error);
    defaultErrorHandler(__DEV__ ? error : scrubError(error), isFatal);
  });
}
