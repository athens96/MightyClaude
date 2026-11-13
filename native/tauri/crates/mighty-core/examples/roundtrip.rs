//! Reads state files (read-only) and says whether each loads and is written back without losing a field.
//!
//!     cargo run -p mighty-core --example roundtrip -- <workspace-state.json>...
//!
//! Exits 1 when any file does not load or changes on the round trip.

use std::process::ExitCode;

use mighty_core::store;
use serde_json::{Map, Value};

fn comparable(value: &Value) -> Value {
    match value {
        Value::Object(map) => {
            Value::Object(map.iter().filter(|(_, v)| !v.is_null()).map(|(k, v)| (k.clone(), comparable(v))).collect::<Map<_, _>>())
        }
        Value::Array(list) => Value::Array(list.iter().map(comparable).collect()),
        Value::Number(n) => n.as_f64().and_then(serde_json::Number::from_f64).map(Value::Number).unwrap_or(Value::Null),
        other => other.clone(),
    }
}

fn main() -> ExitCode {
    let mut failed = false;
    for path in std::env::args().skip(1) {
        let outcome = (|| -> Result<String, String> {
            let bytes = std::fs::read(&path).map_err(|e| e.to_string())?;
            if store::damaged(&bytes) {
                return Err("damaged (NUL bytes): the load would read the backup".into());
            }
            let raw: Value = serde_json::from_slice(bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes)).map_err(|e| e.to_string())?;
            let decoded = store::decode(raw.clone()).map_err(|e| format!("does not decode: {e}"))?;
            let written = serde_json::to_value(&decoded).map_err(|e| e.to_string())?;
            if comparable(&raw) != comparable(&written) {
                return Err("the round trip changed it".into());
            }
            let loaded = store::parse_state(&bytes).map_err(|e| e.to_string())?;
            Ok(format!(
                "{} bytes, {} workspaces, {} panes (load keeps {} workspaces)",
                bytes.len(),
                decoded.workspaces.len(),
                decoded.sessions.len(),
                loaded.workspaces.len()
            ))
        })();
        match outcome {
            Ok(summary) => println!("ok    {path}: {summary}"),
            Err(why) => {
                failed = true;
                println!("FAIL  {path}: {why}");
            }
        }
    }
    if failed { ExitCode::FAILURE } else { ExitCode::SUCCESS }
}
