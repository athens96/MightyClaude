//! The model lists' own checks: C# ModelListProbeVerification's kept-lists file, menu line, kept list and sign-in checks
//! (`TheKeptListsFileIsBoundedAndReadTolerantly`, `TheMenuLineSaysWhichListItIs`, `AKeptListRetiresNothing`,
//! `ASignInOrSignOutDropsThatProvidersLists`), ComposerParityVerification's `ModelSelectionFollowsTheNewList` and
//! `ModelListIsReadAgainWhenItMustBe`, and WindowsParityVerification's `ModelResetReadsEveryWorkspaceAndSaysHowItWent`. The reads
//! over a real child are in providers.rs.

mod common;

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use mighty_core::model::{AppSnapshot, LogEntry, RunSession, RunSettings, Workspace};
use mighty_core::time::DateTimeOffset;
use mighty_run::kept_lists::{self, Entry, KeptModelLists, ModelListNotice};
use mighty_run::model::{ModelCatalog, ModelOption, ProviderRuntime};
use mighty_run::model_selection::{
    self, Cancellation, CancellationSource, Changed, Clock, Loader, ModelCatalogRefresh, ModelContext, ModelSelectionResolution, ResetRead,
};
use mighty_run::{providers, strings};
use serde_json::{Value, json};

fn model(value: &str, resolved: Option<&str>, supports_effort: Option<bool>, levels: Option<&[&str]>) -> ModelOption {
    ModelOption {
        value: value.into(),
        display_name: value.into(),
        description: String::new(),
        resolved_model: resolved.map(str::to_string),
        supports_effort,
        supported_effort_levels: levels.map(|l| l.iter().map(|s| s.to_string()).collect()),
    }
}

fn plain(value: &str) -> ModelOption {
    model(value, None, None, None)
}

fn cli(models: Vec<ModelOption>) -> ModelCatalog {
    ModelCatalog::new("cli", models, "")
}

fn kept_resolution(model: &str, effort: &str) -> ModelSelectionResolution {
    ModelSelectionResolution::new(model, effort)
}

fn runtime(provider: &str, catalog: ModelCatalog) -> ProviderRuntime {
    ProviderRuntime {
        id: provider.into(),
        name: providers::name(provider).into(),
        available: true,
        version: Some("2.1.271".into()),
        detail: String::new(),
        model_catalog: catalog,
        capabilities: providers::capabilities(provider, None),
    }
}

fn now() -> DateTimeOffset {
    DateTimeOffset::now()
}

fn seconds(value: i64) -> i64 {
    value * 10_000_000
}

fn plus(at: DateTimeOffset, ticks: i64) -> DateTimeOffset {
    DateTimeOffset::from_utc_ticks(at.utc_ticks() + ticks)
}

// ── The kept lists file ─────────────────────────────────────────────────────────────────────────────────────────────────

#[test]
fn the_kept_lists_file_is_bounded_and_read_tolerantly() {
    let directory = common::temp("kept-lists");
    let file = directory.join(kept_lists::FILE_NAME);
    assert!(KeptModelLists::load(Some(&file)).entries().is_empty() && KeptModelLists::load(None).entries().is_empty(), "no file, no lists");
    std::fs::write(&file, "not json").unwrap();
    assert!(KeptModelLists::load(Some(&file)).entries().is_empty(), "a broken file reads as none");
    std::fs::write(&file, r#"{"version":2,"lists":[]}"#).unwrap();
    assert!(KeptModelLists::load(Some(&file)).entries().is_empty(), "another version reads as none");
    let stamp = kept_lists::timestamp(now());
    let good = || {
        json!({"provider": "gemini", "workspace": "/w", "listedAt": stamp, "detail": "d",
               "models": [{"value": "gemini-4-carbon-medium", "displayName": "Carbon", "description": ""}]})
    };
    let with = |change: &dyn Fn(&mut serde_json::Map<String, Value>)| {
        let mut row = good();
        change(row.as_object_mut().unwrap());
        row
    };
    let rows = json!([
        good(),
        with(&|row| {
            row.insert("models".into(), json!([{"value": "--bad", "displayName": "x", "description": ""}]));
        }),
        with(&|row| {
            row.insert("provider".into(), json!("hermes"));
        }),
        with(&|row| {
            row.insert("provider".into(), json!("gemini-cli"));
        }),
        with(&|row| {
            row.insert("listedAt".into(), json!("yesterday"));
            row.insert("workspace".into(), json!("/x"));
        }),
        with(&|row| {
            row.insert("workspace".into(), json!("w"));
        }),
        with(&|row| {
            row.insert("models".into(), json!([{"value": "gemini-4-argon-low"}]));
        }),
        "text",
        {"provider": "pi"}
    ]);
    std::fs::write(&file, json!({"version": 1, "lists": rows}).to_string()).unwrap();
    let loaded = KeptModelLists::load(Some(&file));
    assert!(
        loaded.entries().len() == 1
            && loaded.entries()[0].models.iter().map(|m| m.value.as_str()).collect::<Vec<_>>() == ["gemini-4-carbon-medium"],
        "only the entry that checks out: {}",
        loaded.entries().len()
    );
    // A name written twice in an entry refuses it, as the Mac's decoder does; once in the file's root the last one counts.
    let repeated = format!(
        r#"{{"version":1,"lists":[{{"provider":"pi","provider":"gemini","listedAt":"{stamp}","detail":"d","models":[{{"value":"m","displayName":"M","description":""}}]}}],"lists":[]}}"#
    );
    std::fs::write(&file, repeated).unwrap();
    assert!(KeptModelLists::load(Some(&file)).entries().is_empty(), "a repeated name refuses the entry, and the last root lists count");
    // Over the size bound the file is not read at all.
    std::fs::write(&file, vec![0u8; kept_lists::MAXIMUM_BYTES + 1]).unwrap();
    assert!(KeptModelLists::load(Some(&file)).entries().is_empty(), "an oversized file is not read");

    let mut lists = KeptModelLists::default();
    let start = now();
    let catalog = ModelCatalog::new("cli", vec![ModelOption::new("m1", "M1", "")], "d");
    let first = lists.remember(&catalog, "pi", Some("/w"), start, None);
    // The same list again soon after: kept in memory with its new time, not written again.
    let again = lists.remember(&catalog, "pi", Some("/w"), plus(start, seconds(10)), Some(start));
    assert!(first && !again, "a list is written when it is new, not when it is read again unchanged");
    assert_eq!(
        lists.list("pi", Some("/w")).map(|e| e.listed_at.clone()),
        Some(kept_lists::timestamp(plus(start, seconds(10)))),
        "with the time of its latest read"
    );
    let rewrite = kept_lists::REWRITE_AGE.as_secs() as i64;
    let later = lists.remember(&catalog, "pi", Some("/w"), plus(start, seconds(rewrite + 11)), Some(start));
    let mut more = catalog.clone();
    more.models.push(ModelOption::new("m2", "M2", ""));
    let different = lists.remember(&more, "pi", Some("/w"), plus(start, seconds(400)), Some(plus(start, seconds(390))));
    assert!(later && different, "written again once the file is five minutes old, or when the list changed");
    // Neither a kept nor a built-in list is ever kept.
    let kept_again = lists.remember(&kept_lists::kept(&lists.entries()[0].clone(), "r"), "pi", Some("/w"), start, None);
    let built_in = lists.remember(&providers::fallback("pi"), "pi", Some("/v"), start, None);
    assert!(!kept_again && !built_in && lists.entries().len() == 1, "a kept or built-in list is never kept");
    for index in 0..40 {
        lists.remember(&catalog, "codex", Some(&format!("/w{index}")), plus(start, seconds(500 + index)), None);
    }
    assert!(
        lists.entries().len() == kept_lists::MAXIMUM_ENTRIES && lists.entries()[0].workspace.as_deref() == Some("/w39"),
        "at most 24, the newest first"
    );
    // The newest of a provider stands in for a workspace that never read one.
    assert!(
        lists.list("codex", Some("/elsewhere")).and_then(|e| e.workspace.as_deref()) == Some("/w39")
            && lists.list("claude", None).is_none(),
        "the provider's newest stands in"
    );
    std::fs::write(&file, lists.encoded()).unwrap();
    assert_eq!(
        String::from_utf8(KeptModelLists::load(Some(&file)).encoded()).unwrap(),
        String::from_utf8(lists.encoded()).unwrap(),
        "the file reads back as written"
    );
    // The file is Wire.Json: camelCase, nulls left out, every non-ASCII character escaped.
    let entry = Entry {
        provider: "claude".into(),
        workspace: None,
        listed_at: "2026-11-11T02:00:00Z".into(),
        detail: "한".into(),
        models: vec![ModelOption::new("opus", "Opus", "")],
    };
    assert_eq!(
        String::from_utf8(KeptModelLists::new(vec![entry]).encoded()).unwrap(),
        format!(
            r#"{{"version":1,"lists":[{{"provider":"claude","listedAt":"2026-11-11T02:00:00Z","detail":"{}uD55C","models":[{{"value":"opus","displayName":"Opus","description":""}}]}}]}}"#,
            '\\'
        )
    );
    let _ = std::fs::remove_dir_all(&directory);
}

/// The kept lists' file is readable by this user only (C#'s `File.SetUnixFileMode` off Windows).
#[cfg(unix)]
#[test]
fn the_kept_lists_file_is_readable_by_this_user_only() {
    use std::os::unix::fs::PermissionsExt as _;
    let directory = common::temp("kept-mode");
    let file = directory.join(kept_lists::FILE_NAME);
    kept_lists::write(&file, b"{}").unwrap();
    assert_eq!(std::fs::metadata(&file).unwrap().permissions().mode() & 0o777, 0o600);
    let _ = std::fs::remove_dir_all(&directory);
}

#[test]
fn a_kept_lists_date_reads_as_the_c_sharp_formats_do() {
    let utc = kept_lists::date(Some("2026-11-11T02:00:00Z")).unwrap();
    assert_eq!(kept_lists::timestamp(utc), "2026-11-11T02:00:00Z");
    assert_eq!(kept_lists::date(Some("2026-11-11T11:00:00+09:00")), Some(utc), "an offset is read");
    assert_eq!(kept_lists::date(Some("2026-11-11T02:00:00")), Some(utc), "no zone reads as UTC");
    assert_eq!(kept_lists::date(Some("2026-11-11T02:00:00.5Z")).map(|d| d.utc_ticks() - utc.utc_ticks()), Some(5_000_000), "a fraction");
    for bad in ["", "yesterday", "2026-11-11", "2026-13-11T02:00:00Z", "2026-11-11T02:00:00.12345678Z", "2026-11-11 02:00:00Z"] {
        assert!(kept_lists::date(Some(bad)).is_none(), "{bad} is no date");
    }
    assert!(kept_lists::date(Some(&format!("2026-11-11T02:00:00{}", "0".repeat(60)))).is_none(), "longer than 64 is no date");
}

// ── A kept list is shown, never trusted ─────────────────────────────────────────────────────────────────────────────────

/// AKeptListRetiresNothing, but for its two `AntigravityMigration.ModelUnavailable` checks (W5-C's Antigravity port).
#[test]
fn a_kept_list_retires_nothing() {
    let fresh = cli(vec![ModelOption::new("default", "Default", ""), ModelOption::new("claude-fable-5-2", "Fable 5.2", "")]);
    let mut kept = cli(vec![ModelOption::new("default", "Default", "")]);
    kept.read_failure = Some("claude: 종료 코드 1".into());
    kept.listed_at = Some(kept_lists::timestamp(now()));
    assert!(!kept.is_fresh() && fresh.is_fresh() && !providers::fallback("claude").is_fresh(), "only a list the latest read gave is fresh");
    assert_eq!(
        model_selection::reconcile("claude-fable-5-2", "high", Some(&fresh), &kept, Some("claude")),
        kept_resolution("claude-fable-5-2", "high"),
        "a kept list keeps the pane's choice"
    );
    let unmarked = ModelCatalog { read_failure: None, ..kept.clone() };
    assert_eq!(
        model_selection::reconcile("claude-fable-5-2", "high", Some(&fresh), &unmarked, Some("claude")),
        kept_resolution("default", "default"),
        "a fresh list without it retires it"
    );
}

#[test]
fn the_menu_line_says_which_list_it_is() {
    common::korean();
    let at = now();
    assert!(kept_lists::notice(&cli(vec![]), None).is_none(), "a fresh list: no line");
    // Not read yet (or a read another one replaced): no line.
    assert!(kept_lists::notice(&providers::fallback("claude"), None).is_none(), "nothing read yet: no line");
    let built_in = providers::fallback("claude").with_failure("claude: 종료 코드 1".into());
    assert_eq!(
        kept_lists::notice(&built_in, Some(at)),
        Some(ModelListNotice {
            line: "기본 목록 · CLI에서 목록을 읽지 못했습니다".into(), help: "claude: 종료 코드 1".into()
        }),
        "the built-in list"
    );
    let mut kept = cli(vec![]);
    kept.read_failure = Some("agy models: 종료 코드 1".into());
    kept.listed_at = Some(kept_lists::timestamp(plus(at, seconds(-300))));
    assert_eq!(
        kept_lists::notice(&kept, Some(at)),
        Some(ModelListNotice {
            line: "이전 목록 · 새로 읽지 못했습니다 (5분 전 목록)".into(), help: "agy models: 종료 코드 1".into()
        }),
        "a kept list"
    );
}

// ── Reading again ───────────────────────────────────────────────────────────────────────────────────────────────────────

fn changed_nothing() -> Changed {
    Arc::new(|_: &ModelContext, _: Option<&ProviderRuntime>, _: &ProviderRuntime| {})
}

/// A sign-in or sign-out drops every list of that provider in hand, whatever its workspace, and only that provider's.
#[tokio::test(flavor = "multi_thread")]
async fn a_sign_in_or_sign_out_drops_that_providers_lists() {
    let loads: Arc<Mutex<HashMap<ModelContext, usize>>> = Arc::default();
    let counted = loads.clone();
    let loader: Loader = Arc::new(move |key: ModelContext| {
        let number = {
            let mut loads = counted.lock().unwrap();
            let count = loads.entry(key.clone()).or_insert(0);
            *count += 1;
            *count
        };
        let value =
            runtime(&key.provider, cli(vec![ModelOption::new("default", "Default", ""), ModelOption::new(&format!("m{number}"), "M", "")]));
        Box::pin(async move { Ok(value) })
    });
    let refresh = ModelCatalogRefresh::new(tokio::runtime::Handle::current(), loader, changed_nothing(), None);
    let here = ModelContext::new("a", "/a", "gemini");
    let there = ModelContext::new("b", "/b", "gemini");
    let claude = ModelContext::new("a", "/a", "claude");
    for key in [&here, &there, &claude] {
        refresh.request(key, false, false).wait().await;
    }
    assert!(
        loads.lock().unwrap().values().all(|count| *count == 1) && refresh.is_current(&here) && refresh.is_current(&there),
        "each list read once"
    );
    refresh.invalidate(Some("gemini"));
    assert!(
        !refresh.is_current(&here) && !refresh.is_current(&there) && refresh.is_current(&claude),
        "only that provider's lists are old, in every workspace"
    );
    // A run about to start reads anew instead of using the old list for its minute.
    let last = |value: Option<ProviderRuntime>| value.and_then(|r| r.model_catalog.models.last().map(|m| m.value.clone()));
    assert!(
        last(refresh.request(&there, false, false).wait().await).as_deref() == Some("m2")
            && last(refresh.request(&claude, false, false).wait().await).as_deref() == Some("m1"),
        "the next request reads again"
    );
    refresh.shutdown();
}

// M/Tests/ModelRefreshTests.swift:173-216, M/AppStore+ModelCatalogs.swift:47-63.
#[test]
fn model_selection_follows_the_new_list() {
    common::korean();
    use model_selection::{adjusted, is_official_claude_model, reconcile};
    let old = cli(vec![model("old-alias", Some("provider/model"), None, None), plain("retired")]);
    let fresh = cli(vec![model("new-alias", Some("provider/model"), None, Some(&["high"])), model("no-effort", None, Some(false), None)]);
    assert_eq!(
        reconcile("old-alias", "high", Some(&old), &fresh, None),
        kept_resolution("new-alias", "high"),
        "an alias renamed for the same model is followed"
    );
    assert_eq!(
        reconcile("provider/model", "low", None, &fresh, None),
        kept_resolution("new-alias", "default"),
        "a concrete id takes the alias that stands for it, and an effort the alias lacks goes"
    );
    assert_eq!(
        reconcile("retired", "high", Some(&old), &fresh, None),
        kept_resolution("default", "default"),
        "a model the CLI offered before and no longer does gives way to the default"
    );
    assert_eq!(
        reconcile("custom/private", "high", Some(&old), &fresh, None),
        kept_resolution("custom/private", "high"),
        "an id typed by hand, which no list ever offered, survives"
    );
    assert_eq!(reconcile("default", "high", Some(&old), &fresh, None), kept_resolution("default", "high"), "the default stays the default");
    assert_eq!(
        reconcile("retired", "high", Some(&old), &ModelCatalog::new("fallback", vec![], ""), None),
        kept_resolution("retired", "high"),
        "only a list the CLI itself gave retires a model"
    );
    assert_eq!(
        reconcile("no-effort", "high", None, &fresh, None),
        kept_resolution("no-effort", "default"),
        "an effort a model does not take goes back to the default"
    );

    let rows: Vec<Value> = serde_json::from_str(
        r#"[{"value":"default","resolvedModel":"apac.anthropic.claude-opus-5","supportedEffortLevels":["high"]},{"value":"apac.anthropic.claude-fable-5-1","displayName":"Fable"}]"#,
    )
    .unwrap();
    let bedrock = providers::claude_catalog(&rows);
    assert!(
        bedrock.source == "cli"
            && bedrock.models[0].value == "default"
            && bedrock.models[0].resolved_model.as_deref() == Some("apac.anthropic.claude-opus-5"),
        "the fixture's default row must name the model it stands for"
    );
    for previous in [None, Some(providers::fallback("claude"))] {
        assert_eq!(
            reconcile("claude-fable-5-1[1m]", "high", previous.as_ref(), &bedrock, Some("claude")),
            kept_resolution("default", "default"),
            "a retired official id gives way even with no list from before"
        );
    }
    assert_eq!(
        reconcile("apac.anthropic.claude-fable-5-1", "high", None, &bedrock, Some("claude")).model,
        "apac.anthropic.claude-fable-5-1",
        "a regional id the list has is kept"
    );
    assert_eq!(
        reconcile("company/claude-fable-5-1", "high", None, &bedrock, Some("claude")).model,
        "company/claude-fable-5-1",
        "a company's own id is no official id and survives"
    );
    assert_eq!(
        reconcile("default", "max", None, &bedrock, Some("claude")).effort,
        "default",
        "the default row's own effort levels decide the default's effort"
    );

    let concrete: Vec<Value> = serde_json::from_str(
        r#"[{"value":"default","resolvedModel":"apac.anthropic.claude-opus-5"},{"value":"apac.anthropic.claude-sonnet-5","supportsEffort":true,"supportedEffortLevels":["high"]}]"#,
    )
    .unwrap();
    let regional = providers::claude_catalog(&concrete);
    for alias in ["sonnet", "opus", "fable", "haiku", "opusplan", "best", "sonnet[1m]", "opus[1m]", "fable[1m]"] {
        assert!(is_official_claude_model(alias), "{alias} is a name Claude Code itself knows");
        for previous in
            [None, Some(providers::fallback("claude")), Some(cli(vec![model(alias, Some("apac.anthropic.claude-sonnet-5"), None, None)]))]
        {
            assert_eq!(
                reconcile(alias, "high", previous.as_ref(), &regional, Some("claude")),
                kept_resolution(alias, "high"),
                "a stable alias survives a list of concrete Bedrock ids: {alias}"
            );
        }
    }
    assert_eq!(
        reconcile("sonnet", "max", None, &cli(vec![model("sonnet", None, None, Some(&["high"]))]), Some("claude")),
        kept_resolution("sonnet", "default"),
        "an alias the list does have follows its row's effort levels"
    );
    assert!(
        !is_official_claude_model("company/claude-fable-5-1")
            && is_official_claude_model("claude-3-5-sonnet-20241022")
            && !is_official_claude_model("gpt-6-astra"),
        "official names are Claude Code's own shapes only"
    );

    let pane = RunSession {
        id: Some("pane".into()),
        workspace_id: Some("w".into()),
        model: Some("retired".into()),
        settings: Some(RunSettings { effort: "high".into(), permission_mode: "plan".into(), ..RunSettings::default() }),
        logs: Some(vec![LogEntry::new("first".into(), "user", "kept".into(), "2026-10-01T10:00:00Z".into(), None)]),
        ..RunSession::default()
    };
    let changed = adjusted(&pane, Some(&old), &fresh).expect("the pane is adjusted");
    let settings = changed.settings.clone().unwrap();
    assert!(
        changed.model() == "default" && settings.effort == "default" && settings.permission_mode == "plan" && changed.logs().len() == 2,
        "the pane takes the default model and effort and keeps the rest"
    );
    let line = &changed.logs()[1];
    assert!(
        line.kind() == "system"
            && line.provider.as_deref() == Some("claude")
            && line.text()
                == strings::get_with(
                    "composer.model.catalogAdjusted",
                    &[("fromModel", "retired"), ("toModel", "default"), ("fromEffort", "high"), ("toEffort", "default")]
                ),
        "one transcript line says what was replaced by what"
    );
    let custom = RunSession { model: Some("custom/private".into()), ..pane.clone() };
    assert!(adjusted(&custom, Some(&old), &fresh).is_none(), "a choice the list leaves as it is changes nothing and says nothing");
    let named = cli(vec![model("default", Some("provider/current"), None, None), plain("new-alias")]);
    assert_eq!(
        adjusted(&pane, Some(&old), &named).map(|p| p.model().to_string()).as_deref(),
        Some("default"),
        "a new conversation falls back to the plain default"
    );
    let continued = RunSession { resume_id: Some("conversation".into()), ..pane.clone() };
    assert_eq!(
        adjusted(&continued, Some(&old), &named).map(|p| p.model().to_string()).as_deref(),
        Some("provider/current"),
        "a continued conversation names the model the default stands for, so the CLI does not restore the retired one"
    );
    for language in ["ko", "en", "zh", "ja"] {
        let catalogue = mighty_core::locale::catalogue(language);
        let sentence = catalogue.get("composer.model.catalogAdjusted").cloned().unwrap_or_default();
        assert!(
            ["{fromModel}", "{toModel}", "{fromEffort}", "{toEffort}"].iter().all(|k| sentence.contains(k))
                && catalogue.get("composer.placeholder.checkingModels").is_some_and(|s| !s.is_empty()),
            "{language}: the adjusted line names all four values and the checking notice exists"
        );
    }
}

/// A reported change: the last model of the list before (if any) and of the new one.
type Change = (Option<String>, String);

fn last_value(value: &Option<ProviderRuntime>) -> Option<String> {
    value.as_ref().and_then(|r| r.model_catalog.models.last().map(|m| m.value.clone()))
}

// M/LocalModelCatalogRefresh.swift, M/AppStore+ModelCatalogs.swift:10-19, 66-76.
#[tokio::test(flavor = "multi_thread")]
async fn model_list_is_read_again_when_it_must_be() {
    use model_selection::{FAILED_LIFETIME, FORCED_LIFETIME, LIFETIME, PRE_RUN_WAIT};
    let key = ModelContext::new("w", &std::env::temp_dir().to_string_lossy(), "claude");
    let clock_at = Arc::new(Mutex::new(DateTimeOffset::from_utc_ticks(621_355_968_000_000_000 + 20_000 * 864_000_000_000)));
    let advance = |ticks: i64| {
        let mut at = clock_at.lock().unwrap();
        *at = plus(*at, ticks);
    };
    let loads = Arc::new(AtomicUsize::new(0));
    let held: Arc<Mutex<VecDeque<tokio::sync::oneshot::Receiver<()>>>> = Arc::default();
    let changes: Arc<Mutex<Vec<Change>>> = Arc::default();
    let fails = Arc::new(AtomicBool::new(false));
    let fallback = Arc::new(AtomicBool::new(false));
    let loader: Loader = {
        let (loads, held, fails, fallback) = (loads.clone(), held.clone(), fails.clone(), fallback.clone());
        Arc::new(move |context: ModelContext| {
            let number = loads.fetch_add(1, Ordering::SeqCst) + 1;
            let hold = held.lock().unwrap().pop_front();
            let (fails, fallback) = (fails.clone(), fallback.clone());
            Box::pin(async move {
                if let Some(hold) = hold {
                    let _ = hold.await;
                }
                if fails.load(Ordering::SeqCst) {
                    return Err("the CLI did not answer".to_string());
                }
                let catalog = if fallback.load(Ordering::SeqCst) {
                    providers::fallback(&context.provider)
                } else {
                    cli(vec![plain("default"), plain(&format!("m{number}"))])
                };
                Ok(runtime(&context.provider, catalog))
            })
        })
    };
    let recorded = changes.clone();
    let changed: Changed = Arc::new(move |_: &ModelContext, before: Option<&ProviderRuntime>, after: &ProviderRuntime| {
        recorded.lock().unwrap().push((last_value(&before.cloned()), last_value(&Some(after.clone())).unwrap()));
    });
    let clock: Clock = {
        let clock_at = clock_at.clone();
        Arc::new(move || *clock_at.lock().unwrap())
    };
    let refresh = ModelCatalogRefresh::new(tokio::runtime::Handle::current(), loader, changed, Some(clock));
    let loads_now = || loads.load(Ordering::SeqCst);
    assert!(
        FORCED_LIFETIME == Duration::from_secs(2) && LIFETIME == Duration::from_secs(60),
        "a selected pane's list is used again for 2 s, a run's for 60 s"
    );
    assert!(refresh.value(&key).is_none() && !refresh.is_current(&key), "nothing is known before the first reading");

    let first = refresh.request(&key, true, false);
    let joined = refresh.request(&key, false, false);
    assert!(first.same(&joined) && !refresh.is_current(&key), "requests made while a probe runs share it");
    assert!(
        last_value(&first.wait().await).as_deref() == Some("m1")
            && loads_now() == 1
            && *changes.lock().unwrap() == [(None, "m1".to_string())],
        "one reading answers both, and is reported once with nothing before it"
    );
    assert!(
        refresh.is_current(&key) && last_value(&refresh.value(&key)).as_deref() == Some("m1"),
        "the list in hand is the newest asked for"
    );

    assert!(
        last_value(&refresh.request(&key, true, false).wait().await).as_deref() == Some("m1") && loads_now() == 1,
        "a list read a moment ago answers a selected pane without another probe"
    );
    advance(seconds(3));
    assert!(
        last_value(&refresh.request(&key, false, false).wait().await).as_deref() == Some("m1") && loads_now() == 1,
        "a run about to start uses a list up to a minute old"
    );
    assert!(
        last_value(&refresh.request(&key, true, false).wait().await).as_deref() == Some("m2")
            && loads_now() == 2
            && changes.lock().unwrap().last() == Some(&(Some("m1".to_string()), "m2".to_string())),
        "a selected pane reads again after 2 s, and the list before goes with the new one"
    );
    advance(seconds(61));
    assert!(
        last_value(&refresh.request(&key, false, false).wait().await).as_deref() == Some("m3") && loads_now() == 3,
        "a run about to start reads again after a minute"
    );

    // Another agent or account: the list in hand is no longer the right one, however new.
    assert!(
        last_value(&refresh.request(&key, true, true).wait().await).as_deref() == Some("m4")
            && loads_now() == 4
            && refresh.is_current(&key),
        "an invalidating request reads again at once"
    );

    // Invalidated while the probe runs: it reads again before it answers, and the list from before is never reported.
    let (release, hold) = tokio::sync::oneshot::channel();
    held.lock().unwrap().push_back(hold);
    let probing = refresh.request(&key, false, true);
    common::until(|| loads.load(Ordering::SeqCst) == 5, 10_000, "the fifth reading").await;
    assert!(
        refresh.request(&key, false, true).same(&probing) && !refresh.is_current(&key),
        "an invalidating request joins the running probe"
    );
    let _ = release.send(());
    assert!(
        last_value(&probing.wait().await).as_deref() == Some("m6") && loads_now() == 6 && refresh.is_current(&key),
        "an invalidation during a probe makes it read again before it answers"
    );
    assert_eq!(
        changes.lock().unwrap().iter().map(|c| c.1.clone()).collect::<Vec<_>>(),
        ["m1", "m2", "m3", "m4", "m6"],
        "the reading that was overtaken is never reported"
    );

    // The whole runtime read at once feeds the same book.
    refresh.store(&key, runtime("claude", cli(vec![plain("default"), plain("stored")])));
    assert!(
        changes.lock().unwrap().last() == Some(&(Some("m6".to_string()), "stored".to_string()))
            && refresh.is_current(&key)
            && last_value(&refresh.request(&key, true, false).wait().await).as_deref() == Some("stored")
            && loads_now() == 6,
        "a list read another way is the newest, is reported, and is fresh"
    );

    // A list that is not the CLI's own (offline, signed out) is not asked again by each run for a while; a selected pane asks again.
    fallback.store(true, Ordering::SeqCst);
    advance(seconds(3));
    let source = |value: Option<ProviderRuntime>| value.map(|r| r.model_catalog.source);
    assert_eq!(FAILED_LIFETIME, Duration::from_secs(15), "a failed or fallback reading holds for 15 s");
    assert!(
        source(refresh.request(&key, true, false).wait().await).as_deref() == Some("fallback") && loads_now() == 7,
        "the fixture must read a fallback list"
    );
    assert!(
        source(refresh.request(&key, false, false).wait().await).as_deref() == Some("fallback")
            && loads_now() == 7
            && refresh.is_current(&key),
        "a run about to start does not wait for the CLI to fail again within 15 s"
    );
    assert!(
        source(refresh.request(&key, true, false).wait().await).as_deref() == Some("fallback") && loads_now() == 8,
        "a selected pane asks the CLI again"
    );
    advance(seconds(16));
    assert!(
        source(refresh.request(&key, false, false).wait().await).as_deref() == Some("fallback") && loads_now() == 9,
        "after 15 s a run asks again"
    );

    fails.store(true, Ordering::SeqCst);
    advance(seconds(16));
    assert!(refresh.request(&key, false, false).wait().await.is_none() && loads_now() == 10, "a reading that failed answers nothing");
    assert!(refresh.request(&key, false, false).wait().await.is_none() && loads_now() == 10, "and is not asked again by a run within 15 s");
    fails.store(false, Ordering::SeqCst);
    fallback.store(false, Ordering::SeqCst);
    assert!(
        last_value(&refresh.request(&key, true, false).wait().await).as_deref() == Some("m11") && loads_now() == 11,
        "and leaves no probe behind: a selected pane reads again"
    );
    refresh.shutdown();
    assert!(
        refresh.request(&key, true, false).wait().await.is_none() && loads_now() == 11 && refresh.value(&key).is_none(),
        "nothing is read once the app is closing"
    );

    // A run waits for its list no longer than PreRunWait, then goes on with what is in hand.
    let (slow_release, slow) = tokio::sync::watch::channel(false);
    let slow_loader: Loader = Arc::new(move |context: ModelContext| {
        let mut slow = slow.clone();
        Box::pin(async move {
            let _ = slow.wait_for(|go| *go).await;
            Ok(runtime(&context.provider, cli(vec![plain("default")])))
        })
    });
    let waiting = ModelCatalogRefresh::new(tokio::runtime::Handle::current(), slow_loader, changed_nothing(), None);
    assert!(
        PRE_RUN_WAIT == Duration::from_secs(3) && !waiting.wait_current(&key, Some(Duration::from_millis(150))).await,
        "a list that takes too long holds the run up no longer than its limit"
    );
    slow_release.send_replace(true);
    assert!(waiting.wait_current(&key, None).await && waiting.is_current(&key), "a list that comes in time is the run's");

    // A probe that the report of another starts is kept, not forgotten by the first one's end.
    let chain_loads = Arc::new(AtomicUsize::new(0));
    let chained_cell: Arc<OnceLock<ModelCatalogRefresh>> = Arc::default();
    let second: Arc<Mutex<Option<model_selection::ModelRequest>>> = Arc::default();
    let chain_loader: Loader = {
        let chain_loads = chain_loads.clone();
        Arc::new(move |context: ModelContext| {
            chain_loads.fetch_add(1, Ordering::SeqCst);
            Box::pin(async move { Ok(runtime(&context.provider, cli(vec![plain("default")]))) })
        })
    };
    let chain_changed: Changed = {
        let (cell, second) = (chained_cell.clone(), second.clone());
        Arc::new(move |context: &ModelContext, _: Option<&ProviderRuntime>, _: &ProviderRuntime| {
            let mut second = second.lock().unwrap();
            if second.is_none()
                && let Some(chained) = cell.get()
            {
                *second = Some(chained.request(context, true, true));
            }
        })
    };
    let chained = ModelCatalogRefresh::new(tokio::runtime::Handle::current(), chain_loader, chain_changed, None);
    let _ = chained_cell.set(chained.clone());
    chained.request(&key, false, false).wait().await;
    let started = second.lock().unwrap().clone().expect("a probe was started while the last one reported");
    assert!(
        chained.request(&key, false, false).same(&started) && !started.is_completed(),
        "a probe started while the last one reported is the one a request joins"
    );
    started.wait().await;
    assert_eq!(chain_loads.load(Ordering::SeqCst), 2, "and only two readings were made");
    // Every reading is a new object: two that hold the same are the same reading.
    let a = runtime("claude", cli(vec![plain("default"), plain("a")]));
    let a_again = runtime("claude", cli(vec![plain("default"), plain("a")]));
    let b = runtime("claude", cli(vec![plain("default"), plain("b")]));
    assert!(
        ModelCatalogRefresh::same(Some(&a), Some(&a_again))
            && !ModelCatalogRefresh::same(Some(&a), Some(&b))
            && !ModelCatalogRefresh::same(None, Some(&runtime("claude", cli(vec![plain("default")])))),
        "readings are compared by what they hold"
    );

    // One provider read again for a folder: the kept runtime takes the reading, and no CLI is looked for again.
    let found: Arc<Mutex<HashMap<String, usize>>> = Arc::default();
    let finder: providers::Finder = {
        let found = found.clone();
        Arc::new(move |provider: String| {
            *found.lock().unwrap().entry(provider).or_insert(0) += 1;
            Box::pin(async { None })
        })
    };
    let catalog =
        providers::ProviderCatalog::new(Arc::new(mighty_platform::fake::FakeProcessHost::default()), common::environment(), Some(finder));
    let folder = std::env::temp_dir().to_string_lossy().into_owned();
    let whole = catalog.get_runtime(false, Some(&folder)).await;
    let codex = catalog.refresh_provider("codex", Some(&folder), false).await;
    assert!(
        codex.id == "codex" && !codex.available && codex.model_catalog.source == "fallback",
        "a provider with no CLI reads as not installed with the fallback list"
    );
    {
        let found = found.lock().unwrap();
        assert!(found["codex"] == 1 && found["claude"] == 1 && found["gemini"] == 1, "the list is read anew with the CLI already found");
    }
    let after = catalog.get_runtime(false, Some(&folder)).await;
    assert!(
        after.providers.iter().find(|p| p.id == "codex") == Some(&codex)
            && after.providers.iter().map(|p| p.id.clone()).collect::<Vec<_>>()
                == whole.providers.iter().map(|p| p.id.clone()).collect::<Vec<_>>()
            && after.providers[0] == whole.providers[0],
        "the kept runtime of that folder takes the new reading and keeps the others"
    );
}

// M/AppStore+ClaudeModelReset.swift:16-47.
#[tokio::test(flavor = "multi_thread")]
async fn model_reset_reads_every_workspace_and_says_how_it_went() {
    common::korean();
    let workspace = |name: &str| Workspace { name: Some(name.into()), path: Some(name.into()), ..Workspace::default() };
    let workspaces = vec![workspace("a"), workspace("b"), workspace("c")];
    let state = AppSnapshot {
        workspaces: Some(workspaces.clone()),
        sessions: Some(vec![
            Arc::new(RunSession {
                id: Some("claude".into()),
                workspace_id: workspaces[0].id.clone(),
                model: Some("opus".into()),
                settings: Some(RunSettings { effort: "high".into(), permission_mode: "acceptEdits".into(), ..RunSettings::default() }),
                logs: Some(vec![LogEntry::new("entry".into(), "user", "kept".into(), "2026-10-01T10:00:00Z".into(), None)]),
                ..RunSession::default()
            }),
            Arc::new(RunSession {
                id: Some("codex".into()),
                workspace_id: workspaces[1].id.clone(),
                provider: Some("codex".into()),
                model: Some("gpt-5.6-sol".into()),
                settings: Some(RunSettings { effort: "high".into(), ..RunSettings::default() }),
                ..RunSession::default()
            }),
            Arc::new(RunSession {
                id: Some("shell".into()),
                workspace_id: workspaces[2].id.clone(),
                kind: Some("shell".into()),
                model: Some("opus".into()),
                ..RunSession::default()
            }),
        ]),
        ..AppSnapshot::default()
    };
    let reset = model_selection::reset_apply(&state);
    let reset = reset.sessions();
    let first = reset[0].settings.clone().unwrap();
    assert!(
        reset[0].model() == "default" && first.effort == "default" && first.permission_mode == "acceptEdits" && reset[0].logs().len() == 1,
        "a Claude pane goes back to the default model and effort and keeps the rest"
    );
    assert!(
        reset[1].model() == "gpt-5.6-sol" && reset[1].settings.as_ref().unwrap().effort == "high" && reset[2].model() == "opus",
        "only Claude panes are reset"
    );

    let count = |value: usize| value.to_string();
    let read_paths: Arc<Mutex<Vec<String>>> = Arc::default();
    let reader = |failing: &'static str| -> ResetRead {
        let read_paths = read_paths.clone();
        Arc::new(move |workspace: Workspace, _: Cancellation| {
            let path = workspace.path().to_string();
            read_paths.lock().unwrap().push(path.clone());
            Box::pin(async move { if path == failing { providers::fallback("claude") } else { ModelCatalog::new("cli", vec![], "") } })
        })
    };
    assert_eq!(
        model_selection::reset_reload(&workspaces, reader(""), Cancellation::none(), None).await,
        Ok(strings::get_with("settings.cliAccounts.modelReset.done", &[("count", &count(3))])),
        "every list read from the CLI: the sentence counts the workspaces"
    );
    let mut read = read_paths.lock().unwrap().clone();
    read.sort();
    assert_eq!(read, ["a", "b", "c"], "every workspace's list is read, not the active one's alone");
    assert_eq!(
        model_selection::reset_reload(&workspaces, reader("b"), Cancellation::none(), None).await,
        Ok(strings::get_with("settings.cliAccounts.modelReset.partial", &[("count", &count(1))])),
        "a list that did not come from the CLI is counted as not read"
    );
    assert_eq!(
        model_selection::reset_reload(&[], reader(""), Cancellation::none(), None).await,
        Ok(strings::get("settings.cliAccounts.modelReset.cacheCleared")),
        "with no workspace there is nothing to read"
    );
    assert!(
        model_selection::reset_result(3, 0, 0).contains('3')
            && model_selection::reset_result(3, 2, 0).contains('2')
            && model_selection::reset_result(3, 0, 0) != model_selection::reset_result(3, 2, 0),
        "the two sentences carry their own counts"
    );

    // A read whose time runs out says so, not that a sign-in is missing; the reads start off the caller's thread, a few at a time.
    let hangs_or_fails: ResetRead = Arc::new(move |workspace: Workspace, token: Cancellation| {
        let path = workspace.path().to_string();
        Box::pin(async move {
            if path == "a" {
                token.cancelled().await;
                return providers::fallback("claude");
            }
            if path == "c" { providers::fallback("claude") } else { ModelCatalog::new("cli", vec![], "") }
        })
    });
    let timed_out =
        model_selection::reset_reload(&workspaces, hangs_or_fails, Cancellation::none(), Some(Duration::from_millis(200))).await;
    assert_eq!(
        timed_out,
        Ok(format!(
            "{} {}",
            strings::get_with("settings.cliAccounts.modelReset.partial", &[("count", &count(1))]),
            strings::get_with("settings.cliAccounts.modelReset.timedOut", &[("count", &count(1))])
        )),
        "a timed-out list and a refused one are each said as what they are"
    );
    assert!(
        model_selection::reset_result(2, 0, 2) == strings::get_with("settings.cliAccounts.modelReset.timedOut", &[("count", &count(2))])
            && model_selection::reset_result(2, 0, 2) != model_selection::reset_result(2, 2, 0),
        "only timeouts: the timeout sentence alone"
    );
    // Started from a thread of its own (the UI thread's stand-in): a read made in place would run on it.
    let running = Arc::new(AtomicUsize::new(0));
    let most = Arc::new(AtomicUsize::new(0));
    let threads: Arc<Mutex<Vec<std::thread::ThreadId>>> = Arc::default();
    let many: Vec<Workspace> = (0..8).map(|i| workspace(&format!("w{i}"))).collect();
    let counting: ResetRead = {
        let (running, most, threads) = (running.clone(), most.clone(), threads.clone());
        Arc::new(move |_: Workspace, _: Cancellation| {
            let (running, most, threads) = (running.clone(), most.clone(), threads.clone());
            Box::pin(async move {
                threads.lock().unwrap().push(std::thread::current().id());
                let now = running.fetch_add(1, Ordering::SeqCst) + 1;
                most.fetch_max(now, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_millis(30)).await;
                running.fetch_sub(1, Ordering::SeqCst);
                ModelCatalog::new("cli", vec![], "")
            })
        })
    };
    let handle = tokio::runtime::Handle::current();
    let caller = std::thread::spawn(move || {
        let id = std::thread::current().id();
        let said = handle.block_on(model_selection::reset_reload(&many, counting, Cancellation::none(), None));
        (id, said)
    });
    let (caller_id, said) = tokio::task::spawn_blocking(move || caller.join().unwrap()).await.unwrap();
    assert!(said.is_ok());
    let most = most.load(Ordering::SeqCst);
    assert!(
        most <= model_selection::RESET_CONCURRENCY && most > 1,
        "at most {} lists are read at once; got {most}",
        model_selection::RESET_CONCURRENCY
    );
    let threads = threads.lock().unwrap().clone();
    assert!(
        threads.len() == 8 && threads.iter().all(|t| *t != caller_id),
        "every read starts on the runtime's workers, never on the caller's thread"
    );
    let closed = CancellationSource::default();
    closed.cancel();
    let hangs: ResetRead = Arc::new(|_: Workspace, token: Cancellation| {
        Box::pin(async move {
            token.cancelled().await;
            providers::fallback("claude")
        })
    });
    assert_eq!(
        model_selection::reset_reload(&workspaces, hangs, closed.token(), None).await,
        Err(model_selection::ResetCancelled),
        "a reset whose window closed ends without a sentence"
    );
    for language in ["ko", "en", "zh", "ja"] {
        let sentence =
            mighty_core::locale::catalogue(language).get("settings.cliAccounts.modelReset.timedOut").cloned().unwrap_or_default();
        assert!(!sentence.is_empty() && sentence.contains("{count}"), "{language}: the timeout sentence must carry its count");
    }
}
