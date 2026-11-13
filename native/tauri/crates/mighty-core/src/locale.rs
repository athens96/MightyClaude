//! The shared locale catalogues, `locales/{ko,en,zh,ja}.json` at the repository root, embedded as they are (C# `Locale`). ko and en
//! carry every key; zh and ja may miss some, which then read en, then ko, then the key itself.

use std::collections::HashMap;
use std::sync::OnceLock;

/// The languages the app has a copy of (C# `Locale.Languages`).
pub const LANGUAGES: [&str; 4] = ["ko", "en", "zh", "ja"];

const SOURCES: [&str; 4] = [
    include_str!("../../../../../locales/ko.json"),
    include_str!("../../../../../locales/en.json"),
    include_str!("../../../../../locales/zh.json"),
    include_str!("../../../../../locales/ja.json"),
];

static CATALOGUES: [OnceLock<HashMap<String, String>>; 4] = [OnceLock::new(), OnceLock::new(), OnceLock::new(), OnceLock::new()];

fn index(language: &str) -> Option<usize> {
    LANGUAGES.iter().position(|l| *l == language)
}

/// The catalogue of one of [`LANGUAGES`], parsed once; empty for any other name or a file that does not parse.
pub fn catalogue(language: &str) -> &'static HashMap<String, String> {
    static EMPTY: OnceLock<HashMap<String, String>> = OnceLock::new();
    match index(language) {
        Some(at) => CATALOGUES[at].get_or_init(|| serde_json::from_str(SOURCES[at]).unwrap_or_default()),
        None => EMPTY.get_or_init(HashMap::new),
    }
}

/// The language an OS language tag reads, or None when the app has no copy of it (C# `Locale.LanguageForTag`): every Chinese tag
/// reads the Simplified copy.
pub fn language_for_tag(tag: &str) -> Option<&'static str> {
    let primary = tag.split(['-', '_']).next().unwrap_or("").to_ascii_lowercase();
    LANGUAGES.iter().copied().find(|l| *l == primary)
}

/// What "system" reads (C# `Locale.ResolvedSystem`): the first preferred language the app has a catalogue with keys for, else English.
pub fn resolved_system<S: AsRef<str>>(preferred: &[S]) -> &'static str {
    preferred.iter().filter_map(|tag| language_for_tag(tag.as_ref())).find(|language| !catalogue(language).is_empty()).unwrap_or("en")
}

/// The language to read (C# `Locale.ChosenLanguage`): the saved preference when it names a language, else for "system" the OS's
/// languages in order — `os_languages` is the display language first, then the user's further preferred languages.
pub fn chosen_language<S: AsRef<str>>(preference: &str, os_languages: &[S]) -> &'static str {
    LANGUAGES.iter().copied().find(|l| *l == preference).unwrap_or_else(|| resolved_system(os_languages))
}

/// Lookups in one language (C# `Locale.Get` with `Locale.LanguagePreference` resolved to `language`).
#[derive(Debug, Clone, Copy)]
pub struct Locale {
    language: &'static str,
}

impl Locale {
    pub fn new(language: &str) -> Self {
        Locale { language: LANGUAGES.iter().copied().find(|l| *l == language).unwrap_or("en") }
    }

    pub fn language(&self) -> &'static str {
        self.language
    }

    /// The catalogues a lookup reads, in order (C# `Locale.LookupOrder`).
    pub fn lookup_order(&self) -> &'static [&'static str] {
        match self.language {
            "ko" => &["ko"],
            "en" => &["en", "ko"],
            "zh" => &["zh", "en", "ko"],
            _ => &["ja", "en", "ko"],
        }
    }

    pub fn get(&self, key: &str) -> String {
        self.lookup_order().iter().find_map(|language| catalogue(language).get(key)).cloned().unwrap_or_else(|| key.to_string())
    }

    /// [`Locale::get`] with each `{name}` replaced.
    pub fn get_with(&self, key: &str, subs: &[(&str, &str)]) -> String {
        let mut value = self.get(key);
        for (name, replacement) in subs {
            value = value.replace(&format!("{{{name}}}"), replacement);
        }
        value
    }
}

/// Every key of the catalogues (ko and en carry them all).
pub fn keys() -> std::collections::HashSet<&'static str> {
    LANGUAGES.iter().flat_map(|l| catalogue(l).keys().map(String::as_str)).collect()
}

/// C# `LocaleKeyLeak.Detect`: the visible strings whose trimmed text is exactly a locale key — a lookup that fell back to its key
/// instead of a translation. Trimmed of ASCII white space, U+00A0, U+200B and U+FEFF; empty strings are never flagged.
pub fn key_leaks<'a>(visible: impl IntoIterator<Item = &'a str>, keys: &std::collections::HashSet<&str>) -> Vec<&'a str> {
    const TRIM: [char; 9] = [' ', '\t', '\n', '\r', '\u{c}', '\u{b}', '\u{a0}', '\u{200b}', '\u{feff}'];
    visible
        .into_iter()
        .filter(|s| {
            let trimmed = s.trim_matches(TRIM);
            !trimmed.is_empty() && keys.contains(trimmed)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn key_leaks_are_exact_keys_only() {
        let keys = keys();
        let found = key_leaks(["session.state.running", " session.state.idle\u{a0}", "file.name.json", "", "Running"], &keys);
        assert_eq!(found, vec!["session.state.running", " session.state.idle\u{a0}"]);
    }

    #[test]
    fn every_catalogue_parses() {
        for language in LANGUAGES {
            assert!(catalogue(language).len() > 1000, "{language} has too few keys");
        }
    }

    #[test]
    fn system_follows_the_os_order() {
        assert_eq!(chosen_language("system", &["fr-FR", "ja-JP", "en-US"]), "ja");
        assert_eq!(chosen_language("system", &["zh-Hant-TW"]), "zh");
        assert_eq!(chosen_language("system", &["fr-FR"]), "en");
        assert_eq!(chosen_language::<&str>("system", &[]), "en");
        assert_eq!(chosen_language("ko", &["en-US"]), "ko");
        assert_eq!(chosen_language("bogus", &["ko-KR"]), "ko");
    }

    #[test]
    fn lookups_fall_back_and_substitute() {
        let en = Locale::new("en");
        assert_eq!(en.get("session.state.running"), "Running");
        assert_eq!(en.get_with("window.status.paneCount", &[("count", "3")]), "3 panes");
        assert_eq!(en.get("no.such.key"), "no.such.key");
    }
}
