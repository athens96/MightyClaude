import { describe, expect, it } from 'vitest';
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
});

describe('context ring', () => {
  it('says the rounded, clamped percentage or a dash', () => {
    expect(contextLabel(null)).toBe('–');
    expect(contextLabel(42.4)).toBe('42%');
    expect(contextLabel(140)).toBe('100%');
    expect(contextLabel(Number.NaN)).toBe('–');
  });
});
