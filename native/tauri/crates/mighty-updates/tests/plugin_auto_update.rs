//! C# PluginAutoUpdateVerification (Verification.cs:1154-1156), every check under its name: the plugin preference and the automatic
//! schedule, Claude's scope, approval and budget rules, and Codex's Git marketplaces under the app-wide mutation gate — over a fake
//! runner, no CLI started.

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use common::*;
use mighty_core::json::Wire;
use mighty_core::model::AppSnapshot;
use mighty_updates::cli_update::CliAutoUpdateSchedule;
use mighty_updates::plugins::operations::ReaderFactory;
use mighty_updates::plugins::{ClaudePluginReader, CodexPluginReader, PluginOperations, ProviderReader};
use mighty_updates::runner::{CancellationToken, CliRunResult, RunError};

type Mutation =
    Arc<dyn Fn(&[String], CancellationToken) -> mighty_platform::BoxFuture<'static, Result<CliRunResult, RunError>> + Send + Sync>;

/// C#'s `Runner`: help, version and the two reads answered from its fields, every other call handed to `mutation`.
struct Answers {
    listing: Mutex<String>,
    markets: Mutex<String>,
    mutation: Mutex<Mutation>,
}

fn answers(markets: &str) -> Arc<Answers> {
    Arc::new(Answers {
        listing: Mutex::new(r#"{"installed":[],"available":[]}"#.into()),
        markets: Mutex::new(markets.into()),
        mutation: Mutex::new(Arc::new(|_, _| Box::pin(async { ok("{}") }))),
    })
}

fn runner(answers: &Arc<Answers>) -> Arc<FakeRunner> {
    let runner = FakeRunner::new(|_, _| ok(""));
    let answers = answers.clone();
    runner.set_async(move |call, token| {
        let args = call.arguments;
        let a: Vec<&str> = args.iter().map(String::as_str).collect();
        let output = if a.contains(&"--help") {
            Some("--json --available".to_string())
        } else {
            match a.as_slice() {
                ["--version"] => Some("2.1.271".to_string()),
                ["plugin", "list", ..] => Some(answers.listing.lock().unwrap().clone()),
                ["plugin", "marketplace", "list", ..] => Some(answers.markets.lock().unwrap().clone()),
                _ => None,
            }
        };
        match output {
            Some(output) => Box::pin(async move { ok(&output) }),
            None => {
                let mutation = answers.mutation.lock().unwrap().clone();
                mutation(&args, token)
            }
        }
    });
    runner
}

fn mutations(runner: &FakeRunner) -> Vec<Vec<String>> {
    runner
        .args()
        .into_iter()
        .filter(|a| {
            let a: Vec<&str> = a.iter().map(String::as_str).collect();
            !a.contains(&"--help") && matches!(a.as_slice(), ["plugin", "update", ..] | ["plugin", "marketplace", "upgrade", ..])
        })
        .collect()
}

fn reader(provider: &str, runner: &Arc<FakeRunner>, home: &str) -> ProviderReader {
    let environment = std::collections::BTreeMap::from([("PATH".to_string(), home.to_string())]);
    let (yes, also) = (Arc::new(|_: &str| true), Arc::new(|_: &str| true));
    if provider == "claude" {
        ProviderReader::Claude(ClaudePluginReader::with_probes(runner.clone(), environment, yes, also))
    } else {
        ProviderReader::Codex(CodexPluginReader::with_probes(runner.clone(), environment, yes, also))
    }
}

#[tokio::test]
async fn default_preference_and_scheduling() {
    korean();
    let read = AppSnapshot::read(&serde_json::json!({})).unwrap();
    assert!(
        AppSnapshot::default().auto_update_plugins != Some(false) && read.auto_update_plugins != Some(false),
        "an absent plugin preference must default on, matching the Mac's migration"
    );
    for enabled in [true, false] {
        let state = mighty_core::normalize::normalize(
            &AppSnapshot { auto_update_plugins: Some(enabled), auto_update_clis: Some(false), ..AppSnapshot::default() },
            true,
        )
        .unwrap();
        let roundtrip = AppSnapshot::read(&serde_json::from_str(&state.to_json()).unwrap()).unwrap();
        assert!(
            roundtrip.auto_update_plugins == Some(enabled) && roundtrip.auto_update_clis == Some(false),
            "an explicit plugin preference must survive normalization and restart independently of the CLI preference"
        );
    }
    let at = Utc::now();
    let mut schedule = CliAutoUpdateSchedule::new();
    assert!(schedule.is_due(at, true) && !schedule.is_due(at, false), "only an enabled launch starts a pass");
    schedule.pass_started(at);
    let six_hours = TimeDelta::hours(6);
    assert!(
        !schedule.is_due(at + six_hours - TimeDelta::nanoseconds(100), true) && schedule.is_due(at + six_hours, true),
        "the periodic update is due at 6 h, not each tick"
    );
    let three_minutes = TimeDelta::minutes(3);
    assert!(
        !CliAutoUpdateSchedule::idle_long_enough(false, false, Some(at), at + three_minutes - TimeDelta::nanoseconds(100))
            && CliAutoUpdateSchedule::idle_long_enough(false, false, Some(at), at + three_minutes),
        "the provider must be idle for the full 3 min"
    );
    assert!(
        !CliAutoUpdateSchedule::idle_long_enough(true, false, None, at) && !CliAutoUpdateSchedule::idle_long_enough(false, true, None, at),
        "running or starting and queued input both block a background mutation"
    );
    schedule.skipped_busy("codex");
    schedule.skipped_busy("claude");
    assert_eq!(schedule.due_retries(true, |p| p == "claude"), ["claude"], "only ready deferred providers retry");
    schedule.due_retries(false, |_| true);
    assert!(schedule.due_retries(true, |_| true).is_empty(), "turning both preferences off forgets deferred work");
}

#[tokio::test]
async fn claude_scope_approval_and_budget() {
    korean();
    let home = text(&temp("auto-claude"));
    let state = answers("[]");
    *state.listing.lock().unwrap() = r#"{"installed":[{"id":"safe@sample","scope":"user"},{"id":"safe@sample","scope":"user"},{"id":"review@sample","scope":"user"},{"id":"project@sample","scope":"project"},{"id":"local@sample","scope":"local"},{"id":"mighty-bridge@sample","scope":"user"}],"available":[]}"#.into();
    *state.mutation.lock().unwrap() = Arc::new(|args, _| {
        let output =
            if args[2] == "review@sample" { "marketplace text\n{\"shownCommand\":\"do-not-run\"}" } else { "{\"outcome\":\"ok\"}" };
        Box::pin(async move { ok(output) })
    });
    let runner = runner(&state);
    let ProviderReader::Claude(reader) = reader("claude", &runner, &home) else { unreachable!() };
    let result = reader.update_installed(&workspace(&home), Duration::from_secs(300), &CancellationToken::new()).await;
    assert!(
        result.status == "succeeded"
            && result.updated.as_deref() == Some(&["safe@sample".to_string()][..])
            && result.needs_approval.as_deref() == Some(&["review@sample".to_string()][..]),
        "a changed marketplace command is deferred, never approved automatically: {result:?}"
    );
    let asked = mutations(&runner);
    assert!(
        asked.len() == 2
            && asked.iter().all(|a| a.len() == 6
                && a[3] == "--scope"
                && a[4] == "user"
                && a[5] == "--json"
                && !a.iter().any(|x| x == "-y" || x == "--yes")),
        "only deduplicated user plugins update, with the exact no-consent argv: {asked:?}"
    );
    runner.clear();
    reader.update_installed(&workspace(&home), Duration::ZERO, &CancellationToken::new()).await;
    assert!(mutations(&runner).is_empty(), "a spent budget must start no plugin mutation");
    *state.listing.lock().unwrap() = "malformed".into();
    let malformed = reader.update_installed(&workspace(&home), Duration::from_secs(300), &CancellationToken::new()).await;
    assert_eq!(malformed.status, "failed", "a malformed listing is a failure, not an empty successful update");
}

#[tokio::test(flavor = "multi_thread")]
async fn codex_marketplaces_and_global_exclusion() {
    korean();
    let home = text(&temp("auto-codex"));
    let git = r#"{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.test/sample"}}]}"#;
    let state = answers(git);
    let entered = Signal::new();
    let entered_in = entered.clone();
    *state.mutation.lock().unwrap() = Arc::new(move |_, token| {
        let entered = entered_in.clone();
        Box::pin(async move {
            entered.set();
            token.cancelled().await;
            Err(RunError::Cancelled)
        })
    });
    let runner = runner(&state);
    let operations = Arc::new(PluginOperations::new(runner.clone()));
    let factory = |runner: &Arc<FakeRunner>, home: &str| -> ReaderFactory {
        let (runner, home) = (runner.clone(), home.to_string());
        Arc::new(move |provider: &str| reader(provider, &runner, &home))
    };
    let stop = CancellationToken::new();
    let active = {
        let (operations, home, stop, factory) = (operations.clone(), home.clone(), stop.clone(), factory(&runner, &home));
        tokio::spawn(async move {
            operations.update_installed("codex", &workspace(&home), Duration::from_secs(300), &stop, Some(factory)).await
        })
    };
    tokio::time::timeout(Duration::from_secs(5), entered.wait()).await.expect("the upgrade started");
    let second = operations
        .update_installed("claude", &workspace(&home), Duration::from_secs(300), &CancellationToken::new(), Some(factory(&runner, &home)))
        .await;
    assert!(operations.is_running() && second.status == "busy", "Claude and Codex background updates share the same global mutation gate");
    stop.cancel();
    let cancelled = active.await.unwrap();
    assert!(cancelled.status == "cancelled" && !operations.is_running(), "shutdown cancellation releases the mutation gate");
    assert_eq!(
        mutations(&runner),
        vec![strings(&["plugin", "marketplace", "upgrade", "--json"])],
        "Codex upgrades Git marketplaces through the exact command"
    );

    runner.clear();
    *state.markets.lock().unwrap() =
        r#"{"marketplaces":[{"name":"local","marketplaceSource":{"sourceType":"local","source":"./local"}}]}"#.into();
    let ProviderReader::Codex(codex) = reader("codex", &runner, &home) else { unreachable!() };
    let local = codex.upgrade_marketplaces(&workspace(&home), &CancellationToken::new()).await;
    assert!(local.status == "skipped" && mutations(&runner).is_empty(), "local-only marketplaces are never upgraded");

    *state.markets.lock().unwrap() = git.into();
    *state.mutation.lock().unwrap() =
        Arc::new(|_, _| Box::pin(async { ok(r#"{"selectedMarketplaces":["sample"],"errors":["fixture"]}"#) }));
    let ProviderReader::Codex(codex) = reader("codex", &runner, &home) else { unreachable!() };
    let errors = codex.upgrade_marketplaces(&workspace(&home), &CancellationToken::new()).await;
    assert_eq!(errors.status, "failed", "a successful process with explicit marketplace errors must still fail");
}
