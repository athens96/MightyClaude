import { language as currentLanguage, type Language } from '@/lib/i18n';

/**
 * The user guide `scripts/help/build.mjs` publishes, one folder per language
 * (`<base>/<lang>/`). The address is pinned by `native/contracts/fixtures/help-site.json`,
 * which the Mac (`HelpSite.swift`) and Windows (`HelpSite.cs`) are held to as well.
 */
export const HELP_SITE_BASE = 'https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/help';

/** The guide's folder in `language`, by default the language the phone shows. */
export function helpSiteUrl(language: Language = currentLanguage()): string {
  return `${HELP_SITE_BASE}/${language}/`;
}
