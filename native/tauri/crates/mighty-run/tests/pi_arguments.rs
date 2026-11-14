//! The argv a pi run gets (C# PiVerification.cs:135 `PermissionsBecomeTheToolAllowlistAndThinkingLevelsAreTheEffort`, PiDialogVerification.cs:132
//! `TheShippedExtensionIsLoadedForEachRunAndTheReadOnlyListNamesItsTool`): both move the folder the shipped agent files are looked up in
//! (`AgentFiles.Directory`, mighty-run's `set_agent_files`), a process-wide setting, so they run one at a time in a binary of their own.

mod common;

use std::sync::Mutex;

use mighty_core::model::RunSettings;
use mighty_run::model::StartRunRequest;
use mighty_run::{RunError, providers};

/// The agent files' folder is the process's: one check moves it at a time.
static AGENT_FILES: Mutex<()> = Mutex::new(());

const SESSION_ID: &str = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30";

const MODEL_LIST: &str = "provider   model                      context  max-out  thinking  images\n\
anthropic  claude-sonnet-4-5          200K     64K      yes       yes\n\
anthropic  claude-opus-4-1            200K     32K      yes       yes\n\
openai     gpt-4o                     128K     16.4K    no        yes\n\
openrouter anthropic/claude-3.5-haiku 200K     8.2K     no        yes\n";

fn request(mode: &str, model: &str, effort: &str, resume: Option<&str>) -> StartRunRequest {
    let mut value = StartRunRequest::new("pane", "w", "claude", "hi").with_provider("pi").with_settings(RunSettings {
        effort: effort.into(),
        permission_mode: mode.into(),
        ..RunSettings::default()
    });
    value.model = model.into();
    value.resume_id = resume.map(str::to_string);
    value
}

fn arguments(value: &StartRunRequest) -> Vec<String> {
    providers::arguments(value, "", None).unwrap()
}

fn after(args: &[String], flag: &str) -> Option<String> {
    args.iter().position(|a| a == flag).and_then(|at| args.get(at + 1)).cloned()
}

/// PiVerification.cs:135 `PermissionsBecomeTheToolAllowlistAndThinkingLevelsAreTheEffort`.
#[test]
fn permissions_become_the_tool_allowlist_and_thinking_levels_are_the_effort() {
    common::korean();
    let _one = AGENT_FILES.lock().unwrap_or_else(|p| p.into_inner());
    // The shapes without the shipped ask_user extension (the next check has them with it).
    let bare = common::temp("pi-bare");
    providers::set_agent_files(Some(bare.clone()));
    assert_eq!(arguments(&request("manual", "default", "default", None)), ["--mode", "rpc", "--tools", "read,grep,find,ls"], "read-only");
    assert_eq!(arguments(&request("fullAccess", "default", "default", None)), ["--mode", "rpc"], "full access leaves every tool on");
    assert_eq!(
        arguments(&request("manual", "anthropic/claude-sonnet-4-5", "high", Some(SESSION_ID))),
        [
            "--mode",
            "rpc",
            "--model",
            "anthropic/claude-sonnet-4-5",
            "--thinking",
            "high",
            "--tools",
            "read,grep,find,ls",
            "--session",
            SESSION_ID
        ],
        "model, thinking, tools, session"
    );
    assert_eq!(arguments(&request("fullAccess", "default", "off", None)), ["--mode", "rpc", "--thinking", "off"], "off is a level");
    providers::set_agent_files(Some(common::agents()));
    let _ = std::fs::remove_dir_all(&bare);
    assert_eq!(providers::permission_modes("pi", false), ["manual", "fullAccess"], "the two modes pi has");
    let other = RunSettings { effort: "minimal".into(), permission_mode: "acceptEdits".into(), ..RunSettings::default() };
    assert_eq!(providers::normalize_settings("pi", Some(&other)).permission_mode, "manual", "another mode is not pi's");
    let minimal = RunSettings { effort: "minimal".into(), permission_mode: "fullAccess".into(), ..RunSettings::default() };
    let claude = RunSettings { effort: "minimal".into(), ..RunSettings::default() };
    assert!(
        providers::normalize_settings("pi", Some(&minimal)).effort == "minimal"
            && providers::normalize_settings("claude", Some(&claude)).effort == "default",
        "minimal is pi's level only"
    );
    let catalog = providers::pi_catalog(MODEL_LIST);
    assert_eq!(
        providers::efforts("pi", "default", &providers::fallback("pi"), None),
        ["off", "minimal", "low", "medium", "high", "xhigh"],
        "pi's levels"
    );
    assert!(
        providers::efforts("pi", "openai/gpt-4o", &catalog, None).is_empty()
            && providers::efforts("pi", "anthropic/claude-sonnet-4-5", &catalog, None).len() == 6,
        "a model without thinking has no levels"
    );
    request("manual", "default", "minimal", None).validate().unwrap();
    let max = StartRunRequest::new("p", "w", "claude", "x")
        .with_provider("pi")
        .with_settings(RunSettings { effort: "max".into(), ..RunSettings::default() });
    assert!(matches!(max.validate(), Err(RunError::Invalid(_))), "max is not a pi level");
}

/// PiDialogVerification.cs:132 `TheShippedExtensionIsLoadedForEachRunAndTheReadOnlyListNamesItsTool`.
#[test]
fn the_shipped_extension_is_loaded_for_each_run_and_the_read_only_list_names_its_tool() {
    common::korean();
    let _one = AGENT_FILES.lock().unwrap_or_else(|p| p.into_inner());
    providers::set_agent_files(Some(common::agents()));
    let file = providers::agent_file(providers::PI_QUESTION_EXTENSION).expect("the shipped extension");
    let text = std::fs::read_to_string(&file).unwrap();
    assert!(text.contains("ask_user") && !text.contains("require("), "the shipped extension registers ask_user with no dependencies");
    let manual = arguments(&request("manual", "default", "default", None));
    assert!(
        after(&manual, "--tools").as_deref() == Some("read,grep,find,ls,ask_user") && after(&manual, "--extension") == Some(file.clone()),
        "manual: {manual:?}"
    );
    let full = arguments(&request("fullAccess", "default", "default", None));
    assert!(!full.iter().any(|a| a == "--tools") && after(&full, "--extension") == Some(file.clone()), "full access: {full:?}");
    // Without the shipped file pi runs without the tool, as before.
    let folder = common::temp("pi-no-agents");
    providers::set_agent_files(Some(folder.clone()));
    assert!(providers::agent_file(providers::PI_QUESTION_EXTENSION).is_none(), "a folder without the file");
    let bare = arguments(&request("manual", "default", "default", None));
    let restored = (after(&bare, "--tools"), bare.iter().any(|a| a == "--extension"));
    providers::set_agent_files(Some(common::agents()));
    let _ = std::fs::remove_dir_all(&folder);
    assert!(restored.0.as_deref() == Some("read,grep,find,ls") && !restored.1, "without the file: {bare:?}");
}
