//! The session info popover's rows (C# `PaneView.WebSessionInfo`, MainWindow.WebShell.Composer.cs:499-538; `UsageScope`,
//! MainWindow.SessionInfo.cs:249): the pane's title, agent and state, its context, model, latest request time, workspace and path,
//! the tokens the CLI reported with their scope, the cost, the ids, and where the numbers came from. Owner: W6-A.

use mighty_core::locale::Locale;
use mighty_core::model::{RunSession, Workspace};
use mighty_core::model_label::{self, ModelOption};
use mighty_core::shell::Tone;
use serde_json::{Map, Value, json};

use crate::runtime::{is_beta, provider_label, state_label};

/// What the popover reads besides the pane.
pub struct Inputs<'a> {
    pub locale: Locale,
    pub pane: &'a RunSession,
    pub workspace: Option<&'a Workspace>,
    /// The provider's model list as the CLI has it (`PaneRuntime(..).ModelCatalog ?? Fallback`).
    pub models: &'a [ModelOption],
    /// `RunTiming.Label()` when the pane's timing is valid.
    pub elapsed: Option<String>,
    /// The user's number grouping (`N0` in `CultureInfo.CurrentCulture`): the separator and the group sizes.
    pub grouping: Option<&'a (String, Vec<usize>)>,
    /// The user's decimal separator (`NumberDecimalSeparator` in `CultureInfo.CurrentCulture`), when the OS says.
    pub decimal: Option<&'a str>,
    /// `ToLocalTime().ToString("T", CurrentCulture)` of a reported time, when it reads.
    pub long_time: &'a dyn Fn(&str) -> Option<String>,
}

/// `{value:N0}`: digits grouped by the culture's sizes (the last size repeating; a 0 stops grouping), with its separator; by three
/// with a comma when the OS does not say.
pub fn n0(value: i64, grouping: Option<&(String, Vec<usize>)>) -> String {
    let (separator, sizes) = match grouping {
        Some((separator, sizes)) if !sizes.is_empty() => (separator.as_str(), sizes.as_slice()),
        _ => (",", &[3][..]),
    };
    let digits = value.unsigned_abs().to_string();
    let mut groups: Vec<&str> = vec![];
    let mut end = digits.len();
    let mut at = 0;
    while end > 0 {
        let size = sizes.get(at).or(sizes.last()).copied().unwrap_or(3);
        if size == 0 {
            groups.push(&digits[..end]);
            break;
        }
        let start = end.saturating_sub(size);
        groups.push(&digits[start..end]);
        end = start;
        at += 1;
    }
    groups.reverse();
    format!("{}{}", if value < 0 { "-" } else { "" }, groups.join(separator))
}

/// `UsageScope`.
fn usage_scope(scope: Option<&str>, l: &Locale) -> String {
    l.get(match scope {
        Some("run") => "composer.sessionInfo.scopeRun",
        Some("session") => "composer.sessionInfo.scopeSession",
        Some("response") => "composer.sessionInfo.scopeResponse",
        _ => "composer.sessionInfo.scopeUnknown",
    })
}

/// `$"${amount:0.####}"` in the user's culture: the amount's shortest digits rounded at the fourth decimal half away from zero, as
/// .NET rounds a custom format's digits (not the binary value: 0.00005 is "0.0001"), trailing zeros dropped, the user's decimal
/// separator (a "." when the OS does not say).
fn dollars(amount: f64, decimal: Option<&str>) -> String {
    if !amount.is_finite() {
        return format!("${amount}");
    }
    let text = format!("{}", amount.abs());
    let (whole, fraction) = text.split_once('.').unwrap_or((&text, ""));
    let mut digits: Vec<u8> = whole.bytes().chain(fraction.bytes().take(4)).map(|b| b - b'0').collect();
    let mut kept = fraction.len().min(4);
    if fraction.as_bytes().get(4).is_some_and(|d| *d >= b'5') {
        let mut at = digits.len();
        loop {
            if at == 0 {
                digits.insert(0, 1);
                break;
            }
            at -= 1;
            if digits[at] == 9 {
                digits[at] = 0;
            } else {
                digits[at] += 1;
                break;
            }
        }
    }
    while kept > 0 && digits.last() == Some(&0) {
        digits.pop();
        kept -= 1;
    }
    let split = digits.len() - kept;
    let whole: String = digits[..split].iter().map(|d| char::from(b'0' + d)).collect();
    let fraction: String = digits[split..].iter().map(|d| char::from(b'0' + d)).collect();
    let sign = if amount < 0.0 && digits.iter().any(|d| *d != 0) { "-" } else { "" };
    if fraction.is_empty() { format!("${sign}{whole}") } else { format!("${sign}{whole}{}{fraction}", decimal.unwrap_or(".")) }
}

/// `WebSessionInfo`: the popover's object as C#'s anonymous object writes it (nulls left out).
pub fn session_info(inputs: &Inputs) -> Value {
    let (pane, l) = (inputs.pane, &inputs.locale);
    let usage = pane.session_usage.as_ref().filter(|u| u.provider == pane.provider());
    let group = |n: i64| n0(n, inputs.grouping);
    let context = match usage.map(|u| (u.context_used_tokens, u.context_window_tokens)) {
        Some((Some(used), Some(limit))) => {
            l.get_with("composer.sessionInfo.contextTokens", &[("used", &group(used)), ("limit", &group(limit))])
        }
        Some((Some(used), None)) => l.get_with("composer.sessionInfo.contextUsedOnly", &[("tokens", &group(used))]),
        Some((None, Some(limit))) => l.get_with("composer.sessionInfo.contextLimitOnly", &[("tokens", &group(limit))]),
        _ => l.get("composer.sessionInfo.contextUnavailable"),
    };
    let mut rows = vec![];
    let mut row = |key: &str, label: String, value: Option<String>| {
        if let Some(value) = value.filter(|v| !v.is_empty()) {
            rows.push(json!({"key": key, "label": label, "value": value}));
        }
    };
    let model_label_key = if usage.and_then(|u| u.model.as_ref()).is_none() {
        "composer.sessionInfo.selectedModel"
    } else {
        "composer.sessionInfo.usedModel"
    };
    let model = match usage.and_then(|u| u.model.as_deref()) {
        Some(model) => model_label::text(model, None, None),
        None => model_label::selection(pane, inputs.models, l),
    };
    row("model", l.get(model_label_key), Some(model));
    row("elapsed", l.get("composer.sessionInfo.latestRequestTime"), inputs.elapsed.clone());
    row("workspace", l.get("composer.sessionInfo.workspace"), inputs.workspace.map(|w| w.name().to_string()));
    row("host", l.get("remote.sessionInfo.host"), inputs.workspace.and_then(|w| w.remote.as_ref()).and_then(|r| r.host_name.clone()));
    row("path", String::new(), inputs.workspace.map(|w| w.path().to_string()));
    let tokens = usage.is_some_and(|u| {
        [u.input_tokens, u.output_tokens, u.cache_read_tokens, u.cache_write_tokens, u.reasoning_tokens, u.total_tokens]
            .iter()
            .any(Option::is_some)
    });
    let token_rows: Vec<Value> = usage
        .map(|u| {
            [
                ("input", "composer.sessionInfo.input", u.input_tokens),
                ("output", "composer.sessionInfo.output", u.output_tokens),
                ("cache-read", "composer.sessionInfo.cacheRead", u.cache_read_tokens),
                ("cache-write", "composer.sessionInfo.cacheWrite", u.cache_write_tokens),
                ("reasoning", "composer.sessionInfo.reasoning", u.reasoning_tokens),
                ("total", "composer.sessionInfo.total", u.total_tokens),
            ]
            .into_iter()
            .filter_map(|(key, label, value)| value.map(|n| json!({"key": key, "label": l.get(label), "value": group(n)})))
            .collect()
        })
        .unwrap_or_default();
    let cost = usage.and_then(|u| u.cost_usd).filter(|c| c.is_finite() && *c >= 0.0);
    let received = usage
        .and_then(|u| (inputs.long_time)(&u.updated_at))
        .map(|time| l.get_with("composer.sessionInfo.lastReceived", &[("time", &time)]));
    let no_usage =
        usage.is_none_or(|u| !tokens && u.cost_usd.is_none() && u.context_used_tokens.is_none() && u.context_window_tokens.is_none());
    let resume = usage.and_then(|u| u.provider_session_id.clone()).or_else(|| pane.resume_id.clone()).unwrap_or_default();
    let identifiers: Vec<Value> =
        [(l.get("composer.sessionInfo.mightySessionId"), pane.id().to_string()), (l.get("composer.sessionInfo.cliSessionId"), resume)]
            .into_iter()
            .filter(|(_, value)| !value.is_empty())
            .map(|(label, value)| json!({"label": label, "value": value}))
            .collect();
    let tone = Tone::of(pane.status()).name();
    let mut info = Map::new();
    let mut put = |key: &str, value: Option<Value>| {
        if let Some(value) = value {
            info.insert(key.to_string(), value);
        }
    };
    put("title", Some(json!(pane.title())));
    put("provider", Some(json!(pane.provider())));
    put("providerLabel", Some(json!(provider_label(pane.provider()))));
    put("beta", Some(json!(is_beta(pane.provider()))));
    put("status", Some(json!(state_label(pane.status(), l))));
    put("tone", Some(json!(format!("{}{}", tone[..1].to_uppercase(), &tone[1..]))));
    put("contextPercent", usage.and_then(crate::composer::context_percent).map(|p| json!(p)));
    put("context", Some(json!(context)));
    put("rows", Some(Value::Array(rows)));
    put(
        "tokenScope",
        tokens.then(|| {
            json!(l.get_with("composer.sessionInfo.tokenScope", &[("scope", &usage_scope(usage.map(|u| u.token_scope.as_str()), l))]))
        }),
    );
    put("tokens", Some(Value::Array(token_rows)));
    let detailed = usage.is_some_and(|u| u.cache_read_tokens.or(u.cache_write_tokens).or(u.reasoning_tokens).is_some());
    put("note", (tokens && detailed).then(|| json!(l.get("composer.sessionInfo.note"))));
    put(
        "cost",
        cost.map(|amount| {
            let scope = usage_scope(usage.and_then(|u| u.cost_scope.as_deref()), l);
            json!({"label": l.get_with("composer.sessionInfo.costWithScope", &[("scope", &scope)]), "value": dollars(amount, inputs.decimal)})
        }),
    );
    put("noUsage", Some(json!(no_usage)));
    put("identifiers", Some(Value::Array(identifiers)));
    put("reported", usage.map(|_| json!(l.get_with("composer.sessionInfo.reported", &[("provider", provider_label(pane.provider()))]))));
    put("source", usage.map(|u| json!(u.source)));
    put("received", received.map(|r| json!(r)));
    Value::Object(info)
}

#[cfg(test)]
mod tests {
    use mighty_core::Wire;
    use mighty_core::model::AppSnapshot;

    use super::*;

    fn pane(usage: Option<Value>) -> RunSession {
        let mut session =
            json!({"id": "s1", "workspaceId": "w1", "title": "Fix it", "provider": "claude", "model": "default", "status": "completed"});
        if let Some(usage) = usage {
            session["sessionUsage"] = usage;
        }
        let state = AppSnapshot::read(
            &json!({"version": 1, "workspaces": [{"id": "w1", "name": "Repo", "path": "C:\\Repo"}], "sessions": [session]}),
        )
        .unwrap();
        state.sessions()[0].as_ref().clone()
    }

    #[test]
    fn n0_groups_as_the_culture_says() {
        assert_eq!(n0(1234567, None), "1,234,567");
        assert_eq!(n0(999, None), "999");
        assert_eq!(n0(-1234, None), "-1,234");
        let india = (",".to_string(), vec![3, 2]);
        assert_eq!(n0(12345678, Some(&india)), "1,23,45,678");
        let space = ("\u{a0}".to_string(), vec![3]);
        assert_eq!(n0(1234, Some(&space)), "1\u{a0}234");
        assert_eq!(dollars(0.12345, None), "$0.1235");
        assert_eq!(dollars(2.0, None), "$2");
        // Review L9: .NET's `0.####` rounds the shortest digits half away from zero (Rust's `{:.4}` rounds the binary value, half to
        // even: 0.00005 → "0.0000", 1.00005 → "1.0000"), and writes the user's decimal separator.
        assert_eq!(dollars(0.00005, None), "$0.0001");
        assert_eq!(dollars(1.00005, None), "$1.0001");
        assert_eq!(dollars(0.99995, None), "$1");
        assert_eq!(dollars(9.99999, Some(",")), "$10");
        assert_eq!(dollars(0.1, Some(",")), "$0,1");
        assert_eq!(dollars(0.00004, None), "$0");
        assert_eq!(dollars(12.5, Some(",")), "$12,5");
    }

    /// A pane with no usage: the context unavailable, the selected model, the workspace and its path, no tokens, the ids.
    #[test]
    fn a_pane_without_usage_says_so() {
        let l = Locale::new("en");
        let pane = pane(None);
        let workspace = Workspace::read(&json!({"id": "w1", "name": "Repo", "path": "C:\\Repo"})).unwrap();
        let info = session_info(&Inputs {
            locale: l,
            pane: &pane,
            workspace: Some(&workspace),
            models: &[],
            elapsed: None,
            grouping: None,
            decimal: None,
            long_time: &|_| None,
        });
        assert_eq!(info["context"], l.get("composer.sessionInfo.contextUnavailable"));
        assert_eq!(info["noUsage"], true);
        assert_eq!(info["tone"], "Done");
        assert!(info.get("tokenScope").is_none() && info.get("cost").is_none() && info.get("reported").is_none(), "nulls are left out");
        let keys: Vec<&str> = info["rows"].as_array().unwrap().iter().map(|r| r["key"].as_str().unwrap()).collect();
        assert_eq!(keys, vec!["model", "workspace", "path"]);
        assert_eq!(info["identifiers"][0]["value"], "s1");
    }

    /// A pane with usage: the context in grouped numbers, the tokens with their scope and the note, the cost, where they came from.
    #[test]
    fn a_pane_with_usage_lists_its_numbers() {
        let l = Locale::new("en");
        let usage = json!({
            "provider": "claude", "source": "stream", "tokenScope": "session", "model": "claude-opus-5-5", "providerSessionId": "cli-1",
            "inputTokens": 12345, "outputTokens": 678, "cacheReadTokens": 1000, "contextUsedTokens": 50000, "contextWindowTokens": 200000,
            "costUSD": 0.5, "costScope": "run", "updatedAt": "2026-11-14T03:04:05Z"
        });
        let pane = pane(Some(usage));
        let info = session_info(&Inputs {
            locale: l,
            pane: &pane,
            workspace: None,
            models: &[],
            elapsed: Some("01:05".into()),
            grouping: None,
            decimal: None,
            long_time: &|_| Some("3:04:05 AM".into()),
        });
        assert_eq!(info["context"], l.get_with("composer.sessionInfo.contextTokens", &[("used", "50,000"), ("limit", "200,000")]));
        assert_eq!(info["contextPercent"], 25.0);
        assert_eq!(info["tokens"][0]["value"], "12,345");
        assert!(info.get("note").is_some());
        assert_eq!(info["cost"]["value"], "$0.5");
        assert_eq!(info["identifiers"][1]["value"], "cli-1");
        assert_eq!(info["received"], l.get_with("composer.sessionInfo.lastReceived", &[("time", "3:04:05 AM")]));
        assert_eq!(info["rows"][1]["key"], "elapsed");
    }
}
