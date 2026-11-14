//! The status line (C# StatusLineVerification.cs, StatusLineShutdownVerification.cs, ComposerParityVerification.cs:380): the
//! settings precedence, the fingerprint, the payload's shape, ANSI parsing, the trust rule, the runner through `ProcessHost` (its
//! fake everywhere; on Windows the stand-in CLI through Windows' own), the shell Claude Code uses, and the refresher over a fake
//! clock. A runner never reads the user's settings.
//!
//! The refresher's runs are tasks here where C#'s first steps run on the caller's thread, so a check waits for a run to start or end
//! where C# reads the count at once.

mod common;

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_core::json::Wire;
use mighty_core::model::{AppSnapshot, SessionRateLimit};
use mighty_platform::BoxFuture;
use mighty_platform::fake::{FakeProcessHost, FakeScript};
use mighty_tools::status_line::ansi::{self, AnsiColor};
use mighty_tools::status_line::card;
use mighty_tools::status_line::config::{self, StatusLineConfig, StatusLineDiscovery};
use mighty_tools::status_line::payload::{self, StatusLineContext};
use mighty_tools::status_line::refresher::{self, Runner, StatusLineClock, StatusLineRefresher};
use mighty_tools::status_line::run::{self, Cancel, Cancelled, StatusLineResult};
use mighty_tools::status_line::strings::{self, SOURCE_USER, SOURCE_WORKSPACE, SOURCE_WORKSPACE_LOCAL};
use mighty_tools::status_line::trust;
use serde_json::Value;
use std::path::Path;
use tokio::sync::{Notify, watch};

/// The checks that read localized words, or change the language, take turns.
static LANGUAGE: Mutex<()> = Mutex::new(());

fn language() -> std::sync::MutexGuard<'static, ()> {
    LANGUAGE.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn user_config(command: &str) -> StatusLineConfig {
    StatusLineConfig::new(command, 0, SOURCE_USER, false)
}

fn workspace_config(command: &str) -> StatusLineConfig {
    StatusLineConfig::new(command, 0, SOURCE_WORKSPACE, true)
}

// ── settings and fingerprint ──────────────────────────────────────────────────────────────────────────────────────────────

/// "status line config follows Claude settings precedence and command-only entries" (StatusLineVerification.cs:20).
#[test]
fn status_line_config_follows_claude_settings_precedence_and_command_only_entries() {
    let root = common::temp("precedence");
    let home = root.join("home");
    let workspace = root.join("repo");
    let user_settings = home.join(".claude").join("settings.json");
    let local = workspace.join(".claude").join("settings.local.json");
    common::write(&user_settings, r#"{"statusLine":{"type":"command","command":"echo user","padding":2}}"#);
    let discovered = config::discover(None, Some(&home), None);
    let user = discovered.preferred().unwrap();
    assert!(user.command == "echo user" && user.padding == 2 && !user.from_workspace && user.source == SOURCE_USER);
    common::write(&workspace.join(".claude").join("settings.json"), r#"{"statusLine":{"type":"command","command":"echo proj"}}"#);
    let discovered = config::discover(Some(&workspace), Some(&home), None);
    let project = discovered.preferred().unwrap();
    assert!(
        project.command == "echo proj" && project.from_workspace && project.source == SOURCE_WORKSPACE,
        "workspace settings win over the user's"
    );
    common::write(&local, r#"{"statusLine":{"type":"command","command":"echo local"}}"#);
    let discovered = config::discover(Some(&workspace), Some(&home), None);
    let local_config = discovered.preferred().unwrap();
    assert!(
        local_config.command == "echo local" && local_config.source == SOURCE_WORKSPACE_LOCAL,
        "local settings win over the workspace's"
    );
    common::write(&user_settings, r#"{"theme":"dark"}"#);
    assert!(config::discover(None, Some(&home), None).preferred().is_none(), "no statusLine key returns null");
    common::write(&user_settings, r#"{"statusLine":{"type":"command","command":"echo user2"}}"#);
    common::write(&local, r#"{"statusLine":{"type":"other"}}"#);
    let disabled = config::discover(Some(&workspace), Some(&home), None);
    assert!(disabled.workspace_disabled && disabled.workspace.is_none(), "a non-command entry disables the level");
    assert!(
        disabled.user.as_ref().is_some_and(|u| u.command == "echo user2") && disabled.preferred().is_none(),
        "the user level stays visible, nothing is preferred"
    );
    common::write(&local, r#"{"statusLine":{"type":"command","command":"echo local"}}"#);
    let alt = root.join("altconfig");
    common::write(&alt.join("settings.json"), r#"{"statusLine":{"type":"command","command":"echo alt"}}"#);
    let env: BTreeMap<String, String> = [("CLAUDE_CONFIG_DIR".to_string(), alt.to_string_lossy().into_owned())].into();
    assert_eq!(
        config::discover(None, Some(&home), Some(&env)).preferred().map(|c| c.command.clone()).as_deref(),
        Some("echo alt"),
        "CLAUDE_CONFIG_DIR override"
    );
    common::write(&local, r#"{"statusLine":{"type":"command","command":"   "}}"#);
    assert!(config::discover(Some(&workspace), Some(&home), None).workspace_disabled, "a blank command disables the level");
    common::write(&local, r#"{"statusLine":{"type":"command","command":"echo x","padding":20}}"#);
    assert_eq!(config::discover(Some(&workspace), Some(&home), None).workspace.map(|c| c.padding), Some(8), "padding clamped to max 8");
    common::write(&local, r#"{"statusLine":{"type":"command","command":"echo x","padding":-5}}"#);
    assert_eq!(config::discover(Some(&workspace), Some(&home), None).workspace.map(|c| c.padding), Some(0), "padding clamped to min 0");
    common::write(
        &local,
        r#"{"statusLine":{"type":"command","command":"echo local"},"outputStyle":"Explanatory","alwaysThinkingEnabled":true}"#,
    );
    common::write(
        &user_settings,
        r#"{"statusLine":{"type":"command","command":"echo user2"},"outputStyle":"Explanatory","alwaysThinkingEnabled":true}"#,
    );
    let rich = config::discover(Some(&workspace), Some(&home), None);
    let (w, u) = (rich.workspace.clone().unwrap(), rich.user.clone().unwrap());
    assert!(
        w.output_style.as_deref() == Some("Explanatory") && w.thinking_enabled == Some(true),
        "the workspace's style and thinking carried"
    );
    assert!(u.output_style.as_deref() == Some("Explanatory") && u.thinking_enabled == Some(true), "the user's style and thinking carried");
    assert!(w.command == "echo local" && w.from_workspace && u.command == "echo user2" && !u.from_workspace, "discovery keeps both levels");
    assert_eq!(rich.preferred(), rich.workspace.as_ref(), "preferred is the winning level");
    common::remove(&root);
}

/// Settings files C# cannot read as entries: a byte-order mark, a statusLine that is no object, a field of the wrong kind.
#[test]
fn status_line_settings_read_as_csharp_reads_them() {
    let root = common::temp("settings-kinds");
    let settings = root.join(".claude").join("settings.json");
    let read = |text: &[u8]| {
        std::fs::create_dir_all(settings.parent().unwrap()).unwrap();
        std::fs::write(&settings, text).unwrap();
        config::discover(None, Some(&root), None).user
    };
    assert!(read(b"\xEF\xBB\xBF{\"statusLine\":{\"type\":\"command\",\"command\":\"x\"}}").is_none(), "Utf8JsonReader takes no mark");
    assert!(read(br#"{"statusLine":"x"}"#).is_none(), "a statusLine that is no object is no entry");
    assert!(read(br#"{"statusLine":{"type":"command","command":"x","padding":"2"}}"#).is_none(), "a padding of the wrong kind is no entry");
    assert_eq!(
        read(br#"{"statusLine":{"type":"command","command":"x","padding":2.5}}"#).map(|c| c.padding),
        Some(0),
        "a padding no Int32 reads 0"
    );
    common::remove(&root);
}

/// "status line fingerprint matches macOS formula" (StatusLineVerification.cs:116).
#[test]
fn status_line_fingerprint_matches_macos_formula() {
    let _language = language();
    let cfg = StatusLineConfig::new("echo hi", 0, "사용자 설정", false);
    let fingerprint = cfg.fingerprint();
    assert!(fingerprint.len() == 64 && fingerprint == fingerprint.to_lowercase());
    assert_eq!(
        StatusLineConfig::new("echo hi", 2, "사용자 설정", true).fingerprint(),
        fingerprint,
        "padding and fromWorkspace don't affect it"
    );
    assert_ne!(StatusLineConfig::new("echo bye", 0, "사용자 설정", false).fingerprint(), fingerprint);
    mighty_run::strings::set_language("ko");
    let korean = (cfg.fingerprint(), cfg.source_label());
    mighty_run::strings::set_language("en");
    let english = (cfg.fingerprint(), cfg.source_label());
    mighty_run::strings::set_language("ko");
    assert_eq!(english.0, korean.0, "the fingerprint must not change with the app language");
    assert_eq!(cfg.source, "사용자 설정");
    assert_eq!(english.1, mighty_core::locale::catalogue("en")["statusLine.source.user"]);
    assert_eq!(korean.1, mighty_core::locale::catalogue("ko")["statusLine.source.user"], "the label follows the language");
    assert_eq!(StatusLineConfig::new("echo hi", 0, "other", false).source_label(), "other", "an unknown source shows as it is");
}

// ── payload ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn limit(kind: &str, percent: f64, resets_at: Option<&str>) -> SessionRateLimit {
    SessionRateLimit { kind: kind.into(), percent_used: Some(percent), resets_at: resets_at.map(str::to_string) }
}

fn mac_shape_context(reset: &str) -> StatusLineContext {
    StatusLineContext {
        session_id: "sess".into(),
        cwd: Some("/repo".into()),
        project_dir: Some("/repo".into()),
        model_id: Some("claude-fable-5-1".into()),
        model_name: Some("Fable 5.1".into()),
        version: Some("2.1.274".into()),
        cost_usd: Some(1.5),
        duration_ms: Some(4000),
        api_duration_ms: Some(0),
        input_tokens: Some(120_000),
        output_tokens: Some(3000),
        cache_read_tokens: Some(100_000),
        cache_write_tokens: Some(5000),
        context_used_tokens: Some(150_000),
        context_window_tokens: Some(1_000_000),
        effort: Some("high".into()),
        fast_mode: true,
        rate_limits: Some(vec![
            limit("five_hour", 42.5, Some(reset)),
            limit("seven_day", 12.0, Some("2000-01-01T00:00:00Z")),
            SessionRateLimit { kind: "other".into(), percent_used: Some(1.0), resets_at: None },
        ]),
        output_style: None,
        thinking_enabled: None,
        transcript_path: Some(
            payload::transcript_path(Path::new("/Users/me/.claude"), Some("/repo"), "sess").to_string_lossy().into_owned(),
        ),
    }
}

fn empty_context() -> StatusLineContext {
    StatusLineContext {
        session_id: "s".into(),
        cwd: Some("/r".into()),
        project_dir: Some("/r".into()),
        model_id: Some("default".into()),
        model_name: Some("CLI 기본값".into()),
        version: Some(String::new()),
        transcript_path: Some(payload::transcript_path(Path::new("/Users/me/.claude"), Some("/r"), "s").to_string_lossy().into_owned()),
        ..StatusLineContext::default()
    }
}

fn reset_in_an_hour() -> String {
    (chrono::Utc::now() + chrono::Duration::hours(1)).format("%Y-%m-%dT%H:%M:%S%.6f+00:00").to_string()
}

fn payload_of(ctx: &StatusLineContext) -> Value {
    serde_json::from_str(&payload::build_payload(ctx, chrono::Utc::now())).unwrap()
}

fn keys(value: &Value) -> Vec<String> {
    let mut keys: Vec<String> = value.as_object().unwrap().keys().cloned().collect();
    keys.sort();
    keys
}

fn check_keys(value: &Value, expected: &[&str], what: &str) {
    let mut wanted: Vec<String> = expected.iter().map(|k| k.to_string()).collect();
    wanted.sort();
    assert_eq!(keys(value), wanted, "{what} keys");
}

/// "status line payload uses the CLI field names and transcript layout" (StatusLineVerification.cs:204).
#[test]
fn status_line_payload_uses_the_cli_field_names_and_transcript_layout() {
    assert_eq!(
        payload::transcript_path(Path::new("/Users/me/.claude"), Some("/Users/me/Work/My.App"), "abc"),
        Path::new("/Users/me/.claude").join("projects").join("-Users-me-Work-My-App").join("abc.jsonl"),
        "the leading dash is kept"
    );
    assert_eq!(payload::transcript_path(Path::new("/cfg"), Some("/x"), "s"), Path::new("/cfg").join("projects").join("-x").join("s.jsonl"));
    let r = payload_of(&mac_shape_context(&reset_in_an_hour()));
    assert!(r["hook_event_name"] == "Status" && r["session_id"] == "sess" && r["version"] == "2.1.274" && r["cwd"] == "/repo");
    assert_eq!(
        r["transcript_path"].as_str().unwrap(),
        Path::new("/Users/me/.claude").join("projects").join("-repo").join("sess.jsonl").to_string_lossy()
    );
    check_keys(&r["model"], &["id", "display_name"], "model");
    assert!(r["model"]["id"] == "claude-fable-5-1" && r["model"]["display_name"] == "Fable 5.1");
    check_keys(&r["workspace"], &["current_dir", "project_dir"], "workspace");
    assert!(r["workspace"]["current_dir"] == "/repo" && r["workspace"]["project_dir"] == "/repo");
}

/// "status line payload nests cost and fast mode like the CLI" (StatusLineVerification.cs:239).
#[test]
fn status_line_payload_nests_cost_and_fast_mode_like_the_cli() {
    let r = payload_of(&mac_shape_context(&reset_in_an_hour()));
    assert!(r.get("cost_usd").is_none(), "no flat cost_usd key");
    check_keys(
        &r["cost"],
        &["total_cost_usd", "total_duration_ms", "total_api_duration_ms", "total_lines_added", "total_lines_removed"],
        "cost",
    );
    assert!(r["cost"]["total_cost_usd"] == 1.5 && r["cost"]["total_duration_ms"] == 4000 && r["cost"]["total_api_duration_ms"] == 0);
    assert!(r["cost"]["total_lines_added"] == 0 && r["cost"]["total_lines_removed"] == 0 && r["fast_mode"] == true);
    let empty = payload_of(&empty_context());
    assert!(
        empty["cost"]["total_cost_usd"] == 0 && empty["cost"]["total_duration_ms"] == 0 && empty["fast_mode"] == false,
        "unmeasured cost is zero"
    );
}

/// "status line payload nests the context window like the CLI" (StatusLineVerification.cs:265).
#[test]
fn status_line_payload_nests_the_context_window_like_the_cli() {
    let ctx = mac_shape_context(&reset_in_an_hour());
    let r = payload_of(&ctx);
    let cw = &r["context_window"];
    check_keys(
        cw,
        &["total_input_tokens", "total_output_tokens", "context_window_size", "current_usage", "used_percentage", "remaining_percentage"],
        "context_window",
    );
    assert!(cw["context_window_size"] == 1_000_000 && cw["total_input_tokens"] == 120_000 && cw["total_output_tokens"] == 3000);
    assert!(
        (cw["used_percentage"].as_f64().unwrap() - 15.0).abs() < 0.01 && (cw["remaining_percentage"].as_f64().unwrap() - 85.0).abs() < 0.01
    );
    check_keys(
        &cw["current_usage"],
        &["input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"],
        "current_usage",
    );
    assert!(cw["current_usage"]["input_tokens"] == 150_000 && cw["current_usage"]["output_tokens"] == 0);
    assert!(cw["current_usage"]["cache_read_input_tokens"] == 0 && cw["current_usage"]["cache_creation_input_tokens"] == 0);
    let ew = payload_of(&empty_context())["context_window"].clone();
    assert!(
        ew["context_window_size"] == 200_000
            && ew["used_percentage"].is_null()
            && ew["remaining_percentage"].is_null()
            && ew["current_usage"].is_null()
    );
    assert_eq!(r["exceeds_200k_tokens"], false);
    assert_eq!(payload_of(&StatusLineContext { context_used_tokens: Some(250_000), ..ctx })["exceeds_200k_tokens"], true);
    assert_eq!(payload_of(&empty_context())["exceeds_200k_tokens"], false);
    // .NET's banker's rounding of the tenth (Math.Round): 0.25 % reads 0.2.
    let quarter = StatusLineContext { context_used_tokens: Some(25), context_window_tokens: Some(10_000), ..empty_context() };
    assert_eq!(payload_of(&quarter)["context_window"]["used_percentage"], 0.2);
}

/// "status line payload omits unknown sections like the CLI" (StatusLineVerification.cs:302).
#[test]
fn status_line_payload_omits_unknown_sections_like_the_cli() {
    let ctx = mac_shape_context(&reset_in_an_hour());
    let r = payload_of(&ctx);
    assert!(r.get("output_style").is_none() && r.get("thinking").is_none());
    let sr = payload_of(&StatusLineContext { output_style: Some("Explanatory".into()), thinking_enabled: Some(false), ..ctx.clone() });
    check_keys(&sr["output_style"], &["name"], "output_style");
    check_keys(&sr["thinking"], &["enabled"], "thinking");
    assert!(sr["output_style"]["name"] == "Explanatory" && sr["thinking"]["enabled"] == false);
    assert_eq!(payload_of(&StatusLineContext { thinking_enabled: Some(true), ..ctx.clone() })["thinking"]["enabled"], true);
    check_keys(&r["effort"], &["level"], "effort");
    assert_eq!(r["effort"]["level"], "high");
    assert!(payload_of(&StatusLineContext { effort: Some("turbo".into()), ..ctx })["effort"].is_null(), "unknown effort level omitted");
    let er = payload_of(&empty_context());
    assert!(er.get("effort").is_none() && er.get("rate_limits").is_none());
}

/// "status line payload keys rate limits by canonical kind" (StatusLineVerification.cs:333).
#[test]
fn status_line_payload_keys_rate_limits_by_canonical_kind() {
    let r = payload_of(&mac_shape_context(&reset_in_an_hour()));
    let rl = &r["rate_limits"];
    check_keys(rl, &["five_hour"], "rate_limits");
    assert!(rl["five_hour"]["used_percentage"] == 42.5 && rl["five_hour"]["resets_at"].is_i64(), "resets_at is a unix timestamp integer");
    let reset = reset_in_an_hour();
    let aliased = payload_of(&StatusLineContext {
        rate_limits: Some(vec![limit("session", 10.0, Some(&reset)), limit("weekly", 20.0, Some(&reset))]),
        ..mac_shape_context(&reset)
    })["rate_limits"]
        .clone();
    check_keys(&aliased, &["five_hour", "seven_day"], "aliased rate_limits");
    assert!(aliased["five_hour"]["used_percentage"] == 10.0 && aliased["seven_day"]["used_percentage"] == 20.0);
    let no_reset =
        payload_of(&StatusLineContext { rate_limits: Some(vec![limit("5h", 7.5, None)]), ..mac_shape_context(&reset) })["rate_limits"]
            .clone();
    check_keys(&no_reset["five_hour"], &["used_percentage"], "a rate limit without a reset time");
}

/// "status line payload has exactly the macOS top-level keys" (StatusLineVerification.cs:361).
#[test]
fn status_line_payload_has_exactly_the_macos_top_level_keys() {
    let ctx = StatusLineContext {
        output_style: Some("Explanatory".into()),
        thinking_enabled: Some(true),
        ..mac_shape_context(&reset_in_an_hour())
    };
    check_keys(
        &payload_of(&ctx),
        &[
            "hook_event_name",
            "session_id",
            "transcript_path",
            "cwd",
            "model",
            "workspace",
            "version",
            "cost",
            "fast_mode",
            "output_style",
            "thinking",
            "exceeds_200k_tokens",
            "context_window",
            "effort",
            "rate_limits",
        ],
        "payload with everything known",
    );
    check_keys(
        &payload_of(&empty_context()),
        &[
            "hook_event_name",
            "session_id",
            "transcript_path",
            "cwd",
            "model",
            "workspace",
            "version",
            "cost",
            "fast_mode",
            "exceeds_200k_tokens",
            "context_window",
        ],
        "payload with nothing measured",
    );
    // The key order is the CLI's, as C#'s JsonObject writes it.
    let text = payload::build_payload(&empty_context(), chrono::Utc::now());
    assert!(text.starts_with(r#"{"hook_event_name":"Status","session_id":"s","transcript_path":"#), "{text}");
    assert!(text.contains(r#""fast_mode":false,"exceeds_200k_tokens":false,"context_window":{"total_input_tokens":0"#), "{text}");
    assert!(text.contains(r#""display_name":"CLI \uAE30\uBCF8\uAC12""#), "non-ASCII escaped as System.Text.Json escapes it: {text}");
}

// ── ANSI ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// "status line ANSI parsing keeps colours weight and strips other escapes" (StatusLineVerification.cs:380).
#[test]
fn status_line_ansi_parsing_keeps_colours_weight_and_strips_other_escapes() {
    let segs = ansi::parse("\x1b[1;36mhello\x1b[0m world");
    assert_eq!(segs.len(), 2);
    assert!(segs[0].text == "hello" && segs[0].bold && segs[0].foreground == AnsiColor::CYAN);
    assert!(segs[1].text == " world" && !segs[1].bold && segs[1].foreground == AnsiColor::DEFAULT);
    let segs2 = ansi::parse("\x1b[2;3mfaded\x1b[22;23m normal");
    assert!(segs2[0].dim && segs2[0].italic && !segs2[1].dim && !segs2[1].italic);
    let segs3 = ansi::parse("\x1b]0;window title\x07visible");
    assert!(segs3.len() == 1 && segs3[0].text == "visible", "OSC stripped");
    let segs4 = ansi::parse("\x1b[92;101mgreen\x1b[0m");
    assert!(segs4[0].foreground == AnsiColor::BRIGHT_GREEN && segs4[0].background == AnsiColor::BRIGHT_RED);
    let segs5 = ansi::parse("\x1b[4munderlined\x1b[24m not");
    assert!(segs5[0].underline && !segs5[1].underline);
    let segs6 = ansi::parse("plain");
    assert!(segs6.len() == 1 && segs6[0].text == "plain");
}

/// "status line parses 256-colour and RGB like macOS" (StatusLineVerification.cs:415).
#[test]
fn status_line_parses_256_colour_and_rgb_like_macos() {
    let palette = ansi::parse("\x1b[1;38;5;208mbold\x1b[22m normal");
    assert!(palette[0].bold && palette[0].foreground == AnsiColor::palette(208) && !palette[1].bold);
    let rgb = ansi::parse("\x1b[38;2;10;20;30mrgb\x1b[39mplain");
    assert!(rgb[0].foreground == AnsiColor::rgb(10, 20, 30) && rgb[1].foreground == AnsiColor::DEFAULT);
    let bg_palette = ansi::parse("\x1b[48;5;21mbg");
    assert!(bg_palette[0].background == AnsiColor::palette(21) && bg_palette[0].foreground == AnsiColor::DEFAULT);
    assert_eq!(ansi::parse("\x1b[48;2;1;2;3mbg")[0].background, AnsiColor::rgb(1, 2, 3));
    assert_eq!(ansi::parse("\x1b[38;5;300mclamped")[0].foreground, AnsiColor::palette(255), "the palette index clamps");
    assert_eq!(ansi::parse("\x1b[31;38;5mafter")[0].foreground, AnsiColor::DEFAULT, "an incomplete 38;5 clears the foreground");
    let colon = ansi::parse("\x1b[1m\x1b[38:2::10:20:30mrgb");
    assert!(colon[0].bold && colon[0].foreground == AnsiColor::rgb(10, 20, 30), "colon rgb form with bold kept");
    assert_eq!(ansi::parse("\x1b[38:5:9mrgb")[0].foreground, AnsiColor::palette(9), "colon palette form");
    // Edges C#'s parser has: a lone escape at the end is kept, an unknown escape is kept raw, a non-SGR CSI goes with its final byte.
    assert_eq!(ansi::parse("a\x1b")[0].text, "a\x1b");
    assert_eq!(ansi::parse("a\x1bxb")[0].text, "a\x1bxb");
    assert_eq!(ansi::parse("a\x1b[2Kb").iter().map(|s| s.text.as_str()).collect::<String>(), "ab");
    assert_eq!(ansi::parse("\x1b[ 5 ;31mx")[0].foreground, AnsiColor::standard(1), "int.TryParse takes white space around a number");
}

/// "status line palette index maps to the xterm colours" (StatusLineVerification.cs:517).
#[test]
fn status_line_palette_index_maps_to_the_xterm_colours() {
    assert_eq!(ansi::palette_rgb(1), (128, 0, 0));
    assert_eq!(ansi::palette_rgb(9), (255, 0, 0));
    assert_eq!(ansi::palette_rgb(15), (255, 255, 255));
    assert_eq!(ansi::palette_rgb(16), (0, 0, 0));
    assert_eq!(ansi::palette_rgb(21), (0, 0, 255));
    assert_eq!(ansi::palette_rgb(196), (255, 0, 0));
    assert_eq!(ansi::palette_rgb(231), (255, 255, 255));
    assert_eq!(ansi::palette_rgb(232), (8, 8, 8));
    assert_eq!(ansi::palette_rgb(255), (238, 238, 238));
    assert!(
        ansi::palette_rgb(-5) == ansi::palette_rgb(0) && ansi::palette_rgb(999) == ansi::palette_rgb(255),
        "out-of-range indices clamp"
    );
}

// ── trust ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// "status line trust rule requires fingerprint for workspace commands" (StatusLineVerification.cs:451).
#[test]
fn status_line_trust_rule_requires_fingerprint_for_workspace_commands() {
    let ws = workspace_config("echo ws");
    let user = user_config("echo user");
    let map = |pairs: &[(&str, &str)]| -> mighty_core::model::StringMap {
        pairs.iter().map(|(k, v)| (k.to_string(), Some(v.to_string()))).collect()
    };
    assert!(trust::is_trusted(&user, None, "ws1") && trust::is_trusted(&user, Some(&map(&[])), "ws1"), "user config always trusted");
    assert!(!trust::is_trusted(&ws, None, "ws1") && !trust::is_trusted(&ws, Some(&map(&[])), "ws1"));
    let trusted = map(&[("ws1", &ws.fingerprint())]);
    assert!(trust::is_trusted(&ws, Some(&trusted), "ws1"), "trusted when the fingerprint matches");
    assert!(!trust::is_trusted(&workspace_config("echo ws-changed"), Some(&trusted), "ws1"), "a changed command is not trusted");
    assert!(!trust::is_trusted(&ws, Some(&trusted), "ws2"), "a different workspace id is not trusted");
    let workspace_id = mighty_core::wire::new_id();
    let mut snapshot = AppSnapshot::default();
    assert_eq!(
        trust::resolve(&StatusLineDiscovery::new(Some(ws.clone()), None), &snapshot, &workspace_id),
        (None, Some(ws.clone())),
        "a question, not a run"
    );
    assert_eq!(trust::resolve(&StatusLineDiscovery::new(Some(user.clone()), None), &snapshot, &workspace_id), (Some(user.clone()), None));
    trust::trust(&mut snapshot, &ws, &workspace_id);
    assert_eq!(
        snapshot.trusted_status_lines.as_ref().unwrap()[&workspace_id].as_deref(),
        Some(ws.fingerprint().as_str()),
        "허용 stores the fingerprint"
    );
    assert_eq!(trust::resolve(&StatusLineDiscovery::new(Some(ws.clone()), None), &snapshot, &workspace_id), (Some(ws.clone()), None));
    let ws2 = workspace_config("echo ws-changed");
    assert_eq!(
        trust::resolve(&StatusLineDiscovery::new(Some(ws2.clone()), None), &snapshot, &workspace_id),
        (None, Some(ws2)),
        "a changed command asks again"
    );
    let saved: Value = serde_json::from_str(&snapshot.to_json()).unwrap();
    let restored = mighty_core::normalize::normalize(&AppSnapshot::read(&saved).unwrap(), true).unwrap();
    assert!(
        restored.version == 1 && restored.trusted_status_lines.unwrap()[&workspace_id].as_deref() == Some(ws.fingerprint().as_str()),
        "they survive save and restore at version 1"
    );
}

/// "status line falls back to the user command while the workspace command is gated" (StatusLineVerification.cs:492).
#[test]
fn status_line_falls_back_to_the_user_command_while_the_workspace_command_is_gated() {
    let ws = workspace_config("echo ws");
    let user = user_config("echo user");
    let workspace_id = mighty_core::wire::new_id();
    let mut snapshot = AppSnapshot::default();
    let discovery = StatusLineDiscovery::new(Some(ws.clone()), Some(user.clone()));
    assert_eq!(discovery.preferred(), Some(&ws), "the workspace level still wins precedence");
    assert_eq!(
        trust::resolve(&discovery, &snapshot, &workspace_id),
        (Some(user.clone()), Some(ws.clone())),
        "falls back to the user command"
    );
    trust::trust(&mut snapshot, &ws, &workspace_id);
    assert_eq!(trust::resolve(&discovery, &snapshot, &workspace_id), (Some(ws.clone()), None));
    assert_eq!(trust::resolve(&StatusLineDiscovery::new(Some(ws.clone()), None), &AppSnapshot::default(), &workspace_id), (None, Some(ws)));
    assert_eq!(
        trust::resolve(&StatusLineDiscovery::new(None, Some(user.clone())), &AppSnapshot::default(), &workspace_id),
        (Some(user), None)
    );
}

// ── the runner ────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn sid_context(id: &str, cwd: Option<&str>) -> StatusLineContext {
    StatusLineContext { session_id: id.into(), cwd: cwd.map(str::to_string), thinking_enabled: Some(false), ..StatusLineContext::default() }
}

/// "status line runner feeds stdin captures output and colour" (StatusLineVerification.cs:533): the payload reaches the command's
/// stdin and its coloured output comes back as rows, through the fake ProcessHost and, on Windows, the stand-in through Windows' own.
#[tokio::test]
async fn status_line_runner_feeds_stdin_captures_output_and_colour() {
    let processes = FakeProcessHost::default();
    processes.push(FakeScript {
        stdout: b"\x1b[36msid\x1b[0m\r\nsecond\r\n".to_vec(),
        stderr: b"private".to_vec(),
        exit_code: 0,
        hang: false,
    });
    let cwd = common::temp("runner");
    let ctx = sid_context("sid", Some(&cwd.to_string_lossy()));
    let binary = cwd.join("sh.exe").to_string_lossy().into_owned();
    let result =
        run::run_command(&processes, &binary, &["-c".into(), "x".into()], &cwd, &ctx, run::DEFAULT_TIMEOUT, Cancel::never()).await.unwrap();
    assert!(!result.timed_out && result.exit_code == 0 && result.error_text.is_none());
    assert_eq!(result.lines.len(), 2);
    assert!(result.lines[0][0].text == "sid" && result.lines[0][0].foreground == AnsiColor::CYAN && result.lines[1][0].text == "second");
    let input: Value = serde_json::from_slice(&processes.input(0)).unwrap();
    assert_eq!(input["session_id"], "sid", "the payload is fed on stdin");
    let request = processes.spawned()[0].clone();
    assert_eq!(request.env.get("CLAUDE_CODE_STATUSLINE_HOST").map(String::as_str), Some("mightyclaude"));
    assert!(request.cwd == cwd && processes.closed(0), "the command runs in the pane's folder and is closed");
    #[cfg(windows)]
    {
        let host = mighty_platform_windows::WindowsProcessHost::new();
        let fake = common::fake_cli().to_string_lossy().into_owned();
        let real =
            run::run_command(&host, &fake, &["--status-echo".into()], &cwd, &ctx, run::DEFAULT_TIMEOUT, Cancel::never()).await.unwrap();
        assert!(!real.timed_out && real.exit_code == 0, "a short command must not time out: {real:?}");
        let first = &real.lines[0];
        assert!(
            first[0].foreground == AnsiColor::CYAN && first[0].text.starts_with("{\"hook_event_name\":\"Status\""),
            "the payload came back coloured: {first:?}"
        );
    }
    common::remove(&cwd);
}

/// "status line runner enforces timeout" (StatusLineVerification.cs:554).
#[tokio::test]
#[allow(
    clippy::await_holding_lock,
    reason = "the language lock keeps the fingerprint check's switch to English out of this run's words; no other task of this runtime takes it"
)]
async fn status_line_runner_enforces_timeout() {
    let _language = language();
    common::korean();
    let processes = FakeProcessHost::default();
    processes.push(FakeScript { hang: true, ..FakeScript::default() });
    let cwd = common::temp("timeout");
    let binary = cwd.join("sh.exe").to_string_lossy().into_owned();
    let started = Instant::now();
    let result = run::run_command(&processes, &binary, &[], &cwd, &sid_context("sid2", None), Duration::from_millis(400), Cancel::never())
        .await
        .unwrap();
    assert!(result.timed_out && result.exit_code == -1 && result.error_text == Some(strings::error_timeout()));
    assert!(started.elapsed() < Duration::from_secs(4) && processes.closed(0), "killed and closed well before the command would end");
    #[cfg(windows)]
    {
        let host = mighty_platform_windows::WindowsProcessHost::new();
        let fake = common::fake_cli().to_string_lossy().into_owned();
        let started = Instant::now();
        let real = run::run_command(
            &host,
            &fake,
            &["--sleep".into(), "10".into()],
            &cwd,
            &sid_context("sid2", None),
            Duration::from_millis(400),
            Cancel::never(),
        )
        .await
        .unwrap();
        assert!(real.timed_out && real.error_text == Some(strings::error_timeout()) && started.elapsed() < Duration::from_secs(4));
    }
    // A command that exits with an error says its code; one that cannot start says why.
    processes.push(FakeScript { exit_code: 3, ..FakeScript::default() });
    let failed =
        run::run_command(&processes, &binary, &[], &cwd, &sid_context("s", None), run::DEFAULT_TIMEOUT, Cancel::never()).await.unwrap();
    assert_eq!(failed.error_text, Some(strings::error_exit_template().replace("{status}", "3")));
    let refused = run::run_command(&processes, "relative.exe", &[], &cwd, &sid_context("s", None), run::DEFAULT_TIMEOUT, Cancel::never())
        .await
        .unwrap();
    assert!(
        refused.exit_code == -1
            && refused.lines.is_empty()
            && refused
                .error_text
                .as_deref()
                .is_some_and(|e| e.starts_with(strings::error_start_template().split("{error}").next().unwrap()))
    );
    common::remove(&cwd);
}

/// "status line shell follows Claude Code on Windows" (StatusLineVerification.cs:840).
#[test]
fn status_line_shell_follows_claude_code_on_windows() {
    let env = |name: &str| match name {
        "ProgramFiles" => Some("C:\\Program Files".to_string()),
        "LocalAppData" => Some("C:\\Users\\me\\AppData\\Local".to_string()),
        "SystemRoot" => Some("C:\\Windows".to_string()),
        _ => None,
    };
    let git_bash = "C:\\Program Files\\Git\\bin\\bash.exe";
    let with_git = run::shell("~/.claude/statusline.sh", true, &|path| path == git_bash, &env);
    assert_eq!(
        with_git,
        (git_bash.to_string(), vec!["-c".to_string(), "~/.claude/statusline.sh".to_string()]),
        "Git Bash when it is there"
    );
    let without = run::shell("node status.mjs", true, &|_| false, &env);
    assert_eq!(
        without,
        (
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe".to_string(),
            ["-NoProfile", "-NonInteractive", "-Command", "node status.mjs"].map(String::from).to_vec()
        ),
        "PowerShell without Git Bash"
    );
    let named = run::shell("x", true, &|path| path == "D:\\tools\\bash.exe" || path == git_bash, &|name| {
        if name == "CLAUDE_CODE_GIT_BASH_PATH" { Some("D:\\tools\\bash.exe".to_string()) } else { env(name) }
    });
    assert_eq!(named.0, "D:\\tools\\bash.exe", "CLAUDE_CODE_GIT_BASH_PATH first");
    let shell_for = |variable: &str, exists: &dyn Fn(&str) -> bool| {
        let variable = variable.to_string();
        run::shell("x", true, exists, &move |name: &str| {
            if name == "CLAUDE_CODE_GIT_BASH_PATH" { Some(variable.clone()) } else { env(name) }
        })
        .0
    };
    let launcher = "D:\\Git\\git-bash.exe";
    assert_eq!(shell_for(launcher, &|path| path == launcher || path == git_bash), git_bash, "a window launcher is passed over");
    assert!(shell_for(launcher, &|path| path == launcher || path.starts_with("D:\\Git\\")).ends_with("powershell.exe"));
    for other in ["D:\\Git\\git-cmd.exe", "D:/Git/GIT-BASH.EXE", "D:\\tools\\bash.exe ", "D:\\Git\\usr\\bin\\mintty.exe", "git-bash.exe"] {
        assert!(shell_for(other, &|path| path == other).ends_with("powershell.exe"), "only bash or sh is taken from the variable: {other}");
    }
    for shell in ["D:\\tools\\sh.exe", "D:/tools/BASH.EXE", "D:\\msys\\usr\\bin\\bash"] {
        assert_eq!(shell_for(shell, &|path| path == shell), shell, "bash and sh however spelled: {shell}");
    }
    assert_eq!(
        run::shell("x", false, &|_| false, &|_| None),
        ("/bin/sh".to_string(), vec!["-c".to_string(), "x".to_string()]),
        "/bin/sh off Windows"
    );
}

// ── the refresher ─────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C#'s `FakeClock`: time moves when the check says, and every delay waits until the check fires it.
#[derive(Default)]
struct FakeClock {
    base: std::sync::OnceLock<Instant>,
    offset: Mutex<Duration>,
    delays: Mutex<Vec<Arc<Notify>>>,
}

impl FakeClock {
    fn advance(&self, amount: Duration) {
        *self.offset.lock().unwrap() += amount;
    }
    fn complete_all(&self) {
        for delay in self.delays.lock().unwrap().drain(..) {
            delay.notify_one();
        }
    }
    fn pending(&self) -> usize {
        self.delays.lock().unwrap().len()
    }
}

impl StatusLineClock for FakeClock {
    fn now(&self) -> Instant {
        *self.base.get_or_init(Instant::now) + *self.offset.lock().unwrap()
    }
    fn delay(&self, _: Duration) -> BoxFuture<'static, ()> {
        let notify = Arc::new(Notify::new());
        self.delays.lock().unwrap().push(notify.clone());
        Box::pin(async move { notify.notified().await })
    }
}

fn fresh_context() -> StatusLineContext {
    StatusLineContext {
        session_id: "test".into(),
        cwd: Some("/test".into()),
        project_dir: Some("/test".into()),
        model_id: Some("claude".into()),
        model_name: Some("Claude".into()),
        version: Some(String::new()),
        ..StatusLineContext::default()
    }
}

fn empty_result() -> StatusLineResult {
    StatusLineResult { lines: vec![], error_text: None, exit_code: 0, timed_out: false }
}

/// A refresher over a fixed discovery and the trusted map the check holds.
fn refresher(
    discovery: StatusLineDiscovery,
    trusted: Arc<Mutex<AppSnapshot>>,
    workspace: &str,
    runner: Runner,
    clock: Option<Arc<dyn StatusLineClock>>,
) -> Arc<StatusLineRefresher> {
    StatusLineRefresher::new(
        Arc::new(move || discovery.clone()),
        Arc::new(move || trusted.lock().unwrap().trusted_status_lines.clone()),
        workspace,
        runner,
        clock,
        tokio::runtime::Handle::current(),
    )
}

fn counting_runner(count: Arc<AtomicUsize>) -> Runner {
    Arc::new(move |_, _, _| {
        count.fetch_add(1, Ordering::SeqCst);
        Box::pin(async { Ok(empty_result()) })
    })
}

/// "status line refresher debounces single run from rapid triggers" (StatusLineVerification.cs:615).
#[tokio::test]
async fn status_line_refresher_debounces_single_run_from_rapid_triggers() {
    let clock = Arc::new(FakeClock::default());
    let count = Arc::new(AtomicUsize::new(0));
    let r = refresher(
        StatusLineDiscovery::new(None, Some(user_config("echo user"))),
        Arc::default(),
        "w",
        counting_runner(count.clone()),
        Some(clock.clone()),
    );
    r.request_refresh(fresh_context(), false);
    common::until(|| r.result().is_some(), 5000, "the first run ran at once").await;
    assert_eq!(count.load(Ordering::SeqCst), 1);
    r.request_refresh(fresh_context(), false);
    r.request_refresh(fresh_context(), false);
    common::until(|| clock.pending() == 1, 5000, "one debounce waits").await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert_eq!(count.load(Ordering::SeqCst), 1, "the requests within the interval collapse onto the waiting one");
    clock.advance(refresher::MINIMUM_INTERVAL);
    clock.complete_all();
    common::until(|| count.load(Ordering::SeqCst) == 2, 5000, "the debounce fires the second run").await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert_eq!(count.load(Ordering::SeqCst), 2, "exactly two runs despite three rapid requests");
}

/// "status line refresher generation counter discards stale results" (StatusLineVerification.cs:648).
#[tokio::test]
async fn status_line_refresher_generation_counter_discards_stale_results() {
    let gate = Arc::new(Notify::new());
    let changes = Arc::new(AtomicUsize::new(0));
    let opened = gate.clone();
    let runner: Runner = Arc::new(move |_, _, _| {
        let opened = opened.clone();
        Box::pin(async move {
            opened.notified().await;
            Ok(empty_result())
        })
    });
    let r = refresher(StatusLineDiscovery::new(None, Some(user_config("echo user"))), Arc::default(), "w", runner, None);
    let seen = changes.clone();
    r.on_change(Arc::new(move || {
        seen.fetch_add(1, Ordering::SeqCst);
    }));
    r.request_refresh(fresh_context(), false);
    tokio::time::sleep(Duration::from_millis(40)).await;
    r.close();
    gate.notify_one();
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert_eq!(changes.load(Ordering::SeqCst), 0, "a stale run after Close must not say it changed");
    assert!(r.result().is_none(), "the result stays empty after the generation moved on");
}

/// "status line refresher reruns when pending during a run" (StatusLineVerification.cs:672).
#[tokio::test]
async fn status_line_refresher_reruns_when_pending_during_a_run() {
    let runs = Arc::new(AtomicUsize::new(0));
    let (first, second) = (Arc::new(Notify::new()), Arc::new(Notify::new()));
    let (count, gates) = (runs.clone(), (first.clone(), second.clone()));
    let runner: Runner = Arc::new(move |_, _, _| {
        let n = count.fetch_add(1, Ordering::SeqCst) + 1;
        let gate = if n == 1 { gates.0.clone() } else { gates.1.clone() };
        Box::pin(async move {
            gate.notified().await;
            Ok(empty_result())
        })
    });
    let r = refresher(StatusLineDiscovery::new(None, Some(user_config("echo user"))), Arc::default(), "w", runner, None);
    r.request_refresh(fresh_context(), false);
    common::until(|| runs.load(Ordering::SeqCst) == 1, 5000, "the first run started").await;
    r.request_refresh(fresh_context(), false);
    first.notify_one();
    common::until(|| runs.load(Ordering::SeqCst) == 2, 5000, "the re-run started because one was pending").await;
    second.notify_one();
    common::until(|| r.result().is_some() && r.config().is_some(), 5000, "the second run completed").await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert_eq!(runs.load(Ordering::SeqCst), 2, "the original and one re-run for the pending request");
}

/// "status line refresher ignores requests after close" (StatusLineVerification.cs:704).
#[tokio::test]
async fn status_line_refresher_ignores_requests_after_close() {
    let count = Arc::new(AtomicUsize::new(0));
    let r = refresher(
        StatusLineDiscovery::new(None, Some(user_config("echo user"))),
        Arc::default(),
        "w",
        counting_runner(count.clone()),
        None,
    );
    r.close();
    r.request_refresh(fresh_context(), false);
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert_eq!(count.load(Ordering::SeqCst), 0, "no run started after Close");
    assert!(r.is_stopped(), "a refresher with nothing running is stopped at once");
}

/// "status line cancellation drains before replacement" (StatusLineShutdownVerification.cs:8).
#[tokio::test]
async fn status_line_cancellation_drains_before_replacement() {
    let cancellation_seen = Arc::new(Notify::new());
    let (release_sender, release) = watch::channel(false);
    let runs = Arc::new(AtomicUsize::new(0));
    let concurrent = Arc::new(AtomicUsize::new(0));
    let maximum = Arc::new(AtomicUsize::new(0));
    let user = user_config("fixture");
    let old_runner: Runner = {
        let (seen, runs, concurrent, maximum) = (cancellation_seen.clone(), runs.clone(), concurrent.clone(), maximum.clone());
        Arc::new(move |_, _, mut cancel: Cancel| {
            let (seen, concurrent, maximum, mut release) = (seen.clone(), concurrent.clone(), maximum.clone(), release.clone());
            runs.fetch_add(1, Ordering::SeqCst);
            let now = concurrent.fetch_add(1, Ordering::SeqCst) + 1;
            maximum.fetch_max(now, Ordering::SeqCst);
            Box::pin(async move {
                cancel.cancelled().await;
                seen.notify_one();
                let _ = release.wait_for(|go| *go).await;
                concurrent.fetch_sub(1, Ordering::SeqCst);
                Err::<StatusLineResult, Cancelled>(Cancelled)
            })
        })
    };
    let old = refresher(StatusLineDiscovery::new(None, Some(user.clone())), Arc::default(), "workspace", old_runner, None);
    old.request_refresh(fresh_context(), false);
    common::until(|| runs.load(Ordering::SeqCst) == 1, 5000, "the old command started").await;
    old.close();
    tokio::time::timeout(Duration::from_secs(2), cancellation_seen.notified()).await.expect("the runner saw the cancellation");
    assert!(!old.is_stopped(), "Close must wait for the runner's cleanup, not only signal cancellation");
    let replacement_runner: Runner = {
        let (runs, concurrent, maximum) = (runs.clone(), concurrent.clone(), maximum.clone());
        Arc::new(move |_, _, _| {
            runs.fetch_add(1, Ordering::SeqCst);
            let now = concurrent.fetch_add(1, Ordering::SeqCst) + 1;
            maximum.fetch_max(now, Ordering::SeqCst);
            concurrent.fetch_sub(1, Ordering::SeqCst);
            Box::pin(async { Ok(empty_result()) })
        })
    };
    let replacement = refresher(StatusLineDiscovery::new(None, Some(user)), Arc::default(), "workspace", replacement_runner, None);
    let waiting = {
        let (old, replacement) = (old.clone(), replacement.clone());
        tokio::spawn(async move {
            old.when_stopped().await;
            replacement.request_refresh(fresh_context(), false);
        })
    };
    old.request_refresh(fresh_context(), true);
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert_eq!(runs.load(Ordering::SeqCst), 1, "the closed refresher and its replacement cannot overlap the command awaiting cleanup");
    release_sender.send(true).unwrap();
    tokio::time::timeout(Duration::from_secs(2), waiting).await.unwrap().unwrap();
    common::until(|| replacement.result().is_some(), 5000, "the replacement ran").await;
    assert!(
        runs.load(Ordering::SeqCst) == 2 && maximum.load(Ordering::SeqCst) == 1 && old.result().is_none(),
        "only the replacement publishes"
    );
    replacement.close();
    tokio::time::timeout(Duration::from_secs(2), replacement.when_stopped()).await.unwrap();
}

/// "status line real process cancellation drains stderr" (StatusLineShutdownVerification.cs:33): a cancelled command ends at once
/// (the fake everywhere, the stand-in through Windows' own ProcessHost on Windows), and stderr past the pipe's capacity drains.
#[tokio::test]
async fn status_line_real_process_cancellation_drains_stderr() {
    let cwd = common::temp("cancel");
    let processes = FakeProcessHost::default();
    processes.push(FakeScript { hang: true, ..FakeScript::default() });
    let (cancel, fired) = watch::channel(false);
    let binary = cwd.join("sh.exe").to_string_lossy().into_owned();
    let run = {
        let ctx = sid_context("shutdown-fixture", Some(&cwd.to_string_lossy()));
        let binary = binary.clone();
        let cwd = cwd.clone();
        async move {
            run::run_command(&processes, &binary, &[], &cwd, &ctx, Duration::from_secs(30), Cancel::new(fired))
                .await
                .map(|_| processes.closed(0))
        }
    };
    let canceller = tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(100)).await;
        cancel.send(true).unwrap();
    });
    let started = Instant::now();
    assert_eq!(tokio::time::timeout(Duration::from_secs(5), run).await.unwrap(), Err(Cancelled), "the cancellation is not ignored");
    assert!(started.elapsed() < Duration::from_secs(5));
    canceller.await.unwrap();
    #[cfg(windows)]
    {
        let host = mighty_platform_windows::WindowsProcessHost::new();
        let fake = common::fake_cli().to_string_lossy().into_owned();
        let ctx = sid_context("shutdown-fixture", Some(&cwd.to_string_lossy()));
        let (cancel, fired) = watch::channel(false);
        let canceller = tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(100)).await;
            cancel.send(true).unwrap();
        });
        let started = Instant::now();
        let outcome =
            run::run_command(&host, &fake, &["--sleep".into(), "60".into()], &cwd, &ctx, Duration::from_secs(30), Cancel::new(fired)).await;
        assert!(outcome == Err(Cancelled) && started.elapsed() < Duration::from_secs(5), "closing ends the owned process tree promptly");
        canceller.await.unwrap();
        let noisy = run::run_command(
            &host,
            &fake,
            &["--noisy-stderr".into(), "12000".into()],
            &cwd,
            &ctx,
            Duration::from_secs(10),
            Cancel::never(),
        )
        .await
        .unwrap();
        assert!(!noisy.timed_out && noisy.exit_code == 0, "stderr drains past the pipe's capacity: {noisy:?}");
        assert_eq!(noisy.lines[0][0].text, "done");
    }
    common::remove(&cwd);
}

/// "status line refresher never starts two commands at once" (StatusLineVerification.cs:722).
#[tokio::test]
async fn status_line_refresher_never_starts_two_commands_at_once() {
    let gate = Arc::new(Notify::new());
    let concurrent = Arc::new(AtomicUsize::new(0));
    let ever_two = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let runner: Runner = {
        let (gate, concurrent, ever_two) = (gate.clone(), concurrent.clone(), ever_two.clone());
        Arc::new(move |_, _, _| {
            if concurrent.fetch_add(1, Ordering::SeqCst) + 1 > 1 {
                ever_two.store(true, Ordering::SeqCst);
            }
            let (gate, concurrent) = (gate.clone(), concurrent.clone());
            Box::pin(async move {
                gate.notified().await;
                concurrent.fetch_sub(1, Ordering::SeqCst);
                Ok(empty_result())
            })
        })
    };
    let r = refresher(StatusLineDiscovery::new(None, Some(user_config("echo user"))), Arc::default(), "w", runner, None);
    for _ in 0..3 {
        r.request_refresh(fresh_context(), false);
    }
    common::until(|| concurrent.load(Ordering::SeqCst) == 1, 5000, "one command runs").await;
    gate.notify_one();
    common::until(|| r.result().is_some(), 5000, "the run completed").await;
    // The pending request runs after, alone.
    common::until(|| concurrent.load(Ordering::SeqCst) == 1, 5000, "the pending request ran after").await;
    gate.notify_one();
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(!ever_two.load(Ordering::SeqCst), "never more than one concurrent run per pane");
}

fn recording_runner(last: Arc<Mutex<Option<StatusLineConfig>>>, count: Arc<AtomicUsize>) -> Runner {
    Arc::new(move |config, _, _| {
        *last.lock().unwrap() = Some(config);
        count.fetch_add(1, Ordering::SeqCst);
        Box::pin(async { Ok(empty_result()) })
    })
}

/// "status line refresher falls back to user command while workspace command is gated" (StatusLineVerification.cs:752).
#[tokio::test]
async fn status_line_refresher_falls_back_to_user_command_while_workspace_command_is_gated() {
    let last = Arc::new(Mutex::new(None));
    let r = refresher(
        StatusLineDiscovery::new(Some(workspace_config("echo ws")), Some(user_config("echo user"))),
        Arc::default(),
        &mighty_core::wire::new_id(),
        recording_runner(last.clone(), Arc::default()),
        None,
    );
    r.request_refresh(fresh_context(), false);
    common::until(|| r.result().is_some(), 5000, "the run completed").await;
    assert_eq!(last.lock().unwrap().as_ref().map(|c| c.command.clone()).as_deref(), Some("echo user"), "the user command ran");
    assert_eq!(r.untrusted().map(|c| c.command).as_deref(), Some("echo ws"), "the workspace command is asked about");
    assert_eq!(r.config().map(|c| c.command).as_deref(), Some("echo user"), "Config is what ran");
}

/// "status line refresher trust unblocks the workspace command" (StatusLineVerification.cs:776).
#[tokio::test]
async fn status_line_refresher_trust_unblocks_the_workspace_command() {
    let ws = workspace_config("echo ws");
    let workspace_id = mighty_core::wire::new_id();
    let snapshot = Arc::new(Mutex::new(AppSnapshot::default()));
    let (last, count) = (Arc::new(Mutex::new(None)), Arc::new(AtomicUsize::new(0)));
    let r = refresher(
        StatusLineDiscovery::new(Some(ws.clone()), Some(user_config("echo user"))),
        snapshot.clone(),
        &workspace_id,
        recording_runner(last.clone(), count.clone()),
        None,
    );
    r.request_refresh(fresh_context(), false);
    common::until(|| count.load(Ordering::SeqCst) == 1 && r.result().is_some(), 5000, "the first run completed").await;
    assert_eq!(last.lock().unwrap().as_ref().map(|c| c.command.clone()).as_deref(), Some("echo user"), "the user command ran before trust");
    trust::trust(&mut snapshot.lock().unwrap(), &ws, &workspace_id);
    r.request_refresh(fresh_context(), true);
    common::until(|| count.load(Ordering::SeqCst) == 2, 5000, "the second run after trust").await;
    common::until(|| r.untrusted().is_none(), 5000, "the question cleared").await;
    assert_eq!(
        last.lock().unwrap().as_ref().map(|c| c.command.clone()).as_deref(),
        Some("echo ws"),
        "the workspace command ran after trust"
    );
}

/// "status line refresher shows nothing for a disabled or missing entry" (StatusLineVerification.cs:807).
#[tokio::test]
async fn status_line_refresher_shows_nothing_for_a_disabled_or_missing_entry() {
    for discovery in [
        StatusLineDiscovery { workspace: None, user: Some(user_config("echo user")), workspace_disabled: true },
        StatusLineDiscovery::new(None, None),
    ] {
        let count = Arc::new(AtomicUsize::new(0));
        let r = refresher(discovery, Arc::default(), "w", counting_runner(count.clone()), None);
        r.request_refresh(fresh_context(), false);
        tokio::time::sleep(Duration::from_millis(60)).await;
        assert_eq!(count.load(Ordering::SeqCst), 0, "nothing runs");
        assert!(r.config().is_none() && r.untrusted().is_none() && r.result().is_none(), "nothing shows");
    }
}

/// "composer parity: the status line asks again on a tick" (ComposerParityVerification.cs:380).
#[test]
fn composer_parity_the_status_line_asks_again_on_a_tick() {
    assert_eq!(refresher::tick_interval(true), Duration::from_secs(10), "every 10 s while the pane's run goes on");
    assert_eq!(refresher::tick_interval(false), Duration::from_secs(60), "every 60 s at rest");
    assert!(refresher::MINIMUM_INTERVAL < refresher::tick_interval(true), "a tick is never swallowed by the least interval");
}

// ── the card's input ──────────────────────────────────────────────────────────────────────────────────────────────────────

/// What the card reads (MainWindow.StatusLine.cs `Terminal`, MainWindow.WebShell.Cards.cs `WebSegment`): the terminal colours held
/// to design-tokens.json's `windowsOnly.ansi`, bright black the secondary ink, the source's label.
#[test]
fn the_status_line_card_reads_the_terminal_colours() {
    let _language = language();
    common::korean();
    let tokens: Value = serde_json::from_str(&common::fixture("design-tokens.json")).unwrap();
    let ansi_tokens = &tokens["windowsOnly"]["ansi"];
    for index in 0..16 {
        let expected = ansi_tokens["standard"].get((index % 8).to_string()).and_then(Value::as_str).map(str::to_lowercase);
        let found = card::ansi_standard(index).map(|(r, g, b)| format!("#{r:02x}{g:02x}{b:02x}"));
        assert_eq!(found, expected, "standard colour {index}");
    }
    assert!((ansi_tokens["dimOpacity"].as_f64().unwrap() - card::ANSI_DIM_OPACITY).abs() < 1e-12);
    let segments = ansi::parse("\x1b[31mred\x1b[90mgrey\x1b[38;5;21mblue\x1b[38;2;1;2;3mrgb\x1b[0;2mdim");
    let runs: Vec<_> = segments.iter().map(card::run_of).collect();
    assert_eq!(runs[0].color.as_deref(), Some("#db4c4c"));
    assert!(runs[1].color.is_none() && runs[1].bright_black, "bright black takes the secondary ink");
    assert_eq!(runs[2].color.as_deref(), Some("#0000ff"));
    assert_eq!(runs[3].color.as_deref(), Some("#010203"));
    assert!(runs[4].color.is_none() && !runs[4].bright_black && runs[4].dim);
    let config = user_config("echo hi");
    let untrusted = workspace_config("echo ws");
    let result = StatusLineResult { lines: vec![segments.clone()], error_text: Some("x".into()), exit_code: 0, timed_out: false };
    let input = card::input(Some(&config), Some(&untrusted), Some(&result));
    assert_eq!(input.config, Some((strings::source_label(SOURCE_USER), "echo hi".to_string())));
    assert_eq!(input.untrusted, Some((strings::source_label(SOURCE_WORKSPACE), "echo ws".to_string())));
    assert!(input.lines.as_ref().is_some_and(|lines| lines.len() == 1 && lines[0].len() == 5) && input.error_text.as_deref() == Some("x"));
    let card = mighty_composer::cards::status_line_card(&input, &mighty_core::locale::Locale::new("ko")).expect("a card");
    assert_eq!(card.tip, format!("statusLine · {} · echo hi", strings::source_label(SOURCE_USER)));
    let empty = card::input(None, None, None);
    assert!(mighty_composer::cards::status_line_card(&empty, &mighty_core::locale::Locale::new("ko")).is_none(), "nothing to show");
}

/// "model labels: the status line names the current selection" (ModelLabelVerification.cs:141, `ModelLabel.StatusLine`).
#[test]
fn model_labels_the_status_line_names_the_current_selection() {
    let _language = language();
    common::korean();
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
    let catalog = mighty_run::providers::claude_catalog(&rows);
    let usage = |model: &str, selected: Option<&str>| {
        let mut value = mighty_core::model::SessionUsage::from_json(
            &serde_json::json!({"provider": "claude", "source": "claude.stream-json", "tokenScope": "run", "updatedAt": "2026-10-03T00:00:00Z"}),
        )
        .unwrap();
        value.model = Some(model.to_string());
        value.selected_model = selected.map(str::to_string);
        value
    };
    let pane = |model: &str, usage: Option<mighty_core::model::SessionUsage>| mighty_core::model::RunSession {
        workspace_id: Some("w".into()),
        title: Some("t".into()),
        model: Some(model.into()),
        provider: Some("claude".into()),
        session_usage: usage,
        ..mighty_core::model::RunSession::default()
    };
    let locale = mighty_core::locale::Locale::new("ko");
    let current = pane("opus", Some(usage("claude-opus-5-5", Some("opus"))));
    assert_eq!(card::model_label(&current, &catalog, &locale), ("claude-opus-5-5".into(), "Opus 5.5".into()), "the reported selection");
    let switched = mighty_core::model::RunSession { model: Some("haiku".into()), ..current.clone() };
    assert_eq!(card::model_label(&switched, &catalog, &locale), ("claude-haiku-4-5-20251001".into(), "Haiku 4.5".into()), "after a switch");
    let legacy = pane("sonnet", Some(usage("claude-sonnet-4-5", None)));
    assert_eq!(card::model_label(&legacy, &catalog, &locale), ("claude-sonnet-4-5".into(), "Sonnet 4.5".into()), "legacy usage");
    let fallback = mighty_run::providers::fallback("claude");
    assert_eq!(
        card::model_label(&pane("default", None), &fallback, &locale),
        ("default".into(), "Claude 설정 따름".into()),
        "the fallback default"
    );
}
