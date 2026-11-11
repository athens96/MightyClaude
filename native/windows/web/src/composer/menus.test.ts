import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { MenuList, RunSettingsPopover } from './Toolbar';
import { clearUnsupported, contextLabel, unsupportedSettings, type MenuRow, type RunSettingsInfo } from './menus';

const info = (over: Partial<RunSettingsInfo> = {}): RunSettingsInfo => ({
  provider: 'Claude', beta: false, model: 'Default', modelDescription: null, modelSource: '', permission: '', permissionDescription: '', approval: null,
  fastSupported: false, webSearch: false, networkAccess: false, networkAllowed: false, networkNote: '', maxTurns: true, maxBudgetUsd: true,
  values: { fastMode: false, webSearch: 'default', networkAccess: false, maxTurns: '', maxBudgetUsd: '' }, searchDescriptions: {}, held: false, ...over,
});

describe('run settings', () => {
  it('notices settings the runner cannot take and clears only those', () => {
    const settings = info();
    const values = { fastMode: true, webSearch: 'live', networkAccess: true, maxTurns: '5', maxBudgetUsd: '' };
    expect(unsupportedSettings(settings, values)).toBe(true);
    expect(clearUnsupported(settings, values)).toEqual({ fastMode: false, webSearch: 'default', networkAccess: false, maxTurns: '5', maxBudgetUsd: '' });
    const capable = info({ fastSupported: true, webSearch: true, networkAccess: true });
    expect(unsupportedSettings(capable, values)).toBe(false);
    expect(clearUnsupported(capable, values)).toEqual(values);
  });

  it('locks every editable field and Apply from live run state, then unlocks without losing the draft values', () => {
    const services = { bridge: { call: () => Promise.resolve(null) }, store: {}, t: translator([{ 'phone.session.settingsLocked': 'Wait until the run finishes.' }]) } as unknown as AppServices;
    const render = (busy: boolean, openingHeld: boolean) => {
      const settings = info({ held: openingHeld }); settings.values.maxTurns = '17';
      return renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, createElement(RunSettingsPopover, { paneId: 'p', info: settings, busy, close: () => {} })));
    };
    const busy = render(true, false);
    expect(busy).toContain('<fieldset class="popover-body run-settings-fields" disabled=""');
    expect(busy).toContain('data-testid="run-settings-apply" disabled=""');
    expect(busy).toContain('Wait until the run finishes.');
    expect(busy).toContain('value="17"');
    const idle = render(false, true);
    expect(idle).not.toContain('disabled=""');
    expect(idle).not.toContain('Wait until the run finishes.');
    expect(idle).toContain('value="17"');
  });
});

describe('model menu', () => {
  it('draws the list notice under the refresh row as a muted line with why as its tooltip, never a row to pick', () => {
    const rows: MenuRow[] = [
      { kind: 'action', text: '모델 목록 새로고침', enabled: true },
      { kind: 'note', text: '이전 목록 · 새로 읽지 못했습니다 (5분 전 목록)', help: 'agy models: 종료 코드 1 · Error: authentication required.' },
      { kind: 'separator' },
      { kind: 'header', text: '모델' },
    ];
    const chosen: number[][] = [];
    const html = renderToStaticMarkup(createElement(MenuList, { rows, path: [], choose: (path: number[]) => { chosen.push(path); } }));
    expect(html).toContain('<div class="menu-note" title="agy models: 종료 코드 1 · Error: authentication required.">이전 목록 · 새로 읽지 못했습니다 (5분 전 목록)</div>');
    expect(html.match(/<button/g)?.length).toBe(1);
    // A note an older host sends without its help still reads.
    const bare = renderToStaticMarkup(createElement(MenuList, { rows: [{ kind: 'note', text: '기본 목록 · CLI에서 목록을 읽지 못했습니다' }], path: [], choose: () => {} }));
    expect(bare).toBe('<div class="menu-note">기본 목록 · CLI에서 목록을 읽지 못했습니다</div>');
    expect(chosen).toEqual([]);
  });
});

describe('context ring', () => {
  it('says the rounded, clamped percentage or a dash', () => {
    expect(contextLabel(null)).toBe('–');
    expect(contextLabel(42.4)).toBe('42%');
    expect(contextLabel(140)).toBe('100%');
    expect(contextLabel(Number.NaN)).toBe('–');
  });
});
