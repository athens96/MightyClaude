//! The model picker's rows and the custom model dialog: C# ModelLabelVerification's `PicksAndDisplaysDifferOnlyByPickerMarks` and
//! the picker part of `AReportedModelLabelsOnlyTheSelectionItWasReportedFor` (`ModelLabel.PickerOptions`, mighty-composer's
//! `menus::picker_options`), and `PaneView.CustomModel`'s dialog (`custom_model`). The catalogue is the run engine's reading of the
//! installed Claude Code's rows (macOS ModelLabelTests.claudeRows), as runs.rs hands it to the composer.

mod common;

use mighty_composer::custom_model;
use mighty_composer::menus::picker_options;
use mighty_composer::runtime::{CatalogModel, ModelCatalog};
use mighty_core::model::{RunSession, SessionUsage};

fn row(value: &str, display: &str, resolved: Option<&str>) -> CatalogModel {
    CatalogModel {
        value: value.into(),
        display_name: display.into(),
        description: String::new(),
        resolved_model: resolved.map(str::to_string),
    }
}

/// `ProviderCatalog.ClaudeCatalog` of the 2026-10-03 rows: the fallback's default row lent what the CLI's stands for.
fn claude_rows(l: &mighty_core::locale::Locale) -> ModelCatalog {
    let default = ModelCatalog::fallback("claude", l).models[0].clone();
    ModelCatalog {
        source: "cli".into(),
        models: vec![
            CatalogModel { resolved_model: Some("claude-opus-5-5".into()), ..default },
            row("opus", "Opus", Some("claude-opus-5-5")),
            row("claude-fable-5-1[1m]", "Fable", Some("claude-fable-5-1")),
            row("sonnet", "Sonnet", Some("claude-sonnet-5-5")),
            row("haiku", "Haiku", Some("claude-haiku-4-5-20251001")),
        ],
        notice: None,
    }
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

fn usage(model: &str, selected: &str) -> SessionUsage {
    let mut value = SessionUsage::from_json(&serde_json::json!({"provider": "claude", "source": "claude.stream-json", "tokenScope": "run", "updatedAt": "2026-10-03T00:00:00Z"})).unwrap();
    value.model = Some(model.into());
    value.selected_model = Some(selected.into());
    value
}

/// AReportedModelLabelsOnlyTheSelectionItWasReportedFor, its picker rows: values stay the catalogue's; only the selected row borrows
/// the reported version.
#[test]
fn a_reported_model_labels_only_the_selection_it_was_reported_for() {
    let l = common::ko();
    let fallback = ModelCatalog::fallback("claude", &l);
    let session = pane("opus", Some(usage("claude-opus-5-5", "opus")));
    let rows = picker_options(&session, &fallback, &l);
    assert_eq!(
        rows.iter().map(|r| r.value.clone()).collect::<Vec<_>>(),
        fallback.models.iter().map(|m| m.value.clone()).collect::<Vec<_>>(),
        "picker values"
    );
    let label = |value: &str| rows.iter().find(|r| r.value == value).unwrap().display_name.clone();
    assert_eq!(label("opus"), "Opus 5.5", "selected opus row");
    assert_eq!(label("sonnet"), "Sonnet", "sonnet row");
    assert_eq!(label("best"), "best", "best row");
}

#[test]
fn picks_and_displays_differ_only_by_picker_marks() {
    let l = common::ko();
    let catalog = claude_rows(&l);
    let saved = pane("claude-opus-4-1", None);
    let rows = picker_options(&saved, &catalog, &l);
    let last = rows.last().unwrap();
    assert_eq!(last.display_name, "Opus 4.1 · 저장된 모델", "a saved model the catalogue lacks");
    assert_eq!(last.value, "claude-opus-4-1", "its value");
    assert_eq!(mighty_core::model_label::selection(&saved, &catalog.options(), &l), "Opus 4.1", "its display");
    let bare = pane("default", None);
    let empty = ModelCatalog { source: "cli".into(), models: vec![], notice: None };
    assert_eq!(mighty_core::model_label::selection(&bare, &empty.options(), &l), "CLI 기본값", "default with no catalogue row");
    assert_eq!(
        picker_options(&bare, &empty, &l).iter().map(|r| r.display_name.clone()).collect::<Vec<_>>(),
        ["CLI 기본값"],
        "picker with no catalogue rows"
    );
}

/// `PaneView.CustomModel`: the dialog's words and the pane's model, none while it runs, and a name the select button takes trimmed or
/// refuses with the words under the field.
#[test]
fn the_custom_model_dialog_takes_a_model_name() {
    let l = common::ko();
    let session = pane("opus", None);
    let dialog = custom_model::dialog(&session, &l).expect("an idle pane opens it");
    assert_eq!(
        (dialog.title.as_str(), dialog.header.as_str(), dialog.value.as_str(), dialog.select.as_str()),
        ("모델 ID 입력", "모델 ID", "opus", "선택")
    );
    assert_eq!(dialog.cancel, l.get("settings.run.cancelButton"));
    assert!(custom_model::dialog(&RunSession { status: Some("running".into()), ..session }, &l).is_none(), "not while the pane runs");
    assert_eq!(custom_model::accept("  acme/custom-v2 ", &l), Ok("acme/custom-v2".into()));
    assert_eq!(custom_model::accept("bad model", &l), Err(l.get("composer.model.invalidName")));
    assert_eq!(custom_model::accept("   ", &l), Err("모델 이름이 올바르지 않습니다.".into()));
}
