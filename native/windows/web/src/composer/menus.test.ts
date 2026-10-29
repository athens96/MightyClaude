import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { RunSettingsPopover } from './Toolbar';
import { clearUnsupported, contextLabel, unsupportedSettings, type RunSettingsInfo } from './menus';

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

describe('context ring', () => {
  it('says the rounded, clamped percentage or a dash', () => {
    expect(contextLabel(null)).toBe('–');
    expect(contextLabel(42.4)).toBe('42%');
    expect(contextLabel(140)).toBe('100%');
    expect(contextLabel(Number.NaN)).toBe('–');
  });
});
