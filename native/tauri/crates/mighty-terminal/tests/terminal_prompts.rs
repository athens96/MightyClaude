//! C# TerminalPromptVerification.cs (mirrors Swift TerminalPromptTests.swift; docs/terminal-prompts.md), under the names
//! Verification.cs registers: a command in an agent pane's terminal waiting for input becomes a card the user answers.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_platform::fake::{FakePtyHost, FakeShellEnvironment};
use mighty_terminal::prompt::{Clock, PromptRead, PromptTerminal, TerminalPrompt, TerminalPromptAnswer, TerminalPromptMonitor, detect};
use mighty_terminal::{AgentTerminal, TerminalTextCleaner};

/// "terminal prompts: yes/no questions and their default".
#[test]
fn yes_no_questions_and_their_default() {
    let eas = detect("✔ Using remote Android credentials (Expo server)\n? Generate a new Android Keystore? › (Y/n)").unwrap();
    assert!(
        eas.kind == "yesNo" && eas.default_yes == Some(true) && eas.text == "? Generate a new Android Keystore? › (Y/n)",
        "expo yes/no with default yes: {eas:?}"
    );
    assert_eq!(detect("Overwrite the file? [y/N] ").unwrap().default_yes, Some(false), "[y/N] defaults to no");
    let both = detect("Continue? (yes/no) ").unwrap();
    assert!(both.kind == "yesNo" && both.default_yes.is_none(), "(yes/no) has no default");
    assert_eq!(detect("Proceed (y/n)?").unwrap().kind, "yesNo", "(y/n) before a question mark");
}

/// "terminal prompts: secrets, lines and what is no prompt".
#[test]
fn secrets_text_and_not_prompts() {
    let kind = |text: &str| detect(text).map(|d| d.kind);
    assert_eq!(kind("Enter passphrase for key '/Users/me/.ssh/id_ed25519': "), Some("secret"), "passphrase");
    assert_eq!(kind("[sudo] password for me:"), Some("secret"), "sudo password");
    assert_eq!(kind("\u{BE44}\u{BC00}\u{BC88}\u{D638}: "), Some("secret"), "korean password");
    assert_eq!(kind("Project name:"), Some("text"), "a line");
    assert_eq!(kind("? What is your app named? › my-app"), None, "a prefilled answer is not a question mark at the end");
    assert_eq!(kind("? Package name ›"), Some("text"), "a pointer at the end");
    assert_eq!(kind("Downloading 45%"), None, "a progress line");
    assert_eq!(kind("Compressing project files..."), None, "a line still loading");
    assert_eq!(kind("Build finished.\n"), None, "a finished line");
    assert_eq!(kind(""), None, "nothing");
}

/// "terminal prompts: a list moved with the arrow keys".
#[test]
fn a_list_moved_with_the_arrow_keys() {
    let list =
        detect("? Select a build profile › - Use arrow-keys. Return to submit.\n❯   preview\n    production\n    development").unwrap();
    assert!(list.kind == "choice" && list.text.starts_with("? Select a build profile"), "a list: {list:?}");
    assert_eq!(list.options, ["❯   preview", "production", "development"], "its options, trimmed");
    assert_eq!(detect("? Done\n  a\n  b"), None, "lines without a marked choice are no list");
}

/// "terminal prompts: answers type what the terminal would get".
#[test]
fn answers_type_what_the_terminal_would_get() {
    assert!(TerminalPromptAnswer::yes().input() == "y\r" && TerminalPromptAnswer::no().input() == "n\r", "yes and no");
    assert_eq!(TerminalPromptAnswer::typed("my-app").input(), "my-app\r", "a line");
    assert_eq!(TerminalPromptAnswer::typed("one\nrm -rf /").input(), "one\r", "one answer is one line");
    assert!(TerminalPromptAnswer::key("down").input() == "\u{1b}[B" && TerminalPromptAnswer::key("up").input() == "\u{1b}[A", "arrows");
    assert!(TerminalPromptAnswer::key("enter").input() == "\r" && TerminalPromptAnswer::key("space").input() == " ", "enter and space");
}

/// "terminal prompts: the phone's answer is read strictly".
#[test]
fn the_phones_answer_is_read_strictly() {
    let read = |id: &str, answer: &str, text: Option<&str>| TerminalPromptAnswer::from_phone(Some(id), Some(answer), text);
    assert_eq!(read("h1:40", "yes", None), Some(TerminalPromptAnswer::yes()), "yes");
    assert_eq!(read("h1:40", "down", None), Some(TerminalPromptAnswer::key("down")), "a key");
    assert_eq!(read("h1:40", "text", Some("my-app")), Some(TerminalPromptAnswer::typed("my-app")), "text");
    assert!(read("h1:40", "text", None).is_none() && read("h1:40", "text", Some("")).is_none(), "text needs words");
    assert!(read("h1:40", "text", Some(&"a".repeat(4_097))).is_none(), "text over 4,096 bytes");
    assert!(read("h1:40", "text", Some(&"a".repeat(4_096))).is_some(), "text of 4,096 bytes");
    assert!(read("h1:40", "rm -rf", None).is_none(), "an unknown answer");
    assert!(
        read("", "yes", None).is_none() && read("a\nb", "yes", None).is_none() && read(&"a".repeat(201), "yes", None).is_none(),
        "a malformed id"
    );
    assert!(TerminalPromptAnswer::from_phone(None, Some("yes"), None).is_none(), "no id");
    let prompt = TerminalPrompt {
        id: "h1:40".into(),
        handle: "h1".into(),
        text: "Continue? (Y/n)".into(),
        options: vec![],
        kind: "yesNo",
        default_yes: Some(true),
    };
    let json = serde_json::to_string(&prompt).unwrap();
    assert!(json.contains("\"kind\":\"yesNo\"") && json.contains("\"defaultYes\":true"), "the card travels as plain JSON: {json}");
}

/// C#'s test terminal: each handle's whole output, read from a character offset.
#[derive(Default)]
struct FakePane {
    output: Mutex<BTreeMap<String, String>>,
    running: Mutex<Option<Vec<String>>>,
}

impl PromptTerminal for FakePane {
    fn running_handles(&self) -> Vec<String> {
        self.running.lock().unwrap().clone().unwrap_or_else(|| self.output.lock().unwrap().keys().cloned().collect())
    }
    fn read_output(&self, handle: &str, offset: usize, _max_chars: usize) -> PromptRead {
        let text = self.output.lock().unwrap().get(handle).cloned().unwrap_or_default();
        let chars: Vec<char> = text.chars().collect();
        let start = offset.min(chars.len());
        PromptRead { output: chars[start..].iter().collect(), next_offset: chars.len(), more_remains: false }
    }
}

/// "terminal prompts: the monitor waits for quiet output and hides an answered prompt".
#[test]
fn the_monitor_waits_for_quiet_output_and_hides_an_answered_prompt() {
    let pane = FakePane::default();
    let clock = Arc::new(Mutex::new(Duration::ZERO));
    let now: Clock = {
        let clock = clock.clone();
        Arc::new(move || *clock.lock().unwrap())
    };
    let advance = |seconds: u64| *clock.lock().unwrap() += Duration::from_secs(seconds);
    let monitor = TerminalPromptMonitor::new(Duration::from_millis(800), now);
    pane.output.lock().unwrap().insert("h1".into(), "\u{1b}[32m✔\u{1b}[0m Ready\r\n? Generate a new Android Keystore? › (Y/n)".into());
    assert_eq!(monitor.poll(&pane), None, "just printed: not quiet yet");
    advance(1);
    let prompt = monitor.poll(&pane).expect("quiet: the prompt");
    assert!(
        prompt.kind == "yesNo" && prompt.handle == "h1" && prompt.text == "? Generate a new Android Keystore? › (Y/n)",
        "quiet: the prompt {prompt:?}"
    );

    monitor.answered(&prompt);
    advance(5);
    assert_eq!(monitor.poll(&pane), None, "answered: hidden while nothing new is printed");
    pane.output.lock().unwrap().get_mut("h1").unwrap().push_str("y\r\nPassword: ");
    let _ = monitor.poll(&pane);
    advance(1);
    let next = monitor.poll(&pane).expect("more output: a new prompt");
    assert!(next.kind == "secret" && next.id != prompt.id, "more output: a new prompt {next:?}");

    *pane.running.lock().unwrap() = Some(vec![]);
    assert_eq!(monitor.poll(&pane), None, "a command that ended waits for nothing");
}

/// "terminal prompts: the agent terminal reads a command's output with its own cursor".
#[tokio::test]
async fn the_agent_terminal_keeps_a_read_cursor_apart_from_the_agents_reads() {
    let terminal =
        AgentTerminal::new(std::env::temp_dir(), Arc::new(FakePtyHost::default()), Arc::new(FakeShellEnvironment::new(BTreeMap::new())));
    let read = terminal.read_output("none", 5, 10);
    assert!(terminal.running_handles().is_empty() && read.output.is_empty() && read.next_offset == 5 && !read.more_remains, "nothing runs");
    assert!(!terminal.write_to("none", "x") && !terminal.is_command_running("none"), "no such command takes no typing");
    terminal.dispose().await;
}

/// The cleaner half of C# "Agent IO TerminalCleanerAndEphemeralOwnership" (AgentIOVerification.cs): a sequence split between two
/// reads is removed whole, and an OSC payload (a clipboard write) never reaches the agent.
#[test]
fn terminal_cleaner_keeps_its_state_across_reads() {
    let mut cleaner = TerminalTextCleaner::new();
    assert!(
        cleaner.clean("before\u{1b}[3") == "before" && cleaner.clean("1m한글\u{1b}[0m\r\n") == "한글\n",
        "CSI state survives chunk boundaries"
    );
    assert!(
        cleaner.clean("\u{1b}]52;clipboard-secret").is_empty() && cleaner.clean("\u{1b}\\after") == "after",
        "OSC clipboard/control payload never reaches agent output"
    );
    let mut keeping = TerminalTextCleaner::keeping_carriage_returns();
    assert_eq!(keeping.clean("a\r\u{7}b\u{9b}c\tz"), "a\rbc\tz", "carriage returns kept when asked; BEL and C1 controls dropped");
    assert_eq!(
        cleaner.clean("\u{1b}P1;2q\u{7}x\u{1b}^pm\u{1b}\\y\u{1b}_apc\u{7}z\u{1b}(B!"),
        "xyzB!",
        "DCS, PM, APC strings and a lone escape"
    );
}
