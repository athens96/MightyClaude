import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { darkPalette, dialog, lightPalette, settingsLayout } from '@/theme';

/**
 * The popup standard's numbers come from the file the Mac and Windows read
 * (`metrics.dialog`, its `phone` block for the touch sizes, and `metrics.settings`).
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/design-tokens.json');
const contract = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  metrics: {
    dialog: Record<string, unknown> & { phone: Record<string, number> };
    settings: Record<string, number>;
  };
};

const shared = ['padding', 'buttonGap', 'iconSize', 'alertWidth', 'darkBorder', 'backdropOpacityLight', 'backdropOpacityDark'];

describe('design dialog', () => {
  it.each(Object.entries(dialog))('dialog.%s is the contract value', (key, value) => {
    const expected = shared.includes(key) ? contract.metrics.dialog[key] : contract.metrics.dialog.phone[key];
    expect(expected).toEqual(expect.any(Number));
    expect(value).toBe(expected);
  });

  it('carries every touch size of the phone block', () => {
    for (const key of Object.keys(contract.metrics.dialog.phone)) expect(dialog).toHaveProperty(key);
  });

  it.each(Object.entries(settingsLayout))('settingsLayout.%s is metrics.settings', (key, value) => {
    expect(value).toBe(contract.metrics.settings[key]);
  });

  it('dims behind a popup with black at the contract opacity (decision 1)', () => {
    expect(lightPalette.overlay).toBe(`rgba(0,0,0,${contract.metrics.dialog.backdropOpacityLight})`);
    expect(darkPalette.overlay).toBe(`rgba(0,0,0,${contract.metrics.dialog.backdropOpacityDark})`);
  });
});
