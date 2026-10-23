import { describe, expect, it } from 'vitest';
import en from '@locales/en.json';
import ko from '@locales/ko.json';
import { format, loadTranslator, lookupOrder, translator } from './locale';

describe('locale', () => {
  it('reads the fallbacks Locale.cs reads', () => {
    expect(lookupOrder('ko')).toEqual(['ko']);
    expect(lookupOrder('en')).toEqual(['en', 'ko']);
    expect(lookupOrder('ja')).toEqual(['ja', 'en', 'ko']);
    expect(lookupOrder('fr')).toEqual(['en', 'ko']);
  });

  it('replaces every {name}', () => {
    expect(format('{a} and {a} with {b}', { a: 'x', b: 2 })).toBe('x and x with 2');
    expect(format('{a}')).toBe('{a}');
  });

  it('falls back through the catalogues, then to the key', () => {
    const t = translator([{ only: 'first' }, { only: 'second', other: 'Hello {name}' }]);
    expect(t('only')).toBe('first');
    expect(t('other', { name: 'Kim' })).toBe('Hello Kim');
    expect(t('missing.key')).toBe('missing.key');
  });

  it('loads the repo catalogues', async () => {
    const t = await loadTranslator('en');
    expect(t('sidebar.workspacesHeader')).toBe((en as Record<string, string>)['sidebar.workspacesHeader']);
    const k = await loadTranslator('ko');
    expect(k('sidebar.workspacesHeader')).toBe((ko as Record<string, string>)['sidebar.workspacesHeader']);
  });
});
