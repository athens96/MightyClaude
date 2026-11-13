//! A shell code block from an agent's reply that the user ran (C# ShellBlockRuns.cs, ShellBlockScan.cs; spec: run-shell-blocks;
//! the page's half is `src/conversation/shellBlocks.ts` and `ShellBlock.tsx`), saved with its pane as `shellRuns`
//! (mighty-core's `ShellBlockRun`, normalized by `normalize::shell_block_runs`). A record's key is `<entryId>#<n>`, n the block's
//! place among the runnable blocks of that entry; its state one of [`STATES`]; its output the run's cleaned output, its last 16 KB.
//!
//! The host runs a block only when the page's command is the one [`runnable`] finds at the key's place ([`shown`]): the scan reads
//! the reply as the page's markdown parser does, so a difference between the two can only refuse a run, never widen one.

use std::sync::LazyLock;

use mighty_core::model::{LogEntry, RunSession, ShellBlockRun};
use mighty_core::normalize::shell_tail;
use mighty_core::{time, wire};
use regex::Regex;

use crate::cleaner::TerminalTextCleaner;

/// The output kept per run, in UTF-8 bytes (its end).
pub const OUTPUT_LIMIT_BYTES: usize = 16_384;
/// The records kept per pane, the newest.
pub const MAXIMUM_RUNS: usize = 64;
/// What an agent terminal's command may be (`AgentTerminal.Run`).
pub const MAXIMUM_COMMAND_BYTES: usize = 65_536;
pub const STATES: [&str; 5] = ["running", "completed", "failed", "stopped", "interrupted"];

/// The entry id of a key `<entryId>#<n>` (n of one to four ASCII digits), or None when it is not one.
pub fn entry_id(key: Option<&str>) -> Option<&str> {
    let key = key?;
    let at = key.rfind('#').filter(|at| *at > 0)?;
    let (entry, index) = (&key[..at], &key[at + 1..]);
    (wire::identifier(entry) && (1..=4).contains(&index.len()) && index.bytes().all(|b| b.is_ascii_digit())).then_some(entry)
}

/// Whether `key` is `<entryId>#<n>`.
pub fn is_key(key: Option<&str>) -> bool {
    entry_id(key).is_some()
}

/// A command the pane's terminal takes: not blank, no NUL, within [`MAXIMUM_COMMAND_BYTES`].
pub fn is_command(command: Option<&str>) -> bool {
    command.is_some_and(|c| !c.trim().is_empty() && !c.contains('\0') && c.len() <= MAXIMUM_COMMAND_BYTES)
}

/// A new record, running (`ShellBlockRunSupport.Started`); `now` is `Wire.Now()` unless given.
pub fn started(key: &str, command: &str, now: Option<&str>) -> ShellBlockRun {
    ShellBlockRun {
        key: Some(key.into()),
        command: Some(command.into()),
        state: Some("running".into()),
        exit_code: None,
        output: Some(String::new()),
        started_at: Some(now.map_or_else(time::now_o, str::to_string)),
        ended_at: None,
    }
}

/// A running record's end: stopped when the user stopped it, else completed for exit code 0 and failed for any other (or none). A
/// record that already ended is given back as it is.
pub fn finished(run: &ShellBlockRun, exit_code: Option<i32>, stopped: bool, now: Option<&str>) -> ShellBlockRun {
    if run.state.as_deref() != Some("running") {
        return run.clone();
    }
    let state = if stopped {
        "stopped"
    } else if exit_code == Some(0) {
        "completed"
    } else {
        "failed"
    };
    ShellBlockRun { state: Some(state.into()), exit_code, ended_at: Some(now.map_or_else(time::now_o, str::to_string)), ..run.clone() }
}

/// The records with `run` in the place of the one with its key (or after the rest), the newest [`MAXIMUM_RUNS`].
pub fn replace(runs: Option<&[ShellBlockRun]>, run: ShellBlockRun) -> Vec<ShellBlockRun> {
    let mut next = runs.unwrap_or_default().to_vec();
    match next.iter().position(|r| r.key == run.key) {
        Some(at) => next[at] = run,
        None => next.push(run),
    }
    if next.len() > MAXIMUM_RUNS {
        next.drain(..next.len() - MAXIMUM_RUNS);
    }
    next
}

/// The end of `text` that fits [`OUTPUT_LIMIT_BYTES`] in UTF-8 (`ShellBlockRunSupport.Tail`).
pub fn tail(text: &str) -> String {
    shell_tail(text)
}

/// C# `DesktopService.UpdateShellBlockRun`'s change of one pane: `update` gets the pane's record of `key` (None when it has none)
/// and gives back the one to keep, or the same record (or None) to change nothing. Only an agent pane keeps records. The record's
/// output is held to its limit; the pane's next record and whether to save at once (an ended record) or on the schedule (a
/// running one), or None for no change.
pub fn update_pane(
    pane: &RunSession,
    key: &str,
    update: impl FnOnce(Option<&ShellBlockRun>) -> Option<ShellBlockRun>,
) -> Option<(RunSession, bool)> {
    if pane.kind() != "claude" {
        return None;
    }
    let current = pane.shell_block_runs.as_deref().and_then(|runs| runs.iter().find(|r| r.key.as_deref() == Some(key)));
    let next = update(current)?;
    if Some(&next) == current || next.key.as_deref() != Some(key) {
        return None;
    }
    let next = ShellBlockRun { output: Some(tail(next.output.as_deref().unwrap_or(""))), ..next };
    let ended = next.state.as_deref() != Some("running");
    let mut changed = pane.clone();
    changed.shell_block_runs = Some(replace(pane.shell_block_runs.as_deref(), next));
    Some((changed, ended))
}

/// The assistant entry `entry_id` in the pane's transcript or its Mighty runs, or None.
pub fn assistant_entry<'a>(pane: &'a RunSession, entry_id: &str) -> Option<&'a LogEntry> {
    fn find<'a>(entries: &'a [LogEntry], id: &str) -> Option<&'a LogEntry> {
        entries.iter().rev().find(|e| e.id() == id && e.kind() == "assistant" && e.activity.is_none())
    }
    find(pane.logs(), entry_id).or_else(|| {
        pane.graph_runs().iter().find_map(|run| {
            find(run.root_entries(), entry_id)
                .or_else(|| find(run.result_entries(), entry_id))
                .or_else(|| run.agents().iter().find_map(|agent| find(agent.entries.as_deref().unwrap_or(&[]), entry_id)))
        })
    })
}

/// Whether `command` is exactly the command of the runnable block at the key's place in that assistant entry, as the page finds it
/// ([`runnable`]: a closed fence, a console block's prompt lines without their prompt, nothing that hides or reorders text). The page
/// draws the button; this keeps the host from running anything else.
pub fn shown(pane: &RunSession, key: &str, command: &str) -> bool {
    let Some(id) = entry_id(Some(key)) else { return false };
    let Some(entry) = assistant_entry(pane, id) else { return false };
    let Ok(index) = key[key.rfind('#').map_or(0, |at| at + 1)..].parse::<usize>() else { return false };
    runnable(entry.text()).get(index).is_some_and(|block| block == command)
}

// ── ShellBlockScan ──────────────────────────────────────────────────────────────────────────────────────────────────────────

const MAXIMUM_DEPTH: usize = 8;
const MAXIMUM_RENDER_BYTES: usize = 131_072;
const SHELLS: [&str; 8] = ["sh", "bash", "zsh", "shell", "console", "ps1", "powershell", "pwsh"];
const PROMPTS: [&str; 3] = ["$ ", "PS> ", "> "];

fn regex(pattern: &str) -> Regex {
    Regex::new(pattern).expect("a shell block pattern")
}

static FENCE: LazyLock<Regex> = LazyLock::new(|| regex(r"^(\s{0,3})(`{3,}|~{3,})\s*([^`]*)$"));
static HEADING: LazyLock<Regex> = LazyLock::new(|| regex(r"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$"));
static THEMATIC_BREAK: LazyLock<Regex> = LazyLock::new(|| regex(r"^\s{0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$"));
static LIST_ITEM: LazyLock<Regex> = LazyLock::new(|| regex(r"^(\s*)([-*+]|\d{1,9}[.)])(?:\s+(.*))?$"));
static TABLE_DELIMITER: LazyLock<Regex> = LazyLock::new(|| regex(r"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$"));

/// A character that could show one thing and run another (shellBlocks.ts `unsafeCharacters`): bidi controls, zero-width and
/// format characters, the C0 controls but tab and newline, DEL.
fn deceptive(c: char) -> bool {
    matches!(c,
        '\u{202A}'..='\u{202E}' | '\u{2066}'..='\u{2069}' | '\u{200B}'..='\u{200F}' | '\u{FEFF}' | '\u{2060}'
        | '\u{0}'..='\u{8}' | '\u{B}'..='\u{1F}' | '\u{7F}')
}

/// Whether `text` holds a character that hides or reorders what is shown.
pub fn is_deceptive(text: &str) -> bool {
    text.chars().any(deceptive)
}

/// shellBlocks.ts `shellCommand` on Windows (`ShellBlockScan.Command`): the command a block of `language` runs, or None when it
/// gets no button.
pub fn command(language: Option<&str>, text: &str) -> Option<String> {
    let name = language?.split_whitespace().next()?.to_lowercase();
    if !SHELLS.contains(&name.as_str()) {
        return None;
    }
    // Checked on the block as written, before a lone CR becomes a line break.
    if is_deceptive(&text.replace("\r\n", "\n")) {
        return None;
    }
    let lines = text.replace("\r\n", "\n").replace('\r', "\n");
    let body = lines.trim_end_matches('\n');
    let command = if name != "console" {
        body.to_string()
    } else {
        body.split('\n')
            .filter_map(|line| PROMPTS.iter().find(|mark| line.starts_with(*mark)).map(|mark| &line[mark.len()..]))
            .collect::<Vec<_>>()
            .join("\n")
    };
    (!command.trim().is_empty()).then_some(command)
}

/// shellBlocks.ts `runnableBlocks` (`ShellBlockScan.Runnable`): the commands of the reply's closed runnable blocks, by their place.
pub fn runnable(text: &str) -> Vec<String> {
    let mut found = vec![];
    if text.len() > MAXIMUM_RENDER_BYTES {
        return found;
    }
    let normalized = text.replace("\r\n", "\n").replace('\r', "\n");
    let lines: Vec<String> = normalized.split('\n').map(str::to_string).collect();
    blocks(&lines, 0, &mut found);
    found
}

fn blank(line: &str) -> bool {
    line.trim().is_empty()
}

fn indent_of(line: &str) -> usize {
    let mut width = 0;
    for c in line.chars() {
        match c {
            ' ' => width += 1,
            '\t' => width += 4,
            _ => break,
        }
    }
    width
}

fn unindent(line: &str, width: usize) -> String {
    let mut width = width as isize;
    let mut at = 0;
    for c in line.chars() {
        if width <= 0 || !matches!(c, ' ' | '\t') {
            break;
        }
        width -= if c == '\t' { 4 } else { 1 };
        at += c.len_utf8();
    }
    line[at..].to_string()
}

/// Whether a list item's text group matched (`item.Groups[3].Success`).
fn item_has_text(line: &str) -> bool {
    LIST_ITEM.captures(line).is_some_and(|item| item.get(3).is_some())
}

fn interrupts(line: &str) -> bool {
    FENCE.is_match(line)
        || HEADING.is_match(line)
        || THEMATIC_BREAK.is_match(line)
        || line.trim_start().starts_with('>')
        || item_has_text(line)
}

/// The cells of a table row: `|` splits them unless a backslash is before it (`(?<!\\)\|`).
fn cell_count(row: &str) -> usize {
    let mut text = row.trim();
    if let Some(rest) = text.strip_prefix('|') {
        text = rest;
    }
    if text.ends_with('|') && !text.ends_with("\\|") {
        text = &text[..text.len() - 1];
    }
    let mut count = 1;
    let mut previous = None;
    for c in text.chars() {
        if c == '|' && previous != Some('\\') {
            count += 1;
        }
        previous = Some(c);
    }
    count
}

fn table_starts(lines: &[String], i: usize) -> bool {
    i + 1 < lines.len()
        && lines[i].contains('|')
        && lines[i + 1].contains('-')
        && TABLE_DELIMITER.is_match(&lines[i + 1])
        && cell_count(&lines[i]) == cell_count(&lines[i + 1])
}

fn blocks(lines: &[String], depth: usize, found: &mut Vec<String>) {
    let mut i = 0;
    while i < lines.len() {
        let line = &lines[i];
        if blank(line) {
            i += 1;
            continue;
        }
        if let Some(opened) = FENCE.captures(line) {
            let margin = opened.get(1).map_or(0, |m| m.as_str().chars().count());
            let mark = opened.get(2).map_or("", |m| m.as_str()).to_string();
            let fence_char = mark.chars().next().unwrap_or('`').to_string();
            let info = opened.get(3).map_or("", |m| m.as_str()).trim().to_string();
            let mut code = vec![];
            i += 1;
            while i < lines.len() && !(lines[i].trim_start().starts_with(&mark) && lines[i].trim().replace(&fence_char, "").is_empty()) {
                code.push(unindent(&lines[i], margin));
                i += 1;
            }
            // A block still streaming (no closing fence) has no button.
            if i >= lines.len() {
                continue;
            }
            i += 1;
            if let Some(command) = command((!info.is_empty()).then_some(info.as_str()), &code.join("\n")) {
                found.push(command);
            }
            continue;
        }
        if HEADING.is_match(line) || THEMATIC_BREAK.is_match(line) {
            i += 1;
            continue;
        }
        if depth < MAXIMUM_DEPTH && line.trim_start().starts_with('>') {
            let mut inner = vec![];
            while i < lines.len() && lines[i].trim_start().starts_with('>') {
                let rest = &lines[i].trim_start()[1..];
                inner.push(rest.strip_prefix(' ').unwrap_or(rest).to_string());
                i += 1;
            }
            blocks(&inner, depth + 1, found);
            continue;
        }
        if depth < MAXIMUM_DEPTH && LIST_ITEM.is_match(line) {
            i = list(lines, i, depth, found);
            continue;
        }
        if table_starts(lines, i) {
            i += 2;
            while i < lines.len() && !blank(&lines[i]) && lines[i].contains('|') {
                i += 1;
            }
            continue;
        }
        // A paragraph: up to a blank line, or a line that starts another block (markdown.ts: a line of backslashes alone adds no
        // words).
        let mut words = false;
        while i < lines.len() && !blank(&lines[i]) && (!words || !interrupts(&lines[i]) && !table_starts(lines, i)) {
            words |= !lines[i].trim().trim_end_matches('\\').is_empty();
            i += 1;
        }
    }
}

/// The list marker's text (`item.Groups[2]`) and its first character being an ASCII digit (an ordered list).
fn marker(line: &str) -> Option<(usize, bool, Option<String>)> {
    let item = LIST_ITEM.captures(line)?;
    let mark = item.get(2)?.as_str();
    Some((wire::utf16_len(mark), mark.chars().next().is_some_and(|c| c.is_ascii_digit()), item.get(3).map(|m| m.as_str().to_string())))
}

fn list(lines: &[String], start: usize, depth: usize, found: &mut Vec<String>) -> usize {
    let mut i = start;
    let Some((_, ordered, _)) = marker(&lines[i]) else { return i + 1 };
    let margin = indent_of(&lines[i]);
    let sibling = |line: &str| {
        let indent = indent_of(line);
        marker(line).is_some_and(|(_, digit, _)| indent >= margin && indent <= margin + 1 && digit == ordered)
    };
    while i < lines.len() && sibling(&lines[i]) {
        let Some((width, _, text)) = marker(&lines[i]) else { break };
        let inner = margin + width + 1;
        let mut content = vec![text.unwrap_or_default()];
        i += 1;
        while i < lines.len() {
            let line = &lines[i];
            if blank(line) {
                let mut next = i + 1;
                while next < lines.len() && blank(&lines[next]) {
                    next += 1;
                }
                if next >= lines.len() || indent_of(&lines[next]) < inner {
                    break;
                }
                while i < next {
                    content.push(String::new());
                    i += 1;
                }
                continue;
            }
            if indent_of(line) >= inner {
                content.push(unindent(line, inner));
                i += 1;
                continue;
            }
            if LIST_ITEM.is_match(line) || interrupts(line) {
                break;
            }
            content.push(line.trim().to_string());
            i += 1;
        }
        blocks(&content, depth + 1, found);
        let mut after = i;
        while after < lines.len() && blank(&lines[after]) {
            after += 1;
        }
        if after < lines.len() && sibling(&lines[after]) {
            i = after;
        }
    }
    i
}

/// One run's output as the block shows it (C# `ShellBlockOutput`): escape sequences removed as the agent's tool output has them
/// removed ([`TerminalTextCleaner`]), a carriage return's redraw resolved (the line starts again), the last 16 KB. The caller holds
/// its own lock.
#[derive(Debug, Clone)]
pub struct ShellBlockOutput {
    cleaner: TerminalTextCleaner,
    text: String,
    carriage: bool,
}

impl Default for ShellBlockOutput {
    fn default() -> ShellBlockOutput {
        ShellBlockOutput { cleaner: TerminalTextCleaner::keeping_carriage_returns(), text: String::new(), carriage: false }
    }
}

impl ShellBlockOutput {
    pub fn append(&mut self, raw: &str) {
        for c in self.cleaner.clean(raw).chars() {
            if self.carriage {
                self.carriage = false;
                if c == '\n' {
                    self.text.push('\n');
                    continue;
                }
                let line = self.text.rfind('\n').map_or(0, |at| at + 1);
                self.text.truncate(line);
            }
            if c == '\r' {
                self.carriage = true;
            } else {
                self.text.push(c);
            }
        }
        // Keeps a few limits' worth, so the tail is cut once in a while rather than on every chunk.
        if self.text.len() > OUTPUT_LIMIT_BYTES * 4 {
            self.text = tail(&self.text);
        }
    }

    pub fn text(&self) -> String {
        tail(&self.text)
    }
}
