//! Later stages' boxes, host-absent as in C# (MainWindow.Styles, MobileRemote, ScreenShare, RemoteWorkspaces, Companion). Owner: W6-A.

use std::sync::Arc;

use mighty_core::design_tokens::{Token, metrics};
use mighty_native::kit::{ControlSize, TextStyle};
use mighty_native::ui::{Border, El, Grid, HAlign, Kind, Thickness, Track, Ui, VAlign, Wrapping};

use crate::host::Host;

/// 스타일 (C# `BuildStylesSection`, MainWindow.Styles.cs:18-139): the register and rescan buttons, the description, then each registered
/// style's row. Styles (their registry, trust store and approval sheet) are W9's: until then no style is registered, so the list is
/// empty, register's file picker and rescan have nothing to read, and nothing is locked.
pub fn styles(_host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let mut rows = ui.settings_rows();
    let register = ui
        .settings_push(ui.button(locale.get("settings.styles.registerButton"), || {}), ControlSize::Regular, false, false)
        .aid("settings-style-register");
    let rescan = ui
        .settings_push(ui.button(locale.get("settings.styles.rescanButton"), || {}), ControlSize::Regular, false, false)
        .aid("settings-style-rescan");
    rows.add(El::stack(true, metrics::SPACING_SM, vec![register, rescan]));
    rows.add(ui.settings_text(locale.get("settings.styles.description"), TextStyle::size(11.0).ink(Token::Ink2)));
    rows.finish().aid("settings-style-rows")
}

/// `MobileRelayHost.DefaultRelay`.
const DEFAULT_RELAY: &str = "wss://mighty-relay.athens96.workers.dev";
/// `MobileQrSize`, `MobileRemoteIndent` (`Spacing.Xl`).
const MOBILE_QR_SIZE: f64 = 200.0;
const MOBILE_INDENT: f64 = metrics::SPACING_XL;
/// `MobileDirectPortWidth`.
const MOBILE_PORT_WIDTH: f64 = 80.0;

/// C#'s local `Heading`: a box's sub-heading, 11pt medium `ink2`.
fn heading(ui: &Ui, title: String) -> El {
    ui.settings_text(title, TextStyle::size(11.0).ink(Token::Ink2).medium())
}

/// 모바일 원격 (C# `BuildMobileRemoteSection`, MainWindow.MobileRemote.cs:93-456) as C# draws it before the phone remote exists (W8)
/// — what its smoke draws with no host: the saved settings shown with every switch, field and button disabled (C#'s
/// `ShowControls` and its Loaded handler), the state off, the firewall rule unknown (re-check), and the pairing rows (QR, host id, the
/// key as dots) and the device list hidden. A press changes nothing.
pub fn mobile_remote(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let saved = host.view().snapshot.mobile_remote.clone().unwrap_or_default();
    let mut rows = ui.settings_rows();
    let ink3 = Some(ui.brush(Token::Ink3));
    let small = |ui: &Ui, key: &str| ui.settings_push(ui.button(locale.get(key), || {}), ControlSize::Small, false, false);
    let caption = |ui: &Ui, text: String| ui.settings_text(text, TextStyle::size(11.0).ink(Token::Ink2));

    let allow = locale.get("windows.mobile.allow");
    let switch = ui.settings_switch(&allow, saved.enabled, Some("settings-mobile-toggle"), |_| {}).enabled(false);
    rows.add(ui.settings_labeled(ui.settings_titled(&allow, locale.get("windows.mobile.relayDescription")), switch, true));

    let state = El::stack(
        true,
        metrics::SPACING_SM,
        vec![
            El::new(Kind::Ellipse { fill: ink3 }).width(8.0).height(8.0).va(VAlign::Center),
            caption(ui, locale.get("settings.mobileRemote.statusOff")),
        ],
    );
    let words = El::stack(
        false,
        metrics::SPACING_XXS,
        vec![state, caption(ui, locale.get("mobileRemote.status.off")).aid("settings-mobile-detail")],
    );
    let reconnect = small(ui, "settings.mobileRemote.reconnectButton").aid("settings-mobile-reconnect").visible(false).enabled(false);
    rows.add(ui.settings_labeled(words, reconnect, true));

    rows.add(heading(ui, locale.get("windows.mobile.connectionTitle")));
    let relay = ui
        .settings_field(El::text_box(saved.relay_url.clone().unwrap_or_default()), 11.0)
        .placeholder(DEFAULT_RELAY)
        .named(locale.get("settings.mobileRemote.relayLabel"))
        .aid("settings-mobile-relay")
        .enabled(false);
    let apply = ui
        .settings_push(ui.button(locale.get("settings.mobileRemote.applyButton"), || {}), ControlSize::Regular, false, false)
        .aid("settings-mobile-apply")
        .enabled(false)
        .cell(0, 1);
    let line = El::grid(Grid {
        children: vec![relay, apply],
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    })
    .margin(Thickness::new(MOBILE_INDENT, 0.0, 0.0, 0.0));
    let default = caption(ui, locale.get_with("windows.mobile.relayDefaultTemplate", &[("address", DEFAULT_RELAY)]))
        .margin(Thickness::new(MOBILE_INDENT, 0.0, 0.0, 0.0));
    rows.add(El::stack(
        false,
        metrics::SPACING_SM,
        vec![ui.settings_titled(locale.get("windows.mobile.relayTitle"), locale.get("windows.mobile.relayExplanation")), line, default],
    ));

    let direct_label = locale.get("mobileRemote.direct.toggle");
    let direct = ui.settings_switch(&direct_label, saved.direct_enabled, Some("settings-mobile-direct-toggle"), |_| {}).enabled(false);
    let direct_line =
        ui.settings_labeled(ui.settings_titled(&direct_label, locale.get("mobileRemote.direct.windows.description")), direct, true);
    let port_label = locale.get("mobileRemote.direct.portLabel");
    let port = ui
        .settings_field(El::text_box(saved.direct_port.to_string()), 11.0)
        .width(MOBILE_PORT_WIDTH)
        .named(&port_label)
        .aid("settings-mobile-direct-port")
        .enabled(false);
    let apply_port = small(ui, "settings.mobileRemote.applyButton").aid("settings-mobile-direct-apply").enabled(false);
    let indent = Thickness::new(MOBILE_INDENT, 0.0, 0.0, 0.0);
    let port_row = El::border(Border {
        child: Some(Box::new(El::stack(
            true,
            metrics::SPACING_SM,
            vec![ui.settings_text(&port_label, TextStyle::default()), port, apply_port],
        ))),
        ..Border::default()
    })
    .margin(indent)
    .visible(saved.direct_enabled);
    let status_row = El::border(Border {
        child: Some(Box::new(El::stack(
            true,
            metrics::SPACING_SM,
            vec![
                El::new(Kind::Ellipse { fill: ink3 }).width(8.0).height(8.0).va(VAlign::Center),
                ui.settings_text("", TextStyle::size(11.0).ink(Token::Ink2).selectable()).aid("settings-mobile-direct-status"),
            ],
        ))),
        ..Border::default()
    })
    .margin(indent)
    .visible(saved.enabled);
    let firewall_words = El::stack(
        false,
        metrics::SPACING_XXS,
        vec![
            caption(ui, locale.get("mobileRemote.direct.windows.firewall.unknown")).aid("settings-mobile-firewall-state"),
            caption(ui, locale.get("mobileRemote.direct.windows.firewall.description")),
            caption(ui, locale.get("mobileRemote.direct.windows.portHint")),
            caption(ui, locale.get("remote.windows.firewall.removeClosesBoth")),
        ],
    );
    let firewall_buttons = El::stack(
        true,
        metrics::SPACING_SM,
        vec![
            small(ui, "remote.windows.firewall.update").aid("settings-mobile-firewall-update").visible(false).enabled(false),
            small(ui, "setup.button.recheck").aid("settings-mobile-firewall-button").enabled(false),
        ],
    );
    let firewall_row =
        El::border(Border { child: Some(Box::new(ui.settings_labeled(firewall_words, firewall_buttons, true))), ..Border::default() })
            .margin(indent)
            .visible(saved.direct_enabled);
    rows.add(El::stack(false, metrics::SPACING_SM, vec![direct_line, port_row, status_row, firewall_row]));

    // The pairing rows (title, QR, host id, key, copy and regenerate) and the devices' title: hidden without a host.
    rows.add(heading(ui, locale.get("windows.mobile.pairingTitle"))).visible = false;
    let qr = El::new(Kind::Image { src: None }).width(MOBILE_QR_SIZE).height(MOBILE_QR_SIZE).aid("settings-mobile-qr");
    rows.add(qr).visible = false;
    rows.add(heading(ui, locale.get("settings.mobileRemote.connectedDevicesTitle"))).visible = false;
    rows.add(heading(ui, locale.get("windows.mobile.advancedTitle")));
    let legacy_label = locale.get("settings.mobileRemote.legacyAppsToggle");
    let legacy = ui.settings_switch(&legacy_label, saved.allow_legacy_phones, Some("settings-mobile-legacy-toggle"), |_| {}).enabled(false);
    rows.add(ui.settings_labeled(
        ui.settings_titled(&legacy_label, locale.get("settings.mobileRemote.legacyAppsDescription")),
        legacy,
        true,
    ));
    rows.finish()
}

/// 화면 보기·조작 (C# `BuildScreenShareSection` → `BuildScreenSharePaused`, MainWindow.ScreenShare.cs:179-185): screen view is off on
/// Windows (`ScreenShareAvailability.Enabled` is false), so the box says it is paused.
pub fn screen_share(_host: &Arc<Host>, ui: &mut Ui) -> El {
    let mut rows = ui.settings_rows();
    rows.add(ui.settings_text(ui.locale().get("settings.screenShare.paused"), TextStyle::size(11.0).ink(Token::Ink2)));
    rows.finish().aid("settings-screen-share-paused")
}

/// `RemoteProtocol.DefaultPort`.
const REMOTE_PORT: &str = "43137";

/// 원격 작업 공간 (C# `BuildRemoteWorkspacesSection`, MainWindow.RemoteWorkspaces.cs:180-560) as C# draws it with no sharing host and no
/// remote client (W8): not sharing, the local workspaces to pick (the active one picked), the port, start and connect disabled, the
/// firewall rule unknown (re-check, disabled in a smoke), and the scope lines.
pub fn remote_workspaces(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let (locals, active) = {
        let view = host.view();
        let locals: Vec<(String, String, String)> = view
            .snapshot
            .workspaces()
            .iter()
            .filter(|w| w.remote.is_none())
            .map(|w| (w.id().to_string(), w.name().to_string(), w.path().to_string()))
            .collect();
        (locals, view.snapshot.active_workspace_id.clone())
    };
    let caption = |ui: &Ui, key: &str| ui.settings_text(locale.get(key), TextStyle::size(11.0).ink(Token::Ink2));
    let title = |ui: &Ui, key: &str| ui.settings_text(locale.get(key), TextStyle::default().medium());
    let mut rows = ui.settings_rows();
    rows.add(caption(ui, "settings.remote.intro"));

    let status = El::grid(Grid {
        children: vec![
            El::new(Kind::Ellipse { fill: Some(ui.brush(Token::Idle)) }).width(8.0).height(8.0).va(VAlign::Center),
            caption(ui, "settings.remote.sharingOff").aid("settings-remote-share-status").cell(0, 1),
        ],
        cols: vec![Track::Auto, Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    let selected = ui
        .local("remote-selected", || active.iter().filter(|id| locals.iter().any(|(l, _, _)| l == *id)).cloned().collect::<Vec<String>>());
    let mut picks = vec![];
    if locals.is_empty() {
        picks.push(caption(ui, "settings.remote.noLocalWorkspaces"));
    } else {
        picks.push(ui.settings_text(locale.get("settings.remote.pickWorkspaces"), TextStyle::size(11.0).ink(Token::Ink2).medium()));
        for (id, name, path) in &locals {
            let mut folder = ui.settings_text(path.clone(), TextStyle::size(10.0).ink(Token::Ink2).mono());
            if let Some(text) = folder.text_mut() {
                text.wrapping = Wrapping::NoWrap;
            }
            let content = El::stack(false, metrics::SPACING_XXS, vec![ui.settings_text(name.clone(), TextStyle::size(12.0)), folder]);
            let (into, me) = (selected.clone(), id.clone());
            picks.push(
                El::check_box(selected.get().contains(id), content)
                    .named(name.clone())
                    .aid(format!("settings-remote-share-{id}"))
                    .keyed(id.clone())
                    .on_toggle(move |on| {
                        into.update(|list| {
                            list.retain(|x| *x != me);
                            if on {
                                list.push(me.clone());
                            }
                        })
                    }),
            );
        }
    }
    let port = ui
        .settings_field(El::text_box(REMOTE_PORT), 11.0)
        .width(MOBILE_PORT_WIDTH)
        .named(locale.get("settings.remote.portLabel"))
        .aid("settings-remote-port")
        .cell(0, 1);
    let start = ui
        .settings_push(ui.button(locale.get("settings.remote.startSharing"), || {}).enabled(false), ControlSize::Small, true, false)
        .aid("settings-remote-start-sharing")
        .cell(0, 3);
    let port_line = El::grid(Grid {
        children: vec![ui.settings_text(locale.get("settings.remote.portLabel"), TextStyle::size(12.0)), port, start],
        cols: vec![Track::Auto, Track::Auto, Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    let not_sharing = El::stack(
        false,
        metrics::SPACING_SM,
        vec![El::stack(false, metrics::SPACING_XS, picks), port_line, caption(ui, "remote.windows.settings.shareNote")],
    );
    rows.add(El::stack(false, metrics::SPACING_SM, vec![title(ui, "settings.remote.shareTitle"), status, not_sharing]));

    let words = El::stack(
        false,
        metrics::SPACING_XXS,
        vec![
            caption(ui, "mobileRemote.direct.windows.firewall.unknown").aid("settings-remote-firewall-state"),
            caption(ui, "remote.windows.settings.firewallNote"),
            caption(ui, "remote.windows.firewall.removeClosesBoth"),
        ],
    );
    let smoke = host.smoke.is_some();
    let buttons = El::stack(
        true,
        metrics::SPACING_SM,
        vec![
            ui.settings_push(ui.button(locale.get("setup.button.recheck"), || {}).enabled(!smoke), ControlSize::Small, false, false)
                .aid("settings-remote-firewall-add"),
            ui.settings_push(ui.button(locale.get("remote.windows.firewall.remove"), || {}), ControlSize::Small, false, true)
                .aid("settings-remote-firewall-remove")
                .visible(false),
        ],
    );
    rows.add(ui.settings_labeled(words, buttons, true));

    let field = |ui: &Ui, key: &str, placeholder: &str, aid: &str, size: f64| {
        ui.settings_field(El::text_box(""), size).placeholder(locale.get(placeholder)).named(locale.get(key)).aid(aid)
    };
    let key = El::new(Kind::PasswordBox { text: String::new(), placeholder: locale.get("settings.remote.keyPlaceholder") })
        .height(metrics::SPACING_XL + 3.0)
        .va(VAlign::Center)
        .named(locale.get("settings.remote.keyField"))
        .aid("settings-remote-key");
    let connect = ui
        .settings_push(ui.button(locale.get("settings.remote.connectButton"), || {}).enabled(false), ControlSize::Small, true, false)
        .aid("settings-remote-connect")
        .cell(0, 1);
    let name_line = El::grid(Grid {
        children: vec![field(ui, "settings.remote.nameField", "settings.remote.namePlaceholder", "settings-remote-name", 12.0), connect],
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    let label = |ui: &Ui, key: &str| ui.settings_text(locale.get(key), TextStyle::size(12.0));
    rows.add(El::stack(
        false,
        metrics::SPACING_SM,
        vec![
            title(ui, "settings.remote.connectTitle"),
            ui.settings_wide(
                label(ui, "settings.remote.addressField"),
                field(ui, "settings.remote.addressField", "settings.remote.addressPlaceholder", "settings-remote-address", 11.0),
            ),
            ui.settings_wide(label(ui, "settings.remote.keyField"), key),
            ui.settings_wide(label(ui, "settings.remote.nameField"), name_line),
            caption(ui, "settings.remote.connectNote"),
        ],
    ));
    let mut scope = vec![title(ui, "settings.remote.scopeTitle")];
    for key in [
        "settings.remote.scope.runs",
        "settings.remote.scope.notMirrored",
        "settings.remote.scope.outOfScope",
        "remote.windows.settings.scopeKey",
    ] {
        scope.push(caption(ui, key));
    }
    rows.add(El::stack(false, metrics::SPACING_XXS, scope).aid("settings-remote-scope"));
    rows.finish()
}

/// 컴패니언 (C# `BuildCompanionSection`, MainWindow.Companion.cs:510-578). The pets (their catalog, picker, preview, import, reload and
/// position) are W8's: until then no pet is installed, so the picker is empty and the box says so (`companion.error.noPet`); the pet
/// switches keep their defaults for this run. The completion notification switch is the saved setting, and the row under it says
/// whether Windows lets the app notify (notifications.rs).
pub fn companion(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let mut rows = ui.settings_rows();
    let switch = |ui: &mut Ui, rows: &mut mighty_native::kit::SettingsRows, key: &str, aid: &str, on: bool| {
        let label = locale.get(key);
        let kept = ui.local(aid, || on);
        let into = kept.clone();
        let toggle = ui.settings_switch(&label, kept.get(), Some(aid), move |on| into.set(on));
        rows.add(ui.settings_labeled(ui.settings_text(&label, TextStyle::default()), toggle, false));
    };
    switch(ui, &mut rows, "companion.settings.enabled", "settings-companion-enabled", true);
    let picker = ui
        .settings_popup(El::combo_box(-1, vec![]), false)
        .ha(HAlign::Right)
        .named(locale.get("companion.settings.pet"))
        .aid("settings-companion-pet");
    let framed = ui.settings_popup_frame(picker).margin(Thickness::new(0.0, 0.0, -mighty_native::kit::SETTINGS_POPUP_CHIP_INSET, 0.0));
    let buttons = ["companion.settings.import", "companion.settings.reload", "companion.settings.resetPosition"]
        .iter()
        .map(|key| ui.settings_push(ui.button(locale.get(key), || {}), ControlSize::Small, false, false))
        .collect();
    let choice = El::stack(
        false,
        metrics::SPACING_SM,
        vec![
            ui.settings_labeled(ui.settings_text(locale.get("companion.settings.pet"), TextStyle::default()), framed, false),
            El::new(Kind::Panel { children: buttons }),
        ],
    )
    .va(VAlign::Center)
    .cell(0, 1);
    let preview = El::new(Kind::Image { src: None }).width(58.0).height(64.0).va(VAlign::Center).aid("settings-companion-preview");
    rows.add(El::grid(Grid {
        children: vec![preview, choice],
        cols: vec![Track::Auto, Track::Star(1.0)],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    }));
    rows.add(ui.settings_text(locale.get("companion.settings.description"), TextStyle::size(10.0).ink(Token::Ink2)));
    switch(ui, &mut rows, "companion.settings.task", "settings-companion-task", true);
    switch(ui, &mut rows, "companion.settings.motion", "settings-companion-motion", false);

    let label = locale.get("windows.notifications.toggleLabel");
    let on = host.view().snapshot.completion_notifications_enabled;
    let to = host.clone();
    let toggle = ui.settings_switch(&label, on, Some("settings-completion-notifications"), move |on| {
        let host = to.clone();
        crate::native::act(&to, async move { host.view().update(|state| state.completion_notifications_enabled = on) });
    });
    rows.add(ui.settings_labeled(ui.settings_text(&label, TextStyle::default()), toggle, false));
    let words = crate::notifications::permission_text(&locale, crate::notifications::permission(host));
    let to = host.clone();
    let open = ui.settings_push(
        ui.button(locale.get("windows.notifications.settingsButton"), move || {
            if to.smoke.is_none() {
                let _ = crate::platform::open_url("ms-settings:notifications");
            }
        }),
        ControlSize::Small,
        false,
        false,
    );
    rows.add(ui.settings_labeled(ui.settings_text(words, TextStyle::size(11.0).ink(Token::Ink2)), open, false));
    rows.add(ui.settings_text(locale.get("companion.error.noPet"), TextStyle::size(11.0).ink(Token::Ink2)));
    rows.finish()
}
