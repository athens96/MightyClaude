//! The port against C# itself: `tests/parity/expected.json` is what C# MightyCode.Core answered for fixed inputs (written by the
//! tiny .NET program beside it, `tests/parity/Program.cs`, which lists every input); each Rust function must answer the same.
#![cfg(windows)]

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use mighty_platform::TerminalInput;
use mighty_platform_windows::command::{batch_command_line, command_interpreter, launch, quote_windows};
use mighty_platform_windows::discovery::{files, hermes_install_folder, version_text};
use mighty_platform_windows::{Variables, environment, pty, user_environment};
use serde_json::Value;

fn expected() -> Value {
    serde_json::from_str(include_str!("parity/expected.json")).expect("expected.json")
}

fn text(value: &Value) -> &str {
    value.as_str().unwrap_or_else(|| panic!("a string: {value}"))
}

fn strings(value: &Value) -> Vec<String> {
    value.as_array().expect("an array").iter().map(|item| text(item).to_string()).collect()
}

fn variables(value: &Value) -> Variables {
    value.as_object().map(|map| map.iter().map(|(name, value)| (name.clone(), text(value).to_string())).collect()).unwrap_or_default()
}

/// C#'s dictionary, exactly: the same names (spelled as C# kept them) with the same values.
fn assert_same_values(actual: &Variables, expected: &Value, case: &str) {
    let expected = expected.as_object().expect("an object");
    assert_eq!(actual.len(), expected.len(), "{case}: how many");
    for (name, value) in expected {
        let kept = actual.iter().find(|(actual, _)| actual.eq_ignore_ascii_case(name));
        assert_eq!(kept, Some((name.as_str(), text(value))), "{case}: {name}");
    }
}

#[test]
fn quote_windows_matches_csharp() {
    for case in expected()["quote"].as_array().unwrap() {
        assert_eq!(quote_windows(text(&case["input"])), text(&case["output"]), "{case}");
    }
}

#[test]
fn batch_command_line_matches_csharp() {
    for case in expected()["batch"].as_array().unwrap() {
        let line = batch_command_line(text(&case["batch"]), &strings(&case["arguments"])).ok();
        assert_eq!(line.as_deref(), case["output"].as_str(), "{case}");
    }
}

#[test]
fn launch_matches_csharp() {
    let temp = std::env::temp_dir();
    for case in expected()["launch"].as_array().unwrap() {
        let environment = case["environment"].as_object().map(|map| map.iter().map(|(k, v)| (k.clone(), text(v).to_string())).collect());
        let given_variables = case["variables"].is_object().then(|| variables(&case["variables"]));
        let request =
            launch(text(&case["resolved"]), &strings(&case["arguments"]), &temp, environment.as_ref(), given_variables.as_ref()).unwrap();
        let interpreter = request.program == command_interpreter(given_variables.as_ref());
        assert_eq!(interpreter, case["programIsInterpreter"].as_bool().unwrap(), "{case}");
        if !interpreter {
            assert_eq!(request.program.to_string_lossy(), text(&case["program"]), "{case}");
        }
        assert_eq!(request.args, strings(&case["argumentList"]), "{case}");
        assert_eq!(request.shell_command.as_deref(), case["shellCommand"].as_str(), "{case}");
        // C#'s ProcessStartInfo starts from this process's environment, whose spelling a name keeps; the port's request is only what
        // is laid over it, so names compare ignoring case.
        let after = case["environmentAfter"].as_object().unwrap();
        assert_eq!(request.env.len(), after.len(), "{case}");
        for (name, value) in after {
            let set = request.env.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.as_str());
            assert_eq!(set, Some(text(value)), "{case}: {name}");
        }
    }
}

#[test]
fn shell_arguments_match_csharp() {
    for case in expected()["shellArguments"].as_array().unwrap() {
        let shell = std::path::Path::new(text(&case["shell"]));
        let output = pty::shell_arguments(shell, case["command"].as_str(), case["noProfile"].as_bool().unwrap());
        assert_eq!(output, strings(&case["output"]), "{case}");
    }
}

#[test]
fn the_environment_capture_command_matches_csharp() {
    let case = &expected()["environmentCommand"];
    let nonce = text(&case["nonce"]);
    assert_eq!(environment::command(nonce).as_deref(), Some(text(&case["command"])));
    assert_eq!(environment::arguments(nonce).unwrap(), strings(&case["arguments"]));
    assert!(case["uppercaseNonceCommand"].is_null() && environment::command(&nonce.to_uppercase()).is_none());
    assert!(case["shortNonceCommand"].is_null() && environment::command("abc").is_none());
}

/// The inputs too large to keep in the file, made as Program.cs makes them.
fn large_input(name: &str, nonce: &str) -> String {
    let frame = |json: String| {
        format!("profile chatter\n\0MIGHTY_ENV_{nonce}\0{}\0MIGHTY_END_{nonce}\0after profile output", STANDARD.encode(json.as_bytes()))
    };
    let many = |count: usize| format!("{{\"PATH\":\"x\"{}}}", (1..count).map(|i| format!(",\"V{i}\":\"v\"")).collect::<String>());
    match name {
        "4096 names" => frame(many(4096)),
        "4097 names" => frame(many(4097)),
        "a value of 131072 characters" => frame(format!("{{\"PATH\":\"{}\"}}", "v".repeat(131_072))),
        "a value of 131073 characters" => frame(format!("{{\"PATH\":\"{}\"}}", "v".repeat(131_073))),
        other => panic!("no large input named {other}"),
    }
}

#[test]
fn the_environment_frame_parse_matches_csharp() {
    for case in expected()["environmentParse"].as_array().unwrap() {
        let (name, nonce) = (text(&case["name"]), text(&case["nonce"]));
        let output = case["output"].as_str().map(String::from).unwrap_or_else(|| large_input(name, nonce));
        let parsed = environment::parse(&output, nonce);
        assert_eq!(parsed.as_ref().map(Variables::len), case["count"].as_u64().map(|count| count as usize), "{name}");
        if let (Some(parsed), Some(values)) = (&parsed, case["values"].as_object()) {
            assert_same_values(parsed, &Value::Object(values.clone()), name);
        }
    }
}

#[test]
fn the_user_environment_block_parse_matches_csharp() {
    for case in expected()["userEnvironmentParse"].as_array().unwrap() {
        assert_same_values(&user_environment::parse(text(&case["block"])), &case["values"], &case.to_string());
    }
}

#[test]
fn cli_discovery_matches_csharp() {
    let expected = expected();
    for case in expected["hermesInstallFolder"].as_array().unwrap() {
        assert_eq!(hermes_install_folder(&variables(&case["environment"])).as_deref(), case["folder"].as_str(), "{case}");
    }
    let windows_environment = variables(&expected["hermesInstallFolder"][0]["environment"]);
    for case in expected["discoveryFiles"].as_array().unwrap() {
        let directories = strings(&case["directories"]);
        let found = files(
            text(&case["provider"]),
            directories.iter().map(String::as_str),
            case["windows"].as_bool().unwrap(),
            &windows_environment,
        );
        assert_eq!(found, strings(&case["files"]), "{case}");
    }
    for case in expected["versionText"].as_array().unwrap() {
        assert_eq!(version_text(text(&case["program"]), case["output"].as_str()), text(&case["text"]), "{case}");
    }
}

#[test]
fn terminal_input_line_breaks_match_csharp() {
    for case in expected()["hasLineBreak"].as_array().unwrap() {
        assert_eq!(TerminalInput::has_line_break(text(&case["text"])), case["value"].as_bool().unwrap(), "{case}");
    }
}
