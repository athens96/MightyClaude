//! CLI accounts and login recovery, `loginAction` (C# MainWindow.LoginRecovery.cs, MainWindow.WebShell.Cards.cs:554-596,
//! MainWindow.Settings.cs:628-1026). Owner: W6-C.
//!
//! The seams the other parts call, with their final signatures. Each stub answers the resting value (nothing signed in or out, no
//! card, no event acted on) until W6-C fills it.

use std::sync::Arc;

use mighty_composer::cards::LoginInput;
use mighty_core::model::{AppSnapshot, RunSession};
use mighty_run::acp::accounts::CliAccountStatus;
use mighty_run::{RunEvent, StartRunRequest};
use serde_json::Value;

use crate::host::{Host, Reply};

/// `accounts::statuses`: each CLI's account status as last read (C# `cliAccountStatuses`), for the setup screen's sign-in row. Stub:
/// W6-C fills it; until then none was read.
#[allow(dead_code, reason = "a seam W6-B (the setup screen's sign-in row) calls")]
pub fn statuses(_host: &Host) -> Vec<CliAccountStatus> {
    vec![]
}

/// `accounts::start_sign_in`: C# `StartCliSignIn(provider, option)` — the CLI's own sign-in in a terminal window (the `TerminalLauncher`
/// port), then the status polled until it reads signed in. Stub: W6-C fills it; until then nothing opens.
#[allow(dead_code, reason = "a seam W6-B (the setup screen's sign-in row) calls")]
pub fn start_sign_in(_host: &Arc<Host>, _provider: &str, _option: Option<&str>) {}

/// `accounts::refresh`: C# `RefreshCliAccounts` — every account status read again (Settings' CLI tab as it opens, outside a smoke).
/// Stub: W6-C fills it; until then nothing is read.
pub fn refresh(_host: &Arc<Host>) {}

/// `accounts::resend_after_update`: C# `ResendLoginsAwaitingUpdate` — the sign-in retries held while a CLI update ran, sent once it
/// ended (W6-D calls it). Stub: W6-C fills it; until then nothing is held.
#[allow(dead_code, reason = "a seam W6-D (after an update) calls")]
pub fn resend_after_update(_host: &Arc<Host>) {}

/// `accounts::login_input`: what the pane's sign-in card shows (C# `RenderLoginCard`'s state), for `CardInputs.login`. Called while the
/// cards are made, the state locked: it reads the accounts' own state and `snapshot`, never `host.view()`. Stub: W6-C fills it; until
/// then no card.
pub fn login_input(_host: &Host, _snapshot: &AppSnapshot, _pane: &RunSession) -> Option<LoginInput> {
    None
}

/// `loginAction{paneId, action, code?}` (C# `WebLoginAction`). Stub: W6-C fills it; until then it does nothing.
pub async fn login_action(_host: Arc<Host>, _args: Value) -> Reply {
    Ok(Value::Null)
}

/// A run event (C# `ReceiveLoginSignal`, before the pane takes the event): a lost sign-in's signal. Stub: W6-C fills it; until then
/// nothing is done.
pub fn on_run_event(_host: &Arc<Host>, _event: &RunEvent) {}

/// A request about to start (C# MainWindow.LoginRecovery.cs:49, `service.RequestStarting`). Stub: W6-C fills it; until then nothing
/// is done.
pub fn on_request_starting(_host: &Arc<Host>, _request: &StartRunRequest) {}
