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
 * Every text colour on the ground it is drawn on, in concept D:
 * - ink and its two greys on the page, on cards, on the raised strip inside a card;
 * - the status inks as words on the page and on cards (status labels, reachability,
 *   reasons in "알림", the "완료" figure) and on their own soft tints (soft pills, the
 *   quiet stat tiles, the error banner);
 * - the text on every status fill: white on run, done, error, stop and the idle avatar,
 *   dark ink on wait (stat tiles, filled pills, timeline glyphs, tool chips, and the
 *   "?" and "!" inside the wait and error glyph discs);
 * - the accent as links and the selected tab on page and card, and on its blue wash
 *   (selected picker option, inline code); white on the primary button;
 * - the user's ink bubble, the toast, fenced code, the badge;
 * - the block tints as kind marks on cards, and under white/ink glyphs.
 */
const textPairs: Pair[] = [
  ['text', 'background'],
  ['text', 'surface'],
  ['text', 'surfaceRaised'],
  ['text', 'accentMuted'],
  ['background', 'text'],
  ['textMuted', 'background'],
  ['textMuted', 'surface'],
  ['textMuted', 'surfaceRaised'],
  ['textFaint', 'background'],
  ['textFaint', 'surface'],
  ['textFaint', 'surfaceRaised'],
  ['grey', 'background'],
  ['grey', 'surface'],
  ['grey', 'stopSoft'],
  ['accent', 'background'],
  ['accent', 'surface'],
  ['accent', 'surfaceRaised'],
  ['accent', 'accentMuted'],
  ['accent', 'runSoft'],
  ['onAccent', 'accent'],
  ['success', 'background'],
  ['success', 'surface'],
  ['success', 'doneSoft'],
  ['danger', 'background'],
  ['danger', 'surface'],
  ['danger', 'surfaceRaised'],
  ['danger', 'dangerSurface'],
  ['warning', 'background'],
  ['warning', 'surface'],
  ['warning', 'surfaceRaised'],
  ['warning', 'waitSoft'],
  ['textMuted', 'stopSoft'],
  ['onStatus', 'run'],
  ['onStatus', 'done'],
  ['onStatus', 'err'],
  ['onStatus', 'stop'],
  ['onStatus', 'idle'],
  ['onWait', 'wait'],
  ['onBadge', 'wait'],
  ['onBubbleUser', 'bubbleUser'],
  ['codeText', 'codeSurface'],
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

  it('keeps the selection and the timeline marks visible on cards', () => {
    // Non-text marks (WCAG 1.4.11) need 3:1: the run blue of a progress fill and the
    // send button, the run/done/error/stop timeline nodes, on the card they sit on.
    for (const fill of ['run', 'done', 'err', 'stop'] as const) {
      expect(contrast(palette[fill], palette.surface)).toBeGreaterThanOrEqual(3);
    }
  });

  it('keeps the status glyphs visible on the page, on cards and on a pressed row', () => {
    // The turning spark, the check, the slashed ring and the error disc are marks
    // (WCAG 1.4.11): 3:1 on every ground a row or the conversation header sits on. The
    // amber wait disc is carried by its "?" (onWait on wait, checked as text above).
    for (const mark of ['markRun', 'markDone', 'markStop', 'err', 'textMuted'] as const) {
      for (const ground of ['background', 'surface', 'surfaceRaised'] as const) {
        expect(contrast(palette[mark], palette[ground])).toBeGreaterThanOrEqual(3);
      }
    }
  });

  it('lifts the cards off the page without turning them into panels', () => {
    // The page and its cards are told apart, but stay within a step of each other.
    expect(contrast(palette.surface, palette.background)).toBeGreaterThan(1.05);
    expect(contrast(palette.surface, palette.background)).toBeLessThan(1.5);
    expect(contrast(palette.border, palette.background)).toBeGreaterThan(1.05);
  });
});

it('keeps the dark page ink navy rather than black', () => {
  expect(luminance(darkPalette.background)).toBeGreaterThan(0.004);
  const [, r, , b] = /^#(..)(..)(..)$/.exec(darkPalette.background) ?? [];
  expect(parseInt(b ?? '0', 16)).toBeGreaterThan(parseInt(r ?? '0', 16));
});

it('keeps the status fills one set of colours across both modes', () => {
  // A tile, a glyph disc and a node mean the same thing by day and by night.
  for (const fill of ['run', 'wait', 'done', 'err', 'stop'] as const) {
    expect(darkPalette[fill]).toBe(lightPalette[fill]);
  }
});
