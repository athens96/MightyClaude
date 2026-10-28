import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import type { Pills } from './menus';
import { AskUnsandboxedRow, askRows, chooseAskTarget, Toolbar } from './Toolbar';
import type { AskTargetOption } from './types';

const t = translator([{ 'composer.ask.menuLabel': 'Send to', 'composer.ask.claude': 'Claude (default)', 'composer.ask.unsandboxed': '⚠ {name} runs without approvals' }]);
const services = { bridge: { call: () => Promise.resolve(null) }, store: { dispatch: () => {} }, t } as unknown as AppServices;
const pills: Pills = {
  provider: 'claude', model: 'Default', modelTip: '', showsEffort: false, effort: '', effortEnabled: false, effortTip: '', permission: 'Ask', permissionHelp: '',
  unlocked: false, showsFast: false, fast: false, fastEnabled: false, moreOn: false, contextPercent: null, resuming: false,
};
const targets: AskTargetOption[] = [{ id: 'codex', label: 'Codex' }, { id: 'antigravity', label: 'Antigravity' }];
const render = (over: { askTargets?: AskTargetOption[]; askTarget?: string | null } = {}) =>
  renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services },
    createElement(Toolbar, { paneId: 'p', pills, busy: false, showUsage: false, onUsageShown: () => {}, ...over })));

// The 보낼 대상 switch (MainWindow.AskTargets.cs): the host says which targets a pane offers and which one its sends go to.
describe('the 보낼 대상 pill', () => {
  it('shows only while the pane offers a target', () => {
    expect(render()).not.toContain('data-pill="ask"');
    expect(render({ askTargets: [] })).not.toContain('data-pill="ask"');
    const html = render({ askTargets: targets });
    expect(html).toContain('data-pill="ask"');
    expect(html).toContain('>Claude<');
  });

  it('names and tints the target asked, and stays on while the pane is busy', () => {
    const html = renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services },
      createElement(Toolbar, { paneId: 'p', pills, busy: true, showUsage: false, onUsageShown: () => {}, askTargets: targets, askTarget: 'codex' })));
    const pill = html.match(/<button[^>]*data-pill="ask"[^>]*>/)?.[0] ?? '';
    expect(pill).toContain('composer-pill on');
    expect(pill).not.toContain('disabled');
    expect(html).toContain('>Codex<');
  });

  it('lists Claude first, then each target, checking the one in use', () => {
    expect(askRows(targets, 'antigravity', t)).toEqual([
      { kind: 'choice', id: null, text: 'Claude (default)', checked: false },
      { kind: 'separator' },
      { kind: 'choice', id: 'codex', text: 'Codex', checked: false },
      { kind: 'choice', id: 'antigravity', text: 'Antigravity', checked: true },
    ]);
    expect(askRows(targets, null, t)[0]).toEqual({ kind: 'choice', id: null, text: 'Claude (default)', checked: true });
  });

  it('a choice calls setAskTarget with the pane and the target, null for Claude', async () => {
    const calls: [string, unknown][] = [];
    const bridge = { call: (method: string, args?: unknown) => { calls.push([method, args]); return Promise.resolve(null); } };
    await chooseAskTarget(bridge, 'p', 'codex');
    await chooseAskTarget(bridge, 'p', null);
    expect(calls).toEqual([['setAskTarget', { paneId: 'p', target: 'codex' }], ['setAskTarget', { paneId: 'p', target: null }]]);
  });
});

describe('the unsandboxed warning row', () => {
  const row = (target: AskTargetOption | null) =>
    renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, createElement(AskUnsandboxedRow, { target })));

  it('names the CLI asked, and is gone once Claude is the target', () => {
    const html = row(targets[0] ?? null);
    expect(html).toContain('data-testid="composer-ask-unsandboxed"');
    expect(html).toContain('⚠ Codex runs without approvals');
    expect(row(null)).toBe('');
  });
});
