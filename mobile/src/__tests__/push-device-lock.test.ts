import { requireOptionalNativeModule } from 'expo';

import { isDeviceLocked } from '@/lib/push/device-lock';

jest.mock('expo', () => ({ requireOptionalNativeModule: jest.fn() }));

const lookup = requireOptionalNativeModule as jest.MockedFunction<typeof requireOptionalNativeModule>;

describe('isDeviceLocked', () => {
  afterEach(() => lookup.mockReset());

  it('counts the phone as locked when the native module is missing', () => {
    lookup.mockReturnValue(null);
    expect(isDeviceLocked()).toBe(true);
    expect(lookup).toHaveBeenCalledWith('MightyDeviceLock');
  });

  it('counts the phone as locked when the native check throws', () => {
    lookup.mockReturnValue({ isDeviceLocked: () => { throw new Error('no keyguard'); } });
    expect(isDeviceLocked()).toBe(true);
  });

  it('follows the native answer', () => {
    lookup.mockReturnValue({ isDeviceLocked: () => false });
    expect(isDeviceLocked()).toBe(false);
    lookup.mockReturnValue({ isDeviceLocked: () => true });
    expect(isDeviceLocked()).toBe(true);
  });
});
