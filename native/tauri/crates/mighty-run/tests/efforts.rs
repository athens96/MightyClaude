//! C# ModelEffortVerification (the catalogue, effort and argument checks; the composer's reconcile, the remote's bounds and the
//! status line are other stages') and ComposerParityVerification.BedrockPolicyDenialReadsAsGuidance.

mod common;

use mighty_core::model::{RegisteredModelEntry, RunSettings};
use mighty_core::wire::{CODEX_EFFORTS, EFFORTS, run_efforts};
use mighty_run::failure::bedrock_guidance;
use mighty_run::model::{ModelCatalog, ModelOption, StartRunRequest};
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::providers::{self, effort_refusal, efforts};
use mighty_run::stream;
use mighty_run::strings;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

fn claude_catalog() -> ModelCatalog {
    let text = common::fixture("model-list-claude.jsonl");
    let lines: Vec<&str> = text.split('\n').map(str::trim_end).filter(|l| !l.is_empty()).collect();
    let initialize = common::json(lines[1]);
    providers::claude_catalog(initialize["response"]["response"]["models"].as_array().unwrap())
}

fn codex_catalog() -> ModelCatalog {
    let text = common::fixture("model-list-codex.jsonl");
    let answer = common::json(text.split('\n').find(|l| l.starts_with("{\"id\":2,")).unwrap().trim_end());
    providers::codex_catalog(answer["result"]["data"].as_array().unwrap())
}

fn rows(provider: &str, json: &str) -> ModelCatalog {
    let rows = common::json(json);
    let rows = rows.as_array().unwrap();
    if provider == "codex" { providers::codex_catalog(rows) } else { providers::claude_catalog(rows) }
}

fn strings_of(values: &[&str]) -> Vec<String> {
    values.iter().map(|s| s.to_string()).collect()
}

fn effort(level: &str) -> RunSettings {
    RunSettings { effort: level.into(), ..RunSettings::default() }
}

#[test]
fn haiku55_takes_the_levels_claude_code_names_and_haiku45_none() {
    let catalog = claude_catalog();
    let haiku = catalog.models.iter().find(|m| m.value == "haiku").unwrap();
    assert!(
        haiku.resolved_model.as_deref() == Some("claude-haiku-5-5")
            && haiku.supports_effort == Some(true)
            && haiku.supported_effort_levels.as_deref() == Some(&strings_of(&EFFORTS)[..]),
        "Haiku 5.5's levels from the list"
    );
    for model in ["haiku", "claude-haiku-5-5"] {
        assert_eq!(efforts("claude", model, &catalog, None), strings_of(&EFFORTS), "{model}");
    }
    // Haiku 4.5 is listed without a word on effort: the old rule.
    let older = catalog.models.iter().find(|m| m.value == "claude-haiku-4-5-20251001").unwrap();
    assert!(
        older.supports_effort == Some(false)
            && older.supported_effort_levels.as_ref().is_some_and(Vec::is_empty)
            && efforts("claude", &older.value, &catalog, None).is_empty(),
        "Haiku 4.5 takes none"
    );
    // The built-in list, or a list that does not name it: the old rule.
    let elsewhere = ModelCatalog::new("cli", vec![ModelOption::new("opus", "Opus", "")], "");
    for list in [providers::fallback("claude"), elsewhere.clone()] {
        assert!(efforts("claude", "haiku", &list, None).is_empty(), "no list speaks for haiku");
    }
    // A list that says a Haiku model takes an effort without naming the levels is believed; one that says no is too.
    let unnamed = rows("claude", r#"[{"value":"claude-haiku-6","supportsEffort":true}]"#);
    assert!(
        unnamed.models.last().unwrap().supports_effort == Some(true)
            && efforts("claude", "claude-haiku-6", &unnamed, None) == ["low", "medium", "high"],
        "an unnamed yes"
    );
    assert!(
        efforts("claude", "claude-haiku-6", &rows("claude", r#"[{"value":"claude-haiku-6","supportsEffort":false}]"#), None).is_empty(),
        "a no"
    );
    // A model the user registered says for itself, Haiku or not.
    let registered = vec![RegisteredModelEntry {
        name: Some("claude-haiku-7".into()),
        supports_effort: true,
        supported_effort_levels: Some(vec![Some("low".into()), Some("high".into())]),
    }];
    assert_eq!(efforts("claude", "claude-haiku-7", &elsewhere, Some(&registered)), ["low", "high"], "a registered model");
}

#[test]
fn a_haiku_run_with_an_effort_is_checked_against_the_clis_list() {
    common::korean();
    let catalog = claude_catalog();
    let mut request = StartRunRequest::new("pane", "space", "claude", "Reply with ok.").with_settings(effort("max"));
    request.model = "haiku".into();
    // No rule by name before the run: its list decides.
    request.validate().unwrap();
    for model in ["haiku", "claude-haiku-5-5"] {
        assert!(effort_refusal("claude", model, "max", &catalog, None).is_none(), "{model} takes max");
    }
    // A Haiku model the CLI says nothing about takes none, refused in Haiku's words.
    assert_eq!(
        effort_refusal("claude", "claude-haiku-4-5-20251001", "high", &catalog, None),
        Some(strings::get("validation.run.haikuEffort")),
        "Haiku 4.5"
    );
    assert_eq!(
        effort_refusal("claude", "haiku", "high", &providers::fallback("claude"), None),
        Some(strings::get("validation.run.haikuEffort")),
        "Haiku on the built-in list"
    );
    // A level its list does not name is an unknown one, not a Haiku refusal.
    assert_eq!(
        effort_refusal(
            "claude",
            "haiku",
            "max",
            &rows("claude", r#"[{"value":"haiku","supportsEffort":true,"supportedEffortLevels":["low"]}]"#),
            None
        ),
        Some(strings::get("run.error.effortNotSupported")),
        "max on a list of low"
    );
    // The run passes the effort as it does any Claude model's.
    let arguments = providers::arguments(&request, "/tmp/plugin", None).unwrap();
    let settings = common::json(&common::after(&arguments, "--settings"));
    assert!(common::after(&arguments, "--effort") == "max" && settings["env"]["CLAUDE_CODE_EFFORT_LEVEL"] == "max", "--effort max");
}

#[test]
fn codexs_list_keeps_ultra_for_the_models_that_name_it() {
    let catalog = codex_catalog();
    let ultra: Vec<&str> = catalog
        .models
        .iter()
        .skip(1)
        .filter(|m| m.supported_effort_levels.as_ref().is_some_and(|l| l.iter().any(|e| e == "ultra")))
        .map(|m| m.value.as_str())
        .collect();
    assert_eq!(
        ultra,
        ["gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-5.6-sol", "gpt-6.1-terra", "gpt-5.6-terra"],
        "the six that name ultra"
    );
    assert_eq!(efforts("codex", "gpt-6.1-sol", &catalog, None), ["low", "medium", "high", "xhigh", "max", "ultra"], "gpt-6.1-sol's levels");
    // The default stands for gpt-6.1-sol, and takes what it takes.
    assert!(
        catalog.models[0].resolved_model.as_deref() == Some("gpt-6.1-sol")
            && efforts("codex", "default", &catalog, None).last().map(String::as_str) == Some("ultra"),
        "the default"
    );
    assert!(
        efforts("codex", "gpt-6-luna", &catalog, None) == strings_of(&EFFORTS)
            && efforts("codex", "gpt-5.5", &catalog, None) == ["low", "medium", "high", "xhigh"],
        "the others are not offered it"
    );
    // An effort no agent knows is still left out.
    let unknown =
        rows("codex", r#"[{"model":"gpt-7","supportedReasoningEfforts":[{"reasoningEffort":"ultra"},{"reasoningEffort":"persistent"}]}]"#);
    assert_eq!(unknown.models.last().unwrap().supported_effort_levels.as_deref(), Some(&strings_of(&["ultra"])[..]), "persistent left out");
}

#[test]
fn no_other_agent_is_offered_ultra() {
    common::korean();
    // A Claude list that named it would still not bring it.
    let claude = rows("claude", r#"[{"value":"opus","supportsEffort":true,"supportedEffortLevels":["high","max","ultra"]}]"#);
    assert_eq!(
        claude.models.last().unwrap().supported_effort_levels.as_deref(),
        Some(&strings_of(&["high", "max"])[..]),
        "Claude's list never offers ultra"
    );
    let named = ModelCatalog::new(
        "cli",
        vec![ModelOption {
            supports_effort: Some(true),
            supported_effort_levels: Some(strings_of(&CODEX_EFFORTS)),
            ..ModelOption::new("m", "M", "")
        }],
        "",
    );
    for provider in ["claude", "gemini", "pi"] {
        for model in ["default", "m", "opus"] {
            assert!(!efforts(provider, model, &named, None).contains(&"ultra".to_string()), "{provider} {model}");
        }
        assert!(!run_efforts(provider).contains(&"ultra"), "{provider}'s run efforts");
        assert!(
            StartRunRequest::new("pane", "space", "claude", "x").with_provider(provider).with_settings(effort("ultra")).validate().is_err(),
            "{provider} refuses a run set to ultra"
        );
        assert_eq!(
            providers::normalize_settings(provider, Some(&effort("ultra"))).effort,
            "default",
            "{provider} saved ultra goes back to the default"
        );
    }
    assert!(
        run_efforts("codex") == CODEX_EFFORTS && providers::normalize_settings("codex", Some(&effort("ultra"))).effort == "ultra",
        "a Codex pane keeps it"
    );
}

#[test]
fn a_codex_run_passes_ultra_as_it_passes_every_effort() {
    common::korean();
    let catalog = codex_catalog();
    let mut request =
        StartRunRequest::new("pane", "space", "claude", "Reply with ok.").with_provider("codex").with_settings(effort("ultra"));
    request.model = "gpt-6.1-sol".into();
    assert!(effort_refusal("codex", "gpt-6.1-sol", "ultra", &catalog, None).is_none(), "gpt-6.1-sol takes ultra");
    let exec = providers::arguments(&request, "/tmp/plugin", None).unwrap();
    assert_eq!(
        exec,
        [
            "-c",
            "approval_policy=\"never\"",
            "-c",
            "sandbox_mode=\"read-only\"",
            "-c",
            "sandbox_workspace_write.network_access=false",
            "-c",
            "features.fast_mode=false",
            "-c",
            "service_tier=\"default\"",
            "-c",
            "model_reasoning_effort=\"ultra\"",
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--model",
            "gpt-6.1-sol",
            "-",
        ],
        "codex exec"
    );
    assert!(effort_refusal("codex", "gpt-6-luna", "ultra", &catalog, None).is_some(), "a model that does not name it refuses it");
    let mut server = request.clone();
    server.settings = Some(RunSettings { permission_mode: "onRequest".into(), ..effort("ultra") });
    let server = providers::arguments(&server, "/tmp/plugin", None).unwrap();
    assert!(
        server.contains(&"model_reasoning_effort=\"ultra\"".to_string())
            && server[server.len() - 3..] == ["app-server", "--listen", "stdio://"],
        "the app-server: {server:?}"
    );
}

/// TheBoundIsWhatEachReadOfItsCLIKeeps: each read keeps the remote's bound for its provider (`RemoteWorkspaceValidation.MaximumModels`:
/// the default row and `ListedModelLimit` listed ones).
#[test]
fn the_bound_is_what_each_read_of_its_cli_keeps() {
    let claude: Vec<Value> = (1..=200).map(|i| json!({"value": format!("claude-m{i}")})).collect();
    let codex: Vec<Value> = (1..=200).map(|i| json!({"model": format!("gpt-m{i}")})).collect();
    assert_eq!(providers::claude_catalog(&claude).models.len(), 1 + providers::listed_model_limit("claude"), "claude");
    assert_eq!(providers::codex_catalog(&codex).models.len(), 1 + providers::listed_model_limit("codex"), "codex");
    let agy = (1..=200).map(|i| format!("gemini-m{i}\tGemini {i}")).collect::<Vec<_>>().join("\n");
    assert_eq!(providers::antigravity_catalog(&agy).models.len(), 1 + providers::listed_model_limit("gemini"), "gemini");
    let pi = (1..=600).map(|i| format!("anthropic m{i} 200K 64K yes yes")).collect::<Vec<_>>().join("\n");
    assert_eq!(providers::pi_catalog(&pi).models.len(), 1 + providers::listed_model_limit("pi"), "pi");
}

// ── ComposerParityVerification.BedrockPolicyDenialReadsAsGuidance (M/Tests/BedrockAuthDiagnosticsTests.swift:21-93) ─────────

const DENIED: &str = "User: arn:aws:sts::123456789012:assumed-role/private-role/private-session is not authorized to perform: bedrock:InvokeModel on resource: arn:aws:bedrock:ap-northeast-2::foundation-model/private-model with an explicit deny in a service control policy";

/// `ApiError`: the message as Claude Code reports a Bedrock refusal (the body as C#'s default serializer writes it).
fn api_error(message: &str, status: u16, code: Option<&str>) -> String {
    let mut body = serde_json::Map::new();
    body.insert("Message".into(), Value::String(message.into()));
    if let Some(code) = code {
        body.insert("__type".into(), Value::String(code.into()));
    }
    format!(
        "AWS authentication failed · refresh your AWS credentials and retry · API Error: {status} {}",
        mighty_run::text_json::wire(&Value::Object(body))
    )
}

#[test]
fn bedrock_policy_denial_reads_as_guidance() {
    common::korean();
    let scp = strings::get("bedrock.scpDenied");
    for action in ["InvokeModel", "InvokeModelWithResponseStream"] {
        for suffix in ["", ": arn:aws:organizations::123456789012:policy/o-0123456789/service_control_policy/p-01234567"] {
            for connector in ["with", "because of"] {
                for code in [None, Some("AccessDeniedException"), Some("private.namespace#AccessDeniedException")] {
                    let message = format!(
                        "{}{suffix}",
                        DENIED.replace("InvokeModel", action).replace("with an explicit", &format!("{connector} an explicit"))
                    );
                    let guidance = bedrock_guidance(&api_error(&message, 403, code));
                    assert_eq!(
                        guidance.as_deref(),
                        Some(scp.as_str()),
                        "a service control policy's denial of {action} ({connector}, {code:?}) is recognised"
                    );
                    let guidance = guidance.unwrap();
                    assert!(
                        !guidance.contains("private-")
                            && !guidance.contains("123456789012")
                            && !guidance.contains("refresh your AWS")
                            && !guidance.contains("arn:"),
                        "the guidance names no account, role or policy"
                    );
                }
            }
        }
    }
    let others = [
        api_error(DENIED, 400, None),
        api_error(DENIED, 403, Some("ExpiredTokenException")),
        api_error(&format!("Authentication failed: Please make sure your API Key is valid. {DENIED}"), 403, None),
        api_error("The security token included in the request is expired. See service control policy documentation.", 403, None),
        api_error("Not authorized; see service control policy documentation for explicit deny examples.", 403, None),
        api_error(&format!("For example: {DENIED}"), 403, None),
        api_error(
            &format!("{}. See service control policy documentation.", DENIED.replace("service control policy", "identity-based policy")),
            403,
            None,
        ),
        api_error(&DENIED.replace("InvokeModel", "ListFoundationModels"), 403, None),
        api_error(&DENIED.replace("InvokeModel", "InvokeModelWithResponseStreamExtra"), 403, None),
        api_error(&format!("{DENIED}: arn:aws:iam::123456789012:policy/private"), 403, None),
        api_error(&format!("{DENIED}. See service control policy documentation."), 403, None),
        api_error(&format!("{DENIED}: https://private.invalid/policy"), 403, None),
        api_error(&DENIED.replace("with an explicit deny in", "because no permission is granted by"), 403, None),
        format!("API Error: 403 invalid-json {DENIED}"),
        DENIED.to_string(),
    ];
    for (index, other) in others.iter().enumerate() {
        assert!(bedrock_guidance(other).is_none(), "any other failure keeps its own words: case {index}");
    }

    // A confirmed failure reads as guidance wherever the frame is shown; the transcript parser reads the frame as presented.
    let raw = api_error(DENIED, 403, None);
    let marked = json!({"type": "assistant", "error": "authentication_failed", "message": {"id": "aws-error", "content": [{"type": "text", "text": raw}]}});
    let failed = json!({"type": "result", "subtype": "error_during_execution", "is_error": true, "errors": [raw], "result": raw});
    let logs: Arc<Mutex<Vec<(String, String)>>> = Arc::default();
    let sink = logs.clone();
    let parser = OutputParser::new(
        "claude",
        "bedrock",
        ParserHooks {
            log: Some(Arc::new(move |kind, text| sink.lock().unwrap().push((kind.into(), text.into())))),
            ..ParserHooks::default()
        },
    );
    for frame in [marked, failed] {
        let shown = stream::present_failure(&frame);
        assert!(
            shown.as_ref().is_some_and(|s| !s.contains("private-") && !s.contains("123456789012")),
            "the presented frame carries none of the AWS message"
        );
        parser.parse(&shown.unwrap());
    }
    let logs = logs.lock().unwrap().clone();
    assert!(
        parser.failed()
            && logs.iter().map(|(k, _)| k.as_str()).collect::<Vec<_>>() == ["assistant", "error"]
            && logs.iter().all(|(_, t)| *t == scp),
        "the marked message and the failed result both read as the guidance: {logs:?}"
    );
    let ordinary = [
        json!({"type": "assistant", "message": {"id": "quoted-example", "content": [{"type": "text", "text": raw}]}}),
        json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "tool-fixture", "is_error": true, "content": raw}]}}),
        json!({"type": "result", "subtype": "success", "is_error": false, "result": raw}),
        json!({"type": "turn.failed", "error": {"message": raw}}),
    ];
    for frame in ordinary {
        assert!(
            stream::present_failure(&frame).is_none(),
            "an ordinary answer, a tool's result, a successful result and another agent's failure are not rewritten"
        );
    }
    let mixed = json!({"type": "result", "subtype": "error_during_execution", "is_error": true, "errors": ["another failure", raw]});
    let presented = common::json(&stream::present_failure(&mixed).unwrap());
    assert_eq!(presented["errors"], json!(["another failure", scp]), "only the denial among a result's errors is replaced");
}
