//! 앱 업데이트 (C# `BuildAppUpdateSection`, MainWindow.AppUpdate.cs:104-255, over mighty-core's `app_update_view::describe`). The app's
//! updates (the manifest, the signature, the download, the restart) are W9's; this build has no update public key and no built-in
//! address, so the box is what C# draws for such a build: the version, the notice in the wait ink, the check button disabled. The
//! address, automatic and signature rows are hidden in that state and left out here (the page drops hidden rows).

use std::sync::Arc;

use mighty_core::app_update_view::{self, State, describe, text};
use mighty_core::design_tokens::Token;
use mighty_native::kit::{ControlSize, TextStyle};
use mighty_native::ui::{Border, Content, El, Grid, Kind, Track, Ui};

use crate::host::Host;

/// Whether this build carries the update's public key (C# `AppUpdate.HasPublicKey`): never until W9.
const HAS_PUBLIC_KEY: bool = false;

pub fn section(host: &Arc<Host>, ui: &mut Ui) -> El {
    let locale = ui.locale();
    let view = describe(&locale, &State::default(), HAS_PUBLIC_KEY, None, |at| at.format("%H:%M").to_string(), true);
    let mut rows = ui.settings_rows();
    let version = locale.get_with("settings.appUpdate.betaVersionTemplate", &[("version", &crate::settings::about::version_text(host))]);
    let label = ui.settings_text(text(&locale, "CurrentVersionLabel"), TextStyle::default());
    rows.add(ui.settings_labeled(label, ui.settings_text(version, TextStyle::size(13.0).ink(Token::Ink2).selectable()), false));
    // The status: under the box's notice while the box is disabled (C#'s `Render` moves it out of the state grid), in the wait ink.
    let ink = if !view.section_enabled { Token::WaitText } else { Token::Ink2 };
    let status = ui.settings_text(view.status_text.clone(), TextStyle::size(11.0).ink(ink).selectable()).aid("app-update-status");
    if !view.section_enabled {
        rows.add(El::border(Border { child: Some(Box::new(status)), ..Border::default() }));
    }
    // The state row: the ring and the bar (both hidden at rest) and, while the box is enabled, the status; the action button.
    let ring = El::new(Kind::ProgressRing { active: false }).width(14.0).height(14.0).visible(false);
    let bar = El::new(Kind::ProgressBar { value: 0.0, max: 1.0, indeterminate: false }).width(140.0).visible(false).cell(0, 1);
    let mut cells = vec![ring, bar];
    if view.section_enabled {
        cells.push(status_cell(ui, &view));
    }
    let state = El::grid(Grid { children: cells, cols: vec![Track::Auto, Track::Auto, Track::Star(1.0)], ..Grid::default() });
    let mut button = El::button(Content::Text(view.button_label.clone())).enabled(view.button_enabled).aid("app-update-action");
    button = ui.settings_push(button, ControlSize::Regular, false, false);
    rows.add(ui.settings_labeled(state, button, false));
    rows.finish()
}

/// The status in the state grid while the box is enabled (W9).
fn status_cell(ui: &Ui, view: &app_update_view::SectionView) -> El {
    ui.settings_text(view.status_text.clone(), TextStyle::size(11.0).ink(Token::Ink2).selectable()).aid("app-update-status").cell(0, 2)
}
