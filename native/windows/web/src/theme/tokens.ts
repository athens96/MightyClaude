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

/** `#RRGGBB` at an opacity, as rgba(). */
export function hexAlpha(hex: string, opacity: number): string {
  const value = parseInt(hex.slice(1, 7), 16);
  return `rgba(${(value >> 16) & 255},${(value >> 8) & 255},${value & 255},${opacity})`;
}

/** An opacity as a CSS percentage, rounded to a tenth so 0.45 reads 45% rather than 45.00000000000001%. */
export function percent(opacity: number): string {
  return `${Math.round(opacity * 1000) / 10}%`;
}

/**
 * The shared motion (design-tokens.json `motion`, natural submit A2), as Mac and Windows Core DesignMetrics.Motion hold it:
 * durations in ms, the standard easing as cubic-bezier control points, the pending display's delay and shortest showing, and
 * how long a highlight stays (the queue row's flash).
 */
export const Motion = {
  quick: tokens.motion.quick,
  standard: tokens.motion.standard,
  scroll: tokens.motion.scroll,
  easing: tokens.motion.easing as readonly number[],
  pendingShowAfter: tokens.motion.pendingShowAfter,
  pendingMinVisible: tokens.motion.pendingMinVisible,
  highlight: tokens.motion.highlight,
} as const;

/**
 * A CSS cubic-bezier as a function of time 0–1 (x) to progress (y): x is solved by Newton's method, with bisection where the
 * slope is too flat, so a page animation driven by requestAnimationFrame follows the same curve as `transition-timing-function`.
 */
export function cubicBezier(points: readonly number[]): (t: number) => number {
  const [x1 = 0, y1 = 0, x2 = 1, y2 = 1] = points;
  const curve = (a: number, b: number, s: number) => 3 * a * s * (1 - s) * (1 - s) + 3 * b * s * s * (1 - s) + s * s * s;
  const slope = (a: number, b: number, s: number) => 3 * a * (1 - s) * (1 - s) + 6 * (b - a) * s * (1 - s) + 3 * (1 - b) * s * s;
  return (t: number) => {
    if (!(t > 0)) return 0;
    if (t >= 1) return 1;
    let s = t;
    for (let i = 0; i < 8; i++) {
      const error = curve(x1, x2, s) - t;
      if (Math.abs(error) < 1e-6) return curve(y1, y2, s);
      const d = slope(x1, x2, s);
      if (Math.abs(d) < 1e-6) break;
      s -= error / d;
    }
    let low = 0, high = 1;
    s = t;
    for (let i = 0; i < 40; i++) {
      const x = curve(x1, x2, s);
      if (Math.abs(x - t) < 1e-6) break;
      if (x < t) low = s; else high = s;
      s = (low + high) / 2;
    }
    return curve(y1, y2, s);
  };
}

/** The standard easing (Motion.easing) as a function. */
export const ease = cubicBezier(Motion.easing);

/** `--motion-<name>: <n>ms;` for each duration, and the easing as `--motion-easing`. */
export function motionVariables(): string {
  const durations = Object.entries(tokens.motion).filter((entry): entry is [string, number] => typeof entry[1] === 'number');
  return durations.map(([name, value]) => `--motion-${kebab(name)}:${value}ms;`).join('') + `--motion-easing:cubic-bezier(${Motion.easing.join(', ')});`;
}

/** Whether the system asks for less motion: movement is dropped, every show and hide keeps its timing (A1). */
export function reducesMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
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
    metricVariables('layout', metrics.layout) +
    motionVariables() +
    `--font-body:${quoteFamilies(metrics.font.body)}, sans-serif;` +
    `--font-heading:${quoteFamilies(metrics.font.heading)}, sans-serif;` +
    `--font-mono:${quoteFamilies(metrics.font.mono)}, monospace;` +
    // The design's own families, behind a family the text setting chose (theme/text.ts).
    `--font-design-body:${quoteFamilies(metrics.font.body)}, sans-serif;` +
    `--font-design-mono:${quoteFamilies(metrics.font.mono)}, monospace;` +
    // The 파이프라인 view's state washes and its cards' status bar (fixture `pipeline`).
    `--pipeline-column-tint:${percent(tokens.pipeline.columnTintOpacity)};--pipeline-card-tint:${percent(tokens.pipeline.cardTintOpacity)};--pipeline-card-bar:${tokens.pipeline.cardBarWidth}px;` +
    // The agents' brand marks (ProviderMark.swift), the same in both themes.
    Object.entries(tokens.windowsOnly.provider).filter((entry): entry is [string, string[]] => Array.isArray(entry[1])).map(([name, colors]) => `--provider-${name}:${colors[0]};`).join('');
  const extra = (theme: 'light' | 'dark') => {
    const subtle = tokens.windowsOnly.subtle[theme];
    const syntax = tokens.windowsOnly.syntax[theme];
    const derived = tokens.derived[theme];
    const glyphs = Object.entries(derived.tones).map(([tone, value]) => `--glyph-${tone}:${value.glyph};`).join('');
    const discs = Object.entries(derived.disc).map(([tone, value]) => `--disc-${tone}:${value.fill};--disc-${tone}-ink:${value.ink};`).join('');
    return glyphs + discs + `--segment-track:${derived.segmentTrack};--segment-on:${derived.segmentOn};--subtle:${hexAlpha(subtle.color, subtle.opacity)};--syntax-keyword:${syntax.keyword};--syntax-string:${syntax.string};--syntax-number:${syntax.number};--terminal-background:${tokens.windowsOnly.terminal[theme].background};`;
  };
  return (
    `:root{${shared}}` +
    `:root[data-theme="light"]{color-scheme:light;${paletteVariables(tokens.light)}${extra('light')}}` +
    `:root[data-theme="dark"]{color-scheme:dark;${paletteVariables(tokens.dark)}${extra('dark')}}`
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

/** The terminal's xterm palette for a theme: the fixture's windowsOnly.terminal (the Mac terminal's Ghostty themes as values). */
export function terminalPalette(theme: 'light' | 'dark') {
  return tokens.windowsOnly.terminal[theme];
}
