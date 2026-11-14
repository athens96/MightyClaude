//! The smoke's setup, components and status line steps (C# MainWindow.SetupSmoke.cs, MainWindow.Smoke.cs:105-170). Owner: W6-B.
//!
//! The step's contract (smoke.rs): [`start`] begins it and answers whether it runs; a running step ends by reporting once — its probe
//! in the page invoking `smoke_probe`, or host-side code calling `Smoke::probed` — and [`checks`] reads that report. The probe reaches
//! the host's side through the `smoke_setup` command. Each stub answers its resting value until W6-B fills it.

use std::sync::Arc;

use serde_json::Value;

use crate::host::Host;

/// Starts the step; false when it has nothing to check (the smoke goes on to the next). Stub: W6-B fills it; until then no step.
pub fn start(_host: &Arc<Host>) -> bool {
    false
}

/// The step's checks from its report, each `(name, passed)` in `smoke-result.json`'s `checks`. Stub: W6-B fills it; until then none.
pub fn checks(_report: &Value) -> Vec<(&'static str, bool)> {
    vec![]
}

/// The step's host side, for its probe (`invoke('smoke_setup', {action, args})`); smoke only. Stub: W6-B fills it; until then every
/// action is unknown.
#[tauri::command]
pub async fn smoke_setup(action: String, args: Value, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    let _ = args;
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    Err(format!("unknown smoke action {action}"))
}
