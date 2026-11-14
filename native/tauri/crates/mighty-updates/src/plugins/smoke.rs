//! The smoke's plugin fixtures (C# MainWindow.Plugins.cs `PluginSmokeSnapshot`, `CodexPluginSmokeSnapshot`,
//! `FakeMarketplaceReader`): the plugin window is driven with these, so no claude or codex process starts in a smoke.

use std::sync::Arc;

use mighty_core::model::Workspace;
use mighty_platform::BoxFuture;

use super::model::{
    ClaudeCatalogPlugin, ClaudeInstalledPlugin, ClaudePluginMarketplace, ClaudePluginOperationResult, ClaudePluginSnapshot, PluginReader,
    status,
};
use super::strings::{codex as codex_strings, plugin as strings};
use crate::runner::CancellationToken;

fn installed(id: &str, name: &str, marketplace: &str, scope: &str) -> ClaudeInstalledPlugin {
    ClaudeInstalledPlugin {
        plugin_id: id.into(),
        name: name.into(),
        marketplace: Some(marketplace.into()),
        scope: scope.into(),
        ..Default::default()
    }
}

fn catalog(id: &str, name: &str, marketplace: &str, kind: &str, description: &str) -> ClaudeCatalogPlugin {
    ClaudeCatalogPlugin {
        id: id.into(),
        name: name.into(),
        marketplace: marketplace.into(),
        source_kind: kind.into(),
        description: description.into(),
        ..Default::default()
    }
}

/// The Claude window's fixture: two installed plugins (a user one, another project's), three on offer, two marketplaces.
pub fn claude_snapshot() -> ClaudePluginSnapshot {
    ClaudePluginSnapshot {
        status: status::READY.into(),
        detail: strings::detail_ready(),
        cli_version: Some("2.1.271 (smoke fixture)".into()),
        installed: vec![
            ClaudeInstalledPlugin {
                version: Some("1.2.0".into()),
                enabled: Some(true),
                description: "Formats source files".into(),
                ..installed("fmt@sample", "fmt", "sample", "user")
            },
            ClaudeInstalledPlugin {
                enabled: Some(false),
                project_path: Some(r"C:\fixture\project".into()),
                description: "Lints source files".into(),
                ..installed("lint@other", "lint", "other", "project")
            },
        ],
        available: vec![
            ClaudeCatalogPlugin {
                version: Some("1.2.0".into()),
                ..catalog("fmt@sample", "fmt", "sample", "github", "Formats source files")
            },
            catalog("docs@sample", "docs", "sample", "git", "Writes docs"),
            catalog("ship@other", "ship", "other", "npm", "Ships builds"),
        ],
        marketplaces: vec![ClaudePluginMarketplace::new("other", "git"), ClaudePluginMarketplace::new("sample", "github")],
        updated_at: Some(mighty_core::time::now_o()),
        diagnostic_output: String::new(),
    }
}

/// The Codex window's fixture: a user-level plugin, a project-scope row the Codex window must not draw, two on offer.
pub fn codex_snapshot() -> ClaudePluginSnapshot {
    ClaudePluginSnapshot {
        status: status::READY.into(),
        detail: codex_strings::detail_ready(),
        cli_version: Some("codex-cli 0.153.4 (smoke fixture)".into()),
        installed: vec![
            ClaudeInstalledPlugin {
                version: Some("1.0.0".into()),
                enabled: Some(true),
                description: "Formats source files".into(),
                ..installed("format@sample", "format", "sample", "user")
            },
            ClaudeInstalledPlugin {
                project_path: Some(r"C:\fixture\project".into()),
                description: "Never listed under Codex".into(),
                ..installed("stray@sample", "stray", "sample", "project")
            },
        ],
        available: vec![
            ClaudeCatalogPlugin {
                version: Some("1.0.0".into()),
                ..catalog("format@sample", "format", "sample", "local", "Formats source files")
            },
            catalog("remote@openai-curated-remote", "remote", "openai-curated-remote", "remote", "Curated remote catalog entry"),
        ],
        marketplaces: vec![ClaudePluginMarketplace::new("sample", "git")],
        updated_at: Some(mighty_core::time::now_o()),
        diagnostic_output: String::new(),
    }
}

/// C# `FakeMarketplaceReader`: answers every read with its fixture, every install with success (or, holding, only once cancelled —
/// the smoke's cancel leg waits for the window's own cancel button instead of a real CLI), every refresh with one marketplace
/// refreshed.
pub struct FakeMarketplaceReader {
    fixture: Arc<ClaudePluginSnapshot>,
    hold_until_cancelled: bool,
}

impl FakeMarketplaceReader {
    pub fn new(fixture: ClaudePluginSnapshot, hold_until_cancelled: bool) -> FakeMarketplaceReader {
        FakeMarketplaceReader { fixture: Arc::new(fixture), hold_until_cancelled }
    }
}

impl PluginReader for FakeMarketplaceReader {
    fn snapshot(&self, _workspace: &Workspace, _cancel: CancellationToken) -> BoxFuture<'static, Arc<ClaudePluginSnapshot>> {
        let fixture = self.fixture.clone();
        Box::pin(async move { fixture })
    }

    fn shutdown(&self) {}

    fn install(
        &self,
        _plugin_id: &str,
        _scope: &str,
        _workspace: &Workspace,
        cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult> {
        let hold = self.hold_until_cancelled;
        Box::pin(async move {
            if hold {
                cancel.cancelled().await;
                return ClaudePluginOperationResult::new(status::CANCELLED, strings::operation_cancelled());
            }
            ClaudePluginOperationResult::new(status::SUCCEEDED, strings::install_succeeded())
        })
    }

    fn refresh_marketplace(
        &self,
        _marketplace: &str,
        _workspace: &Workspace,
        _cancel: CancellationToken,
    ) -> BoxFuture<'static, ClaudePluginOperationResult> {
        Box::pin(async move {
            ClaudePluginOperationResult::new(status::SUCCEEDED, strings::marketplaces_refreshed_template().replace("{count}", "1"))
        })
    }
}
