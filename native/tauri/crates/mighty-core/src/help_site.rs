//! The user guide's address (C# `HelpSite`, MightyCode.Core/HelpSite.cs): one folder per language under one base, pinned by
//! native/contracts/fixtures/help-site.json, which the Mac and the phone are held to as well. Owner: W6-A.

use crate::locale::{LANGUAGES, Locale};

/// `HelpSite.Base`.
pub const BASE: &str = "https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/help";

/// `HelpSite.Url`: the guide's folder in `language`; a language the app has no copy of reads English.
pub fn url(language: &str) -> String {
    let language = if LANGUAGES.contains(&language) { language } else { "en" };
    format!("{BASE}/{language}/")
}

/// `HelpSite.Current`: the guide in the language the app shows now (`Locale.ChosenLanguage`, the host's locale).
pub fn current(locale: &Locale) -> String {
    url(locale.language())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> serde_json::Value {
        serde_json::from_str(include_str!("../../../../contracts/fixtures/help-site.json")).unwrap()
    }

    /// Verification.cs:1317 "help site: every language opens its folder of the shared base in native/contracts/fixtures/help-site.json".
    #[test]
    fn every_language_opens_its_folder_of_the_shared_base() {
        let fixture = fixture();
        assert_eq!(fixture["base"], BASE);
        let cases = fixture["cases"].as_array().unwrap();
        let languages: Vec<&str> = cases.iter().map(|c| c["language"].as_str().unwrap()).collect();
        assert_eq!(languages, LANGUAGES.to_vec(), "the fixture names every app language in order");
        for case in cases {
            assert_eq!(url(case["language"].as_str().unwrap()), case["url"].as_str().unwrap());
        }
        assert_eq!(url("system"), url("en"), "a language the app has no copy of reads the English guide");
    }

    /// Verification.cs:1318 "help site: the current guide follows the chosen language".
    #[test]
    fn the_current_guide_follows_the_chosen_language() {
        for language in LANGUAGES {
            assert_eq!(current(&Locale::new(language)), url(language));
        }
        let system = Locale::new(crate::locale::chosen_language("system", &["fr-FR".to_string()]));
        assert!(LANGUAGES.contains(&system.language()) && current(&system) == url(system.language()));
    }
}
