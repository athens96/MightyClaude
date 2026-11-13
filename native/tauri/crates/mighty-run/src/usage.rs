//! C# `SessionUsageSupport` and `SessionUsageTracker` (SessionUsage.cs:34-269): what an agent's stream says about its tokens, cost,
//! context and rate limits, one figure set per process. Counts replace snapshots; resume totals are never summed.

use mighty_core::json::Wire;
use mighty_core::model::{SessionRateLimit, SessionUsage};
use mighty_core::{normalize, time, wire};
use serde_json::Value;

use crate::meta;
use crate::stream;

pub const MAXIMUM_TOKENS: i64 = 9_000_000_000_000;

/// `SessionUsageSupport.Scope`.
pub fn scope(value: Option<&str>) -> bool {
    matches!(value, Some("response" | "run" | "session"))
}

/// `SessionUsageSupport.Count`.
pub fn count(value: Option<i64>) -> Option<i64> {
    value.filter(|v| (0..=MAXIMUM_TOKENS).contains(v))
}

fn count_of(value: &Value, key: &str) -> Option<i64> {
    count(meta::integer(value, key))
}

/// `SessionUsageSupport.Sum`: null when any part is, or when there is none.
pub fn sum(values: &[Option<i64>]) -> Option<i64> {
    if values.is_empty() || values.iter().any(Option::is_none) {
        return None;
    }
    let mut total: i64 = 0;
    for value in values.iter().flatten() {
        total = total.checked_add(*value)?;
    }
    count(Some(total))
}

/// `SessionUsageSupport.Cost`.
pub fn cost(value: Option<f64>) -> Option<f64> {
    value.filter(|n| n.is_finite() && (0.0..=1_000_000_000.0).contains(n))
}

/// `SessionUsageSupport.Normalize`.
pub fn normalized(value: &SessionUsage) -> Option<SessionUsage> {
    normalize::session_usage(value)
}

/// `SessionUsageSupport.RateLimitWindows`: `unifiedWindows` keyed by window kind (utilization 0…1, reset in epoch seconds),
/// ordered by kind, at most 16; without a usable one, the single window `rateLimitType` names.
pub fn rate_limit_windows(info: Option<&Value>) -> Vec<SessionRateLimit> {
    fn window(kind: &str, row: &Value) -> Option<SessionRateLimit> {
        let percent = meta::number(row, "utilization").filter(|u| (0.0..=1.0).contains(u)).map(|u| u * 100.0);
        let reset = meta::number(row, "resetsAt")
            .filter(|s| *s > 0.0 && *s < 4_102_444_800.0)
            .and_then(|s| chrono::DateTime::from_timestamp(s as i64, 0).map(|t| t.format("%Y-%m-%dT%H:%M:%SZ").to_string()));
        ((percent.is_some() || reset.is_some()) && !kind.is_empty() && kind.len() <= 80).then(|| SessionRateLimit {
            kind: kind.to_string(),
            percent_used: percent,
            resets_at: reset,
        })
    }
    let Some(info) = info else { return vec![] };
    if let Some(Value::Object(unified)) = meta::prop(info, "unifiedWindows") {
        let mut names: Vec<&String> = unified.keys().collect();
        names.sort_by(|a, b| normalize::ordinal(a, b));
        let windows: Vec<SessionRateLimit> = names.into_iter().take(16).filter_map(|name| window(name, &unified[name.as_str()])).collect();
        if !windows.is_empty() {
            return windows;
        }
    }
    match meta::text(info, "rateLimitType") {
        Some(kind) => window(kind, info).into_iter().collect(),
        None => vec![],
    }
}

fn initial(provider: &str) -> SessionUsage {
    let source = match provider {
        "gemini" => "antigravity.stream-json".to_string(),
        "pi" => "pi.rpc".to_string(),
        "opencode" | "hermes" => format!("{provider}.acp"),
        _ => format!("{provider}.stream-json"),
    };
    let scope = match provider {
        "claude" => "response",
        "pi" | "opencode" => "run",
        _ => "session",
    };
    SessionUsage {
        provider: provider.to_string(),
        source,
        token_scope: scope.to_string(),
        model: None,
        selected_model: None,
        provider_session_id: None,
        input_tokens: None,
        output_tokens: None,
        cache_read_tokens: None,
        cache_write_tokens: None,
        reasoning_tokens: None,
        total_tokens: None,
        context_used_tokens: None,
        context_window_tokens: None,
        cost_usd: None,
        cost_scope: None,
        rate_limits: None,
        rate_limits_updated_at: None,
        updated_at: time::now_o(),
    }
}

pub type UsageCallback = Box<dyn FnMut(SessionUsage) + Send>;

/// C# `SessionUsageTracker`: one process's figures.
pub struct SessionUsageTracker {
    value: SessionUsage,
    emitted: Option<String>,
    callback: Option<UsageCallback>,
    last_mod_sequence: i64,
    authoritative_context: bool,
    result_seen: bool,
}

impl SessionUsageTracker {
    pub fn new(provider: &str, callback: Option<UsageCallback>) -> SessionUsageTracker {
        SessionUsageTracker {
            value: initial(provider),
            emitted: None,
            callback,
            last_mod_sequence: -1,
            authoritative_context: false,
            result_seen: false,
        }
    }

    /// One stream frame.
    pub fn consume(&mut self, root: &Value) {
        if self.callback.is_none() {
            return;
        }
        let kind = meta::text(root, "type");
        match self.value.provider.as_str() {
            "claude" => self.claude(root, kind),
            "pi" => self.pi(root, kind),
            "opencode" | "hermes" => self.acp(root, kind),
            "codex" => {
                if kind == Some("thread.started") {
                    self.identity(meta::text(root, "thread_id"));
                    self.publish();
                }
                if kind == Some("turn.completed")
                    && let Some(usage) = meta::prop(root, "usage")
                    && let (Some(input), Some(output)) = (count_of(usage, "input_tokens"), count_of(usage, "output_tokens"))
                {
                    self.value.source = "codex.exec".into();
                    self.value.token_scope = "session".into();
                    self.value.input_tokens = Some(input);
                    self.value.output_tokens = Some(output);
                    self.value.cache_read_tokens = count_of(usage, "cached_input_tokens");
                    self.value.cache_write_tokens = count_of(usage, "cache_write_input_tokens");
                    self.value.reasoning_tokens = count_of(usage, "reasoning_output_tokens");
                    self.value.total_tokens = sum(&[Some(input), Some(output)]);
                    self.publish();
                }
            }
            _ => self.antigravity(root),
        }
    }

    fn antigravity(&mut self, root: &Value) {
        let kind = meta::text(root, "event");
        if kind == Some("init") {
            self.identity(meta::text(root, "conversation_id"));
            self.model(meta::text_of(meta::prop(root, "init"), "model"));
            self.publish();
        }
        let result = meta::prop(root, "result");
        let usage = result.and_then(|r| meta::prop(r, "usage"));
        if kind == Some("result")
            && let Some(usage) = usage.filter(|u| u.is_object())
            && let (Some(fresh), Some(output)) = (count_of(usage, "input_tokens"), count_of(usage, "output_tokens"))
            && let Some(cached) =
                if meta::prop(usage, "cache_read_tokens").is_none() { Some(0) } else { count_of(usage, "cache_read_tokens") }
            && let Some(input) = sum(&[Some(fresh), Some(cached)])
        {
            self.identity(meta::text_of(result, "conversation_id"));
            self.value.source = "antigravity.stream-json".into();
            self.value.token_scope = "session".into();
            self.value.input_tokens = Some(input);
            self.value.output_tokens = Some(output);
            self.value.cache_read_tokens = Some(cached);
            self.value.reasoning_tokens = count_of(usage, "thinking_tokens");
            self.value.total_tokens = sum(&[Some(input), Some(output)]);
            self.publish();
        }
    }

    fn pi(&mut self, root: &Value, kind: Option<&str>) {
        if kind == Some("response")
            && meta::text(root, "command") == Some("get_state")
            && matches!(meta::prop(root, "success"), Some(Value::Bool(true)))
            && let Some(data) = meta::prop(root, "data").filter(|d| d.is_object())
        {
            self.identity(meta::text(data, "sessionId"));
            if let Some(model) = meta::prop(data, "model").filter(|m| m.is_object()) {
                self.model(meta::text(model, "id"));
                self.value.context_window_tokens = count_of(model, "contextWindow");
            }
            self.publish();
            return;
        }
        if kind != Some("message_end") {
            return;
        }
        let Some(message) = meta::prop(root, "message").filter(|m| m.is_object()) else { return };
        if meta::text(message, "role") != Some("assistant") {
            return;
        }
        let Some(usage) = meta::prop(message, "usage").filter(|u| u.is_object()) else { return };
        let (Some(fresh), Some(output)) = (count_of(usage, "input"), count_of(usage, "output")) else { return };
        let read = if meta::prop(usage, "cacheRead").is_none() { Some(0) } else { count_of(usage, "cacheRead") };
        let written = if meta::prop(usage, "cacheWrite").is_none() { Some(0) } else { count_of(usage, "cacheWrite") };
        let (Some(read), Some(written)) = (read, written) else { return };
        let Some(input) = sum(&[Some(fresh), Some(read), Some(written)]) else { return };
        self.model(meta::text(message, "model"));
        let input_total = self.value.input_tokens.unwrap_or(0) + input;
        let output_total = self.value.output_tokens.unwrap_or(0) + output;
        self.value.input_tokens = Some(input_total);
        self.value.output_tokens = Some(output_total);
        self.value.cache_read_tokens = Some(self.value.cache_read_tokens.unwrap_or(0) + read);
        self.value.cache_write_tokens = Some(self.value.cache_write_tokens.unwrap_or(0) + written);
        self.value.total_tokens = sum(&[Some(input_total), Some(output_total)]);
        self.value.context_used_tokens = Some(input);
        if let Some(cost) = cost(meta::prop(usage, "cost").and_then(|c| meta::number(c, "total"))) {
            self.value.cost_usd = Some(self.value.cost_usd.unwrap_or(0.0) + cost);
            self.value.cost_scope = Some("run".into());
        }
        self.publish();
    }

    fn acp(&mut self, root: &Value, kind: Option<&str>) {
        fn option(options: Option<&Value>, matches: impl Fn(&Value) -> bool) -> Option<&Value> {
            options.and_then(Value::as_array)?.iter().find(|o| o.is_object() && matches(o))
        }
        match kind {
            Some("acp.session") => {
                self.identity(meta::text(root, "sessionId"));
                let options = meta::prop(root, "configOptions");
                let model = option(options, |o| meta::text(o, "id") == Some("model"))
                    .or_else(|| option(options, |o| meta::text(o, "category") == Some("model")));
                if let Some(model) = model {
                    self.model(meta::text(model, "currentValue"));
                }
                self.publish();
            }
            Some("acp.update") => {
                let Some(update) = meta::prop(root, "update").filter(|u| u.is_object()) else { return };
                if meta::text(update, "sessionUpdate") == Some("config_option_update") {
                    if let Some(chosen) = option(meta::prop(update, "configOptions"), |o| meta::text(o, "id") == Some("model")) {
                        self.model(meta::text(chosen, "currentValue"));
                    }
                    self.publish();
                    return;
                }
                if meta::text(update, "sessionUpdate") != Some("usage_update") {
                    return;
                }
                if let Some(used) = count_of(update, "used") {
                    self.value.context_used_tokens = Some(used);
                }
                if let Some(size) = count_of(update, "size").filter(|s| *s > 0) {
                    self.value.context_window_tokens = Some(size);
                }
                if let Some(cost_value) = meta::prop(update, "cost").filter(|c| c.is_object())
                    && meta::text(cost_value, "currency").unwrap_or("USD").eq_ignore_ascii_case("USD")
                    && let Some(amount) = cost(meta::number(cost_value, "amount"))
                {
                    self.value.cost_usd = Some(amount);
                    self.value.cost_scope = Some("session".into());
                }
                self.publish();
            }
            Some("acp.prompt_result") => {
                let Some(usage) = meta::prop(root, "usage").filter(|u| u.is_object()) else { return };
                let (Some(input), Some(output)) = (count_of(usage, "inputTokens"), count_of(usage, "outputTokens")) else { return };
                let read = count_of(usage, "cachedReadTokens");
                let written = count_of(usage, "cachedWriteTokens");
                if self.value.provider == "hermes" {
                    let total = count_of(usage, "totalTokens");
                    if total.unwrap_or(0) == 0 && input == 0 && output == 0 {
                        return;
                    }
                    let session_input = match total {
                        Some(t) if t >= output => Some(t - output),
                        _ => sum(&[Some(input), Some(read.unwrap_or(0))]),
                    };
                    self.value.token_scope = "session".into();
                    self.value.input_tokens = session_input;
                    self.value.output_tokens = Some(output);
                    self.value.cache_read_tokens = read;
                    self.value.reasoning_tokens = count_of(usage, "thoughtTokens");
                    self.value.total_tokens = sum(&[session_input, Some(output)]);
                    self.publish();
                    return;
                }
                let run_input = sum(&[Some(input), Some(read.unwrap_or(0)), Some(written.unwrap_or(0))]);
                self.value.token_scope = "run".into();
                self.value.input_tokens = run_input;
                self.value.output_tokens = Some(output);
                self.value.cache_read_tokens = read;
                self.value.cache_write_tokens = written;
                self.value.reasoning_tokens = count_of(usage, "thoughtTokens");
                self.value.total_tokens = count_of(usage, "totalTokens").or_else(|| sum(&[run_input, Some(output)]));
                self.publish();
            }
            _ => {}
        }
    }

    /// A Mods `session.usage` event of the main session: the context in use and its window, the session's cost and rate limits.
    pub fn consume_mod(&mut self, root: &Value) {
        if self.value.provider != "claude" || meta::text(root, "event") != Some("session.usage") || meta::text(root, "agentId").is_some() {
            return;
        }
        let Some(incoming) = meta::prop(root, "usage").filter(|u| u.is_object()).and_then(SessionUsage::from_json) else { return };
        if incoming.provider != "claude" {
            return;
        }
        self.identity(meta::text(root, "claudeSessionId"));
        if let Some(sequence) = meta::safe_integer(root, "sequence") {
            if sequence <= self.last_mod_sequence {
                return;
            }
            self.last_mod_sequence = sequence;
        }
        self.authoritative_context = true;
        self.value.context_used_tokens = incoming.context_used_tokens;
        self.value.context_window_tokens = incoming.context_window_tokens;
        if incoming.model.is_some() {
            self.value.model = incoming.model.clone();
        }
        self.value.source = "claude.mods+stream-json".into();
        if let Some(cost) = incoming.cost_usd {
            self.value.cost_usd = Some(cost);
            self.value.cost_scope = Some("session".into());
        }
        if incoming.rate_limits.is_some() {
            self.value.rate_limits = incoming.rate_limits.clone();
            self.value.rate_limits_updated_at = incoming.rate_limits_updated_at.clone().or(Some(incoming.updated_at.clone()));
        }
        self.publish();
    }

    fn claude(&mut self, root: &Value, kind: Option<&str>) {
        if !meta::top_level(root) {
            return;
        }
        self.identity(meta::text(root, "session_id"));
        let subtype = meta::text(root, "subtype");
        if kind == Some("system") && subtype == Some("init") {
            self.model(meta::text(root, "model"));
            self.publish();
        }
        if kind == Some("system") && subtype == Some("compact_boundary") {
            self.value.context_used_tokens = None;
            self.authoritative_context = false;
            self.publish();
        }
        if kind == Some("rate_limit_event") {
            let windows = rate_limit_windows(meta::prop(root, "rate_limit_info"));
            if !windows.is_empty() {
                self.value.rate_limits = Some(windows);
                self.value.rate_limits_updated_at = Some(time::now_o());
                self.publish();
            }
        }
        if kind == Some("assistant")
            && let Some(message) = meta::prop(root, "message").filter(|m| m.is_object())
        {
            let old = self.value.model.clone();
            self.model(meta::text(message, "model"));
            if old != self.value.model {
                self.value.context_window_tokens = None;
                self.authoritative_context = false;
            }
            if let Some(usage) = meta::prop(message, "usage").filter(|u| u.is_object()) {
                let Some(input) = claude_input(usage) else { return };
                if count_of(usage, "output_tokens").is_none() {
                    return;
                }
                if !self.result_seen {
                    self.claude_counts(usage);
                    self.value.token_scope = "response".into();
                }
                self.value.context_used_tokens = Some(input);
            }
            self.publish();
        }
        if kind != Some("result") || stream::is_notification_result(root) {
            return;
        }
        self.result_seen = true;
        self.value.token_scope = "run".into();
        match meta::prop(root, "modelUsage") {
            Some(Value::Object(models)) if (1..=128).contains(&models.len()) => {
                if models.values().all(Value::is_object) {
                    let rows: Vec<(&String, &Value)> = models.iter().collect();
                    let per = |f: &dyn Fn(&Value) -> Option<i64>| sum(&rows.iter().map(|(_, row)| f(row)).collect::<Vec<_>>());
                    self.value.input_tokens = per(&|row| {
                        sum(&[
                            count_of(row, "inputTokens"),
                            count_of(row, "cacheReadInputTokens"),
                            count_of(row, "cacheCreationInputTokens"),
                        ])
                    });
                    self.value.output_tokens = per(&|row| count_of(row, "outputTokens"));
                    self.value.cache_read_tokens = per(&|row| count_of(row, "cacheReadInputTokens"));
                    self.value.cache_write_tokens = per(&|row| count_of(row, "cacheCreationInputTokens"));
                    self.value.reasoning_tokens = per(&|row| count_of(row, "thinkingTokens"));
                    self.value.total_tokens = sum(&[self.value.input_tokens, self.value.output_tokens]);
                    if self.value.model.is_none() && rows.len() == 1 {
                        let name = rows[0].0.clone();
                        self.model(Some(&name));
                    }
                    if !self.authoritative_context
                        && let Some(model) = self.value.model.clone()
                        && let Some(row) = models.get(&model)
                    {
                        self.value.context_window_tokens = count_of(row, "contextWindow");
                    }
                }
            }
            _ => {
                if let Some(usage) = meta::prop(root, "usage").filter(|u| u.is_object()) {
                    self.claude_counts(usage);
                }
            }
        }
        if self.value.cost_scope.as_deref() != Some("session")
            && let Some(cost) = cost(meta::number(root, "total_cost_usd"))
        {
            self.value.cost_usd = Some(cost);
            self.value.cost_scope = Some("run".into());
        }
        self.publish();
    }

    fn claude_counts(&mut self, usage: &Value) {
        self.value.input_tokens = claude_input(usage);
        self.value.output_tokens = count_of(usage, "output_tokens");
        self.value.cache_read_tokens = count_of(usage, "cache_read_input_tokens");
        self.value.cache_write_tokens = count_of(usage, "cache_creation_input_tokens");
        self.value.reasoning_tokens = None;
        self.value.total_tokens = sum(&[self.value.input_tokens, self.value.output_tokens]);
    }

    fn model(&mut self, model: Option<&str>) {
        if let Some(model) = model.filter(|m| wire::model(m)) {
            self.value.model = Some(model.to_string());
        }
    }

    fn identity(&mut self, id: Option<&str>) {
        let Some(id) = id.filter(|i| wire::identifier(i)) else { return };
        if self.value.provider_session_id.as_deref().is_some_and(|current| current != id) {
            self.value = initial(&self.value.provider);
            self.authoritative_context = false;
            self.result_seen = false;
            self.last_mod_sequence = -1;
        }
        self.value.provider_session_id = Some(id.to_string());
    }

    fn publish(&mut self) {
        if self.callback.is_none() {
            return;
        }
        let Some(clean) = normalized(&self.value) else { return };
        let key = SessionUsage { updated_at: String::new(), ..clean.clone() }.to_json();
        if self.emitted.as_deref() == Some(key.as_str()) {
            return;
        }
        self.emitted = Some(key);
        self.value = SessionUsage { updated_at: time::now_o(), ..clean };
        if let Some(callback) = self.callback.as_mut() {
            callback(self.value.clone());
        }
    }
}

fn claude_input(usage: &Value) -> Option<i64> {
    let read = if meta::prop(usage, "cache_read_input_tokens").is_none() { Some(0) } else { count_of(usage, "cache_read_input_tokens") };
    let created =
        if meta::prop(usage, "cache_creation_input_tokens").is_none() { Some(0) } else { count_of(usage, "cache_creation_input_tokens") };
    sum(&[count_of(usage, "input_tokens"), read, created])
}
