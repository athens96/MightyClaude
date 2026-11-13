//! What one Edit, Write or MultiEdit result says about the file before it (C# `FileEdit`, `FilePatch`, FilePatch.cs; the parser's
//! `EditedOriginal`, OutputParser.cs:348-368; M/MightyCore/FilePatch.swift): Claude Code's `structuredPatch` read, and undone on the
//! file as it is just after the edit (native/contracts/fixtures/file-diff.json `reversePatch`).

use serde_json::Value;

/// C# `FilePatch.MaximumHunks`.
pub const MAXIMUM_HUNKS: usize = 10_000;
const BYTE_ORDER_MARK: char = '\u{FEFF}';

/// One hunk of Claude Code's `structuredPatch` (C# `FilePatchHunk`): lines are `" ctx"`, `"-old"`, `"+new"` (and `"\ No newline…"`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PatchHunk {
    pub old_start: usize,
    pub old_lines: usize,
    pub new_start: usize,
    pub new_lines: usize,
    pub lines: Vec<String>,
}

/// C# `FileEdit`: the original text when the CLI sent it, `created` when the tool made the file, and the patch it applied. No
/// original, not created and no patch: the original is unknown.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct FileEdit {
    pub original: Option<String>,
    pub created: bool,
    pub patch: Option<Vec<PatchHunk>>,
}

impl FileEdit {
    pub fn unknown() -> FileEdit {
        FileEdit::default()
    }

    /// The convenience form (C# `Capture(…, string? original)`): the text, or None for a created file.
    pub fn text(original: Option<&str>) -> FileEdit {
        match original {
            Some(text) => FileEdit { original: Some(text.into()), created: false, patch: None },
            None => FileEdit { original: None, created: true, patch: None },
        }
    }

    /// C# `OutputParser.EditedOriginal`'s reading of a `tool_use_result` (`toolUseResult` in transcripts) that answered `tool` (Edit,
    /// Write or MultiEdit, not failed): `originalFile` as text is the original (its `structuredPatch` travels with it, to follow the
    /// run's later edits). Without it a Write of `type: "create"`, or an Edit with an empty `oldString` whose patch is there and only
    /// adds, made the file; an Edit's or a Write update's `structuredPatch` is undone against the file on disk; anything else is
    /// unknown, which still settles the path for the run.
    pub fn from_result(tool: &str, result: &Value) -> FileEdit {
        let text = |key: &str| result.get(key).and_then(Value::as_str);
        let patch = result.get("structuredPatch").and_then(parse);
        if let Some(original) = text("originalFile") {
            return FileEdit { original: Some(original.into()), created: false, patch };
        }
        if tool == "Write" && text("type") == Some("create") {
            return FileEdit { original: None, created: true, patch: None };
        }
        if tool == "Write" && text("type") != Some("update") {
            return FileEdit::unknown();
        }
        if tool != "Write" && text("oldString") == Some("") {
            let adds_only = patch.as_ref().is_some_and(|hunks| {
                !hunks.is_empty() && hunks.iter().all(|h| h.lines.iter().all(|l| l.starts_with('+') || l.starts_with('\\')))
            });
            return if adds_only { FileEdit { original: None, created: true, patch: None } } else { FileEdit::unknown() };
        }
        match patch {
            Some(patch) => FileEdit { original: None, created: false, patch: Some(patch) },
            None => FileEdit::unknown(),
        }
    }
}

/// C# `FilePatch.Parse`: the hunks of a `structuredPatch` array; None when it is not one.
pub fn parse(value: &Value) -> Option<Vec<PatchHunk>> {
    let items = value.as_array()?;
    if items.len() > MAXIMUM_HUNKS {
        return None;
    }
    // TryGetInt32 of a non-negative integer.
    let number =
        |item: &Value, key: &str| item.get(key).and_then(Value::as_i64).filter(|n| (0..=i32::MAX as i64).contains(n)).map(|n| n as usize);
    items
        .iter()
        .map(|item| {
            item.as_object()?;
            Some(PatchHunk {
                old_start: number(item, "oldStart")?,
                old_lines: number(item, "oldLines")?,
                new_start: number(item, "newStart")?,
                new_lines: number(item, "newLines")?,
                lines: item.get("lines")?.as_array()?.iter().map(|l| l.as_str().map(str::to_string)).collect::<Option<Vec<_>>>()?,
            })
        })
        .collect()
}

/// C# `WithoutByteOrderMark`: a text without the byte order mark it may start with (the files are read without one).
pub fn without_byte_order_mark(text: &str) -> &str {
    text.strip_prefix(BYTE_ORDER_MARK).unwrap_or(text)
}

/// C# `Inverted`: the same hunks the other way round: undoing them on the file before the patch gives the file after it.
pub fn inverted(hunks: &[PatchHunk]) -> Vec<PatchHunk> {
    hunks
        .iter()
        .map(|h| PatchHunk {
            old_start: h.new_start,
            old_lines: h.new_lines,
            new_start: h.old_start,
            new_lines: h.old_lines,
            lines: h
                .lines
                .iter()
                .map(|l| match l.as_bytes().first() {
                    Some(b'+') => format!("-{}", &l[1..]),
                    Some(b'-') => format!("+{}", &l[1..]),
                    _ => l.clone(),
                })
                .collect(),
        })
        .collect()
}

/// A `\` line right after a line of a side (not first, not after another marker).
fn marker(lines: &[String], index: usize) -> bool {
    index > 0 && lines[index].starts_with('\\') && matches!(lines[index - 1].as_bytes().first(), Some(b' ' | b'+' | b'-'))
}

/// A patch line's text, a trailing CR aside; line 1's without the byte order mark the file is read past.
fn text_of(line: &str, number: usize) -> &str {
    let text = line[1..].strip_suffix('\r').unwrap_or(&line[1..]);
    if number == 1 { without_byte_order_mark(text) } else { text }
}

/// C# `FilePatch.ReverseApply`: the file before the patch, from `current` (the file after it). Each hunk's new side (its context and
/// added lines) must stand exactly at its `newStart`, in order, a trailing CR aside, and its old side must then start at its `oldStart`
/// in the result. A removed line comes back with the line break of the hunk's nearest context line (before it on a tie; with none,
/// the file's: CRLF when it has one). A `\` line after a removed line restores it without a break; after a context or added line,
/// that line on disk must have none. A byte order mark before line 1 is read past, on disk and in the patch, and kept. A count that
/// disagrees with the lines, a line without a break before the end, or any mismatch: None.
pub fn reverse_apply(current: &str, hunks: &[PatchHunk]) -> Option<String> {
    let mark = current.starts_with(BYTE_ORDER_MARK);
    let current = if mark { &current[BYTE_ORDER_MARK.len_utf8()..] } else { current };
    let mut lines: Vec<(&str, &str)> = Vec::new();
    let mut rest = current;
    while !rest.is_empty() {
        match rest.find('\n') {
            None => {
                lines.push((rest, ""));
                break;
            }
            Some(end) => {
                let crlf = end > 0 && rest.as_bytes()[end - 1] == b'\r';
                lines.push((&rest[..if crlf { end - 1 } else { end }], if crlf { "\r\n" } else { "\n" }));
                rest = &rest[end + 1..];
            }
        }
    }
    let ending = if current.contains("\r\n") { "\r\n" } else { "\n" };
    let mut out = Output { text: String::with_capacity(current.len() + 1), written: 0, closed: false };
    if mark {
        out.text.push(BYTE_ORDER_MARK);
    }
    let mut cursor = 0usize;
    for hunk in hunks {
        let count = hunk.lines.len();
        let (mut new_side, mut old_side) = (0usize, 0usize);
        for i in 0..count {
            if marker(&hunk.lines, i) {
                continue;
            }
            let op = *hunk.lines[i].as_bytes().first()?;
            if !matches!(op, b' ' | b'+' | b'-') {
                return None;
            }
            if op != b'-' {
                new_side += 1;
            }
            if op != b'+' {
                old_side += 1;
            }
        }
        if new_side != hunk.new_lines || old_side != hunk.old_lines {
            return None;
        }
        // A hunk with no new lines has no context either: the file was emptied, so it stands at the end of what is left.
        let at = if hunk.new_lines == 0 { hunk.new_start.min(lines.len()) } else { hunk.new_start.checked_sub(1)? };
        if at < cursor || at + hunk.new_lines > lines.len() {
            return None;
        }
        for line in &lines[cursor..at] {
            if !out.emit(line.0, line.1) {
                return None;
            }
        }
        // The old side starts at oldStart (with no old lines: after line oldStart, or at the top written as 0 or 1).
        let written = out.written;
        let start_ok = if hunk.old_lines > 0 {
            Some(written) == hunk.old_start.checked_sub(1)
        } else {
            written == hunk.old_start || Some(written) == hunk.old_start.checked_sub(1)
        };
        if !start_ok {
            return None;
        }
        // Where each new-side line stands on disk, and the nearest context line (with a break) before and after each line.
        let mut disk = vec![usize::MAX; count];
        let mut before = vec![usize::MAX; count];
        let mut after = vec![usize::MAX; count];
        let (mut position, mut seen) = (at, usize::MAX);
        for i in 0..count {
            let op = hunk.lines[i].as_bytes()[0];
            if matches!(op, b' ' | b'+') {
                disk[i] = position;
                position += 1;
            }
            before[i] = seen;
            if op == b' ' && !lines[disk[i]].1.is_empty() {
                seen = i;
            }
        }
        let mut seen = usize::MAX;
        for i in (0..count).rev() {
            after[i] = seen;
            if hunk.lines[i].as_bytes()[0] == b' ' && !lines[disk[i]].1.is_empty() {
                seen = i;
            }
        }
        let mut old_number = hunk.old_start;
        for i in 0..count {
            let line = &hunk.lines[i];
            let op = line.as_bytes()[0];
            if op == b'\\' {
                continue;
            }
            let bare = i + 1 < count && marker(&hunk.lines, i + 1);
            if op == b'-' {
                let near =
                    if before[i] != usize::MAX && (after[i] == usize::MAX || i - before[i] <= after[i] - i) { before[i] } else { after[i] };
                let end = if bare {
                    ""
                } else if near != usize::MAX {
                    lines[disk[near]].1
                } else {
                    ending
                };
                if !out.emit(text_of(line, old_number), end) {
                    return None;
                }
                old_number += 1;
                continue;
            }
            let (body, end) = lines[disk[i]];
            if body != text_of(line, disk[i] + 1) || bare && !end.is_empty() {
                return None;
            }
            if op == b' ' {
                if !out.emit(body, end) {
                    return None;
                }
                old_number += 1;
            }
        }
        cursor = at + hunk.new_lines;
    }
    for line in &lines[cursor..] {
        if !out.emit(line.0, line.1) {
            return None;
        }
    }
    Some(out.text)
}

/// The file being rebuilt: its text, the lines written, and whether one went without a break (only a file's last line may).
struct Output {
    text: String,
    written: usize,
    closed: bool,
}

impl Output {
    fn emit(&mut self, body: &str, end: &str) -> bool {
        if self.closed {
            return false;
        }
        self.text.push_str(body);
        self.text.push_str(end);
        self.written += 1;
        self.closed = end.is_empty();
        true
    }
}
