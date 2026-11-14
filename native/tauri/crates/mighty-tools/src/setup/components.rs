//! The CLI rows the components box drew before it hosted the setup list (C# `ComponentStatus`, `ComponentAction`,
//! `ComponentSection`, ComponentSection.cs): one row per provider from the runtime, its install command and the copy confirmation.
//! The setup screen's blocked-pane install and the copyable commands still read it.

use std::time::Duration;

use mighty_run::model::{ProviderRuntime, RuntimeInfo};
use mighty_run::strings;

/// One component row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentStatus {
    pub id: String,
    pub title: String,
    /// installed · missing · attention · checking · unsupported
    pub state: String,
    pub version: Option<String>,
    pub detail: String,
    pub actions: Vec<ComponentAction>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentAction {
    /// install · copy-command · update
    pub id: String,
    pub title: String,
}

/// How often the open section looks again while a row still needs the user (an install lands outside the app).
pub const RECHECK_INTERVAL: Duration = Duration::from_secs(5);

pub fn section_title() -> String {
    strings::get("settings.components.sectionTitle")
}

/// One row per provider id (`Wire.Providers`) from the runtime, as the Mac's providerRow(_:).
pub fn section_rows(runtime: &RuntimeInfo) -> Vec<ComponentStatus> {
    let mods = runtime.mods.as_ref().map(|mods| mods.minimum_version.clone()).unwrap_or_else(|| "2.1.271".to_string());
    mighty_core::wire::PROVIDERS.iter().map(|id| provider_row(id, runtime.providers.iter().find(|p| p.id == *id), &mods)).collect()
}

/// The row's detail says what the Mac's does in the same case: a missing CLI its runtime's words and the install command to run in
/// a terminal (a CLI not yet looked for the Mac's fallback words), an old Claude the Mods version it needs, a ready CLI its own
/// ready sentence.
fn provider_row(id: &str, provider: Option<&ProviderRuntime>, mods_version: &str) -> ComponentStatus {
    let title = provider_title(id).to_string();
    // A Claude that answered with a version below the Mods minimum is there but out of date (the runtime counts it unavailable):
    // attention and the update, never the install.
    if id == "claude"
        && let Some(version) = provider.and_then(|p| p.version.as_deref())
        && !mighty_run::providers::supports_mods(Some(version))
    {
        return ComponentStatus {
            id: id.into(),
            title,
            state: "attention".into(),
            version: Some(version.into()),
            detail: strings::get_with("settings.components.modsNeedsVersion", &[("version", mods_version)]),
            actions: vec![ComponentAction { id: "update".into(), title: strings::get("settings.components.updateCLI") }],
        };
    }
    match provider {
        Some(provider) if provider.available => ComponentStatus {
            id: id.into(),
            title,
            state: "installed".into(),
            version: provider.version.clone(),
            detail: strings::get(match id {
                "claude" => "settings.components.readyClaude",
                "codex" => "settings.components.readyCodex",
                _ => "settings.components.ready",
            }),
            actions: vec![],
        },
        _ => {
            let command = install_command(id);
            let words = provider.map(|p| p.detail.clone()).unwrap_or_else(|| strings::get("provider.fallback.checkInstall"));
            let detail = match command {
                Some(command) => words + &strings::get_with("settings.components.installInTerminal", &[("command", command)]),
                None => words,
            };
            ComponentStatus {
                id: id.into(),
                title,
                state: "missing".into(),
                version: provider.and_then(|p| p.version.clone()),
                detail,
                actions: match command {
                    Some(_) => {
                        vec![ComponentAction { id: "copy-command".into(), title: strings::get("settings.components.copyInstallCommand") }]
                    }
                    None => vec![],
                },
            }
        }
    }
}

/// What the section says once a row's install command is on the clipboard.
pub fn copied_message(command: &str) -> String {
    strings::get_with("settings.components.copied", &[("command", command)])
}

/// Whether the open section has to look again: only while a row still needs the user.
pub fn needs_recheck(rows: &[ComponentStatus]) -> bool {
    rows.iter().any(|row| row.state != "installed")
}

/// Install commands, matching ComponentCatalog.installCommand() on macOS: npm for Claude Code, Codex, pi and opencode, Antigravity's
/// own PowerShell installer (it puts agy.exe in %LOCALAPPDATA%\agy\bin), Hermes Agent's run so its parameters bind.
pub fn install_command(provider: &str) -> Option<&'static str> {
    match provider {
        "claude" => Some("npm install -g @anthropic-ai/claude-code"),
        "codex" => Some("npm install -g @openai/codex"),
        "gemini" => Some("irm https://antigravity.google/cli/install.ps1 | iex"),
        "pi" => Some("npm install -g @mariozechner/pi-coding-agent"),
        "opencode" => Some("npm install -g @opencode/cli"),
        "hermes" => Some("& ([scriptblock]::Create((irm https://hermes-agent.nousresearch.com/install.ps1))) -NonInteractive"),
        _ => None,
    }
}

fn provider_title(id: &str) -> &str {
    match id {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "gemini" => "Antigravity CLI",
        "pi" => "pi",
        "opencode" => "opencode",
        "hermes" => "Hermes Agent",
        _ => id,
    }
}
