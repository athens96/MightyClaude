//! ProviderCatalog checks: C# ModelListProbeVerification (the reads, their arguments and failures, the list kept across a restart;
//! the kept-lists file itself, the menu line and the refresh queue are model_lists.rs's, the remote wire W8's),
//! AutoPermissionVerification, and Verification.cs "wire settings include
//! explicit nullable limits; provider validation" and "Claude/Codex metadata protocol sends no user prompt or model turn". The reads
//! over a real child run on Windows (the Mac ProcessHost is W1-D's).

mod common;

use std::collections::BTreeMap;

use mighty_core::json::{JsonOut, Wire};
use mighty_core::model::RunSettings;
use mighty_run::flow_steps::TASK_TOOLS;
use mighty_run::model::{ModelCatalog, ModelOption, StartRunRequest};
use mighty_run::providers::{self, claude_headless_environment, quiet_environment};
use mighty_run::strings;

/// What each real CLI accepted, run by hand with exactly these arguments (C# `ModelListProbeVerification.Verified`).
fn verified() -> Vec<(&'static str, Vec<&'static str>)> {
    vec![
        (
            "claude",
            vec![
                "--print",
                "--verbose",
                "--input-format",
                "stream-json",
                "--output-format",
                "stream-json",
                "--no-session-persistence",
                "--permission-mode",
                "dontAsk",
                "--permission-prompts",
                "none",
                "--strict-mcp-config",
                "--mcp-config",
                "{\"mcpServers\":{}}",
                "--tools",
                "",
                "--safe-mode",
            ],
        ),
        ("codex", vec!["app-server", "--listen", "stdio://"]),
        ("gemini", vec!["models"]),
        ("pi", vec!["--list-models"]),
        ("opencode", vec!["models", "--standalone"]),
    ]
}

fn catalog_json(catalog: &ModelCatalog) -> String {
    let mut out = JsonOut::new();
    catalog.write(&mut out);
    out.finish()
}

/// Only the Windows-only model-list checks below compare lists this way.
#[cfg(windows)]
fn models_json(models: &[ModelOption]) -> String {
    let mut out = JsonOut::new();
    out.begin_array();
    for model in models {
        model.write(&mut out);
    }
    out.end_array();
    out.finish()
}

/// With DISABLE_TELEMETRY Claude Code answers with its built-in list (Fable 5.2 read as Fable 5.1 [1m]).
#[test]
fn the_probe_lets_the_cli_fetch_the_accounts_current_models() {
    let environment = mighty_platform::fake::FakeShellEnvironment::new(BTreeMap::from([("PATH".to_string(), "/bin".to_string())]));
    let claude = claude_headless_environment(&environment);
    assert!(!claude.contains_key("DISABLE_TELEMETRY"), "the Claude model list probe sets no DISABLE_TELEMETRY");
    assert!(!quiet_environment(&environment).contains_key("DISABLE_TELEMETRY"), "no other read or run sets it either");
    assert!(
        claude.get("DISABLE_AUTOUPDATER").map(String::as_str) == Some("1")
            && claude.get("CLAUDE_CODE_SAFE_MODE").map(String::as_str) == Some("1")
            && claude.get("DISABLE_ERROR_REPORTING").map(String::as_str) == Some("1"),
        "the probe stays quiet otherwise"
    );
    // Only a variable the user's own environment already sets may reach the CLIs.
    let inherited = mighty_platform::fake::FakeShellEnvironment::new(BTreeMap::from([("DISABLE_TELEMETRY".to_string(), "1".to_string())]));
    assert!(claude_headless_environment(&inherited).contains_key("DISABLE_TELEMETRY"), "the user's own setting is kept");
}

#[test]
fn every_read_runs_the_arguments_its_cli_was_checked_with() {
    for (provider, arguments) in verified() {
        let listed = providers::model_list_arguments(provider);
        assert!(listed.as_ref().is_some_and(|l| *l == arguments), "{provider}: {listed:?}");
    }
    assert!(
        providers::model_list_arguments("hermes").is_none() && providers::model_list_arguments("other").is_none(),
        "no list command for anything else"
    );
    let pinned: Vec<&str> = verified().into_iter().map(|(p, _)| p).collect();
    assert!(mighty_core::wire::PROVIDERS.iter().filter(|p| **p != "hermes").all(|p| pinned.contains(p)), "every provider's read is pinned");
    assert_eq!(providers::model_list_command("opencode"), "opencode models --standalone", "opencode is named with its arguments");
    assert!(
        providers::model_list_command("gemini") == "agy models" && providers::model_list_command("pi") == "pi --list-models",
        "agy and pi are named with their arguments"
    );
    assert!(
        providers::model_list_command("claude") == "claude"
            && providers::model_list_command("codex") == "codex app-server --listen stdio://",
        "claude by its name alone"
    );
}

#[test]
fn the_real_cli_outputs_reach_each_list() {
    let agy_text = common::fixture("model-list-agy.txt");
    let agy = providers::antigravity_catalog(&agy_text);
    assert!(agy.is_fresh() && agy.models.len() == 17, "agy: 17 rows, got {}", agy.models.len());
    assert_eq!(
        agy.models.iter().take(4).map(|m| m.value.as_str()).collect::<Vec<_>>(),
        ["default", "gemini-4-carbon-medium", "gemini-4-carbon-high", "gemini-4-carbon-low"],
        "agy: its new models first"
    );
    assert!(
        agy.models.iter().find(|m| m.value == "gemini-4-carbon-medium").unwrap().display_name == "Gemini 4 Carbon (Medium)"
            && agy.models.last().unwrap().value == "gpt-oss-120b-medium",
        "agy: its names and last row"
    );
    assert!(
        !providers::fallback("gemini").models.iter().any(|m| m.value == "gemini-4-carbon-medium"),
        "Gemini 4 Carbon is the CLI's alone"
    );
    assert_eq!(
        catalog_json(&providers::antigravity_catalog(&format!("Fetching available models...\n{agy_text}"))),
        catalog_json(&agy),
        "agy: the older first line changes nothing"
    );

    let pi = providers::pi_catalog(&common::fixture("model-list-pi.txt"));
    assert!(pi.is_fresh() && pi.models.len() == 19, "pi: every row, got {}", pi.models.len());
    for model in ["anthropic/claude-fable-5-2", "anthropic/claude-haiku-5-5", "anthropic/claude-opus-5-5", "anthropic/claude-sonnet-5-5"] {
        assert!(pi.models.iter().any(|m| m.value == model), "pi: {model}");
    }
    assert!(pi.models.iter().skip(1).all(|m| m.supports_effort == Some(true)), "pi: every row thinks");

    let codex_lines = common::fixture("model-list-codex.jsonl");
    let answer = common::json(codex_lines.split('\n').find(|l| l.starts_with("{\"id\":2,")).unwrap().trim_end());
    let codex = providers::codex_catalog(answer["result"]["data"].as_array().unwrap());
    assert_eq!(
        codex.models.iter().map(|m| m.value.as_str()).collect::<Vec<_>>(),
        [
            "default",
            "gpt-6.1-sol",
            "gpt-6-astra",
            "gpt-6-sol",
            "gpt-6-luna",
            "gpt-5.6-sol",
            "gpt-6.1-terra",
            "gpt-5.6-terra",
            "gpt-5.6-luna",
            "gpt-5.5"
        ],
        "codex"
    );
    assert!(codex.is_fresh());
    assert_eq!(codex.models[0].resolved_model.as_deref(), Some("gpt-6.1-sol"), "codex: the default is the row it marks isDefault");
    assert_eq!(
        codex.models[1].supported_effort_levels.as_deref(),
        Some(&["low", "medium", "high", "xhigh", "max", "ultra"].map(String::from)[..]),
        "codex: its effort levels"
    );

    let claude_text = common::fixture("model-list-claude.jsonl");
    let claude_lines: Vec<&str> = claude_text.split('\n').map(str::trim_end).filter(|l| !l.is_empty()).collect();
    let initialize = common::json(claude_lines[1]);
    let claude = providers::claude_catalog(initialize["response"]["response"]["models"].as_array().unwrap());
    assert!(claude.is_fresh() && claude.models.len() == 14, "claude: 14 rows, got {}", claude.models.len());
    assert_eq!(claude.models[0].resolved_model.as_deref(), Some("claude-opus-5-5"), "claude: the default stands for Opus 5.5");
    assert!(
        claude
            .models
            .iter()
            .find(|m| m.value == "fable")
            .is_some_and(|m| m.display_name == "Fable 5.2" && m.resolved_model.as_deref() == Some("claude-fable-5-2")),
        "claude: fable is Fable 5.2"
    );
    assert!(
        claude.models.iter().any(|m| m.value == "claude-fable-5-1")
            && claude.models.iter().any(|m| m.value == "haiku" && m.resolved_model.as_deref() == Some("claude-haiku-5-5")),
        "claude: the older Fable and Haiku 5.5"
    );
    // An answer that lists no model is a failed read, as on the Mac.
    assert!(
        providers::claude_catalog(&[]).source == "fallback" && providers::codex_catalog(&[]).source == "fallback",
        "no model is the built-in list"
    );
}

#[test]
fn a_failed_read_names_the_command_and_the_line_that_says_why() {
    common::korean();
    let usage = common::fixture("model-list-agy-unknown-flag.txt");
    let exit = providers::read_failure("gemini", &strings::get_with("provider.catalog.failure.exit", &[("code", "1")]), &usage);
    assert_eq!(exit, "agy models: 종료 코드 1 · Error: flags provided but not defined: -standalone");
    // No line naming an error: the first that says anything (pi without a sign-in).
    let none = providers::read_failure(
        "pi",
        &strings::get("provider.catalog.failure.noModels"),
        "No models available. Use /login to log in.\n  /x/docs/providers.md\n",
    );
    assert_eq!(none, "pi --list-models: 출력에서 모델을 찾지 못했습니다 · No models available. Use /login to log in.");
    assert_eq!(
        providers::read_failure("codex", &strings::get("process.error.timeout"), ""),
        format!("codex app-server --listen stdio://: {}", strings::get("process.error.timeout")),
        "a timeout has no line of its own"
    );
    let home = std::env::var("USERPROFILE").or_else(|_| std::env::var("HOME")).unwrap();
    let auth = std::path::Path::new(&home).join(".pi").join("agent").join("auth.json");
    let redacted = providers::read_failure("pi", "x", &format!("error: {} unreadable", auth.display()));
    assert!(!redacted.to_lowercase().contains(&home.to_lowercase()) && redacted.contains('~'), "the home folder reads ~: {redacted}");
    assert!(providers::read_failure("pi", "x", &"error ".repeat(400)).len() <= 400, "one bounded line");
}

/// TheNewFieldsAreAdditiveOnTheWire's catalogue half (the remote's /v2/info is W8's).
#[test]
fn the_new_fields_are_additive_on_the_wire() {
    let fresh = common::json(&catalog_json(&ModelCatalog::new("cli", vec![ModelOption::new("opus", "Opus", "")], "")));
    assert!(
        fresh.get("readFailure").is_none() && fresh.get("listedAt").is_none() && fresh.get("isFresh").is_none(),
        "a fresh list is encoded as before: {fresh}"
    );
    let kept = ModelCatalog {
        read_failure: Some("claude: 종료 코드 1".into()),
        listed_at: Some("2026-11-11T02:00:00Z".into()),
        ..ModelCatalog::new("cli", vec![ModelOption::new("opus", "Opus", "")], "d")
    };
    let data = common::json(&catalog_json(&kept));
    assert!(data["readFailure"] == "claude: 종료 코드 1" && data["listedAt"] == "2026-11-11T02:00:00Z", "a kept list has the Mac's names");
    // AKeptListRetiresNothing's first check: only a list the latest read gave is fresh.
    assert!(!kept.is_fresh() && !providers::fallback("claude").is_fresh(), "only a list the latest read gave is fresh");
}

// ── AutoPermissionVerification ──────────────────────────────────────────────────────────────────────────────────────────

/// "Claude Auto permissions, supported runtime gate and legacy-safe persistence" (the restart through StateStore is mighty-core's).
#[test]
fn auto_permission() {
    common::korean();
    let selected = RunSettings { permission_mode: "auto".into(), ..RunSettings::default() };
    let request = StartRunRequest::new("pane", "workspace", "claude", "metadata fixture only").with_settings(selected.clone());
    request.validate().unwrap();
    assert_eq!(RunSettings::default().permission_mode, "manual", "The safe default changed.");
    assert_eq!(providers::normalize_settings("claude", Some(&selected)), selected, "Saved Auto was lost before discovery.");
    for provider in ["codex", "gemini"] {
        assert!(request.clone().with_provider(provider).validate().is_err());
        assert_eq!(providers::normalize_settings(provider, Some(&selected)).permission_mode, "manual", "Auto leaked to another provider.");
    }
    let mut shell = request.clone();
    shell.kind = "shell".into();
    assert!(shell.validate().is_err());
    for version in [None, Some("unknown"), Some("2.1.82"), Some("2.1.270"), Some("2.1.273-preview")] {
        assert!(
            !providers::capabilities("claude", version).permission_modes.contains(&"auto".to_string()),
            "Unsupported CLI advertised Auto: {version:?}"
        );
    }
    for version in ["2.1.271", "2.1.273 (Claude Code)", "3.0.0"] {
        assert!(
            providers::capabilities("claude", Some(version)).permission_modes.contains(&"auto".to_string()),
            "Supported CLI omitted Auto: {version}"
        );
    }
    let args = providers::arguments(&request, "/tmp/plugin", None).unwrap();
    assert_eq!(common::after(&args, "--permission-mode"), "auto", "Auto was translated into another mode.");
    assert_eq!(common::after(&args, "--permission-prompts"), "none", "Windows silently claimed an approval channel.");
    // Auto widens nothing: the one tool list it names is Claude Code's own task tools, which the request's plan needs.
    assert!(
        !args.contains(&"bypassPermissions".to_string())
            && args.iter().filter(|a| *a == "--allowedTools").count() == 1
            && common::after(&args, "--allowedTools") == TASK_TOOLS.join(","),
        "Auto weakened explicit policy."
    );
    let json = common::json(&selected.to_json());
    assert_eq!(json.as_object().unwrap().len(), 4, "New wire fields broke legacy shape.");
    assert_eq!(RunSettings::read(&json).unwrap(), selected, "Auto wire value was lost.");
}

/// Verification.cs "wire settings include explicit nullable limits; provider validation".
#[test]
fn wire_settings_include_explicit_nullable_limits_and_provider_validation() {
    common::korean();
    let json = RunSettings::default().to_json();
    assert!(json.contains("\"maxTurns\":null") && json.contains("\"maxBudgetUsd\":null"));
    let high = RunSettings { effort: "high".into(), ..RunSettings::default() };
    assert!(StartRunRequest::new("p", "w", "claude", "x").with_provider("gemini").with_settings(high.clone()).validate().is_err());
    let plan = RunSettings { permission_mode: "plan".into(), ..RunSettings::default() };
    assert!(StartRunRequest::new("p", "w", "claude", "x").with_provider("codex").with_settings(plan).validate().is_err());
    let mut sonnet = StartRunRequest::new("p", "w", "claude", "secret").with_settings(RunSettings {
        max_turns: Some(3),
        max_budget_usd: Some(1.5),
        ..high
    });
    sonnet.model = "sonnet".into();
    assert!(providers::arguments(&sonnet, "/plugin", None).unwrap().contains(&"--effort".to_string()));
    assert!(providers::efforts("codex", "default", &providers::fallback("codex"), None).is_empty());
    assert!(!providers::supports_mods(Some("2.1.263")));
    assert!(providers::supports_mods(Some("2.1.271 (Claude Code)")));
}

// ── Reads over a real child (Windows) ───────────────────────────────────────────────────────────────────────────────────

#[cfg(windows)]
mod reads {
    use super::*;

    /// The fake list CLIs (C# `FakeListClis`): each answers its verified arguments with the real CLI's recorded output.
    struct FakeListClis {
        state: std::path::PathBuf,
    }

    impl FakeListClis {
        fn new() -> FakeListClis {
            FakeListClis { state: common::temp("model-lists") }
        }
        fn state(&self) -> String {
            self.state.to_string_lossy().into_owned()
        }
        fn command(&self, provider: &str) -> mighty_run::model::CliCommand {
            common::fake_command(&["--model-list-cli", provider, &self.state()])
        }
        fn catalog(&self) -> std::sync::Arc<providers::ProviderCatalog> {
            let state = self.state();
            common::catalog(move |provider| Some(common::fake_command(&["--model-list-cli", provider, &state])))
        }
        /// `Catalog(storage)`: the lists kept in `storage` too.
        fn catalog_kept(&self, storage: &std::path::Path) -> providers::ProviderCatalog {
            let state = self.state();
            let finder: providers::Finder = std::sync::Arc::new(move |provider: String| {
                let found = Some(common::fake_command(&["--model-list-cli", &provider, &state]));
                Box::pin(async move { found })
            });
            providers::ProviderCatalog::new(
                std::sync::Arc::new(mighty_platform_windows::WindowsProcessHost::new()),
                common::environment(),
                Some(finder),
            )
            .with_storage(Some(storage.to_path_buf()))
        }
        fn recover(&self, provider: &str) {
            let _ = std::fs::remove_file(self.file(provider, "-fail"));
        }
        fn file(&self, provider: &str, suffix: &str) -> std::path::PathBuf {
            self.state.join(format!("{}{suffix}", providers::executable(provider)))
        }
        fn arguments(&self, provider: &str) -> Option<Vec<String>> {
            std::fs::read_to_string(self.file(provider, "-argv.json")).ok().map(|t| serde_json::from_str(&t).unwrap())
        }
        fn fail(&self, provider: &str, stderr: &str) {
            std::fs::write(self.file(provider, "-fail"), stderr).unwrap();
        }
        fn output(&self, provider: &str, stdout: &str) {
            std::fs::write(self.file(provider, "-output"), stdout).unwrap();
        }
    }

    impl Drop for FakeListClis {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.state);
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_fake_cli_gives_its_fixture_list() {
        common::korean();
        let expected = [
            ("claude", "fable"),
            ("codex", "gpt-6.1-sol"),
            ("gemini", "gemini-4-carbon-medium"),
            ("pi", "anthropic/claude-fable-5-2"),
            ("opencode", "opencode/big-pickle"),
        ];
        let fake = FakeListClis::new();
        let catalog = fake.catalog();
        for (provider, arguments) in verified() {
            let runtime = catalog.refresh_provider(provider, Some(&fake.state()), false).await;
            assert!(runtime.model_catalog.is_fresh(), "{provider}: {:?}", runtime.model_catalog.read_failure);
            assert!(fake.arguments(provider).is_some_and(|a| a == arguments), "{provider} was run with {:?}", fake.arguments(provider));
            let want = expected.iter().find(|(p, _)| *p == provider).unwrap().1;
            assert!(runtime.model_catalog.models.iter().any(|m| m.value == want), "{provider} lists {want}");
        }
        let environment: BTreeMap<String, Option<String>> =
            serde_json::from_str(&std::fs::read_to_string(fake.state.join("claude-env.json")).unwrap()).unwrap();
        assert!(
            environment["CLAUDE_CODE_SAFE_MODE"].as_deref() == Some("1")
                && (environment["DISABLE_TELEMETRY"].is_none() || std::env::var("DISABLE_TELEMETRY").is_ok()),
            "Claude's read is quiet and lets it fetch the current models"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn agy_refuses_the_opencode_flag_and_its_own_read_lists() {
        common::korean();
        let fake = FakeListClis::new();
        let command = fake.command("gemini");
        // What the Mac ran before its fix, answered as the real agy 1.5.5 answers it.
        let host = mighty_platform_windows::WindowsProcessHost::new();
        let mut args = command.prefix.clone();
        args.extend(["models".to_string(), "--standalone".to_string()]);
        let request =
            mighty_platform::ProcessHost::cli_request(&host, std::path::Path::new(&command.binary), &args, &fake.state, &BTreeMap::new())
                .unwrap();
        let captured = mighty_platform::capture(&host, request, None, std::time::Duration::from_secs(20), 1 << 20).await.unwrap();
        let errors = String::from_utf8_lossy(&captured.stderr).into_owned();
        assert!(
            captured.exit_code == 1 && errors.contains("flags provided but not defined: -standalone"),
            "agy refuses --standalone: {errors}"
        );
        let catalog = fake.catalog();
        let read = catalog.read_list_command("gemini", &command, Some(&fake.state())).await;
        assert_eq!(
            catalog_json(&read),
            catalog_json(&providers::antigravity_catalog(&common::fixture("model-list-agy.txt"))),
            "agy models gives the recorded list"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_failed_read_keeps_the_last_list_marked_and_a_restart_still_has_it() {
        use mighty_run::kept_lists::{self, KeptModelLists, ModelListNotice};
        common::korean();
        let fake = FakeListClis::new();
        let storage = fake.state.join("storage");
        let workspace = fake.state.join("site");
        std::fs::create_dir_all(&workspace).unwrap();
        let workspace = workspace.to_string_lossy().into_owned();
        let file = storage.join(kept_lists::FILE_NAME);
        const SIGNED_OUT: &str = "Error: authentication required. Run 'agy' to log in, then retry.\n";
        let (fresh, kept) = {
            let catalog = fake.catalog_kept(&storage);
            let fresh = catalog.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
            assert!(fresh.is_fresh() && kept_lists::notice(&fresh, None).is_none(), "a list the CLI just gave needs no line");
            let saved = KeptModelLists::load(Some(&file));
            assert!(
                saved.list("gemini", Some(&workspace)).is_some_and(|e| models_json(&e.models) == models_json(&fresh.models)),
                "the list is in the file"
            );

            fake.fail("gemini", SIGNED_OUT);
            let kept = catalog.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
            assert!(
                kept.source == "cli"
                    && !kept.is_fresh()
                    && models_json(&kept.models) == models_json(&fresh.models)
                    && kept.detail == fresh.detail,
                "the last list stays, marked"
            );
            assert_eq!(
                kept.read_failure.as_deref(),
                Some("agy models: 종료 코드 1 · Error: authentication required. Run 'agy' to log in, then retry."),
                "why"
            );
            assert!(kept_lists::date(kept.listed_at.as_deref()).is_some(), "when it was read");
            assert_eq!(
                kept_lists::notice(&kept, None),
                Some(ModelListNotice {
                    line: strings::get_with("composer.model.listKept", &[("age", &strings::get("resume.time.now"))]),
                    help: kept.read_failure.clone().unwrap(),
                }),
                "the menu line"
            );
            // Another workspace that never read a list gets this provider's newest, marked the same way.
            let other = catalog.refresh_provider("gemini", Some(&fake.state()), false).await.model_catalog;
            assert!(
                models_json(&other.models) == models_json(&fresh.models) && other.read_failure.is_some(),
                "another workspace gets the newest list"
            );
            (fresh, kept)
        };
        // After a restart the read still fails: the list comes back from the file, not the built-in one.
        {
            let restarted = fake.catalog_kept(&storage);
            let restored = restarted.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
            assert!(
                restored.source == "cli"
                    && models_json(&restored.models) == models_json(&fresh.models)
                    && restored.read_failure == kept.read_failure,
                "a restart keeps the list"
            );
            // A list the CLI gives again is fresh again, and only that one.
            fake.recover("gemini");
            assert!(
                restarted.refresh_provider("gemini", Some(&workspace), false).await.model_catalog.is_fresh(),
                "the next good read is fresh"
            );
        }
        // With nothing kept, the built-in list, and why.
        fake.fail("gemini", SIGNED_OUT);
        let empty = fake.catalog_kept(&fake.state.join("empty"));
        let built_in = empty.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
        assert!(
            built_in.source == "fallback"
                && models_json(&built_in.models) == models_json(&providers::fallback("gemini").models)
                && built_in.read_failure == kept.read_failure,
            "the built-in list with the reason"
        );
        assert_eq!(
            kept_lists::notice(&built_in, None),
            Some(ModelListNotice { line: strings::get("composer.model.listFallback"), help: kept.read_failure.clone().unwrap() }),
            "the built-in list's line"
        );
        // A catalog without a storage folder (the checks) keeps the lists in memory only: a read that fails later still has the last
        // list, and no lists file is written or changed anywhere.
        let before = kept_files(&fake.state);
        assert!(before.iter().any(|(path, _)| *path == file), "the storage folder's file is there to compare");
        fake.recover("gemini");
        let memory = fake.catalog();
        let listed = memory.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
        fake.fail("gemini", SIGNED_OUT);
        let remembered = memory.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
        assert!(
            listed.is_fresh()
                && remembered.source == "cli"
                && models_json(&remembered.models) == models_json(&listed.models)
                && remembered.read_failure == kept.read_failure,
            "the last list is kept in memory"
        );
        assert_eq!(kept_files(&fake.state), before, "nothing is written without a storage folder");
    }

    /// A kept-lists file that is there but cannot be read (another program holds it) is not taken for "no lists": the list the CLI gave
    /// is used, nothing is written over the file, and once it reads, the lists it holds stay beside the new one.
    #[tokio::test(flavor = "multi_thread")]
    async fn a_kept_lists_file_that_cannot_be_read_is_not_written_over() {
        use std::os::windows::fs::OpenOptionsExt as _;

        use mighty_run::kept_lists::{self, Entry, KeptModelLists};
        common::korean();
        let fake = FakeListClis::new();
        let storage = fake.state.join("storage");
        let workspace = fake.state.join("site");
        std::fs::create_dir_all(&workspace).unwrap();
        let workspace = workspace.to_string_lossy().into_owned();
        let file = storage.join(kept_lists::FILE_NAME);
        // Another workspace's Codex list, kept by an earlier launch.
        let other = Entry {
            provider: "codex".into(),
            workspace: Some(fake.state.join("other").to_string_lossy().into_owned()),
            listed_at: "2026-11-11T02:00:00Z".into(),
            detail: "d".into(),
            models: vec![ModelOption::new("gpt-6.1-sol", "Sol", "")],
        };
        kept_lists::write(&file, &KeptModelLists::new(vec![other.clone()]).encoded()).unwrap();
        let catalog = fake.catalog_kept(&storage);
        // Held without sharing: the catalog's first read of the file fails.
        let held = std::fs::OpenOptions::new().read(true).share_mode(0).open(&file).unwrap();
        assert!(KeptModelLists::read(Some(&file)).is_err(), "the fixture must hold the file");
        let fresh = catalog.refresh_provider("gemini", Some(&workspace), false).await.model_catalog;
        assert!(fresh.is_fresh(), "the list the CLI gave is used as it is");
        drop(held);
        assert_eq!(
            KeptModelLists::load(Some(&file)).entries(),
            std::slice::from_ref(&other),
            "nothing was written over the file it could not read"
        );
        // The file reads now: the next list the CLI gives is kept beside the ones it holds.
        catalog.refresh_provider("gemini", Some(&workspace), false).await;
        let saved = KeptModelLists::load(Some(&file));
        assert!(
            saved.list("codex", other.workspace.as_deref()) == Some(&other) && saved.list("gemini", Some(&workspace)).is_some(),
            "the lists it held stay: {:?}",
            saved.entries().iter().map(|e| e.provider.as_str()).collect::<Vec<_>>()
        );
    }

    /// Every kept-lists file under `folder`, with what it holds.
    fn kept_files(folder: &std::path::Path) -> Vec<(std::path::PathBuf, Vec<u8>)> {
        let mut found = Vec::new();
        for entry in std::fs::read_dir(folder).into_iter().flatten().flatten() {
            let path = entry.path();
            if path.is_dir() {
                found.extend(kept_files(&path));
            } else if path.file_name().is_some_and(|n| n == mighty_run::kept_lists::FILE_NAME) {
                found.push((path.clone(), std::fs::read(&path).unwrap()));
            }
        }
        found.sort();
        found
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_missing_cli_or_no_models_is_a_failed_read_too() {
        common::korean();
        let fake = FakeListClis::new();
        let missing = common::absent_catalog();
        let none = missing.refresh_provider("pi", Some(&fake.state()), false).await.model_catalog;
        assert!(
            none.source == "fallback" && none.read_failure == Some(strings::get_with("run.error.cliExecutableMissing", &[("name", "pi")])),
            "no CLI: {:?}",
            none.read_failure
        );
        let catalog = fake.catalog();
        fake.output("pi", "No models available. Use /login to log into a provider via OAuth or API key. See:\n  /x/docs/providers.md\n");
        let empty = catalog.refresh_provider("pi", Some(&fake.state()), false).await.model_catalog;
        assert_eq!(
            (empty.source.as_str(), empty.read_failure.as_deref()),
            (
                "fallback",
                Some(
                    "pi --list-models: 출력에서 모델을 찾지 못했습니다 · No models available. Use /login to log into a provider via OAuth or API key. See:"
                )
            ),
            "no models"
        );
        fake.fail("codex", "Error: not logged in\n");
        let codex = catalog.refresh_provider("codex", Some(&fake.state()), false).await.model_catalog;
        assert_eq!(
            (codex.source.as_str(), codex.read_failure.as_deref()),
            ("fallback", Some("codex app-server --listen stdio://: 종료 코드 1 · Error: not logged in")),
            "codex ended"
        );
        fake.fail("claude", "Error: something broke\n");
        let claude = catalog.refresh_provider("claude", Some(&fake.state()), false).await.model_catalog;
        assert_eq!(
            (claude.source.as_str(), claude.read_failure.as_deref()),
            ("fallback", Some("claude: 종료 코드 1 · Error: something broke")),
            "claude ended"
        );
    }

    /// Verification.cs "Claude/Codex metadata protocol sends no user prompt or model turn".
    #[tokio::test(flavor = "multi_thread")]
    async fn claude_and_codex_metadata_protocol_sends_no_user_prompt_or_model_turn() {
        common::korean();
        let directory = common::temp("metadata");
        let catalog = common::absent_catalog();
        for provider in ["claude", "codex"] {
            let record = directory.join(provider).to_string_lossy().into_owned();
            let read = catalog.read_models(provider, &common::fake_command(&["--fake-cli", provider, &record]), None, None).await;
            assert!(read.source == "cli" && read.models.len() == 2, "{provider} metadata: {read:?}");
            let input = std::fs::read_to_string(format!("{record}.input")).unwrap();
            assert!(!input.contains("turn/start") && !input.contains("\"type\":\"user\""));
            let arguments = std::fs::read_to_string(format!("{record}.args")).unwrap();
            if provider == "claude" {
                assert!(
                    arguments.contains("--no-session-persistence")
                        && arguments.contains("--safe-mode")
                        && input.split('\n').filter(|l| !l.is_empty()).count() == 1
                );
            }
        }
        common::remove_when_free(&directory).await;
    }
}
