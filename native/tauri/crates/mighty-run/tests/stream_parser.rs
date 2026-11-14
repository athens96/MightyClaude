//! OutputParser and ClaudeStream checks: C# ActivityUsageVerification (Activities, ProviderUsage's Codex half, ClaudeContext,
//! LeftoverTaskResult), ComposerParityVerification.StreamEventsReachTheTranscript (Claude and Codex compaction, rate limits, over-long
//! lines) and RunLogIsCopiedBehindItsRoles, Verification.cs "stream parser keeps text after thinking…" (its Claude half),
//! ResumedAgentVerification's replay of claude-stream-resumed-agent.jsonl (the background and input parts; the graph's are mighty-graph's)
//! ClaudeResumeVerification, and RuntimeParityVerification's ApprovalStateWinsDelayedMods and ModsNeverReplaceRootResume (its
//! child-graph checks are mighty-graph's).

mod common;

use std::sync::{Arc, Mutex};

use mighty_core::model::{AgentActivity, LogEntry, RunSession, SessionUsage};
use mighty_run::activity;
use mighty_run::compaction;
use mighty_run::lines::{Lines, Next};
use mighty_run::meta;
use mighty_run::model::RunEvent;
use mighty_run::parser::{OutputParser, ParserHooks, maximum_line_bytes};
use mighty_run::plan_mode::{BackgroundTaskTracker, work};
use mighty_run::resume::MISSING_CONVERSATION;
use mighty_run::run_log;
use mighty_run::stream;
use mighty_run::strings;
use serde_json::{Value, json};

type List<T> = Arc<Mutex<Vec<T>>>;

#[derive(Default, Clone)]
struct Seen {
    logs: List<(String, String)>,
    activity: List<AgentActivity>,
    usage: List<SessionUsage>,
}

impl Seen {
    fn hooks(&self, usage: bool) -> ParserHooks {
        let (logs, activities, usages) = (self.logs.clone(), self.activity.clone(), self.usage.clone());
        ParserHooks {
            log: Some(Arc::new(move |kind, text| logs.lock().unwrap().push((kind.to_string(), text.to_string())))),
            activity: Some(Arc::new(move |value| activities.lock().unwrap().push(value))),
            usage: usage.then(|| Arc::new(move |value| usages.lock().unwrap().push(value)) as _),
            ..ParserHooks::default()
        }
    }
    fn logs(&self) -> Vec<(String, String)> {
        self.logs.lock().unwrap().clone()
    }
    fn activity(&self) -> Vec<AgentActivity> {
        self.activity.lock().unwrap().clone()
    }
    fn usage(&self) -> Vec<SessionUsage> {
        self.usage.lock().unwrap().clone()
    }
    fn last_usage(&self) -> SessionUsage {
        self.usage().last().cloned().unwrap()
    }
}

fn pair(kind: &str, text: &str) -> (String, String) {
    (kind.to_string(), text.to_string())
}

/// `SessionUsage.ContextPercent`.
fn context_percent(usage: &SessionUsage) -> Option<f64> {
    match (usage.context_used_tokens, usage.context_window_tokens) {
        (Some(used), Some(window)) if window > 0 => Some(used as f64 * 100.0 / window as f64),
        _ => None,
    }
}

#[test]
fn activities() {
    common::korean();
    let seen = Seen::default();
    let now = Arc::new(Mutex::new(0.0));
    let clock = now.clone();
    let parser = OutputParser::new("claude", "run-one", seen.hooks(false)).with_clock(Arc::new(move || *clock.lock().unwrap()));
    parser.parse(r#"{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"tool-1","name":"Bash","input":{"command":"dotnet test","secret":"NEVER_DISPLAY"}}]}}"#);
    *now.lock().unwrap() = 100.0;
    parser.receive_mod(&common::json(
        r#"{"event":"tool.waiting","claudeSessionId":"session","toolUseId":"tool-1","tool":"Bash","summary":"dotnet test","sequence":2}"#,
    ));
    *now.lock().unwrap() = 1250.0;
    parser.parse(r#"{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool-1","content":[{"type":"text","text":"passed"}]}]}}"#);
    let complete = seen.activity().last().cloned().unwrap();
    assert!(
        complete.state == "completed"
            && complete.kind == "command"
            && complete.summary == "dotnet test"
            && complete.output.as_deref() == Some("passed")
            && complete.duration_ms == Some(1250.0),
        "Tool identity/duration/output must come from actual lifecycle: {complete:?}"
    );
    parser
        .receive_mod(&common::json(r#"{"event":"tool.call","claudeSessionId":"session","toolUseId":"tool-1","tool":"Bash","sequence":3}"#));
    let all = seen.activity();
    let mut ids: Vec<&String> = all.iter().map(|a| &a.id).collect();
    ids.dedup();
    assert!(*all.last().unwrap() == complete && ids.len() == 1, "Late Mods must not resurrect or duplicate a tool");
    parser.parse(r#"{"type":"assistant","message":{"content":[{"type":"tool_use","id":"unsettled","name":"Read","input":{"file_path":"README.md"}}]}}"#);
    parser.finish_activities(true);
    assert_eq!(seen.activity().last().unwrap().state, "stopped", "Cancelled unfinished tool must be stopped");
    let count = seen.activity().len();
    parser.receive_mod(&common::json(
        r#"{"event":"tool.complete","claudeSessionId":"session","toolUseId":"unsettled","tool":"Read","sequence":4}"#,
    ));
    assert_eq!(seen.activity().len(), count, "Finalized parser must reject late Mods");
    let next = Seen::default();
    OutputParser::new("claude", "run-two", next.hooks(false))
        .parse(r#"{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tool-1","name":"Bash","input":{}}]}}"#);
    let next = next.activity();
    assert!(next.len() == 1 && next[0].id != complete.id, "Activity identity must be run scoped");
    assert_eq!(activity::duration_label(&complete).as_deref(), Some("1.2초"), "Duration display must not round up");
}

/// ActivityUsageVerification.cs:31 `ProviderUsage`: Codex's cumulative snapshots and Antigravity's streamed answer, totals and tool rows.
#[test]
fn provider_usage() {
    common::korean();
    let seen = Seen::default();
    let codex = OutputParser::new("codex", "codex-usage", seen.hooks(true));
    codex.parse(r#"{"type":"thread.started","thread_id":"thread"}"#);
    codex.parse(r#"{"type":"item.started","item":{"id":"cmd","type":"command_execution","command":"git status"}}"#);
    codex.parse(r#"{"type":"item.completed","item":{"id":"cmd","type":"command_execution","command":"git status","aggregated_output":"clean","status":"completed"}}"#);
    const TOTALS: &str = r#"{"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":100,"output_tokens":80,"reasoning_output_tokens":40}}"#;
    codex.parse(TOTALS);
    let emitted = seen.usage().len();
    codex.parse(TOTALS);
    let last = seen.last_usage();
    assert!(
        seen.usage().len() == emitted
            && last.input_tokens == Some(1200)
            && last.token_scope == "session"
            && context_percent(&last).is_none()
            && last.model.is_none(),
        "Codex cumulative snapshots must not sum or invent context/model"
    );
    assert!(seen.activity().iter().any(|a| a.kind == "command" && a.output.as_deref() == Some("clean")), "Codex command content missing");
    // Antigravity: a long Markdown answer streamed in pieces stays one message; its totals are not context usage.
    let seen = Seen::default();
    let gemini = OutputParser::new("gemini", "gemini-usage", seen.hooks(true));
    gemini.parse(r#"{"event":"init","conversation_id":"11111111-2222-4333-8444-555555555555","init":{"model":"gemini-3.8-flash-low"}}"#);
    let markdown = format!("```swift\n{}\n```\n\n|a|b|\n|-|-|\n|1|2|", "x".repeat(20000));
    let chars: Vec<char> = markdown.chars().collect();
    for chunk in chars.chunks(3000) {
        let piece: String = chunk.iter().collect();
        gemini.parse(
            &serde_json::json!({"event": "step_update", "step_update": {"step_index": 1, "state": "ACTIVE", "step_type": "agent_response", "text_delta": piece}})
                .to_string(),
        );
    }
    gemini.parse(r#"{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response"}}"#);
    gemini.parse(r#"{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"file.swift"}}}}"#);
    gemini.parse(r#"{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"tool","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"file.swift"},"output":"content"}}}"#);
    gemini.parse(r#"{"event":"result","result":{"status":"SUCCESS","response":"repeated","usage":{"input_tokens":1400,"output_tokens":90,"cache_read_tokens":200,"total_tokens":1490}}}"#);
    let answers: Vec<String> = seen.logs().into_iter().filter(|(kind, _)| kind == "assistant").map(|(_, text)| text).collect();
    assert_eq!(answers, [markdown], "Long Markdown must remain one logical message across delta reads");
    let last = seen.last_usage();
    assert!(
        last.total_tokens == Some(1690)
            && last.input_tokens == Some(1600)
            && last.context_used_tokens.is_none()
            && last.context_window_tokens.is_none()
            && last.token_scope == "session",
        "Antigravity totals are not context usage, and input includes cached input"
    );
    assert!(
        seen.activity().iter().any(|a| a.tool_name.as_deref() == Some("view_file")
            && a.summary == "file.swift"
            && a.output.as_deref() == Some("content")
            && a.state == "completed"),
        "Antigravity tool lifecycle missing"
    );
    gemini.parse(r#"{"event":"result","result":{"status":"ERROR","error":{"message":"failed"}}}"#);
    gemini.parse(r#"{"event":"result","result":{"status":"WAITING"}}"#);
    assert!(gemini.failed(), "A later notice must not clear a fatal error");
}

#[test]
fn claude_context() {
    common::korean();
    let seen = Seen::default();
    let parser = OutputParser::new("claude", "claude-context", ParserHooks { usage: seen.hooks(true).usage, ..ParserHooks::default() });
    parser.parse(r#"{"type":"system","subtype":"init","session_id":"claude-session","model":"claude-sonnet-test"}"#);
    parser.parse(r#"{"type":"assistant","session_id":"claude-session","message":{"model":"claude-sonnet-test","usage":{"input_tokens":100,"cache_read_input_tokens":200,"cache_creation_input_tokens":50,"output_tokens":10},"content":[]}}"#);
    let last = seen.last_usage();
    assert!(
        last.input_tokens == Some(350) && last.context_used_tokens == Some(350) && context_percent(&last).is_none(),
        "Claude input must include caches without inventing capacity"
    );
    parser.parse(r#"{"type":"result","session_id":"claude-session","total_cost_usd":0.2,"modelUsage":{"claude-sonnet-test":{"inputTokens":2000,"cacheReadInputTokens":300,"cacheCreationInputTokens":100,"outputTokens":50,"contextWindow":200000}}}"#);
    let last = seen.last_usage();
    assert!(
        last.input_tokens == Some(2400)
            && last.context_used_tokens == Some(350)
            && last.context_window_tokens == Some(200000)
            && last.token_scope == "run",
        "Run totals must not overwrite current context"
    );
    let stamp = "2026-09-16T00:00:00Z";
    // `new SessionUsage { … }` written by Wire.Json: its defaults (provider claude, scope run) are written too.
    let observed = json!({
        "provider": "claude", "tokenScope": "run", "providerSessionId": "claude-session", "source": "claude.mods", "contextUsedTokens": 1000, "contextWindowTokens": 200000,
        "costUSD": 0.5, "costScope": "session", "rateLimits": [{"kind": "five_hour", "percentUsed": 42}], "updatedAt": stamp,
    });
    let envelope = |sequence: Value, usage: &Value| json!({"version": 1, "claudeSessionId": "claude-session", "event": "session.usage", "sequence": sequence, "usage": usage});
    let with = |changes: Value| {
        let mut value = observed.clone();
        for (key, change) in changes.as_object().unwrap() {
            value[key] = change.clone();
        }
        value
    };
    parser.receive_mod(&envelope(common::json("3e0"), &observed));
    let count = seen.usage().len();
    parser.receive_mod(&envelope(json!(2), &with(json!({"contextUsedTokens": 1900}))));
    assert_eq!(seen.usage().len(), count, "Stale Mods usage sequence must be ignored");
    for sequence in ["3.0", "3e0", "2.0", "2e0"] {
        let newer = with(json!({
            "contextUsedTokens": 1900, "contextWindowTokens": 100000, "costUSD": 9,
            "rateLimits": [{"kind": "five_hour", "percentUsed": 95}], "rateLimitsUpdatedAt": "2026-09-16T00:01:00Z",
        }));
        parser.receive_mod(&envelope(common::json(sequence), &newer));
    }
    let last = seen.last_usage();
    assert!(
        seen.usage().len() == count
            && last.context_used_tokens == Some(1000)
            && last.context_window_tokens == Some(200000)
            && last.cost_usd == Some(0.5)
            && last.rate_limits.as_ref().is_some_and(|l| l.len() == 1 && l[0].percent_used == Some(42.0)),
        "Equivalent or older decimal/exponent Mods sequences must not replace context, quota or cost"
    );
    assert!(
        last.rate_limits_updated_at.as_deref() == Some(stamp)
            && context_percent(&last) == Some(0.5)
            && last.cost_scope.as_deref() == Some("session"),
        "Direct Mods context/quota source missing"
    );
    parser.parse(r#"{"type":"assistant","session_id":"claude-session","message":{"model":"claude-sonnet-test","usage":{"input_tokens":500,"output_tokens":10},"content":[]}}"#);
    let last = seen.last_usage();
    assert!(
        last.rate_limits_updated_at.as_deref() == Some(stamp) && last.input_tokens == Some(2400) && last.context_used_tokens == Some(500),
        "stdout must not refresh quota age or replace final run totals"
    );
    parser.parse(r#"{"type":"system","subtype":"compact_boundary","session_id":"claude-session"}"#);
    assert!(seen.last_usage().context_used_tokens.is_none(), "Compaction must clear unknown current context");
    let count = seen.usage().len();
    parser.parse(r#"{"type":"assistant","session_id":"child-session","parent_tool_use_id":"agent-tool","message":{"model":"different","usage":{"input_tokens":123,"output_tokens":4},"content":[]}}"#);
    assert_eq!(seen.usage().len(), count, "Subagent usage must not overwrite main context");
}

/// Recorded from `claude --resume` after a process that left `sleep 600` running in the background.
#[test]
fn leftover_task_result() {
    common::korean();
    let seen = Seen::default();
    let parser = OutputParser::new("claude", "leftover", seen.hooks(true));
    parser.parse(r#"{"type":"system","subtype":"task_notification","task_id":"b9f44slha","status":"stopped","summary":"Background shell command didn't finish before the previous session ended"}"#);
    let leftover = r#"{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","total_cost_usd":0,"origin":{"kind":"task-notification"}}"#;
    assert!(
        stream::is_notification_result(&common::json(leftover)),
        "The recorded leftover event must be recognized as a notification result"
    );
    parser.parse(leftover);
    assert!(
        seen.activity().is_empty() && seen.usage().is_empty(),
        "A leftover task-notification result must not end a turn or publish usage before the real request even starts"
    );
    parser.parse(r#"{"type":"assistant","session_id":"claude-session","message":{"model":"claude-sonnet-test","usage":{"input_tokens":100,"output_tokens":20},"content":[{"type":"text","text":"pong"}]}}"#);
    assert_eq!(seen.logs(), [pair("assistant", "pong")], "Only the real turn's text may reach the transcript");
    let last = seen.last_usage();
    assert!(
        last.input_tokens == Some(100) && last.output_tokens == Some(20) && last.token_scope == "response",
        "The leftover result must not have latched resultSeen before the real turn's own usage arrived"
    );
    parser.parse(r#"{"type":"result","session_id":"claude-session","subtype":"success","is_error":false,"num_turns":1,"result":"pong","total_cost_usd":0.01}"#);
    assert_eq!(
        seen.activity().iter().filter(|a| a.kind == "turn").count(),
        1,
        "Exactly one turn may finish: the leftover notification's result does not count as one"
    );
    let last = seen.last_usage();
    assert!(
        last.input_tokens == Some(100) && last.cost_usd == Some(0.01) && last.token_scope == "run",
        "The real result must still finalize usage"
    );
    assert!(
        !stream::is_notification_result(&common::json(r#"{"type":"result","origin":{"kind":"user"}}"#))
            && !stream::is_notification_result(&common::json(r#"{"type":"assistant","origin":{"kind":"task-notification"}}"#)),
        "Only a result whose origin is a task notification or a peer message qualifies"
    );
}

/// Verification.cs "stream parser keeps text after thinking, errors latch and dedupe is bounded": the Claude half and Antigravity's
/// latched error (the Codex dedupe set is private and bounded the same way, by `seen`'s key hash).
#[test]
fn stream_parser_keeps_text_after_thinking() {
    common::korean();
    let seen = Seen::default();
    let parser = OutputParser::new("claude", "thinking", seen.hooks(false));
    parser.parse(r#"{"type":"assistant","message":{"id":"a","content":[{"type":"thinking","thinking":"private"}]}}"#);
    parser.parse(r#"{"type":"assistant","message":{"id":"a","content":[{"type":"text","text":"visible"}]}}"#);
    assert_eq!(seen.logs().into_iter().map(|(_, text)| text).collect::<Vec<_>>(), ["visible"]);
    // An Antigravity error stays the run's failure whatever status comes after it.
    let gemini = OutputParser::new("gemini", "latched", Seen::default().hooks(false));
    gemini.parse(r#"{"event":"result","result":{"status":"ERROR"}}"#);
    gemini.parse(r#"{"event":"result","result":{"status":"WAITING"}}"#);
    assert!(gemini.failed(), "an Antigravity error latches");
}

/// ComposerParityVerification.StreamEventsReachTheTranscript (M/MightyCore/CLIStreamParser.swift:383-384, 432-437). The Codex
/// collaboration rows (collab_tool_call) are not ported yet.
#[tokio::test]
async fn stream_events_reach_the_transcript() {
    common::korean();
    let seen = Seen::default();
    let claude = OutputParser::new("claude", "claude-run", seen.hooks(true));
    let metadata = common::json(r#"{"trigger":"auto","pre_tokens":84120,"post_tokens":21300,"duration_ms":12300}"#);
    claude.parse(r#"{"type":"system","subtype":"compact_boundary","session_id":"claude-one","compact_metadata":{"trigger":"auto","pre_tokens":84120,"post_tokens":21300,"duration_ms":12300}}"#);
    let logs = seen.logs();
    assert_eq!(
        logs,
        [pair("system", &format!("{} · {}", compaction::title(), compaction::claude_summary(Some(&metadata))))],
        "Claude summarizing its context is one transcript line with the figures"
    );
    assert!(logs[0].1.contains("84,120") && logs[0].1.contains("21,300"), "the line carries the token counts: {}", logs[0].1);
    claude.parse(r#"{"type":"system","subtype":"compact_boundary","parent_tool_use_id":"child","compact_metadata":{"trigger":"auto"}}"#);
    assert_eq!(seen.logs().len(), 1, "a sub-agent's own compaction stays out of the request's transcript");

    claude.parse(r#"{"type":"rate_limit_event","session_id":"claude-one","rate_limit_info":{"status":"allowed_warning","rateLimitType":"seven_day","utilization":0.86,"resetsAt":1789668000,"unifiedWindows":{"five_hour":{"utilization":0.22,"resetsAt":1789641600},"seven_day":{"utilization":0.86,"resetsAt":1789668000}}}}"#);
    let last = seen.last_usage();
    let limits = last.rate_limits.clone().unwrap_or_default();
    assert!(
        limits.iter().map(|l| l.kind.as_str()).collect::<Vec<_>>() == ["five_hour", "seven_day"]
            && limits.iter().map(|l| l.percent_used.unwrap_or(-1.0).round() as i64).collect::<Vec<_>>() == [22, 86]
            && limits[0].resets_at.as_deref().is_some_and(|r| r.starts_with("2026-09-"))
            && last.rate_limits_updated_at.is_some(),
        "a rate_limit_event fills the usage windows, in percent, with their reset times"
    );
    claude.parse(r#"{"type":"rate_limit_event","session_id":"claude-one","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.5,"unifiedWindows":{"x":{"utilization":7}}}}"#);
    let limits = seen.last_usage().rate_limits.unwrap_or_default();
    assert!(
        limits.len() == 1 && limits[0].kind == "five_hour" && limits[0].percent_used == Some(50.0) && limits[0].resets_at.is_none(),
        "a malformed window is dropped and the event's own window still counts"
    );
    let readings = seen.usage().len();
    claude.parse(
        r#"{"type":"rate_limit_event","session_id":"claude-one","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":true}}}}"#,
    );
    claude.parse(r#"{"type":"rate_limit_event","session_id":"claude-one","parent_tool_use_id":"child","rate_limit_info":{"rateLimitType":"weekly","utilization":0.9}}"#);
    assert_eq!(seen.usage().len(), readings, "an event with no usable window, or a sub-agent's, changes nothing");

    let codex_seen = Seen::default();
    let codex = OutputParser::new("codex", "codex-run", codex_seen.hooks(false));
    codex.parse(r#"{"type":"item.started","item":{"id":"compact-1","type":"context_compaction"}}"#);
    assert!(codex_seen.logs().is_empty(), "only a finished compaction is said");
    codex.parse(r#"{"type":"item.completed","item":{"id":"compact-1","type":"context_compaction"}}"#);
    assert_eq!(
        codex_seen.logs(),
        [pair("system", &format!("{} · {}", compaction::title(), compaction::codex_summary()))],
        "Codex summarizing its context is one transcript line"
    );
    for language in mighty_core::locale::LANGUAGES {
        let locale = mighty_core::locale::Locale::new(language);
        for key in
            ["run.agent.spawn", "run.agent.sendInput", "run.agent.wait", "run.agent.close", "run.notice.lineTooLong", "bedrock.scpDenied"]
        {
            let text = locale.get(key);
            assert!(!text.is_empty() && text != key, "{language}: the rows' words and the notices exist ({key})");
        }
    }

    // A line longer than the limit is skipped up to its line break and reported once (M/MightyCore/AgentOutputLines.swift:3-48).
    assert!(
        maximum_line_bytes("claude") == 64 * 1_048_576
            && maximum_line_bytes("codex") == 64 * 1_048_576
            && maximum_line_bytes("gemini") == 8 * 1_048_576,
        "an agent's line may be 64 MiB, Gemini's 8 MiB"
    );
    let text = format!("short\n{}\nafter\n{}\nlast", "a".repeat(40), "b".repeat(99));
    let mut lines = Lines::new(Some(Box::new(std::io::Cursor::new(text.into_bytes()))), 16);
    let (mut read, mut skipped) = (Vec::new(), 0);
    loop {
        match lines.next().await {
            Next::Line(line) => read.push(line),
            Next::TooLong => skipped += 1,
            Next::End => break,
        }
    }
    assert!(
        read == ["short", "after", "last"] && skipped == 2,
        "each over-long line is skipped and reported once; read {read:?}, reported {skipped}"
    );
}

const STAMP: &str = "2026-10-06T01:00:00.000Z";

fn entry(id: &str, kind: &str, text: &str, provider: Option<&str>) -> LogEntry {
    LogEntry::new(id.to_string(), kind, text.to_string(), STAMP.to_string(), provider.map(str::to_string))
}

/// ComposerParityVerification.RunLogIsCopiedBehindItsRoles (M/SessionPaneView.swift:322-325, 962-964).
#[test]
fn run_log_is_copied_behind_its_roles() {
    common::korean();
    let pane = RunSession {
        id: Some("pane".into()),
        workspace_id: Some("w".into()),
        provider: Some("codex".into()),
        logs: Some(vec![
            entry("1", "user", "hello", None),
            entry("2", "assistant", "hi", Some("claude")),
            entry("3", "assistant", "hey\nthere", None),
            entry("4", "output", "out", None),
            entry("5", "error", "bad", None),
            entry("6", "system", "note", None),
            entry("7", "image", "picture", None),
        ]),
        ..RunSession::default()
    };
    let system = strings::get("pane.log.roleSystem");
    let expected = [
        format!("[{}] hello", strings::get("pane.log.roleUser")),
        "[Claude] hi".into(),
        "[Codex] hey\nthere".into(),
        format!("[{}] out", strings::get("composer.sessionInfo.output")),
        format!("[{}] bad", strings::get("session.state.error")),
        format!("[{system}] note"),
        format!("[{system}] picture"),
    ]
    .join("\n\n");
    assert_eq!(run_log::copy(&pane), expected, "each entry is copied behind its role, a blank line between entries");
    assert_eq!(run_log::copy(&RunSession { logs: Some(vec![]), ..pane.clone() }), "", "an empty log copies nothing");
    assert!(
        run_log::role(&entry("8", "assistant", "x", Some("gemini")), "claude") == "Antigravity"
            && run_log::role(&entry("9", "assistant", "x", None), "claude") == "Claude",
        "an answer is its own agent's, or the pane's when the entry names none"
    );
}

// ── ClaudeResumeVerification ────────────────────────────────────────────────────────────────────────────────────────────

fn log(kind: &str, text: &str) -> LogEntry {
    LogEntry::new(mighty_core::wire::new_id(), kind, text.to_string(), "2026-10-23T00:00:00Z".to_string(), None)
}

fn ended(session: &RunSession, status: &str) -> RunSession {
    mighty_run::apply::apply_session(session, &RunEvent::state("pane", status, None), mighty_core::time::DateTimeOffset::now())
}

/// A Claude pane holding the id of a conversation Claude Code never wrote recovers after one failed run.
#[test]
fn a_pane_forgets_a_conversation_claude_code_does_not_have() {
    common::korean();
    const ID: &str = "8cc9f04d-1f6e-487a-a773-41586e4f7f12";
    let stuck = RunSession {
        id: Some("pane".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some("running".into()),
        resume_id: Some(ID.into()),
        logs: Some(vec![
            log("user", "first"),
            log("output", "Error: Expected message role 'user', got 'undefined'"),
            log("user", "again"),
            log("output", &format!("{MISSING_CONVERSATION}{ID}")),
            log("error", &format!("{MISSING_CONVERSATION}{ID}")),
        ]),
        ..RunSession::default()
    };
    let after = ended(&stuck, "error");
    assert!(after.resume_id.is_none(), "a run refused for the pane's own missing conversation clears the resume id");
    assert!(
        after.status.as_deref() == Some("error") && after.logs.as_ref().unwrap().len() == stuck.logs.as_ref().unwrap().len(),
        "the failed run still shows as failed with its logs"
    );
    let other = RunSession { resume_id: Some("11111111-2222-3333-4444-555555555555".into()), ..stuck.clone() };
    assert_eq!(ended(&other, "error").resume_id, other.resume_id, "a refusal naming another id keeps the pane's own");
    let mut later = stuck.logs.clone().unwrap();
    later.extend([log("user", "new request"), log("error", "rate limited")]);
    let earlier = RunSession { logs: Some(later), ..stuck.clone() };
    assert_eq!(ended(&earlier, "error").resume_id.as_deref(), Some(ID), "only the last run's logs count");
    assert_eq!(ended(&stuck, "stopped").resume_id.as_deref(), Some(ID), "a stopped run keeps the conversation");
    let codex = RunSession { provider: Some("codex".into()), ..stuck.clone() };
    assert_eq!(ended(&codex, "error").resume_id.as_deref(), Some(ID), "only Claude Code's refusal is read");
}

// ── ResumedAgentVerification: the recording read as RunManager reads a Claude run's frames ───────────────────────────────

struct Replay {
    background: BackgroundTaskTracker,
    closes: Vec<bool>,
    lines: Vec<String>,
    next: usize,
    turn_result_seen: bool,
    session_state: Option<String>,
}

impl Replay {
    fn new() -> Replay {
        let fixed = || {
            use chrono::TimeZone;
            mighty_core::time::DateTimeOffset::from_utc_ticks(mighty_core::time::ticks_of(
                chrono::Utc.timestamp_opt(1_800_000_000, 0).unwrap(),
            ))
        };
        Replay {
            background: BackgroundTaskTracker::new(Some(Box::new(fixed))),
            closes: vec![],
            lines: common::recording("claude-stream-resumed-agent"),
            next: 0,
            turn_result_seen: false,
            session_state: None,
        }
    }

    fn feed(&mut self, through: usize) {
        while self.next <= through {
            let line = self.lines[self.next].clone();
            self.next += 1;
            let frame = common::json(&line);
            if let Some(state) = stream::session_state(&frame) {
                self.session_state = Some(state);
            }
            self.background.consume(&frame);
            if meta::text(&frame, "type") == Some("result")
                && !stream::is_notification_result(&frame)
                && meta::absent_or_null(&frame, "parent_tool_use_id")
            {
                self.background.turn_ended();
            }
            if !stream::is_settle_point(&line) {
                continue;
            }
            if stream::is_turn_result(&line) {
                self.turn_result_seen = true;
            }
            let running = !work::running(&self.background.work()).is_empty();
            self.closes.push(stream::closes_input(self.turn_result_seen, running, self.session_state.as_deref()));
        }
    }

    fn busy(&self) -> bool {
        self.session_state.as_deref().is_some_and(|s| s != "idle")
    }
}

#[test]
fn the_recording_is_the_resume_as_claude_code_reports_it() {
    let frames: Vec<Value> = common::recording("claude-stream-resumed-agent").iter().map(|l| common::json(l)).collect();
    assert_eq!(frames.len(), 53, "the recording has its 53 frames");
    let listed = &frames[23]["tasks"][0];
    assert!(
        meta::text(&frames[23], "subtype") == Some("background_tasks_changed")
            && listed["task_id"] == "agent1"
            && listed["run_id"] == "taskrun-2",
        "the resumed run is listed under the first task id and a run of its own"
    );
    let start = &frames[24];
    assert!(
        start["subtype"] == "task_started"
            && start["task_id"] == "agent1"
            && start["run_id"] == "taskrun-2"
            && start["tool_use_id"] == "toolu_3"
            && start["description"] == "Count one to three",
        "the resumed run starts naming the SendMessage call, with the first description"
    );
    let end = &frames[30];
    assert!(
        end["subtype"] == "task_notification" && end["run_id"] == "taskrun-2" && end["tool_use_id"] == "toolu_3",
        "its own notification ends it"
    );
    assert_eq!(frames[34]["origin"]["runId"], "taskrun-2", "its notice turn names the run");
    let idle: Vec<usize> =
        frames.iter().enumerate().filter(|(_, f)| stream::session_state(f).as_deref() == Some("idle")).map(|(i, _)| i).collect();
    assert_eq!(idle, [35, 52], "idle comes only after the resumed agent's notice turn");
}

/// The user's case: a notice turn resumes the agent its own notice was about, and Claude ends that turn while the agent works. The
/// strip, queue, pet and graph checks of the C# test read mighty-graph's tracker and the UI's policies, ported with them.
#[test]
fn an_agent_resumed_from_a_notice_turn_keeps_the_pane_waiting_until_its_notification() {
    common::korean();
    let mut replay = Replay::new();
    // The request's own turn: the agent ended before its result and its notice turn is still to come, so the CLI is busy and the
    // input stays open (closed, Claude Code would stop the resumed agent 10 minutes on).
    replay.feed(17);
    let work17 = replay.background.work();
    assert!(
        work17.turn_ended
            && !work::waiting_on_background(&work17)
            && work::tasks(&work17).len() == 1
            && work::tasks(&work17)[0].status.as_deref() == Some("completed"),
        "the request's turn left nothing running"
    );
    assert!(replay.closes == [false] && replay.busy(), "the request's result leaves the input open while the CLI is busy");
    // The notice turn resumes it: listed again under a new run, then started by the SendMessage call.
    replay.feed(24);
    let work24 = replay.background.work();
    let task = &work::tasks(&work24)[0];
    assert!(
        work::tasks(&work24).len() == 1
            && task.id.as_deref() == Some("agent1")
            && task.status.as_deref() == Some("running")
            && task.tool_use_id.as_deref() == Some("toolu_3")
            && task.ended_at.is_none()
            && task.summary.is_none()
            && task.description.as_deref() == Some("Count one to three")
            && task.kind.as_deref() == Some("agent"),
        "the resumed run runs again: {task:?}"
    );
    // The notice turn's result: the agent still runs, so the input stays open and the pane waits on it.
    replay.feed(27);
    let work27 = replay.background.work();
    assert!(replay.closes == [false, false] && work::running(&work27).len() == 1, "the notice turn's result leaves the input open");
    assert!(work::waiting_on_background(&work27), "the pane waits on the resumed agent");
    // Its end: the run's own notification ends it.
    replay.feed(30);
    let work30 = replay.background.work();
    assert!(
        work::tasks(&work30)[0].status.as_deref() == Some("completed")
            && work::tasks(&work30)[0].ended_at.is_some()
            && !work::waiting_on_background(&work30),
        "its notification ends it"
    );
    // That notice turn's result, then idle: only then does the input close.
    replay.feed(35);
    assert_eq!(replay.closes, [false, false, false, true], "the input closes at idle");
}

/// The same agent resumed from an ordinary turn: 바로 실행's steer reopens the turn, so the pane waits on background work only once that
/// turn's own result came, until the agent's notification. (The graph tracker's steer block is mighty-graph's.)
#[test]
fn an_agent_resumed_from_a_steered_turn_waits_once_that_turn_ends() {
    common::korean();
    let mut replay = Replay::new();
    replay.feed(35);
    replay.background.reopen();
    replay.feed(40);
    let open = replay.background.work();
    assert!(
        !open.turn_ended
            && !work::waiting_on_background(&open)
            && work::tasks(&open).len() == 1
            && work::tasks(&open)[0].status.as_deref() == Some("running")
            && work::tasks(&open)[0].tool_use_id.as_deref() == Some("toolu_4"),
        "the steered turn is open while the agent runs again"
    );
    replay.feed(43);
    let waiting = replay.background.work();
    assert!(
        waiting.turn_ended && work::waiting_on_background(&waiting) && !*replay.closes.last().unwrap(),
        "after that turn's result the pane waits on the agent"
    );
    replay.feed(47);
    let done = replay.background.work();
    assert!(
        !work::waiting_on_background(&done) && work::tasks(&done)[0].status.as_deref() == Some("completed"),
        "its notification ends it"
    );
    replay.feed(52);
    assert!(*replay.closes.last().unwrap(), "idle closes the input");
}

// ── RuntimeParityVerification ───────────────────────────────────────────────────────────────────────────────────────────

#[test]
fn approval_state_wins_delayed_mods() {
    common::korean();
    for (literal, expected) in
        [("1.0", 1), ("1e0", 1), ("-0.0", 0), ("9007199254740991", 9_007_199_254_740_991), ("-9007199254740991", -9_007_199_254_740_991)]
    {
        assert_eq!(
            meta::safe_integer(&common::json(&format!("{{\"n\":{literal}}}")), "n"),
            Some(expected),
            "Mods accept safe integral JSON numbers: {literal}"
        );
    }
    for literal in ["null", "true", "\"1\"", "0.5", "1e309", "9007199254740992", "-9007199254740992"] {
        // serde_json refuses 1e309 outright (C# reads it and finds it not finite): either way no sequence.
        let read = serde_json::from_str::<Value>(&format!("{{\"n\":{literal}}}")).ok();
        assert!(
            read.is_none_or(|value| meta::safe_integer(&value, "n").is_none()),
            "Mods reject non-integral, non-finite and unsafe integers: {literal}"
        );
    }
    assert!(meta::integer(&common::json(r#"{"n":1.0}"#), "n").is_none(), "The existing integer metadata decoder retains its strict syntax");
    let seen = Seen::default();
    let parser = OutputParser::new("claude", "consent-run", seen.hooks(false));
    let request = mighty_run::permissions::ToolPermissionRequest::new("approval", "consent-run", "call", "Bash", "{}", "git status");
    const CALL: &str =
        r#"{"type":"assistant","message":{"content":[{"type":"tool_use","id":"call","name":"Bash","input":{"command":"git status"}}]}}"#;
    let last = || seen.activity().last().cloned().unwrap();
    parser.parse(CALL);
    parser.permission_activity(&request, "waiting");
    let waiting_count = seen.activity().len();
    parser.receive_mod(&common::json(r#"{"event":"tool.call","claudeSessionId":"root","toolUseId":"call","tool":"Bash","sequence":1.0}"#));
    parser.parse(CALL);
    assert!(
        seen.activity().len() == waiting_count && last().state == "waiting",
        "Late Mod and stdout starts cannot erase an outstanding UI approval"
    );
    parser.permission_activity(&request, "running");
    let allowed_count = seen.activity().len();
    parser
        .receive_mod(&common::json(r#"{"event":"tool.waiting","claudeSessionId":"root","toolUseId":"call","tool":"Bash","sequence":2e0}"#));
    assert!(
        seen.activity().len() == allowed_count && last().state == "running",
        "A delayed waiting Mod cannot undo the user's allow response"
    );
    parser.receive_mod(&common::json(
        r#"{"event":"tool.complete","claudeSessionId":"root","toolUseId":"call","tool":"Bash","output":"clean","sequence":3.0}"#,
    ));
    assert!(last().state == "completed" && last().output.as_deref() == Some("clean"), "The actual result still completes an allowed tool");
    let completed_count = seen.activity().len();
    parser.receive_mod(&common::json(
        r#"{"event":"tool.complete","claudeSessionId":"root","toolUseId":"call","tool":"Bash","output":"duplicate","sequence":3e0}"#,
    ));
    parser.receive_mod(&common::json(r#"{"event":"tool.complete","claudeSessionId":"root","toolUseId":"call","tool":"Bash","output":"older","sequence":2.0,"isError":true}"#));
    assert!(
        seen.activity().len() == completed_count && last().state == "completed" && last().output.as_deref() == Some("clean"),
        "Equivalent decimal/exponent sequences and older telemetry cannot replace a result"
    );
    let mut denied = request.clone();
    denied.id = "denied-approval".into();
    denied.tool_use_id = "denied".into();
    parser.permission_activity(&denied, "waiting");
    parser.permission_activity(&denied, "error");
    let denied_count = seen.activity().len();
    parser
        .receive_mod(&common::json(r#"{"event":"tool.waiting","claudeSessionId":"root","toolUseId":"denied","tool":"Bash","sequence":4}"#));
    parser.receive_mod(&common::json(
        r#"{"event":"tool.complete","claudeSessionId":"root","toolUseId":"denied","tool":"Bash","sequence":5}"#,
    ));
    assert!(seen.activity().len() == denied_count && last().state == "error", "Late telemetry cannot resurrect or accept a denied call");
    // Consent ownership has the same bounded lifetime as the activity it describes.
    for i in 0..512 {
        parser.receive_mod(&json!({"event": "tool.call", "claudeSessionId": "root", "toolUseId": format!("later-{i}"), "tool": "Read"}));
    }
    parser.receive_mod(&common::json(r#"{"event":"tool.waiting","claudeSessionId":"root","toolUseId":"call","tool":"Bash","sequence":6}"#));
    assert_eq!(last().state, "waiting", "Evicted activities do not retain stale permission ownership");
    parser.finish_activities(true);
    let finished_count = seen.activity().len();
    parser.permission_activity(&request, "running");
    assert_eq!(seen.activity().len(), finished_count, "A finalized parser ignores late consent callbacks");
}

#[test]
fn mods_never_replace_root_resume() {
    let resumes: List<String> = Arc::default();
    let sink = resumes.clone();
    let parser = OutputParser::new(
        "claude",
        "resume-run",
        ParserHooks { resume: Some(Arc::new(move |id| sink.lock().unwrap().push(id.to_string()))), ..ParserHooks::default() },
    );
    let resumed = || resumes.lock().unwrap().clone();
    parser.receive_mod(&common::json(r#"{"event":"session.start","claudeSessionId":"mod-before-root"}"#));
    assert!(resumed().is_empty(), "Mods do not choose a resumable CLI conversation");
    parser.parse(r#"{"type":"system","subtype":"init","session_id":"root-session"}"#);
    parser.receive_mod(&common::json(
        r#"{"event":"tool.call","claudeSessionId":"child-session","agentId":"child","toolUseId":"read","tool":"Read"}"#,
    ));
    parser.receive_mod(&common::json(r#"{"event":"session.start","claudeSessionId":"late-unscoped-session"}"#));
    parser.parse(r#"{"type":"assistant","session_id":"child-session","parent_tool_use_id":"spawn","message":{"content":[]}}"#);
    assert_eq!(resumed(), ["root-session"], "Child and unscoped Mods must not overwrite the saved root resume ID");
    parser.parse(r#"{"type":"system","subtype":"init","session_id":"next-root-session"}"#);
    assert_eq!(resumed(), ["root-session", "next-root-session"], "A new root CLI session can still update resume identity");
}
