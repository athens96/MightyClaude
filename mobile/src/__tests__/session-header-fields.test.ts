import type { MobileSettings } from '@/api/types';
import { settingFields } from '@/components/session-header';

function settings(overrides: Partial<MobileSettings> = {}): MobileSettings {
  return {
    editable: true,
    model: 'claude-opus-5-5',
    permissionMode: 'auto',
    effort: 'high',
    agentViewMode: 'cli',
    mightyStyle: 'ouroboros',
    styleId: 'superpowers',
    options: {
      models: [{ id: 'claude-opus-5-5', label: 'Opus 5.5' }],
      permissionModes: [{ id: 'auto', label: 'Auto' }],
      efforts: [{ id: 'high', label: 'High' }],
      mightyStyles: [{ id: 'ouroboros', label: 'Ouroboros' }],
      styles: [{ id: 'superpowers', label: 'Superpowers', source: 'bundled' }],
    },
    ...overrides,
  };
}

describe('settingFields', () => {
  it('is empty without settings', () => {
    expect(settingFields(undefined, false)).toEqual([]);
  });

  it('lists every field with options and a value, in chip order', () => {
    expect(settingFields(settings(), false)).toEqual([
      'model',
      'permissionMode',
      'effort',
      'agentViewMode',
      'mightyStyle',
    ]);
  });

  it('uses the open style list on a host with "style"', () => {
    expect(settingFields(settings(), true)).toEqual(['model', 'permissionMode', 'effort', 'agentViewMode', 'styleId']);
  });

  it('leaves out a field with no value or no options', () => {
    const base = settings();
    expect(settingFields(settings({ effort: '' }), false)).not.toContain('effort');
    expect(
      settingFields(settings({ options: { ...base.options, models: [] } }), false),
    ).not.toContain('model');
    expect(settingFields(settings({ styleId: undefined }), true)).not.toContain('styleId');
  });
});
