//! The app's copy as C# `Locale.Get` reads it: one language for the process, chosen by the host ([`set_language`]); by default the
//! OS's preferred languages, as C#'s `LanguagePreference = "system"` reads them. The checks pin Korean, as C#'s `Verification` does.

use std::sync::RwLock;

use mighty_core::locale::{self, Locale};

static LANGUAGE: RwLock<Option<&'static str>> = RwLock::new(None);
/// `CultureInfo.CurrentCulture`'s group separator and sizes, as the host read them from the OS's regional format.
static NUMBER_GROUPING: RwLock<Option<(String, Vec<usize>)>> = RwLock::new(None);

/// The number format `{value:N0}` writes (C#'s current culture: the user's regional format, never the app's language), read by the
/// host from the OS. Until it is set, a comma every three digits.
pub fn set_number_grouping(separator: &str, sizes: &[usize]) {
    if let Ok(mut current) = NUMBER_GROUPING.write() {
        *current = Some((separator.to_string(), sizes.to_vec()));
    }
}

/// `value.ToString("N0")` in the current culture (`$"{value:N0}"`).
pub fn n0(value: i64) -> String {
    match NUMBER_GROUPING.read().ok().and_then(|current| current.clone()) {
        Some((separator, sizes)) => crate::compaction::grouped_with(value, &separator, &sizes),
        None => crate::compaction::grouped(value),
    }
}

/// The language every lookup reads (one of `ko`, `en`, `zh`, `ja`; anything else reads English).
pub fn set_language(language: &str) {
    let chosen = Locale::new(language).language();
    if let Ok(mut current) = LANGUAGE.write() {
        *current = Some(chosen);
    }
}

pub fn locale() -> Locale {
    if let Ok(current) = LANGUAGE.read()
        && let Some(language) = *current
    {
        return Locale::new(language);
    }
    let preferred: Vec<String> = sys_locale::get_locales().collect();
    let system = locale::chosen_language("system", &preferred);
    // The OS's languages only while nothing chose one: a language set meanwhile (the host's, a check's) is never overwritten.
    match LANGUAGE.write() {
        Ok(mut current) => Locale::new(current.get_or_insert(system)),
        Err(_) => Locale::new(system),
    }
}

/// `Locale.Get(key)`.
pub fn get(key: &str) -> String {
    locale().get(key)
}

/// `Locale.Get(key, substitutions)`.
pub fn get_with(key: &str, subs: &[(&str, &str)]) -> String {
    locale().get_with(key, subs)
}

/// `ProviderMark.Label`.
pub fn provider_label(provider: &str) -> &'static str {
    match provider {
        "codex" => "Codex",
        "gemini" => "Antigravity",
        "pi" => "pi",
        "opencode" => "opencode",
        "hermes" => "Hermes",
        _ => "Claude",
    }
}
