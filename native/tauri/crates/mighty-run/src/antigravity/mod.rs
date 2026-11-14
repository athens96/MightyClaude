//! Antigravity CLI (`agy`) in the run engine (W5-C): C# AntigravityPermissions.cs and the records' helpers of AntigravityRecords.cs.
//!
//! A headless `agy -p` turn cannot ask: what its review mode would have asked about is declined and the turn goes on. The stream says
//! exactly what was declined, so once the turn is over the pane asks the way a Claude card does, and an allow becomes a rule in agy's own
//! settings (`%USERPROFILE%\.gemini\antigravity-cli\settings.json`, `permissions.allow`) for a follow-up turn in the same conversation.
//!
//! | C# | here |
//! |---|---|
//! | `AntigravityRecords` (the transcript's paths and helpers the parser, the watcher and the history read) | [`records`] |
//! | `AntigravityDeniedAction`, `AntigravityPermissionRequest`, `AntigravityAllowGrant`, `AntigravityRuleSafety`, `AntigravityDenialCollector`, `AntigravityPermissionBoard`, `AntigravityPermissionFlow` | [`permissions`] |
//! | `AntigravitySettingsText` (the byte-exact edits of `permissions.allow`), `AntigravitySettingsException` | [`settings`] |
//! | `AntigravityRuleLeases` (the rules the app holds, its ledger) | [`leases`] |
//! | `AntigravityMigration.ModelUnavailable` | [`model_unavailable`] |
//!
//! The parser's half (`OutputParser.Antigravity`) is `parser::antigravity`; the sub-agent watcher (`AntigravitySubagentWatcher`,
//! `AntigravityChildRecord`) is mighty-graph's, beside the tracker it feeds; the cards in the desktop service are `service`'s.

pub mod leases;
pub mod permissions;
pub mod records;
pub mod settings;

pub use leases::{AntigravityRuleLeases, Lease};
pub use permissions::{
    AllowGrant, Continuation, DenialCollector, DeniedAction, OnceReason, PermissionBoard, PermissionDecision, PermissionRequest,
    RuleVerdict,
};
pub use settings::SettingsError;

use crate::model::ModelCatalog;

/// C# `AntigravityMigration.ModelUnavailable`: a pane's model agy's live list no longer has, so the run starts on agy's default instead.
/// Only the list the CLI gave on its latest read decides; the fallback list, and a kept older list, may lag behind it.
pub fn model_unavailable(model: &str, catalog: &ModelCatalog) -> bool {
    model != "default" && catalog.is_fresh() && !catalog.models.iter().any(|option| option.value == model)
}
