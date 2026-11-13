//! The one display label for a model (C# `ModelLabel`, MightyCode.Core/ModelLabel.cs; macOS ModelLabel.swift): family plus version
//! (`Opus 5.5`, `GPT-6.1 Sol`, `Gemini 3 Pro`). Held to `native/contracts/fixtures/model-labels.json`.

use crate::locale::Locale;
use crate::model::RunSession;

pub const ONE_MILLION_SUFFIX: &str = " (1M)";
const FAMILIES: [&str; 4] = ["opus", "sonnet", "haiku", "fable"];

/// A model a catalogue lists (C# `ModelOption`).
#[derive(Debug, Clone, PartialEq)]
pub struct ModelOption {
    pub value: String,
    pub display_name: String,
    pub resolved_model: Option<String>,
}

/// `ProviderMark.Label`.
pub fn provider_label(provider: &str) -> &'static str {
    match provider {
        "codex" => "Codex",
        "gemini" => "Antigravity",
        "pi" => "pi",
        "opencode" => "opencode",
        "hermes" => "Hermes",
        _ => "Claude",
    }
}

/// The models of `ProviderCatalog.Fallback(provider)` (the list before the CLI gave its own).
pub fn fallback_models(provider: &str, locale: &Locale) -> Vec<ModelOption> {
    let mut models = vec![ModelOption {
        value: "default".into(),
        display_name: locale.get_with("provider.fallback.defaultLabel", &[("name", provider_label(provider))]),
        resolved_model: None,
    }];
    let named = |value: &str, name: &str| ModelOption { value: value.into(), display_name: name.into(), resolved_model: None };
    match provider {
        "gemini" => {
            for (slug, name) in [
                ("gemini-4-argon-medium", "Gemini 4 Argon (Medium)"),
                ("gemini-4-argon-high", "Gemini 4 Argon (High)"),
                ("gemini-4-argon-low", "Gemini 4 Argon (Low)"),
                ("gemini-3.8-flash-high", "Gemini 3.8 Flash (High)"),
                ("gemini-3.8-flash-medium", "Gemini 3.8 Flash (Medium)"),
                ("gemini-3.8-flash-low", "Gemini 3.8 Flash (Low)"),
                ("claude-opus-5-5-low", "Claude Opus 5.5 (Low)"),
                ("claude-opus-5-5-medium", "Claude Opus 5.5 (Medium)"),
                ("claude-opus-5-5-high", "Claude Opus 5.5 (High)"),
                ("claude-sonnet-5-5-low", "Claude Sonnet 5.5 (Low)"),
                ("claude-sonnet-5-5-medium", "Claude Sonnet 5.5 (Medium)"),
                ("claude-sonnet-5-5-high", "Claude Sonnet 5.5 (High)"),
                ("gpt-oss-120b-medium", "GPT-OSS 120B (Medium)"),
            ] {
                models.push(named(slug, name));
            }
        }
        "pi" | "opencode" | "hermes" => {}
        "claude" => models.extend(["best", "fable", "opus", "sonnet", "haiku", "opusplan"].map(|v| named(v, v))),
        _ => models.extend(["gpt-5.6-sol", "gpt-6-astra"].map(|v| named(v, v))),
    }
    models
}

fn capitalized(word: &str) -> String {
    let mut chars = word.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().collect::<String>() + chars.as_str(),
        None => String::new(),
    }
}

fn digits(token: &str) -> bool {
    !token.is_empty() && token.bytes().all(|b| b.is_ascii_digit())
}

fn starts_with_digit(token: &str) -> bool {
    token.as_bytes().first().is_some_and(u8::is_ascii_digit)
}

fn revision(token: &str) -> bool {
    token.len() >= 2 && token.starts_with('v') && digits(&token[1..])
}

fn word(token: &str) -> String {
    if revision(token) { token.to_string() } else { capitalized(token) }
}

fn claude(tokens: &[&str]) -> Option<String> {
    let mut family: Option<&str> = None;
    let mut version = vec![];
    let mut suffix = String::new();
    for (index, token) in tokens.iter().enumerate() {
        if digits(token) {
            if token.len() <= 2 {
                version.push(*token);
            } else if token.len() != 8 {
                return None;
            }
        } else if index == tokens.len() - 1 && family.is_some() && revision(token) {
            suffix = format!(" {token}");
        } else if family.is_none() && token.bytes().all(|b| b.is_ascii_alphabetic()) {
            family = Some(token);
        } else {
            return None;
        }
    }
    let family = family?;
    if version.is_empty() {
        return None;
    }
    Some(format!("{} {}{suffix}", capitalized(family), version.join(".")))
}

fn gpt(tokens: &[&str]) -> Option<String> {
    let first = tokens.first().filter(|t| starts_with_digit(t))?;
    let mut rest: Vec<&str> = tokens[1..].to_vec();
    let n = rest.len();
    if n >= 3
        && digits(rest[n - 3])
        && rest[n - 3].len() == 4
        && digits(rest[n - 2])
        && rest[n - 2].len() == 2
        && digits(rest[n - 1])
        && rest[n - 1].len() == 2
    {
        rest.truncate(n - 3);
    }
    Some(std::iter::once(format!("GPT-{first}")).chain(rest.iter().map(|t| word(t))).collect::<Vec<_>>().join(" "))
}

fn gemini(tokens: &[&str]) -> Option<String> {
    let first = tokens.first().filter(|t| starts_with_digit(t))?;
    let mut rest: &[&str] = &tokens[1..];
    let effort = (rest.len() >= 2 && matches!(rest[rest.len() - 1], "low" | "medium" | "high")).then(|| rest[rest.len() - 1]);
    if effort.is_some() {
        rest = &rest[..rest.len() - 1];
    }
    let mut words = vec![];
    for token in rest {
        if matches!(*token, "preview" | "exp" | "latest") {
            break;
        }
        if !digits(token) {
            words.push(word(token));
        }
    }
    let head = ["Gemini".to_string(), first.to_string()].into_iter().chain(words).collect::<Vec<_>>().join(" ");
    Some(match effort {
        Some(effort) => format!("{head} ({})", capitalized(effort)),
        None => head,
    })
}

/// `ModelLabel.Format`: a full model id read into its label, or None when the id is not one of the shapes read.
pub fn format(id: &str) -> Option<String> {
    let mut core = id.trim().to_lowercase();
    let one_million = core.ends_with("[1m]");
    if one_million {
        core.truncate(core.len() - 4);
    }
    if let Some(slash) = core.rfind('/') {
        core = core[slash + 1..].to_string();
    }
    if let Some(wrapper) = core.rfind("anthropic.") {
        core = core[wrapper + "anthropic.".len()..].to_string();
    }
    if let Some(at) = core.find('@') {
        core.truncate(at);
    }
    if let Some(revision) = core.rfind("-v") {
        let parts: Vec<&str> = core[revision + 2..].split(':').collect();
        if parts.len() == 2 && parts.iter().all(|p| digits(p)) {
            core.truncate(revision);
        }
    }
    let tokens: Vec<&str> = core.split('-').collect();
    if tokens.len() < 2 || tokens.iter().any(|t| t.is_empty()) {
        return None;
    }
    let rest = &tokens[1..];
    let label = match tokens[0] {
        "claude" => claude(rest),
        "gpt" => gpt(rest),
        "gemini" => gemini(rest),
        _ => None,
    }?;
    Some(if one_million { label + ONE_MILLION_SUFFIX } else { label })
}

/// `ModelLabel.Text`.
pub fn text(model: &str, resolved: Option<&str>, fallback: Option<&str>) -> String {
    let value = model.trim();
    if let Some(label) = format(value) {
        return label;
    }
    let resolved_label = resolved.and_then(format);
    let mut alias = value.to_lowercase();
    let one_million = alias.ends_with("[1m]");
    if one_million {
        alias.truncate(alias.len() - 4);
    }
    if FAMILIES.contains(&alias.as_str()) {
        let family = capitalized(&alias);
        let suffix = if one_million { ONE_MILLION_SUFFIX } else { "" };
        return match resolved_label {
            Some(label) if label.starts_with(&format!("{family} ")) => {
                if label.ends_with(ONE_MILLION_SUFFIX) {
                    label
                } else {
                    label + suffix
                }
            }
            _ => family + suffix,
        };
    }
    let name = match fallback {
        Some(f) if !f.is_empty() => f,
        _ => value,
    };
    match resolved_label {
        Some(label) => format!("{name} · {label}"),
        None => name.to_string(),
    }
}

fn family_hint<'a>(model: &str, hint: Option<&'a str>) -> Option<&'a str> {
    let mut alias = model.trim().to_lowercase();
    if alias.ends_with("[1m]") {
        alias.truncate(alias.len() - 4);
    }
    if FAMILIES.contains(&alias.as_str()) { hint } else { None }
}

/// `ModelLabel.Option`.
pub fn option(option: &ModelOption, hint: Option<&str>) -> String {
    text(&option.value, option.resolved_model.as_deref().or_else(|| family_hint(&option.value, hint)), Some(&option.display_name))
}

/// `ModelLabel.InCatalog`.
pub fn in_catalog(model: &str, catalog: &[ModelOption], hint: Option<&str>) -> String {
    match catalog.iter().find(|m| m.value == model) {
        Some(found) => option(found, hint),
        None => text(model, family_hint(model, hint), None),
    }
}

/// `ModelLabel.ReportedModel`: the model the CLI reported, while it is still the pane's selection.
pub fn reported_model(session: &RunSession) -> Option<&str> {
    let usage = session.session_usage.as_ref()?;
    (usage.provider == session.provider() && usage.selected_model.as_deref() == session.model.as_deref())
        .then_some(usage.model.as_deref())
        .flatten()
}

/// `ModelLabel.Selection`: the pane's selection as displays show it.
pub fn selection(session: &RunSession, catalog: &[ModelOption], locale: &Locale) -> String {
    if session.model() == "default" && !catalog.iter().any(|m| m.value == "default") {
        return locale.get("composer.model.cliDefault");
    }
    in_catalog(session.model(), catalog, reported_model(session))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_shared_model_labels_hold() {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures/model-labels.json");
        let cases: serde_json::Value = serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
        let cases = cases.as_array().unwrap();
        assert!(cases.len() > 20);
        for case in cases {
            let model = case["model"].as_str().unwrap();
            let expected = case["expected"].as_str().unwrap();
            let got = text(model, case["resolved"].as_str(), case["fallback"].as_str());
            assert_eq!(got, expected, "{case}");
        }
    }
}
