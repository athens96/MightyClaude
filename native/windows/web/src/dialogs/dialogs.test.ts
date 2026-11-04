import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { DialogLoading, DialogShell, formSheetWidth } from './DialogShell';
import { clampSize, dragSize, fitSize, loadSize, saveSize, sheetWidth } from './panelSize';

const services = {
  bridge: { call: () => Promise.resolve(null) },
  store: { dispatch: () => {} },
  t: translator([{ 'dialog.close': 'Close', 'dialog.closeBusy': 'You can close this when the work finishes', 'dialog.resize': 'Resize', 'dialog.loading': 'Loading…' }]),
} as unknown as AppServices;
const render = (element: ReturnType<typeof createElement>) => renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, element));

describe('panel size', () => {
  const room = { width: 1280, height: 800 };
  const min = { width: 420, height: 320 };
  it('keeps a panel at least its least size and inside the window, the window winning', () => {
    expect(clampSize({ width: 100, height: 100 }, min, room)).toEqual(min);
    expect(clampSize({ width: 5000, height: 5000 }, min, room)).toEqual({ width: 1240, height: 760 });
    expect(clampSize({ width: 600, height: 400 }, min, { width: 300, height: 300 })).toEqual({ width: 260, height: 260 });
    expect(fitSize(room)).toEqual({ width: 1240, height: 760 });
  });

  it('grows by twice the drag, so the corner stays under the pointer of a centred panel', () => {
    expect(dragSize({ width: 600, height: 400 }, 10, -5, min, room)).toEqual({ width: 620, height: 390 });
  });

  it('remembers the last size per kind of panel and ignores what it cannot read', () => {
    const store = new Map<string, string>();
    const storage = { getItem: (key: string) => store.get(key) ?? null, setItem: (key: string, value: string) => { store.set(key, value); } };
    expect(loadSize('plugins', storage)).toBeNull();
    saveSize('plugins', { width: 801.4, height: 600.6 }, storage);
    expect(loadSize('plugins', storage)).toEqual({ width: 801, height: 601 });
    expect(loadSize('plan', storage)).toBeNull();
    store.set('mighty.panelSize.plan', '{"width":"x"}');
    expect(loadSize('plan', storage)).toBeNull();
    store.set('mighty.panelSize.plan', 'not json');
    expect(loadSize('plan', storage)).toBeNull();
  });

  it('snaps a form sheet to S 360, M 460 or L 560', () => {
    expect(sheetWidth(undefined, { s: 360, m: 460, l: 560 })).toBe(460);
    expect([300, 360, 400, 460, 500, 900].map((w) => formSheetWidth(w))).toEqual([360, 360, 460, 460, 560, 560]);
  });
});

describe('dialog shell', () => {
  it('draws an alert 400 wide with its buttons and no ✕ or grip', () => {
    const html = render(createElement(DialogShell, { kind: 'alert', label: 'Delete?', title: 'Delete?', onCancel: () => {}, buttons: createElement('button', { className: 'btn danger' }, 'Delete') }, 'Gone for good.'));
    expect(html).toContain('class="dialog kind-alert"');
    expect(html).toContain('width:400px');
    expect(html).toContain('<strong class="dialog-title">Delete?</strong>');
    expect(html).toContain('class="dialog-footer"');
    expect(html).not.toContain('dialog-close');
    expect(html).not.toContain('dialog-grip');
  });

  it('draws a panel with its ✕, the corner grip and no footer when it holds nothing', () => {
    const html = render(createElement(DialogShell, { kind: 'panel', panel: 'test', width: 760, height: 620, label: 'Plugins', title: 'Plugins', subtitle: 'D:\\Work', onCancel: () => {} }, 'list'));
    expect(html).toContain('class="dialog kind-panel"');
    expect(html).toContain('width:760px;height:620px');
    expect(html).toMatch(/class="dialog-close" aria-label="Close" title="Close" data-testid="dialog-close"/);
    expect(html).toContain('aria-label="Resize"');
    expect(html).not.toContain('dialog-footer');
  });

  it('dims the ✕ of a busy panel with the reason it cannot close', () => {
    const html = render(createElement(DialogShell, { kind: 'panel', label: 'Plugins', title: 'Plugins', busy: true, onCancel: () => {}, status: 'Installing…' }, 'list'));
    expect(html).toMatch(/class="dialog-close busy" aria-label="Close" aria-disabled="true" title="You can close this when the work finishes"/);
    expect(html).toContain('<div class="dialog-status">Installing…</div>');
  });

  it('shows a body being read as the spinner, the words and three row places', () => {
    const html = render(createElement(DialogLoading));
    expect(html).toContain('Loading…');
    expect(html.match(/class="dialog-skeleton"/g)).toHaveLength(3);
  });
});
