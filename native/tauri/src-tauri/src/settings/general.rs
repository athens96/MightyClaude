//! `general`: 화면, 글자, 에이전트 링크 (C# MainWindow.Settings.cs:83-142 `BuildDisplaySection`, `BuildAgentLinksSection`;
//! MainWindow.TextAppearance.cs:113-202 `BuildTextSection`; MainWindow.Delegation.cs `DelegationOn`, `SetDelegationOn`). Owner: W6-A.

use std::sync::{Arc, Mutex, OnceLock};

use mighty_core::design_tokens::{Token, metrics};
use mighty_core::locale::LANGUAGES;
use mighty_core::text_appearance::{SCALES, TERMINAL_BASE_SIZE, TextAppearance, code_families};
use mighty_native::kit::{ControlSize, SETTINGS_POPUP_CHIP_INSET, TextStyle};
use mighty_native::ui::{Border, Content, El, HAlign, Kind, TextBlock, Thickness, Ui, Wrapping};
use mighty_platform::{PrefValue, PrefsStore};
use tauri::Manager;

use crate::host::Host;

/// `Locale.PickerChoices`: the language picker's values and their labels' keys.
pub const LANGUAGE_CHOICES: [(&str, &str); 5] = [
    ("system", "settings.display.languageSystem"),
    ("ko", "settings.display.languageKorean"),
    ("en", "settings.display.languageEnglish"),
    ("zh", "settings.display.languageChinese"),
    ("ja", "settings.display.languageJapanese"),
];

/// `DelegationSwitch`'s value name under `HKCU\Software\MightyCode`.
pub const DELEGATION_VALUE: &str = "delegation.enabled";

/// The delegation switch as the app holds it: a smoke's own (off until it is switched, C# `smokeDelegationOn`), else the value
/// switched in this run (kept in memory without `--allow-state-write`), else the registry's (a missing value is on).
static DELEGATION: Mutex<Option<bool>> = Mutex::new(None);

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// C# `DelegationOn`.
pub fn delegation_on(host: &Host) -> bool {
    if let Some(on) = *lock(&DELEGATION) {
        return on;
    }
    if host.smoke.is_some() {
        return false;
    }
    // Read once and kept (a builder reads it four times a second; C# reads the registry at most every 2 s).
    let saved = host.app.try_state::<Arc<dyn PrefsStore>>().and_then(|prefs| prefs.get(DELEGATION_VALUE).ok().flatten());
    let on = saved.is_none_or(|value| value.is_on());
    *lock(&DELEGATION) = Some(on);
    on
}

/// C# `SetDelegationOn`: a smoke's switch, else the registry's DWORD (only with `--allow-state-write`), else this run's.
pub fn set_delegation_on(host: &Host, on: bool) {
    if host.may_change_machine()
        && let Some(prefs) = host.app.try_state::<Arc<dyn PrefsStore>>()
        && let Err(error) = prefs.set(DELEGATION_VALUE, PrefValue::Int(i64::from(on)))
    {
        host.log.line(&format!("delegation switch not saved: {error}"));
    }
    *lock(&DELEGATION) = Some(on);
    host.panes_changed();
}

/// `InstalledFamilies`: every installed family and the code picker's (`CodeFamilies`), read once per run from the font catalog
/// ([`read_families`] when Settings opens; none until then, as when the OS could not list them).
static FAMILIES: OnceLock<(Vec<String>, Vec<String>)> = OnceLock::new();

/// Reads the installed families once (I/O: called as Settings opens, never from a builder).
pub fn read_families(host: &Host) {
    if FAMILIES.get().is_some() {
        return;
    }
    let read = host.app.try_state::<crate::platform::Ports>().and_then(|ports| ports.fonts.families().ok()).unwrap_or_default();
    let all: Vec<String> = read.iter().map(|f| f.name.clone()).collect();
    let code = code_families(read.iter().map(|f| (f.name.as_str(), f.monospaced)));
    let _ = FAMILIES.set((all, code));
}

fn families() -> (Vec<String>, Vec<String>) {
    FAMILIES.get().cloned().unwrap_or_default()
}

/// 화면 (C# `BuildDisplaySection`): the theme and language pickers, the language note, then the status line, delegation and browser
/// switches, each with its explanation under its label.
pub fn display(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let (theme, preference, status_line, browser) = {
        let view = host.view();
        let s = &view.snapshot;
        let theme = if s.theme() == "light" { "light" } else { "dark" };
        (theme, s.language_preference().to_string(), s.status_line_enabled, s.browser_engine_enabled)
    };
    let mut rows = ui.settings_rows();

    let theme_label = locale.get("settings.display.themeLabel");
    let options = [
        ("dark".to_string(), locale.get("settings.display.themeDarkWindows")),
        ("light".to_string(), locale.get("settings.display.themeLightWindows")),
    ];
    let to = host.clone();
    let picker = ui.settings_segmented(&theme_label, "settings-theme", &options, theme, move |value| set_theme(&to, &value), false, true);
    rows.add(ui.settings_labeled(ui.settings_text(&theme_label, TextStyle::default()), picker, false));

    let language_label = locale.get("settings.display.languageLabel");
    let options: Vec<(String, String)> = LANGUAGE_CHOICES.iter().map(|(value, key)| (value.to_string(), locale.get(key))).collect();
    let chosen = if LANGUAGES.contains(&preference.as_str()) { preference.as_str() } else { "system" };
    let to = host.clone();
    let picker = ui.settings_segmented(
        &language_label,
        "settings-language",
        &options,
        chosen,
        move |value| {
            crate::native::act(&to, {
                let to = to.clone();
                async move { to.view().update(|state| state.language_preference = Some(value)) }
            })
        },
        false,
        true,
    );
    rows.add(ui.settings_labeled(ui.settings_text(&language_label, TextStyle::default()), picker, false));
    rows.add(ui.settings_text(locale.get("settings.display.languageRestartNote"), TextStyle::size(11.0).ink(Token::Ink2)));

    let label = locale.get("settings.display.statusLineToggle");
    let to = host.clone();
    let switch = ui.settings_switch(&label, status_line, Some("settings-status-line"), move |on| set_status_line(&to, on));
    let words = ui.settings_titled(&label, locale.get("settings.display.statusLineDescription"));
    rows.add(ui.settings_labeled(words, switch, true));

    let label = locale.get("settings.delegation.toggle");
    let to = host.clone();
    let switch = ui.settings_switch(&label, delegation_on(host), Some("settings-delegation"), move |on| set_delegation_on(&to, on));
    let words = ui.settings_titled(&label, locale.get("settings.delegation.descriptionWindows"));
    rows.add(ui.settings_labeled(words, switch, true));

    let label = locale.get("settings.display.browserToggle");
    let to = host.clone();
    let switch = ui.settings_switch(&label, browser, Some("settings-browser-engine"), move |on| {
        crate::native::act(&to, {
            let to = to.clone();
            async move { to.view().update(|state| state.browser_engine_enabled = on) }
        })
    });
    let words = ui.settings_titled(&label, locale.get("windows.settings.browserDescription"));
    rows.add(ui.settings_labeled(words, switch, true));
    rows.finish()
}

/// The theme picker (C#: `UpdateAsync(Theme)` then `Render`): saved like `setTheme`, the terminals recoloured.
pub fn set_theme(host: &Arc<Host>, value: &str) {
    let host = host.clone();
    let value = value.to_string();
    crate::native::act(&host.clone(), async move {
        host.view().update(|state| state.theme = Some(value))?;
        crate::terminal::restyle(&host);
        Ok(())
    });
}

/// C# `SetStatusLineEnabled`: saved, then each pane's status line read again (status_line.rs, W6-B).
fn set_status_line(host: &Arc<Host>, on: bool) {
    let host = host.clone();
    crate::native::act(&host.clone(), async move {
        host.view().update(|state| state.status_line_enabled = on)?;
        crate::status_line::set_enabled(&host, on);
        host.panes_changed();
        Ok(())
    });
}

/// 글자 (C# `BuildTextSection`): the size from 85% to 150%, the interface, conversation and code fonts each over a line of words in
/// that font, the shortcuts' note and the button back to the standard setting. Every choice applies at once.
pub fn text(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let current = crate::frame::text_setting(host);
    let (all, code) = families();
    let mut rows = ui.settings_rows();

    let size_label = locale.get("settings.text.sizeLabel");
    let sizes: Vec<Content> = SCALES
        .iter()
        .map(|scale| {
            let percent = TextAppearance::new(*scale, None, None, None).percent().to_string();
            if *scale == 1.0 {
                locale.get_with("settings.text.sizeStandard", &[("percent", &percent)]).into()
            } else {
                format!("{percent}%").into()
            }
        })
        .collect();
    let index = SCALES.iter().position(|s| *s == current.scale).unwrap_or(2) as i32;
    let to = host.clone();
    let size = picker(ui, &size_label, "settings-text-size", index, sizes).on_select(move |at| {
        if let Some(scale) = SCALES.get(at as usize) {
            crate::frame::set_text(&to, crate::frame::text_setting(&to).with_scale(*scale));
        }
    });
    rows.add(ui.settings_labeled(ui.settings_text(&size_label, TextStyle::default()), framed(ui, size), false));

    let blocks: [FontBlock; 3] = [
        (
            "settings.text.uiFontLabel",
            "settings-text-font",
            "settings.text.fontSystem",
            &all,
            current.family.as_deref(),
            TextAppearance::with_family,
            Area::Interface,
        ),
        (
            "settings.text.transcriptFontLabel",
            "settings-text-transcript-font",
            "settings.text.sameAsInterface",
            &all,
            current.transcript_family.as_deref(),
            TextAppearance::with_transcript_family,
            Area::Transcript,
        ),
        (
            "settings.text.codeFontLabel",
            "settings-text-code-font",
            "settings.text.monospaceSystem",
            &code,
            current.code_family.as_deref(),
            TextAppearance::with_code_family,
            Area::Code,
        ),
    ];
    for (label_key, aid, none_key, list, saved, change, area) in blocks {
        let label = locale.get(label_key);
        // A saved family no longer installed stays listed (it draws in the stand-in).
        let mut names: Vec<String> = list.to_vec();
        if let Some(saved) = saved
            && !names.iter().any(|n| n.eq_ignore_ascii_case(saved))
        {
            names.push(saved.to_string());
        }
        let index = saved.and_then(|s| names.iter().position(|n| n.eq_ignore_ascii_case(s))).map_or(0, |at| at + 1) as i32;
        let items: Vec<Content> = std::iter::once(locale.get(none_key)).chain(names.iter().cloned()).map(Content::from).collect();
        let (to, chosen) = (host.clone(), names.clone());
        let combo = picker(ui, &label, aid, index, items).on_select(move |at| {
            let family = (at > 0).then(|| chosen.get(at as usize - 1).cloned()).flatten();
            crate::frame::set_text(&to, change(&crate::frame::text_setting(&to), family.as_deref()));
        });
        let line = ui.settings_labeled(ui.settings_text(&label, TextStyle::default()), framed(ui, combo), false);
        let size = if area == Area::Code { TERMINAL_BASE_SIZE } else { metrics::TYPE_BODY };
        let preview = preview(ui, &current, area, size);
        rows.add(El::stack(false, metrics::SPACING_SM, vec![line, preview]));
    }

    let to = host.clone();
    let reset = ui
        .settings_push(
            ui.button(locale.get("settings.text.reset"), move || crate::frame::set_text(&to, TextAppearance::default())),
            ControlSize::Regular,
            false,
            false,
        )
        .aid("settings-text-reset")
        .enabled(!current.is_standard());
    let note = ui.settings_text(locale.get("settings.text.noteWindows"), TextStyle::size(11.0).ink(Token::Ink2));
    rows.add(ui.settings_labeled(note, reset, false));
    rows.finish()
}

/// One font picker of the text box: its label's key, its automation id, its "none" entry's key, the families it lists, the family
/// saved, how a pick changes the setting, and the area its preview draws in.
type FontBlock<'a> =
    (&'static str, &'static str, &'static str, &'a [String], Option<&'a str>, fn(&TextAppearance, Option<&str>) -> TextAppearance, Area);

/// Which of the text setting's fonts a preview draws in (C# `TextArea`).
#[derive(Clone, Copy, PartialEq, Eq)]
enum Area {
    Interface,
    Transcript,
    Code,
}

/// The text box's pop-up (C#'s `Picker`): an inline pop-up at the right, at most 240 wide, named and marked.
fn picker(ui: &Ui, label: &str, aid: &str, index: i32, items: Vec<Content>) -> El {
    let mut combo = ui.settings_popup(El::combo_box(index, items), false).ha(HAlign::Right).named(label).aid(aid);
    combo.max_width = Some(240.0);
    combo
}

/// C#'s `Framed`: the pop-up with the Mac's chip, ending at the row's edge.
fn framed(ui: &Ui, combo: El) -> El {
    ui.settings_popup_frame(combo).margin(Thickness::new(0.0, 0.0, -SETTINGS_POPUP_CHIP_INSET, 0.0))
}

/// C#'s `Preview`: the preview words in the area's font on the subtle wash.
fn preview(ui: &Ui, text: &TextAppearance, area: Area, size: f64) -> El {
    let family = match area {
        Area::Interface => Some(text.family.clone().unwrap_or_else(|| metrics::FONT_BODY.to_string())),
        Area::Transcript => text.resolved_transcript_family().map(str::to_string),
        Area::Code => Some(text.code_family.clone().unwrap_or_else(|| metrics::FONT_MONO.to_string())),
    };
    let words = El::new(Kind::TextBlock(TextBlock {
        text: ui.locale().get("settings.text.preview"),
        size: Some(size),
        weight: 400,
        family,
        foreground: Some(ui.brush(Token::Ink)),
        wrapping: Wrapping::Wrap,
        ..TextBlock::default()
    }));
    El::border(Border {
        child: Some(Box::new(words)),
        padding: Thickness::all(metrics::SPACING_SM),
        corner_radius: metrics::RADIUS_SEGMENT,
        background: Some(ui.env.theme.subtle()),
        ..Border::default()
    })
}

/// 에이전트 링크 (C# `BuildAgentLinksSection`): where the active workspace opens the links an agent asks for.
pub fn agent_links(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let (workspace, chosen) = {
        let view = host.view();
        let workspace = view.snapshot.active_workspace_id.clone();
        let choice = workspace
            .as_ref()
            .and_then(|w| view.snapshot.agent_web_open_choices.as_ref().and_then(|c| c.get(w).cloned().flatten()))
            .unwrap_or_default();
        (
            workspace,
            match choice.as_str() {
                "inApp" => "inApp",
                "external" => "external",
                _ => "ask",
            },
        )
    };
    let title = locale.get("agentTerminal.urlOpen.settingTitle");
    let options = [
        ("ask".to_string(), locale.get("agentTerminal.urlOpen.settingAsk")),
        ("inApp".to_string(), locale.get("agentTerminal.urlOpen.settingInApp")),
        ("external".to_string(), locale.get("agentTerminal.urlOpen.settingExternal")),
    ];
    let (to, at) = (host.clone(), workspace.clone());
    let picker = ui.settings_segmented(
        &title,
        "settings-web-open-choice",
        &options,
        chosen,
        move |value| {
            let Some(workspace) = at.clone() else { return };
            let host = to.clone();
            crate::native::act(&to, async move {
                host.view().update(|state| {
                    let mut choices = state.agent_web_open_choices.clone().unwrap_or_default();
                    if value == "ask" {
                        choices.shift_remove(&workspace);
                    } else {
                        choices.insert(workspace, Some(value));
                    }
                    state.agent_web_open_choices = Some(choices);
                })
            })
        },
        false,
        workspace.is_some(),
    );
    let mut rows = ui.settings_rows();
    rows.add(ui.settings_labeled(ui.settings_text(&title, TextStyle::default()), picker, false));
    rows.finish()
}
