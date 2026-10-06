import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { HELP_SITE_BASE, helpSiteUrl } from '@/lib/help-site';
import { resetLanguage, type Language } from '@/lib/i18n';

/**
 * The same file `HelpSiteTests.swift` and Windows' `HelpSiteVerification` read: every app
 * opens the user guide at the same address in its display language.
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/help-site.json');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  base: string;
  cases: { language: Language; url: string }[];
};

describe('help site', () => {
  afterEach(() => resetLanguage());

  it('shares the base with the desktop apps', () => {
    expect(HELP_SITE_BASE).toBe(fixture.base);
    expect(fixture.cases.map((entry) => entry.language)).toEqual(['ko', 'en', 'zh', 'ja']);
  });

  it.each(fixture.cases.map((entry) => [entry.language, entry.url] as const))(
    'opens the %s guide at %s',
    (language, url) => {
      expect(helpSiteUrl(language)).toBe(url);
    },
  );

  it.each(fixture.cases.map((entry) => [entry.language, entry.url] as const))(
    'follows the phone language %s',
    (language, url) => {
      resetLanguage(language);
      expect(helpSiteUrl()).toBe(url);
    },
  );
});
