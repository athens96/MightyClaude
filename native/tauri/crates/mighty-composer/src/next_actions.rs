//! The next-action breadcrumb (C# `NextActions.cs`): the options after `◆ … → next:` in the latest reply, which only fill a draft.
//! The same literal contract as macOS and the phone (`native/contracts/fixtures/next-actions.json`).

use mighty_core::model::LogEntry;

/// C# `NextAction(Label, Fill)` (NextActions.cs:6).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NextAction {
    pub label: String,
    pub fill: String,
}

impl NextAction {
    pub fn new(label: &str, fill: &str) -> Self {
        NextAction { label: label.into(), fill: fill.into() }
    }

    /// C# `NextAction.DisplayLabel`: the label without backticks, unless nothing but them is left.
    pub fn display_label(&self) -> String {
        let stripped = self.label.replace('`', "");
        if trim(&stripped).is_empty() { self.label.clone() } else { stripped }
    }
}

/// C# `NextActions.Maximum`.
pub const MAXIMUM: usize = 4;
const WHITESPACE: [char; 5] = [' ', '\t', '\r', '\n', '\u{3000}'];
const SEPARATORS: [&str; 6] =
    [", \u{B610}\u{B294} ", ", or ", " \u{2014} \u{B610}\u{B294} ", " \u{2014} or ", " \u{B610}\u{B294} ", " or "];
const CHOICE_SUFFIXES: [&str; 3] =
    [" \u{C911}\u{C5D0}\u{C11C} \u{C120}\u{D0DD}", " \u{C911} \u{C120}\u{D0DD}", " \u{C911} \u{D558}\u{B098}"];

pub(crate) fn trim(text: &str) -> &str {
    text.trim_matches(&WHITESPACE[..])
}

/// C# `NextActions.BreadcrumbNext` (NextActions.cs:21): what follows `→ next:` on the last `◆` line that has it.
pub fn breadcrumb_next(text: &str) -> Option<String> {
    if !text.contains('\u{25C6}') {
        return None;
    }
    for raw in text.split('\n').rev() {
        let line = trim(raw);
        if !line.starts_with('\u{25C6}') {
            continue;
        }
        let found = ["\u{2192} next:", "-> next:"]
            .iter()
            .filter_map(|marker| line.find(marker).map(|at| (at, marker.len())))
            .min_by_key(|(at, _)| *at);
        if let Some((at, len)) = found {
            return Some(trim(&line[at + len..]).to_string());
        }
    }
    None
}

/// C# `NextActions.Parse` (NextActions.cs:34): at most four options.
pub fn parse(text: &str) -> Vec<NextAction> {
    let Some(mut next) = breadcrumb_next(text).filter(|n| !n.is_empty()) else { return vec![] };
    let suffix = CHOICE_SUFFIXES.iter().find(|s| next.ends_with(*s));
    if let Some(suffix) = suffix {
        next.truncate(next.len() - suffix.len());
    }
    let mut options = split_top_level(&next, &SEPARATORS);
    if suffix.is_some() {
        options = options.iter().flat_map(|value| split_top_level(value, &[","])).collect();
    }
    options
        .iter()
        .map(|value| trim(strip_alternative(trim(value))).to_string())
        .filter(|value| !value.is_empty())
        .map(|label| {
            let fill = fill_for(&label);
            NextAction { label, fill }
        })
        .filter(|action| !trim(&action.fill).is_empty())
        .take(MAXIMUM)
        .collect()
}

/// C# `NextActions.Latest` (NextActions.cs:45): the newest reply's options, unless a request came after it.
pub fn latest(entries: &[LogEntry]) -> Option<(String, Vec<NextAction>)> {
    for entry in entries.iter().rev() {
        match entry.kind() {
            "user" => return None,
            "assistant" => {
                let actions = parse(entry.text());
                return (!actions.is_empty()).then(|| (entry.id().to_string(), actions));
            }
            _ => continue,
        }
    }
    None
}

/// C# `NextActions.Insertion` (NextActions.cs:55): a blank draft is replaced; any other keeps its words and gets a new line.
pub fn insertion(draft: &str, fill: &str) -> (bool, String) {
    if trim(draft).is_empty() {
        return (true, fill.to_string());
    }
    (false, if draft.ends_with('\n') { fill.to_string() } else { format!("\n{fill}") })
}

/// C# `Matches` (NextActions.cs:60): ASCII capitals folded, every other character as it is.
fn matches_at(text: &[char], index: usize, wanted: &[char]) -> bool {
    if index + wanted.len() > text.len() {
        return false;
    }
    wanted.iter().enumerate().all(|(i, w)| text[index + i].to_ascii_lowercase() == *w)
}

/// C# `SplitTopLevel` (NextActions.cs:70): split outside backticks and parentheses.
fn split_top_level(text: &str, separators: &[&str]) -> Vec<String> {
    let chars: Vec<char> = text.chars().collect();
    let seps: Vec<Vec<char>> = separators.iter().map(|s| s.chars().collect()).collect();
    let mut parts = Vec::new();
    let mut current = String::new();
    let (mut in_code, mut depth) = (false, 0usize);
    let mut index = 0;
    while index < chars.len() {
        if !in_code
            && depth == 0
            && let Some(sep) = seps.iter().find(|s| matches_at(&chars, index, s))
        {
            parts.push(std::mem::take(&mut current));
            index += sep.len();
            continue;
        }
        let c = chars[index];
        if c == '`' {
            in_code = !in_code;
        } else if !in_code && c == '(' {
            depth += 1;
        } else if !in_code && c == ')' {
            depth = depth.saturating_sub(1);
        }
        current.push(c);
        index += 1;
    }
    parts.push(current);
    parts
}

/// C# `StripAlternative` (NextActions.cs:85): a leading "또는" or "or" before white space.
fn strip_alternative(text: &str) -> &str {
    let chars: Vec<char> = text.chars().collect();
    for word in ["\u{B610}\u{B294}", "or"] {
        let w: Vec<char> = word.chars().collect();
        if chars.len() > w.len() && matches_at(&chars, 0, &w) && WHITESPACE.contains(&chars[w.len()]) {
            let byte = text.char_indices().nth(w.len()).map_or(text.len(), |(at, _)| at);
            return &text[byte..];
        }
    }
    text
}

/// C# `Command`: `^(?:ooo(?:[ \t]|\z)|/[A-Za-z][A-Za-z0-9_-]*(?::[A-Za-z0-9_-]+)?(?:[ \t]|\z))`.
fn is_command(text: &str) -> bool {
    let b = text.as_bytes();
    let ends = |at: usize| at == b.len() || matches!(b[at], b' ' | b'\t');
    if b.starts_with(b"ooo") && ends(3) {
        return true;
    }
    if b.len() < 2 || b[0] != b'/' || !b[1].is_ascii_alphabetic() {
        return false;
    }
    let word = |c: u8| c.is_ascii_alphanumeric() || c == b'_' || c == b'-';
    let mut at = 2;
    while at < b.len() && word(b[at]) {
        at += 1;
    }
    if at < b.len() && b[at] == b':' {
        let mut end = at + 1;
        while end < b.len() && word(b[end]) {
            end += 1;
        }
        if end > at + 1 && ends(end) {
            return true;
        }
    }
    ends(at)
}

/// C# `FillFor` (NextActions.cs:91): a command in backticks, else the one backticked span that is the whole label, else the label.
fn fill_for(label: &str) -> String {
    let mut spans: Vec<&str> = Vec::new();
    let mut start: Option<usize> = None;
    for (index, c) in label.char_indices() {
        if c != '`' {
            continue;
        }
        match start {
            Some(opened) => {
                if index > opened {
                    spans.push(&label[opened..index]);
                }
                start = None;
            }
            None => start = Some(index + 1),
        }
    }
    if let Some(span) = spans.iter().find(|span| is_command(trim(span))) {
        return trim(span).to_string();
    }
    if spans.len() == 1 && trim(label) == format!("`{}`", spans[0]) { trim(spans[0]).to_string() } else { label.to_string() }
}
