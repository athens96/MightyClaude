//! The app's copy as C# `Locale.Get` reads it: one language for the process, chosen by the host ([`set_language`]); by default the
//! OS's preferred languages, as C#'s `LanguagePreference = "system"` reads them. The checks pin Korean, as C#'s `Verification` does.

use std::sync::RwLock;

use mighty_core::locale::{self, Locale};

static LANGUAGE: RwLock<Option<&'static str>> = RwLock::new(None);

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
    let language = locale::chosen_language("system", &preferred);
    set_language(language);
    Locale::new(language)
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
