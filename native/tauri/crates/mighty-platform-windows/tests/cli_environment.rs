//! C# `CliEnvironmentVerification` (MightyCode.Core.Tests/CliEnvironmentVerification.cs): the profile capture's framing, its
//! token rotation and explicit unsets, its time limit and freshness, and the real PowerShell wire (a Windows-only check in C#).
#![cfg(windows)]

mod common;

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use mighty_platform::{EnvironmentSource, ShellEnvironment, SpawnRequest};
use mighty_platform_windows::environment::{self, CliEnvironmentSnapshot, MAXIMUM_OUTPUT};
use mighty_platform_windows::{Variables, WindowsShellEnvironment};
use tokio::sync::Notify;

const NONCE: &str = "0123456789abcdef0123456789abcdef";

/// The capture's output around one frame (CliEnvironmentVerification.cs:9).
fn frame(nonce: &str, json: &str) -> String {
    format!("profile chatter\n\0MIGHTY_ENV_{nonce}\0{}\0MIGHTY_END_{nonce}\0after profile output", STANDARD.encode(json.as_bytes()))
}

fn json(pairs: &[(&str, &str)]) -> String {
    serde_json::to_string(&pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect::<BTreeMap<_, _>>()).unwrap()
}

fn variables(pairs: &[(&str, &str)]) -> Variables {
    pairs.iter().copied().collect()
}

fn capture_with(capture: impl Fn(String) -> Option<String> + Send + Sync + 'static) -> environment::Capture {
    let capture = Arc::new(capture);
    Arc::new(move |_baseline, nonce| {
        let capture = capture.clone();
        Box::pin(async move { capture(nonce) })
    })
}

/// C# "CLI environment profile frame rejects ambiguity and redacts diagnostics" (CliEnvironmentVerification.cs:10-24).
#[test]
fn strict_framing_and_secrets() {
    let valid = frame(NONCE, &json(&[("Path", r"C:\trusted bin"), ("AWS_BEARER_TOKEN_BEDROCK", "fixture-secret=\nsecond-line")]));
    let values = environment::parse(&valid, NONCE).expect("a valid frame");
    assert!(
        values.get("PATH") == Some(r"C:\trusted bin") && values.get("AWS_BEARER_TOKEN_BEDROCK") == Some("fixture-secret=\nsecond-line"),
        "Framing must preserve Unicode/newlines and Windows case semantics."
    );
    assert!(environment::parse(&valid, &"a".repeat(32)).is_none(), "A different invocation's frame is rejected.");
    assert!(environment::parse(&format!("{valid}{valid}"), NONCE).is_none(), "Repeated frames are ambiguous and rejected.");
    for json in [
        "{}",
        "[]",
        r#"{"PATH":"x","path":"y"}"#,
        r#"{"PATH":"x","bad=name":"x"}"#,
        r#"{"PATH":"x","AWS_KEY":true}"#,
        r#"{"PATH":"x","AWS_KEY":"bad\u0000value"}"#,
    ] {
        assert!(
            environment::parse(&frame(NONCE, json), NONCE).is_none(),
            "Malformed/partial environment cannot replace the current environment: {json}"
        );
    }
    assert!(environment::parse(&format!("{valid}{}", "x".repeat(MAXIMUM_OUTPUT)), NONCE).is_none(), "Profile chatter is bounded too.");
    let command = environment::command(NONCE).unwrap();
    assert!(
        !command.contains("fixture-secret")
            && !command.contains("ExecutionPolicy")
            && !command.contains("-NoProfile")
            && !command.contains("Set-Location"),
        "Capture never embeds credentials or changes execution policy/project location."
    );
    let snapshot = CliEnvironmentSnapshot { values, from_profile: true };
    assert!(
        !format!("{snapshot}").contains("fixture-secret") && !format!("{snapshot:?}").contains("fixture-secret"),
        "Diagnostics never print the environment dictionary."
    );
    // Beyond C#'s list: exactly repeated names are as ambiguous as names in two cases.
    assert!(environment::parse(&frame(NONCE, r#"{"PATH":"x","PATH":"y"}"#), NONCE).is_none());
}

/// C# "CLI environment refresh rotates tokens and preserves explicit unsets" (CliEnvironmentVerification.cs:26-44).
#[tokio::test]
async fn refresh_rotation_and_unset() {
    let baseline =
        variables(&[("PATH", "base-path"), ("AWS_BEARER_TOKEN_BEDROCK", "old-inherited"), ("AWS_ACCESS_KEY_ID", "stale-access-key")]);
    #[derive(Default)]
    struct State {
        count: usize,
        value: String,
        malformed: bool,
    }
    let state = Arc::new(Mutex::new(State { value: "rotated-first".into(), ..State::default() }));
    let shared = state.clone();
    let resolver = WindowsShellEnvironment::with_capture(
        capture_with(move |nonce| {
            let mut state = shared.lock().unwrap();
            state.count += 1;
            Some(if state.malformed {
                "broken profile output".into()
            } else {
                frame(&nonce, &json(&[("PATH", "profile-path"), ("AWS_BEARER_TOKEN_BEDROCK", &state.value)]))
            })
        }),
        None,
        None,
    );
    let mut first = resolver.resolve_with(&baseline, false).await;
    assert!(
        first.from_profile
            && first.values.get("AWS_BEARER_TOKEN_BEDROCK") == Some("rotated-first")
            && first.values.get("AWS_ACCESS_KEY_ID") == Some(""),
        "Fresh profile wins and explicit unset does not revive inherited AWS credentials."
    );
    first.values.set("AWS_BEARER_TOKEN_BEDROCK", "consumer-mutation");
    let again = resolver.resolve_with(&baseline, false).await;
    assert!(
        again.values.get("AWS_BEARER_TOKEN_BEDROCK") == Some("rotated-first") && state.lock().unwrap().count == 1,
        "Cached credentials are returned as independent dictionaries."
    );
    state.lock().unwrap().value = "rotated-second".into();
    let forced = resolver.resolve_with(&baseline, true).await;
    assert!(
        forced.values.get("AWS_BEARER_TOKEN_BEDROCK") == Some("rotated-second") && state.lock().unwrap().count == 2,
        "Forced login/run refresh reloads the rotated token."
    );
    state.lock().unwrap().malformed = true;
    let failed = resolver.resolve_with(&baseline, true).await;
    assert!(
        !failed.from_profile
            && failed.values.get("AWS_BEARER_TOKEN_BEDROCK") == Some("old-inherited")
            && resolver.current_with(&baseline).get("PATH") == Some("base-path"),
        "Failed refresh explicitly returns baseline and discards prior captured credentials."
    );
}

/// C# "CLI environment timeout cancellation and forced freshness" (CliEnvironmentVerification.cs:46-69). C#'s cancelled token is a
/// dropped future here: it must neither hang nor leave a capture behind.
#[tokio::test(flavor = "multi_thread")]
async fn cancellation_timeout_and_freshness() {
    let baseline = variables(&[("PATH", "base-path")]);
    let never: environment::Capture = Arc::new(|_, _| Box::pin(std::future::pending()));
    let hanging = WindowsShellEnvironment::with_capture(never, Some(Duration::from_millis(30)), None);
    let timeout = hanging.resolve_with(&baseline, false).await;
    assert!(
        !timeout.from_profile && timeout.values.get("PATH") == Some("base-path"),
        "Slow interactive profiles fall back without leaking output."
    );
    let cancelled = tokio::time::timeout(Duration::from_millis(1), hanging.resolve_with(&baseline, true)).await;
    assert!(cancelled.is_err(), "a resolve whose caller gives up does not complete");
    assert_eq!(hanging.current_with(&baseline).get("PATH"), Some("base-path"), "and changes nothing");

    let started = Arc::new(Notify::new());
    let release = Arc::new(Notify::new());
    let generation = Arc::new(Mutex::new(0usize));
    let (signal, gate, counter) = (started.clone(), release.clone(), generation.clone());
    let capture: environment::Capture = Arc::new(move |_, nonce| {
        let (signal, gate, counter) = (signal.clone(), gate.clone(), counter.clone());
        Box::pin(async move {
            let generation = {
                let mut counter = counter.lock().unwrap();
                *counter += 1;
                *counter
            };
            if generation == 1 {
                signal.notify_one();
                gate.notified().await;
            }
            Some(frame(&nonce, &json(&[("PATH", &format!("generation-{generation}"))])))
        })
    });
    let resolver = Arc::new(WindowsShellEnvironment::with_capture(capture, Some(Duration::from_secs(10)), None));
    let (first, base) = (resolver.clone(), baseline.clone());
    let prior = tokio::spawn(async move { first.resolve_with(&base, false).await });
    started.notified().await;
    let (second, base) = (resolver.clone(), baseline.clone());
    let fresh = tokio::spawn(async move { second.resolve_with(&base, true).await });
    tokio::time::sleep(Duration::from_millis(50)).await;
    release.notify_one();
    prior.await.unwrap();
    let fresh = fresh.await.unwrap();
    assert!(
        fresh.values.get("PATH") == Some("generation-2") && resolver.current_with(&baseline).get("PATH") == Some("generation-2"),
        "Forced refresh cannot join an already-running capture or be overwritten by the older result."
    );

    let expiring = WindowsShellEnvironment::with_capture(
        capture_with(|nonce| Some(frame(&nonce, r#"{"PATH":"profile-path"}"#))),
        None,
        Some(Duration::ZERO),
    );
    expiring.resolve_with(&baseline, false).await;
    assert_eq!(
        expiring.current_with(&baseline).get("PATH"),
        Some("profile-path"),
        "Cache TTL never removes credentials from synchronous terminal launches; async entrypoints refresh them."
    );
}

/// The platform port over the same capture: `resolve` says where its values came from, `invalidate` forgets the capture.
#[tokio::test]
async fn the_port_reports_its_source_and_forgets_on_invalidate() {
    let resolver = WindowsShellEnvironment::with_capture(
        capture_with(|nonce| Some(frame(&nonce, r#"{"PATH":"profile-path","Mighty_Port":"1"}"#))),
        None,
        None,
    );
    assert_eq!(resolver.current().source, EnvironmentSource::ProcessFallback);
    let resolved = resolver.resolve(None, false).await;
    assert_eq!(resolved.source, EnvironmentSource::Profile);
    assert_eq!(resolved.values.get("Mighty_Port").map(String::as_str), Some("1"));
    assert!(resolved.values.keys().filter(|name| name.eq_ignore_ascii_case("PATH")).count() == 1, "one entry per name ignoring case");
    assert_eq!(resolver.current().source, EnvironmentSource::Profile);
    resolver.invalidate(None);
    assert_eq!(resolver.current().source, EnvironmentSource::ProcessFallback);
}

/// A deliberate difference from C# (docs/tauri-architecture.md §2.2): a capture that prints more than 1 MiB is a failed capture
/// here, where C#'s `ReadAsync` throws out of `RefreshAsync`. `run_capture` gives nothing and the resolve falls back to the
/// baseline. 4,081 lines of 257 bytes are 241 bytes past the limit, few enough for the pipe to take once the read has stopped, so
/// the child ends by itself.
#[tokio::test(flavor = "multi_thread")]
async fn a_capture_past_its_limit_falls_back_instead_of_failing() {
    let directory = common::temp("capture-limit");
    let request = common::fake_request(&["--noisy-child", &(4081 * 256).to_string()], &directory);
    let over = request.clone();
    let output = tokio::time::timeout(Duration::from_secs(30), environment::run_capture(over)).await.expect("the capture in 30 s");
    assert!(output.is_none(), "more than 1 MiB of output is no capture");
    let capture: environment::Capture = Arc::new(move |_baseline, _nonce| Box::pin(environment::run_capture(request.clone())));
    let resolver = WindowsShellEnvironment::with_capture(capture, Some(Duration::from_secs(30)), None);
    let resolved = resolver.resolve_with(&variables(&[("PATH", "baseline-path")]), true).await;
    assert!(!resolved.from_profile && resolved.values.get("PATH") == Some("baseline-path"), "the resolve falls back: {resolved}");
    common::remove_when_free(&directory).await;
}

/// The real port (the user's own PowerShell profiles, in the home folder) never fails and never waits past its 4 s bound; either
/// way the registry-refreshed PATH is there.
#[tokio::test(flavor = "multi_thread")]
async fn the_real_capture_resolves_within_its_bound() {
    let port = WindowsShellEnvironment::new();
    let started = std::time::Instant::now();
    let resolved = port.resolve(Some(&std::env::temp_dir()), true).await;
    assert!(started.elapsed() < Duration::from_secs(8), "bounded: {:?}", started.elapsed());
    assert!(resolved.values.iter().any(|(name, value)| name.eq_ignore_ascii_case("PATH") && !value.is_empty()), "{resolved:?}");
    assert_eq!(port.current().source, resolved.source);
}

/// C# "CLI environment real PowerShell capture wire" (CliEnvironmentVerification.cs:71-94; Windows-only in C#): the app's
/// ConsoleHost shell runs the capture, which preserves Unicode and an explicit deletion. Only this isolated fixture suppresses
/// personal profiles; the payload, framing and shell are production's.
#[tokio::test(flavor = "multi_thread")]
async fn windows_capture_wire() {
    let shell = mighty_platform_windows::WindowsPtyHost::new();
    let shell = mighty_platform::PtyHost::default_shell(&shell);
    assert!(
        shell.file_name().unwrap().eq_ignore_ascii_case("powershell.exe"),
        "The fixture exercises the app's selected ConsoleHost shell."
    );
    let mut arguments = environment::arguments(NONCE).unwrap();
    assert!(
        !arguments.iter().any(|argument| argument == "-NoProfile" || argument == "-ExecutionPolicy"),
        "Production keeps normal profile loading and execution policy."
    );
    arguments.insert(0, "-NoProfile".into());
    let encoded = STANDARD.decode(arguments.last().unwrap()).unwrap();
    let units: Vec<u16> = encoded.as_chunks::<2>().0.iter().map(|pair| u16::from_le_bytes(*pair)).collect();
    let command =
        format!("$env:MIGHTY_ENV_FIXTURE='token-한글=fixture';$env:MIGHTY_ENV_REMOVED=$null;{}", String::from_utf16(&units).unwrap());
    let utf16: Vec<u8> = command.encode_utf16().flat_map(u16::to_le_bytes).collect();
    *arguments.last_mut().unwrap() = STANDARD.encode(utf16);
    // What is checked is the capture, not its speed: a CI runner's first PowerShell start has run past 12 s.
    let request = SpawnRequest::new(shell, arguments, std::env::temp_dir()).env("MIGHTY_ENV_REMOVED", "inherited-fixture");
    let output = tokio::time::timeout(Duration::from_secs(60), environment::run_capture(request))
        .await
        .expect("the capture in 60 s")
        .expect("Synthetic PowerShell capture must finish successfully.");
    let values = environment::parse(&output, NONCE).expect("a frame");
    assert!(
        values.get("MIGHTY_ENV_FIXTURE") == Some("token-한글=fixture") && !values.contains("MIGHTY_ENV_REMOVED"),
        "Real PowerShell capture preserves Unicode and explicit credential deletion."
    );
}
