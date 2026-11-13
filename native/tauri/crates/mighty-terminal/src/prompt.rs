//! A command in an agent pane's terminal that waits for the user to type something (C# TerminalPrompt.cs; docs/terminal-prompts.md;
//! Swift TerminalPrompt.swift): the pane and the phone show it as a card, and the answer is typed into the command's terminal.
//! [`TerminalPromptMonitor`] follows the commands of one terminal ([`PromptTerminal`], the agent's [`crate::agent::AgentTerminal`])
//! with a read cursor of its own, and asks [`detect`] about each one's cleaned output once it has been quiet.

use std::collections::HashMap;
use std::sync::{Arc, LazyLock, Mutex};
use std::time::{Duration, Instant};

use mighty_core::wire::utf16_len;
use regex::Regex;
use serde::Serialize;

use crate::cleaner::TerminalTextCleaner;
use crate::text::{first_units, last_units};

/// The longest prompt text a card shows, in UTF-16 units (`TerminalPrompt.MaximumTextLength`).
pub const MAXIMUM_TEXT_LENGTH: usize = 300;
/// The most options a list card offers (`TerminalPrompt.MaximumOptions`).
pub const MAXIMUM_OPTIONS: usize = 12;

/// `TerminalPromptKinds`.
pub mod kinds {
    pub const YES_NO: &str = "yesNo";
    pub const SECRET: &str = "secret";
    pub const TEXT: &str = "text";
    pub const CHOICE: &str = "choice";
    pub const ALL: [&str; 4] = [YES_NO, SECRET, TEXT, CHOICE];
}

/// C# `TerminalPrompt(Id, Handle, Text, Options, Kind, DefaultYes?)`, in its JSON shape (the phone's `terminalPrompt`). `id` is
/// `<handle>:<cleaned output length>`: a new wait after more output is a new prompt. `default_yes` is for yesNo: true when Enter
/// alone answers yes, false for no, None for neither.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalPrompt {
    pub id: String,
    pub handle: String,
    pub text: String,
    pub options: Vec<String>,
    pub kind: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub default_yes: Option<bool>,
}

/// What the user answered a [`TerminalPrompt`] with (C# `TerminalPromptAnswer`); [`TerminalPromptAnswer::input`] is what that types.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerminalPromptAnswer {
    pub name: String,
    pub line: Option<String>,
}

/// The longest typed answer the phone (or the page) may send, in UTF-8 bytes (`MaximumPhoneText`).
pub const MAXIMUM_PHONE_TEXT: usize = 4_096;
/// The keys an answer may press (`TerminalPromptAnswer.Keys`).
pub const KEYS: [&str; 4] = ["up", "down", "enter", "space"];

impl TerminalPromptAnswer {
    pub fn yes() -> TerminalPromptAnswer {
        TerminalPromptAnswer { name: "yes".into(), line: None }
    }
    pub fn no() -> TerminalPromptAnswer {
        TerminalPromptAnswer { name: "no".into(), line: None }
    }
    pub fn typed(line: &str) -> TerminalPromptAnswer {
        TerminalPromptAnswer { name: "text".into(), line: Some(line.into()) }
    }
    pub fn key(key: &str) -> TerminalPromptAnswer {
        TerminalPromptAnswer { name: key.into(), line: None }
    }

    /// The bytes the terminal would get had the user typed the answer there: Enter is a CR; a line holding a CR or LF is cut at it.
    pub fn input(&self) -> String {
        match self.name.as_str() {
            "yes" => "y\r".into(),
            "no" => "n\r".into(),
            "text" => format!("{}\r", self.line.as_deref().unwrap_or("").split(['\r', '\n']).next().unwrap_or("")),
            "up" => "\u{1b}[A".into(),
            "down" => "\u{1b}[B".into(),
            "enter" => "\r".into(),
            "space" => " ".into(),
            _ => String::new(),
        }
    }

    /// The phone's (and the page's) `{promptId, answer, text?}`, read strictly: None for an unknown answer, a missing, empty or
    /// over-long text, or a malformed id.
    pub fn from_phone(prompt_id: Option<&str>, answer: Option<&str>, text: Option<&str>) -> Option<TerminalPromptAnswer> {
        let id = prompt_id.filter(|id| !id.is_empty())?;
        if utf16_len(id) > 200 || id.chars().any(char::is_control) {
            return None;
        }
        match answer? {
            "yes" => Some(TerminalPromptAnswer::yes()),
            "no" => Some(TerminalPromptAnswer::no()),
            "text" => text.filter(|t| !t.is_empty() && t.len() <= MAXIMUM_PHONE_TEXT).map(TerminalPromptAnswer::typed),
            key if KEYS.contains(&key) => Some(TerminalPromptAnswer::key(key)),
            _ => None,
        }
    }
}

/// What [`detect`] found at the end of a command's output.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Detected {
    pub text: String,
    pub options: Vec<String>,
    pub kind: &'static str,
    pub default_yes: Option<bool>,
}

static YES_NO: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)[(\[]\s*(y(?:es)?)\s*/\s*(n(?:o)?)\s*[)\]]").expect("yes/no"));
// Korean, Japanese and Chinese programs ask for a password in these words too (Swift TerminalPrompt.swift); matched against what a
// command prints, never shown.
static SECRET: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)(password|passphrase|passcode|\bpin\b|token|secret|비밀번호|암호|パスワード|密码)[^\n]*[:?：]\s*$").expect("secret")
});
const MARKERS: [&str; 7] = ["❯", "›", ">", "●", "◉", "◆", "→"];

/// C# `TerminalPromptDetector.Detect`: the prompt `tail` (a command's cleaned output) ends with, None when it does not look like one.
/// The last line is the one after the last newline, where the cursor waits.
pub fn detect(tail: &str) -> Option<Detected> {
    let lines: Vec<&str> = tail.split('\n').collect();
    let last = lines.last().copied().unwrap_or("").trim_end();
    if let Some(list) = choice(&lines) {
        return Some(list);
    }
    if last.is_empty() || utf16_len(last) > 400 || last.ends_with("...") || last.ends_with('…') {
        return None;
    }
    let text = shown(last);
    if let Some(found) = YES_NO.find(last) {
        let letters: Vec<char> = found.as_str().chars().filter(|c| c.is_alphabetic()).collect();
        let yes: Vec<char> = letters.iter().copied().take_while(|c| "yYeEsS".contains(*c)).collect();
        let no = &letters[yes.len()..];
        let (first_yes, first_no) = (yes.first().copied(), no.first().copied());
        let default_yes = match (first_yes, first_no) {
            (Some('Y'), Some('n')) => Some(true),
            (Some('y'), Some('N')) => Some(false),
            _ => None,
        };
        return Some(Detected { text, options: vec![], kind: kinds::YES_NO, default_yes });
    }
    if SECRET.is_match(last) {
        return Some(Detected { text, options: vec![], kind: kinds::SECRET, default_yes: None });
    }
    if last.chars().last().is_some_and(|c| "?:›>：？".contains(c)) {
        return Some(Detected { text, options: vec![], kind: kinds::TEXT, default_yes: None });
    }
    None
}

/// A list the arrow keys move through: a line marked with a pointer among the last lines, below the question (a line opening with
/// `?`).
fn choice(lines: &[&str]) -> Option<Detected> {
    let recent: Vec<&str> = lines[lines.len().saturating_sub(MAXIMUM_OPTIONS + 2)..].iter().map(|line| line.trim_end()).collect();
    let question = recent.iter().rposition(|line| line.trim().starts_with('?'))?;
    let options: Vec<&str> = recent[question + 1..].iter().copied().filter(|line| !line.trim().is_empty()).collect();
    if options.len() < 2 || !options.iter().any(|line| is_marked(line)) {
        return None;
    }
    Some(Detected {
        text: shown(recent[question]),
        options: options.iter().take(MAXIMUM_OPTIONS).map(|line| line.trim().to_string()).collect(),
        kind: kinds::CHOICE,
        default_yes: None,
    })
}

/// Whether a list line is the one the pointer is on.
pub fn is_marked(line: &str) -> bool {
    let start = line.trim();
    MARKERS.iter().any(|marker| start.starts_with(marker))
}

fn shown(line: &str) -> String {
    first_units(line.trim(), MAXIMUM_TEXT_LENGTH).to_string()
}

/// One page of a command's output read from a cursor (C# `PromptRead`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PromptRead {
    pub output: String,
    pub next_offset: usize,
    pub more_remains: bool,
}

/// What [`TerminalPromptMonitor`] needs of an agent pane's terminal (C# `IPromptTerminal`).
pub trait PromptTerminal {
    /// The commands still running, oldest first.
    fn running_handles(&self) -> Vec<String>;
    /// The command's own output from `offset` (a position the terminal gave), at most `max_chars` characters of it.
    fn read_output(&self, handle: &str, offset: usize, max_chars: usize) -> PromptRead;
}

/// The monitor's clock: time since some fixed start.
pub type Clock = Arc<dyn Fn() -> Duration + Send + Sync>;

struct Follow {
    offset: usize,
    /// The cleaned output's length so far, in UTF-16 units (the prompt id's second half).
    length: usize,
    cleaner: TerminalTextCleaner,
    tail: String,
    grew_at: Duration,
    answered_at_length: Option<usize>,
}

/// C# `TerminalPromptMonitor`: follows the commands running in one agent pane's terminal and says which of them waits for input.
/// It reads each command's output with its own cursor (the agent's reads are not touched), cleans it, keeps its last
/// [`TAIL_CHARACTERS`], and asks [`detect`] once the output has not grown for the quiet time. An answered prompt stays hidden until
/// the output grows.
pub struct TerminalPromptMonitor {
    quiet: Duration,
    now: Clock,
    follows: Mutex<HashMap<String, Follow>>,
}

/// `TerminalPromptMonitor.QuietSeconds`.
pub const QUIET: Duration = Duration::from_millis(800);
/// `TailCharacters`, in UTF-16 units.
pub const TAIL_CHARACTERS: usize = 4_096;
const READ_CHUNK: usize = 21_000;

impl Default for TerminalPromptMonitor {
    fn default() -> TerminalPromptMonitor {
        let start = Instant::now();
        TerminalPromptMonitor::new(QUIET, Arc::new(move || start.elapsed()))
    }
}

impl TerminalPromptMonitor {
    pub fn new(quiet: Duration, now: Clock) -> TerminalPromptMonitor {
        TerminalPromptMonitor { quiet, now, follows: Mutex::new(HashMap::new()) }
    }

    /// Reads what the running commands printed since the last poll; the prompt the newest waiting one shows, None when none waits.
    pub fn poll(&self, terminal: &dyn PromptTerminal) -> Option<TerminalPrompt> {
        let mut follows = self.follows.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let running = terminal.running_handles();
        follows.retain(|handle, _| running.contains(handle));
        let mut found = None;
        for handle in &running {
            let now = (self.now)();
            let follow = follows.entry(handle.clone()).or_insert_with(|| Follow {
                offset: 0,
                length: 0,
                cleaner: TerminalTextCleaner::new(),
                tail: String::new(),
                grew_at: now,
                answered_at_length: None,
            });
            loop {
                let read = terminal.read_output(handle, follow.offset, READ_CHUNK);
                let more = read.more_remains && read.next_offset > follow.offset;
                follow.offset = read.next_offset;
                let cleaned = follow.cleaner.clean(&read.output);
                if !cleaned.is_empty() {
                    let tail = format!("{}{cleaned}", follow.tail);
                    follow.tail = last_units(&tail, TAIL_CHARACTERS).to_string();
                    follow.length += utf16_len(&cleaned);
                    follow.grew_at = (self.now)();
                }
                if !more {
                    break;
                }
            }
            let quiet = (self.now)().saturating_sub(follow.grew_at) >= self.quiet;
            if !quiet || follow.answered_at_length.is_some_and(|at| follow.length <= at) {
                continue;
            }
            let Some(detected) = detect(&follow.tail) else { continue };
            found = Some(TerminalPrompt {
                id: format!("{handle}:{}", follow.length),
                handle: handle.clone(),
                text: detected.text,
                options: detected.options,
                kind: detected.kind,
                default_yes: detected.default_yes,
            });
        }
        found
    }

    /// The user answered `prompt`: it is not shown again until its command prints more.
    pub fn answered(&self, prompt: &TerminalPrompt) {
        let mut follows = self.follows.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some(follow) = follows.get_mut(&prompt.handle) {
            follow.answered_at_length = Some(follow.length);
        }
    }
}
