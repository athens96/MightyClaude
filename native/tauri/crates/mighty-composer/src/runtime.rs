//! What the composer reads of a pane's CLI (C# `ProviderRuntime`, `ModelCatalog`, `ProviderCapabilities`, Models.cs:499-511) and
//! the rules over them it needs while the run engine (W2) has not read the CLI yet: `ProviderCatalog.Fallback`,
//! `ProviderCatalog.Capabilities(provider)` with no version, `ProviderCatalog.BetaLabel`, and the pane-facing words of the WinUI host
//! (`PermissionLabel`, `EffortLabel`, `EffortPillLabel`, `PermissionHelp`, MainWindow.cs:759-776; `PermissionDescription` and
//! `SearchDescription`, MainWindow.RunSettings.cs:231-252; `StateLabel`, MainWindow.Navigation.cs:17).

use mighty_core::locale::Locale;
use mighty_core::model_label::{self, ModelOption};
use mighty_core::shell::Tone;

/// C# `ProviderCapabilities(Effort, PermissionModes, MaxTurns, MaxBudgetUsd, Resume, FastMode, WebSearch, NetworkAccess, Attachments)`.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ProviderCapabilities {
    pub effort: bool,
    pub permission_modes: Vec<String>,
    pub max_turns: bool,
    pub max_budget_usd: bool,
    pub resume: bool,
    pub fast_mode: bool,
    pub web_search: bool,
    pub network_access: bool,
    pub attachments: bool,
}

impl ProviderCapabilities {
    /// C# `ProviderCatalog.Capabilities(provider)` with no CLI version read: Claude without auto mode, Codex without on-request.
    pub fn fallback(provider: &str) -> Self {
        let modes: &[&str] = match provider {
            "pi" => &["manual", "fullAccess"],
            "hermes" => &["manual", "acceptEdits", "fullAccess"],
            // ["manual", "onRequest", "acceptEdits", "fullAccess"] without onRequest (SupportsCodexApprovals(null) is false).
            "codex" => &["manual", "acceptEdits", "fullAccess"],
            // PermissionModes(provider, includeAuto: SupportsMods(null) = false) for Claude, and every other agent.
            _ => &["manual", "plan", "acceptEdits", "fullAccess"],
        };
        ProviderCapabilities {
            effort: provider != "gemini",
            permission_modes: modes.iter().map(|m| m.to_string()).collect(),
            max_turns: provider == "claude",
            max_budget_usd: provider == "claude",
            resume: true,
            fast_mode: provider == "codex",
            web_search: provider == "codex",
            network_access: provider == "codex",
            attachments: true,
        }
    }
}

/// C# `ModelOption(Value, DisplayName, Description, ResolvedModel?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CatalogModel {
    pub value: String,
    pub display_name: String,
    pub description: String,
    pub resolved_model: Option<String>,
}

/// C# `ModelListNotice(Line, Help)`: the model menu's muted line about the list (`ModelListNotices.Notice`, KeptModelLists.cs:138).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelListNotice {
    pub line: String,
    pub help: String,
}

/// C# `ModelCatalog(Source, Models, Detail, ReadFailure?)` with its notice already worked out (the run engine's).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelCatalog {
    pub source: String,
    pub models: Vec<CatalogModel>,
    pub notice: Option<ModelListNotice>,
}

impl ModelCatalog {
    /// C# `ProviderCatalog.Fallback(provider)` (ProviderCatalog.cs:72): the built-in list before the CLI gave its own.
    pub fn fallback(provider: &str, l: &Locale) -> Self {
        let example = l.get("provider.fallback.exampleDescription");
        let models = model_label::fallback_models(provider, l)
            .into_iter()
            .map(|m| CatalogModel {
                description: if m.value == "default" { l.get("provider.fallback.defaultDescription") } else { example.clone() },
                value: m.value,
                display_name: m.display_name,
                resolved_model: m.resolved_model,
            })
            .collect();
        ModelCatalog { source: "fallback".into(), models, notice: None }
    }

    /// The models as mighty-core's `ModelLabel` reads them.
    pub fn options(&self) -> Vec<ModelOption> {
        self.models
            .iter()
            .map(|m| ModelOption { value: m.value.clone(), display_name: m.display_name.clone(), resolved_model: m.resolved_model.clone() })
            .collect()
    }

    pub fn find(&self, value: &str) -> Option<&CatalogModel> {
        self.models.iter().find(|m| m.value == value)
    }
}

/// C# `ProviderRuntime` as the composer reads it (Models.cs:511): given by the run engine once it read the CLI (W2).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderRuntime {
    pub name: String,
    pub available: bool,
    pub detail: String,
    pub catalog: ModelCatalog,
    pub capabilities: ProviderCapabilities,
}

/// C# `ProviderCatalog.IsBeta`.
pub fn is_beta(provider: &str) -> bool {
    mighty_core::shell::is_beta(provider)
}

/// C# `ProviderCatalog.IsLocalOnly`.
pub fn is_local_only(provider: &str) -> bool {
    matches!(provider, "pi" | "opencode" | "hermes")
}

/// C# `ProviderCatalog.BetaLabel`: the badge rides in the text of a menu row.
pub fn beta_label(provider: &str, text: &str, l: &Locale) -> String {
    if is_beta(provider) { format!("{text} · {}", l.get("badge.beta")) } else { text.to_string() }
}

/// C# `ProviderMark.Label`.
pub fn provider_label(provider: &str) -> &'static str {
    model_label::provider_label(provider)
}

/// C# `PermissionLabel` (MainWindow.cs:759): Claude's own mode names in every language; the shared labels for the others.
pub fn permission_label(provider: &str, mode: &str, l: &Locale) -> String {
    if provider == "claude" {
        return match mode {
            "plan" => "Plan mode",
            "acceptEdits" => "Accept file edits",
            "auto" => "Auto mode",
            "fullAccess" => "Bypass",
            _ => "Always ask",
        }
        .into();
    }
    l.get(match mode {
        "plan" => "permission.label.plan",
        "onRequest" => "permission.label.onRequest",
        "fullAccess" => "permission.label.fullAccess",
        "acceptEdits" if provider == "codex" => "permission.label.acceptEditsCodex",
        "acceptEdits" => "permission.label.acceptEdits",
        _ if matches!(provider, "codex" | "pi") => "permission.label.defaultCodex",
        _ => "permission.label.default",
    })
}

/// C# `EffortLabel` (MainWindow.cs:773).
pub fn effort_label(effort: &str, l: &Locale) -> String {
    match effort {
        "off" => "Off".into(),
        "minimal" => "Minimal".into(),
        "low" => "Low".into(),
        "medium" => "Medium".into(),
        "high" => "High".into(),
        "xhigh" => "XHigh".into(),
        "max" => "Max".into(),
        "ultra" => l.get("composer.effort.ultra"),
        _ => "Auto".into(),
    }
}

/// C# `EffortPillLabel` (MainWindow.cs:775).
pub fn effort_pill_label(effort: &str, l: &Locale) -> String {
    if effort == "ultra" { "Ultra".into() } else { effort_label(effort, l) }
}

/// C# `PermissionHelp` (MainWindow.cs:776).
pub fn permission_help(provider: &str, mode: &str, l: &Locale) -> String {
    match mode {
        "onRequest" => l.get("permission.codex.onRequest"),
        "manual" if provider == "codex" => l.get("composer.permissionHelp.manualCodex"),
        "manual" => l.get("composer.permissionHelp.manual"),
        "plan" => l.get("composer.permissionHelp.plan"),
        "acceptEdits" if provider == "codex" => l.get("composer.permissionHelp.acceptEditsCodex"),
        "acceptEdits" => l.get("composer.permissionHelp.acceptEdits"),
        "auto" => l.get("composer.permissionHelp.auto"),
        "fullAccess" => l.get("composer.permissionHelp.fullAccess"),
        _ => String::new(),
    }
}

/// C# `PermissionDescription` (MainWindow.RunSettings.cs:238): a permission mode in words, as the run settings say it.
pub fn permission_description(provider: &str, mode: &str, l: &Locale) -> String {
    let key = match provider {
        "claude" => match mode {
            "plan" => "permission.claude.plan",
            "acceptEdits" => "permission.claude.acceptEdits",
            "auto" => "permission.claude.auto",
            "fullAccess" => "permission.claude.fullAccess",
            _ => "permission.claude.default",
        },
        "pi" => {
            if mode == "fullAccess" {
                "permission.pi.fullAccess"
            } else {
                "permission.pi.default"
            }
        }
        "opencode" => match mode {
            "plan" => "permission.opencode.plan",
            "acceptEdits" => "permission.opencode.acceptEdits",
            "fullAccess" => "permission.opencode.fullAccess",
            _ => "permission.opencode.default",
        },
        "hermes" => match mode {
            "acceptEdits" => "permission.hermes.acceptEdits",
            "fullAccess" => "permission.hermes.fullAccess",
            _ => "permission.hermes.default",
        },
        _ if mode == "fullAccess" => "permission.other.fullAccess",
        "codex" => match mode {
            "onRequest" => "permission.codex.onRequest",
            "acceptEdits" => "permission.codex.acceptEdits",
            _ => "permission.codex.default",
        },
        "gemini" if mode == "manual" => "permission.antigravity.default",
        "gemini" if mode == "acceptEdits" => "permission.antigravity.acceptEdits",
        _ => match mode {
            "plan" => "permission.other.plan",
            "acceptEdits" => "permission.other.acceptEdits",
            _ => "permission.other.default",
        },
    };
    l.get(key)
}

/// C# `SearchDescription` (MainWindow.RunSettings.cs:231).
pub fn search_description(value: &str, l: &Locale) -> String {
    l.get(match value {
        "disabled" => "settings.run.webSearchDescriptionDisabled",
        "cached" => "settings.run.webSearchDescriptionCached",
        "live" => "settings.run.webSearchDescriptionLive",
        _ => "settings.run.webSearchDescriptionDefault",
    })
}

/// C# `StateLabel` (MainWindow.Navigation.cs:17): a status in the language.
pub fn state_label(state: &str, l: &Locale) -> String {
    l.get(Tone::of(state).word_key())
}
