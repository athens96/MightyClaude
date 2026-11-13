//! Reads state files (read-only) and says whether each loads as C# loads it, and what a save would write.
//!
//!     cargo run -p mighty-core --example roundtrip -- <workspace-state.json>...
//!
//! Exits 1 when any file does not load, or a second save of what the first one wrote changes it (a save is a fixed point).

use std::process::ExitCode;

use mighty_core::locale::Locale;
use mighty_core::store;

fn main() -> ExitCode {
    let locale = Locale::new("en");
    let mut failed = false;
    for path in std::env::args().skip(1) {
        let outcome = (|| -> Result<String, String> {
            let bytes = std::fs::read(&path).map_err(|e| e.to_string())?;
            if store::damaged(&bytes) {
                return Err("damaged (NUL bytes): the load would read the backup".into());
            }
            let started = std::time::Instant::now();
            let loaded = store::read_state(&bytes, &locale).map_err(|e| format!("{} ({})", e, e.kind()))?;
            let read = started.elapsed();
            let started = std::time::Instant::now();
            let (_, first) = store::encode_for_save(&loaded).map_err(|e| e.to_string())?;
            let saved = started.elapsed();
            let again = store::read_state(&first, &locale).map_err(|e| format!("the saved state does not load: {e}"))?;
            let (_, second) = store::encode_for_save(&again).map_err(|e| e.to_string())?;
            if first != second {
                return Err("a second save changed what the first wrote".into());
            }
            Ok(format!(
                "{} bytes read in {:.1} ms, {} written in {:.1} ms (normalize and write), {} workspaces, {} panes",
                bytes.len(),
                read.as_secs_f64() * 1000.0,
                first.len(),
                saved.as_secs_f64() * 1000.0,
                loaded.workspaces().len(),
                loaded.sessions().len()
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
