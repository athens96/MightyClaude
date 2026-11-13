#![allow(dead_code)]

use std::path::{Path, PathBuf};

use serde_json::{Map, Value};

/// The repository root (this crate is native/tauri/crates/mighty-core).
pub fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../..").canonicalize().expect("repository root")
}

pub fn read_json(path: &Path) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// A value as compared after a round trip: object members whose value is null left out (C# `Wire.Json` writes no null, and a
/// field read as null comes back absent), and every number as its f64 (252 and 252.0 are the same number).
pub fn comparable(value: &Value) -> Value {
    match value {
        Value::Object(map) => {
            Value::Object(map.iter().filter(|(_, v)| !v.is_null()).map(|(k, v)| (k.clone(), comparable(v))).collect::<Map<_, _>>())
        }
        Value::Array(list) => Value::Array(list.iter().map(comparable).collect()),
        Value::Number(n) => n.as_f64().and_then(serde_json::Number::from_f64).map(Value::Number).unwrap_or(Value::Null),
        other => other.clone(),
    }
}

/// The first place two values differ, for a readable failure.
pub fn first_difference(a: &Value, b: &Value, at: &str) -> Option<String> {
    match (a, b) {
        (Value::Object(x), Value::Object(y)) => {
            for key in x.keys().chain(y.keys()) {
                match (x.get(key), y.get(key)) {
                    (Some(p), Some(q)) => {
                        if let Some(d) = first_difference(p, q, &format!("{at}.{key}")) {
                            return Some(d);
                        }
                    }
                    (p, q) => return Some(format!("{at}.{key}: {p:?} vs {q:?}")),
                }
            }
            None
        }
        (Value::Array(x), Value::Array(y)) if x.len() == y.len() => {
            x.iter().zip(y).enumerate().find_map(|(i, (p, q))| first_difference(p, q, &format!("{at}[{i}]")))
        }
        _ if a == b => None,
        _ => Some(format!("{at}: {a} vs {b}")),
    }
}

/// A new empty folder under the system's temporary folder.
pub fn temp_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("mighty-core-{name}-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}
