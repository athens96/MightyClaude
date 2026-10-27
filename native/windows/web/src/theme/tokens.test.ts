import { describe, expect, it } from 'vitest';
import tokens from '@tokens/design-tokens.json';
import { Motion, cubicBezier, ease, kebab, motionVariables, paletteVariables, percent, terminalPalette, themeName, tokenStyleSheet } from './tokens';
import { terminalThemes } from '../panes/terminal/rules';

describe('tokens', () => {
  it('names variables in kebab case', () => {
    expect(kebab('cardRaised')).toBe('card-raised');
    expect(kebab('ink2')).toBe('ink2');
    expect(paletteVariables({ accentSoft: '#E6EDFF' })).toBe('--accent-soft:#E6EDFF;');
  });

  it('carries both palettes from the shared fixture', () => {
    const sheet = tokenStyleSheet();
    expect(sheet).toContain(`:root[data-theme="light"]{color-scheme:light;--page:${tokens.light.page};`);
    expect(sheet).toContain(`--page:${tokens.dark.page};`);
    expect(sheet).toContain(`--space-md:${tokens.metrics.spacing.md}px;`);
  });

  it('reads any theme but light as dark, as the host does', () => {
    expect(themeName('light')).toBe('light');
    expect(themeName('dark')).toBe('dark');
    expect(themeName(undefined)).toBe('dark');
  });

  it('takes the terminal palette from the fixture (windowsOnly.terminal), both themes, every xterm colour', () => {
    const names = ['background', 'foreground', 'cursor', 'cursorAccent', 'selectionBackground', 'black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
      'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite'];
    for (const theme of ['light', 'dark'] as const) {
      const palette = terminalPalette(theme) as Record<string, string>;
      expect(Object.keys(palette).sort()).toEqual([...names].sort());
      for (const value of Object.values(palette)) expect(value).toMatch(/^#[0-9A-F]{6}$/);
      expect(terminalThemes[theme]).toBe(terminalPalette(theme));
      expect(tokenStyleSheet()).toContain(`--terminal-background:${palette.background};`);
    }
    expect(terminalPalette('dark').background).toBe('#202020');
  });

  it('carries the pipeline tints and status bar from the fixture', () => {
    expect(percent(0.45)).toBe('45%');
    expect(percent(0.085)).toBe('8.5%');
    const sheet = tokenStyleSheet();
    expect(sheet).toContain(`--pipeline-column-tint:${percent(tokens.pipeline.columnTintOpacity)};`);
    expect(sheet).toContain(`--pipeline-card-tint:${percent(tokens.pipeline.cardTintOpacity)};`);
    expect(sheet).toContain(`--pipeline-card-bar:${tokens.pipeline.cardBarWidth}px;`);
    // The column's wash, the card's lighter tint (so a card stands out from its column) and the bar.
    expect(tokens.pipeline).toEqual({ columnTintOpacity: 0.6, cardTintOpacity: 0.35, cardBarWidth: 3 });
    expect(percent(tokens.pipeline.columnTintOpacity)).toBe('60%');
    expect(percent(tokens.pipeline.cardTintOpacity)).toBe('35%');
  });

  it('carries the motion tokens from the fixture, as DesignMetrics.Motion does on the Mac and in Windows Core', () => {
    expect(Motion).toEqual({
      quick: tokens.motion.quick, standard: tokens.motion.standard, scroll: tokens.motion.scroll, easing: tokens.motion.easing,
      pendingShowAfter: tokens.motion.pendingShowAfter, pendingMinVisible: tokens.motion.pendingMinVisible, highlight: tokens.motion.highlight,
    });
    // Every key of the fixture is carried, and nothing else.
    expect(Object.keys(Motion).sort()).toEqual(Object.keys(tokens.motion).sort());
    expect(Motion.pendingShowAfter).toBe(150);
    expect(Motion.pendingMinVisible).toBe(300);
    expect(Motion.scroll).toBe(280);
    expect(Motion.highlight).toBe(1200);
    expect(Motion.easing).toEqual([0.2, 0, 0, 1]);
  });

  it('writes the motion as CSS variables', () => {
    const sheet = tokenStyleSheet();
    expect(motionVariables()).toContain('--motion-scroll:280ms;');
    expect(sheet).toContain('--motion-pending-show-after:150ms;');
    expect(sheet).toContain('--motion-highlight:1200ms;');
    expect(sheet).toContain('--motion-easing:cubic-bezier(0.2, 0, 0, 1);');
  });

  it('follows the standard easing curve from 0 to 1, rising all the way', () => {
    expect(ease(0)).toBe(0);
    expect(ease(1)).toBe(1);
    expect(ease(-1)).toBe(0);
    expect(ease(2)).toBe(1);
    let previous = 0;
    for (let step = 1; step <= 100; step++) {
      const value = ease(step / 100);
      expect(value).toBeGreaterThanOrEqual(previous - 1e-9);
      previous = value;
    }
    // A decelerating curve: most of the way is done by half the time.
    expect(ease(0.5)).toBeGreaterThan(0.75);
    // The linear curve is the identity.
    const linear = cubicBezier([0, 0, 1, 1]);
    for (const t of [0.1, 0.25, 0.5, 0.9]) expect(linear(t)).toBeCloseTo(t, 5);
  });
});
