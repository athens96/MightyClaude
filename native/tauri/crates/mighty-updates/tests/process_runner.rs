//! The app's runner (`HostCliRunner`, C# `CliRunner`) over the platform's processes: on every OS against `mighty_platform::fake`'s
//! `ProcessHost` (the request it makes, the time limit, the cancel, the output cap), and on Windows end to end — the updater and both
//! plugin readers driving the crate's stand-in CLI (`mighty-updates-fake`, copied in as claude, codex, winget and node) through the
//! real `WindowsProcessHost`. Nothing real is started and nothing leaves the temp folders.

mod common;

use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;

use common::*;
use mighty_platform::fake::{FakeProcessHost, FakeScript};
use mighty_updates::runner::{CancellationToken, CliCall, CliRunner, HostCliRunner, RunError};

fn none() -> CancellationToken {
    CancellationToken::new()
}

#[tokio::test]
async fn the_host_runner_starts_the_cli_through_the_process_host() {
    let host = Arc::new(FakeProcessHost::default());
    host.push(FakeScript { stdout: b"2.1.271 (Claude Code)\n".to_vec(), stderr: b"warned".to_vec(), exit_code: 0, hang: false });
    let runner = HostCliRunner::new(host.clone());
    let folder = temp("runner-request");
    let program = text(&folder.join("claude.exe"));
    let call = CliCall::new(&program, ["plugin", "list"], Duration::from_secs(5))
        .environment(Some(BTreeMap::from([("GIT_TERMINAL_PROMPT".to_string(), "0".to_string())])))
        .without(["FORCE_AUTOUPDATE_PLUGINS"])
        .working_directory(Some(text(&folder)));
    let result = runner.run(call, none()).await.unwrap();
    assert!(result.exit_code == 0 && !result.timed_out, "{result:?}");
    assert_eq!((result.output.as_str(), result.error_output.as_str()), ("2.1.271 (Claude Code)\n", "warned"), "both outputs as text");
    let request = &host.spawned()[0];
    assert_eq!(request.program, folder.join("claude.exe"), "the full path the caller found");
    assert_eq!(request.args, ["plugin", "list"], "the argument list as it is, never a command line");
    assert_eq!(request.cwd, folder, "the working folder");
    assert!(request.env["GIT_TERMINAL_PROMPT"] == "0" && request.env["NO_COLOR"] == "1", "the call's variables over NO_COLOR=1");
    assert_eq!(request.env_remove, ["FORCE_AUTOUPDATE_PLUGINS"], "a name the child must not inherit");
    assert!(host.input(0).is_empty() && host.closed(0), "stdin closed at once, the child closed after");

    // A working folder that does not exist runs in the app's own.
    host.push(FakeScript::default());
    let elsewhere = CliCall::new(&program, ["--version"], Duration::from_secs(5)).working_directory(Some(text(&folder.join("gone"))));
    runner.run(elsewhere, none()).await.unwrap();
    assert_eq!(host.spawned()[1].cwd, std::env::current_dir().unwrap(), "the app's working folder");
}

#[tokio::test]
async fn a_run_past_its_time_is_killed_and_reported_timed_out() {
    let host = Arc::new(FakeProcessHost::default());
    host.push(FakeScript { hang: true, ..FakeScript::default() });
    let runner = HostCliRunner::new(host.clone());
    let program = text(&temp("runner-timeout").join("claude.exe"));
    let result = runner.run(CliCall::new(&program, ["update"], Duration::from_millis(50)), none()).await.unwrap();
    assert!(result.timed_out && result.exit_code == -1, "a slow child is killed and reads -1: {result:?}");
    assert!(host.closed(0), "and closed with everything it started");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_cancelled_run_kills_the_child_and_says_cancelled() {
    let host = Arc::new(FakeProcessHost::default());
    host.push(FakeScript { hang: true, ..FakeScript::default() });
    let runner = Arc::new(HostCliRunner::new(host.clone()));
    let program = text(&temp("runner-cancel").join("claude.exe"));
    let cancel = CancellationToken::new();
    let running = {
        let (runner, cancel, program) = (runner.clone(), cancel.clone(), program.clone());
        tokio::spawn(async move { runner.run(CliCall::new(&program, ["update"], Duration::from_secs(60)), cancel).await })
    };
    until(|| !host.spawned().is_empty(), 5000, "the child started").await;
    cancel.cancel();
    assert_eq!(running.await.unwrap(), Err(RunError::Cancelled), "the caller's cancel propagates");
    assert!(host.closed(0), "the child is closed");
    assert_eq!(
        runner.run(CliCall::new(&program, ["--version"], Duration::from_secs(5)), cancel).await,
        Err(RunError::Cancelled),
        "nothing starts once cancelled"
    );
    assert_eq!(host.spawned().len(), 1);
}

#[tokio::test]
async fn output_is_kept_to_the_cap_and_never_cut_through_a_character() {
    let host = Arc::new(FakeProcessHost::default());
    host.push(FakeScript { stdout: "abc한글".as_bytes().to_vec(), ..FakeScript::default() });
    let runner = HostCliRunner::new(host.clone()).with_output_cap(5);
    let program = text(&temp("runner-cap").join("claude.exe"));
    let result = runner.run(CliCall::new(&program, ["--version"], Duration::from_secs(5)), none()).await.unwrap();
    assert_eq!(result.output, "abc", "5 bytes keep \"abc\" and leave out the cut 한");
    assert_eq!(HostCliRunner::new(host).with_output_cap(0).output_cap(), HostCliRunner::OUTPUT_CAP_BYTES, "0 is the default cap");
}

#[cfg(windows)]
mod windows {
    use std::path::{Path, PathBuf};

    use mighty_updates::cli_update::{CliUpdateService, strings};
    use mighty_updates::plugins::strings::{codex as codex_strings, plugin as plugin_strings};
    use mighty_updates::plugins::{ClaudePluginReader, CodexPluginReader, PluginOperations, PluginReader, status};

    use super::*;

    fn runner() -> Arc<dyn CliRunner> {
        Arc::new(HostCliRunner::new(Arc::new(mighty_platform_windows::WindowsProcessHost::new())))
    }

    /// The stand-in copied in as `name` (`claude.exe`, …).
    fn stand_in(folder: &Path, name: &str) -> PathBuf {
        std::fs::create_dir_all(folder).unwrap();
        let path = folder.join(name);
        std::fs::copy(env!("CARGO_BIN_EXE_mighty-updates-fake"), &path).unwrap();
        path
    }

    /// The stand-in's state folder for everything under `home`.
    fn state(home: &Path) -> PathBuf {
        dir(home, &["fake-state"])
    }

    fn calls(home: &Path) -> Vec<String> {
        std::fs::read_to_string(home.join("fake-state").join("calls.txt")).unwrap_or_default().lines().map(str::to_string).collect()
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn claude_updates_itself_through_the_real_process_layer() {
        korean();
        let home = temp("e2e-native");
        let versions = dir(&home, &[".local", "share", "claude", "versions", "2.1.270"]);
        let claude = stand_in(&versions, "claude.exe");
        let state = state(&home);
        std::fs::write(state.join("claude.version"), "2.1.270").unwrap();
        std::fs::write(state.join("claude.next"), "2.1.271").unwrap();
        let service = CliUpdateService::new(runner(), env(&[&versions]), text(&home));

        let installation = service.inspect("claude", &none()).await;
        assert!(
            installation.method == "native" && installation.executable_path.as_deref() == Some(text(&claude).as_str()),
            "{installation:?}"
        );
        let result = service.update("claude", &none()).await;
        assert!(
            result.status == "updated"
                && result.before_version.as_deref() == Some("2.1.270")
                && result.after_version.as_deref() == Some("2.1.271"),
            "the stand-in replaced itself: {result:?}"
        );
        assert!(result.output.contains("updated"), "its output is kept for the details");
        assert_eq!(
            calls(&home),
            ["claude\t--version", "claude\t--version", "claude\tupdate", "claude\t--version"],
            "every run the update made"
        );

        // A hung installer is killed at the time limit, and a cancel stops it at once.
        std::fs::write(state.join("hang"), "").unwrap();
        let slow = CliUpdateService::new(runner(), env(&[&versions]), text(&home))
            .with_timeouts(Duration::from_secs(10), Duration::from_millis(500));
        let timed = slow.update("claude", &none()).await;
        assert!(
            timed.status == "failed" && timed.detail == strings::detail_failed_exit(-1),
            "the time limit reads as a failed run: {timed:?}"
        );
        let service = Arc::new(service);
        let running = {
            let service = service.clone();
            tokio::spawn(async move { service.update("claude", &CancellationToken::new()).await })
        };
        until(|| calls(&home).iter().filter(|c| c.ends_with("\tupdate")).count() == 3, 10_000, "the hung update started").await;
        service.cancel().await;
        let cancelled = running.await.unwrap();
        assert!(cancelled.status == "cancelled" && cancelled.detail == strings::detail_cancelled(), "{cancelled:?}");
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn npm_and_winget_installs_update_through_their_own_installer() {
        korean();
        let home = temp("e2e-npm");
        let prefix = dir(&home, &["AppData", "Roaming", "npm"]);
        stand_in(&prefix, "codex.exe");
        let package = prefix.join("node_modules").join("@openai").join("codex");
        std::fs::create_dir_all(&package).unwrap();
        std::fs::write(package.join("package.json"), r#"{"name":"@openai/codex","version":"0.51.0","bin":{"codex":"bin/codex.js"}}"#)
            .unwrap();
        let script = dir(&prefix, &["node_modules", "npm", "bin"]).join("npm-cli.js");
        std::fs::write(&script, "").unwrap();
        let nodejs = dir(&home, &["nodejs"]);
        stand_in(&nodejs, "node.exe");
        let package_root = dir(&home, &["Local", "Microsoft", "WinGet", "Packages", "Anthropic.ClaudeCode_8wekyb3d8bbwe"]);
        stand_in(&package_root, "claude.exe");
        let tools = dir(&home, &["tools"]);
        stand_in(&tools, "winget.exe");
        let state = state(&home);
        std::fs::write(state.join("codex.version"), "0.51.0").unwrap();
        std::fs::write(state.join("codex.next"), "0.52.0").unwrap();

        let service = CliUpdateService::new(runner(), env(&[&prefix, &nodejs, &package_root, &tools]), text(&home));
        let npm = service.update("codex", &none()).await;
        assert!(npm.status == "updated" && npm.method == "npm" && npm.after_version.as_deref() == Some("0.52.0"), "{npm:?}");
        let winget = service.update("claude", &none()).await;
        assert!(
            winget.status == "current" && winget.method == "winget" && winget.output.contains("No applicable upgrade found."),
            "{winget:?}"
        );
        let recorded = calls(&home);
        assert!(
            recorded.contains(&format!(
                "node\t{}\tinstall\t--global\t--prefix\t{}\t@openai/codex@latest\t--no-audit\t--no-fund",
                text(&script),
                text(&prefix)
            )),
            "npm ran through node with its own script: {recorded:?}"
        );
        assert!(
            recorded.contains(
                &"winget\tupgrade\t--id\tAnthropic.ClaudeCode\t--exact\t--silent\t--accept-source-agreements\t--accept-package-agreements\t--disable-interactivity"
                    .to_string()
            ),
            "winget upgraded exactly its package: {recorded:?}"
        );
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_claude_plugin_reader_lists_and_installs_through_the_stand_in() {
        korean();
        let home = temp("e2e-claude-plugins");
        let bin = dir(&home, &["bin"]);
        stand_in(&bin, "claude.exe");
        state(&home);
        let folder = text(&dir(&home, &["workspace"]));
        let mut environment = env(&[&bin]);
        environment.insert("FORCE_AUTOUPDATE_PLUGINS".into(), "1".into());
        let operations = PluginOperations::new(runner());
        let reader: Arc<dyn PluginReader> = Arc::new(ClaudePluginReader::new(operations.runner(), environment));

        let snapshot = reader.snapshot(&workspace(&folder), none()).await;
        assert!(snapshot.status == status::READY && snapshot.detail == plugin_strings::detail_ready(), "{snapshot:?}");
        assert!(snapshot.available.len() == 1 && snapshot.available[0].id == "fmt@sample", "the stand-in's catalog");
        let installed = reader.install("fmt@sample", "local", &workspace(&folder), none()).await;
        assert!(installed.status == status::SUCCEEDED && installed.detail == plugin_strings::install_succeeded(), "{installed:?}");
        let refreshed = reader.refresh_marketplace("sample", &workspace(&folder), none()).await;
        assert_eq!(refreshed.status, status::SUCCEEDED, "{refreshed:?}");
        assert!(
            calls(&home).contains(&"claude\tplugin\tinstall\tfmt@sample\t--scope\tlocal\t--json".to_string()),
            "the macOS argument list"
        );
        let seen = std::fs::read_to_string(home.join("fake-state").join("env.txt")).unwrap();
        assert!(
            seen.lines().all(|l| l.contains("FORCE_AUTOUPDATE_PLUGINS=<unset>")
                && l.contains("DISABLE_AUTOUPDATER=1")
                && l.contains("GIT_TERMINAL_PROMPT=0")),
            "the macOS switches reached every run and FORCE_AUTOUPDATE_PLUGINS none: {seen}"
        );
        let _ = std::fs::remove_dir_all(home);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_codex_plugin_reader_probes_installs_and_upgrades_through_the_stand_in() {
        korean();
        let home = temp("e2e-codex-plugins");
        let bin = dir(&home, &["bin"]);
        stand_in(&bin, "codex.exe");
        state(&home);
        let folder = text(&dir(&home, &["workspace"]));
        let reader = CodexPluginReader::new(runner(), env(&[&bin]));

        let snapshot = reader.snapshot(&workspace(&folder), none()).await;
        assert!(snapshot.status == status::READY && snapshot.installed.is_empty() && snapshot.available.len() == 1, "{snapshot:?}");
        let installed = reader.install("fmt@sample", "user", &workspace(&folder), none()).await;
        assert!(installed.status == status::SUCCEEDED && installed.detail == codex_strings::install_succeeded(), "{installed:?}");
        let upgraded = reader.refresh_marketplace("sample", &workspace(&folder), none()).await;
        assert_eq!(upgraded.status, status::SUCCEEDED, "{upgraded:?}");
        let automatic = reader.upgrade_marketplaces(&workspace(&folder), &none()).await;
        assert_eq!(automatic.status, "succeeded", "{automatic:?}");
        let recorded = calls(&home);
        for probe in [
            "codex\tplugin\tlist\t--help",
            "codex\tplugin\tadd\t--help",
            "codex\tplugin\tmarketplace\tlist\t--help",
            "codex\tplugin\tmarketplace\tupgrade\t--help",
        ] {
            assert!(recorded.contains(&probe.to_string()), "the probe {probe} ran");
        }
        assert!(recorded.contains(&"codex\tplugin\tadd\tfmt@sample\t--json".to_string()), "the install");
        assert!(recorded.contains(&"codex\tplugin\tmarketplace\tupgrade\t--json".to_string()), "the automatic upgrade");
        let _ = std::fs::remove_dir_all(home);
    }
}
