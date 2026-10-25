import { describe, expect, it } from 'vitest';
import tokens from '@tokens/design-tokens.json';
import { kebab, paletteVariables, percent, terminalPalette, themeName, tokenStyleSheet } from './tokens';
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
});
