//! The checks' stand-in agent CLI: what C# relaunches its test exe as (MightyCode.Core.Tests/Verification.cs:49-217
//! `FakeCliAsync`, ModelListProbeVerification.cs:151-187). None of these is a real CLI. std and serde_json only.
//!
//! - `--fake-cli <provider> <record> [args…]`: records its arguments in `<record>.args` (a JSON array) and plays C#'s fake: with
//!   `--permission-prompt-tool` Claude's stdio control protocol (the handshake, one tool request, the plan, resumed-agent and
//!   background fixtures); with `app-server` or `--safe-mode` the model-list sessions; otherwise reads stdin to its end into
//!   `<record>.prompt` and answers as Codex's exec (or agy) would.
//! - `--model-list-cli <provider> <state> [args…]`: each CLI's model-list read, answered with the real CLI's recorded output
//!   (native/contracts/fixtures/model-list-*) for its verified arguments, refused otherwise.
//! - `--replay-cli <recording> <record> [args…]`: Claude with host prompts that asks one tool permission and one plan, records the
//!   answers, then replays the recording (native/contracts/fixtures/<recording>.jsonl) and ends once its input closes.

use std::fs::OpenOptions;
use std::io::{BufRead, Read, Write};
use std::path::PathBuf;
use std::time::Duration;

use serde_json::{Value, json};

fn fixtures() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..").join("..").join("..").join("contracts").join("fixtures")
}

fn fixture(name: &str) -> String {
    std::fs::read_to_string(fixtures().join(name)).unwrap_or_default()
}

fn say(line: &str) {
    let mut out = std::io::stdout().lock();
    let _ = writeln!(out, "{line}");
    let _ = out.flush();
}

fn append(path: &str, text: &str) {
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let _ = file.write_all(text.as_bytes());
    }
}

fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(Value::as_str)
}

fn read_line() -> Option<String> {
    let mut line = String::new();
    match std::io::stdin().lock().read_line(&mut line) {
        Ok(0) | Err(_) => None,
        Ok(_) => Some(line.trim_end_matches(['\r', '\n']).to_string()),
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("--fake-cli") if args.len() >= 3 => fake_cli(&args[1], &args[2], &args[3..]),
        Some("--model-list-cli") if args.len() >= 3 => model_list_cli(&args[1], &args[2], &args[3..]),
        Some("--replay-cli") if args.len() >= 3 => replay_cli(&args[1], &args[2], &args[3..]),
        // C# `--long-child <pid file>`: a shell pane's command that writes its id and runs until it is stopped.
        Some("--long-child") if args.len() >= 2 => {
            let _ = std::fs::write(&args[1], std::process::id().to_string());
            std::thread::sleep(Duration::from_secs(60));
        }
        _ => {
            eprintln!("mighty-fake-cli: unknown mode {args:?}");
            std::process::exit(64);
        }
    }
}

fn fake_cli(provider: &str, record: &str, args: &[String]) {
    let _ = std::fs::write(format!("{record}.args"), serde_json::to_string(args).unwrap_or_default());
    let has = |flag: &str| args.iter().any(|a| a == flag);
    if has("--version") {
        say("2.1.271");
        return;
    }
    if has("--permission-prompt-tool") {
        claude_with_prompts(record);
        return;
    }
    if has("app-server") || has("--safe-mode") {
        while let Some(line) = read_line() {
            append(&format!("{record}.input"), &format!("{line}\n"));
            let Ok(root) = serde_json::from_str::<Value>(&line) else { continue };
            if provider == "claude" {
                if text(&root, "type") != Some("control_request") {
                    eprintln!("Prompt forbidden");
                    std::process::exit(1);
                }
                let models = json!([{"value": "sonnet", "displayName": "Sonnet", "description": "Fixture", "supportsEffort": true, "supportedEffortLevels": ["low", "high"]}]);
                say(&json!({"type": "control_response", "response": {"subtype": "success", "request_id": text(&root, "request_id"), "response": {"models": models}}}).to_string());
            } else if text(&root, "method") == Some("initialize") {
                say("{\"id\":1,\"result\":{}}");
            } else if text(&root, "method") == Some("model/list") {
                say(
                    "{\"id\":2,\"result\":{\"data\":[{\"id\":\"gpt-6-astra\",\"model\":\"gpt-6-astra\",\"displayName\":\"Astra\",\"supportedReasoningEfforts\":[{\"reasoningEffort\":\"high\"}]}],\"nextCursor\":null}}",
                );
            } else if text(&root, "method") != Some("initialized") {
                eprintln!("Turn creation forbidden");
                std::process::exit(1);
            }
        }
        return;
    }
    let mut input = String::new();
    let _ = std::io::stdin().read_to_string(&mut input);
    let _ = std::fs::write(format!("{record}.prompt"), &input);
    if has("--verify-attachments") {
        let flag = if provider == "codex" { "--image" } else { "--add-dir" };
        let Some(index) = args.iter().position(|a| a == flag).filter(|i| i + 1 < args.len()) else {
            eprintln!("{provider} staged attachment argument missing");
            std::process::exit(3);
        };
        let directory = if provider == "codex" {
            PathBuf::from(&args[index + 1]).parent().map(PathBuf::from).unwrap_or_default()
        } else {
            PathBuf::from(&args[index + 1])
        };
        let mut captured = serde_json::Map::new();
        if let Ok(entries) = std::fs::read_dir(&directory) {
            for entry in entries.flatten() {
                let bytes = std::fs::read(entry.path()).unwrap_or_default();
                captured.insert(entry.path().to_string_lossy().into_owned(), Value::String(base64(&bytes)));
            }
        }
        let _ = std::fs::write(format!("{record}.attachments"), Value::Object(captured).to_string());
    }
    if provider == "gemini" {
        say(
            "{\"event\":\"init\",\"conversation_id\":\"11111111-2222-4333-8444-555555555555\",\"init\":{\"model\":\"gemini-3.8-flash-low\"}}",
        );
        say(
            "{\"event\":\"step_update\",\"step_update\":{\"step_index\":1,\"state\":\"DONE\",\"step_type\":\"agent_response\",\"text_delta\":\"FAKE_CLI_OK\\n\"}}",
        );
        say("{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"FAKE_CLI_OK\\n\"}}");
    } else {
        say("{\"type\":\"thread.started\",\"thread_id\":\"fixture-thread\"}");
        say("{\"type\":\"item.completed\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"FAKE_CLI_OK\"}}");
    }
    if has("--hold-run") {
        std::thread::sleep(Duration::from_secs(60));
    }
    if has("--fail-run") {
        std::process::exit(2);
    }
}

fn base64(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for chunk in bytes.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = (b[0] as u32) << 16 | (b[1] as u32) << 8 | b[2] as u32;
        out.push(ALPHABET[(n >> 18) as usize & 63] as char);
        out.push(ALPHABET[(n >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 { ALPHABET[(n >> 6) as usize & 63] as char } else { '=' });
        out.push(if chunk.len() > 2 { ALPHABET[n as usize & 63] as char } else { '=' });
    }
    out
}

/// A Claude launched with host permission prompts (Verification.cs:56-167).
fn claude_with_prompts(record: &str) {
    let mut plan_fixture = false;
    while let Some(line) = read_line() {
        append(&format!("{record}.input"), &format!("{line}\n"));
        let Ok(root) = serde_json::from_str::<Value>(&line) else { continue };
        match text(&root, "type") {
            Some("control_request") => {
                say(&json!({"type": "control_response", "response": {"subtype": "success", "request_id": text(&root, "request_id"), "response": {}}}).to_string());
            }
            Some("user") if line.contains("sign-in fixture") => {
                let words = if line.contains("expired sign-in fixture") {
                    "Failed to authenticate: OAuth session expired and could not be refreshed"
                } else {
                    "Sign-in went stale · try later"
                };
                let mark = if line.contains("unmarked sign-in fixture") { "server_error" } else { "authentication_failed" };
                say(&json!({"type": "assistant", "error": mark, "message": {"content": [{"type": "text", "text": words}]}}).to_string());
                if line.contains("child sign-in fixture") {
                    say(&json!({"type": "assistant", "parent_tool_use_id": "toolu_child", "error": "rate_limit", "message": {"content": [{"type": "text", "text": "Rate limited"}]}}).to_string());
                }
                let result = if line.contains("mismatched sign-in fixture") { "API Error: 500 Internal server error" } else { words };
                let denied = line.contains("denied sign-in fixture");
                let mut frame = json!({"type": "result", "subtype": if denied { "error_during_execution" } else { "success" }, "is_error": true, "result": result});
                if denied {
                    frame["errors"] = json!([
                        "User: arn:aws:sts::123456789012:assumed-role/fixture/session is not authorized to perform: bedrock:InvokeModel with an explicit deny in a service control policy"
                    ]);
                }
                say(&frame.to_string());
                std::process::exit(1);
            }
            Some("user") if line.contains("resumed agent fixture") => {
                for frame in [
                    "{\"type\":\"system\",\"subtype\":\"session_state_changed\",\"state\":\"running\"}",
                    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Agent\",\"input\":{\"description\":\"Count\",\"prompt\":\"Count.\",\"run_in_background\":true}}]}}",
                    "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"agent1\",\"run_id\":\"run-1\",\"tool_use_id\":\"toolu_1\",\"description\":\"Count\",\"is_backgrounded\":true,\"task_type\":\"local_agent\"}",
                    "{\"type\":\"system\",\"subtype\":\"task_updated\",\"task_id\":\"agent1\",\"run_id\":\"run-1\",\"patch\":{\"status\":\"completed\"}}",
                    "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"agent1\",\"run_id\":\"run-1\",\"tool_use_id\":\"toolu_1\",\"status\":\"completed\",\"summary\":\"one\"}",
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"launched\"}",
                ] {
                    say(frame);
                }
                std::thread::sleep(Duration::from_millis(400));
                for frame in [
                    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_2\",\"name\":\"SendMessage\",\"input\":{\"to\":\"agent1\",\"summary\":\"Count on\",\"message\":\"Count on.\"}}]}}",
                    "{\"type\":\"system\",\"subtype\":\"background_tasks_changed\",\"tasks\":[{\"task_id\":\"agent1\",\"run_id\":\"run-2\",\"task_type\":\"local_agent\",\"description\":\"Count\"}]}",
                    "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"agent1\",\"run_id\":\"run-2\",\"tool_use_id\":\"toolu_2\",\"description\":\"Count\",\"is_backgrounded\":true,\"task_type\":\"local_agent\"}",
                    "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_2\",\"content\":\"{\\\"success\\\":true,\\\"resumedAgentId\\\":\\\"agent1\\\"}\"}]}}",
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"resumed\",\"origin\":{\"kind\":\"task-notification\",\"runId\":\"run-1\"}}",
                ] {
                    say(frame);
                }
                let Some(follow) = read_line() else { std::process::exit(24) };
                let _ = std::fs::write(format!("{record}.follow"), follow);
                for frame in [
                    "{\"type\":\"system\",\"subtype\":\"task_updated\",\"task_id\":\"agent1\",\"run_id\":\"run-2\",\"patch\":{\"status\":\"completed\"}}",
                    "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"agent1\",\"run_id\":\"run-2\",\"tool_use_id\":\"toolu_2\",\"status\":\"completed\",\"summary\":\"two\"}",
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\",\"origin\":{\"kind\":\"task-notification\",\"runId\":\"run-2\"}}",
                    "{\"type\":\"system\",\"subtype\":\"session_state_changed\",\"state\":\"idle\"}",
                ] {
                    say(frame);
                }
                while let Some(extra) = read_line() {
                    append(&format!("{record}.extra"), &format!("{extra}\n"));
                }
                return;
            }
            Some("user") if line.contains("background stop fixture") => {
                let _ = std::fs::write(format!("{record}.pid"), std::process::id().to_string());
                say(
                    "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"sh1\",\"description\":\"npm run dev\",\"task_type\":\"local_bash\"}",
                );
                say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"started\"}");
                while read_line().is_some() {}
                return;
            }
            Some("user") => {
                plan_fixture = line.contains("plan fixture");
                if line.contains("style fixture") {
                    say(
                        "{\"type\":\"control_request\",\"request_id\":\"style-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"mcp__plugin_fixture__step\",\"tool_use_id\":\"tool-style-1\",\"input\":{\"step\":1}}}",
                    );
                    continue;
                }
                if plan_fixture {
                    say(
                        "{\"type\":\"control_request\",\"request_id\":\"plan-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"ExitPlanMode\",\"tool_use_id\":\"toolu_plan\",\"input\":{\"plan\":\"# Plan\\n1. Do it\",\"planFilePath\":\"/tmp/plan.md\"},\"requires_user_interaction\":true}}",
                    );
                } else {
                    say(
                        "{\"type\":\"control_request\",\"request_id\":\"ask-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Read\",\"tool_use_id\":\"tool-ask-1\",\"input\":{\"file_path\":\"~/.claude/CLAUDE.md\"},\"blocked_path\":\"~/.claude/CLAUDE.md\"}}",
                    );
                }
            }
            Some("control_response") => {
                let _ = std::fs::write(format!("{record}.decision"), &line);
                if plan_fixture {
                    let _ =
                        std::fs::write(format!("{record}.env"), std::env::var("CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS").unwrap_or_default());
                    if line.contains("\"interrupt\":true") {
                        say(
                            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"errors\":[\"[Request interrupted by user]\"]}",
                        );
                        while read_line().is_some() {}
                        return;
                    }
                    say(
                        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_todo\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[{\"content\":\"Do it\",\"status\":\"in_progress\",\"activeForm\":\"Doing it\"}]}}]}}",
                    );
                    say(
                        "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"bg1\",\"tool_use_id\":\"toolu_bg\",\"description\":\"Review\",\"is_backgrounded\":true,\"task_type\":\"local_agent\"}",
                    );
                }
                say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"fixture\",\"session_id\":\"fixture\"}");
                if !plan_fixture {
                    return;
                }
                if std::path::Path::new(&format!("{record}.quiet")).exists() {
                    say(
                        "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"bg1\",\"status\":\"completed\",\"summary\":\"quiet\"}",
                    );
                    while read_line().is_some() {}
                    return;
                }
                let Some(follow) = read_line() else { std::process::exit(24) };
                let _ = std::fs::write(format!("{record}.follow"), follow);
                say(
                    "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"bg1\",\"tool_use_id\":\"toolu_bg\",\"status\":\"completed\",\"summary\":\"All good\"}",
                );
                say(
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"noted\",\"origin\":{\"kind\":\"task-notification\"}}",
                );
                while let Some(extra) = read_line() {
                    append(&format!("{record}.extra"), &format!("{extra}\n"));
                }
                return;
            }
            _ => {}
        }
    }
}

/// The verified arguments of each CLI's model-list read (ModelListProbeVerification.Verified).
fn verified(provider: &str) -> Option<Vec<&'static str>> {
    Some(match provider {
        "claude" => vec![
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
        "codex" => vec!["app-server", "--listen", "stdio://"],
        "gemini" => vec!["models"],
        "pi" => vec!["--list-models"],
        "opencode" => vec!["models", "--standalone"],
        _ => return None,
    })
}

fn executable(provider: &str) -> &str {
    if provider == "gemini" { "agy" } else { provider }
}

fn model_list_cli(provider: &str, state: &str, args: &[String]) {
    let name = executable(provider);
    let state = PathBuf::from(state);
    let _ = std::fs::write(state.join(format!("{name}-argv.json")), serde_json::to_string(args).unwrap_or_default());
    if provider == "claude" {
        let env = json!({"CLAUDE_CODE_SAFE_MODE": std::env::var("CLAUDE_CODE_SAFE_MODE").ok(), "DISABLE_TELEMETRY": std::env::var("DISABLE_TELEMETRY").ok()});
        let _ = std::fs::write(state.join("claude-env.json"), env.to_string());
    }
    let failure = state.join(format!("{name}-fail"));
    if failure.exists() {
        eprint!("{}", std::fs::read_to_string(&failure).unwrap_or_default());
        std::process::exit(1);
    }
    let matches = verified(provider).is_some_and(|v| v.len() == args.len() && v.iter().zip(args).all(|(a, b)| *a == b));
    if !matches {
        if provider == "gemini" {
            eprint!("{}", fixture("model-list-agy-unknown-flag.txt"));
            std::process::exit(1);
        }
        eprintln!("error: unexpected arguments: {}", args.join(" "));
        std::process::exit(2);
    }
    if matches!(provider, "gemini" | "pi" | "opencode") {
        if provider == "gemini" {
            eprintln!("Fetching available models...");
        }
        let replaced = state.join(format!("{name}-output"));
        let text = if replaced.exists() {
            std::fs::read_to_string(replaced).unwrap_or_default()
        } else {
            fixture(&format!("model-list-{name}.txt"))
        };
        let mut out = std::io::stdout().lock();
        let _ = out.write_all(text.as_bytes());
        let _ = out.flush();
        return;
    }
    let lines: Vec<String> =
        fixture(&format!("model-list-{provider}.jsonl")).split('\n').filter(|l| !l.trim().is_empty()).map(str::to_string).collect();
    let with_id = |line: &str, id: &Value| {
        let mut node: Value = serde_json::from_str(line).unwrap_or(Value::Null);
        node["id"] = id.clone();
        node.to_string()
    };
    while let Some(line) = read_line() {
        let Ok(request) = serde_json::from_str::<Value>(&line) else { continue };
        if provider == "claude" {
            if text(&request, "type") != Some("control_request") {
                continue;
            }
            say(&lines[0]);
            say(&lines[1].replace("REQUEST_ID", text(&request, "request_id").unwrap_or("")));
        } else if text(&request, "method") == Some("initialize") {
            say(&with_id(&lines[0], request.get("id").unwrap_or(&Value::Null)));
            say(&lines[1]);
        } else if text(&request, "method") == Some("model/list") {
            say(&with_id(&lines[2], request.get("id").unwrap_or(&Value::Null)));
        }
    }
}

/// Claude with host prompts: the handshake answered, then (after the prompt) one tool request and one plan, each waited for and its
/// answer recorded in `<record>.answers`; then the recording replayed and, once the host closes stdin, the end.
fn replay_cli(recording: &str, record: &str, args: &[String]) {
    let _ = std::fs::write(format!("{record}.args"), serde_json::to_string(args).unwrap_or_default());
    if args.iter().any(|a| a == "--version") {
        say("2.1.322");
        return;
    }
    let frames: Vec<String> = fixture(&format!("{recording}.jsonl"))
        .split('\n')
        .map(|l| l.trim_end_matches('\r').to_string())
        .filter(|l| !l.trim().is_empty())
        .collect();
    let mut asked = 0;
    while let Some(line) = read_line() {
        append(&format!("{record}.input"), &format!("{line}\n"));
        let Ok(root) = serde_json::from_str::<Value>(&line) else { continue };
        match text(&root, "type") {
            Some("control_request") => {
                say(&json!({"type": "control_response", "response": {"subtype": "success", "request_id": text(&root, "request_id"), "response": {}}}).to_string());
            }
            Some("user") if asked == 0 => {
                asked = 1;
                say(
                    "{\"type\":\"control_request\",\"request_id\":\"replay-ask\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"tool_use_id\":\"toolu_replay_ask\",\"input\":{\"command\":\"ls\",\"description\":\"List the folder\"}}}",
                );
            }
            Some("control_response") if asked == 1 => {
                asked = 2;
                append(&format!("{record}.answers"), &format!("{line}\n"));
                say(
                    "{\"type\":\"control_request\",\"request_id\":\"replay-plan\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"ExitPlanMode\",\"tool_use_id\":\"toolu_replay_plan\",\"input\":{\"plan\":\"# Plan\\n1. Replay\"},\"requires_user_interaction\":true}}",
                );
            }
            Some("control_response") if asked == 2 => {
                asked = 3;
                append(&format!("{record}.answers"), &format!("{line}\n"));
                for frame in &frames {
                    say(frame);
                }
            }
            _ => {}
        }
    }
}
