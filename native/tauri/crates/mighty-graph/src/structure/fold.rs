//! Finished requests fold in the phone's structure view (C# `MightyStructure.Fold`, MightyCode.Core/MightyStructureFold.cs; macOS
//! MightyStructureFold.swift): a card is folded exactly while it is finished and the user has not opened it.

use std::collections::HashSet;

use super::{Item, NEWLINES};

/// `IsFolded`: finished, and not opened by hand.
pub fn is_folded(item: &Item, unfolded: &HashSet<String>) -> bool {
    item.finished && !unfolded.contains(&item.run_id)
}

/// `Folded`: the cards of `items` that show folded.
pub fn folded<'a>(items: impl IntoIterator<Item = &'a Item>, unfolded: &HashSet<String>) -> HashSet<String> {
    items.into_iter().filter(|i| is_folded(i, unfolded)).map(|i| i.run_id.clone()).collect()
}

/// `Toggled`: the hand-opened cards after the user opens or folds `item`'s card; only a finished card has the control.
pub fn toggled(item: &Item, unfolded: &HashSet<String>) -> HashSet<String> {
    let mut next = unfolded.clone();
    if item.finished && !next.remove(&item.run_id) {
        next.insert(item.run_id.clone());
    }
    next
}

/// `ResultLine`: the folded card's result line, the answer's first line without its Markdown marks; none for a request that failed,
/// stopped, or completed without an answer.
pub fn result_line(item: &Item) -> Option<String> {
    if item.status != "completed" {
        return None;
    }
    item.answer.as_ref().map(|a| a.text.as_str()).or(item.result.as_deref()).and_then(first_line)
}

/// `FirstLine`: the first line with words in it, without the Markdown that opens a line (a heading's #, a quote's >, a list's mark or
/// number) or wraps a word (`, **, __).
pub fn first_line(text: &str) -> Option<String> {
    for raw in text.split(NEWLINES).filter(|l| !l.is_empty()) {
        let mut line = raw.trim_start_matches([' ', '\t']);
        if line.starts_with("```") {
            continue;
        }
        while let Some(rest) = line.strip_prefix(['#', '>']) {
            line = rest.trim_start_matches(' ');
        }
        let b = line.as_bytes();
        if b.len() > 1 && matches!(b[0], b'-' | b'*' | b'+') && b[1] == b' ' {
            line = &line[2..];
        }
        if let Some(dot) = line.find('.')
            && dot > 0
            && line.as_bytes().get(dot + 1) == Some(&b' ')
            && line[..dot].chars().all(char::is_numeric)
        {
            line = &line[dot + 2..];
        }
        let cleaned = line.replace("**", "").replace("__", "").replace('`', "");
        let cleaned = cleaned.trim();
        if !cleaned.is_empty() {
            return Some(cleaned.to_string());
        }
    }
    None
}
