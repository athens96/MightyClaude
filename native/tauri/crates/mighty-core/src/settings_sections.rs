//! Settings' boxes in the Mac's order and its categories (C# `SettingsSections`, `SettingsSectionsSmoke`, MightyCode.Core/
//! SettingsSections.cs; `SettingsNavigation`, `SettingsCategory`, SettingsNavigation.cs). A slot without a Windows title is a
//! feature Windows does not have yet and is not shown; a category is available when one of its slots is. Owner: W6-A.

use crate::locale::Locale;

pub const DISPLAY: &str = "display";
pub const PHASE_MODELS: &str = "phaseModels";
pub const STYLES: &str = "styles";
pub const COMPONENTS: &str = "components";
pub const MOBILE_REMOTE: &str = "mobileRemote";
pub const REMOTE_WORKSPACES: &str = "remoteWorkspaces";
pub const CLI_UPDATE: &str = "cliUpdate";
pub const COMPANION: &str = "companion";
pub const PROVIDERS: &str = "providers";
pub const CLI_ACCOUNTS: &str = "cliAccounts";
pub const CLAUDE_MODS: &str = "claudeMods";
pub const APP_UPDATE: &str = "appUpdate";
pub const APP_INFO: &str = "appInfo";

/// One slot (C# `SettingsSectionSlot`): its id and the heading Windows draws, none for a feature not on Windows yet.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Slot {
    pub id: &'static str,
    pub windows_title: Option<String>,
}

impl Slot {
    pub fn on_windows(&self) -> bool {
        self.windows_title.is_some()
    }
}

/// `MacOrder`: every macOS slot in the Mac's order, with its Windows heading.
pub fn mac_order(locale: &Locale) -> Vec<Slot> {
    let slot = |id, title: String| Slot { id, windows_title: Some(title) };
    vec![
        slot(DISPLAY, locale.get("settings.display.sectionTitle")),
        slot(PHASE_MODELS, locale.get("settings.phaseModels.sectionTitle")),
        slot(STYLES, locale.get("settings.styles.sectionTitle")),
        slot(COMPONENTS, locale.get("settings.components.sectionTitle")),
        slot(MOBILE_REMOTE, locale.get("settings.mobileRemote.sectionTitle")),
        slot(REMOTE_WORKSPACES, locale.get("settings.remote.navTitle")),
        slot(CLI_UPDATE, locale.get("settings.cliUpdate.sectionTitle")),
        slot(COMPANION, locale.get("companion.settings.sectionTitle")),
        slot(PROVIDERS, locale.get("settings.providers.sectionTitleWindows")),
        slot(CLI_ACCOUNTS, locale.get("settings.cliAccounts.sectionTitle")),
        slot(CLAUDE_MODS, "Claude Mods".into()),
        slot(APP_UPDATE, locale.get("settings.appUpdate.sectionTitle")),
        slot(APP_INFO, locale.get("settings.appInfo.sectionTitle")),
    ]
}

/// `Windows`: the slots Windows shows, in the Mac's order.
pub fn windows(locale: &Locale) -> Vec<Slot> {
    mac_order(locale).into_iter().filter(Slot::on_windows).collect()
}

/// `WindowsTitles`: the headings Windows draws, in order.
pub fn windows_titles(locale: &Locale) -> Vec<String> {
    windows(locale).into_iter().filter_map(|slot| slot.windows_title).collect()
}

/// The heading of a registered slot (`SettingsSections.Windows.First(slot => slot.Id == id).WindowsTitle`).
pub fn title_of(locale: &Locale, id: &str) -> Option<String> {
    windows(locale).into_iter().find(|slot| slot.id == id).and_then(|slot| slot.windows_title)
}

/// One category of the side list (C# `SettingsCategory`): the Mac's `SettingsPane`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Category {
    pub id: &'static str,
    pub title: String,
    pub sections: Vec<&'static str>,
}

/// `SettingsNavigation.Categories`.
pub fn categories(locale: &Locale) -> Vec<Category> {
    let category = |id, key: &str, sections: Vec<&'static str>| Category { id, title: locale.get(key), sections };
    vec![
        category("general", "settings.nav.general", vec![DISPLAY]),
        category("models", "settings.nav.models", vec![PHASE_MODELS]),
        category("styles", "settings.nav.styles", vec![STYLES]),
        category("tools", "settings.nav.tools", vec![COMPONENTS]),
        category("cli", "settings.nav.cli", vec![CLI_UPDATE, CLI_ACCOUNTS, PROVIDERS, CLAUDE_MODS]),
        category("mobile", "settings.nav.mobile", vec![MOBILE_REMOTE]),
        category("remote", "settings.remote.navTitle", vec![REMOTE_WORKSPACES]),
        category("companion", "settings.nav.companion", vec![COMPANION]),
        category("about", "settings.nav.about", vec![APP_UPDATE, APP_INFO]),
    ]
}

/// `SettingsNavigation.Available`: the categories one of whose slots Windows shows.
pub fn available(locale: &Locale) -> Vec<Category> {
    let shown = windows(locale);
    categories(locale).into_iter().filter(|c| c.sections.iter().any(|id| shown.iter().any(|slot| slot.id == *id))).collect()
}

/// What the smoke records under `settingsSections` (C# `SettingsSectionsSmokeOutcome`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SmokeOutcome {
    pub sections: Vec<String>,
    pub cli_update_statuses: Vec<String>,
    pub toggled_to: bool,
    pub restored: bool,
}

/// C# `SettingsSectionsSmoke.RunAsync`: the headings built must be the registration's, the CLI update rows shown must be the fixture's
/// statuses (`expected_statuses`, W6-D's `updates::section_fixture_statuses`), and the auto-update switch is flipped, read back and
/// always put back to the value found. An error says what was wrong.
pub fn smoke_run(
    locale: &Locale,
    rendered_titles: &[String],
    rendered_statuses: &[String],
    expected_statuses: &[String],
    read: impl Fn() -> Option<bool>,
    mut write: impl FnMut(Option<bool>),
) -> Result<SmokeOutcome, String> {
    let titles = windows_titles(locale);
    if rendered_titles != titles.as_slice() {
        return Err(format!(
            "Settings sections are not in the macOS order: expected {} but built {}",
            titles.join(", "),
            rendered_titles.join(", ")
        ));
    }
    if rendered_statuses != expected_statuses {
        return Err(format!(
            "CLI update section must show every fixture result: expected {} but showed {}",
            expected_statuses.join(", "),
            rendered_statuses.join(", ")
        ));
    }
    let original = read();
    let flipped = original != Some(true);
    write(Some(flipped));
    let persisted = read() == Some(flipped);
    write(original);
    if !persisted {
        return Err("the CLI auto-update switch did not persist to saved state".into());
    }
    if read() != original {
        return Err("the CLI auto-update switch was not restored".into());
    }
    Ok(SmokeOutcome {
        sections: rendered_titles.to_vec(),
        cli_update_statuses: rendered_statuses.to_vec(),
        toggled_to: flipped,
        restored: true,
    })
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;

    use super::*;
    use crate::model::AppSnapshot;
    use crate::normalize;

    fn ko() -> Locale {
        Locale::new("ko")
    }

    /// Verification.cs:1173 "settings sections appear in the macOS order with their titles".
    #[test]
    fn sections_appear_in_mac_order_with_their_titles() {
        let l = ko();
        let titles = windows_titles(&l);
        assert_eq!(
            titles,
            vec![
                "화면".to_string(),
                l.get("settings.phaseModels.sectionTitle"),
                l.get("settings.styles.sectionTitle"),
                l.get("settings.components.sectionTitle"),
                l.get("settings.mobileRemote.sectionTitle"),
                l.get("settings.remote.navTitle"),
                l.get("settings.cliUpdate.sectionTitle"),
                l.get("companion.settings.sectionTitle"),
                "이 PC의 CLI".to_string(),
                l.get("settings.cliAccounts.sectionTitle"),
                "Claude Mods".to_string(),
                l.get("settings.appUpdate.sectionTitle"),
                "앱 정보".to_string(),
            ]
        );
        let mut unique = titles.clone();
        unique.sort();
        unique.dedup();
        assert_eq!(unique.len(), titles.len(), "section titles must be unique");
        let mac: Vec<&str> = mac_order(&l).iter().map(|s| s.id).collect();
        let positions: Vec<usize> = windows(&l).iter().map(|s| mac.iter().position(|id| *id == s.id).unwrap()).collect();
        assert!(positions.windows(2).all(|w| w[0] < w[1]), "Windows sections keep the macOS relative order");
        assert!(mac.iter().position(|id| *id == CLI_UPDATE) < mac.iter().position(|id| *id == PROVIDERS));
    }

    /// Verification.cs:1174 "settings sections leave out features that are not on Windows yet".
    #[test]
    fn sections_leave_out_features_not_on_windows_yet() {
        let absent: [&str; 0] = [];
        assert_eq!(mac_order(&ko()).len(), windows(&ko()).len() + absent.len());
    }

    /// Verification.cs:1175 "settings sections registering a section does not touch the others".
    #[test]
    fn registering_a_section_does_not_touch_the_others() {
        let l = ko();
        let before_catalog = windows_titles(&l);
        let fixture: Vec<Slot> = mac_order(&l)
            .into_iter()
            .map(|slot| if slot.id == CLAUDE_MODS { Slot { windows_title: None, ..slot } } else { slot })
            .collect();
        let before: Vec<String> = fixture.iter().filter_map(|s| s.windows_title.clone()).collect();
        let registered: Vec<String> = fixture
            .into_iter()
            .map(|slot| if slot.id == CLAUDE_MODS { Slot { windows_title: Some("Claude Mods".into()), ..slot } } else { slot })
            .filter_map(|s| s.windows_title)
            .collect();
        assert_eq!(registered.len(), before.len() + 1);
        assert_eq!(registered.iter().filter(|t| *t != "Claude Mods").cloned().collect::<Vec<_>>(), before);
        let accounts = l.get("settings.cliAccounts.sectionTitle");
        assert_eq!(registered.iter().position(|t| t == "Claude Mods"), registered.iter().position(|t| *t == accounts).map(|at| at + 1));
        assert_eq!(windows_titles(&l), before_catalog);
    }

    fn fixture_statuses() -> Vec<String> {
        ["updated", "current", "skipped", "failed"].iter().map(|s| s.to_string()).collect()
    }

    /// Verification.cs:1176 "settings sections smoke flips the CLI auto-update switch and restores it".
    #[test]
    fn smoke_flips_the_auto_update_switch_and_restores_it() {
        let l = ko();
        for original in [None, Some(false), Some(true)] {
            let saved = RefCell::new(AppSnapshot { auto_update_clis: original, ..AppSnapshot::default() });
            let writes = RefCell::new(vec![]);
            let outcome = smoke_run(
                &l,
                &windows_titles(&l),
                &fixture_statuses(),
                &fixture_statuses(),
                || saved.borrow().auto_update_clis,
                |value| {
                    writes.borrow_mut().push(value);
                    saved.borrow_mut().auto_update_clis = value;
                },
            )
            .unwrap();
            assert_eq!(outcome.toggled_to, original != Some(true));
            assert!(outcome.restored);
            assert_eq!(saved.borrow().auto_update_clis, original);
            assert_eq!(*writes.borrow(), vec![Some(original != Some(true)), original]);
            assert_eq!(outcome.sections, windows_titles(&l));
        }
    }

    /// Verification.cs:1177 "settings sections smoke shows every fixture status and rejects a wrong screen" (the screen's half; the
    /// rows' words are CliUpdateService's, W6-D's).
    #[test]
    fn smoke_rejects_a_wrong_screen() {
        let l = ko();
        let state = RefCell::new(None::<bool>);
        let read = || *state.borrow();
        let mut reversed = windows_titles(&l);
        reversed.reverse();
        assert!(smoke_run(&l, &reversed, &fixture_statuses(), &fixture_statuses(), read, |v| *state.borrow_mut() = v).is_err());
        let dropped = fixture_statuses()[..3].to_vec();
        assert!(smoke_run(&l, &windows_titles(&l), &dropped, &fixture_statuses(), read, |v| *state.borrow_mut() = v).is_err());
        let original = *state.borrow();
        assert!(smoke_run(&l, &windows_titles(&l), &fixture_statuses(), &fixture_statuses(), read, |_| {}).is_err(), "never persists");
        assert_eq!(*state.borrow(), original, "a failed run leaves the switch as it was");
    }

    /// Verification.cs:1178 "remote settings: the remote page sits between mobile and companion".
    #[test]
    fn the_remote_page_sits_between_mobile_and_companion() {
        let l = ko();
        let ids: Vec<&str> = available(&l).iter().map(|c| c.id).collect();
        let at = |id: &str| ids.iter().position(|i| *i == id).unwrap();
        assert_eq!(at("remote"), at("mobile") + 1);
        assert_eq!(at("companion"), at("remote") + 1);
        assert_eq!(available(&l).iter().find(|c| c.id == "remote").unwrap().sections, vec![REMOTE_WORKSPACES]);
        let state = AppSnapshot { settings_pane: Some("remote".into()), ..AppSnapshot::default() };
        assert_eq!(normalize::normalize(&state, false).unwrap().settings_pane.as_deref(), Some("remote"));
    }

    #[test]
    fn every_category_is_available_on_windows() {
        let ids: Vec<&str> = available(&ko()).iter().map(|c| c.id).collect();
        assert_eq!(ids, vec!["general", "models", "styles", "tools", "cli", "mobile", "remote", "companion", "about"]);
    }
}
