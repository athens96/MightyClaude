import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { spacing } from '@/theme';

/**
 * The same file the Mac (`DesignTokenParityTests`) and Windows (Core.Tests) read: the
 * phone's spacing scale is the shared dense scale, under the phone's own names, each one
 * step above the contract's (`spacing.xs` is the contract's `xxs`, and so on).
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/design-tokens.json');
const contract = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  metrics: { spacing: Record<string, number> };
};

const phoneToContract: Record<keyof typeof spacing, string> = {
  xs: 'xxs',
  sm: 'xs',
  md: 'sm',
  lg: 'md',
  xl: 'lg',
  xxl: 'xl',
};

describe('design spacing', () => {
  it.each(Object.entries(phoneToContract))('spacing.%s is the contract step %s', (phone, step) => {
    const expected = contract.metrics.spacing[step];
    expect(expected).toEqual(expect.any(Number));
    expect(spacing[phone as keyof typeof spacing]).toBe(expected);
  });

  it('covers every contract step, and nothing off the scale', () => {
    expect(Object.values(phoneToContract).sort()).toEqual(Object.keys(contract.metrics.spacing).sort());
    expect(Object.keys(spacing).sort()).toEqual(Object.keys(phoneToContract).sort());
  });
});
