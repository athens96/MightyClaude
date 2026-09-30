import { resetLanguage, t } from '@/lib/i18n';
import { providerIsBeta, providerLabel } from '@/theme';

describe('beta providers', () => {
  afterEach(() => {
    resetLanguage();
  });

  it('marks Codex and Gemini as beta and keeps Claude official', () => {
    expect(providerIsBeta('claude')).toBe(false);
    expect(providerIsBeta('codex')).toBe(true);
    expect(providerIsBeta('gemini')).toBe(true);
    expect(providerIsBeta('')).toBe(false);
    expect(providerIsBeta('constructor')).toBe(false);
  });

  it('never changes the provider name itself', () => {
    expect(['claude', 'codex', 'gemini'].map(providerLabel)).toEqual(['Claude', 'Codex', 'Gemini']);
  });

  it('reads the badge text from the shared locale key in both languages', () => {
    resetLanguage('ko');
    expect(t('badge.beta')).toBe('베타');
    expect(t('badge.betaAccessibility')).toBe('베타 기능');
    resetLanguage('en');
    expect(t('badge.beta')).toBe('Beta');
    expect(t('badge.betaAccessibility')).toBe('Beta feature');
  });
});
