import en from '../locales/en.json';
import ja from '../locales/ja.json';
import ko from '../locales/ko.json';
import zh from '../locales/zh.json';

export type Language = 'ko' | 'en' | 'zh' | 'ja';

type Catalog = Record<string, string>;

// The phone bundles its own copy of the repo-root locale files;
// scripts/check-locales.js fails when any copy differs from the root.
// ko and en carry every key; zh and ja are translations that may miss some.
const catalogs: Record<Language, Catalog> = { en, ko, zh, ja };

const SUPPORTED: readonly Language[] = ['ko', 'en', 'zh', 'ja'];

/// The language one OS language tag reads, or undefined when the app has no copy
/// of it. Every Chinese tag reads the Simplified copy for now, zh-Hant, zh-TW and
/// zh-HK included.
export function languageForTag(tag: string | undefined): Language | undefined {
  const primary = (tag ?? '').split(/[-_]/)[0]?.toLowerCase();
  return SUPPORTED.find((language) => language === primary);
}

/// A language whose bundled catalog holds at least one key.
function hasKeys(language: Language): boolean {
  return Object.keys(catalogs[language]).length > 0;
}

/// What the phone reads (it has no picker, docs/i18n.md: it follows the OS
/// language). Given the OS's preferred languages in order, the first one the app
/// has a catalog with keys for wins, so an untranslated catalog is passed over for
/// the next preferred language; English when none qualifies.
export function resolvedSystem(
  locales: string | readonly string[] | undefined,
  usable: (language: Language) => boolean = hasKeys,
): Language {
  const list = typeof locales === 'string' ? [locales] : (locales ?? []);
  for (const tag of list) {
    const language = languageForTag(tag);
    if (language && usable(language)) return language;
  }
  return 'en';
}

/// Hermes answers with the OS locale through Intl, which names the most preferred
/// language only. A runtime without it is not a crash: an unknown OS language
/// reads English.
export function osLocale(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().locale;
  } catch {
    return undefined;
  }
}

let resolved: Language | undefined;

export function language(): Language {
  if (resolved === undefined) resolved = resolvedSystem(osLocale());
  return resolved;
}

/// Only the tests reach for this: on the phone the language is read once per
/// start, because a change of language takes effect on the next start.
export function resetLanguage(override?: Language): void {
  resolved = override;
}

function fill(template: string, values?: Record<string, string | number>): string {
  if (!values) return template;
  return template.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/g, (whole: string, name: string) =>
    name in values ? String(values[name]) : whole,
  );
}

/// The catalogs a lookup reads, in order, before it gives back the key itself.
/// ko and en keep their own rule (ko alone; en then ko); a translation falls
/// back to en, then ko.
export function lookupOrder(language: Language): Language[] {
  if (language === 'ko') return ['ko'];
  if (language === 'en') return ['en', 'ko'];
  return [language, 'en', 'ko'];
}

/// The first catalog that has the key wins, and a key none has returns the key
/// itself so nothing crashes.
export function chooseTemplate(key: string, order: readonly Catalog[]): string {
  for (const catalog of order) {
    const value = catalog[key];
    if (value !== undefined) return value;
  }
  return key;
}

export function t(key: string, values?: Record<string, string | number>): string {
  return fill(
    chooseTemplate(key, lookupOrder(language()).map((each) => catalogs[each])),
    values,
  );
}
