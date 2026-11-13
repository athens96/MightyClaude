//! Byte parity with the C# app: the parity corpus (`examples/parity.rs`: the synthetic edge cases and the repository's fixtures)
//! saved by MightyCode.Core itself (`tools/parity`, built with the .NET SDK) and by this crate, and the doubles, strings and times
//! vectors, compared byte for byte. Without the .NET 10 SDK the test says so and passes; `MIGHTY_PARITY=require` makes it fail
//! instead. `tools/parity/run.ps1` runs the same over copies of real profiles too.

#[path = "../examples/parity.rs"]
#[allow(dead_code)]
mod parity;

mod common;

use std::path::{Path, PathBuf};
use std::process::Command;

/// The C# tool, built into the workspace's target folder, or None when there is no .NET SDK to build it.
fn csharp_tool() -> Option<PathBuf> {
    let tauri = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let project = tauri.join("tools/parity/Parity.csproj");
    let out = tauri.join("target/parity-cs");
    let built = Command::new("dotnet")
        .args(["build"])
        .arg(&project)
        .args(["-c", "Release", "-o"])
        .arg(&out)
        .args(["--nologo", "-v", "quiet"])
        .status();
    match built {
        Ok(status) if status.success() => Some(out.join("parity.dll")),
        other => {
            let why = format!("the C# parity tool did not build ({other:?})");
            assert!(std::env::var("MIGHTY_PARITY").as_deref() != Ok("require"), "{why}");
            eprintln!("skipped: {why}");
            None
        }
    }
}

fn dotnet(tool: &Path, args: &[&Path]) {
    let mut command = Command::new("dotnet");
    command.arg(tool);
    for (i, arg) in args.iter().enumerate() {
        if i == 0 {
            command.arg(arg.to_string_lossy().to_string())
        } else {
            command.arg(arg)
        };
    }
    assert!(command.status().expect("dotnet").success(), "the C# tool failed: {args:?}");
}

#[test]
fn rust_saves_what_csharp_saves() {
    let Some(tool) = csharp_tool() else { return };
    let work = common::temp_dir("parity");
    let corpus = work.join("corpus");
    std::fs::create_dir_all(&corpus).unwrap();
    let count = parity::synthetic(&corpus).unwrap() + parity::fixtures(&corpus, &common::repo()).unwrap();
    assert!(count > 100, "the corpus has {count} cases");
    let (cs, rs) = (work.join("cs"), work.join("rs"));
    dotnet(&tool, &[Path::new("save-corpus"), &corpus, &cs]);
    parity::save_corpus(&corpus, &rs).unwrap();
    assert!(parity::compare_corpus(&corpus, &cs, &rs).unwrap(), "C# and Rust saved different bytes (see the output above)");

    let vectors = work.join("vectors");
    parity::make_vectors(&vectors).unwrap();
    for kind in ["doubles", "strings", "times"] {
        let input = vectors.join(format!("{kind}.txt"));
        let (a, b) = (vectors.join(format!("{kind}.cs.txt")), vectors.join(format!("{kind}.rs.txt")));
        dotnet(&tool, &[Path::new(kind), &input, &a]);
        let each: fn(&str) -> String = match kind {
            "doubles" => parity::double,
            "strings" => |l| mighty_core::json::escape(&String::from_utf8_lossy(&parity::hex(l))),
            _ => parity::time_line,
        };
        parity::lines(&input.to_string_lossy(), &b.to_string_lossy(), each).unwrap();
        assert!(parity::compare_lines(&a, &b).unwrap(), "{kind} differ");
    }
    std::fs::remove_dir_all(work).ok();
}
