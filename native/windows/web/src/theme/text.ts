// The text setting (Core TextAppearance, MainWindow.TextAppearance.cs) as CSS variables over the tokens' own: every `--type-*`
// size at the setting's scale (`--text-scale` for the sizes a host form names itself), and the chosen families in front of
// the design's, as WinUI's AppText falls back to DesignMetrics.Font.
import tokens from '@tokens/design-tokens.json';
import type { TextSetting } from '../native/types';

function family(chosen: string | null | undefined, fallback: string): string | null {
  return chosen ? `"${chosen.replace(/["\\]/g, '')}", var(${fallback})` : null;
}

/** The style sheet for a text setting; empty for the design's own (scale 1, no families). */
export function textStyleSheet(text: TextSetting | undefined): string {
  if (!text) return '';
  const scale = Number.isFinite(text.scale) && text.scale > 0 ? text.scale : 1;
  const parts: string[] = [`--text-scale:${scale};`];
  if (scale !== 1) {
    for (const [name, value] of Object.entries(tokens.metrics.type)) {
      if (typeof value !== 'number') continue;
      // WinUI's rounding: to the nearest half point (TextAppearance.Size).
      parts.push(`--${'type-' + name.replace(/[A-Z]/g, (letter) => '-' + letter.toLowerCase())}:${Math.round(value * scale * 2) / 2}px;`);
    }
  }
  const body = family(text.family, '--font-design-body');
  const mono = family(text.codeFamily, '--font-design-mono');
  const transcript = family(text.transcriptFamily, '--font-design-body');
  if (body) parts.push(`--font-body:${body};--font-heading:${body};`);
  if (mono) parts.push(`--font-mono:${mono};`);
  if (transcript) parts.push(`--font-transcript:${transcript};`);
  return `:root{${parts.join('')}}`;
}

/** Keeps one style element holding the setting's sheet. */
export function applyText(document: Document, text: TextSetting | undefined): void {
  let style = document.querySelector<HTMLStyleElement>('style[data-text]');
  if (!style) {
    style = document.createElement('style');
    style.dataset.text = '';
    document.head.appendChild(style);
  }
  style.textContent = textStyleSheet(text);
}
