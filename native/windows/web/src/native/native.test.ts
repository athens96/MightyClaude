import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { emptyState, reduce } from '../store/store';
import { textStyleSheet } from '../theme/text';
import { NativeNode } from './NativeNode';
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
});
