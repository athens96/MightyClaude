//! Seeds a test profile with the help capture's demo state:
//!
//!     cargo run -p mighty-core --example seed_demo -- <profile folder> [ko|en|zh|ja]
//!
//! Writes `<profile>\workspace-state.json` made from native/contracts/fixtures/demo-profile.json, and creates the workspaces'
//! empty folders under `<profile>\Projects`. Refuses a profile that already holds a state file.

use std::path::{Path, PathBuf};
use std::process::ExitCode;

use mighty_core::{demo, store};

fn main() -> ExitCode {
    let mut args = std::env::args().skip(1);
    let Some(profile) = args.next().map(PathBuf::from) else {
        eprintln!("usage: seed_demo <profile folder> [ko|en|zh|ja]");
        return ExitCode::FAILURE;
    };
    let language = args.next().unwrap_or_else(|| "en".into());
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures/demo-profile.json");
    let state_file = profile.join(store::STATE_FILE);
    if state_file.exists() {
        eprintln!("{} already exists", state_file.display());
        return ExitCode::FAILURE;
    }
    let run = || -> Result<usize, Box<dyn std::error::Error>> {
        let demo_profile: serde_json::Value = serde_json::from_slice(&std::fs::read(&source)?)?;
        let projects = std::path::absolute(profile.join("Projects"))?;
        let state = demo::demo_state(&demo_profile, &language, &projects);
        for workspace in state["workspaces"].as_array().into_iter().flatten() {
            std::fs::create_dir_all(workspace["path"].as_str().unwrap_or_default())?;
        }
        std::fs::create_dir_all(&profile)?;
        std::fs::write(&state_file, serde_json::to_vec_pretty(&state)?)?;
        Ok(state["sessions"].as_array().map_or(0, Vec::len))
    };
    match run() {
        Ok(panes) => {
            println!("seeded {} ({panes} panes, {language})", state_file.display());
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("seed failed: {error}");
            ExitCode::FAILURE
        }
    }
}
