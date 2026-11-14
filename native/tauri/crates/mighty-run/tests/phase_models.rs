//! C# PhaseModelVerification, PhaseModelPreferencesVerification and ModelDefaultsVerification under their names. Every file is a temp
//! folder's (the store's home is always given). `SectionRegisteredInMacOrder`'s place in the settings screen's order is W6's
//! `SettingsSections`; its copy checks are here.

mod common;

use std::path::Path;
use std::sync::Arc;

use indexmap::IndexMap;
use mighty_core::json::{JsonOut, Wire};
use mighty_core::model::{AppSnapshot, PhaseModelsSnapshot, RegisteredModelEntry, RunSettings, Workspace};
use mighty_run::model::{ModelCatalog, ModelOption, StartRunRequest};
use mighty_run::model_settings::{self, ModelSettingsFileStore, OmcAgentCatalog};
use mighty_run::phase_models::{self, new_snapshot};
use mighty_run::phase_routing::{self, Phase};
use mighty_run::phase_section::{self, PhaseModelTools};
use mighty_run::providers;

fn map(pairs: &[(&str, &str)]) -> IndexMap<String, String> {
    pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
}

fn snapshot(change: impl FnOnce(&mut PhaseModelsSnapshot)) -> PhaseModelsSnapshot {
    let mut config = new_snapshot();
    change(&mut config);
    config
}

fn write(path: &Path, text: &str) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(path, text).unwrap();
}

fn registered(name: &str, supports_effort: bool, levels: &[&str]) -> RegisteredModelEntry {
    RegisteredModelEntry {
        name: Some(name.into()),
        supports_effort,
        supported_effort_levels: Some(levels.iter().map(|l| Some(l.to_string())).collect()),
    }
}

fn after(args: &[String], flag: &str) -> String {
    common::after(args, flag)
}

// 1. Phase → knob mapping matches macOS PhaseModelRouting.
#[test]
fn row_rule_matches_mac_os() {
    use phase_routing::*;
    let c = new_snapshot();
    let cp = apply_claude_row(Phase::Planning, "opus-x", &c);
    assert!(
        cp.claude_opus_alias.as_deref() == Some("opus-x") && cp.claude_main.as_deref() == Some("default"),
        "claude planning → opusAlias only"
    );
    let ce = apply_claude_row(Phase::Execution, "main-x", &c);
    assert!(
        ce.claude_main.as_deref() == Some("main-x") && ce.claude_sonnet_alias.as_deref() == Some("main-x"),
        "claude execution → main + sonnetAlias"
    );
    assert_eq!(apply_claude_row(Phase::Review, "r", &c), c, "claude review → nothing changes");
    assert_eq!(
        apply_claude_row(Phase::Subagents, "sub-x", &c).claude_subagent_default.as_deref(),
        Some("sub-x"),
        "claude subagents → subagentDefault"
    );
    assert_eq!(apply_codex_row(Phase::Review, "rev-x", &c).codex_review_model.as_deref(), Some("rev-x"), "codex review → reviewModel");
    assert_eq!(
        apply_codex_row(Phase::Subagents, "dsub-x", &c).codex_subagent_default.as_deref(),
        Some("dsub-x"),
        "codex subagents → subagentDefault"
    );
    assert_eq!(apply_codex_row(Phase::Planning, "p", &c), c, "codex planning → nothing changes");
    assert_eq!(apply_codex_row(Phase::Execution, "e", &c), c, "codex execution → nothing changes");

    let uniform = snapshot(|s| {
        s.claude_main = Some("m".into());
        s.claude_sonnet_alias = Some("m".into());
    });
    assert_eq!(claude_row_state(Phase::Execution, &uniform), Some(RowState::Uniform("m".into())), "uniform row state");
    let mixed = snapshot(|s| {
        s.claude_main = Some("a".into());
        s.claude_sonnet_alias = Some("b".into());
    });
    assert!(!claude_row_state(Phase::Execution, &mixed).unwrap().is_uniform(), "mixed row state");
    assert!(claude_row_state(Phase::Review, &c).is_none(), "claude review row state is null");
    assert!(codex_row_state(Phase::Planning, &c).is_none(), "codex planning row state is null");

    for (key, phase) in [
        ("planner", Some(Phase::Planning)),
        ("architect", Some(Phase::Planning)),
        ("critic", Some(Phase::Planning)),
        ("executor", Some(Phase::Execution)),
        ("codeReviewer", Some(Phase::Review)),
        ("verifier", Some(Phase::Review)),
        ("other", None),
    ] {
        assert_eq!(omc_agent_phase(key), phase, "{key}");
    }
    for (key, phase) in [
        ("clarification.default_model", Some(Phase::Planning)),
        ("evaluation.semantic_model", Some(Phase::Review)),
        ("consensus.judge_model", Some(Phase::Review)),
        ("llm.qa_model", Some(Phase::Review)),
        ("orchestrator.cli_path", None),
    ] {
        assert_eq!(ouroboros_key_phase(key), phase, "{key}");
    }
    assert!(omc_row_state(Phase::Planning, None).is_none(), "omc nil → null row state");
    assert!(ouroboros_row_state(Phase::Review, None).is_none(), "ouroboros nil → null row state");
    let agents = map(&[("planner", "a"), ("architect", "b"), ("critic", "b")]);
    assert!(!omc_row_state(Phase::Planning, Some(&agents)).unwrap().is_uniform(), "omc planning mixed when values differ");
}

// 2. File store keeps unrelated keys.
#[test]
fn file_store_keeps_unrelated_keys() {
    let dir = common::temp("pm-keep");
    let store = ModelSettingsFileStore::new(&dir);
    write(
        &store.omc_config_path(),
        "{\n  \"otherKey\": \"preserved\",\n  \"agents\": {\n    \"planner\": { \"model\": \"old-model\", \"extraField\": \"kept\" },\n    \"other\": { \"setting\": \"value\" }\n  }\n}",
    );
    store.save_omc_agents(&map(&[("planner", "new-model")])).unwrap();
    assert_eq!(store.load_omc_agents().unwrap().unwrap()["planner"], "new-model", "planner model updated");
    let raw = std::fs::read_to_string(store.omc_config_path()).unwrap();
    assert!(raw.contains("otherKey") && raw.contains("preserved"), "otherKey and its value must survive");
    assert!(raw.contains("other") && raw.contains("extraField"), "the other agent and planner's extraField must survive");

    write(
        &store.ouroboros_config_path(),
        "orchestrator:\n  cli_path: /custom/claude\nclarification:\n  default_model: opus\nllm:\n  qa_model: sonnet",
    );
    store.save_ouroboros_keys(&map(&[("clarification.default_model", "fable")])).unwrap();
    let raw = std::fs::read_to_string(store.ouroboros_config_path()).unwrap();
    assert!(raw.contains("cli_path: /custom/claude"), "orchestrator.cli_path must survive");
    assert!(raw.contains("default_model: fable"), "default_model updated");
    assert!(raw.contains("qa_model: sonnet"), "qa_model unchanged");
    let _ = std::fs::remove_dir_all(&dir);
}

/// The file C# writes, byte for byte: System.Text.Json indented (two spaces, `Environment.NewLine`), the comments gone, the agents'
/// order kept, a new agent last — or in the place of one removed before it, as .NET's `Dictionary` reuses a freed entry.
#[test]
fn the_omc_file_is_written_as_c_sharp_writes_it() {
    let dir = common::temp("pm-bytes");
    let store = ModelSettingsFileStore::new(&dir);
    write(
        &store.omc_config_path(),
        "{\n  // user file\n  \"theme\": \"dark\",\n  \"agents\": { \"executor\": { \"model\": \"haiku\", \"prompt\": \"keep\" } }\n}",
    );
    store.save_omc_agents(&map(&[("planner", "fable"), ("executor", "default")])).unwrap();
    let nl = mighty_run::os::new_line();
    let expected = [
        "{",
        r#"  "theme": "dark","#,
        r#"  "agents": {"#,
        r#"    "executor": {"#,
        r#"      "prompt": "keep""#,
        "    },",
        r#"    "planner": {"#,
        r#"      "model": "fable""#,
        "    }",
        "  }",
        "}",
    ]
    .join(nl);
    assert_eq!(std::fs::read_to_string(store.omc_config_path()).unwrap(), expected);
    write(&store.omc_config_path(), r#"{"agents": {"executor": {"model": "haiku"}, "verifier": {"model": "x"}}}"#);
    store.save_omc_agents(&map(&[("executor", "default"), ("planner", "fable")])).unwrap();
    let agents: Vec<String> = store.load_omc_agents().unwrap().unwrap().keys().cloned().collect();
    assert_eq!(agents, ["planner", "verifier"], "a new agent takes the place of the one removed before it");
    let backups = std::fs::read_dir(store.omc_config_path().parent().unwrap())
        .unwrap()
        .flatten()
        .filter(|e| {
            e.file_name().to_string_lossy().starts_with("config.mighty-backup-") && e.file_name().to_string_lossy().ends_with(".jsonc")
        })
        .count();
    assert_eq!(backups, 2, "a backup beside the file for each write");
    let _ = std::fs::remove_dir_all(&dir);
}

// 3. Unparseable file is refused; file left byte-identical.
#[test]
fn refuse_unparseable_file() {
    let dir = common::temp("pm-refuse");
    let store = ModelSettingsFileStore::new(&dir);
    let bad = "{ this is not json }";
    write(&store.omc_config_path(), bad);
    assert!(store.save_omc_agents(&map(&[("planner", "model")])).is_err(), "SaveOmcAgents must throw on unparseable JSONC");
    assert_eq!(std::fs::read_to_string(store.omc_config_path()).unwrap(), bad, "file must be byte-identical after refusal");
    assert!(store.load_omc_agents().is_err(), "LoadOmcAgents must throw on unparseable JSONC");
    let _ = std::fs::remove_dir_all(&dir);
}

// 4. omc agent catalog: scan agents from config.jsonc.
#[test]
fn omc_agent_catalog() {
    let dir = common::temp("pm-catalog");
    let store = ModelSettingsFileStore::new(&dir);
    assert!(store.load_omc_agents().unwrap().is_none(), "absent file → null");
    write(
        &store.omc_config_path(),
        r#"{"agents": {"planner": {"model": "fable"}, "executor": {"model": "sonnet"}, "noModel": {"prompt": "only"}, "codeReviewer": {"model": "opus"}}}"#,
    );
    let agents = store.load_omc_agents().unwrap().unwrap();
    assert!(agents["planner"] == "fable" && agents["executor"] == "sonnet" && agents["codeReviewer"] == "opus", "the models");
    assert!(!agents.contains_key("noModel"), "entry without model key is absent");
    store.save_omc_agents(&map(&[("planner", "default")])).unwrap();
    let after = store.load_omc_agents().unwrap().unwrap();
    assert!(!after.contains_key("planner"), "default removes model key");
    assert_eq!(after["executor"], "sonnet", "executor unchanged after partial save");

    let catalog = OmcAgentCatalog::new(&dir);
    assert!(catalog.scan().is_none(), "no installed_plugins.json → omc not installed");
    let install = dir.join("cache").join("omc").join("oh-my-claudecode").join("5.4.0");
    let project_install = dir.join("project-omc");
    write(&project_install.join("agents").join("planner.md"), "---\nmodel: haiku\n---\n");
    let write_installed = |records: &str| {
        write(
            &catalog.installed_plugins_path(),
            &format!(r#"{{"version":2,"plugins":{{"other@x":[],"oh-my-claudecode@omc":[{records}]}}}}"#),
        );
    };
    let record = |scope: &str, path: &Path| {
        format!(r#"{{"scope":"{scope}","installPath":{}}}"#, serde_json::to_string(&path.to_string_lossy()).unwrap())
    };
    write_installed(&record("project", &project_install));
    assert!(catalog.scan().is_none(), "a project-scope record alone is not an install");
    write_installed(&record("user", &install));
    assert!(catalog.scan().is_none(), "a user record without agents/*.md is not an install");
    let agents_dir = install.join("agents");
    write(&agents_dir.join("code-reviewer.md"), "---\nname: code-reviewer\nmodel: opus\n---\nmodel: ignored\n");
    write(&agents_dir.join("security-reviewer.md"), "---\nmodel: sonnet\n---\n");
    write(&agents_dir.join("verifier.md"), "---\nmodel: sonnet\n---\n");
    write(&agents_dir.join("git-master.md"), "no frontmatter\n");
    write(&agents_dir.join("README.txt"), "not an agent");
    write_installed(&format!("{},{}", record("project", &project_install), record("user", &install)));
    let scanned = catalog.scan().expect("user record with agents/*.md → omc installed");
    let mut keys: Vec<&str> = scanned.keys().map(String::as_str).collect();
    keys.sort();
    assert_eq!(keys, ["codeReviewer", "gitMaster", "securityReviewer", "verifier"], "agent keys are the file names in omc camelCase");
    assert_eq!(scanned["codeReviewer"], "opus", "frontmatter model read, body ignored");
    assert_eq!(scanned["verifier"], "sonnet", "single-word name unchanged");
    assert_eq!(scanned["gitMaster"], "default", "no frontmatter → default");
    assert_eq!(model_settings::kebab_to_camel_case("document-specialist"), "documentSpecialist", "kebab → camelCase");
    let _ = std::fs::remove_dir_all(&dir);
}

fn backups(store: &ModelSettingsFileStore) -> usize {
    std::fs::read_dir(store.omc_config_path().parent().unwrap())
        .unwrap()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with("config.mighty-backup-"))
        .count()
}

// 4b. A pick writes only the agents it changed; frontmatter defaults never reach config.jsonc.
#[test]
fn omc_save_writes_only_changed_agents() {
    common::korean();
    let dir = common::temp("pm-changed");
    let install = dir.join("omc-install");
    for (file, model) in
        [("planner", "opus"), ("architect", "opus"), ("executor", "sonnet"), ("code-reviewer", "opus"), ("verifier", "sonnet")]
    {
        write(&install.join("agents").join(format!("{file}.md")), &format!("---\nmodel: {model}\n---\n"));
    }
    write(
        &dir.join(".claude").join("plugins").join("installed_plugins.json"),
        &format!(
            r#"{{"plugins":{{"oh-my-claudecode@omc":[{{"scope":"user","installPath":{}}}]}}}}"#,
            serde_json::to_string(&install.to_string_lossy()).unwrap()
        ),
    );
    let store = ModelSettingsFileStore::new(&dir);
    write(
        &store.omc_config_path(),
        "{\n  // user file\n  \"theme\": \"dark\",\n  \"agents\": { \"executor\": { \"model\": \"haiku\", \"prompt\": \"keep\" } }\n}",
    );
    let tools = phase_section::load_tools(&dir);
    let omc = tools.omc_agents.clone().unwrap();
    assert_eq!(omc["executor"], "haiku", "a configured agent shows its config.jsonc value");
    assert_eq!(omc["planner"], "default", "an unconfigured agent shows default, not its frontmatter model");

    let saved = phase_section::save_tools(&tools, &phase_section::apply_phase_row(Phase::Planning, "fable", &new_snapshot(), &tools), &dir);
    assert!(saved.error.is_none(), "save succeeds");
    let written = store.load_omc_agents().unwrap().unwrap();
    let mut keys: Vec<&str> = written.keys().map(String::as_str).collect();
    keys.sort();
    assert_eq!(keys, ["architect", "executor", "planner"], "only the changed planning agents were added to config.jsonc");
    assert!(written["planner"] == "fable" && written["architect"] == "fable", "planning agents written");
    assert_eq!(written["executor"], "haiku", "an untouched agent keeps its value");
    assert!(
        !written.contains_key("codeReviewer") && !written.contains_key("verifier"),
        "frontmatter defaults of untouched agents never reach config.jsonc"
    );
    let root: serde_json::Value = serde_json::from_slice(&std::fs::read(store.omc_config_path()).unwrap()).unwrap();
    assert_eq!(root["theme"], "dark", "unrelated keys survive");
    assert_eq!(root["agents"]["executor"]["prompt"], "keep", "unrelated agent fields survive");

    // Picking the value an agent already has writes nothing at all.
    let bytes = std::fs::read(store.omc_config_path()).unwrap();
    let before = backups(&store);
    let same = phase_section::save_tools(&saved, &phase_section::apply_knob("omc.agent.executor", "haiku", &new_snapshot(), &saved), &dir);
    assert!(same.error.is_none(), "a no-op pick is not an error");
    assert_eq!(std::fs::read(store.omc_config_path()).unwrap(), bytes, "a no-op pick leaves config.jsonc byte-identical");
    assert_eq!(backups(&store), before, "a no-op pick makes no backup");

    let changed = phase_section::changed(Some(&map(&[("a", "x"), ("b", "y")])), Some(&map(&[("a", "x"), ("b", "z"), ("c", "w")])));
    assert!(changed.len() == 2 && changed["b"] == "z" && changed["c"] == "w", "Changed keeps only differing keys");
    let _ = std::fs::remove_dir_all(&dir);
}

// 5. Launch args merge into a single --settings JSON.
#[test]
fn launch_args_merge_one_settings_json() {
    let high = RunSettings { effort: "high".into(), ..RunSettings::default() };
    let request = || StartRunRequest::new("s", "w", "claude", "x");
    let effort_args = providers::arguments(&request().with_settings(high.clone()), "/plugin", None).unwrap();
    assert_eq!(effort_args.iter().filter(|a| *a == "--settings").count(), 1, "effort alone produces one --settings flag");
    assert!(effort_args.iter().any(|a| a.contains("CLAUDE_CODE_EFFORT_LEVEL")), "effort env key present");

    let pm = snapshot(|s| {
        s.claude_opus_alias = Some("custom-opus".into());
        s.claude_subagent_default = Some("custom-sub".into());
    });
    let pm_args = providers::arguments(&request(), "/plugin", Some(&pm)).unwrap();
    assert_eq!(pm_args.iter().filter(|a| *a == "--settings").count(), 1, "phase models alone produces one --settings flag");
    assert!(pm_args.iter().any(|a| a.contains("ANTHROPIC_DEFAULT_OPUS_MODEL")), "opus env key present");
    assert!(pm_args.iter().any(|a| a.contains("CLAUDE_CODE_SUBAGENT_MODEL")), "subagent env key present");
    assert!(!pm_args.iter().any(|a| a.contains("ANTHROPIC_DEFAULT_SONNET_MODEL")), "default sonnet alias omitted");

    let both = providers::arguments(&request().with_settings(high), "/plugin", Some(&pm)).unwrap();
    assert_eq!(both.iter().filter(|a| *a == "--settings").count(), 1, "effort + phase models merges into one --settings flag");
    let settings = after(&both, "--settings");
    assert!(settings.contains("CLAUDE_CODE_EFFORT_LEVEL") && settings.contains("ANTHROPIC_DEFAULT_OPUS_MODEL"), "both keys in merged JSON");

    let main = snapshot(|s| s.claude_main = Some("fable".into()));
    assert_eq!(after(&providers::arguments(&request(), "/plugin", Some(&main)).unwrap(), "--model"), "fable", "claudeMain injects --model");
    let mut explicit_request = request();
    explicit_request.model = "opus".into();
    let explicit = providers::arguments(&explicit_request, "/plugin", Some(&main)).unwrap();
    assert!(
        explicit.iter().filter(|a| *a == "--model").count() == 1 && after(&explicit, "--model") == "opus",
        "session model takes precedence"
    );

    let codex_pm = snapshot(|s| {
        s.codex_review_model = Some("review-m".into());
        s.codex_subagent_default = Some("sub-m".into());
        s.codex_plan_mode_reasoning_effort = Some("high".into());
    });
    let codex = providers::arguments(&request().with_provider("codex"), "/plugin", Some(&codex_pm)).unwrap();
    for expected in ["review_model=\"review-m\"", "agents.default_subagent_model=\"sub-m\"", "plan_mode_reasoning_effort=\"high\""] {
        assert!(codex.iter().any(|a| a == expected), "{expected}");
    }
    assert!(!codex.iter().any(|a| a == "--settings"), "codex has no --settings flag");
}

// 6. AppSnapshot PhaseModels field uses macOS JSON field names.
#[test]
fn snapshot_shares_mac_os_field_names() {
    let encoded = |state: &AppSnapshot| {
        let mut out = JsonOut::new();
        state.write(&mut out);
        out.finish()
    };
    let state = AppSnapshot::default();
    assert!(state.phase_models.is_none(), "phaseModels defaults to null");
    assert!(!encoded(&state).contains("phaseModels"), "null phaseModels omitted from JSON");
    let loaded = AppSnapshot::read(&serde_json::from_str(r#"{"version":1,"workspaces":[],"sessions":[]}"#).unwrap()).unwrap();
    assert!(loaded.phase_models.is_none(), "missing phaseModels → null");
    let pm = snapshot(|s| {
        s.claude_main = Some("fable".into());
        s.claude_opus_alias = Some("opus-x".into());
        s.claude_sonnet_alias = Some("sonnet-x".into());
        s.claude_haiku_alias = Some("haiku-x".into());
        s.claude_subagent_default = Some("sub-x".into());
        s.codex_review_model = Some("rev-x".into());
        s.codex_subagent_default = Some("csub-x".into());
        s.codex_plan_mode_reasoning_effort = Some("high".into());
    });
    let with = AppSnapshot { phase_models: Some(pm), ..AppSnapshot::default() };
    let json = encoded(&with);
    for name in [
        "claudeMain",
        "claudeOpusAlias",
        "claudeSonnetAlias",
        "claudeHaikuAlias",
        "claudeSubagentDefault",
        "codexReviewModel",
        "codexSubagentDefault",
        "codexPlanModeReasoningEffort",
    ] {
        assert!(json.contains(&format!("\"{name}\"")), "{name} field name");
    }
    let back = AppSnapshot::read(&serde_json::from_str(&json).unwrap()).unwrap();
    let phase = back.phase_models.clone().unwrap();
    assert!(
        phase.claude_main.as_deref() == Some("fable")
            && phase.codex_review_model.as_deref() == Some("rev-x")
            && phase.codex_plan_mode_reasoning_effort.as_deref() == Some("high"),
        "round-trip"
    );
    assert!(with.version == 1 && back.version == 1, "version stays 1");
}

// 7. The screen's copy all comes from the locale catalogue (its place between display and styles is W6's SettingsSections).
#[test]
fn section_registered_in_mac_order() {
    common::korean();
    for text in [
        phase_section::section_title(),
        phase_section::description(),
        phase_section::default_option(),
        phase_section::mixed_label(),
        phase_section::phase_label(Phase::Planning),
        phase_section::phase_label(Phase::Execution),
        phase_section::phase_label(Phase::Review),
        phase_section::phase_label(Phase::Subagents),
        phase_section::tool_label(phase_section::OMC_TOOL),
        phase_section::tool_label(phase_section::OUROBOROS_TOOL),
    ] {
        assert!(!text.is_empty() && !text.starts_with("settings."), "screen copy comes from the locale catalogue: {text}");
    }
}

// 8. The rows the screen draws: four phase rows, a detail block per tool, a tool that is not installed left out.
#[test]
fn section_rows_follow_installed_tools() {
    common::korean();
    let dir = common::temp("pm-rows");
    let bare = phase_section::load_tools(&dir);
    assert!(bare.omc_agents.is_none() && bare.ouroboros_keys.is_none() && bare.error.is_none(), "nothing installed, nothing to read");
    let bare_blocks = phase_section::tool_blocks(&new_snapshot(), &bare);
    assert!(
        bare_blocks.len() == 2 && bare_blocks.iter().all(|b| b.tool == phase_section::CLAUDE_TOOL || b.tool == phase_section::CODEX_TOOL),
        "only Claude and Codex blocks without the other tools"
    );

    let install = dir.join("cache").join("omc").join("oh-my-claudecode").join("5.4.0");
    write(&install.join("agents").join("code-reviewer.md"), "---\nmodel: opus\n---\nbody\n");
    write(&install.join("agents").join("planner.md"), "---\nmodel: fable\n---\nbody\n");
    write(&install.join("agents").join("executor.md"), "---\nname: executor\n---\nbody\n");
    write(
        &dir.join(".claude").join("plugins").join("installed_plugins.json"),
        &format!(
            r#"{{"plugins":{{"oh-my-claudecode@omc":[{{"scope":"user","installPath":{}}}]}}}}"#,
            serde_json::to_string(&install.to_string_lossy()).unwrap()
        ),
    );
    let yaml = dir.join(".ouroboros").join("config.yaml");
    write(
        &yaml,
        "orchestrator:\n  cli_path: /usr/local/bin/claude-nested\nclarification:\n  default_model: sonnet\nconsensus:\n  judge_model: sonnet\n",
    );

    let tools = phase_section::load_tools(&dir);
    let omc = tools.omc_agents.clone().expect("omc installed → agent list");
    assert_eq!(omc["codeReviewer"], "default", "without config.jsonc the value is default, not the frontmatter model");
    assert_eq!(omc["executor"], "default", "an agent without frontmatter model shows default");
    assert_eq!(
        tools.omc_defaults.as_ref().unwrap()["codeReviewer"],
        "opus",
        "file name → camelCase key, frontmatter model kept as the displayed default"
    );
    let keys = tools.ouroboros_keys.clone().expect("Ouroboros installed → key list");
    assert_eq!(keys["clarification.default_model"], "sonnet", "Ouroboros value read");
    assert!(!keys.contains_key("orchestrator.cli_path"), "only *_model keys are owned");

    let blocks = phase_section::tool_blocks(&new_snapshot(), &tools);
    assert!(
        blocks.len() == 4 && blocks[2].tool == phase_section::OMC_TOOL && blocks[3].tool == phase_section::OUROBOROS_TOOL,
        "omc and Ouroboros come after Claude and Codex"
    );
    let reviewer = blocks[2].knobs.iter().find(|k| k.knob_id == "omc.agent.codeReviewer").unwrap();
    assert!(
        reviewer.value == "default" && reviewer.label.contains("codeReviewer") && reviewer.label.contains("opus"),
        "the omc detail row shows the frontmatter model only in its label"
    );
    assert_eq!(
        blocks[2].knobs.iter().find(|k| k.knob_id == "omc.agent.executor").unwrap().label,
        "executor",
        "an agent without frontmatter model is labelled by its key alone"
    );
    assert!(blocks[3].knobs.iter().any(|k| k.knob_id == "ouroboros.consensus.judge_model"), "the Ouroboros knob id is its dotted key");
    assert!(
        blocks[1].knobs.iter().find(|k| k.knob_id == "codex.codexPlanModeReasoningEffort").unwrap().is_effort,
        "plan mode reasoning effort is an effort knob"
    );

    let rows = phase_section::summary_rows(&new_snapshot(), &tools);
    assert_eq!(
        rows.iter().map(|r| r.phase).collect::<Vec<_>>(),
        [Phase::Planning, Phase::Execution, Phase::Review, Phase::Subagents],
        "phase row order matches macOS"
    );
    let row = |rows: &[phase_section::PhaseModelSummaryRow], phase: Phase| rows.iter().find(|r| r.phase == phase).unwrap().clone();
    assert!(!row(&rows, Phase::Execution).mixed && !row(&rows, Phase::Subagents).mixed, "rows whose knobs are all default are not mixed");
    assert_eq!(row(&rows, Phase::Execution).value, "default", "an all-default row shows default");
    assert!(row(&rows, Phase::Planning).mixed, "Ouroboros sonnet against Claude default makes the planning row mixed across tools");
    let mixed_rows = phase_section::summary_rows(&snapshot(|s| s.claude_main = Some("a".into())), &tools);
    assert!(
        row(&mixed_rows, Phase::Execution).mixed && row(&mixed_rows, Phase::Execution).value == phase_section::MIXED_SENTINEL,
        "a mixed row carries the mixed sentinel"
    );
    assert!(!row(&mixed_rows, Phase::Subagents).mixed, "the subagents row is untouched");

    let edit = phase_section::apply_phase_row(Phase::Planning, "fable", &new_snapshot(), &tools);
    assert!(
        edit.config.claude_opus_alias.as_deref() == Some("fable") && edit.config.claude_main.as_deref() == Some("default"),
        "planning → claude opus alias only"
    );
    let agents = edit.omc_agents.clone().unwrap();
    assert!(agents["planner"] == "fable" && agents["codeReviewer"] == "default", "planning → omc planner, a review agent untouched");
    assert!(
        !agents.contains_key("architect") && !agents.contains_key("critic"),
        "a phase row never adds an agent the install does not have"
    );
    let ouroboros = edit.ouroboros_keys.clone().unwrap();
    assert!(
        ouroboros["clarification.default_model"] == "fable" && ouroboros["consensus.judge_model"] == "sonnet",
        "planning → Ouroboros clarification only"
    );

    let reloaded = phase_section::save_tools(&tools, &edit, &dir);
    assert!(reloaded.error.is_none(), "writing the two files reports no error");
    assert!(
        reloaded.omc_agents.as_ref().unwrap()["planner"] == "fable"
            && reloaded.ouroboros_keys.as_ref().unwrap()["clarification.default_model"] == "fable",
        "both values survive the write"
    );
    assert!(std::fs::read_to_string(&yaml).unwrap().contains("cli_path: /usr/local/bin/claude-nested"), "orchestrator.cli_path survives");

    let knob_edit = phase_section::apply_knob("omc.agent.codeReviewer", "haiku", &new_snapshot(), &reloaded);
    assert!(
        knob_edit.omc_agents.as_ref().unwrap()["codeReviewer"] == "haiku" && knob_edit.omc_agents.as_ref().unwrap()["planner"] == "fable",
        "a detail row changes one agent"
    );
    let after_knob = phase_section::save_tools(&reloaded, &knob_edit, &dir);
    assert!(
        after_knob.omc_agents.as_ref().unwrap()["codeReviewer"] == "haiku" && after_knob.omc_agents.as_ref().unwrap()["planner"] == "fable",
        "a detail row change reaches config.jsonc and reloads"
    );
    let effort_edit = phase_section::apply_knob("codex.codexPlanModeReasoningEffort", "high", &new_snapshot(), &reloaded);
    assert_eq!(effort_edit.config.codex_plan_mode_reasoning_effort.as_deref(), Some("high"), "the effort knob is edited in details");

    // An unreadable file: words the screen can show, the file as it was.
    let store = ModelSettingsFileStore::new(&dir);
    std::fs::write(store.omc_config_path(), "{ not json").unwrap();
    let broken_bytes = std::fs::read(store.omc_config_path()).unwrap();
    let broken = phase_section::load_tools(&dir);
    let path = store.omc_config_path().display().to_string();
    assert!(
        broken.error.as_deref().is_some_and(|e| e.contains(&path) && !e.starts_with("settings.")),
        "unreadable file → visible localized error with its path"
    );
    let refused =
        phase_section::save_tools(&broken, &phase_section::apply_phase_row(Phase::Review, "opus", &new_snapshot(), &broken), &dir);
    assert!(refused.error.as_deref().is_some_and(|e| e.contains(&path)), "a refused write shows the localized error");
    assert_eq!(std::fs::read(store.omc_config_path()).unwrap(), broken_bytes, "the unparseable file stays byte-identical");
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn provider_rows_and_version_pins() {
    common::korean();
    let original = snapshot(|s| {
        s.codex_review_model = Some("review-kept".into());
        s.codex_subagent_default = Some("child-kept".into());
        s.claude_main = Some("main-kept".into());
    });
    let tools = phase_section::smoke_fixture_tools();
    let claude = phase_models::apply_row("claude", Phase::Execution, "fable", &original, &tools).unwrap();
    assert!(
        claude.config.claude_main.as_deref() == Some("fable")
            && claude.config.claude_sonnet_alias.as_deref() == Some("fable")
            && claude.config.codex_review_model.as_deref() == Some("review-kept")
            && claude.config.codex_subagent_default.as_deref() == Some("child-kept"),
        "Claude row cannot overwrite Codex choices"
    );
    assert!(
        claude.omc_agents.as_ref().unwrap()["executor"] == "fable"
            && claude.ouroboros_keys.as_ref().unwrap()["clarification.default_model"] == "default",
        "only matching installed external phase knobs change"
    );
    let codex = phase_models::apply_row("codex", Phase::Review, "new-review", &original, &tools).unwrap();
    assert!(
        codex.config.claude_main.as_deref() == Some("main-kept")
            && codex.config.codex_review_model.as_deref() == Some("new-review")
            && codex.ouroboros_keys.as_ref().unwrap()["evaluation.semantic_model"] == "new-review",
        "Codex review preserves Claude and applies external review knobs"
    );
    assert!(
        phase_models::apply_row("codex", Phase::Execution, "ignored", &original, &PhaseModelTools::default()).is_err(),
        "ignored model knob was accepted"
    );
    let catalog = ModelCatalog::new(
        "fixture",
        vec![
            ModelOption::new("default", "Default", ""),
            ModelOption { resolved_model: Some("claude-sonnet-pinned".into()), ..ModelOption::new("sonnet", "Sonnet", "") },
            ModelOption::new("custom", "custom", ""),
        ],
        "",
    );
    let options = phase_models::versions(&catalog, &[registered("custom", false, &[])], "retired-version");
    assert_eq!(
        options.iter().map(|v| v.value.as_str()).collect::<Vec<_>>(),
        ["sonnet", "claude-sonnet-pinned", "retired-version", "custom"],
        "latest aliases, fixed versions, saved retired values and registrations remain separate and deduplicated"
    );
    let sonnet_label = mighty_core::model_label::option(&catalog.models[1].label_option(), None);
    assert!(
        options[0].label.contains(&sonnet_label) && options[0].label != options[1].label,
        "alias is labelled as latest separately from concrete pin"
    );
    let entry = phase_models::registration(" custom ", &[], true, &["xhigh", "low", "xhigh"]).unwrap();
    assert!(
        entry.name.as_deref() == Some("custom") && entry.supported_effort_levels == Some(vec![Some("low".into()), Some("xhigh".into())]),
        "registrations trim and canonicalize levels"
    );
    for invalid in ["", "default", "bad\"model", "custom"] {
        assert!(
            phase_models::registration(invalid, std::slice::from_ref(&entry), false, &[]).is_err(),
            "invalid registration accepted: {invalid}"
        );
    }
    assert!(phase_models::registration("effort", &[], true, &[]).is_err(), "empty effort set accepted");
}

#[test]
fn effort_and_arguments_reach_all_launch_paths() {
    let config = snapshot(|s| {
        s.claude_main = Some("registered-main".into());
        s.claude_main_effort = Some("max".into());
        s.codex_main_effort = Some("high".into());
        s.codex_subagent_effort = Some("xhigh".into());
        s.codex_subagent_default = Some("child".into());
        s.codex_review_model = Some("review".into());
    });
    let mut request = StartRunRequest::new("session", "workspace", "claude", "hello");
    request.registered_models = vec![registered("registered-main", true, &["max", "low"])];
    request.phase_models = Some(config);
    let resolved = phase_models::resolve_effort(&request, &providers::fallback("claude"));
    assert!(
        resolved.settings.as_ref().map(|s| s.effort.as_str()) == Some("max")
            && request.settings.is_none()
            && phase_models::effective_model(&request) == "registered-main",
        "phase effort uses effective main model without changing saved pane settings"
    );
    let explicit = StartRunRequest { settings: Some(RunSettings { effort: "low".into(), ..RunSettings::default() }), ..request.clone() };
    assert_eq!(
        phase_models::resolve_effort(&explicit, &providers::fallback("claude")).settings.map(|s| s.effort),
        Some("low".into()),
        "explicit pane effort wins"
    );
    let haiku = StartRunRequest { model: "haiku".into(), ..request.clone() };
    assert!(
        phase_models::resolve_effort(&haiku, &providers::fallback("claude")).settings.is_none(),
        "unsupported inherited effort falls back to CLI default"
    );
    let args = providers::arguments(&resolved, "plugin", None).unwrap();
    assert!(
        after(&args, "--model") == "registered-main"
            && after(&args, "--effort") == "max"
            && args.iter().filter(|a| *a == "--settings").count() == 1,
        "effective main and effort reach merged Claude invocation"
    );
    let resumed = providers::arguments(
        &StartRunRequest { phase_models: None, resume_id: Some("previous".into()), ..request.clone() },
        "plugin",
        None,
    )
    .unwrap();
    assert_eq!(after(&resumed, "--model"), "default", "resumed Claude resets a previously pinned model when user selects default");
    let codex = StartRunRequest {
        provider: "codex".into(),
        model: "codex-fixture".into(),
        registered_models: vec![registered("codex-fixture", true, &["high"])],
        ..request.clone()
    };
    let codex = phase_models::resolve_effort(&codex, &providers::fallback("codex"));
    let codex_args = providers::arguments(&codex, "plugin", None).unwrap();
    for expected in ["model_reasoning_effort=\"high\"", "agents.default_subagent_reasoning_effort=\"xhigh\"", "review_model=\"review\""] {
        assert!(codex_args.iter().any(|a| a == expected), "Codex main/review/subagent settings reach exec: {expected}");
    }
    let approval =
        StartRunRequest { settings: Some(RunSettings { permission_mode: "onRequest".into(), ..codex.settings() }), ..codex.clone() };
    let approval_args = providers::arguments(&approval, "plugin", None).unwrap();
    assert!(
        approval_args.iter().any(|a| a == "app-server")
            && approval_args.iter().any(|a| a == "agents.default_subagent_reasoning_effort=\"xhigh\""),
        "approval app-server receives phase settings too"
    );
    let attachment = mighty_run::attachments::make("fixture.txt", b"fixture").unwrap();
    let staged = mighty_run::attachments::StagedAttachments::create(std::slice::from_ref(&attachment)).unwrap();
    let attached = staged.arguments_for(&StartRunRequest { attachments: Some(vec![attachment]), ..resolved.clone() }, "plugin").unwrap();
    assert!(
        attached.iter().any(|a| a == "registered-main")
            && attached.iter().any(|a| a == "max")
            && attached.iter().any(|a| a == "--input-format"),
        "attachment path retains exact phase snapshot and reasoning"
    );
    let _ = staged.dispose();
}

/// PersistenceAndLocalStartSnapshot, but for its last check (a remote's JSON request cannot carry phase models: the remote wire's
/// `StartRunRequest` reader is W8's).
#[tokio::test(flavor = "multi_thread")]
async fn persistence_and_local_start_snapshot() {
    let config = snapshot(|s| {
        s.claude_main_effort = Some("max".into());
        s.codex_main_effort = Some("high".into());
        s.codex_subagent_effort = Some("xhigh".into());
    });
    let state = AppSnapshot { phase_models: Some(config.clone()), ..AppSnapshot::default() };
    let mut out = JsonOut::new();
    state.write(&mut out);
    let json = out.finish();
    let round = AppSnapshot::read(&serde_json::from_str(&json).unwrap()).unwrap();
    assert!(
        round.phase_models.as_ref() == Some(&config) && json.contains("codexSubagentEffort"),
        "optional effort fields use Mac wire names and survive restart"
    );
    let unsafe_config = phase_models::normalized(Some(&snapshot(|s| {
        s.claude_main = Some("bad\"injection".into());
        s.codex_subagent_effort = Some("high\"injection".into());
        s.claude_main_effort = Some("max".into());
    })))
    .unwrap();
    assert!(
        unsafe_config.claude_main.as_deref() == Some("default") && unsafe_config.codex_subagent_effort.is_none(),
        "persisted malformed config cannot enter CLI config strings"
    );

    let folder = common::temp("phase-local");
    let workspace = Workspace { path: Some(folder.to_string_lossy().into_owned()), ..Workspace::default() };
    let pane = mighty_core::model::RunSession {
        id: Some("phase-pane".into()),
        workspace_id: workspace.id.clone(),
        ..mighty_core::model::RunSession::default()
    };
    let snapshot_state = AppSnapshot {
        workspaces: Some(vec![workspace.clone()]),
        sessions: Some(vec![Arc::new(pane)]),
        phase_models: Some(config.clone()),
        ..AppSnapshot::default()
    };
    let finder: providers::Finder = Arc::new(|_: String| Box::pin(async { None }));
    let catalog = Arc::new(providers::ProviderCatalog::new(
        Arc::new(mighty_platform::fake::FakeProcessHost::default()),
        common::environment(),
        Some(finder),
    ));
    let (service, mut events) = mighty_run::RunService::new(
        snapshot_state,
        catalog,
        mighty_run::service::RunServiceOptions {
            plugin_directory: "unused".into(),
            permission_card: false,
            remote: None,
            configure: None,
            admitting: None,
        },
    );
    let mut request = StartRunRequest::new("phase-pane", workspace.id.as_deref().unwrap(), "claude", "hello");
    request.phase_models = Some(snapshot(|s| s.claude_main = Some("untrusted".into())));
    let _ = service.start(request).await;
    let mut accepted = None;
    while let Ok(event) = events.try_recv() {
        if let mighty_run::ServiceEvent::RequestStarting(request) = event {
            accepted = Some(request.phase_models.clone());
        }
    }
    assert_eq!(accepted, Some(Some(config)), "all local/mobile starts snapshot application phase preferences and ignore caller override");
    common::remove_when_free(&folder).await;
}

#[test]
fn legacy_per_mode_keys_load() {
    let json = r#"{
      "version": 1, "workspaces": [], "sessions": [], "layout": "grid", "theme": "dark", "sidebarWidth": 252,
      "modelDefaults": {
        "claude": {"modeDefaults": {"manual": "claude-sonnet-5", "auto": "claude-opus-5", "plan": "claude-fable-5-1"},
                   "registeredModels": [{"name": "acme/legacy", "supportsEffort": false}]},
        "codex": {"modeDefaults": {"manual": "gpt-6-astra"}, "registeredModels": []}
      }
    }"#;
    let state = AppSnapshot::read(&serde_json::from_str(json).unwrap()).unwrap();
    assert_eq!(state.version, 1, "version must stay 1");
    let mode = |state: &AppSnapshot, side: &str, key: &str| {
        let defaults = state.model_defaults.as_ref().expect("modelDefaults field must survive deserialization");
        let side = if side == "codex" { defaults.codex.as_ref() } else { defaults.claude.as_ref() };
        side.and_then(|s| s.mode_defaults.as_ref()).and_then(|m| m.get(key).cloned().flatten())
    };
    assert_eq!(mode(&state, "claude", "manual").as_deref(), Some("claude-sonnet-5"), "legacy claude manual key must be preserved");
    assert_eq!(mode(&state, "claude", "auto").as_deref(), Some("claude-opus-5"), "legacy claude auto key must be preserved");
    assert_eq!(phase_section::registered_of(&state, "claude").len(), 1, "legacy registeredModels must be preserved");
    assert_eq!(mode(&state, "codex", "manual").as_deref(), Some("gpt-6-astra"), "legacy codex manual key must be preserved");
    let mut out = JsonOut::new();
    state.write(&mut out);
    let round = AppSnapshot::read(&serde_json::from_str(&out.finish()).unwrap()).unwrap();
    assert_eq!(mode(&round, "claude", "manual").as_deref(), Some("claude-sonnet-5"), "claude manual must survive round-trip");
    let workspace = Workspace::read(
        &serde_json::from_str(
            r#"{"id": "ws1", "name": "Legacy workspace", "path": "/tmp/ws", "createdAt": "2026-09-24T00:00:00Z",
                "modelDefaults": {"claude": {"modeDefaults": {"plan": "claude-fable-5-1"}, "registeredModels": []},
                                  "codex": {"modeDefaults": {}, "registeredModels": []}}}"#,
        )
        .unwrap(),
    )
    .unwrap();
    let plan = workspace
        .model_defaults
        .as_ref()
        .and_then(|d| d.claude.as_ref())
        .and_then(|c| c.mode_defaults.as_ref())
        .and_then(|m| m.get("plan").cloned().flatten());
    assert_eq!(plan.as_deref(), Some("claude-fable-5-1"), "workspace legacy plan key must be preserved");
}

/// The settings screen's data (W6 draws it): a provider's phase rows with their pickers, review left out for Claude, the words for a
/// phase without a model or effort knob, a mixed row's picker, the alias knobs, and a registered name added and removed.
#[test]
fn the_section_form_is_the_screens_rows() {
    common::korean();
    let config = snapshot(|s| {
        s.claude_main = Some("a".into());
        s.claude_sonnet_alias = Some("b".into());
    });
    let tools = phase_section::smoke_fixture_tools();
    let catalog = |provider: &str| providers::fallback(provider);
    let none = |_: &str| Vec::new();
    let form = phase_section::form(&config, &tools, &catalog, &none);
    assert_eq!(form.providers.iter().map(|p| p.provider.as_str()).collect::<Vec<_>>(), ["claude", "codex"]);
    let claude = &form.providers[0];
    assert_eq!(
        claude.phases.iter().map(|p| p.phase).collect::<Vec<_>>(),
        [Phase::Planning, Phase::Execution, Phase::Subagents],
        "Claude has no review row"
    );
    let execution = &claude.phases[1];
    let model = execution.model.as_ref().unwrap();
    assert!(
        model.current == phase_section::MIXED_SENTINEL
            && model.options[0].value == "default"
            && model.options[1].label == phase_section::mixed_label(),
        "a mixed row's picker"
    );
    assert_eq!(model.id, "phaseModels-model-claude-execution");
    assert_eq!(
        execution.effort.as_ref().unwrap().label,
        format!("{} · {}", phase_section::phase_label(Phase::Execution), mighty_run::strings::get("composer.label.effort"))
    );
    assert!(claude.phases[0].effort.is_none() && claude.phases[0].effort_unsupported.is_some(), "a phase with no effort says so");
    let codex = &form.providers[1];
    assert!(codex.beta && !claude.beta, "Codex carries the beta badge");
    let planning = &codex.phases[0];
    assert!(
        planning.model.is_none()
            && planning.pane_model.as_deref() == Some(mighty_run::strings::get("settings.phaseModels.paneModel").as_str()),
        "a phase that uses the pane's model"
    );
    assert_eq!(
        planning.effort.as_ref().unwrap().options.iter().map(|o| o.value.as_str()).collect::<Vec<_>>(),
        ["default", "low", "medium", "high"],
        "Codex's levels without a list that names any"
    );
    assert_eq!(claude.knobs.len(), 5, "every Claude alias knob");
    assert_eq!(codex.knobs.len(), 2, "Codex's effort knob is a phase row's, not a detail row");

    let state = AppSnapshot::default();
    let added = phase_section::register(&state, "codex", " acme/custom ", true, &["high"]).unwrap();
    assert_eq!(phase_section::registered_of(&added, "codex").iter().map(|e| e.name.clone().unwrap()).collect::<Vec<_>>(), ["acme/custom"]);
    assert!(phase_section::registered_of(&added, "claude").is_empty(), "another provider's names are its own");
    assert!(phase_section::register(&added, "codex", "acme/custom", false, &[]).is_err(), "a name twice is refused");
    assert!(
        phase_section::registered_of(&phase_section::unregister(&added, "codex", "acme/custom"), "codex").is_empty(),
        "the delete button removes it"
    );
}
