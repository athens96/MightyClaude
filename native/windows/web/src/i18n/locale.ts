// The app's words from the repo's locales/{ko,en,ja,zh}.json, read the way MightyClaude.Core/Locale.cs reads them: the
// chosen language, then its fallbacks (ko alone; en then ko; any other then en then ko), then the key itself; `{name}`
// placeholders replaced from the substitutions. Each language is its own chunk, loaded only when a lookup order needs it.

export type Language = 'ko' | 'en' | 'zh' | 'ja';
export type Catalogue = Readonly<Record<string, string>>;

export const languages: readonly Language[] = ['ko', 'en', 'zh', 'ja'];

export function isLanguage(value: string): value is Language {
  return (languages as readonly string[]).includes(value);
}

/** Locale.LookupOrder: the catalogues a lookup reads, in order, before it gives back the key. */
export function lookupOrder(language: string): Language[] {
  if (language === 'ko') return ['ko'];
  if (language === 'en' || !isLanguage(language)) return ['en', 'ko'];
  return [language, 'en', 'ko'];
}

/** Locale.Get's substitution: every `{name}` replaced by its value. */
export function format(template: string, subs?: Readonly<Record<string, string | number>>): string {
  if (!subs) return template;
  let value = template;
  for (const [name, replacement] of Object.entries(subs)) value = value.split('{' + name + '}').join(String(replacement));
  return value;
}

export type Translate = (key: string, subs?: Readonly<Record<string, string | number>>) => string;

/** A lookup over catalogues already loaded, in lookup order. */
export function translator(catalogues: readonly Catalogue[]): Translate {
  return (key, subs) => {
    for (const catalogue of catalogues) {
      const value = catalogue[key];
      if (value !== undefined) return format(value, subs);
    }
    return format(key, subs);
  };
}

const loaders: Record<Language, () => Promise<{ default: Catalogue }>> = {
  ko: () => import('@locales/ko.json'),
  en: () => import('@locales/en.json'),
  zh: () => import('@locales/zh.json'),
  ja: () => import('@locales/ja.json'),
};

/** Loads the catalogues `language` reads and gives back its lookup. */
export async function loadTranslator(language: string): Promise<Translate> {
  const catalogues = await Promise.all(lookupOrder(language).map(async (name) => (await loaders[name]()).default));
  return translator(catalogues);
}
