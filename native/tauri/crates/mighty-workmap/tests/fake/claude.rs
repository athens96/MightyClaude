//! The scripted Claude of the summary checks (C# `WorkMapVerification.FakeClaudeAsync`, the test exe's `--workmap-fake ok|hold|silent`):
//! reads the one user message and answers with a result event. `ok` answers "got:<content>|<working folder>|tools-off|on", `hold`
//! waits a minute without answering, `silent` ends after an init event.

use std::io::{BufRead, Write};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mode = args.first().cloned().unwrap_or_default();
    let mut line = String::new();
    let _ = std::io::stdin().lock().read_line(&mut line);
    let mut out = std::io::stdout().lock();
    match mode.as_str() {
        "hold" => std::thread::sleep(std::time::Duration::from_secs(60)),
        "silent" => {
            let _ = writeln!(out, "{{\"type\":\"system\",\"subtype\":\"init\"}}");
        }
        _ => {
            let message: serde_json::Value = serde_json::from_str(line.trim()).unwrap_or_default();
            let content = message["message"]["content"].as_str().unwrap_or("").to_string();
            let tools = match args.iter().position(|a| a == "--tools") {
                Some(at) if args.get(at + 1).is_some_and(|v| v.is_empty()) => "tools-off",
                _ => "tools-on",
            };
            let folder = std::env::current_dir().map(|d| d.to_string_lossy().into_owned()).unwrap_or_default();
            let _ = writeln!(out, "{{\"type\":\"system\",\"subtype\":\"init\"}}");
            let result = serde_json::json!({"type": "result", "subtype": "success", "is_error": false, "result": format!("got:{content}|{folder}|{tools}")});
            let _ = writeln!(out, "{result}");
        }
    }
    let _ = out.flush();
}
