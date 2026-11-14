//! `cargo run -p mighty-native --example native-diff -- <cs.jsonl> <rs.jsonl> [--mask <text>]... [--last] [--write-cs <folder>]` (plan W6 §1.8): compares the
//! `native` pushes C#'s web smoke sent (captured over CDP) with the Rust host's (`--native-dump`), byte for byte after normalizing.
//!
//! Each line is one push, or `{"seq":n,"message":{…}}` around one. Then:
//! - every object's keys are sorted on both sides: WebView2 hands `PostWebMessageAsJson`'s message to the page through Chromium's
//!   values, whose dictionaries are sorted maps, so C#'s page (and so its capture) never sees the order C# wrote; the order is the one
//!   thing this cannot compare;
//! - every `id` (and a segment option's `id`) is renumbered by its first appearance in its message, so C#'s global counters and Rust's
//!   key-path ids compare; a dialog's `dialog-N` becomes `dialog`, and its title tells it apart (the two smokes open other dialogs
//!   between the ones they share);
//! - each `--mask` text (the profile path, the version) is replaced by `<masked>` inside every string, and a `select` whose name says
//!   it lists font families has its `items` emptied;
//! - a close (`native{id}` without a view) is left out, and only the first push of each view (its id, its `selected` category,
//!   its title) is kept: each view as it opened, before either smoke changed it (both walk Settings' categories in the same order;
//!   their later steps differ), or with `--last` the last one;
//! - both are written again with C#'s `Wire.Json` writer, which leaves only escaping differences out.
//!
//! It prints each view's verdict and exits 1 when any differs or is missing on one side.

use std::collections::BTreeMap;
use std::process::ExitCode;

use mighty_native::parity;
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

/// Where two values differ, as JSON paths with both sides (at most `limit`).
fn differences(a: &Value, b: &Value, path: &str, out: &mut Vec<String>, limit: usize) {
    if out.len() >= limit || a == b {
        return;
    }
    match (a, b) {
        (Value::Object(x), Value::Object(y)) => {
            for key in x.keys().chain(y.keys().filter(|k| !x.contains_key(*k))) {
                let at = format!("{path}.{key}");
                match (x.get(key), y.get(key)) {
                    (Some(p), Some(q)) => differences(p, q, &at, out, limit),
                    (p, q) => out.push(format!("{at}: cs {} / rs {}", short(p), short(q))),
                }
                if out.len() >= limit {
                    return;
                }
            }
        }
        (Value::Array(x), Value::Array(y)) if x.len() == y.len() => {
            for (at, (p, q)) in x.iter().zip(y).enumerate() {
                differences(p, q, &format!("{path}[{at}]"), out, limit);
            }
        }
        _ => out.push(format!("{path}: cs {} / rs {}", short(Some(a)), short(Some(b)))),
    }
}

fn short(value: Option<&Value>) -> String {
    let Some(value) = value else { return "(none)".into() };
    let text = value.to_string();
    if text.chars().count() > 160 { format!("{}…", text.chars().take(160).collect::<String>()) } else { text }
}

/// A file name for a view's key.
fn slug(key: &str) -> String {
    let words: String = key.trim().chars().map(|c| if c.is_ascii_alphanumeric() { c.to_ascii_lowercase() } else { '-' }).collect();
    words.split('-').filter(|w| !w.is_empty()).collect::<Vec<_>>().join("-")
}

/// The first (or with `last`, the last) push of each view, normalized, by its key.
fn normalize(pushes: Vec<Value>, masks: &[String], last: bool) -> BTreeMap<String, (String, Value)> {
    let mut kept = BTreeMap::new();
    for mut push in pushes {
        let Some(mut id) = push.get("id").and_then(Value::as_str).map(str::to_string) else { continue };
        if push.get("view").is_none() {
            continue;
        }
        if id.starts_with("dialog-") {
            id = "dialog".to_string();
            push["id"] = Value::from(id.clone());
        }
        let push = parity::normalize(&push, masks);
        let view = push.get("view");
        let part = |key: &str| view.and_then(|v| v.get(key)).and_then(Value::as_str).unwrap_or("").to_string();
        let key = format!("{id} {} {}", part("selected"), part("title"));
        if !last && kept.contains_key(&key) {
            continue;
        }
        kept.insert(key, (parity::text(&push), push));
    }
    kept
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut files = vec![];
    let mut masks = vec![];
    let mut golden = None;
    let mut last = false;
    let mut at = 0;
    while at < args.len() {
        if args[at] == "--mask" {
            masks.extend(args.get(at + 1).cloned());
            at += 2;
        } else if args[at] == "--last" {
            last = true;
            at += 1;
        } else if args[at] == "--write-cs" {
            golden = args.get(at + 1).cloned();
            at += 2;
        } else {
            files.push(args[at].clone());
            at += 1;
        }
    }
    let [cs, rs] = files.as_slice() else {
        eprintln!("usage: native-diff <cs.jsonl> <rs.jsonl> [--mask <text>]... [--last] [--write-cs <folder>]");
        return ExitCode::from(2);
    };
    let (cs, rs) = match (read(cs), read(rs)) {
        (Ok(cs), Ok(rs)) => (normalize(cs, &masks, last), normalize(rs, &masks, last)),
        (Err(e), _) | (_, Err(e)) => {
            eprintln!("{e}");
            return ExitCode::from(2);
        }
    };
    if let Some(folder) = &golden {
        // Each C# view normalized, one file per view (the goldens' source: tests/golden).
        let _ = std::fs::create_dir_all(folder);
        let mut written: Vec<String> = vec![];
        for (key, (_, value)) in &cs {
            let base = if slug(key).is_empty() { "view".to_string() } else { slug(key) };
            let (mut name, mut n) = (base.clone(), 1);
            while written.contains(&name) {
                n += 1;
                name = format!("{base}-{n}");
            }
            written.push(name.clone());
            match std::fs::write(format!("{folder}/{name}.json"), parity::text(value) + "\n") {
                Ok(()) => println!("wrote    {name}.json  {key}"),
                Err(e) => eprintln!("{folder}/{name}.json: {e}"),
            }
        }
    }
    let mut differ = false;
    for key in cs.keys().chain(rs.keys().filter(|k| !cs.contains_key(*k))) {
        match (cs.get(key), rs.get(key)) {
            (Some((a, _)), Some((b, _))) if a == b => println!("same     {key}"),
            (Some((a, x)), Some((b, y))) => {
                differ = true;
                let at = a.bytes().zip(b.bytes()).position(|(x, y)| x != y).unwrap_or(a.len().min(b.len()));
                println!("DIFFERS  {key} at byte {at}");
                let mut found = vec![];
                differences(x, y, "", &mut found, 40);
                for line in found {
                    println!("  {line}");
                }
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
