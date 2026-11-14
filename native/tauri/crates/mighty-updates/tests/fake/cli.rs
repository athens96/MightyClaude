//! The checks' stand-in CLI for CLI updates and plugins (what C#'s checks answer with their fake runners), relaunched by the tests
//! through the real process layer. None of these is a real CLI, nothing touches the network, and it writes only into its state
//! folder. std only.
//!
//! The tests copy this program under the name it plays (`claude.exe`, `codex.exe`, `winget.exe`, `node.exe`, `agy.exe`), and it
//! reads its part from its own file name and keeps its state in the folder `MIGHTY_UPDATES_FAKE_STATE` names, else the nearest
//! `fake-state` folder beside one of its own folders (so a check never touches the test process's environment):
//!
//! - every run appends `<name>\t<arg>\t<arg>…` to `calls.txt`, and `FORCE_AUTOUPDATE_PLUGINS`, `DISABLE_AUTOUPDATER`,
//!   `GIT_TERMINAL_PROMPT` and `NO_COLOR` as they reached it to `env.txt`;
//! - `--version` prints `<name>.version` (a default per part); an update (`claude update`, `agy update`, `node … install --global
//!   … <package>@latest`) moves `<name>.next` into `<name>.version`, and `winget upgrade` answers "No applicable upgrade found.";
//! - `claude plugin list --json --available` and `plugin marketplace list --json` print `claude-listing.json` and
//!   `claude-markets.json` (defaults: one plugin `fmt@sample` on offer in the github marketplace `sample`); `plugin install <id> --scope
//!   <scope> --json` answers the install's JSON line, `plugin marketplace update <name>` exits 0, `plugin update <id> … --json`
//!   answers `{"outcome":"ok"}`;
//! - codex answers its four `--help` probes with the JSON flags, its listing and marketplaces from `codex-listing.json` and
//!   `codex-markets.json`, and `plugin add <id> --json` with the installed path, after which its listing names the plugin installed;
//! - `hang` in `MIGHTY_UPDATES_FAKE_HANG` (or a file `hang`) makes any run but `--version` wait 60 s first.

use std::fs::OpenOptions;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::Duration;

const CLAUDE_LISTING: &str = r#"{"installed":[],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","description":"Formats source files","version":"1.2.0","source":{"source":"github"}}]}"#;
const CLAUDE_MARKETS: &str = r#"[{"name":"sample","source":{"source":"github"}}]"#;
const CODEX_MARKETS: &str =
    r#"{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.com/sample"}}]}"#;

fn state() -> PathBuf {
    if let Ok(folder) = std::env::var("MIGHTY_UPDATES_FAKE_STATE") {
        return PathBuf::from(folder);
    }
    let exe = std::env::current_exe().unwrap_or_default();
    exe.ancestors().skip(1).map(|folder| folder.join("fake-state")).find(|state| state.is_dir()).unwrap_or_else(|| PathBuf::from("."))
}

fn read(name: &str) -> Option<String> {
    std::fs::read_to_string(state().join(name)).ok()
}

fn append(name: &str, text: &str) {
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(state().join(name)) {
        let _ = file.write_all(text.as_bytes());
    }
}

fn say(text: &str) {
    let mut out = std::io::stdout().lock();
    let _ = writeln!(out, "{text}");
    let _ = out.flush();
}

fn default_version(part: &str) -> &'static str {
    match part {
        "claude" => "2.1.271 (Claude Code)",
        "codex" => "codex-cli 0.9.0",
        "agy" => "1.0.0",
        "node" => "v22.0.0",
        "winget" => "v1.9.0",
        _ => "1.0.0",
    }
}

fn version(part: &str) -> String {
    read(&format!("{part}.version")).map(|v| v.trim().to_string()).unwrap_or_else(|| default_version(part).to_string())
}

/// Moves `<part>.next` into `<part>.version`, as an installer that replaced the CLI.
fn bump(part: &str) {
    let folder = state();
    if let Ok(next) = std::fs::read_to_string(folder.join(format!("{part}.next"))) {
        let _ = std::fs::write(folder.join(format!("{part}.version")), next);
        let _ = std::fs::remove_file(folder.join(format!("{part}.next")));
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let exe = std::env::current_exe().unwrap_or_default();
    let part = Path::new(&exe).file_stem().and_then(|s| s.to_str()).unwrap_or("").to_ascii_lowercase();
    append("calls.txt", &format!("{}\n", std::iter::once(part.clone()).chain(args.iter().cloned()).collect::<Vec<_>>().join("\t")));
    let names = ["FORCE_AUTOUPDATE_PLUGINS", "DISABLE_AUTOUPDATER", "GIT_TERMINAL_PROMPT", "NO_COLOR"];
    let seen: Vec<String> = names.iter().map(|n| format!("{n}={}", std::env::var(n).unwrap_or_else(|_| "<unset>".into()))).collect();
    append("env.txt", &format!("{part}\t{}\n", seen.join("\t")));

    let refs: Vec<&str> = args.iter().map(String::as_str).collect();
    if refs == ["--version"] {
        say(&version(&part));
        return;
    }
    let hang = std::env::var("MIGHTY_UPDATES_FAKE_HANG").is_ok_and(|v| v == "hang") || state().join("hang").exists();
    if hang {
        std::thread::sleep(Duration::from_secs(60));
    }
    let code = match part.as_str() {
        "claude" => claude(&refs),
        "codex" => codex(&refs),
        "agy" | "pi" | "opencode" | "hermes" => {
            bump(&part);
            say("updated");
            0
        }
        "winget" if refs.first() == Some(&"upgrade") => {
            say("No applicable upgrade found.");
            0
        }
        "node" => node(&refs),
        _ => {
            eprintln!("mighty-updates-fake: unknown part {part} {args:?}");
            64
        }
    };
    std::process::exit(code);
}

fn claude(args: &[&str]) -> i32 {
    match args {
        ["update"] => {
            bump("claude");
            say("updated");
            0
        }
        ["plugin", "list", "--json", "--available"] => {
            say(&read("claude-listing.json").unwrap_or_else(|| CLAUDE_LISTING.into()));
            0
        }
        ["plugin", "marketplace", "list", "--json"] => {
            say(&read("claude-markets.json").unwrap_or_else(|| CLAUDE_MARKETS.into()));
            0
        }
        ["plugin", "install", id, "--scope", scope, "--json"] => {
            say("installing…");
            say(&format!(r#"{{"command":"install","outcome":"ok","pluginId":"{id}","scope":"{scope}"}}"#));
            0
        }
        ["plugin", "marketplace", "update", _] => {
            say("updated");
            0
        }
        ["plugin", "update", _, "--scope", "user", "--json"] => {
            say(r#"{"outcome":"ok"}"#);
            0
        }
        _ => {
            eprintln!("claude: unexpected {args:?}");
            2
        }
    }
}

fn codex(args: &[&str]) -> i32 {
    match args {
        ["plugin", "list", "--help"] => say("  codex plugin list [OPTIONS]\n  --json    Output JSON\n  --available  Include catalog"),
        ["plugin", "add", "--help"] => say("  codex plugin add [OPTIONS]\n  --json    Output JSON"),
        ["plugin", "marketplace", "list", "--help"] => say("  codex plugin marketplace list [OPTIONS]\n  --json    Output JSON"),
        ["plugin", "marketplace", "upgrade", "--help"] => say("  codex plugin marketplace upgrade [OPTIONS]\n  --json    Output JSON"),
        ["plugin", "list", "--json", "--available"] => {
            say(&read("codex-listing.json").unwrap_or_else(|| codex_listing(state().join("codex-installed").exists())))
        }
        ["plugin", "marketplace", "list", "--json"] => say(&read("codex-markets.json").unwrap_or_else(|| CODEX_MARKETS.into())),
        ["plugin", "add", id, "--json"] => {
            let _ = std::fs::write(state().join("codex-installed"), id);
            let name = id.split('@').next().unwrap_or("");
            let market = id.split('@').nth(1).unwrap_or("");
            let installed = state().join("plugins").join(name);
            let path = installed.to_string_lossy().replace('\\', "\\\\");
            say(&format!(r#"{{"pluginId":"{id}","name":"{name}","marketplaceName":"{market}","installedPath":"{path}"}}"#));
        }
        ["plugin", "marketplace", "upgrade", name, "--json"] => {
            say(&format!(r#"{{"selectedMarketplaces":["{name}"],"upgradedRoots":[],"errors":[]}}"#))
        }
        ["plugin", "marketplace", "upgrade", "--json"] => say(r#"{"selectedMarketplaces":["sample"],"upgradedRoots":[],"errors":[]}"#),
        _ => {
            eprintln!("codex: unexpected {args:?}");
            return 2;
        }
    }
    0
}

fn codex_listing(installed: bool) -> String {
    let row = r#"{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installed":true,"enabled":true}"#;
    format!(
        r#"{{"installed":[{}],"available":[{{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installed":{installed},"enabled":false,"installPolicy":"AVAILABLE"}}]}}"#,
        if installed { row } else { "" }
    )
}

/// `node <npm-cli.js> install --global --prefix <prefix> <package>@latest --no-audit --no-fund`: the package's CLI moves to its next
/// version.
fn node(args: &[&str]) -> i32 {
    match args {
        [script, "install", "--global", "--prefix", _, package, "--no-audit", "--no-fund"] if script.ends_with("npm-cli.js") => {
            let part = match *package {
                "@openai/codex@latest" => "codex",
                "@anthropic-ai/claude-code@latest" => "claude",
                _ => return 3,
            };
            bump(part);
            say("added 1 package");
            0
        }
        _ => {
            eprintln!("node: unexpected {args:?}");
            2
        }
    }
}
