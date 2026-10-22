jest.mock('@react-native-firebase/crashlytics', () => ({
  getCrashlytics: jest.fn(() => ({ name: 'crashlytics' })),
  recordError: jest.fn(),
  setCrashlyticsCollectionEnabled: jest.fn(async () => null),
  deleteUnsentReports: jest.fn(async () => undefined),
}));
jest.mock('@/lib/push/default-error-handler', () => ({ defaultErrorHandler: jest.fn() }));
jest.mock('@/lib/push/settings', () => {
  const listeners = new Set<() => void>();
  let crashReports = true;
  return {
    pushSettings: {
      get: () => ({ kinds: {}, crashReports }),
      ready: Promise.resolve(),
      subscribe: (listener: () => void) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
      },
    },
    setCrashReports(next: boolean) {
      crashReports = next;
      for (const listener of listeners) listener();
    },
  };
});

import { deleteUnsentReports, recordError, setCrashlyticsCollectionEnabled } from '@react-native-firebase/crashlytics';

import { installCrashReporting, scrubError } from '@/lib/push/crash';
import { defaultErrorHandler } from '@/lib/push/default-error-handler';

const { setCrashReports } = jest.requireMock('@/lib/push/settings') as { setCrashReports(next: boolean): void };

describe('scrubError', () => {
  it('keeps the error name and stack frames, never the message', () => {
    const error = new TypeError('cannot read /Users/someone/secret-project/plan.md: rm -rf build');
    error.stack = [
      'TypeError: cannot read /Users/someone/secret-project/plan.md: rm -rf build',
      '    at decide (address at index.android.bundle:1:23456)',
      '    at anonymous (index.android.bundle:12:34)',
      '    at a line that is not a frame /Users/someone',
    ].join('\n');
    const scrubbed = scrubError(error);
    expect(scrubbed.name).toBe('TypeError');
    expect(scrubbed.message).toBe('TypeError');
    expect(scrubbed.stack).toBe(['TypeError', '    at decide (address at index.android.bundle:1:23456)', '    at anonymous (index.android.bundle:12:34)'].join('\n'));
    expect(JSON.stringify({ m: scrubbed.message, s: scrubbed.stack })).not.toMatch(/secret|rm -rf|Users/);
  });

  it('names anything that is not an Error plainly', () => {
    expect(scrubError('a string with content').message).toBe('Error');
    expect(scrubError({ name: 'Bad Name With Spaces' }).name).toBe('Error');
  });
});

describe('installCrashReporting', () => {
  let handler: ((error: unknown, isFatal?: boolean) => void) | undefined;
  const flush = () => new Promise((resolve) => setImmediate(resolve));

  beforeAll(async () => {
    (globalThis as { ErrorUtils?: unknown }).ErrorUtils = {
      getGlobalHandler: () => () => undefined,
      setGlobalHandler: (next: typeof handler) => {
        handler = next;
      },
    };
    installCrashReporting();
    await flush();
  });

  it('turns collection on from the setting, and off with unsent reports deleted', async () => {
    expect(setCrashlyticsCollectionEnabled).toHaveBeenLastCalledWith(expect.anything(), true);
    expect(deleteUnsentReports).not.toHaveBeenCalled();
    setCrashReports(false);
    await flush();
    expect(setCrashlyticsCollectionEnabled).toHaveBeenLastCalledWith(expect.anything(), false);
    expect(deleteUnsentReports).toHaveBeenCalledTimes(1);
    setCrashReports(true);
    await flush();
  });

  it('records only the scrubbed error and still hands the error to React Native', () => {
    const error = new Error('the secret plan');
    handler?.(error, true);
    expect(recordError).toHaveBeenCalledTimes(1);
    const recorded = (recordError as jest.Mock).mock.calls[0][1] as Error;
    expect(recorded.message).toBe('Error');
    expect(defaultErrorHandler).toHaveBeenCalledWith(expect.any(Error), true);
  });

  it('records nothing while crash reports are off', async () => {
    (recordError as jest.Mock).mockClear();
    setCrashReports(false);
    await flush();
    handler?.(new Error('x'), false);
    expect(recordError).not.toHaveBeenCalled();
    setCrashReports(true);
    await flush();
  });

  it('hands React Native a scrubbed error outside development', () => {
    const dev = (globalThis as { __DEV__?: boolean }).__DEV__;
    (globalThis as { __DEV__?: boolean }).__DEV__ = false;
    try {
      handler?.(new Error('the secret plan'), true);
      const forwarded = (defaultErrorHandler as jest.Mock).mock.calls.at(-1)?.[0] as Error;
      expect(forwarded.message).not.toMatch(/secret/);
    } finally {
      (globalThis as { __DEV__?: boolean }).__DEV__ = dev;
    }
  });
});
