//! The checks' stand-in agent CLI: what C# relaunches its test exe as (MightyCode.Core.Tests/Verification.cs:49-217
//! `FakeCliAsync`, ModelListProbeVerification.cs:151-187). None of these is a real CLI. std and serde_json only.
//!
//! - `--fake-cli <provider> <record> [args…]`: records its arguments in `<record>.args` (a JSON array) and plays C#'s fake: with
//!   `--permission-prompt-tool` Claude's stdio control protocol (the handshake, one tool request, the plan, resumed-agent and
//!   background fixtures); with `app-server` or `--safe-mode` the model-list sessions; as `pi`, `pi --mode rpc` answering from
//!   `--pi-fixture <recording>` (`--pi-hang`, `--pi-steer`, `--pi-hold-start`, `--pi-pause-before-end`); otherwise reads stdin to its
//!   end into `<record>.prompt` and answers as
//!   Codex's exec (or agy) would; with `--agy-denied <settings>` as C#'s `FakeAgy` (refused `echo hi` on a new conversation, continued
//!   it says whether the settings allow it), with `--agy-signin-lost` or `--agy-quoted` agy's lost sign-in on stderr or as its answer.
//! - `--model-list-cli <provider> <state> [args…]`: each CLI's model-list read, answered with the real CLI's recorded output
//!   (native/contracts/fixtures/model-list-*) for its verified arguments, refused otherwise.
//! - `--replay-cli <recording> <record> [args…]`: Claude with host prompts that asks one tool permission and one plan, records the
//!   answers, then replays the recording (native/contracts/fixtures/<recording>.jsonl) and ends once its input closes.
//! - `--codex-approval-fixture [args…]`: Codex's `app-server` for one approval turn (CodexApprovalVerification.cs:141 `FakeCliAsync`):
//!   the handshake, one command approval that must be accepted, the answer, the turn's end, then stdin must end.
//! - `--fake-acp <state> <fixture> [--fake-linger|--fake-stall|--fake-await-answers] [args…]` (C# AcpRunVerification.FakeAcpAsync): an ACP agent that answers
//!   the channel's requests with a recorded opencode or Hermes session (each `mighty-…` answer only after one request line), records its
//!   argv, its environment and its stdin in `<state>`, and plays Hermes's `config get/set` over `<state>/hermes-config.json`.
//! - `--hermes-acp-cli <state> <mode> [args…]` (C# AcpAgentAccountVerification.FakeHermesAsync): `hermes acp` as 0.23.1 answers the
//!   sign-in probe, by `<mode>` (`answer`, `unconfigured`, `error`, `missing`, `silent`; `file` reads it from `<state>/mode.txt`).

use std::fs::OpenOptions;
use std::io::{BufRead, Read, Write};
use std::path::{Path, PathBuf};
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
        Some("--codex-approval-fixture") => codex_approval_fixture(&args[1..]),
        Some("--fake-acp") if args.len() >= 3 => fake_acp(&args[1], &args[2], &args[3..]),
        Some("--hermes-acp-cli") if args.len() >= 3 => fake_hermes(&args[1], &args[2], &args[3..]),
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
    if provider == "pi" && !has("--version") {
        fake_pi(record, args);
        return;
    }
    if has("--version") {
        say("2.1.271");
        return;
    }
    if has("--close-input") {
        claude_closing_input(record);
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
    // An agy that is refused a command on its first turn and, continued (--conversation), says whether its settings allow it.
    if let Some(at) = args.iter().position(|a| a == "--agy-denied")
        && let Some(settings) = args.get(at + 1)
    {
        fake_agy(settings, has("--conversation"), &input);
        return;
    }
    // What Antigravity CLI 1.3.5 prints on stderr when its sign-in is gone, and the same words as a model answer.
    const SIGN_IN_LOST: &str = "Error: authentication required. Run 'agy' to log in, then retry.";
    if has("--agy-signin-lost") {
        eprintln!("{SIGN_IN_LOST}");
        std::process::exit(1);
    }
    if has("--agy-quoted") {
        say(&json!({"event": "step_update", "step_update": {"step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": SIGN_IN_LOST}}).to_string());
        say("{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":{\"message\":\"Internal error encountered.\"}}}");
        std::process::exit(1);
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

/// A pi that answers on stdin as `pi --mode rpc` does, from a fixture (`--pi-fixture <file>`), and exits when stdin ends
/// (PiVerification.cs:360 `FakeAsync`): `get_state` answered with the first line; a prompt with the next three, then (after
/// `--pi-hang`'s minute, or the steer `--pi-steer` reads) the rest; with `--pi-dialogs` (the app's smoke) it waits for the answer to
/// each dialog it shows (`extension_ui_request` but `notify`) as pi does. Every line it reads is kept in `<record>.stdin`. Not C#'s:
/// `--pi-hold-start <file>` holds `agent_start` back until the file exists, and `--pi-pause-before-end <ms>` waits that long before
/// `agent_end`; stdin is read on a thread of its own, so `<record>.eof` says whether its end came before `agent_end` was written.
fn fake_pi(record: &str, args: &[String]) {
    let has = |flag: &str| args.iter().any(|a| a == flag);
    let value = |flag: &str| args.iter().position(|a| a == flag).and_then(|at| args.get(at + 1)).cloned();
    let path = value("--pi-fixture").unwrap_or_default();
    let fixture: Vec<String> = std::fs::read_to_string(&path)
        .unwrap_or_default()
        .split('\n')
        .map(|line| line.trim_end_matches('\r').to_string())
        .filter(|line| !line.is_empty())
        .collect();
    let stdin = format!("{record}.stdin");
    let eof = format!("{record}.eof");
    let hold = value("--pi-hold-start");
    let pause = value("--pi-pause-before-end").and_then(|ms| ms.parse::<u64>().ok());
    let ended = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let (lines, incoming) = std::sync::mpsc::channel::<String>();
    let reader = ended.clone();
    std::thread::spawn(move || {
        while let Some(line) = read_line() {
            if lines.send(line).is_err() {
                return;
            }
        }
        reader.store(true, std::sync::atomic::Ordering::SeqCst);
    });
    let next = || incoming.recv().ok();
    let mut agent_end = false;
    while let Some(line) = next() {
        append(&stdin, &format!("{line}\n"));
        if line.contains("\"get_state\"") {
            if let Some(first) = fixture.first() {
                say(first);
            }
        } else if line.contains("\"prompt\"") {
            for output in fixture.iter().skip(1).take(3) {
                if output.contains("\"type\":\"agent_start\"")
                    && let Some(file) = &hold
                {
                    let until = std::time::Instant::now() + Duration::from_secs(30);
                    while !Path::new(file).exists() && std::time::Instant::now() < until {
                        std::thread::sleep(Duration::from_millis(20));
                    }
                }
                say(output);
            }
            if has("--pi-hang") {
                std::thread::sleep(Duration::from_secs(60));
            }
            if has("--pi-steer")
                && let Some(steer) = next()
            {
                append(&stdin, &format!("{steer}\n"));
            }
            for output in fixture.iter().skip(4) {
                if output.contains("\"type\":\"agent_end\"") {
                    if let Some(ms) = pause {
                        std::thread::sleep(Duration::from_millis(ms));
                    }
                    if ended.load(std::sync::atomic::Ordering::SeqCst) && !agent_end {
                        let _ = std::fs::write(&eof, "before agent_end");
                    }
                    agent_end = true;
                }
                say(output);
                let asks = output.contains("\"extension_ui_request\"") && !output.contains("\"method\":\"notify\"");
                if has("--pi-dialogs")
                    && asks
                    && let Some(answer) = next()
                {
                    append(&stdin, &format!("{answer}\n"));
                }
            }
        }
    }
    if !Path::new(&eof).exists() {
        let _ = std::fs::write(&eof, if agent_end { "after agent_end" } else { "without agent_end" });
    }
}

/// C#'s check fails the fixture with an exception (a non-zero exit).
fn check(value: bool, message: &str) {
    if !value {
        eprintln!("{message}");
        std::process::exit(1);
    }
}

/// Codex's `app-server` for one approval turn (CodexApprovalVerification.cs:141 `FakeCliAsync`).
fn codex_approval_fixture(args: &[String]) {
    check(args.iter().any(|a| a == "app-server") && !args.iter().any(|a| a == "exec"), "Fixture expected app-server arguments.");
    let read = || -> Value {
        let Some(line) = read_line() else {
            eprintln!("unexpected stdin EOF");
            std::process::exit(1);
        };
        serde_json::from_str(&line).unwrap_or(Value::Null)
    };
    let initialize = read();
    check(text(&initialize, "method") == Some("initialize"), "first frame is initialize");
    say(&json!({"id": initialize["id"], "result": {}}).to_string());
    check(text(&read(), "method") == Some("initialized"), "client acknowledges initialization");
    let thread = read();
    say(&json!({"id": thread["id"], "result": {"thread": {"id": "thread-1"}}}).to_string());
    let turn = read();
    check(turn["params"]["approvalPolicy"] == "on-request", "actual turn carries approval policy");
    say(&json!({"id": turn["id"], "result": {"turn": {"id": "turn-1"}}}).to_string());
    say(&json!({"id": 40, "method": "item/commandExecution/requestApproval", "params": {"threadId": "thread-1", "turnId": "turn-1", "itemId": "cmd-1", "command": "fixture command", "cwd": "C:\\work"}}).to_string());
    check(read()["result"]["decision"] == "accept", "explicit single-call response arrives");
    say(&json!({"method": "item/completed", "params": {"threadId": "thread-1", "turnId": "turn-1", "item": {"id": "result", "type": "agentMessage", "text": "fixture complete"}}}).to_string());
    say(&json!({"method": "turn/completed", "params": {"threadId": "thread-1", "turn": {"id": "turn-1", "status": "completed"}}})
        .to_string());
    check(read_line().is_none(), "stdin ends only after turn completion");
}

/// C# `AntigravityPermissionVerification.FakeAgy` (`--agy-denied <settings>`): refused a command on its first turn and, continued, says
/// whether its settings allow it.
fn fake_agy(settings: &str, continued: bool, input: &str) {
    const CONVERSATION: &str = "11111111-2222-4333-8444-555555555555";
    say(&format!("{{\"event\":\"init\",\"conversation_id\":\"{CONVERSATION}\"}}"));
    if continued {
        let allowed = std::fs::read_to_string(settings).is_ok_and(|text| text.contains("command(echo hi)"));
        let answer = if allowed { "allowed" } else { "missing" };
        say(&format!(
            "{{\"event\":\"result\",\"result\":{{\"conversation_id\":\"{CONVERSATION}\",\"status\":\"SUCCESS\",\"response\":\"{answer}\"}}}}"
        ));
        return;
    }
    // Asked for something "risky", it is refused a chained command instead.
    let command = if input.contains("risky") { "echo hi && date" } else { "echo hi" };
    say(&format!(
        "{{\"event\":\"step_update\",\"step_update\":{{\"step_index\":2,\"state\":\"ERROR\",\"step_type\":\"tool\",\"tool_name\":\"run_command\",\"tool_info\":{{\"name\":\"run_command\",\"parameters\":{{\"CommandLine\":\"{command}\"}},\"error\":{{\"type\":\"TOOL_ERROR\",\"message\":\"permission check failed for unsandboxed \\\"{command}\\\": user denied permission to run command:\\n{command}\\nDo not attempt to circumvent this denial.\"}}}}}}}}"
    ));
    say(&format!(
        "{{\"event\":\"result\",\"result\":{{\"conversation_id\":\"{CONVERSATION}\",\"status\":\"SUCCESS\",\"response\":\"\",\"denied_actions\":[{{\"action\":\"command\",\"display_name\":\"RunCommand\"}}]}}}}"
    ));
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

/// Closes this process's stdin, so the host's next write to it fails.
fn close_stdin() {
    #[cfg(windows)]
    {
        use std::os::windows::io::{AsRawHandle, FromRawHandle};
        // SAFETY: the process's own stdin handle, closed once; nothing reads it afterwards.
        drop(unsafe { std::fs::File::from_raw_handle(std::io::stdin().as_raw_handle()) });
    }
    #[cfg(unix)]
    {
        use std::os::unix::io::FromRawFd;
        // SAFETY: descriptor 0, closed once; nothing reads it afterwards.
        drop(unsafe { std::fs::File::from_raw_fd(0) });
    }
}

/// A Claude with host prompts whose stdin breaks before its handshake is answered: it reads the `initialize` request, closes its
/// input, asks a control request the host must answer (that write breaks the pipe), then answers the handshake, so the host's
/// prompt write fails; then ends as if it had succeeded.
fn claude_closing_input(record: &str) {
    let Some(line) = read_line() else { return };
    append(&format!("{record}.input"), &format!("{line}\n"));
    let initialize = serde_json::from_str::<Value>(&line).ok().and_then(|root| text(&root, "request_id").map(str::to_string));
    close_stdin();
    say("{\"type\":\"control_request\",\"request_id\":\"probe-1\",\"request\":{\"subtype\":\"fixture_probe\"}}");
    std::thread::sleep(Duration::from_millis(500));
    say(&json!({"type": "control_response", "response": {"subtype": "success", "request_id": initialize, "response": {}}}).to_string());
    std::thread::sleep(Duration::from_millis(500));
    say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"never asked\"}");
}

/// The watchdog checks' end: after the silence the host's next line (its input still open) is kept in `<record>.second` and the
/// turn ends idle; an input closed meanwhile leaves `<record>.closed`.
fn after_silence(record: &str) {
    std::thread::sleep(Duration::from_millis(1500));
    match read_line() {
        None => {
            let _ = std::fs::write(format!("{record}.closed"), "");
        }
        Some(next) => {
            let _ = std::fs::write(format!("{record}.second"), next);
            say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\"}");
            say("{\"type\":\"system\",\"subtype\":\"session_state_changed\",\"state\":\"idle\"}");
            while read_line().is_some() {}
        }
    }
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
                // The steer is a turn of its own, which the CLI ends with its own result (C#'s fake leaves it out: its runner never
                // waits for it).
                say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"steered\"}");
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
            // The request's turn ends idle with a background agent running; a steer opens a new turn, the agent ends, and the new
            // turn stays silent past the idle close: its input must stay open (the last turn's result does not close it).
            Some("user") if line.contains("steered watchdog fixture") => {
                for frame in [
                    "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"bg1\",\"tool_use_id\":\"toolu_bg\",\"description\":\"Review\",\"is_backgrounded\":true,\"task_type\":\"local_agent\"}",
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"first\"}",
                    "{\"type\":\"system\",\"subtype\":\"session_state_changed\",\"state\":\"idle\"}",
                ] {
                    say(frame);
                }
                let Some(follow) = read_line() else { std::process::exit(24) };
                let _ = std::fs::write(format!("{record}.follow"), follow);
                say(
                    "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"bg1\",\"tool_use_id\":\"toolu_bg\",\"status\":\"completed\",\"summary\":\"done\"}",
                );
                after_silence(record);
                return;
            }
            // The request's result comes while the CLI says it still runs (a resumed agent no task names) and stays silent past the
            // idle close: its input must stay open until it says idle.
            Some("user") if line.contains("busy watchdog fixture") => {
                say("{\"type\":\"system\",\"subtype\":\"session_state_changed\",\"state\":\"running\"}");
                say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"first\"}");
                after_silence(record);
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
                // The steered turn's own result (see the resumed agent fixture).
                say("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"steered\"}");
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

/// An ACP agent replaying a recorded session (C# `AcpRunVerification.FakeAcpAsync`): each answer to one of the channel's requests
/// (`"id":"mighty-…"`) goes only after one request line was read; `--fake-stall` stops before the prompt's answer and `--fake-linger`
/// keeps the server up after its input ended, both for a minute. `--fake-await-answers` (not C#'s: its checks answer no card) also waits,
/// after each request of the agent's own (a permission, a form), for the client's answer line before it goes on, as a real agent does.
/// Not C#'s either: each line read is also timestamped in `stdin-times.txt`, and `stdin-ended` says when stdin ended.
fn fake_acp(state: &str, fixture_path: &str, rest: &[String]) {
    let linger = rest.iter().any(|a| a == "--fake-linger");
    let stall = rest.iter().any(|a| a == "--fake-stall");
    let await_answers = rest.iter().any(|a| a == "--fake-await-answers");
    let args: Vec<&String> = rest.iter().filter(|a| !a.starts_with("--fake-")).collect();
    let state = PathBuf::from(state);
    if args.len() == 1 && args[0] == "--version" {
        let version = std::fs::read_to_string(state.join("version")).unwrap_or_else(|_| "opencode v2.2.7".into());
        say(&version);
        return;
    }
    let config = state.join("hermes-config.json");
    let values = || -> serde_json::Map<String, Value> {
        std::fs::read_to_string(&config).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
    };
    let log = state.join("config.log").to_string_lossy().into_owned();
    if args.len() == 4 && args[0] == "config" && args[1] == "get" && args[2] == "--json" {
        let key = args[3];
        append(&log, &format!("get {key}\n"));
        match values().get(key.as_str()) {
            Some(value) => say(&value.to_string()),
            None => {
                eprintln!("Config key not set: {key}");
                std::process::exit(1);
            }
        }
        return;
    }
    if args.len() == 4 && args[0] == "config" && args[1] == "set" {
        let mut stored = values();
        stored.insert(args[2].clone(), serde_json::from_str(args[3]).unwrap_or(Value::Null));
        let _ = std::fs::write(&config, Value::Object(stored).to_string());
        append(&log, &format!("set {} {}\n", args[2], args[3]));
        return;
    }
    let _ = std::fs::write(state.join("argv.json"), serde_json::to_string(&args).unwrap_or_default());
    let env = json!({
        "OPENCODE_CONFIG_CONTENT": std::env::var("OPENCODE_CONFIG_CONTENT").ok(),
        "HERMES_YOLO_MODE": std::env::var("HERMES_YOLO_MODE").ok(),
    });
    let _ = std::fs::write(state.join("env.json"), env.to_string());
    let stdin = state.join("stdin.txt").to_string_lossy().into_owned();
    // Each line read, also in `stdin-times.txt` with when it was read (milliseconds since 1970), and `stdin-ended` once stdin ends.
    let times = state.join("stdin-times.txt").to_string_lossy().into_owned();
    let ended = state.join("stdin-ended");
    let now = || std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0);
    let read_line = || match read_line() {
        Some(line) => {
            append(&times, &format!("{} {line}\n", now()));
            Some(line)
        }
        None => {
            let _ = std::fs::write(&ended, now().to_string());
            None
        }
    };
    let recorded = std::fs::read_to_string(fixture_path).unwrap_or_default();
    // The client's requests read while waiting for an answer, each still owed its recorded answer.
    let mut requests: std::collections::VecDeque<String> = std::collections::VecDeque::new();
    let frame = |line: &str| serde_json::from_str::<Value>(line).unwrap_or(Value::Null);
    let method = |line: &str| frame(line).get("method").is_some();
    let is_request = |line: &str| method(line) && frame(line).get("id").is_some_and(|id| !id.is_null());
    for line in recorded.split('\n').map(|l| l.trim_end_matches('\r')).filter(|l| !l.is_empty()) {
        if stall && line.contains("\"stopReason\"") {
            break;
        }
        if line.contains("\"id\":\"mighty-") && requests.pop_front().is_none() {
            let Some(request) = read_line() else { return };
            append(&stdin, &format!("{request}\n"));
        }
        say(line);
        if await_answers && is_request(line) {
            // The recording's updates came before the client's next request: a request read here waits for its own answer, and a
            // notification is no answer.
            loop {
                let Some(next) = read_line() else { return };
                append(&stdin, &format!("{next}\n"));
                if is_request(&next) {
                    requests.push_back(next);
                } else if !method(&next) {
                    break;
                }
            }
        }
    }
    while let Some(extra) = read_line() {
        append(&stdin, &format!("{extra}\n"));
    }
    if linger || stall {
        std::thread::sleep(Duration::from_secs(60));
    }
}

/// The first line of a fixture that is not empty.
fn fixture_line(name: &str) -> String {
    fixture(name).split('\n').map(|l| l.trim_end_matches('\r')).find(|l| !l.is_empty()).unwrap_or("").to_string()
}

/// `hermes acp` as 0.23.1 answers the sign-in probe (C# `AcpAgentAccountVerification.FakeHermesAsync`): it reads the one request, answers
/// with the recorded line (after a log line on standard error), then serves until its input ends.
fn fake_hermes(state: &str, mode: &str, args: &[String]) {
    let state = PathBuf::from(state);
    let mode = if mode == "file" {
        std::fs::read_to_string(state.join("mode.txt")).unwrap_or_default().trim().to_string()
    } else {
        mode.to_string()
    };
    let _ = std::fs::write(state.join(format!("{mode}.argv.json")), serde_json::to_string(args).unwrap_or_default());
    let folder = state.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let marker = std::env::current_dir().unwrap_or_default().join(format!("mighty-hermes-probe-{folder}.marker"));
    let run = json!({"NO_COLOR": std::env::var("NO_COLOR").ok(), "inTemporaryFolder": if marker.exists() { "yes" } else { "no" }});
    let _ = std::fs::write(state.join(format!("{mode}.run.json")), run.to_string());
    if args.len() != 1 || args[0] != "acp" {
        eprintln!("hermes: error: argument command: invalid choice: '{}'", args.join(" "));
        std::process::exit(2);
    }
    if mode == "missing" {
        eprintln!("The ACP server can't start: its protocol packages are missing from this install.");
        eprintln!("From the Hermes environment, run: hermes pm install --extra acp");
        std::process::exit(1);
    }
    let request = read_line();
    let _ = std::fs::write(state.join(format!("{mode}.stdin")), request.as_deref().unwrap_or("<none>"));
    eprintln!("2026-11-11 01:18:54 [INFO] acp_adapter.entry: serving ACP on stdio");
    let answer = match mode.as_str() {
        "answer" => Some(format!("warning line\n{}", fixture_line("hermes-acp-answer.jsonl"))),
        "unconfigured" => Some(fixture_line("hermes-acp-unconfigured.jsonl")),
        "error" => Some(
            "{\"jsonrpc\":\"2.0\",\"id\":\"mighty-1\",\"error\":{\"code\":-32603,\"message\":\"Internal error\",\"data\":{\"details\":\"boom\"}}}"
                .to_string(),
        ),
        _ => None,
    };
    if let Some(answer) = answer {
        say(&answer);
    }
    let mut more = 0;
    while read_line().is_some() {
        more += 1;
    }
    let _ = std::fs::write(state.join(format!("{mode}.closed")), format!("input ended after {more} more line(s)"));
}
