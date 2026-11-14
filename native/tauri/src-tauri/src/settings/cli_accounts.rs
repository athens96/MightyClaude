//! `cli`: 이 PC의 CLI, CLI 계정 and Claude Mods (C# MainWindow.Settings.cs:145-153, :573-1026). Owner: W6-C.
//!
//! Each box is mighty-accounts' builder over the host's models (accounts.rs): the run engine's runtime, the accounts' statuses and
//! their rows' state. The CLI accounts box shows the smoke's fixture statuses while the accounts step holds them (C#'s smoke hands
//! `BuildCliAccountsSection` its fixture statuses).

use std::sync::{Arc, Mutex};

use mighty_accounts::accounts::CliAccountStatus;
use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// The smoke's fixture statuses while its accounts step shows them (C# `CliAccountSmoke.FixtureStatuses`).
static FIXTURE: Mutex<Option<Vec<CliAccountStatus>>> = Mutex::new(None);

/// The smoke shows (Some) or stops showing (None) its fixture statuses in the box.
pub fn show_fixture(statuses: Option<Vec<CliAccountStatus>>) {
    *FIXTURE.lock().unwrap_or_else(|p| p.into_inner()) = statuses;
}

/// 이 PC의 CLI (Providers) (C# `BuildProvidersSection`, MainWindow.Settings.cs:576-621).
pub fn providers(host: &Arc<Host>, ui: &mut Ui) -> El {
    let model = crate::accounts::providers_model(host);
    let recheck = host.clone();
    mighty_accounts::view::providers(ui, &model, move || crate::accounts::recheck_runtime(&recheck))
}

/// CLI 계정 (CLI accounts) (C# `BuildCliAccountsSectionFromState`, MainWindow.Settings.cs:628-1026).
pub fn accounts(host: &Arc<Host>, ui: &mut Ui) -> El {
    let fixture = FIXTURE.lock().unwrap_or_else(|p| p.into_inner()).clone();
    let model = crate::accounts::accounts_model(host, fixture);
    mighty_accounts::view::accounts(ui, &model, Arc::new(crate::accounts::TabActions { host: host.clone() }))
}

/// Claude Mods (C# `BuildClaudeModsSection`, MainWindow.Settings.cs:145-153): Claude's runtime as the run engine read it.
pub fn claude_mods(host: &Arc<Host>, ui: &mut Ui) -> El {
    let claude = host.runs().and_then(|runs| runs.runtime("claude"));
    mighty_accounts::view::claude_mods(ui, claude.as_ref())
}
