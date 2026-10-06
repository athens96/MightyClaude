import ko from '@/locales/ko.json';
import en from '@/locales/en.json';
import zh from '@/locales/zh.json';
import ja from '@/locales/ja.json';
import { chooseTemplate, lookupOrder, resetLanguage, resolvedSystem, t } from '@/lib/i18n';

const anyCatalog = (): boolean => true;
const languageFor = (locales: string | readonly string[] | undefined) => resolvedSystem(locales, anyCatalog);

const catalogs: Record<string, Record<string, string>> = { ko, en, zh, ja };

function placeholders(value: string): string[] {
  return [...value.matchAll(/\{([A-Za-z][A-Za-z0-9]*)\}/g)].map((match) => match[1] ?? '').sort();
}

afterEach(() => {
  resetLanguage();
});

describe('resolvedSystem', () => {
  it('reads Korean only for a Korean OS language', () => {
    expect(languageFor('ko')).toBe('ko');
    expect(languageFor('ko-KR')).toBe('ko');
    expect(languageFor('ko_KR')).toBe('ko');
  });

  it('reads Chinese for every Chinese OS language, Traditional included for now', () => {
    expect(languageFor('zh-Hans')).toBe('zh');
    expect(languageFor('zh-Hans-CN')).toBe('zh');
    expect(languageFor('zh-TW')).toBe('zh');
    expect(languageFor('zh-Hant-HK')).toBe('zh');
  });

  it('reads Japanese for a Japanese OS language', () => {
    expect(languageFor('ja-JP')).toBe('ja');
    expect(languageFor('ja')).toBe('ja');
  });

  it('reads English for every other OS language, and for none at all', () => {
    expect(languageFor('en-US')).toBe('en');
    expect(languageFor('fr-FR')).toBe('en');
    expect(languageFor('kok-IN')).toBe('en');
    expect(languageFor(undefined)).toBe('en');
    expect(languageFor([])).toBe('en');
  });

  it('passes over a language whose catalog has no keys yet', () => {
    const translated = (language: string) => language !== 'zh' && language !== 'ja';
    expect(resolvedSystem(['ja-JP', 'ko-KR'], translated)).toBe('ko');
    expect(resolvedSystem(['zh-Hans-CN', 'ja-JP', 'en-US', 'ko-KR'], translated)).toBe('en');
    expect(resolvedSystem(['zh-Hans-CN', 'fr-FR'], translated)).toBe('en');
  });

  it('passes over the bundled catalogs that have no keys yet', () => {
    // Reads the bundled files, so it holds before and after the translations land.
    const jaHasKeys = Object.keys(catalogs.ja ?? {}).length > 0;
    expect(resolvedSystem(['ja-JP', 'ko-KR'])).toBe(jaHasKeys ? 'ja' : 'ko');
  });

  it('takes the first preferred language the app has', () => {
    expect(languageFor(['ko-KR', 'en-US', 'zh-Hans-CN'])).toBe('ko');
    expect(languageFor(['en-US', 'ko-KR', 'zh-Hans-CN'])).toBe('en');
    expect(languageFor(['zh-Hans-CN', 'ko-KR', 'en-US'])).toBe('zh');
    expect(languageFor(['fr-FR', 'de-DE', 'ja-JP', 'ko-KR'])).toBe('ja');
    expect(languageFor(['fr-FR', 'de-DE'])).toBe('en');
  });
});

describe('t', () => {
  it('answers with the chosen language', () => {
    resetLanguage('ko');
    expect(t('phone.pair.connect')).toBe('연결하기');
    resetLanguage('en');
    expect(t('phone.pair.connect')).toBe('Connect');
  });

  it('fills {name} in both languages', () => {
    resetLanguage('ko');
    expect(t('phone.pair.paired', { name: 'mac' })).toBe('mac 페어링 완료');
    resetLanguage('en');
    expect(t('phone.hosts.remove.message', { name: 'mac' })).toBe('Delete the pairing with mac?');
  });

  it('returns the key itself when no language has it', () => {
    for (const language of ['en', 'ko', 'zh', 'ja'] as const) {
      resetLanguage(language);
      expect(t('phone.nothing.here')).toBe('phone.nothing.here');
    }
  });

  it('reads a translation, else English, and fills {name} in it', () => {
    for (const language of ['zh', 'ja'] as const) {
      resetLanguage(language);
      const own = catalogs[language]?.['phone.pair.connect'];
      expect(t('phone.pair.connect')).toBe(own ?? 'Connect');
      const filled = t('phone.hosts.remove.message', { name: 'mac' });
      expect(filled).toContain('mac');
      expect(filled).not.toContain('{name}');
    }
  });
});

describe('lookupOrder', () => {
  it('keeps ko and en as they were and sends a translation to English, then Korean', () => {
    expect(lookupOrder('ko')).toEqual(['ko']);
    expect(lookupOrder('en')).toEqual(['en', 'ko']);
    expect(lookupOrder('zh')).toEqual(['zh', 'en', 'ko']);
    expect(lookupOrder('ja')).toEqual(['ja', 'en', 'ko']);
  });
});

describe('chooseTemplate', () => {
  const zhCatalog = { both: '中文' };
  const enCatalog = { both: 'English', enOnly: 'English only' };
  const koCatalog = { both: '한국어', enOnly: '영어만', koOnly: '한국어만' };

  it('prefers the chosen language', () => {
    expect(chooseTemplate('both', [zhCatalog, enCatalog, koCatalog])).toBe('中文');
  });

  it('falls back to English, then Korean, when the chosen language has not got the key', () => {
    expect(chooseTemplate('enOnly', [zhCatalog, enCatalog, koCatalog])).toBe('English only');
    expect(chooseTemplate('koOnly', [zhCatalog, enCatalog, koCatalog])).toBe('한국어만');
  });

  it('returns the key itself when no language has it', () => {
    expect(chooseTemplate('none', [zhCatalog, enCatalog, koCatalog])).toBe('none');
  });
});

describe('the bundled copies', () => {
  it('carry the same key set', () => {
    expect(Object.keys(catalogs.ko ?? {}).sort()).toEqual(Object.keys(catalogs.en ?? {}).sort());
  });

  it('carry the same placeholders per key', () => {
    for (const [key, value] of Object.entries(ko as Record<string, string>)) {
      expect(placeholders((en as Record<string, string>)[key] ?? '')).toEqual(placeholders(value));
    }
  });

  it('give the translations only keys English has, with the same placeholders', () => {
    const english = en as Record<string, string>;
    for (const language of ['zh', 'ja']) {
      for (const [key, value] of Object.entries(catalogs[language] ?? {})) {
        expect(english[key]).toBeDefined();
        expect(placeholders(value)).toEqual(placeholders(english[key] ?? ''));
      }
    }
  });
});
