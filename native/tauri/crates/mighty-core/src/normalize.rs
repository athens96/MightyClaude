//! C# `StateStore.Normalize` (MightyCode.Core/StateStore.cs:248) and every rule it calls, in the same order with the same budgets:
//! `ActivitySupport.Normalize`/`Clean` (AgentActivity.cs), `AgentImageSupport.Normalized` (AgentImages.cs),
//! `SessionUsageSupport.Normalize` (SessionUsage.cs), `MightyGraphSupport.Normalized` with the advisor, identity and figures rules
//! (MightyGraph.cs, ExecutionGraph.cs), `GraphBlockPreferences` (GraphBlockPreferences.cs), `ProviderCatalog.NormalizeSettings`
//! (ProviderCatalog.cs), `ClaudePlanMode.NormalizedHistory`, `TodoProgress.Normalized`, `BackgroundWork.Normalized`
//! (ClaudePlanMode.cs), `ShellBlockRunSupport.Normalized` (ShellBlockRuns.cs), `PaneTitle` (PaneTitle.cs), `RenameSupport.ClampTitle`
//! (RenameSupport.cs), `PaneLayout.Normalize` (`layout.rs`), `PhaseModelPreferences.Normalize`, `MobileRemoteSettings.Normalized`,
//! `ReferencePreview.BubbleSize`, `TranscriptRetention`, and the load's `AntigravityMigration`, and `FilePaneKind.Stored`.
//!
//! Where C# throws while it normalizes (a null where it reads a string: a graph run's input, an agent's title or input, an entry's
//! id or timestamp), this fails with the same outcome: the load fails.
//!
//! What C# keeps in a `ConditionalWeakTable` (a pane normalized before is taken as it is) is a cache of a result this computes
//! again: normalizing a normalized pane with at least the budget it was made with gives the same pane.

use std::collections::HashSet;
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

use indexmap::IndexMap;

use crate::graph::{
    self, FlowStep, GraphAdvisor, GraphAdvisorCall, GraphAgentFigures, GraphAgentIdentity, GraphBlockSize, MightyGraphAgent,
    MightyGraphRun, terminal,
};
use crate::json::{R, ReadError, escaped_len};
use crate::layout;
use crate::locale::{self, Locale};
use crate::model::{
    AgentActivity, AgentImageRef, AgentRunTiming, AppSnapshot, BackgroundTask, BackgroundWork, LogEntry, MOBILE_DIRECT_PORT,
    MobileRemoteSettings, PaneLayoutNode, PhaseModelsSnapshot, PlanRecord, RunSession, RunSettings, SessionUsage, ShellBlockRun, TodoItem,
    TodoProgress, Workspace,
};
use crate::time;
use crate::wire::{self, EFFORTS, PROVIDERS, is_identifier, is_model, new_id};

// ── paths ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// Which OS's `Path.IsPathFullyQualified` the rules follow: C#'s depends on the OS it runs on, so this does too, chosen once by
/// the host (by default the OS this was built for).
static PATH_STYLE: AtomicU8 = AtomicU8::new(0);

pub fn set_windows_paths(windows: bool) {
    PATH_STYLE.store(if windows { 1 } else { 2 }, Ordering::Relaxed);
}

fn windows_paths() -> bool {
    match PATH_STYLE.load(Ordering::Relaxed) {
        1 => true,
        2 => false,
        _ => cfg!(windows),
    }
}

/// C# `Path.IsPathFullyQualified`.
pub fn fully_qualified(path: &str) -> bool {
    if !windows_paths() {
        return path.starts_with('/');
    }
    let p: Vec<u16> = path.encode_utf16().take(3).collect();
    let separator = |c: u16| c == b'\\' as u16 || c == b'/' as u16;
    if p.len() < 2 {
        return false;
    }
    if separator(p[0]) {
        return p[1] == b'?' as u16 || separator(p[1]);
    }
    p.len() >= 3 && p[1] == b':' as u16 && separator(p[2]) && (p[0] < 128 && (p[0] as u8).is_ascii_alphabetic())
}

/// A host's folder: absolute on the host's own OS, whatever this OS calls absolute (C# `StateStore.RemotePath`).
pub fn remote_path(path: Option<&str>) -> bool {
    let Some(path) = path else { return false };
    let b = path.as_bytes();
    let length = wire::utf16_len(path);
    length > 0
        && length <= 4096
        && !path.contains('\0')
        && (path.starts_with('/')
            || path.starts_with("\\\\")
            || (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/')))
}

/// C# `StateStore.ValidWorkspace`.
pub fn valid_workspace(w: &Workspace) -> bool {
    let Some(path) = w.path.as_deref() else { return false };
    let length = wire::utf16_len(path);
    if !is_identifier(w.id.as_deref()) || length == 0 || length > 4096 || path.contains('\0') {
        return false;
    }
    match &w.remote {
        None => fully_qualified(path),
        Some(r) => {
            r.version == 2
                && is_identifier(r.connection_id.as_deref())
                && is_identifier(r.workspace_id.as_deref())
                && !wire::clean(r.host_name.as_deref().unwrap_or(""), 120).trim().is_empty()
                && remote_path(Some(path))
        }
    }
}

/// C# `DelegationPanes.IsWorkingFolder`.
pub fn is_working_folder(path: Option<&str>) -> bool {
    path.is_some_and(|p| wire::utf16_len(p) <= 4096 && !p.contains('\0') && fully_qualified(p))
}

// ── text ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `char.IsControl` / `UnicodeCategory.Control` (Cc).
fn is_control(c: char) -> bool {
    matches!(c as u32, 0x00..=0x1f | 0x7f..=0x9f)
}

/// The escape sequences `ActivitySupport.Clean` removes: `\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))`.
fn strip_escapes(value: &str) -> std::borrow::Cow<'_, str> {
    if !value.contains('\u{1b}') {
        return std::borrow::Cow::Borrowed(value);
    }
    let b = value.as_bytes();
    let mut out = String::with_capacity(value.len());
    let mut at = 0;
    let mut kept = 0;
    while at < b.len() {
        if b[at] != 0x1b {
            at += 1;
            continue;
        }
        let end = match b.get(at + 1) {
            Some(b'[') => {
                let mut i = at + 2;
                while i < b.len() && (b'0'..=b'?').contains(&b[i]) {
                    i += 1;
                }
                while i < b.len() && (b' '..=b'/').contains(&b[i]) {
                    i += 1;
                }
                (i < b.len() && (b'@'..=b'~').contains(&b[i])).then_some(i + 1)
            }
            Some(b']') => {
                let rest = &b[at + 2..];
                match rest.iter().position(|c| *c == 0x07) {
                    Some(bell) => Some(at + 2 + bell + 1),
                    None => rest.windows(2).rposition(|w| w == b"\x1b\\").map(|p| at + 2 + p + 2),
                }
            }
            _ => None,
        };
        match end {
            Some(end) => {
                out.push_str(&value[kept..at]);
                kept = end;
                at = end;
            }
            None => at += 1,
        }
    }
    out.push_str(&value[kept..]);
    std::borrow::Cow::Owned(out)
}

/// C# `ActivitySupport.Clean`: escape sequences and control characters (but tab, LF, CR) removed, white space made a space on one
/// line, the first `maximum_bytes` of UTF-8, trimmed.
pub fn activity_clean(value: Option<&str>, maximum_bytes: usize, single_line: bool) -> String {
    let plain = strip_escapes(value.unwrap_or(""));
    let mut out = String::new();
    let mut bytes = 0;
    for c in plain.chars() {
        if is_control(c) && !matches!(c, '\t' | '\n' | '\r') {
            continue;
        }
        let item = if single_line && c.is_whitespace() { ' ' } else { c };
        if bytes + item.len_utf8() > maximum_bytes {
            break;
        }
        out.push(item);
        bytes += item.len_utf8();
    }
    out.trim().to_string()
}

/// C# `ActivitySupport.PrefixUtf8`: the characters that fit `maximum_bytes` of UTF-8.
pub fn prefix_utf8(value: &str, maximum_bytes: usize) -> String {
    let mut end = 0;
    for (at, c) in value.char_indices() {
        if at + c.len_utf8() > maximum_bytes {
            break;
        }
        end = at + c.len_utf8();
    }
    value[..end].to_string()
}

/// C# `RenameSupport.ClampTitle`: control characters removed, at most 120 text elements.
pub fn clamp_title(value: Option<&str>) -> String {
    let clean = wire::clean_default(value);
    if wire::utf16_len(&clean) <= 120 {
        return clean;
    }
    wire::text_elements(&clean, 120).0.to_string()
}

const ATTACHMENT_MARKER: &str = "\u{CCA8}\u{BD80}: ";
const ASK_SKILL: &str = "oh-my-claudecode:ask";
const ASK_TARGETS: [&str; 5] = ["codex", "antigravity", "gemini", "grok", "cursor"];

/// C# `PaneTitle.Shortened`: white space collapsed, at most 40 text elements and "…".
pub fn shortened(raw: &str) -> Option<String> {
    let collapsed = raw.split(char::is_whitespace).filter(|p| !p.is_empty()).collect::<Vec<_>>().join(" ");
    if collapsed.is_empty() {
        return None;
    }
    let (head, cut) = wire::text_elements(&collapsed, 40);
    Some(if cut { format!("{head}…") } else { collapsed })
}

/// C# `PaneTitle.FollowsRequests`.
pub fn follows_requests(s: &RunSession) -> bool {
    s.kind() == "claude" && matches!(s.title_mode.as_deref(), None | Some("auto"))
}

/// C# `PaneTitle.Tooltip`: the latest request's words, without its attachment list.
pub fn tooltip(s: &RunSession) -> Option<String> {
    s.logs().iter().rev().filter(|e| e.kind() == "user").find_map(|e| {
        let text = e.text();
        let text =
            if text.starts_with(ATTACHMENT_MARKER) { "" } else { text.split(&format!("\n\n{ATTACHMENT_MARKER}")).next().unwrap_or("") };
        let trimmed = text.trim();
        (!trimmed.is_empty()).then(|| trimmed.to_string())
    })
}

/// C# `AskTargets.Question`: a request asked of another CLI, without its `/oh-my-claudecode:ask <target> ` prefix.
pub fn ask_question(text: &str) -> &str {
    let trimmed = text.trim();
    for target in ASK_TARGETS {
        if let Some(rest) = trimmed.strip_prefix(&format!("/{ASK_SKILL} {target} ")) {
            return rest;
        }
    }
    text
}

/// C# `ProviderCatalog.Name`.
pub fn provider_name(provider: &str) -> &'static str {
    match provider {
        "claude" => "Claude Code",
        "codex" => "Codex CLI",
        "pi" => "pi",
        "opencode" => "opencode CLI",
        "hermes" => "Hermes CLI",
        _ => "Antigravity CLI",
    }
}

/// C# `PaneTitle.AutoTitle`.
pub fn auto_title(s: &RunSession, default_title: &str) -> String {
    match tooltip(s) {
        Some(text) => shortened(ask_question(&text)).unwrap_or_else(|| default_title.to_string()),
        None => default_title.to_string(),
    }
}

/// C# `PaneTitle.Restored`: a pane whose title follows its requests is titled by its latest one again.
pub fn restored_title(s: RunSession) -> RunSession {
    if !follows_requests(&s) {
        return s;
    }
    let fallback =
        if s.resume_id.is_some() && !s.title().is_empty() { s.title().to_string() } else { provider_name(s.provider()).to_string() };
    let title = auto_title(&s, &fallback);
    RunSession { title: Some(title), ..s }
}

/// C# `PaneTitle.NormalizedMode`.
pub fn title_mode(mode: Option<&str>) -> Option<String> {
    mode.filter(|m| matches!(*m, "auto" | "fixed")).map(str::to_string)
}

// ── activity, pictures, usage ───────────────────────────────────────────────────────────────────────────────────────────

const ACTIVITY_KINDS: [&str; 8] = ["turn", "tool", "command", "read", "edit", "search", "web", "agent"];
const ACTIVITY_STATES: [&str; 5] = ["running", "waiting", "completed", "error", "stopped"];
/// `ActivitySupport.MaximumMessageBytes`.
pub const MAXIMUM_MESSAGE_BYTES: usize = 131_072;
const MAXIMUM_DURATION_MS: f64 = 30.0 * 24.0 * 60.0 * 60.0 * 1000.0;

/// C# `ActivitySupport.Normalize`.
pub fn activity(value: &AgentActivity, restoring: bool) -> Option<AgentActivity> {
    if !wire::identifier(&value.id)
        || !PROVIDERS.contains(&value.provider.as_str())
        || !ACTIVITY_KINDS.contains(&value.kind.as_str())
        || !ACTIVITY_STATES.contains(&value.state.as_str())
    {
        return None;
    }
    let optional = |text: Option<&str>, bytes: usize, line: bool| Some(activity_clean(text, bytes, line)).filter(|c| !c.is_empty());
    Some(AgentActivity {
        id: value.id.clone(),
        provider: value.provider.clone(),
        kind: value.kind.clone(),
        summary: activity_clean(Some(&value.summary), 1000, true),
        tool_name: optional(value.tool_name.as_deref(), 160, true),
        output: optional(value.output.as_deref(), 8192, false),
        duration_ms: value
            .duration_ms
            .filter(|d| d.is_finite() && (0.0..=MAXIMUM_DURATION_MS).contains(d) && value.kind != "turn" && terminal(&value.state)),
        state: if restoring && matches!(value.state.as_str(), "running" | "waiting") { "stopped".into() } else { value.state.clone() },
    })
}

const IMAGE_TYPES: [&str; 9] =
    ["image/png", "image/jpeg", "image/gif", "image/webp", "image/heic", "image/heif", "image/tiff", "image/bmp", "image/svg+xml"];

/// C# `AgentImageSupport.MediaType`.
pub fn media_type(value: Option<&str>) -> Option<&'static str> {
    let lower: String = value.unwrap_or("").trim().chars().flat_map(char::to_lowercase).collect();
    let canonical = match lower.as_str() {
        "image/jpg" | "image/pjpeg" => "image/jpeg",
        "image/x-png" => "image/png",
        "image/svg" => "image/svg+xml",
        other => other,
    };
    IMAGE_TYPES.iter().copied().find(|t| *t == canonical)
}

/// C# `AgentImageSupport.Normalized(AgentImageRef)`.
pub fn image(value: &AgentImageRef) -> Option<AgentImageRef> {
    let hash = value.hash.as_deref()?;
    if hash.len() != 64 || !hash.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c)) {
        return None;
    }
    let kind = media_type(value.media_type.as_deref())?;
    if !(0..=1_000_000).contains(&value.width) || !(0..=1_000_000).contains(&value.height) || !(1..=20 * 1_048_576).contains(&value.bytes) {
        return None;
    }
    let path = value.path.clone().filter(|p| (p.starts_with('/') || fully_qualified(p)) && p.len() <= 4096 && !p.contains('\0'));
    Some(AgentImageRef {
        media_type: Some(kind.into()),
        source: Some(activity_clean(value.source.as_deref(), 240, true)),
        path,
        ..value.clone()
    })
}

/// C# `AgentImageSupport.Normalized(IEnumerable<AgentImageRef?>)`: the first 16, the valid ones, null when none is left.
pub fn images(values: Option<&Vec<Option<AgentImageRef>>>) -> Option<Vec<Option<AgentImageRef>>> {
    let kept: Vec<Option<AgentImageRef>> = values?.iter().take(16).filter_map(|v| v.as_ref().and_then(image)).map(Some).collect();
    (!kept.is_empty()).then_some(kept)
}

/// C# `AgentImageSupport.ApproximateBytes`.
pub fn approximate_bytes(refs: Option<&Vec<Option<AgentImageRef>>>) -> i64 {
    refs.map(|list| {
        list.iter().flatten().map(|r| 160 + bytes(r.media_type.as_deref()) + bytes(r.source.as_deref()) + bytes(r.path.as_deref())).sum()
    })
    .unwrap_or(0)
}

fn bytes(value: Option<&str>) -> i64 {
    value.map_or(0, |v| v.len() as i64)
}

const MAXIMUM_USAGE_TOKENS: i64 = 9_000_000_000_000;

fn usage_count(value: Option<i64>) -> Option<i64> {
    value.filter(|v| (0..=MAXIMUM_USAGE_TOKENS).contains(v))
}

fn usage_scope(value: Option<&str>) -> bool {
    matches!(value, Some("response" | "run" | "session"))
}

/// C# `SessionUsageSupport.Normalize`.
pub fn session_usage(value: &SessionUsage) -> Option<SessionUsage> {
    if !PROVIDERS.contains(&value.provider.as_str())
        || !usage_scope(Some(&value.token_scope))
        || value.source.is_empty()
        || value.source != activity_clean(Some(&value.source), 80, true)
        || time::parse(Some(&value.updated_at)).is_none()
    {
        return None;
    }
    let input = usage_count(value.input_tokens);
    let output = usage_count(value.output_tokens);
    let cost = value.cost_usd.filter(|c| c.is_finite() && (0.0..=1_000_000_000.0).contains(c));
    let limits = value.rate_limits.as_ref().map(|list| {
        let mut seen = HashSet::new();
        list.iter()
            .filter(|v| !v.kind.is_empty() && v.kind == activity_clean(Some(&v.kind), 80, true))
            .take(16)
            .filter(|v| seen.insert(v.kind.clone()))
            .map(|v| crate::model::SessionRateLimit {
                kind: v.kind.clone(),
                percent_used: v
                    .percent_used
                    .filter(|p| p.is_finite() && *p >= 0.0 && *p <= if v.kind == "spend_limit" { 1_000_000.0 } else { 100.0 }),
                resets_at: v.resets_at.clone().filter(|r| time::parse(Some(r)).is_some()),
            })
            .filter(|v| v.percent_used.is_some() || v.resets_at.is_some())
            .collect()
    });
    Some(SessionUsage {
        model: value.model.clone().filter(|m| wire::model(m)),
        selected_model: value.selected_model.clone().filter(|m| wire::model(m)),
        provider_session_id: value.provider_session_id.clone().filter(|m| wire::identifier(m)),
        input_tokens: input,
        output_tokens: output,
        cache_read_tokens: usage_count(value.cache_read_tokens).filter(|r| input.is_none_or(|i| *r <= i)),
        cache_write_tokens: usage_count(value.cache_write_tokens).filter(|w| input.is_none_or(|i| *w <= i)),
        reasoning_tokens: usage_count(value.reasoning_tokens).filter(|r| output.is_none_or(|o| *r <= o)),
        total_tokens: usage_count(value.total_tokens),
        context_used_tokens: usage_count(value.context_used_tokens),
        context_window_tokens: value.context_window_tokens.filter(|w| (1..=1_000_000_000).contains(w)),
        cost_usd: cost,
        cost_scope: value.cost_scope.clone().filter(|s| cost.is_some() && usage_scope(Some(s))),
        rate_limits: limits,
        rate_limits_updated_at: value.rate_limits_updated_at.clone().filter(|r| time::parse(Some(r)).is_some()),
        ..value.clone()
    })
}

// ── graph runs (MightyGraphSupport.Normalized) ──────────────────────────────────────────────────────────────────────────

/// `MightyGraphSupport.LiveHistoryLimit`.
pub const LIVE_HISTORY_LIMIT: i64 = 2 * 1024 * 1024;
const VALID_STATES: [&str; 6] = ["idle", "running", "waiting", "completed", "error", "stopped"];
const VALID_KINDS: [&str; 4] = ["task", "steer", "compact", "question"];
const ADVISOR_STATES: [&str; 5] = ["consulting", "done", "declined", "error", "stopped"];
const ISOLATIONS: [&str; 2] = ["worktree", "remote"];
const NEWLINES: [char; 7] = ['\n', '\r', '\u{b}', '\u{c}', '\u{85}', '\u{2028}', '\u{2029}'];

fn null(what: &str) -> ReadError {
    ReadError::crash(format!("a null {what} (C# throws while it normalizes)"))
}

/// `MightyGraphSupport.ValidEffort`.
fn valid_effort(value: &str) -> bool {
    value == "default" || EFFORTS.contains(&value) || wire::PI_THINKING_LEVELS.contains(&value)
}

/// The `Bounded` of `MightyGraphSupport.Normalized`, charged what it kept: the first `max` UTF-8 bytes, then the trailing
/// continuation bytes taken off, which leaves the last character's lead byte, decoded as U+FFFD (C#'s own rule, kept as it is:
/// any cut text that ends in a character beyond ASCII ends in U+FFFD).
fn bounded(text: Option<&str>, max: i64, budget: &mut i64, what: &str) -> R<String> {
    let text = text.ok_or_else(|| null(what))?;
    let max = max.max(0) as usize;
    if text.len() <= max {
        *budget -= text.len() as i64;
        return Ok(text.to_string());
    }
    let mut kept = &text.as_bytes()[..max];
    while let Some(&last) = kept.last() {
        if last & 0x80 != 0 && last & 0xC0 != 0xC0 {
            kept = &kept[..kept.len() - 1];
        } else {
            break;
        }
    }
    *budget -= kept.len() as i64;
    Ok(String::from_utf8_lossy(kept).into_owned())
}

/// `GraphTime.Kept`.
fn graph_time(value: Option<&str>) -> Option<String> {
    value.filter(|v| v.len() <= 80 && time::parse(Some(v)).is_some()).map(str::to_string)
}

/// `GraphAgentIdentity.Normalized`.
fn identity(value: &GraphAgentIdentity) -> Option<GraphAgentIdentity> {
    let kind = value.kind.as_deref().map(|t| activity_clean(Some(t), 160, true)).filter(|c| !c.is_empty());
    let model = |m: &Option<String>| m.clone().filter(|m| wire::model(m));
    let result = GraphAgentIdentity {
        kind,
        requested_model: model(&value.requested_model),
        resolved_model: model(&value.resolved_model),
        isolation: value.isolation.clone().filter(|i| ISOLATIONS.contains(&i.as_str())),
    };
    (result != GraphAgentIdentity::default()).then_some(result)
}

fn identity_bytes(value: &GraphAgentIdentity) -> i64 {
    bytes(value.kind.as_deref())
        + bytes(value.requested_model.as_deref())
        + bytes(value.resolved_model.as_deref())
        + bytes(value.isolation.as_deref())
}

/// `GraphAgentFigures.Normalized`.
fn figures(value: &GraphAgentFigures) -> Option<GraphAgentFigures> {
    let result = GraphAgentFigures {
        started_at: graph_time(value.started_at.as_deref()),
        ended_at: graph_time(value.ended_at.as_deref()),
        tool_calls: value.tool_calls.filter(|n| (0..=1_000_000).contains(n)),
        exit_code: value.exit_code,
    };
    (result != GraphAgentFigures::default()).then_some(result)
}

fn advisor_call_bytes(c: &GraphAdvisorCall) -> i64 {
    [&c.id, &c.caller, &c.started_at, &c.ended_at, &c.state, &c.text, &c.error_code].iter().map(|v| bytes(v.as_deref())).sum()
}

fn advisor_bytes(a: &GraphAdvisor) -> i64 {
    bytes(a.model.as_deref()) + a.calls.iter().flatten().flatten().map(advisor_call_bytes).sum::<i64>()
}

/// `GraphAdvisor.Normalized(restoring)`.
fn advisor(value: &GraphAdvisor, restoring: bool) -> Option<GraphAdvisor> {
    let calls: &[Option<GraphAdvisorCall>] = value.calls.as_deref().unwrap_or(&[]);
    let mut seen = HashSet::new();
    let mut kept = vec![];
    for call in calls.iter().skip(calls.len().saturating_sub(32)) {
        let Some(call) = call else { continue };
        if !is_identifier(call.id.as_deref())
            || !call.state.as_deref().is_some_and(|s| ADVISOR_STATES.contains(&s))
            || call.caller.as_deref().is_some_and(|c| !wire::identifier(c))
            || !seen.insert(call.id.clone().unwrap_or_default())
        {
            continue;
        }
        let optional =
            |text: Option<&str>, bytes: usize, line: bool| text.map(|t| activity_clean(Some(t), bytes, line)).filter(|c| !c.is_empty());
        kept.push(Some(GraphAdvisorCall {
            started_at: graph_time(call.started_at.as_deref()),
            ended_at: graph_time(call.ended_at.as_deref()),
            state: Some(if restoring && call.state.as_deref() == Some("consulting") {
                "stopped".into()
            } else {
                call.state.clone().unwrap_or_default()
            }),
            redacted: (call.redacted == Some(true)).then_some(true),
            text: optional(call.text.as_deref(), 1024, false),
            error_code: optional(call.error_code.as_deref(), 80, true),
            before_change: (call.before_change == Some(true)).then_some(true),
            followed: (call.followed == Some(true)).then_some(true),
            ..call.clone()
        }));
    }
    let count = (value.total.unwrap_or(0) as i64).max(calls.len() as i64);
    let all = count.min(1_000_000);
    let result = GraphAdvisor {
        model: value.model.clone().filter(|m| wire::model(m)),
        usage: value.usage.and_then(|u| u.normalized()),
        total: (all > kept.len() as i64).then_some(all as i32),
        calls: Some(kept),
    };
    let empty = result.model.is_none() && result.usage.is_none() && result.calls.as_ref().is_none_or(Vec::is_empty);
    (!empty).then_some(result)
}

/// `MightyGraphSupport.IsHeartbeatBlock`.
fn is_heartbeat_block(agent: &MightyGraphAgent) -> R<bool> {
    if agent.kind.is_some() {
        return Ok(false);
    }
    let input = agent.input.as_deref().ok_or_else(|| null("graph agent input"))?;
    if !input.is_empty()
        || agent.identity.is_some()
        || agent.usage.is_some()
        || agent.response_records.is_some()
        || agent.background == Some(true)
        || agent.source_block_id.is_some()
    {
        return Ok(false);
    }
    let entries = agent.entries.as_deref().ok_or_else(|| null("graph agent entries"))?;
    if entries.is_empty() {
        return Ok(false);
    }
    for e in entries {
        if e.kind() != "assistant" {
            return Ok(false);
        }
        if !e.id.as_deref().ok_or_else(|| null("graph entry id"))?.ends_with("-answer") {
            return Ok(false);
        }
    }
    Ok(true)
}

/// `MightyGraphSupport.Clip`.
fn clip(text: Option<&str>, maximum: i64, budget: &mut i64) -> String {
    let value = activity_clean(text, maximum.min((*budget).max(0)).max(0) as usize, false);
    *budget -= value.len() as i64;
    value
}

/// `MightyGraphSupport.Entries`.
fn graph_entries(values: &[LogEntry], restoring: bool, budget: &mut i64) -> R<Vec<LogEntry>> {
    let mut ids = HashSet::new();
    let mut result = vec![];
    for original in values.iter().skip(values.len().saturating_sub(100)).rev() {
        let id = original.id.as_deref().ok_or_else(|| null("graph entry id"))?;
        let timestamp = original.timestamp.as_deref().ok_or_else(|| null("graph entry timestamp"))?;
        let overhead = 256 + id.len() as i64 + timestamp.len() as i64;
        if *budget < overhead
            || !wire::identifier(id)
            || !ids.insert(id.to_string())
            || !crate::model::STORED_KINDS.contains(&original.kind())
        {
            continue;
        }
        *budget -= overhead;
        let pictures = if original.kind() == "image" { images(original.images.as_ref()) } else { None };
        *budget -= approximate_bytes(pictures.as_ref());
        let mut entry = LogEntry { text: Some(clip(original.text.as_deref(), 32_768, budget)), images: pictures, ..original.clone() };
        if let Some(a) = &entry.activity {
            entry.activity = activity(a, restoring).map(|a| {
                let summary = clip(Some(&a.summary), 1_000, budget);
                let output = a.output.as_deref().map(|o| clip(Some(o), 8_192, budget));
                AgentActivity { summary, output, ..a }
            });
        }
        result.push(entry);
    }
    result.reverse();
    Ok(result)
}

/// `MightyGraphSupport.LimitedEntries`: one allowance per list, charged to the shared budget.
fn limited_entries(values: Option<&[LogEntry]>, maximum: i64, restoring: bool, budget: &mut i64, what: &str) -> R<Vec<LogEntry>> {
    let values = values.ok_or_else(|| null(what))?;
    let allowance = maximum.min(*budget);
    let mut remaining = allowance;
    let result = graph_entries(values, restoring, &mut remaining)?;
    *budget -= allowance - remaining;
    Ok(result)
}

fn break_cycles(agents: &mut [MightyGraphAgent]) {
    let ids: HashSet<String> = agents.iter().map(|a| a.id().to_string()).collect();
    let parents: std::collections::HashMap<String, Option<String>> =
        agents.iter().map(|a| (a.id().to_string(), a.parent_id.clone())).collect();
    for agent in agents.iter_mut() {
        let Some(parent) = agent.parent_id.clone() else { continue };
        let mut visited = HashSet::from([agent.id().to_string()]);
        let mut current = Some(parent);
        while let Some(at) = current {
            if !ids.contains(&at) || !visited.insert(at.clone()) {
                agent.parent_id = None;
                break;
            }
            current = parents.get(&at).cloned().flatten();
        }
    }
}

/// `MightyGraphSupport.ApplyProvider`.
fn apply_provider(run: &mut MightyGraphRun, provider: &str) {
    let p = if PROVIDERS.contains(&provider) { provider } else { "claude" };
    run.provider = Some(p.into());
    let label = |e: &LogEntry| LogEntry {
        provider: Some(p.into()),
        activity: e.activity.as_ref().map(|a| AgentActivity { provider: p.into(), ..a.clone() }),
        ..e.clone()
    };
    run.root_entries = Some(run.root_entries().iter().map(label).collect());
    run.result_entries = Some(run.result_entries().iter().map(label).collect());
    for agent in run.agents.iter_mut().flatten() {
        agent.entries = Some(agent.entries().iter().map(label).collect());
    }
}

const NOTICE_SECTION_MARK: &str = "\n\n---\n\n**";

/// `MightyGraphSupport.RefreshResult`.
pub fn refresh_result(run: &mut MightyGraphRun) {
    let notice_ended = run.is_notice() && matches!(run.status(), "error" | "stopped");
    let final_output = run.final_output.clone().unwrap_or_default();
    if !(run.status() == "completed" && run.settled() || run.reopened_with_reply() || notice_ended) || final_output.is_empty() {
        run.result_entries = Some(vec![]);
        return;
    }
    let ts = run
        .result_entries()
        .first()
        .and_then(|e| e.timestamp.clone())
        .or_else(|| run.root_entries().last().and_then(|e| e.timestamp.clone()))
        .or_else(|| Some(time::now_o()));
    let provider = run.provider.clone().unwrap_or_else(|| "claude".into());
    let entry = LogEntry {
        id: Some(format!("{}-result", run.id())),
        kind: Some("assistant".into()),
        text: Some(final_output.clone()),
        timestamp: ts,
        provider: Some(provider),
        activity: None,
        images: None,
    };
    run.result_entries = Some(vec![entry.clone()]);
    let own = final_output.split(NOTICE_SECTION_MARK).next().unwrap_or("");
    let root = run.root_entries.get_or_insert_with(Vec::new);
    if !root.iter().any(|e| e.kind() == "assistant" && (e.text() == final_output || e.text() == own)) {
        match root.iter().position(|e| e.id == entry.id) {
            Some(at) => root[at] = entry,
            None => root.push(entry),
        }
    }
}

/// `MightyGraphSupport.Normalized(values, restoring, ref budget, provider)`.
pub fn graph_runs(values: &[MightyGraphRun], restoring: bool, budget: &mut i64, provider: Option<&str>) -> R<Vec<MightyGraphRun>> {
    let mut ids = HashSet::new();
    let mut result = vec![];
    for run in values.iter().skip(values.len().saturating_sub(128)).rev() {
        let claude = provider.or(run.provider.as_deref()).unwrap_or("claude") == "claude";
        let all = run.agents.as_deref().ok_or_else(|| null("graph run agents"))?;
        let mut seen_agents = HashSet::new();
        let mut agents = vec![];
        for a in all.iter().take(128) {
            if !is_identifier(a.id.as_deref()) || (claude && is_heartbeat_block(a)?) {
                continue;
            }
            if seen_agents.insert(a.id().to_string()) {
                agents.push(a);
            }
        }
        let overhead = 512
            + bytes(run.id.as_deref())
            + bytes(run.source_run_id.as_deref())
            + agents.iter().map(|a| 512 + bytes(a.id.as_deref()) + bytes(a.parent_id.as_deref())).sum::<i64>();
        if *budget < overhead || !is_identifier(run.id.as_deref()) || !ids.insert(run.id().to_string()) {
            continue;
        }
        *budget -= overhead;

        let mut r = MightyGraphRun {
            id: run.id.clone(),
            source_run_id: run.source_run_id.clone().filter(|s| wire::identifier(s)),
            input: Some(bounded(run.input.as_deref(), 32_768, budget, "graph run input")?),
            status: Some(if run.status.as_deref().is_some_and(|s| VALID_STATES.contains(&s)) {
                run.status().into()
            } else {
                "stopped".into()
            }),
            usage: run.usage.and_then(|u| u.normalized()),
            node_model_label: run.node_model_label.clone(),
            configured_model: run.configured_model.clone(),
            root_entries: Some(vec![]),
            result_entries: Some(vec![]),
            agents: Some(vec![]),
            response_records: run.response_records.clone(),
            notice_run: (run.notice_run == Some(true)).then_some(true),
            configured_effort: run.configured_effort.clone().filter(|e| valid_effort(e)),
            configured_advisor: run.configured_advisor.clone().filter(|m| wire::model(m)),
            ..MightyGraphRun::default()
        };
        r.advisor = run.advisor.as_ref().and_then(|a| advisor(a, restoring)).filter(|a| advisor_bytes(a) <= (*budget).max(0));
        *budget -= r.advisor.as_ref().map_or(0, advisor_bytes);
        r.notice_title = match (&r.notice_run, &run.notice_title) {
            (Some(true), Some(title)) => Some(bounded(Some(title), 240, budget, "notice title")?).filter(|t| !t.is_empty()),
            _ => None,
        };

        let resolved = match provider.or(run.provider.as_deref()) {
            Some(p) => p.to_string(),
            None => run
                .root_entries
                .as_deref()
                .ok_or_else(|| null("graph run root entries"))?
                .iter()
                .filter_map(|e| e.provider.as_deref())
                .find(|p| PROVIDERS.contains(p))
                .unwrap_or("claude")
                .to_string(),
        };
        r.provider = Some(if PROVIDERS.contains(&resolved.as_str()) { resolved } else { "claude".into() });
        if restoring && !terminal(r.status()) {
            r.status = Some("stopped".into());
        }

        r.final_output = match &run.final_output {
            None => None,
            Some(text) => {
                let limit = 32_768i64.min(((*budget).max(0)) / 3);
                Some(bounded(Some(text), limit, budget, "final output")?)
            }
        };
        if let Some(text) = &r.final_output {
            *budget -= text.len() as i64 * 2;
        }
        r.root_entries = Some(limited_entries(run.root_entries.as_deref(), 131_072, restoring, budget, "graph run root entries")?);
        if r.notice_run != Some(true)
            && let Some(saved) = &run.steps
        {
            let mut steps = vec![];
            for step in saved.iter().take(100) {
                let key = step.key.as_deref().ok_or_else(|| null("flow step key"))?;
                let key = if wire::utf16_len(key) > 1_100 { utf16_prefix(key, 1_100) } else { key.to_string() };
                let content = bounded(step.content.as_deref(), 1_024, budget, "flow step content")?;
                let active = match &step.active_form {
                    Some(form) => Some(bounded(Some(form), 1_024, budget, "flow step form")?),
                    None => None,
                };
                let status = match step.status.as_deref() {
                    Some(s @ ("pending" | "in_progress" | "completed")) => s.to_string(),
                    _ => "pending".into(),
                };
                steps.push(FlowStep {
                    key: Some(key),
                    content: Some(content),
                    active_form: active,
                    status: Some(status),
                    started_at: step.started_at.clone().filter(|t| time::parse(Some(t)).is_some()),
                    ended_at: step.ended_at.clone().filter(|t| time::parse(Some(t)).is_some()),
                });
            }
            r.steps = Some(steps);
        }
        r.steps_carried = run
            .steps_carried
            .as_ref()
            .map(|keys| {
                keys.iter()
                    .take(256)
                    .map(|k| if wire::utf16_len(k) > 1_100 { utf16_prefix(k, 1_100) } else { k.clone() })
                    .collect::<Vec<_>>()
            })
            .filter(|keys| !keys.is_empty());

        let mut built = vec![];
        for agent in agents {
            let mut a = MightyGraphAgent {
                id: agent.id.clone(),
                parent_id: agent.parent_id.clone().filter(|p| wire::identifier(p) && Some(p) != agent.id.as_ref()),
                title: Some(bounded(agent.title.as_deref(), 240, budget, "graph agent title")?),
                input: Some(bounded(agent.input.as_deref(), 16_384, budget, "graph agent input")?),
                status: Some(if agent.status.as_deref().is_some_and(|s| VALID_STATES.contains(&s)) {
                    agent.status().into()
                } else {
                    "stopped".into()
                }),
                kind: agent.kind.clone().filter(|k| VALID_KINDS.contains(&k.as_str())),
                usage: agent.usage.and_then(|u| u.normalized()),
                activity_generation: agent.activity_generation.filter(|g| (0..=1_000_000).contains(g)),
                response_records: agent.response_records.clone(),
                background: (agent.background == Some(true)).then_some(true),
                source_block_id: agent.source_block_id.clone().filter(|s| wire::identifier(s)),
                ..MightyGraphAgent::default()
            };
            a.identity = if a.kind.is_none() { agent.identity.as_ref().and_then(identity) } else { None };
            *budget -= a.identity.as_ref().map_or(0, identity_bytes);
            a.figures = if matches!(a.kind.as_deref(), None | Some("task")) { agent.figures.as_ref().and_then(figures) } else { None };
            a.notice_reply = match &agent.notice_reply {
                Some(reply) => Some(bounded(Some(reply), 32_768, budget, "notice reply")?).filter(|r| !r.is_empty()),
                None => None,
            };
            a.output_file = agent.output_file.clone().filter(|f| f.len() <= 4096 && !f.contains(NEWLINES));
            *budget -= bytes(a.output_file.as_deref());
            if restoring && !terminal(a.status()) {
                a.status = Some("stopped".into());
            }
            a.entries = Some(limited_entries(agent.entries.as_deref(), 65_536, restoring, budget, "graph agent entries")?);
            built.push(a);
        }
        break_cycles(&mut built);
        r.agents = Some(built);
        let provider_now = r.provider.clone().unwrap_or_else(|| "claude".into());
        apply_provider(&mut r, &provider_now);
        refresh_result(&mut r);
        result.push(r);
    }
    result.reverse();
    Ok(result)
}

/// The first `units` UTF-16 units of `value` (a cut surrogate pair's high half written as U+FFFD, as `Wire.Json` writes it).
fn utf16_prefix(value: &str, units: usize) -> String {
    let mut out = String::new();
    let mut count = 0;
    for c in value.chars() {
        let width = c.len_utf16();
        if count + width > units {
            if width == 2 && count < units {
                out.push('\u{FFFD}');
            }
            break;
        }
        count += width;
        out.push(c);
    }
    out
}

// ── block sizes (GraphBlockPreferences) ─────────────────────────────────────────────────────────────────────────────────

type BlockSizes = IndexMap<String, Option<GraphBlockSize>>;

fn clean_size(id: &str, size: Option<&GraphBlockSize>) -> Option<GraphBlockSize> {
    let length = wire::utf16_len(id);
    if length == 0 || length > 512 || id.chars().any(is_control) {
        return None;
    }
    size?.normalized_for(id)
}

/// `GraphBlockPreferences.SplittingExecution`: sizes saved before the 실행 split move once.
pub fn splitting_execution(values: Option<&BlockSizes>) -> Option<BlockSizes> {
    let values = values?;
    let mut result = values.clone();
    for (id, size) in values {
        if graph::node_suffix(id) != Some("request") {
            continue;
        }
        result.shift_remove(id);
        let moved = format!("{}{}", &id[..id.len() - "request".len()], graph::WORK_SUFFIX);
        result.entry(moved).or_insert(*size);
    }
    (!result.is_empty()).then_some(result)
}

/// `GraphBlockPreferences.Normalize(values, runs)`: only the pane's own blocks, its newest turns first.
pub fn block_sizes(values: Option<&BlockSizes>, runs: &[MightyGraphRun]) -> Option<BlockSizes> {
    let values = values.filter(|v| !v.is_empty())?;
    let mut result = BlockSizes::new();
    let mut places = 0;
    let retain = |ids: &[String], result: &mut BlockSizes, places: &mut usize| {
        if *places >= 1_024 {
            return;
        }
        let mut kept = false;
        for id in ids {
            if let Some(size) = clean_size(id, values.get(id).and_then(Option::as_ref)) {
                result.insert(id.clone(), Some(size));
                kept = true;
            }
        }
        if kept {
            *places += 1;
        }
    };
    retain(&[graph::PENDING_NODE_ID.to_string()], &mut result, &mut places);
    for run in runs.iter().rev() {
        let id = run.id();
        retain(&[graph::node_id(id, "request"), graph::node_id(id, graph::WORK_SUFFIX)], &mut result, &mut places);
        if run.settled() {
            retain(&[graph::node_id(id, "result")], &mut result, &mut places);
        }
        for agent in run.agents() {
            retain(&[graph::node_id(id, &format!("agent:{}", agent.id()))], &mut result, &mut places);
        }
        let records = graph::node_id(id, graph::PLAN_RECORD_SUFFIX);
        let mut keys: Vec<&String> = values.keys().filter(|k| k.starts_with(&records)).collect();
        keys.sort_by(|a, b| ordinal(a, b));
        for key in keys {
            retain(std::slice::from_ref(key), &mut result, &mut places);
        }
        if places >= 1_024 {
            break;
        }
    }
    (!result.is_empty()).then_some(result)
}

/// `StringComparer.Ordinal`: by UTF-16 units.
pub fn ordinal(a: &str, b: &str) -> std::cmp::Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

// ── settings, plans, checklists, background work, shell block runs ──────────────────────────────────────────────────────

/// `ProviderCatalog.PermissionModes(provider)`.
pub fn permission_modes(provider: &str) -> &'static [&'static str] {
    match provider {
        "pi" => &["manual", "fullAccess"],
        "hermes" => &["manual", "acceptEdits", "fullAccess"],
        "codex" => &["manual", "onRequest", "acceptEdits", "fullAccess"],
        "claude" => &["plan", "manual", "acceptEdits", "auto", "fullAccess"],
        _ => &["manual", "plan", "acceptEdits", "fullAccess"],
    }
}

/// `ProviderCatalog.NormalizeSettings`.
pub fn settings(provider: &str, value: Option<&RunSettings>) -> RunSettings {
    let default = RunSettings::default();
    let v = value.unwrap_or(&default);
    let permission =
        if permission_modes(provider).contains(&v.permission_mode.as_str()) { v.permission_mode.clone() } else { "manual".into() };
    let effort_ok = provider != "gemini" && wire::run_efforts(provider).contains(&v.effort.as_str());
    let claude = provider == "claude";
    let codex = provider == "codex";
    RunSettings {
        effort: if effort_ok { v.effort.clone() } else { "default".into() },
        max_turns: if claude { v.max_turns.filter(|t| (1..=1000).contains(t)) } else { None },
        max_budget_usd: if claude { v.max_budget_usd.filter(|b| *b > 0.0 && *b <= 10_000.0) } else { None },
        fast_mode: codex && v.fast_mode,
        web_search: if codex && matches!(v.web_search.as_str(), "disabled" | "cached" | "live") {
            v.web_search.clone()
        } else {
            "default".into()
        },
        network_access: codex && matches!(permission.as_str(), "acceptEdits" | "onRequest") && v.network_access,
        advisor: if claude { v.advisor.clone().filter(|a| wire::model(a)) } else { None },
        permission_mode: permission,
    }
}

const PLAN_OUTCOMES: [&str; 4] = ["approvedAuto", "approvedConfirm", "revised", "cancelled"];

fn plan_bounded(record: PlanRecord) -> PlanRecord {
    let original = record.plan.clone().unwrap_or_default();
    let plan = prefix_utf8(&original, 32_768);
    let feedback = record.feedback.as_deref().map(|f| prefix_utf8(f, 4_096)).filter(|f| !f.is_empty());
    PlanRecord {
        plan_truncated: if wire::utf16_len(&plan) < wire::utf16_len(&original) { Some(true) } else { record.plan_truncated },
        plan: Some(plan),
        feedback,
        ..record
    }
}

fn plan_cost(r: &PlanRecord) -> usize {
    bytes(r.plan.as_deref()) as usize + bytes(r.feedback.as_deref()) as usize
}

fn plan_budgeted(mut list: Vec<PlanRecord>) -> Vec<PlanRecord> {
    let mut used = 0;
    for i in (0..list.len()).rev() {
        let mut record = list[i].clone();
        if used + plan_cost(&record) > 98_304 {
            let plan = record.plan.clone().unwrap_or_default();
            let lines: Vec<&str> = plan.split('\n').take(8).collect();
            let summary = prefix_utf8(&lines.join("\n"), 1_024);
            if summary.len() < plan.len() {
                record.plan = Some(summary);
                record.plan_truncated = Some(true);
            }
            if let Some(feedback) = &record.feedback {
                record.feedback = Some(prefix_utf8(feedback, 512));
            }
            list[i] = record.clone();
        }
        used += plan_cost(&record);
    }
    list
}

/// `ClaudePlanMode.NormalizedHistory`.
pub fn plan_history(history: Option<&Vec<PlanRecord>>) -> Option<Vec<PlanRecord>> {
    let history = history?;
    let list: Vec<PlanRecord> = history
        .iter()
        .filter(|r| {
            is_identifier(r.id.as_deref())
                && r.plan.as_deref().is_some_and(|p| !p.is_empty())
                && r.outcome.as_deref().is_some_and(|o| PLAN_OUTCOMES.contains(&o))
                && time::parse(r.decided_at.as_deref()).is_some()
        })
        .map(|r| {
            plan_bounded(PlanRecord {
                run_id: Some(r.run_id.clone().unwrap_or_default()),
                received_at: r.received_at.clone().or_else(|| r.decided_at.clone()),
                graph_run_id: r.graph_run_id.clone().filter(|g| wire::identifier(g)),
                launch_override: (r.launch_override.as_deref() == Some("plan")).then(|| "plan".into()),
                ..r.clone()
            })
        })
        .collect();
    let list: Vec<PlanRecord> = list.iter().skip(list.len().saturating_sub(10)).cloned().collect();
    (!list.is_empty()).then(|| plan_budgeted(list))
}

/// `TodoProgress.Normalized`.
pub fn todo_progress(value: Option<&TodoProgress>) -> Option<TodoProgress> {
    let value = value?;
    let items: Vec<TodoItem> = value
        .items
        .iter()
        .flatten()
        .map(|item| {
            let content = activity_clean(item.content.as_deref(), 1024, true);
            let active = item.active_form.as_deref().map(|a| activity_clean(Some(a), 1024, true)).filter(|a| !a.is_empty());
            let id = item.id.as_deref().map(|i| activity_clean(Some(i), 128, true)).filter(|i| !i.is_empty());
            let status = match item.status.as_deref() {
                Some(s @ ("pending" | "in_progress" | "completed")) => s.to_string(),
                _ => "pending".into(),
            };
            TodoItem { content: Some(content), status: Some(status), active_form: active, id }
        })
        .filter(|i| i.content.as_deref().is_some_and(|c| !c.is_empty()))
        .take(100)
        .collect();
    Some(TodoProgress { items: Some(items), run_id: value.run_id.clone().filter(|r| wire::identifier(r)) })
}

/// `BackgroundTaskTracker.ValidId`.
fn valid_task_id(value: Option<&str>) -> bool {
    value.is_some_and(|v| !v.is_empty() && v.len() <= 128 && !v.chars().any(|c| is_control(c) || c == ' '))
}

/// `BackgroundWork.Normalized`.
pub fn background_work(value: Option<&BackgroundWork>, restoring: bool) -> Option<BackgroundWork> {
    let value = value?;
    let tasks: Vec<BackgroundTask> = value
        .tasks
        .iter()
        .flatten()
        .filter(|t| valid_task_id(t.id.as_deref()) && time::parse(t.started_at.as_deref()).is_some())
        .map(|t| {
            let summary = t.summary.as_deref().map(|s| activity_clean(Some(s), 4096, false)).filter(|s| !s.is_empty());
            let status = match t.status.as_deref() {
                Some(s @ ("running" | "completed" | "failed" | "stopped" | "unknown")) => s.to_string(),
                _ => "unknown".into(),
            };
            BackgroundTask {
                kind: Some(if matches!(t.kind.as_deref(), Some("agent" | "shell")) {
                    t.kind.clone().unwrap_or_default()
                } else {
                    "other".into()
                }),
                description: Some(activity_clean(t.description.as_deref(), 1024, true)),
                summary,
                status: Some(if restoring && status == "running" { "unknown".into() } else { status }),
                ended_at: t.ended_at.clone().filter(|e| time::parse(Some(e)).is_some()),
                tool_use_id: t.tool_use_id.clone().filter(|i| wire::identifier(i)),
                ..t.clone()
            }
        })
        .collect();
    let tasks: Vec<BackgroundTask> = tasks.iter().skip(tasks.len().saturating_sub(32)).cloned().collect();
    (!tasks.is_empty()).then_some(BackgroundWork { tasks: Some(tasks), turn_ended: value.turn_ended || restoring })
}

const SHELL_STATES: [&str; 5] = ["running", "completed", "failed", "stopped", "interrupted"];

/// `ShellBlockRunSupport.IsKey`: `<entryId>#<n>`.
fn is_shell_key(key: Option<&str>) -> bool {
    let Some(key) = key else { return false };
    let Some(at) = key.rfind('#').filter(|at| *at > 0) else { return false };
    let index = &key[at + 1..];
    wire::identifier(&key[..at]) && (1..=4).contains(&index.len()) && index.bytes().all(|b| b.is_ascii_digit())
}

/// `ShellBlockRunSupport.Tail`: the end that fits 16 KB of UTF-8.
pub fn shell_tail(text: &str) -> String {
    if wire::utf16_len(text) * 3 <= 16_384 || text.len() <= 16_384 {
        return text.to_string();
    }
    let mut bytes = 0;
    let mut start = text.len();
    for (at, c) in text.char_indices().rev() {
        if bytes + c.len_utf8() > 16_384 {
            break;
        }
        bytes += c.len_utf8();
        start = at;
    }
    text[start..].to_string()
}

/// `ShellBlockRunSupport.Normalized`.
pub fn shell_block_runs(runs: Option<&Vec<ShellBlockRun>>, restoring: bool) -> Option<Vec<ShellBlockRun>> {
    let runs = runs.filter(|r| !r.is_empty())?;
    let mut kept: Vec<ShellBlockRun> = vec![];
    for run in runs {
        let command_ok = run.command.as_deref().is_some_and(|c| !c.trim().is_empty() && !c.contains('\0') && c.len() <= 65_536);
        if !is_shell_key(run.key.as_deref())
            || !command_ok
            || !run.state.as_deref().is_some_and(|s| SHELL_STATES.contains(&s))
            || run.started_at.as_deref().is_none_or(str::is_empty)
        {
            continue;
        }
        let state = if restoring && run.state.as_deref() == Some("running") {
            "interrupted".to_string()
        } else {
            run.state.clone().unwrap_or_default()
        };
        let output = run.output.as_deref().map(shell_tail).unwrap_or_default();
        let ended = run.ended_at.clone().filter(|e| wire::utf16_len(e) <= 64);
        let value = ShellBlockRun { state: Some(state), output: Some(output), ended_at: ended, ..run.clone() };
        if let Some(at) = kept.iter().position(|r| r.key == value.key) {
            kept.remove(at);
        }
        kept.push(value);
    }
    if kept.len() > 64 {
        kept.drain(..kept.len() - 64);
    }
    (!kept.is_empty()).then_some(kept)
}

// ── the rest of the state ───────────────────────────────────────────────────────────────────────────────────────────────

/// `PhaseModelPreferences.Normalize`.
pub fn phase_models(value: Option<&PhaseModelsSnapshot>) -> Option<PhaseModelsSnapshot> {
    let v = value?;
    let model = |name: &Option<String>| Some(if is_model(name.as_deref()) { name.clone().unwrap_or_default() } else { "default".into() });
    let effort = |e: &Option<String>| e.clone().filter(|e| EFFORTS.contains(&e.as_str()));
    Some(PhaseModelsSnapshot {
        claude_main: model(&v.claude_main),
        claude_opus_alias: model(&v.claude_opus_alias),
        claude_sonnet_alias: model(&v.claude_sonnet_alias),
        claude_haiku_alias: model(&v.claude_haiku_alias),
        claude_subagent_default: model(&v.claude_subagent_default),
        codex_review_model: model(&v.codex_review_model),
        codex_subagent_default: model(&v.codex_subagent_default),
        codex_plan_mode_reasoning_effort: Some(effort(&v.codex_plan_mode_reasoning_effort).unwrap_or_else(|| "default".into())),
        claude_main_effort: effort(&v.claude_main_effort),
        codex_main_effort: effort(&v.codex_main_effort),
        codex_subagent_effort: effort(&v.codex_subagent_effort),
    })
}

/// `MobileRelayHost.NormalizeRelay`: `ws(s)://host[:port]`, or null for anything `Uri` would not take as one (the parts .NET's
/// `Uri` checks that matter for a relay address: scheme, host, user info, fragment, port).
pub fn normalize_relay(value: &str) -> Option<String> {
    if value.trim().is_empty() || wire::utf16_len(value) > 256 || value.chars().any(char::is_whitespace) {
        return None;
    }
    let input = if value.contains("://") { value.to_string() } else { format!("wss://{value}") };
    let (scheme, rest) = input.split_once("://")?;
    let scheme = scheme.to_ascii_lowercase();
    let default_port = match scheme.as_str() {
        "ws" | "http" => 80,
        "wss" | "https" => 443,
        _ => return None,
    };
    if rest.contains('#') {
        return None;
    }
    let authority = rest.split(['/', '?']).next().unwrap_or("");
    if authority.contains('@') || authority.is_empty() {
        return None;
    }
    let (host, port) = if let Some(bracketed) = authority.strip_prefix('[') {
        let (inside, after) = bracketed.split_once(']')?;
        (format!("[{}]", inside.to_ascii_lowercase()), after.strip_prefix(':'))
    } else {
        match authority.rsplit_once(':') {
            Some((h, p)) => (h.to_ascii_lowercase(), Some(p)),
            None => (authority.to_ascii_lowercase(), None),
        }
    };
    if host.is_empty() || !host.is_ascii() {
        return None;
    }
    let port: u32 = match port {
        None => default_port,
        Some(p) if !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()) => p.parse().ok().filter(|p| *p <= 65535)?,
        Some(_) => return None,
    };
    let secure = matches!(scheme.as_str(), "https" | "wss");
    let (scheme, default) = if secure { ("wss", 443) } else { ("ws", 80) };
    Some(if port == default { format!("{scheme}://{host}") } else { format!("{scheme}://{host}:{port}") })
}

/// `MobileRemoteSettings.Normalized`.
pub fn mobile_remote(value: Option<&MobileRemoteSettings>) -> MobileRemoteSettings {
    let value = value.cloned().unwrap_or_default();
    let port = if (1024..=65535).contains(&value.direct_port) { value.direct_port } else { MOBILE_DIRECT_PORT };
    let relay = value.relay_url.as_deref().unwrap_or("").trim().to_string();
    if relay.is_empty() {
        return MobileRemoteSettings { relay_url: Some(String::new()), direct_port: port, ..value };
    }
    match normalize_relay(&relay) {
        Some(valid) => MobileRemoteSettings { relay_url: Some(valid), direct_port: port, ..value },
        None => MobileRemoteSettings { enabled: false, relay_url: Some(String::new()), direct_port: port, ..value },
    }
}

/// `ReferencePreview.BubbleSize`.
pub fn bubble_size(width: Option<f64>, height: Option<f64>) -> (Option<f64>, Option<f64>) {
    (width.filter(|w| w.is_finite() && *w > 0.0).map(|w| w.max(300.0)), height.filter(|h| h.is_finite() && *h > 0.0).map(|h| h.max(200.0)))
}

/// C# `DesignMetrics.Layout`.
pub const SIDEBAR_MIN: f64 = 210.0;
pub const SIDEBAR_DEFAULT: f64 = 252.0;
pub const SIDEBAR_MAX: f64 = 360.0;
const LAYOUTS: [&str; 5] = ["grid", "columns", "focus", "tabs", "custom"];
const SETTINGS_PANES: [&str; 9] = ["general", "models", "styles", "tools", "cli", "mobile", "remote", "companion", "about"];
/// `TranscriptRetention.MaximumEntries`.
pub const MAXIMUM_ENTRIES: usize = 400;

/// `FilePaneKind.IsFilePane`, `WorkMapPaneKind.IsWorkMap`, `AgentIOPaneKind.IsAgentIO`.
pub fn is_files(kind: &str) -> bool {
    kind == "files"
}
pub fn is_work_map(kind: &str) -> bool {
    kind == "workmap"
}
pub fn is_agent_io(kind: &str) -> bool {
    matches!(kind, "agent-terminal" | "agent-browser")
}

/// One normalize's running text budget (`textBudget`, 2 MiB of JSON).
struct Budget {
    text: i64,
}

impl Budget {
    /// `Bounded(text, limit)`: `Wire.Clean` within the limit and a sixth of what is left, charged its JSON size.
    fn bounded(&mut self, text: Option<&str>, limit: i64) -> String {
        let result = wire::clean(text.unwrap_or(""), limit.min((self.text / 6).max(0)) as usize);
        self.text -= escaped_len(&result) as i64;
        result
    }
}

fn normalize_log(log: &LogEntry, restoring: bool, budget: &mut Budget) -> LogEntry {
    let mut activity = log.activity.as_ref().and_then(|a| activity(a, restoring));
    if let Some(a) = activity.as_mut() {
        a.summary = budget.bounded(Some(&a.summary), 1000);
        a.output = a.output.as_deref().map(|o| budget.bounded(Some(o), 8192));
    }
    let limit = if log.kind() == "assistant" { MAXIMUM_MESSAGE_BYTES as i64 } else { 32768 };
    LogEntry {
        text: Some(budget.bounded(log.text.as_deref(), limit)),
        provider: log.provider.clone().filter(|p| PROVIDERS.contains(&p.as_str())),
        activity,
        images: if log.kind() == "image" { images(log.images.as_ref()) } else { None },
        ..log.clone()
    }
}

fn normalize_session(s: &RunSession, restoring: bool, remote_ids: &HashSet<String>, budget: &mut Budget) -> R<RunSession> {
    let kind = s.kind();
    // A files pane runs nothing and keeps nothing but its title.
    if is_files(kind) || is_work_map(kind) {
        return Ok(RunSession {
            id: s.id.clone(),
            workspace_id: s.workspace_id.clone(),
            kind: s.kind.clone(),
            title: Some(clamp_title(s.title.as_deref())),
            ..RunSession::default()
        });
    }
    if matches!(kind, "browser" | "agent-browser") {
        return Ok(RunSession {
            title: Some(clamp_title(s.title.as_deref())),
            status: Some("idle".into()),
            logs: Some(vec![]),
            draft: Some(String::new()),
            run_timing: None,
            session_usage: None,
            current_activity: None,
            agent_view_mode: None,
            graph_runs: None,
            graph_result_size: None,
            graph_plan_size: None,
            graph_block_sizes: None,
            graph_view_mode: None,
            plan_history: None,
            todo_progress: None,
            background_work: None,
            shell_block_runs: None,
            resume_id: None,
            workspace_profile_key: Some(wire::clean(s.workspace_profile_key.as_deref().unwrap_or(""), 128)).filter(|k| !k.is_empty()),
            owner_session_id: s.owner_session_id.clone().filter(|o| wire::identifier(o)),
            parent_session_id: None,
            working_folder: None,
            ask_target: None,
            ..s.clone()
        });
    }
    let provider = if PROVIDERS.contains(&s.provider()) { s.provider().to_string() } else { "claude".into() };
    let logs: Vec<&LogEntry> =
        s.logs().iter().filter(|l| is_identifier(l.id.as_deref()) && crate::model::STORED_KINDS.contains(&l.kind())).collect();
    let logs: Vec<LogEntry> = logs
        .iter()
        .skip(logs.len().saturating_sub(MAXIMUM_ENTRIES))
        .map(|l| normalize_log(l, restoring, budget))
        .filter(|l| !l.text().is_empty())
        .collect();
    let mut timing =
        if kind == "shell" { None } else { s.run_timing.filter(AgentRunTiming::is_valid).or_else(|| AgentRunTiming::infer(&logs)) };
    if let Some(t) = timing {
        timing = Some(if restoring {
            t.interrupt()
        } else if s.status() == "running" {
            t.observe(None)
        } else {
            t
        });
    }
    let usage = if kind == "claude" && s.session_usage.as_ref().is_some_and(|u| u.provider == provider) {
        s.session_usage.as_ref().and_then(session_usage)
    } else {
        None
    };
    let view_mode = (kind == "claude" && graph::GRAPH_PROVIDERS.contains(&provider.as_str())).then(|| "mighty".to_string());
    let mut graph_budget = LIVE_HISTORY_LIMIT;
    let graph_runs = match &s.graph_runs {
        Some(runs) if !runs.is_empty() => Some(graph_runs(runs, restoring, &mut graph_budget, None)?).filter(|r| !r.is_empty()),
        _ => None,
    };
    let sizes =
        if s.graph_sizes_layout.is_none() { splitting_execution(s.graph_block_sizes.as_ref()) } else { s.graph_block_sizes.clone() };
    let plan_mode = kind == "claude" && provider == "claude";
    let shell = kind == "shell";
    let normalized = RunSession {
        title: Some(clamp_title(s.title.as_deref())),
        draft: Some(budget.bounded(s.draft.as_deref(), 100_000)),
        model: Some(if is_model(s.model.as_deref()) { s.model().to_string() } else { "default".into() }),
        settings: Some(settings(&provider, s.settings.as_ref())),
        resume_id: s.resume_id.clone().filter(|r| wire::session_identifier(r)),
        status: Some(
            match (restoring, s.status()) {
                (true, "running") => {
                    if shell {
                        "idle"
                    } else {
                        "stopped"
                    }
                }
                (_, status @ ("idle" | "running" | "completed" | "error" | "stopped")) => status,
                _ => "idle",
            }
            .into(),
        ),
        logs: Some(logs),
        run_timing: timing,
        session_usage: usage,
        current_activity: if restoring { None } else { s.current_activity.as_ref().and_then(|a| activity(a, false)) },
        agent_view_mode: view_mode,
        graph_view_mode: if shell { None } else { crate::model::graph_view_mode(s.graph_view_mode.as_deref()) },
        graph_result_size: if shell { None } else { s.graph_result_size.as_ref().and_then(GraphBlockSize::normalized) },
        graph_plan_size: if shell { None } else { s.graph_plan_size.as_ref().and_then(GraphBlockSize::normalized) },
        graph_sizes_layout: Some(graph::SPLIT_LAYOUT),
        graph_block_sizes: if shell { None } else { block_sizes(sizes.as_ref(), graph_runs.as_deref().unwrap_or(&[])) },
        graph_runs,
        title_mode: title_mode(s.title_mode.as_deref()),
        plan_history: if plan_mode { plan_history(s.plan_history.as_ref()) } else { None },
        todo_progress: if plan_mode || kind == "claude" && provider == "codex" { todo_progress(s.todo_progress.as_ref()) } else { None },
        background_work: if plan_mode { background_work(s.background_work.as_ref(), restoring || s.status() != "running") } else { None },
        parent_session_id: s.parent_session_id.clone().filter(|p| kind == "claude" && wire::identifier(p) && Some(p) != s.id.as_ref()),
        working_folder: s.working_folder.clone().filter(|_| kind == "claude" && is_working_folder(s.working_folder.as_deref())),
        ask_target: if plan_mode && !remote_ids.contains(s.workspace_id()) { s.ask_target.clone() } else { None },
        shell_block_runs: if kind == "claude" { shell_block_runs(s.shell_block_runs.as_ref(), restoring) } else { None },
        provider: Some(provider),
        ..s.clone()
    };
    Ok(if restoring { restored_title(normalized) } else { normalized })
}

/// C# `StateStore.Normalize(value, restoring)`.
pub fn normalize(value: &AppSnapshot, restoring: bool) -> R<AppSnapshot> {
    if value.version != 1 {
        return Ok(AppSnapshot::default());
    }
    let phase = phase_models(value.phase_models.as_ref());
    let mut seen = HashSet::new();
    let workspaces: Vec<Workspace> = value
        .workspaces()
        .iter()
        .filter(|w| valid_workspace(w))
        .filter(|w| seen.insert(w.id().to_string()))
        .take(64)
        .map(|w| Workspace {
            name: Some(wire::clean(w.name.as_deref().unwrap_or(""), 120)),
            remote: w.remote.as_ref().map(|r| crate::model::RemoteWorkspaceReference {
                host_name: Some(wire::clean(r.host_name.as_deref().unwrap_or(""), 120)),
                ..r.clone()
            }),
            ..w.clone()
        })
        .collect();
    let ids: HashSet<String> = workspaces.iter().map(|w| w.id().to_string()).collect();
    let remote_ids: HashSet<String> = workspaces.iter().filter(|w| w.remote.is_some()).map(|w| w.id().to_string()).collect();
    let mut budget = Budget { text: 2 * 1024 * 1024 };
    let all = value.sessions();
    let mut seen = HashSet::new();
    let mut sessions: Vec<Arc<RunSession>> = vec![];
    for s in all {
        let kind = s.kind();
        let keep = is_identifier(s.id.as_deref())
            && ids.contains(s.workspace_id())
            && s.workspace_id.is_some()
            && (matches!(kind, "claude" | "shell" | "browser")
                || !restoring && is_files(kind) && s.id() == format!("files:{}", s.workspace_id())
                || !restoring && is_work_map(kind) && s.id() == format!("workmap:{}", s.workspace_id())
                || !restoring
                    && is_agent_io(kind)
                    && s.owner_session_id.as_deref().is_some_and(|owner| {
                        s.id() == format!("{kind}:{owner}")
                            && all
                                .iter()
                                .any(|p| p.id.as_deref() == Some(owner) && p.kind() == "claude" && p.workspace_id == s.workspace_id)
                    }));
        if !keep || !seen.insert(s.id().to_string()) {
            continue;
        }
        if sessions.len() == layout::MAXIMUM_SESSIONS {
            break;
        }
        sessions.push(Arc::new(normalize_session(s, restoring, &remote_ids, &mut budget)?));
    }
    let workspace_id = match &value.active_workspace_id {
        Some(id) if ids.contains(id) => Some(id.clone()),
        _ => workspaces.first().map(|w| w.id().to_string()),
    };
    let expanded = value.expanded_workspace_ids.as_ref().map(|list| {
        let mut kept: Vec<String> = list.iter().filter(|id| ids.contains(*id)).cloned().collect();
        kept.sort_by(|a, b| ordinal(a, b));
        kept.dedup();
        kept
    });
    let in_active = |s: &&Arc<RunSession>| s.workspace_id.is_some() && s.workspace_id == workspace_id;
    let active_session_id = sessions
        .iter()
        .filter(in_active)
        .find(|s| s.id.is_some() && s.id == value.active_session_id)
        .or_else(|| sessions.iter().find(in_active))
        .map(|s| s.id().to_string());
    let saved_selection = |key: &str| value.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(key)).cloned().flatten();
    let pane_ids = |workspace: &str| -> Vec<String> {
        sessions.iter().filter(|s| s.workspace_id() == workspace).map(|s| s.id().to_string()).collect()
    };
    let layouts = value.pane_layouts.as_ref().map(|saved| {
        let mut kept = IndexMap::new();
        for (key, node) in saved.iter().filter(|(key, _)| ids.contains(*key)) {
            let active = if Some(key) == workspace_id.as_ref() { active_session_id.clone() } else { saved_selection(key) };
            if let Some(node) = layout::normalize(node.as_ref(), &pane_ids(key), active.as_deref()) {
                kept.insert(key.clone(), Some(node));
            }
        }
        kept
    });
    fn selected(node: Option<&PaneLayoutNode>) -> Option<String> {
        let node = node?;
        if node.kind() == "tabs" {
            return node.selected_session_id.clone().or_else(|| node.session_ids.as_ref().and_then(|ids| ids.first().cloned().flatten()));
        }
        node.children.iter().flatten().find_map(|c| selected(c.as_ref()))
    }
    let mut modes = IndexMap::new();
    let mut selections = IndexMap::new();
    for workspace in &workspaces {
        let id = workspace.id();
        let root = layouts.as_ref().and_then(|l| l.get(id)).and_then(Option::as_ref);
        let saved_mode = value.pane_layout_modes.as_ref().and_then(|m| m.get(id)).cloned().flatten();
        let mode = match saved_mode {
            Some(mode) if LAYOUTS.contains(&mode.as_str()) => mode,
            _ if value.pane_layout_modes.is_none()
                && value.layout() == "focus"
                && value.layout.is_some()
                && Some(id) == workspace_id.as_deref() =>
            {
                "focus".into()
            }
            _ => match root {
                Some(root) => if root.kind() == "tabs" { "tabs" } else { "custom" }.into(),
                None if matches!(value.layout.as_deref(), Some("grid" | "columns" | "tabs")) => value.layout().to_string(),
                None => "grid".into(),
            },
        };
        modes.insert(id.to_string(), Some(mode));
        let panes = pane_ids(id);
        let preferred = if Some(id) == workspace_id.as_deref() { active_session_id.clone() } else { saved_selection(id) };
        let chosen = preferred.filter(|p| panes.contains(p)).or_else(|| selected(root)).or_else(|| panes.first().cloned());
        if let Some(chosen) = chosen {
            selections.insert(id.to_string(), Some(chosen));
        }
    }
    let trusted: Option<IndexMap<String, Option<String>>> = value.trusted_status_lines.as_ref().map(|lines| {
        lines
            .iter()
            .filter(|(k, v)| wire::identifier(k) && v.as_deref().is_some_and(|v| (1..=64).contains(&wire::utf16_len(v))))
            .map(|(k, v)| (k.clone(), v.clone()))
            .collect()
    });
    let (bubble_width, bubble_height) = bubble_size(value.reference_bubble_width, value.reference_bubble_height);
    Ok(AppSnapshot {
        phase_models: phase,
        workspaces: Some(workspaces),
        sessions: Some(sessions),
        active_workspace_id: workspace_id,
        active_session_id,
        expanded_workspace_ids: expanded,
        pane_layouts: layouts,
        pane_layout_modes: Some(modes),
        pane_layout_active_session_ids: Some(selections),
        layout: Some(if value.layout.as_deref().is_some_and(|l| LAYOUTS.contains(&l)) {
            value.layout().to_string()
        } else {
            "grid".into()
        }),
        theme: Some(if value.theme.as_deref() == Some("light") { "light" } else { "dark" }.into()),
        sidebar_width: if value.sidebar_width.is_finite() { value.sidebar_width.clamp(SIDEBAR_MIN, SIDEBAR_MAX) } else { SIDEBAR_DEFAULT },
        reference_bubble_width: bubble_width,
        reference_bubble_height: bubble_height,
        trusted_status_lines: trusted.filter(|t| !t.is_empty()),
        language_preference: Some(match value.language_preference.as_deref() {
            Some(l) if locale::LANGUAGES.contains(&l) => l.to_string(),
            _ => "system".into(),
        }),
        agent_web_open_choices: value.agent_web_open_choices.as_ref().map(|choices| {
            choices
                .iter()
                .filter(|(k, v)| ids.contains(*k) && matches!(v.as_deref(), Some("inApp" | "external")))
                .map(|(k, v)| (k.clone(), v.clone()))
                .collect()
        }),
        mobile_remote: Some(mobile_remote(value.mobile_remote.as_ref())),
        settings_pane: Some(match value.settings_pane.as_deref() {
            Some(p) if SETTINGS_PANES.contains(&p) => p.to_string(),
            _ => "general".into(),
        }),
        ..value.clone()
    })
}

/// `AntigravityModels`, the slugs of `ProviderCatalog.Fallback("gemini")` after its default.
const ANTIGRAVITY_MODELS: [&str; 13] = [
    "gemini-4-argon-medium",
    "gemini-4-argon-high",
    "gemini-4-argon-low",
    "gemini-3.8-flash-high",
    "gemini-3.8-flash-medium",
    "gemini-3.8-flash-low",
    "claude-opus-5-5-low",
    "claude-opus-5-5-medium",
    "claude-opus-5-5-high",
    "claude-sonnet-5-5-low",
    "claude-sonnet-5-5-medium",
    "claude-sonnet-5-5-high",
    "gpt-oss-120b-medium",
];

/// C# `AntigravityMigration.Migrated`: a `gemini` pane as agy can run it, with a note in its log for each change.
pub fn antigravity_migrated(session: &RunSession, locale: &Locale) -> RunSession {
    if session.kind() != "claude" || session.provider() != "gemini" {
        return session.clone();
    }
    let mut value = session.clone();
    let mut logs = session.logs().to_vec();
    let model = value.model.clone();
    if !(model.as_deref() == Some("default") || model.as_deref().is_some_and(|m| ANTIGRAVITY_MODELS.contains(&m))) {
        let shown = activity_clean(model.as_deref(), 200, true);
        logs.push(LogEntry::new(
            new_id(),
            "system",
            locale.get_with("run.notice.antigravityModelReset", &[("model", &shown)]),
            time::now_o(),
            None,
        ));
        value.model = Some("default".into());
    }
    if value.resume_id.is_some() {
        logs.push(LogEntry::new(new_id(), "system", locale.get("run.notice.antigravityConversationCleared"), time::now_o(), None));
        value.resume_id = None;
    }
    value.logs = Some(logs);
    value
}

/// C# `FilePaneKind.Stored`: what is written to disk, without files, work map and agent IO panes.
pub fn stored(snapshot: &AppSnapshot) -> AppSnapshot {
    let files: HashSet<String> = snapshot
        .sessions()
        .iter()
        .filter(|s| is_files(s.kind()) || is_work_map(s.kind()) || is_agent_io(s.kind()))
        .map(|s| s.id().to_string())
        .collect();
    if files.is_empty() {
        return snapshot.clone();
    }
    let sessions: Vec<Arc<RunSession>> = snapshot.sessions().iter().filter(|s| !files.contains(s.id())).cloned().collect();
    let layouts = snapshot.pane_layouts.as_ref().map(|saved| {
        let mut kept = IndexMap::new();
        for (workspace, node) in saved {
            let panes: Vec<String> = sessions.iter().filter(|s| s.workspace_id() == workspace).map(|s| s.id().to_string()).collect();
            if let Some(node) = layout::normalize(node.as_ref(), &panes, None) {
                kept.insert(workspace.clone(), Some(node));
            }
        }
        kept
    });
    AppSnapshot {
        active_session_id: snapshot.active_session_id.clone().filter(|id| !files.contains(id)),
        pane_layout_active_session_ids: snapshot.pane_layout_active_session_ids.as_ref().map(|m| {
            m.iter().filter(|(_, v)| v.as_deref().is_none_or(|v| !files.contains(v))).map(|(k, v)| (k.clone(), v.clone())).collect()
        }),
        sessions: Some(sessions),
        pane_layouts: layouts,
        ..snapshot.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::json::Wire;

    fn state(text: &str) -> AppSnapshot {
        AppSnapshot::read(&serde_json::from_str(text).unwrap()).unwrap()
    }

    fn root() -> &'static str {
        if cfg!(windows) { "C:\\\\work\\\\a" } else { "/work/a" }
    }

    #[test]
    fn restore_stops_running_panes_and_fills_defaults() {
        let s = state(&format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","name":"A","path":"{}"}}],"sessions":[
                {{"id":"s1","workspaceId":"w1","status":"running","titleMode":"fixed","title":"One"}},
                {{"id":"s2","workspaceId":"w1","kind":"shell","status":"running","title":"Two"}},
                {{"id":"s3","workspaceId":"gone","title":"Orphan"}}]}}"#,
            root()
        ));
        let page = normalize(&s, true).unwrap();
        assert_eq!(page.sessions().len(), 2);
        assert_eq!(page.sessions()[0].status(), "stopped");
        assert_eq!(page.sessions()[1].status(), "idle");
        assert_eq!(page.active_workspace_id.as_deref(), Some("w1"));
        assert_eq!(page.active_session_id.as_deref(), Some("s1"));
        assert_eq!(page.pane_layout_modes.as_ref().unwrap()["w1"].as_deref(), Some("grid"));
        assert_eq!(page.sidebar_width, 252.0);
    }

    #[test]
    fn an_automatic_title_follows_the_latest_request() {
        let s = state(&format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{}"}}],"sessions":[{{"id":"s1","workspaceId":"w1","title":"Claude","logs":[
                {{"id":"l1","kind":"user","text":"first","timestamp":"2026-10-01T00:00:00Z"}},
                {{"id":"l2","kind":"user","text":"  fix   the\nbuild  ","timestamp":"2026-10-01T00:00:00Z"}}]}}]}}"#,
            root()
        ));
        assert_eq!(normalize(&s, true).unwrap().sessions()[0].title(), "fix the build");
    }

    #[test]
    fn shortened_cuts_at_forty_elements() {
        assert_eq!(shortened(&"a".repeat(50)).unwrap(), format!("{}…", "a".repeat(40)));
        assert_eq!(shortened("   "), None);
    }

    #[test]
    fn activity_clean_strips_escapes_and_trims() {
        assert_eq!(activity_clean(Some("  \u{1b}[31mred\u{1b}[0m\u{1}  "), 100, false), "red");
        assert_eq!(activity_clean(Some("\u{1b}]0;title\u{7}x"), 100, false), "x");
        assert_eq!(activity_clean(Some("a\nb\tc"), 100, true), "a b c");
        assert_eq!(activity_clean(Some("가나다"), 7, false), "가나");
    }

    #[test]
    fn relays_normalize_as_uri_does() {
        assert_eq!(normalize_relay("relay.example.com").as_deref(), Some("wss://relay.example.com"));
        assert_eq!(normalize_relay("https://Relay.Example.com:443/x?y").as_deref(), Some("wss://relay.example.com"));
        assert_eq!(normalize_relay("http://h:8080").as_deref(), Some("ws://h:8080"));
        assert_eq!(normalize_relay("ftp://h"), None);
        assert_eq!(normalize_relay("wss://u@h"), None);
    }
}
