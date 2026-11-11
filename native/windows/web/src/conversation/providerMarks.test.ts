import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { providerMarkOutlines } from '@mobile/lib/provider-marks';
import core from '../../../MightyCode.Core/ProviderMark.cs?raw';
import { ProviderMark } from './ProviderMark';
import { markOutline, pageMarkOutlines } from './providerMarks';

type Point = [number, number];

/** The closed figures of an outline of absolute M / L / Z commands. */
function figures(outline: string): Point[][] {
  const result: Point[][] = [];
  for (const part of outline.split(/(?=[MLZ])/)) {
    const values = part.slice(1).trim().split(' ').filter((value) => value.length > 0).map(Number);
    if (part.startsWith('M')) result.push([[values[0]!, values[1]!]]);
    else if (part.startsWith('L')) result.at(-1)!.push([values[0]!, values[1]!]);
  }
  return result;
}

/** The point's winding number under the outline: 0 is left open by a nonzero fill. */
function winding(outline: string, [x, y]: Point): number {
  let total = 0;
  for (const figure of figures(outline)) {
    figure.forEach(([x1, y1], index) => {
      const [x2, y2] = figure[(index + 1) % figure.length]!;
      const side = (x2 - x1) * (y - y1) - (x - x1) * (y2 - y1);
      if (y1 <= y && y2 > y && side > 0) total += 1;
      else if (y1 > y && y2 <= y && side < 0) total -= 1;
    });
  }
  return total;
}

describe('provider marks', () => {
  it('draws opencode and Hermes from the outlines Core ProviderMark.cs holds', () => {
    const constant = (name: string) => new RegExp(`const string ${name} = "([^"]+)";`).exec(core)?.[1];
    expect(pageMarkOutlines.opencode).toBe(constant('Opencode'));
    expect(pageMarkOutlines.hermes).toBe(constant('Hermes'));
  });

  it('takes the other four from the phone and knows no seventh', () => {
    for (const provider of ['claude', 'codex', 'gemini', 'pi'] as const) expect(markOutline(provider)).toBe(providerMarkOutlines[provider]);
    expect(markOutline('opencode')).toBe(pageMarkOutlines.opencode);
    expect(markOutline('hermes')).toBe(pageMarkOutlines.hermes);
    expect(markOutline('rovo')).toBeUndefined();
    expect(markOutline('')).toBeUndefined();
  });

  it.each(Object.entries(pageMarkOutlines))('%s is closed figures of M / L / Z inside the 24 box', (_, outline) => {
    expect(outline).toMatch(/^M[MLZ0-9 .]+Z$/);
    for (const figure of figures(outline)) {
      expect(figure.length).toBeGreaterThanOrEqual(3);
      for (const value of figure.flat()) {
        expect(value).toBeGreaterThanOrEqual(0);
        expect(value).toBeLessThanOrEqual(24);
      }
    }
  });

  it('leaves the O\'s hole open: the inner figure is wound against the frame', () => {
    const o = pageMarkOutlines.opencode;
    expect(winding(o, [12, 10])).toBe(0);
    expect(winding(o, [12, 17])).not.toBe(0);
    expect(winding(o, [5, 10])).not.toBe(0);
    expect(winding(o, [1, 1])).toBe(0);
  });

  it('fills the H and both wings and nothing between them', () => {
    const h = pageMarkOutlines.hermes;
    for (const inside of [[8.5, 15], [15.5, 8], [12, 12], [5, 7], [19, 7]] as Point[]) expect(winding(h, inside)).not.toBe(0);
    for (const outside of [[12, 8], [12, 17], [6.5, 12], [17.5, 12]] as Point[]) expect(winding(h, outside)).toBe(0);
  });

  it('renders the mark with the default nonzero fill, coloured by its provider', () => {
    const html = renderToStaticMarkup(createElement(ProviderMark, { provider: 'opencode', size: 12 }));
    expect(html).toContain('data-provider="opencode"');
    expect(html).toContain(`d="${pageMarkOutlines.opencode}"`);
    expect(html).not.toContain('fill-rule');
    expect(renderToStaticMarkup(createElement(ProviderMark, { provider: 'hermes', size: 12 }))).toContain('class="provider-mark brand"');
    expect(renderToStaticMarkup(createElement(ProviderMark, { provider: 'rovo', size: 12 }))).toBe('');
  });
});
