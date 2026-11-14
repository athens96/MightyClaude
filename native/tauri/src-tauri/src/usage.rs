//! Account usage, `usageRefresh` and `usageDirect` (C# MainWindow.AccountUsage.cs, MainWindow.WebShell.Shell.cs:422-452). Owner: W6-C.
//!
//! The seams the methods call, with their final signatures. Each stub does nothing until W6-C fills it.

use std::sync::Arc;

use crate::host::Host;

/// `usageRefresh{force}`: C# `QueueAccountUsageRefresh(force)`. Stub: W6-C fills it; until then nothing is read.
pub fn refresh(_host: &Arc<Host>, _force: bool) {}

/// `usageDirect{on}`: C# `SetUsageDirectLookup(on)` — the direct HTTPS lookup switched (saved only with `--allow-state-write`). Stub:
/// W6-C fills it; until then nothing changes.
pub async fn direct(_host: &Arc<Host>, _on: bool) -> Result<(), String> {
    Ok(())
}
