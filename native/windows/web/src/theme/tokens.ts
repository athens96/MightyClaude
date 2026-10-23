// The shared design tokens (native/contracts/fixtures/design-tokens.json) as CSS variables: one block per theme, picked
// by <html data-theme>. A colour is `--<token>` in kebab case (cardRaised → --card-raised); metrics are px.
import tokens from '@tokens/design-tokens.json';

export type Palette = Readonly<Record<string, string>>;

export function kebab(name: string): string {
  return name.replace(/[A-Z]/g, (letter) => '-' + letter.toLowerCase());
}

/** `--name: value;` for every string entry of a palette. */
export function paletteVariables(palette: Palette): string {
  return Object.entries(palette)
    .filter(([, value]) => typeof value === 'string')
    .map(([name, value]) => `--${kebab(name)}:${value};`)
    .join('');
}

/** `--<prefix>-<name>: <n>px;` for every number of a metrics group. */
export function metricVariables(prefix: string, group: Readonly<Record<string, unknown>>): string {
  return Object.entries(group)
    .filter((entry): entry is [string, number] => typeof entry[1] === 'number')
    .map(([name, value]) => `--${prefix}-${kebab(name)}:${value}px;`)
    .join('');
}

function quoteFamilies(list: string): string {
  return list.split(',').map((family) => `"${family.trim()}"`).join(', ');
}

/** The whole style sheet of variables: metrics and fonts for both themes, then each theme's palette. */
export function tokenStyleSheet(): string {
  const metrics = tokens.metrics;
  const shared =
    metricVariables('radius', metrics.radius) +
    metricVariables('stroke', metrics.stroke) +
    metricVariables('type', metrics.type) +
    metricVariables('space', metrics.spacing) +
    metricVariables('inset', metrics.inset) +
    `--font-body:${quoteFamilies(metrics.font.body)}, sans-serif;` +
    `--font-heading:${quoteFamilies(metrics.font.heading)}, sans-serif;` +
    `--font-mono:${quoteFamilies(metrics.font.mono)}, monospace;`;
  return (
    `:root{${shared}}` +
    `:root[data-theme="light"]{color-scheme:light;${paletteVariables(tokens.light)}}` +
    `:root[data-theme="dark"]{color-scheme:dark;${paletteVariables(tokens.dark)}}`
  );
}

/** Adds the variables to the document once. */
export function installTokens(document: Document): void {
  const style = document.createElement('style');
  style.dataset.tokens = '';
  style.textContent = tokenStyleSheet();
  document.head.appendChild(style);
}

/** "light" is light; anything else is the dark theme, as the host's DesignTokens.Palette reads it. */
export function themeName(theme: string | undefined): 'light' | 'dark' {
  return theme === 'light' ? 'light' : 'dark';
}
