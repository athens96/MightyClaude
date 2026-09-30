import { darkPalette, lightPalette, type Palette } from '@/theme';

/** WCAG 2.x relative luminance of a `#rrggbb` colour. */
function luminance(hex: string): number {
  const match = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
  if (!match) throw new Error(`not a #rrggbb colour: ${hex}`);
  const channel = (part: string): number => {
    const value = parseInt(part, 16) / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  const [, r = '', g = '', b = ''] = match;
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function contrast(foreground: string, background: string): number {
  const a = luminance(foreground);
  const b = luminance(background);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** Body-size text needs 4.5:1 (WCAG 2.1 AA, 1.4.3). */
const AA = 4.5;

type Pair = [foreground: keyof Palette, background: keyof Palette];

/**
 * Every text colour on the ground it is drawn on. The accent and the status and block
 * tints are drawn as words (links, status labels, block marks, headings) on the page
 * and on the surface sheets; `onAccent` is the label of the filled primary button and
 * of a prominent guided chip, and danger is the label of the soft danger button.
 */
const textPairs: Pair[] = [
  ['text', 'background'],
  ['text', 'surface'],
  ['text', 'surfaceRaised'],
  ['text', 'bubbleUser'],
  ['text', 'accentMuted'],
  ['textMuted', 'background'],
  ['textMuted', 'surface'],
  ['textMuted', 'surfaceRaised'],
  ['textFaint', 'background'],
  ['textFaint', 'surface'],
  ['grey', 'background'],
  ['accent', 'background'],
  ['accent', 'surface'],
  ['onAccent', 'accent'],
  ['danger', 'dangerSurface'],
  ['danger', 'background'],
  ['success', 'background'],
  ['warning', 'background'],
  ['blockAgent', 'background'],
  ['blockTask', 'background'],
  ['blockSteer', 'background'],
  ['blockCompact', 'background'],
  ['blockQuestion', 'background'],
  ['blockAgent', 'surface'],
  ['blockTask', 'surface'],
  ['blockSteer', 'surface'],
  ['blockCompact', 'surface'],
  ['blockQuestion', 'surface'],
  ['onAccent', 'blockAgent'],
  ['onAccent', 'blockTask'],
  ['onAccent', 'blockSteer'],
  ['onAccent', 'blockCompact'],
  ['onAccent', 'blockQuestion'],
  ['onAccent', 'success'],
  ['onAccent', 'danger'],
];

describe.each([
  ['light', lightPalette],
  ['dark', darkPalette],
])('%s palette', (_name, palette) => {
  it.each(textPairs)('%s on %s clears AA', (foreground, background) => {
    expect(contrast(palette[foreground], palette[background])).toBeGreaterThanOrEqual(AA);
  });

  it('keeps the accent a distinct mark on the soft selected wash', () => {
    // The selected chip's dot and border sit on accentMuted: a non-text mark needs 3:1.
    expect(contrast(palette.accent, palette.accentMuted)).toBeGreaterThanOrEqual(3);
  });

  it('keeps the page, its sheets and its hairlines apart but close', () => {
    // Paper, not panels: the surfaces stay within a small step of the page.
    expect(contrast(palette.surface, palette.background)).toBeLessThan(1.5);
    expect(contrast(palette.surfaceRaised, palette.background)).toBeLessThan(1.5);
    expect(contrast(palette.border, palette.background)).toBeGreaterThan(1.1);
  });
});

it('keeps the dark palette warm charcoal rather than black', () => {
  expect(luminance(darkPalette.background)).toBeGreaterThan(0.015);
});
