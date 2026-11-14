//! Settings › 앱 업데이트's words and what the box shows for an update's state (C# `AppUpdateStrings`, MightyCode.Core/AppUpdateStrings.cs;
//! `AppUpdatePresentation.Describe`, AppUpdateCoordinator.cs:86-134). The updates themselves are W9's; a build without the update's
//! public key (a development build) shows the box disabled with its notice. Owner: W6-A.

use chrono::{DateTime, Utc};

use crate::locale::Locale;

/// `AppUpdateStrings`: each field's locale key, in C#'s order (every key under `settings.appUpdate.`).
pub const KEYS: [(&str, &str); 25] = [
    ("SectionTitle", "settings.appUpdate.sectionTitle"),
    ("CurrentVersionLabel", "settings.appUpdate.currentVersionLabel"),
    ("ManifestUrlPlaceholder", "settings.appUpdate.manifestUrlPlaceholder"),
    ("BuiltInAddressTemplate", "settings.appUpdate.builtInAddressTemplate"),
    ("ManifestUrlHint", "settings.appUpdate.manifestUrlHintWindows"),
    ("AutoUpdateToggle", "settings.appUpdate.autoUpdateToggle"),
    ("AutoUpdateDescription", "settings.appUpdate.autoUpdateDescription"),
    ("SignatureVerified", "settings.appUpdate.signatureVerified"),
    ("NotCheckedYet", "settings.appUpdate.notCheckedYet"),
    ("LastCheckedTemplate", "settings.appUpdate.lastCheckedTemplate"),
    ("Checking", "settings.appUpdate.checking"),
    ("UpToDate", "settings.appUpdate.upToDate"),
    ("AvailableTemplate", "settings.appUpdate.availableTemplate"),
    ("DownloadingTemplate", "settings.appUpdate.downloadingTemplate"),
    ("StagingProgress", "settings.appUpdate.stagingProgress"),
    ("StagedTemplate", "settings.appUpdate.stagedTemplate"),
    ("ReadyTemplate", "settings.appUpdate.readyTemplate"),
    ("Installing", "settings.appUpdate.installing"),
    ("CheckButton", "settings.appUpdate.checkButton"),
    ("DownloadButton", "settings.appUpdate.downloadButton"),
    ("CancelButton", "settings.appUpdate.cancelButton"),
    ("RestartNowButton", "settings.appUpdate.restartNowButton"),
    ("InProgressButton", "settings.appUpdate.inProgressButton"),
    ("RetryNotice", "settings.appUpdate.retryNotice"),
    ("NoPublicKeyNotice", "settings.appUpdate.noPublicKeyNoticeWindows"),
];

/// The words of one field in a language.
pub fn text(locale: &Locale, field: &str) -> String {
    KEYS.iter().find(|(name, _)| *name == field).map(|(_, key)| locale.get(key)).unwrap_or_default()
}

/// An update's phase (C# `AppUpdatePhase`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Phase {
    #[default]
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Staging,
    Ready,
    Installing,
    Failed,
}

/// What `Describe` reads of the update's state (C# `AppUpdateState`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct State {
    pub phase: Phase,
    /// The available manifest's version and notes.
    pub version: Option<String>,
    pub notes: Option<String>,
    pub download_fraction: f64,
    pub checked_at: Option<DateTime<Utc>>,
    pub error_message: Option<String>,
    pub last_error: Option<String>,
}

/// What the box shows (C# `AppUpdateSectionView`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SectionView {
    pub section_enabled: bool,
    pub signature_notice: String,
    pub built_in_address_line: Option<String>,
    pub address_field_enabled: bool,
    pub status_text: String,
    pub button_label: String,
    pub button_enabled: bool,
    pub notes: Option<String>,
    pub kept_error: Option<String>,
    pub retry_notice: Option<String>,
}

/// `AppUpdatePresentation.Describe`.
pub fn describe(
    locale: &Locale,
    state: &State,
    has_public_key: bool,
    built_in: Option<&str>,
    format_time: impl Fn(&DateTime<Utc>) -> String,
    automatic: bool,
) -> SectionView {
    let t = |field: &str| text(locale, field);
    if !has_public_key {
        return SectionView {
            section_enabled: false,
            signature_notice: t("NoPublicKeyNotice"),
            built_in_address_line: None,
            address_field_enabled: false,
            status_text: t("NoPublicKeyNotice"),
            button_label: t("CheckButton"),
            button_enabled: false,
            notes: None,
            kept_error: None,
            retry_notice: None,
        };
    }
    let version = state.version.clone().unwrap_or_default();
    let with = |field: &str, name: &str, value: &str| t(field).replace(&format!("{{{name}}}"), value);
    let status_text = match state.phase {
        Phase::Checking => t("Checking"),
        Phase::UpToDate => t("UpToDate"),
        Phase::Available => with("AvailableTemplate", "version", &version),
        Phase::Downloading => with("DownloadingTemplate", "percent", &((state.download_fraction * 100.0) as i64).to_string()),
        Phase::Staging => t("StagingProgress"),
        Phase::Ready => with(if automatic { "StagedTemplate" } else { "ReadyTemplate" }, "version", &version),
        Phase::Installing => t("Installing"),
        Phase::Failed => state.error_message.clone().unwrap_or_else(|| t("NotCheckedYet")),
        Phase::Idle => match &state.checked_at {
            Some(at) => with("LastCheckedTemplate", "time", &format_time(at)),
            None => t("NotCheckedYet"),
        },
    };
    let (button_label, button_enabled) = match state.phase {
        Phase::Available => (t("DownloadButton"), true),
        Phase::Downloading => (t("CancelButton"), true),
        Phase::Ready => (t("RestartNowButton"), true),
        Phase::Checking | Phase::Staging | Phase::Installing => (t("InProgressButton"), false),
        _ => (t("CheckButton"), true),
    };
    let notes = matches!(state.phase, Phase::Available | Phase::Ready).then(|| state.notes.clone()).flatten().filter(|n| !n.is_empty());
    let failed = state.last_error.is_some() && (state.phase == Phase::Ready || automatic);
    SectionView {
        section_enabled: true,
        signature_notice: t("SignatureVerified"),
        built_in_address_line: built_in.map(|address| with("BuiltInAddressTemplate", "address", address)),
        address_field_enabled: built_in.is_none(),
        status_text,
        button_label,
        button_enabled,
        notes,
        kept_error: (failed && state.phase == Phase::Ready).then(|| state.last_error.clone()).flatten(),
        retry_notice: (failed && automatic).then(|| t("RetryNotice")),
    }
}

/// StringsVerification's `Validate` for a table of field → words: each non-empty, none repeating another, its `{name}` placeholders
/// well-formed (an ASCII letter, then letters, digits or `_`; no stray brace), and exactly the expected fields with the expected words.
pub fn validate(actual: &[(&str, String)], expected: &[(&str, String)]) -> Result<(), String> {
    let mut seen = std::collections::HashSet::new();
    for (field, value) in actual {
        if value.is_empty() {
            return Err(format!("{field} is empty"));
        }
        if !seen.insert(value.clone()) {
            return Err(format!("{field} repeats another value"));
        }
        let mut rest = value.as_str();
        while let Some(open) = rest.find(['{', '}']) {
            if rest.as_bytes()[open] == b'}' {
                return Err(format!("{field} has a stray brace"));
            }
            let after = &rest[open + 1..];
            let close = after.find('}').ok_or_else(|| format!("{field} has an unclosed placeholder"))?;
            let name = &after[..close];
            let good =
                name.chars().next().is_some_and(|c| c.is_ascii_alphabetic()) && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_');
            if !good {
                return Err(format!("{field} has a bad placeholder {{{name}}}"));
            }
            rest = &after[close + 1..];
        }
        match expected.iter().find(|(name, _)| name == field) {
            None => return Err(format!("{field} is not in the table")),
            Some((_, words)) if words != value => return Err(format!("{field}: expected {words}, got {value}")),
            Some(_) => {}
        }
    }
    match expected.iter().find(|(name, _)| !actual.iter().any(|(field, _)| field == name)) {
        Some((missing, _)) => Err(format!("{missing} is missing")),
        None => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ko_table() -> Vec<(&'static str, String)> {
        let catalogue: serde_json::Value = serde_json::from_str(include_str!("../../../../../locales/ko.json")).unwrap();
        KEYS.iter().map(|(field, key)| (*field, catalogue[*key].as_str().unwrap_or_default().to_string())).collect()
    }

    /// Verification.cs:1433 "app update strings match macOS" (StringsVerification `AppUpdateStringsMatchMacOS`).
    #[test]
    fn app_update_strings_match_macos() {
        let ko = Locale::new("ko");
        let actual: Vec<(&str, String)> = KEYS.iter().map(|(field, _)| (*field, text(&ko, field))).collect();
        let expected = ko_table();
        validate(&actual, &expected).unwrap();
        let broken = |field: &str, value: Option<&str>| -> Vec<(&str, String)> {
            actual
                .iter()
                .filter(|(name, _)| value.is_some() || *name != field)
                .map(|(name, words)| (*name, if *name == field { value.unwrap_or_default().to_string() } else { words.clone() }))
                .collect()
        };
        let up_to_date = text(&ko, "UpToDate");
        assert!(validate(&broken("SectionTitle", Some("")), &expected).is_err(), "empty");
        assert!(validate(&broken("SectionTitle", Some(&up_to_date)), &expected).is_err(), "a duplicate");
        assert!(validate(&broken("AvailableTemplate", Some("새 버전 { version } 이 있습니다.")), &expected).is_err(), "a bad placeholder");
        assert!(validate(&broken("CheckButton", Some("Check for Updates")), &expected).is_err(), "English words");
        assert!(validate(&broken("Installing", None), &expected).is_err(), "a missing field");
    }

    /// `Describe` without the public key: the box disabled with the notice, whatever the state.
    #[test]
    fn a_build_without_the_key_shows_the_notice_and_a_disabled_check() {
        let ko = Locale::new("ko");
        let view = describe(&ko, &State { phase: Phase::Available, ..State::default() }, false, None, |_| String::new(), true);
        assert!(!view.section_enabled && !view.button_enabled && !view.address_field_enabled);
        assert_eq!(view.status_text, text(&ko, "NoPublicKeyNotice"));
        assert_eq!(view.signature_notice, text(&ko, "NoPublicKeyNotice"));
        assert_eq!(view.button_label, text(&ko, "CheckButton"));
        assert_eq!((view.notes, view.kept_error, view.retry_notice, view.built_in_address_line), (None, None, None, None));
    }

    /// `Describe` with the key: each phase's words and button, the notes of an available or ready update, a kept error and the retry.
    #[test]
    fn each_phase_has_its_words_and_button() {
        let en = Locale::new("en");
        let at = |phase| State { phase, version: Some("0.5.0".into()), notes: Some("Notes".into()), ..State::default() };
        let view = describe(&en, &at(Phase::Available), true, Some("https://x"), |_| String::new(), true);
        assert_eq!(view.status_text, text(&en, "AvailableTemplate").replace("{version}", "0.5.0"));
        assert_eq!((view.button_label.as_str(), view.button_enabled), (text(&en, "DownloadButton").as_str(), true));
        assert_eq!(view.notes.as_deref(), Some("Notes"));
        assert!(!view.address_field_enabled && view.built_in_address_line.is_some());
        let downloading = State { download_fraction: 0.456, ..at(Phase::Downloading) };
        assert_eq!(
            describe(&en, &downloading, true, None, |_| String::new(), true).status_text,
            text(&en, "DownloadingTemplate").replace("{percent}", "45")
        );
        let checking = describe(&en, &at(Phase::Checking), true, None, |_| String::new(), true);
        assert!(!checking.button_enabled && checking.notes.is_none());
        let ready = State { last_error: Some("disk full".into()), ..at(Phase::Ready) };
        let view = describe(&en, &ready, true, None, |_| String::new(), false);
        assert_eq!(view.status_text, text(&en, "ReadyTemplate").replace("{version}", "0.5.0"));
        assert_eq!((view.kept_error.as_deref(), view.retry_notice), (Some("disk full"), None));
        let idle = State { checked_at: Some(DateTime::<Utc>::UNIX_EPOCH), ..State::default() };
        assert_eq!(
            describe(&en, &idle, true, None, |_| "09:30".into(), true).status_text,
            text(&en, "LastCheckedTemplate").replace("{time}", "09:30")
        );
        assert_eq!(describe(&en, &State::default(), true, None, |_| String::new(), true).status_text, text(&en, "NotCheckedYet"));
    }
}
