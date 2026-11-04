import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { emptyState, reduce } from '../store/store';
import { textStyleSheet } from '../theme/text';
import { NativeNode, rowParts } from './NativeNode';
import { groupCategories, orderedButtons } from './Surfaces';
import { applyNative, boxStyle, edges, nodeText, type NativeNode as Node, type NativeView } from './types';

const view = (title: string): NativeView => ({ kind: 'dialog', title, sections: [] });

describe('native views', () => {
  it('opens a view on top, draws it again in its place and closes it', () => {
    let open = applyNative([], { kind: 'native', id: 'settings', place: 'surface', view: view('Settings') });
    open = applyNative(open, { kind: 'native', id: 'dialog-1', place: 'dialog', view: view('Confirm') });
    expect(open.map((item) => item.id)).toEqual(['settings', 'dialog-1']);
    open = applyNative(open, { kind: 'native', id: 'settings', place: 'surface', view: view('Settings again') });
    expect(open.map((item) => item.view.title)).toEqual(['Settings again', 'Confirm']);
    const closed = applyNative(open, { kind: 'native', id: 'dialog-1', view: null });
    expect(closed.map((item) => item.id)).toEqual(['settings']);
    expect(applyNative(closed, { kind: 'native', id: 'gone', view: null })).toBe(closed);
  });

  it('keeps the views and the text setting in the store', () => {
    const state = reduce(reduce(emptyState, { kind: 'native', id: 'setup', place: 'surface', view: view('Setup') }), { kind: 'text', scale: 1.25, family: 'Aptos' });
    expect(state.natives).toHaveLength(1);
    expect(state.text).toEqual({ scale: 1.25, family: 'Aptos', transcriptFamily: undefined, codeFamily: undefined });
  });

  it('places a node the way the WinUI control set it', () => {
    expect(edges([1, 2, 3, 4])).toBe('2px 3px 4px 1px');
    expect(boxStyle({ margin: [10, 0, 10, 0], ha: 'end', row: 1, col: 2, colSpan: 2, dim: true })).toEqual({
      margin: '0px 10px 0px 10px', justifySelf: 'end', alignSelf: 'flex-end', gridRow: '2 / span 1', gridColumn: '3 / span 2', opacity: 0.5,
    });
  });

  it('reads the words of a tree', () => {
    const tree: Node = { t: 'stack', dir: 'h', c: [{ t: 'text', text: 'Theme' }, { t: 'button', id: 3, name: 'Reset', label: 'Reset' }, { t: 'switch', id: 4, on: true, name: 'x' }] };
    expect(nodeText(tree)).toBe('Theme Reset');
  });

  it('draws the controls with their state and the ids their input goes back to', () => {
    const tree: Node = {
      t: 'grid', cols: ['minmax(0,1fr)', 'auto'], rows: null, gap: [0, 12], c: [
        { t: 'text', text: 'Status line', ink: 'ink2', size: 11, row: 0, col: 0 },
        { t: 'switch', id: 7, on: true, name: 'Status line', row: 0, col: 1 },
        { t: 'segmented', name: 'Theme', options: [{ id: 8, label: 'Dark', on: true }, { id: 9, label: 'Light', on: false }], row: 1, col: 0 },
        { t: 'select', id: 10, index: 1, name: 'Model', items: ['a', 'b'], row: 1, col: 1 },
      ],
    };
    const html = renderToStaticMarkup(createElement(NativeNode, { node: tree }));
    expect(html).toContain('grid-template-columns:minmax(0,1fr) auto');
    expect(html).toContain('color:var(--ink2)');
    expect(html).toMatch(/role="switch" aria-checked="true"[^>]*data-native-id="7"/);
    expect(html).toMatch(/aria-checked="false"[^>]*data-native-id="9"/);
    expect(html).toContain('<option value="1" selected="">b</option>');
  });
});

describe('settings rows and dialog buttons', () => {
  it('draws a marked row as the name and caption at the left and its control at the right', () => {
    const row: Node = {
      t: 'grid', role: 'row', cols: ['minmax(0,0.45fr)', 'minmax(0,0.55fr)'], rows: null, gap: [0, 0], c: [
        { t: 'stack', dir: 'v', gap: 2, row: 0, col: 0, c: [{ t: 'text', text: 'Theme', size: 13 }, { t: 'text', text: 'Follows the system', size: 11, ink: 'ink2' }] },
        { t: 'select', id: 3, index: 0, name: 'Theme', items: ['System'], row: 0, col: 1, margin: [0, -1, 0, 0] },
      ],
    };
    const html = renderToStaticMarkup(createElement(NativeNode, { node: row }));
    expect(html).toContain('class="settings-row"');
    expect(html).toMatch(/class="settings-row-label".*Theme.*Follows the system/);
    expect(html).toMatch(/class="settings-row-control"><div class="native-node native-n-select"/);
    // The form's share, sizes, inks and margins give way to the standard's (native.css).
    expect(html).not.toContain('grid-template-columns');
    expect(html).not.toContain('font-size:calc(13px');
    expect(html).not.toContain('margin:');
  });

  it('takes a pair of pop-ups for one pair and a wide row for the name over its field', () => {
    const pair: Node = { t: 'grid', role: 'row', cols: null, rows: null, gap: [0, 6], c: [
      { t: 'text', text: 'Planning', col: 0 },
      { t: 'stack', dir: 'h', col: 1, c: [{ t: 'select', id: 1, index: 0, name: 'Model', items: ['a'] }, { t: 'box', c: [{ t: 'select', id: 2, index: 0, name: 'Effort', items: ['b'] }] }] },
    ] };
    expect(rowParts(pair).pair).toBe(true);
    expect(rowParts(pair).controls).toHaveLength(2);
    const wide: Node = { t: 'stack', dir: 'v', role: 'wideRow', c: [{ t: 'text', text: 'Address' }, { t: 'field', id: 4, text: 'host:22' }] };
    expect(renderToStaticMarkup(createElement(NativeNode, { node: wide }))).toContain('class="settings-row wide"');
  });

  it('draws a settings card row without the form insets and the reading as the standard loading', () => {
    const html = renderToStaticMarkup(createElement(NativeNode, { node: { t: 'box', role: 'settingsRow', pad: [0, 8, 0, 8], line: [0, 1, 0, 0], margin: [7, 0, 7, 0], c: [{ t: 'text', text: 'x' }] } }));
    expect(html).toContain('native-role-settingsRow');
    expect(html).not.toMatch(/padding|border|margin/);
  });

  it('orders the buttons [secondary] [cancel] [primary] and leaves a panel’s close to its ✕', () => {
    const buttons = [{ id: 'close', label: 'Cancel', disabled: false, accent: false }, { id: 'primary', label: 'OK', disabled: false, accent: true }, { id: 'secondary', label: 'More', disabled: false, accent: false }] as const;
    expect(orderedButtons(buttons, false).map((b) => b.id)).toEqual(['secondary', 'close', 'primary']);
    expect(orderedButtons(buttons, true).map((b) => b.id)).toEqual(['secondary', 'primary']);
  });
});

describe('text setting', () => {
  it('scales every type size to the nearest half point and puts a chosen family first', () => {
    expect(textStyleSheet(undefined)).toBe('');
    expect(textStyleSheet({ scale: 1 })).toBe(':root{--text-scale:1;}');
    const sheet = textStyleSheet({ scale: 1.1, family: 'Aptos', codeFamily: 'Fira "Code"' });
    expect(sheet).toContain('--text-scale:1.1;');
    expect(sheet).toMatch(/--type-body:\d+(\.5)?px;/);
    expect(sheet).toContain('--font-body:"Aptos", var(--font-design-body);');
    expect(sheet).toContain('--font-mono:"Fira Code", var(--font-design-mono);');
  });

  it('groups the settings categories into One UI cards, an unknown one in the last', () => {
    const ids = ['general', 'models', 'styles', 'tools', 'cli', 'mobile', 'remote', 'companion', 'about', 'labs'].map((id) => ({ id }));
    expect(groupCategories(ids).map((group) => group.map((item) => item.id))).toEqual([
      ['general', 'models', 'styles'], ['tools', 'cli'], ['mobile', 'remote'], ['companion', 'about', 'labs'],
    ]);
    // A host without a category leaves no empty card.
    expect(groupCategories([{ id: 'about' }]).map((group) => group.map((item) => item.id))).toEqual([['about']]);
  });
});

describe('a dialog footer part that draws nothing', () => {
  it('counts as none, so an idle panel has no footer', async () => {
    const { shows } = await import('./Surfaces');
    expect(shows(null)).toBe(false);
    expect(shows({ t: 'stack', dir: 'h', c: [] })).toBe(false);
    expect(shows({ t: 'stack', dir: 'h', c: [{ t: 'stack', dir: 'h', c: [] }, { t: 'text', text: '  ' }] })).toBe(false);
    expect(shows({ t: 'stack', dir: 'h', c: [{ t: 'spinner' }] })).toBe(true);
    expect(shows({ t: 'text', text: 'Installing' })).toBe(true);
  });
});
