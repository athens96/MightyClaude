//! `cargo run -p mighty-native --example native-diff -- <cs.jsonl> <rs.jsonl> [--mask <text>]...` (plan W6 §1.8): compares the
//! `native` pushes C#'s web smoke sent (captured over CDP) with the Rust host's (`--native-dump`), byte for byte after normalizing.
//!
//! Each line is one push, or `{"seq":n,"message":{…}}` around one. Both sides are read keeping their key order, then:
//! - every `id` (and a segment option's `id`) is renumbered by its first appearance in its message, and `dialog-N` by the order the
//!   dialogs first appear in the file, so C#'s global counters and Rust's key-path ids compare;
//! - each `--mask` text (the profile path, the version) is replaced by `<masked>` inside every string, and a `select` whose name says
//!   it lists font families has its `items` emptied;
//! - only the last push of each view (its id, its `selected` category, its title) is kept;
//! - both are written again with C#'s `Wire.Json` writer, which leaves only escaping differences out.
//!
//! It prints each view's verdict and exits 1 when any differs or is missing on one side.

use std::collections::BTreeMap;
use std::process::ExitCode;

use mighty_core::json::JsonOut;
use serde_json::Value;

fn read(path: &str) -> Result<Vec<Value>, String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{path}: {e}"))?;
    // A file PowerShell wrote starts with a byte order mark.
    let text = text.trim_start_matches('\u{feff}');
    let mut pushes = vec![];
    for (number, line) in text.lines().enumerate().filter(|(_, l)| !l.trim().is_empty()) {
        let value: Value = serde_json::from_str(line).map_err(|e| format!("{path}:{}: {e}", number + 1))?;
        let message = value.get("message").cloned().unwrap_or(value);
        if message.get("kind").and_then(Value::as_str) == Some("native") {
            pushes.push(message);
        }
    }
    Ok(pushes)
}

/// Renumbers every `id` number of a message by its first appearance.
fn renumber(value: &mut Value, seen: &mut Vec<i64>) {
    match value {
        Value::Object(map) => {
            for (key, item) in map.iter_mut() {
                if key == "id"
                    && let Some(id) = item.as_i64()
                {
                    let at = seen.iter().position(|s| *s == id).unwrap_or_else(|| {
                        seen.push(id);
                        seen.len() - 1
                    });
                    *item = Value::from(at as i64 + 1);
                } else {
                    renumber(item, seen);
                }
            }
        }
        Value::Array(items) => items.iter_mut().for_each(|item| renumber(item, seen)),
        _ => {}
    }
}

fn mask(value: &mut Value, masks: &[String]) {
    match value {
        Value::String(text) => {
            for m in masks.iter().filter(|m| !m.is_empty()) {
                *text = text.replace(m.as_str(), "<masked>");
            }
        }
        Value::Object(map) => {
            let fonts = map.get("t").and_then(Value::as_str) == Some("select")
                && map.get("name").and_then(Value::as_str).is_some_and(|n| n.to_lowercase().contains("font") || n.contains("글꼴"));
            if fonts {
                map.insert("items".into(), Value::Array(vec![]));
            }
            map.values_mut().for_each(|item| mask(item, masks));
        }
        Value::Array(items) => items.iter_mut().for_each(|item| mask(item, masks)),
        _ => {}
    }
}

/// The last push of each view, normalized, by its key.
fn normalize(pushes: Vec<Value>, masks: &[String]) -> BTreeMap<String, String> {
    let mut dialogs: Vec<String> = vec![];
    let mut kept = BTreeMap::new();
    for mut push in pushes {
        let Some(mut id) = push.get("id").and_then(Value::as_str).map(str::to_string) else { continue };
        if id.starts_with("dialog-") {
            let at = dialogs.iter().position(|d| *d == id).unwrap_or_else(|| {
                dialogs.push(id.clone());
                dialogs.len() - 1
            });
            id = format!("dialog-{}", at + 1);
            push["id"] = Value::from(id.clone());
        }
        renumber(&mut push, &mut vec![]);
        mask(&mut push, masks);
        let view = push.get("view");
        let part = |key: &str| view.and_then(|v| v.get(key)).and_then(Value::as_str).unwrap_or("").to_string();
        let key = format!("{id} {} {}", part("selected"), part("title"));
        let mut out = JsonOut::new();
        out.value(&push);
        kept.insert(key, out.finish());
    }
    kept
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut files = vec![];
    let mut masks = vec![];
    let mut at = 0;
    while at < args.len() {
        if args[at] == "--mask" {
            masks.extend(args.get(at + 1).cloned());
            at += 2;
        } else {
            files.push(args[at].clone());
            at += 1;
        }
    }
    let [cs, rs] = files.as_slice() else {
        eprintln!("usage: native-diff <cs.jsonl> <rs.jsonl> [--mask <text>]...");
        return ExitCode::from(2);
    };
    let (cs, rs) = match (read(cs), read(rs)) {
        (Ok(cs), Ok(rs)) => (normalize(cs, &masks), normalize(rs, &masks)),
        (Err(e), _) | (_, Err(e)) => {
            eprintln!("{e}");
            return ExitCode::from(2);
        }
    };
    let mut differ = false;
    for key in cs.keys().chain(rs.keys().filter(|k| !cs.contains_key(*k))) {
        match (cs.get(key), rs.get(key)) {
            (Some(a), Some(b)) if a == b => println!("same     {key}"),
            (Some(a), Some(b)) => {
                differ = true;
                let at = a.bytes().zip(b.bytes()).position(|(x, y)| x != y).unwrap_or(a.len().min(b.len()));
                // `JsonOut` writes ASCII only (every other character escaped), so any byte starts a character.
                let from = at.saturating_sub(80);
                println!(
                    "DIFFERS  {key} at byte {at}\n  cs: …{}\n  rs: …{}",
                    &a[from..(at + 80).min(a.len())],
                    &b[from..(at + 80).min(b.len())]
                );
            }
            (Some(_), None) => {
                differ = true;
                println!("CS ONLY  {key}");
            }
            (None, _) => {
                differ = true;
                println!("RS ONLY  {key}");
            }
        }
    }
    if differ { ExitCode::from(1) } else { ExitCode::SUCCESS }
}
