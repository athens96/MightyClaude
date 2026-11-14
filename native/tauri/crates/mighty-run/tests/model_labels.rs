//! C# ModelLabelVerification's catalogue checks over the run engine's lists (`CliCatalogueRowsCarryTheirVersion`,
//! `FallbackCatalogueNeverInventsAVersion`, and `AReportedModelLabelsOnlyTheSelectionItWasReportedFor` but for its picker rows, which
//! are mighty-composer's `picker_options` and are checked there). `EveryFixtureCaseLabelsAsCommitted` is mighty-core's
//! (`model_label`), `GraphCapsulesAndResumeRowsReadVersions` mighty-graph's and mighty-history's; `StatusLineNamesTheCurrentSelection`
//! reads `ModelLabel.StatusLine`, the status line's (W6).

mod common;

use mighty_core::locale::Locale;
use mighty_core::model::{AppSnapshot, RunSession, SessionUsage};
use mighty_core::model_label;
use mighty_core::time::DateTimeOffset;
use mighty_run::model::{ModelCatalog, ModelOption, RunEvent};
use mighty_run::providers;
use serde_json::Value;

fn ko() -> Locale {
    Locale::new("ko")
}

/// The catalogue the installed Claude Code reported on 2026-10-03 (macOS ModelLabelTests.claudeRows).
fn claude_rows() -> ModelCatalog {
    let rows: Vec<Value> = serde_json::from_str(
        r#"[
          {"value": "default", "resolvedModel": "claude-opus-5-5", "displayName": "Default (recommended)", "description": "Opus 5.5 · Best for everyday, complex tasks"},
          {"value": "opus", "resolvedModel": "claude-opus-5-5", "displayName": "Opus"},
          {"value": "claude-fable-5-1[1m]", "resolvedModel": "claude-fable-5-1", "displayName": "Fable"},
          {"value": "sonnet", "resolvedModel": "claude-sonnet-5-5", "displayName": "Sonnet"},
          {"value": "haiku", "resolvedModel": "claude-haiku-4-5-20251001", "displayName": "Haiku"}
        ]"#,
    )
    .unwrap();
    providers::claude_catalog(&rows)
}

fn labels(models: &[ModelOption]) -> Vec<String> {
    models.iter().map(|m| model_label::option(&m.label_option(), None)).collect()
}

fn options(catalog: &ModelCatalog) -> Vec<model_label::ModelOption> {
    catalog.models.iter().map(ModelOption::label_option).collect()
}

fn usage(model: Option<&str>, selected: Option<&str>) -> SessionUsage {
    let mut value = SessionUsage::from_json(&serde_json::json!({"provider": "claude", "source": "claude.stream-json", "tokenScope": "run", "updatedAt": "2026-10-03T00:00:00Z"})).unwrap();
    value.model = model.map(str::to_string);
    value.selected_model = selected.map(str::to_string);
    value
}

fn pane(model: &str, usage: Option<SessionUsage>) -> RunSession {
    RunSession {
        workspace_id: Some("w".into()),
        title: Some("t".into()),
        model: Some(model.into()),
        session_usage: usage,
        ..RunSession::default()
    }
}

#[test]
fn cli_catalogue_rows_carry_their_version() {
    common::korean();
    let catalog = claude_rows();
    assert_eq!(catalog.source, "cli", "catalogue source");
    assert_eq!(
        labels(&catalog.models),
        ["Claude 설정 따름 · Opus 5.5", "Opus 5.5", "Fable 5.1 (1M)", "Sonnet 5.5", "Haiku 4.5"],
        "Claude picker rows"
    );
    // The composer button reads the same row the picker drew.
    let rows = options(&catalog);
    for option in &catalog.models {
        assert_eq!(
            model_label::in_catalog(&option.value, &rows, None),
            model_label::option(&option.label_option(), None),
            "button label of {}",
            option.value
        );
    }
    // Only the label changes: the values sent to the CLI stay the aliases.
    assert_eq!(
        catalog.models.iter().map(|m| m.value.as_str()).collect::<Vec<_>>(),
        ["default", "opus", "claude-fable-5-1[1m]", "sonnet", "haiku"]
    );

    let mut codex = vec![providers::fallback("codex").models[0].clone()];
    let data: Vec<Value> =
        serde_json::from_str(r#"[{"model": "gpt-6.1-sol", "displayName": "GPT-6.1-Sol", "isDefault": true}, {"model": "gpt-5.2-codex", "displayName": "GPT-5.2-Codex"}]"#)
            .unwrap();
    providers::add_codex_models(&mut codex, &data);
    assert_eq!(labels(&codex), ["Codex 설정 따름 · GPT-6.1 Sol", "GPT-6.1 Sol", "GPT-5.2 Codex"], "Codex picker rows");
    assert_eq!(codex.iter().map(|m| m.value.as_str()).collect::<Vec<_>>(), ["default", "gpt-6.1-sol", "gpt-5.2-codex"], "Codex values");
}

#[test]
fn fallback_catalogue_never_invents_a_version() {
    common::korean();
    let fallback = options(&providers::fallback("claude"));
    let label = |model: &str, hint: Option<&str>| model_label::in_catalog(model, &fallback, hint);
    assert_eq!(label("opus", None), "Opus", "fallback opus");
    assert_eq!(label("default", None), "Claude 설정 따름", "fallback default");
    assert_eq!(label("opusplan", None), "opusplan", "fallback opusplan");
    // The pane's own reported model may give a family alias its version …
    assert_eq!(label("opus", Some("claude-opus-5-5")), "Opus 5.5", "opus with its reported id");
    // … but never another family's, and never default/best theirs.
    assert_eq!(label("sonnet", Some("claude-opus-5-5")), "Sonnet", "sonnet with an Opus report");
    assert_eq!(label("default", Some("claude-opus-5-5")), "Claude 설정 따름", "default with a report");
    assert_eq!(label("best", Some("claude-opus-5-5")), "best", "best with a report");
    assert_eq!(
        model_label::in_catalog("gemini-3-pro-preview", &options(&providers::fallback("gemini")), None),
        "Gemini 3 Pro",
        "Gemini fallback row"
    );
    assert_eq!(model_label::in_catalog("gpt-6-astra", &[], None), "GPT-6 Astra", "an id with no catalogue");
    assert_eq!(label("claude-sonnet-5-5", None), "Sonnet 5.5", "a stored full id");
}

/// AReportedModelLabelsOnlyTheSelectionItWasReportedFor, but for its picker rows (mighty-composer's page_models checks them).
#[test]
fn a_reported_model_labels_only_the_selection_it_was_reported_for() {
    common::korean();
    let fallback = options(&providers::fallback("claude"));
    let session = pane("opus", Some(usage(Some("claude-opus-5-5"), Some("opus"))));
    assert_eq!(model_label::selection(&session, &fallback, &ko()), "Opus 5.5", "selection");
    // Reported for another selection: stale, so no version for it.
    let switched = RunSession { model: Some("sonnet".into()), ..session.clone() };
    assert_eq!(model_label::reported_model(&switched), None, "reported model after a switch");
    assert_eq!(model_label::selection(&switched, &fallback, &ko()), "Sonnet", "selection after a switch");
    let stale = pane("opus", Some(usage(Some("claude-opus-4-5"), Some("default"))));
    assert_eq!(model_label::selection(&stale, &fallback, &ko()), "Opus", "a report for an earlier selection");
    // Usage saved before the selection was recorded gives no hint.
    let legacy = pane("opus", Some(usage(Some("claude-opus-5-5"), None)));
    assert_eq!(model_label::reported_model(&legacy), None, "legacy reported model");
    assert_eq!(model_label::selection(&legacy, &fallback, &ko()), "Opus", "legacy selection");

    // Recording usage remembers the selection it was reported for.
    let recorded_pane = RunSession { id: Some("s1".into()), ..pane("opus", None) };
    let state = AppSnapshot { sessions: Some(vec![std::sync::Arc::new(recorded_pane)]), ..AppSnapshot::default() };
    let event = RunEvent { usage: Some(usage(Some("claude-opus-5-5"), None)), ..RunEvent::new("s1", "usage") };
    let after = mighty_run::apply::apply(&state, &event, DateTimeOffset::now());
    let recorded = &after.sessions()[0];
    assert_eq!(recorded.session_usage.as_ref().and_then(|u| u.selected_model.as_deref()), Some("opus"), "the recorded selection");
    assert_eq!(model_label::reported_model(recorded), Some("claude-opus-5-5"), "the recorded report");
    // A bad selection carries none; an older save carries none.
    assert_eq!(
        mighty_run::usage::normalized(&usage(Some("x"), Some("bad model"))).and_then(|u| u.selected_model),
        None,
        "an invalid selection"
    );
    let old = SessionUsage::from_json(
        &serde_json::json!({"provider": "claude", "source": "s", "tokenScope": "run", "updatedAt": "2026-10-03T00:00:00Z"}),
    )
    .unwrap();
    assert_eq!(old.selected_model, None, "an older save");
}
