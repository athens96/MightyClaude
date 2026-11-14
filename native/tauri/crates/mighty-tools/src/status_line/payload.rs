//! The session context a status line command reads on stdin (C# `StatusLineContext`, `StatusLineSupport.TranscriptPath`,
//! `BuildPayload`, StatusLine.cs:184-329), with the CLI's field names and shape, written as `JsonObject.ToJsonString()` writes it.

use std::path::{Path, PathBuf};

use mighty_core::json::JsonOut;
use mighty_core::model::SessionRateLimit;

/// C# `StatusLineContext`.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StatusLineContext {
    pub session_id: String,
    pub cwd: Option<String>,
    pub project_dir: Option<String>,
    pub model_id: Option<String>,
    pub model_name: Option<String>,
    pub version: Option<String>,
    pub cost_usd: Option<f64>,
    pub duration_ms: Option<i64>,
    pub api_duration_ms: Option<i64>,
    pub input_tokens: Option<i64>,
    pub output_tokens: Option<i64>,
    pub cache_read_tokens: Option<i64>,
    pub cache_write_tokens: Option<i64>,
    pub context_used_tokens: Option<i64>,
    pub context_window_tokens: Option<i64>,
    pub effort: Option<String>,
    pub fast_mode: bool,
    pub rate_limits: Option<Vec<SessionRateLimit>>,
    pub output_style: Option<String>,
    pub thinking_enabled: Option<bool>,
    pub transcript_path: Option<String>,
}

/// The transcript path by the Mac's slug rule: every character that is not a letter or digit becomes `-` (each UTF-16 unit, as
/// C# reads the string), the first 200 kept: `<config>/projects/<slug>/<session>.jsonl`.
pub fn transcript_path(config_dir: &Path, cwd: Option<&str>, session_id: &str) -> PathBuf {
    let mut slug: Vec<char> = Vec::new();
    for c in cwd.unwrap_or("").chars() {
        if c.len_utf16() == 1 && c.is_alphanumeric() {
            slug.push(c);
        } else {
            slug.extend(std::iter::repeat_n('-', c.len_utf16()));
        }
    }
    slug.truncate(200);
    let slug: String = slug.into_iter().collect();
    let projects = config_dir.join("projects");
    let folder = if slug.is_empty() { projects } else { projects.join(slug) };
    folder.join(format!("{session_id}.jsonl"))
}

/// Claude Code's five levels, and Codex's `ultra` for a Codex pane set to it.
fn valid_effort(effort: &str) -> bool {
    mighty_core::wire::CODEX_EFFORTS.contains(&effort)
}

/// C# `BuildPayload`: the JSON the command reads, in the CLI's shape. `now` decides which rate-limit resets are still ahead.
pub fn build_payload(ctx: &StatusLineContext, now: chrono::DateTime<chrono::Utc>) -> String {
    let mut out = JsonOut::new();
    let opt_str = |out: &mut JsonOut, value: Option<&str>| match value {
        Some(text) => out.str(text),
        None => out.null(),
    };
    out.begin_object();
    out.name("hook_event_name");
    out.str("Status");
    out.name("session_id");
    out.str(&ctx.session_id);
    out.name("transcript_path");
    opt_str(&mut out, ctx.transcript_path.as_deref());
    out.name("cwd");
    opt_str(&mut out, ctx.cwd.as_deref());
    out.name("model");
    out.begin_object();
    out.name("id");
    out.str(ctx.model_id.as_deref().unwrap_or(""));
    out.name("display_name");
    out.str(ctx.model_name.as_deref().unwrap_or(""));
    out.end_object();
    out.name("workspace");
    out.begin_object();
    out.name("current_dir");
    out.str(ctx.cwd.as_deref().unwrap_or(""));
    out.name("project_dir");
    out.str(ctx.project_dir.as_deref().unwrap_or(""));
    out.end_object();
    out.name("version");
    opt_str(&mut out, ctx.version.as_deref());
    out.name("cost");
    out.begin_object();
    out.name("total_cost_usd");
    out.float(ctx.cost_usd.unwrap_or(0.0));
    out.name("total_duration_ms");
    out.int(ctx.duration_ms.unwrap_or(0));
    out.name("total_api_duration_ms");
    out.int(ctx.api_duration_ms.unwrap_or(0));
    out.name("total_lines_added");
    out.int(0);
    out.name("total_lines_removed");
    out.int(0);
    out.end_object();
    out.name("fast_mode");
    out.bool(ctx.fast_mode);
    if let Some(style) = &ctx.output_style {
        out.name("output_style");
        out.begin_object();
        out.name("name");
        out.str(style);
        out.end_object();
    }
    if let Some(enabled) = ctx.thinking_enabled {
        out.name("thinking");
        out.begin_object();
        out.name("enabled");
        out.bool(enabled);
        out.end_object();
    }
    let size = ctx.context_window_tokens.unwrap_or(200_000);
    let usage = ctx.context_used_tokens.map(|used| {
        let percent = (used as f64 / size.max(1) as f64 * 100.0).clamp(0.0, 100.0);
        (used, percent)
    });
    out.name("exceeds_200k_tokens");
    out.bool(usage.is_some_and(|(used, _)| used > 200_000));
    out.name("context_window");
    out.begin_object();
    out.name("total_input_tokens");
    out.int(ctx.input_tokens.unwrap_or(0));
    out.name("total_output_tokens");
    out.int(ctx.output_tokens.unwrap_or(0));
    out.name("context_window_size");
    out.int(size);
    match usage {
        Some((used, percent)) => {
            out.name("current_usage");
            out.begin_object();
            out.name("input_tokens");
            out.int(used);
            out.name("output_tokens");
            out.int(0);
            out.name("cache_creation_input_tokens");
            out.int(0);
            out.name("cache_read_input_tokens");
            out.int(0);
            out.end_object();
            // .NET's Math.Round rounds a half to even.
            out.name("used_percentage");
            out.float((percent * 10.0).round_ties_even() / 10.0);
            out.name("remaining_percentage");
            out.float(((100.0 - percent) * 10.0).round_ties_even() / 10.0);
        }
        None => {
            for name in ["current_usage", "used_percentage", "remaining_percentage"] {
                out.name(name);
                out.null();
            }
        }
    }
    out.end_object();
    if let Some(effort) = ctx.effort.as_deref().filter(|effort| valid_effort(effort)) {
        out.name("effort");
        out.begin_object();
        out.name("level");
        out.str(effort);
        out.end_object();
    }
    if let Some(limits) = ctx.rate_limits.as_ref().filter(|limits| !limits.is_empty()) {
        let mut kept: Vec<(&str, f64, Option<i64>)> = Vec::new();
        for limit in limits {
            let key = match limit.kind.as_str() {
                "five_hour" | "session" | "5h" | "primary" => "five_hour",
                "seven_day" | "weekly" | "7d" | "secondary" => "seven_day",
                _ => continue,
            };
            if kept.iter().any(|(known, _, _)| *known == key) {
                continue;
            }
            let Some(percent) = limit.percent_used.filter(|p| p.is_finite()) else { continue };
            let mut resets_at = None;
            if let Some(text) = &limit.resets_at {
                let Some(at) = mighty_core::time::parse(Some(text)).map(|t| t.to_chrono()) else { continue };
                if at <= now {
                    continue;
                }
                resets_at = Some(at.timestamp());
            }
            kept.push((key, percent.clamp(0.0, 100.0), resets_at));
        }
        if !kept.is_empty() {
            out.name("rate_limits");
            out.begin_object();
            for (key, percent, resets_at) in kept {
                out.name(key);
                out.begin_object();
                out.name("used_percentage");
                out.float(percent);
                if let Some(at) = resets_at {
                    out.name("resets_at");
                    out.int(at);
                }
                out.end_object();
            }
            out.end_object();
        }
    }
    out.end_object();
    out.finish()
}
