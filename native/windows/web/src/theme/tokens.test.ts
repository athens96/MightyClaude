import { describe, expect, it } from 'vitest';
import tokens from '@tokens/design-tokens.json';
import { kebab, paletteVariables, themeName, tokenStyleSheet } from './tokens';

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
});
