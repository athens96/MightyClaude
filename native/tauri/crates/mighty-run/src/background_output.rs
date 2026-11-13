//! C# `BackgroundOutputTail` (BackgroundOutputTail.cs): the lines a host shows for a background run, the end of its output file
//! (native/contracts/fixtures/background-output.json).

use std::io::{Read, Seek, SeekFrom};
use std::sync::OnceLock;

use regex::Regex;

pub const MAX_BYTES: usize = 8192;
pub const MAX_LINES: usize = 6;
pub const MAX_LINE_CHARS: usize = 200;

/// CSI sequences, OSC strings (ended by BEL or ST), then the other two-byte escapes.
fn ansi() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new(r"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]").expect("pattern"))
}

/// The first `count` Unicode scalars (`ExecutionGraphSupport.Prefix`).
pub fn prefix(value: &str, count: usize) -> String {
    value.chars().take(count).collect()
}

/// `BackgroundOutputTail.Lines`: the last six lines of `data` (invalid UTF-8 as U+FFFD, escapes removed, a line's last
/// carriage-return segment, each cut to 200 scalars and "…"); `truncated` drops the first line, which the read started inside.
pub fn lines(data: &[u8], truncated: bool) -> Vec<String> {
    let text = String::from_utf8_lossy(data);
    let clean = ansi().replace_all(&text, "");
    let mut rows: Vec<String> = clean
        .split('\n')
        .map(|row| {
            let line = row.strip_suffix('\r').unwrap_or(row);
            line.split('\r').next_back().unwrap_or("").to_string()
        })
        .collect();
    if truncated && !rows.is_empty() {
        rows.remove(0);
    }
    while rows.last().is_some_and(String::is_empty) {
        rows.pop();
    }
    let skip = rows.len().saturating_sub(MAX_LINES);
    rows.into_iter()
        .skip(skip)
        .map(|row| if row.chars().count() > MAX_LINE_CHARS { format!("{}…", prefix(&row, MAX_LINE_CHARS)) } else { row })
        .collect()
}

/// `BackgroundOutputTail.Read`: the file's last lines (at most 8 KiB of its end, even while it grows), or None when it cannot be
/// read (missing, or on another computer).
pub fn read(path: &str) -> Option<Vec<String>> {
    let mut file = std::fs::File::open(path).ok()?;
    let length = file.metadata().ok()?.len();
    let start = length.saturating_sub(MAX_BYTES as u64);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut buffer = vec![0u8; MAX_BYTES];
    let mut count = 0;
    while count < MAX_BYTES {
        match file.read(&mut buffer[count..]) {
            Ok(0) => break,
            Ok(read) => count += read,
            Err(_) => return None,
        }
    }
    Some(lines(&buffer[..count], start > 0))
}
