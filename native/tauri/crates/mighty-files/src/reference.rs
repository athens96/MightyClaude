//! The files a pane names, as transcript clicks and the final-result file list resolve them (C# `ReferencePreview`,
//! ReferencePreview.cs): one policy, so a path never leaves the workspace, a final link never escapes, and a web address's suffix is
//! never a local file. Resolution is `ResultFiles.LocalPath` and `ResultFiles.Resolve` (mighty-graph's `result_files`).

use std::path::Path;
use std::sync::LazyLock;

use mighty_core::workspace_files;
use mighty_graph::result_files::{self, ResultFileLookup, StdFiles};
use regex::Regex;

pub use mighty_core::normalize::bubble_size;

/// C# `ReferencePreview.BubbleDefaultWidth`, `BubbleMinimumWidth`, `BubbleMinimumHeight`.
pub const BUBBLE_DEFAULT_WIDTH: f64 = 420.0;
pub const BUBBLE_MINIMUM_WIDTH: f64 = 300.0;
pub const BUBBLE_MINIMUM_HEIGHT: f64 = 200.0;

/// C# `ReferencePreview.Target`: the file relative to the real root ('/' separated) and the line asked for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Target {
    pub relative_path: String,
    pub line: Option<i32>,
}

/// `int.TryParse` of a line number (white space and a sign allowed, as .NET's default number style takes them).
fn line_number(text: &str) -> Option<i32> {
    text.trim().parse::<i32>().ok()
}

/// C# `ReferencePreview.Resolve`: a path (or `file:` destination) a pane named, with a `:line` suffix read when no line was given,
/// resolved to an existing regular file inside `root`; None otherwise.
pub fn resolve(path: &str, root: &str, line: Option<i32>) -> Option<Target> {
    let mut local = result_files::local_path(path)?;
    let mut line = line;
    if line.is_none()
        && let Some(colon) = local.rfind(':')
        && local[..colon].encode_utf16().count() > 1
        && let Some(parsed) = line_number(&local[colon + 1..]).filter(|n| *n > 0)
    {
        line = Some(parsed);
        local.truncate(colon);
    }
    let files = StdFiles { ignores_case: std::path::MAIN_SEPARATOR == '\\' };
    let resolved = files.resolve(&local, root)?;
    let real_root = workspace_files::real_path(Path::new(root))?;
    let relative = workspace_files::relative(Path::new(&resolved), &real_root)?.replace('\\', "/");
    Some(Target { relative_path: relative, line: line.filter(|l| *l > 0) })
}

static REFERENCE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^((?:[A-Za-z]:[\\/]|\.{1,2}[\\/]|/)?(?:[\p{L}\p{N}_.@-]+[\\/])*[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,12})(?::(\d{1,6}))?")
        .expect("regex")
});
static PARENTHESIZED: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^[^()<>\r\n]+\.[A-Za-z0-9]{1,12}(?::\d{1,6})?$").expect("regex"));

/// `(?<![\p{L}\p{N}_])`: no letter, digit or underscore right before.
fn may_start(line: &str, at: usize) -> bool {
    !line[..at].chars().next_back().is_some_and(|c| c.is_alphanumeric() || c == '_')
}

/// C# `ReferencePreview.TextTargetAt`: the file-like text under a click at `position` (a UTF-16 offset into `text`); resolution still
/// checks the workspace boundary. Only the clicked line is looked at.
pub fn text_target_at(text: &str, position: usize) -> Option<(String, Option<i32>)> {
    let units: Vec<u16> = text.encode_utf16().collect();
    if position >= units.len() {
        return None;
    }
    let is_break = |u: &u16| *u == b'\r' as u16 || *u == b'\n' as u16;
    let start = units[..=position].iter().rposition(is_break).map_or(0, |at| at + 1);
    let end = units[position..].iter().position(is_break).map_or(units.len(), |at| at + position);
    if end - start > 16_384 {
        return None;
    }
    let line = String::from_utf16_lossy(&units[start..end]);
    // The click as a byte offset into the line.
    let offset_units = position - start;
    let mut click = line.len();
    let mut counted = 0;
    for (at, c) in line.char_indices() {
        if counted + c.len_utf16() > offset_units {
            click = at;
            break;
        }
        counted += c.len_utf16();
    }
    // Markdown destinations are shown in parentheses by the transcript renderer; keep spaces in those names instead of treating
    // only the last word as a path.
    let clicked = line[click..].chars().next().map_or(0, char::len_utf8);
    let open = line[..click + clicked].rfind('(');
    let close = line[click..].find(')').map(|at| at + click);
    if let (Some(open), Some(close)) = (open, close)
        && close > open
        && click > open
    {
        let candidate = &line[open + 1..close];
        if !candidate.contains("://") && PARENTHESIZED.is_match(candidate) {
            if let Some(colon) = candidate.rfind(':')
                && candidate[..colon].encode_utf16().count() > 1
                && let Some(number) = line_number(&candidate[colon + 1..])
            {
                return Some((candidate[..colon].to_string(), Some(number)));
            }
            return Some((candidate.to_string(), None));
        }
    }
    let mut at = 0;
    while at < line.len() {
        let Some(c) = line[at..].chars().next() else { break };
        if may_start(&line, at)
            && let Some(found) = REFERENCE.captures(&line[at..])
        {
            let whole = found.get(0).expect("the match");
            let (match_start, match_end) = (at, at + whole.end());
            if click >= match_start && click < match_end {
                // Never a web address's suffix as a local file.
                let token_start = line[..match_start].rfind([' ', '\t']).map_or(0, |s| s + 1);
                let token_end = line[match_start..].find([' ', '\t']).map_or(line.len(), |e| e + match_start);
                if line[token_start..token_end].contains("://") {
                    return None;
                }
                let path = found.get(1).expect("the path").as_str().to_string();
                return Some((path, found.get(2).and_then(|n| n.as_str().parse().ok())));
            }
            at = match_end.max(at + c.len_utf8());
            continue;
        }
        at += c.len_utf8();
    }
    None
}
