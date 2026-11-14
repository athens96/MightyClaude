//! The plugin browser, dialog D3, and `openPlugins` (C# MainWindow.Plugins.cs:50-469, MainWindow.WebShell.Shell.cs:84-88). Owner: W6-D.
//!
//! The seams the other parts call, with their final signatures. Each stub answers the resting value (no browser, nothing running)
//! until W6-D fills it.

use std::sync::Arc;

use crate::host::Host;

/// `plugins::open`: C# `OpenPluginBrowser(provider)` — the plugin browser of `provider` (`claude` or `codex`) for the active workspace,
/// a panel dialog, until it closes. `openPlugins` and the slash palette call it. Stub: W6-D fills it; until then nothing opens.
pub async fn open(_host: &Arc<Host>, _provider: &str) {}

/// `plugins::operations_running`: C# `pluginOperations.IsRunning` — a plugin install, update or removal is under way. Stub: W6-D fills
/// it; until then none.
#[allow(dead_code, reason = "a seam W6-B and W6-D calls")]
pub fn operations_running(_host: &Host) -> bool {
    false
}
