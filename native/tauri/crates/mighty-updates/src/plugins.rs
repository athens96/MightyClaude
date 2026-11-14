//! Plugins (C# `ClaudePlugins`, `ClaudePluginReader`, `ClaudePluginBrowser`, `CodexPluginReader`, `PluginOperations`, `PluginStrings`).
//!
//! - [`model`]: the provider-neutral snapshot both readers answer with (`ClaudeInstalledPlugin`, `ClaudeCatalogPlugin`, …).
//! - [`support`]: parsing and shaping that depend only on data (`ClaudePluginSupport`).
//! - [`claude`], [`codex`]: the readers over each CLI's own plugin subcommands, with the two mutations macOS has — install one
//!   plugin, refresh one registered marketplace — and the automatic update (`PluginAutoUpdates.cs`).
//! - [`browser`]: what the plugin window shows and its install and refresh (`ClaudePluginPresentation.cs`), the smoke outcomes.
//! - [`operations`]: the app-wide one-at-a-time gate and the shared runner (`PluginOperations`), `PluginAutoUpdateResult`.
//! - [`strings`]: `PluginStrings`, `CodexPluginStrings`.
//! - [`smoke`]: the smoke's fixture snapshots and fake reader (MainWindow.Plugins.cs), so no CLI starts in a smoke.

pub mod browser;
pub mod claude;
pub mod codex;
pub mod model;
pub mod operations;
pub mod smoke;
pub mod strings;
pub mod support;

pub use browser::ClaudePluginBrowser;
pub use claude::ClaudePluginReader;
pub use codex::CodexPluginReader;
pub use model::{
    ClaudeCatalogPlugin, ClaudeInstalledPlugin, ClaudePluginMarketplace, ClaudePluginOperationResult, ClaudePluginSnapshot, PluginReader,
    status,
};
pub use operations::{PluginAutoUpdateResult, PluginOperations, ProviderReader};
