import ko from '@/locales/ko.json';
import en from '@/locales/en.json';
import { chooseTemplate, languageFor, resetLanguage, t } from '@/lib/i18n';

const catalogs: Record<string, Record<string, string>> = { ko, en };

function placeholders(value: string): string[] {
  return [...value.matchAll(/\{([A-Za-z][A-Za-z0-9]*)\}/g)].map((match) => match[1] ?? '').sort();
}

afterEach(() => {
  resetLanguage();
});

describe('languageFor', () => {
  it('reads Korean only for a Korean OS language', () => {
    expect(languageFor('ko')).toBe('ko');
    expect(languageFor('ko-KR')).toBe('ko');
    expect(languageFor('ko_KR')).toBe('ko');
  });

  it('reads English for every other OS language, and for none at all', () => {
    expect(languageFor('en-US')).toBe('en');
    expect(languageFor('ja-JP')).toBe('en');
    expect(languageFor('kok-IN')).toBe('en');
    expect(languageFor(undefined)).toBe('en');
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

  it('returns the key itself when neither language has it', () => {
    resetLanguage('en');
    expect(t('phone.nothing.here')).toBe('phone.nothing.here');
    resetLanguage('ko');
    expect(t('phone.nothing.here')).toBe('phone.nothing.here');
  });
});

describe('chooseTemplate', () => {
  it('prefers the chosen language', () => {
    expect(chooseTemplate('k', { k: 'chosen' }, { k: '한국어' })).toBe('chosen');
  });

  it('falls back to the Korean value when the chosen language has not got the key', () => {
    expect(chooseTemplate('k', {}, { k: '한국어' })).toBe('한국어');
  });

  it('returns the key itself when neither language has it', () => {
    expect(chooseTemplate('k', {}, {})).toBe('k');
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
});
