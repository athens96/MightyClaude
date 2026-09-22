import en from '../locales/en.json';
import ko from '../locales/ko.json';

export type Language = 'ko' | 'en';

type Catalog = Record<string, string>;

// The phone bundles its own copy of the two repo-root locale files;
// scripts/check-locales.js fails when either copy differs from the root.
const catalogs: Record<Language, Catalog> = { en, ko };

/// The phone has no picker (docs/i18n.md): it follows the OS language, and any
/// OS language that is not Korean reads the English draft.
export function languageFor(locale: string | undefined): Language {
  if (!locale) return 'en';
  return /^ko(-|_|$)/i.test(locale) ? 'ko' : 'en';
}

/// Hermes answers with the OS locale through Intl. A runtime without it is not
/// a crash: an unknown OS language is simply not Korean.
export function osLocale(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().locale;
  } catch {
    return undefined;
  }
}

let resolved: Language | undefined;

export function language(): Language {
  if (resolved === undefined) resolved = languageFor(osLocale());
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

/// A key the chosen language has not got yet falls back to the Korean value, and
/// a key neither language has returns the key itself so nothing crashes.
export function chooseTemplate(key: string, chosen: Catalog, korean: Catalog): string {
  return chosen[key] ?? korean[key] ?? key;
}

export function t(key: string, values?: Record<string, string | number>): string {
  return fill(chooseTemplate(key, catalogs[language()], catalogs.ko), values);
}
