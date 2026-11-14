//! C# BedrockSettingsVerification (MightyCode.Core.Tests/BedrockSettingsVerification.cs, Verification.cs:431-432): a Bedrock status
//! claims no verified access, and the reset of the user's Claude settings — here in a temporary home only — keeps everything else
//! and leaves a backup.

mod common;

use common::{env, temp, text};
use mighty_accounts::accounts::{self, CliLoginOption, method};
use mighty_accounts::bedrock;
use serde_json::Value;

/// BedrockSettingsVerification.cs:6 `ExternalStatusDoesNotClaimVerifiedAccess`.
#[test]
fn external_status_does_not_claim_verified_access() {
    common::korean();
    let status = accounts::parse_claude_status(
        r#"{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock","email":"old-account@example.test","subscriptionType":"max"}"#,
    );
    assert!(
        status.method.as_deref() == Some("AWS Bedrock")
            && status.method_id.as_deref() == Some(method::CLAUDE_BEDROCK)
            && status.access_verified == Some(false)
            && !status.can_sign_out,
        "configuration is not verified access or OAuth signout"
    );
    assert!(status.account.is_none() && status.plan.is_none(), "leftover subscription identity cannot label Bedrock credentials");
    assert_eq!(status.detail, bedrock::configuration_only(), "Bedrock says it is configuration");
    let signed_out = accounts::parse_claude_status(r#"{"loggedIn":false,"authMethod":"third_party","apiProvider":"bedrock"}"#);
    assert!(
        signed_out.method.as_deref() == Some("AWS Bedrock") && signed_out.logged_in == Some(false),
        "Bedrock configuration remains visible before login"
    );
    assert_eq!(
        accounts::login_arguments("claude", CliLoginOption::Bedrock),
        Some(vec!["claude", "/setup-bedrock"]),
        "the CLI owns its interactive setup"
    );
    let conflict = bedrock::conflict_detail(
        &env(&[("AWS_BEARER_TOKEN_BEDROCK", "placeholder-one")]),
        Some(r#"{"env":{"AWS_BEARER_TOKEN_BEDROCK":"placeholder-two"}}"#),
    );
    assert!(conflict.as_deref().is_some_and(|c| !c.contains("placeholder")), "conflicts reveal no key material");
    assert!(
        bedrock::conflict_detail(&env(&[("AWS_BEARER_TOKEN_BEDROCK", "same")]), Some(r#"{"env":{"AWS_BEARER_TOKEN_BEDROCK":"same"}}"#))
            .is_none(),
        "identical tokens are not a conflict"
    );
    assert!(
        bedrock::conflict_detail(&env(&[("AWS_BEARER_TOKEN_BEDROCK", "same")]), Some("[]")).is_none(),
        "malformed settings do not break account status"
    );
}

/// BedrockSettingsVerification.cs:22 `ResetPreservesUnrelatedCredentialsAndMakesBackup`.
#[test]
fn reset_preserves_unrelated_credentials_and_makes_backup() {
    const ORIGINAL: &str = r#"{"env":{"CLAUDE_CODE_USE_BEDROCK":"1","AWS_BEARER_TOKEN_BEDROCK":"fixture-token","AWS_REGION":"ap-northeast-2","AWS_ACCESS_KEY_ID":"unrelated-aws-key","ANTHROPIC_DEFAULT_SONNET_MODEL":"apac.anthropic.claude-sonnet-4-5","CUSTOM_FLAG":"keep"},"model":"apac.anthropic.claude-test","permissions":{"deny":["Bash(rm *)"]}}"#;
    let reset: Value = serde_json::from_str(&bedrock::reset_user_settings_json(ORIGINAL).unwrap()).unwrap();
    let env_block = &reset["env"];
    assert!(
        env_block.get("AWS_BEARER_TOKEN_BEDROCK").is_none()
            && env_block.get("CLAUDE_CODE_USE_BEDROCK").is_none()
            && env_block.get("AWS_REGION").is_none(),
        "Bedrock environment settings are removed"
    );
    assert!(
        env_block["AWS_ACCESS_KEY_ID"] == "unrelated-aws-key" && env_block["CUSTOM_FLAG"] == "keep" && reset.get("permissions").is_some(),
        "unrelated AWS credentials and permission denies survive"
    );
    assert!(
        reset.get("model").is_none() && env_block.get("ANTHROPIC_DEFAULT_SONNET_MODEL").is_none(),
        "only Bedrock model pins are removed"
    );
    let other: Value = serde_json::from_str(
        &bedrock::reset_user_settings_json(
            r#"{"env":{"AWS_REGION":"eu-west-1","ANTHROPIC_DEFAULT_SONNET_MODEL":"sonnet"},"model":"sonnet"}"#,
        )
        .unwrap(),
    )
    .unwrap();
    assert!(other["model"] == "sonnet" && other["env"]["AWS_REGION"] == "eu-west-1", "non-Bedrock model and region settings survive");
    assert!(
        bedrock::reset_user_settings_json("[]").is_err() && bedrock::reset_user_settings_json(r#"{"env":3}"#).is_err(),
        "only an object, with an object env"
    );

    let home = temp("bedrock-reset");
    std::fs::create_dir_all(home.join(".claude")).unwrap();
    let path = home.join(".claude").join("settings.json");
    std::fs::write(&path, ORIGINAL).unwrap();
    let backup = bedrock::reset_user_settings(&text(&home), &env(&[])).unwrap().expect("a backup");
    assert_eq!(std::fs::read_to_string(&backup).unwrap(), ORIGINAL, "backup preserves the original complete settings");
    let written = std::fs::read_to_string(&path).unwrap();
    assert!(!written.contains("fixture-token") && written.ends_with('\n'), "reset was actually written");
    assert_eq!(bedrock::reset_user_settings(&text(&home), &env(&[])).unwrap(), None, "repeated reset is a no-op");
    assert_eq!(bedrock::reset_user_settings(&text(&home.join("nobody")), &env(&[])).unwrap(), None, "no settings file, nothing to do");
    let _ = std::fs::remove_dir_all(&home);
}

/// `ResetUserSettingsJson` writes as System.Text.Json writes indented JSON: two spaces, `"name": value`, the OS's new line,
/// `JavaScriptEncoder.Default`'s escapes, a final newline.
#[test]
fn the_reset_settings_are_written_as_system_text_json_writes_them() {
    let new_line = mighty_run::os::new_line();
    let written = bedrock::reset_user_settings_json(r#"{"a":"<é>","b":[],"c":{},"d":[1,true,null]}"#).unwrap();
    let expected = format!(
        "{{{n}  \"a\": \"\\u003C\\u00E9\\u003E\",{n}  \"b\": [],{n}  \"c\": {{}},{n}  \"d\": [{n}    1,{n}    true,{n}    null{n}  ]{n}}}\n",
        n = new_line
    );
    assert_eq!(written, expected);
    assert_eq!(
        bedrock::settings_path("/home/me", &env(&[("CLAUDE_CONFIG_DIR", "/etc/claude")])),
        mighty_run::os::combine("/etc/claude", "settings.json"),
        "CLAUDE_CONFIG_DIR"
    );
}

/// Review C15: a number is written back as it was read (`JsonNode` keeps its text), so settings whose only difference would be
/// `1.0` against `1` are not rewritten, and no backup is left.
#[test]
fn numbers_keep_their_text_and_unchanged_settings_are_left_alone() {
    let new_line = mighty_run::os::new_line();
    let written = bedrock::reset_user_settings_json(r#"{"a":1.0,"b":[1e2,-0.50],"model":"apac.anthropic.claude-test"}"#).unwrap();
    assert_eq!(written, format!("{{{n}  \"a\": 1.0,{n}  \"b\": [{n}    1e2,{n}    -0.50{n}  ]{n}}}\n", n = new_line));
    assert_eq!(
        bedrock::reset_user_settings_json(r#"{"x":1,"x":2,"y":3}"#).unwrap(),
        format!("{{{n}  \"x\": 2,{n}  \"y\": 3{n}}}\n", n = new_line),
        "a name given twice keeps its place and its last value"
    );

    let home = temp("bedrock-reset-numbers");
    std::fs::create_dir_all(home.join(".claude")).unwrap();
    let path = home.join(".claude").join("settings.json");
    const ORIGINAL: &str = r#"{"cleanupPeriodDays":30.0,"env":{"CUSTOM":"keep"}}"#;
    std::fs::write(&path, ORIGINAL).unwrap();
    assert_eq!(bedrock::reset_user_settings(&text(&home), &env(&[])).unwrap(), None, "nothing of Bedrock, nothing written");
    assert_eq!(std::fs::read_to_string(&path).unwrap(), ORIGINAL, "the settings are untouched");
    assert_eq!(std::fs::read_dir(home.join(".claude")).unwrap().count(), 1, "no backup and no temporary file");
    let _ = std::fs::remove_dir_all(&home);
}

/// Review C5 and C14: the backup takes the settings' permissions (C# `File.Copy` keeps the mode), and a read-only settings file
/// leaves no temporary file behind whether or not it could be replaced.
#[test]
fn the_backup_keeps_the_settings_permissions_and_no_temporary_is_left() {
    let home = temp("bedrock-reset-read-only");
    let folder = home.join(".claude");
    std::fs::create_dir_all(&folder).unwrap();
    let path = folder.join("settings.json");
    std::fs::write(&path, r#"{"env":{"CLAUDE_CODE_USE_BEDROCK":"1","CUSTOM":"keep"}}"#).unwrap();
    let mut read_only = std::fs::metadata(&path).unwrap().permissions();
    read_only.set_readonly(true);
    std::fs::set_permissions(&path, read_only.clone()).unwrap();

    let result = bedrock::reset_user_settings(&text(&home), &env(&[]));
    let names: Vec<String> = std::fs::read_dir(&folder).unwrap().map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
    assert!(!names.iter().any(|n| n.ends_with(".tmp")), "no temporary file is left: {names:?}");
    let backup = names.iter().find(|n| n.starts_with("settings.json.bedrock-backup-")).expect("the backup was made first");
    let backup = folder.join(backup);
    assert_eq!(std::fs::metadata(&backup).unwrap().permissions(), read_only, "the backup has the settings' permissions");
    assert!(std::fs::read_to_string(&backup).unwrap().contains("CLAUDE_CODE_USE_BEDROCK"), "the backup holds the original");
    let settings = std::fs::read_to_string(&path).unwrap();
    match result {
        Ok(made) => assert!(made.is_some() && !settings.contains("BEDROCK"), "replaced: {settings}"),
        Err(_) => assert!(settings.contains("BEDROCK"), "refused, the settings as they were"),
    }

    for entry in std::fs::read_dir(&folder).unwrap() {
        let file = entry.unwrap().path();
        let mut writable = std::fs::metadata(&file).unwrap().permissions();
        #[allow(clippy::permissions_set_readonly_false, reason = "the test's own files, to remove them")]
        writable.set_readonly(false);
        std::fs::set_permissions(&file, writable).unwrap();
    }
    let _ = std::fs::remove_dir_all(&home);
}
