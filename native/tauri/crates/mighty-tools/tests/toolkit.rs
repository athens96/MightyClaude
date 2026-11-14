//! The toolkit and the components rows (C# ToolkitFileVerification.cs, ToolkitVerification.cs, WindowsParityVerification.cs:119,
//! WindowsCommandVerification.cs:68-91, ComposerParityVerification.cs:503). Every check uses its own temporary folders: no real
//! user path is read, nothing is installed, the steps run through fake executors (and, on Windows, the stand-in CLI through
//! Windows' own ProcessHost).
//!
//! Not here: "components screen is live in the running app" (L1561) becomes the host's registry and smoke checks in phase 2;
//! "bare names are found along the PATH with PATHEXT" and "batch files run through cmd with every argument quoted" (L1544-1545)
//! are mighty-platform-windows' (`command.rs`).

mod common;

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use mighty_platform::BoxFuture;
use mighty_platform::fake::{FakeProcessHost, FakeScript, FakeShellEnvironment};
use mighty_run::model::{ModelCatalog, ModsInfo, ProviderCapabilities, ProviderRuntime, RuntimeInfo};
use mighty_run::strings;
use mighty_tools::setup::components::{self, ComponentStatus};
use mighty_tools::toolkit::file::{self, ToolkitEntrySource, ToolkitFileEntry, ToolkitInstallSpec};
use mighty_tools::toolkit::probe::{self, ProbeResult, ToolkitProbeContext};
use mighty_tools::toolkit::runner::{self, presentation};
use mighty_tools::toolkit::store::{self, ToolkitStore};
use mighty_tools::toolkit::{
    CommandOutput, PlanAction, ProcessToolkitExecutor, StepOutcome, ToolkitExecutor, ToolkitPlanItem, ToolkitRunItem, ToolkitRunner,
    Verdict,
};
use serde_json::Value;

fn kind_for(install: &ToolkitInstallSpec) -> &'static str {
    match install {
        ToolkitInstallSpec::Plugin { .. } => "plugin",
        ToolkitInstallSpec::Mcp { .. } => "mcp",
        ToolkitInstallSpec::Skill { .. } => "skill",
        ToolkitInstallSpec::Package { .. } => "package",
        ToolkitInstallSpec::RepoScript { .. } => "repoScript",
    }
}

fn make_entry(id: &str, install: ToolkitInstallSpec) -> ToolkitFileEntry {
    let kind = kind_for(&install);
    ToolkitFileEntry::new(id, id, kind, install)
}

fn plugin(source: &str, id: &str) -> ToolkitInstallSpec {
    ToolkitInstallSpec::Plugin { source: source.into(), plugin_id: id.into() }
}

fn mcp(name: &str, executable: &str) -> ToolkitInstallSpec {
    ToolkitInstallSpec::Mcp { name: name.into(), executable: executable.into(), args: vec![] }
}

fn skill(url: &str) -> ToolkitInstallSpec {
    ToolkitInstallSpec::Skill { url: url.into() }
}

fn package(manager: &str, name: &str, executable: Option<&str>) -> ToolkitInstallSpec {
    ToolkitInstallSpec::Package { manager: manager.into(), name: name.into(), executable: executable.map(str::to_string) }
}

fn repo(url: &str, git_ref: &str, path: &str) -> ToolkitInstallSpec {
    ToolkitInstallSpec::RepoScript { url: url.into(), git_ref: git_ref.into(), script_path: path.into() }
}

/// A probe context over an empty folder of its own (C#'s `FakeContext` reads the system's temporary folder).
fn fake_context(home: &Path) -> ToolkitProbeContext {
    ToolkitProbeContext { home_directory: home.into(), path_directories: vec![], local_app_data: home.into() }
}

fn ids(entries: &[ToolkitFileEntry]) -> Vec<String> {
    entries.iter().map(|e| e.id.clone()).collect()
}

fn read(path: &Path) -> String {
    let bytes = std::fs::read(path).unwrap();
    let text = String::from_utf8(bytes).unwrap();
    text.strip_prefix('\u{feff}').unwrap_or(&text).to_string()
}

/// Every argv it gets is recorded; every step succeeds.
#[derive(Default, Clone)]
struct FakeExecutor {
    log: Arc<Mutex<Vec<Vec<String>>>>,
}

impl FakeExecutor {
    fn log(&self) -> Vec<Vec<String>> {
        self.log.lock().unwrap().clone()
    }
}

impl ToolkitExecutor for FakeExecutor {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput> {
        self.log.lock().unwrap().push(argv.to_vec());
        Box::pin(async { CommandOutput::success() })
    }
}

/// Fails any command whose argv holds the token.
struct SpecificFailExecutor {
    log: Arc<Mutex<Vec<Vec<String>>>>,
    token: String,
}

impl ToolkitExecutor for SpecificFailExecutor {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput> {
        self.log.lock().unwrap().push(argv.to_vec());
        let fails = argv.contains(&self.token);
        Box::pin(async move { if fails { CommandOutput::failure("not a network error", 1) } else { CommandOutput::success() } })
    }
}

/// Always fails with a network error; counts calls per "binary subcommand".
#[derive(Default)]
struct NetworkFailExecutor {
    counts: Mutex<HashMap<String, usize>>,
}

impl ToolkitExecutor for NetworkFailExecutor {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput> {
        let key = if argv.len() > 1 { format!("{} {}", argv[0], argv[1]) } else { argv.first().cloned().unwrap_or_default() };
        *self.counts.lock().unwrap().entry(key).or_default() += 1;
        Box::pin(async { CommandOutput::failure("could not resolve host npmjs.com", 1) })
    }
}

/// A git clone that "succeeds" by placing SKILL.md at its destination.
#[derive(Default)]
struct SkillCreatingExecutor {
    log: Mutex<Vec<Vec<String>>>,
}

impl ToolkitExecutor for SkillCreatingExecutor {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput> {
        self.log.lock().unwrap().push(argv.to_vec());
        if argv.len() >= 4 && argv[0] == "git" && argv[1] == "clone" {
            let destination = PathBuf::from(&argv[argv.len() - 1]);
            common::write(&destination.join("SKILL.md"), "# Test Skill");
        }
        Box::pin(async { CommandOutput::success() })
    }
}

// ── ToolkitFileVerification ───────────────────────────────────────────────────────────────────────────────────────────────

const ALL_FIVE_TEMPLATES: &str = r#"{
  "version": 1,
  "entries": [
    { "id": "my-plugin", "displayName": "My Plugin", "install": { "kind": "plugin", "source": "athens96/mighty-styles", "pluginID": "mighty-styles@mighty-styles" },
      "approval": { "contentHash": "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890" } },
    { "id": "my-mcp", "displayName": "My MCP Server", "install": { "kind": "mcp", "name": "my-mcp", "executable": "/usr/local/bin/my-mcp-server", "args": ["--port", "3000"] } },
    { "id": "my-skill", "displayName": "My Skill", "install": { "kind": "skill", "url": "https://github.com/example/my-skill.git" },
      "approval": { "contentHash": "fedcba0987654321fedcba0987654321fedcba0987654321fedcba0987654321" } },
    { "id": "my-package", "displayName": "My Brew Package", "install": { "kind": "package", "manager": "brew", "name": "ripgrep" } },
    { "id": "my-repo-script", "displayName": "My Repo Script",
      "install": { "kind": "repoScript", "url": "https://github.com/example/setup-scripts.git", "ref": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2", "scriptPath": "scripts/install.sh" },
      "approval": { "contentHash": "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef", "resolvedCommit": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2" } }
  ]
}"#;

/// "toolkit file parses" (ToolkitFileVerification.cs:73).
#[test]
fn toolkit_file_parses() {
    let parsed = file::parse(ALL_FIVE_TEMPLATES).unwrap();
    assert_eq!(parsed.entries.len(), 5);
    let plugin_entry = &parsed.entries[0];
    assert!(plugin_entry.id == "my-plugin" && plugin_entry.display_name == "My Plugin" && plugin_entry.install_kind == "plugin");
    assert_eq!(plugin_entry.install, plugin("athens96/mighty-styles", "mighty-styles@mighty-styles"));
    assert!(plugin_entry.approval.as_ref().is_some_and(|a| !a.content_hash.is_empty() && a.resolved_commit.is_none()));
    let mcp_entry = &parsed.entries[1];
    assert!(mcp_entry.id == "my-mcp" && mcp_entry.install_kind == "mcp" && mcp_entry.approval.is_none());
    assert!(
        matches!(&mcp_entry.install, ToolkitInstallSpec::Mcp { name, executable, args } if name == "my-mcp" && executable == "/usr/local/bin/my-mcp-server" && args.len() == 2)
    );
    let skill_entry = &parsed.entries[2];
    assert!(skill_entry.id == "my-skill" && skill_entry.install_kind == "skill" && skill_entry.approval.is_some());
    assert_eq!(skill_entry.install, skill("https://github.com/example/my-skill.git"));
    let package_entry = &parsed.entries[3];
    assert!(package_entry.id == "my-package" && package_entry.install_kind == "package" && package_entry.approval.is_none());
    assert_eq!(package_entry.install, package("brew", "ripgrep", None));
    let repo_entry = &parsed.entries[4];
    assert!(repo_entry.id == "my-repo-script" && repo_entry.install_kind == "repoScript");
    assert_eq!(
        repo_entry.install,
        repo("https://github.com/example/setup-scripts.git", "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2", "scripts/install.sh")
    );
    assert_eq!(repo_entry.approval.as_ref().and_then(|a| a.resolved_commit.as_deref()), Some("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"));
}

/// "toolkit file refuses version mismatch" (ToolkitFileVerification.cs:124).
#[test]
fn toolkit_file_refuses_version_mismatch() {
    assert!(file::parse(r#"{"version":2,"entries":[]}"#).is_err());
}

/// "toolkit file skips unknown install kinds" (ToolkitFileVerification.cs:131).
#[test]
fn toolkit_file_skips_unknown_install_kinds() {
    let json = r#"{"version":1,"entries":[
        {"id":"good","displayName":"Good","install":{"kind":"skill","url":"https://github.com/example/x.git"}},
        {"id":"bad","displayName":"Bad","install":{"kind":"shell","command":"rm -rf /"}}]}"#;
    let parsed = file::parse(json).unwrap();
    assert!(parsed.entries.len() == 1 && parsed.entries[0].id == "good", "unknown kind is skipped");
}

/// "toolkit file npm package name is accepted" (ToolkitFileVerification.cs:155).
#[test]
fn toolkit_file_npm_package_name_is_accepted() {
    let json = r#"{"version":1,"entries":[{"id":"my-npm-tool","displayName":"NPM Tool","install":{"kind":"package","manager":"npm","name":"typescript"}}]}"#;
    let parsed = file::parse(json).unwrap();
    assert!(parsed.entries.len() == 1 && parsed.entries[0].install == package("npm", "typescript", None));
}

/// "toolkit add takes one entry object as the Mac does" (ToolkitFileVerification.cs:175).
#[test]
fn toolkit_add_takes_one_entry_object_as_the_mac_does() {
    let entry = r#"{ "id": "rg", "displayName": "ripgrep", "install": { "kind": "package", "manager": "npm", "name": "typescript" }, "approval": { "contentHash": "abc" }, "platforms": ["macos"] }"#;
    let added = file::parse_added_entry(entry).expect("one entry object is added");
    assert!(
        added.id == "rg"
            && added.approval.is_none()
            && matches!(&added.install, ToolkitInstallSpec::Package { name, .. } if name == "typescript")
    );
    assert!(file::parse_added_entry(ALL_FIVE_TEMPLATES).is_none(), "a whole toolkit.json is not one tool");
    assert!(file::parse_added_entry(&format!("[{entry}]")).is_none(), "an exported list is not one tool either");
    assert!(file::parse_added_entry(&entry.replace("\"platforms\"", "\"extra\"")).is_none(), "an unknown field refuses it");
    assert!(file::parse_added_entry(r#"{ "id": "x", "displayName": "X" }"#).is_none() && file::parse_added_entry("not json").is_none());
}

// ── ToolkitVerification ───────────────────────────────────────────────────────────────────────────────────────────────────

/// "toolkit platform rule matches macOS" (ToolkitVerification.cs:14).
#[test]
fn toolkit_platform_rule_matches_macos() {
    let brew = make_entry("brew-pkg", package("brew", "ripgrep", None));
    let script = make_entry("my-script", repo("https://github.com/x/y.git", "abc123", "install.sh"));
    for entry in [&brew, &script] {
        assert!(file::is_macos_only(entry) && !file::is_windows_platform(entry), "{} is macOS-only", entry.id);
    }
    for entry in [
        make_entry("p", plugin("org/repo", "repo@repo")),
        make_entry("m", mcp("my-mcp", "/usr/bin/node")),
        make_entry("s", skill("https://github.com/x/y.git")),
        make_entry("n", package("npm", "typescript", None)),
        make_entry("w", package("winget", "OpenJS.NodeJS", Some("node.exe"))),
    ] {
        assert!(!file::is_macos_only(&entry) && file::is_windows_platform(&entry), "{} runs on Windows", entry.id);
    }
}

/// "toolkit winget template decodes and builds its command" (ToolkitVerification.cs:41).
#[test]
fn toolkit_winget_template_decodes_and_builds_its_command() {
    let json = r#"{"version":1,"entries":[{"id":"nodejs","displayName":"Node.js","install":{"kind":"package","manager":"winget","name":"OpenJS.NodeJS","executable":"node.exe"}}]}"#;
    let parsed = file::parse(json).unwrap();
    assert_eq!(parsed.entries.len(), 1);
    assert_eq!(parsed.entries[0].install, package("winget", "OpenJS.NodeJS", Some("node.exe")));
    let home = common::temp("winget");
    let commands = runner::install_commands(&parsed.entries[0], None, &fake_context(&home));
    assert_eq!(commands.len(), 1);
    let command = &commands[0];
    assert_eq!(command[0], "winget");
    for part in [
        "--exact",
        "--id",
        "OpenJS.NodeJS",
        "--source",
        "winget",
        "--scope",
        "user",
        "--accept-source-agreements",
        "--accept-package-agreements",
        "--disable-interactivity",
    ] {
        assert!(command.contains(&part.to_string()), "has {part}");
    }
    let no_exe = r#"{"version":1,"entries":[{"id":"bad-winget","displayName":"Bad","install":{"kind":"package","manager":"winget","name":"Some.Package"}}]}"#;
    assert!(file::parse(no_exe).unwrap().entries.is_empty(), "winget without executable is skipped");
    for manager in ["brew", "npm"] {
        let with_exe = format!(
            r#"{{"version":1,"entries":[{{"id":"x","displayName":"X","install":{{"kind":"package","manager":"{manager}","name":"x","executable":"x.exe"}}}}]}}"#
        );
        assert!(file::parse(&with_exe).unwrap().entries.is_empty(), "{manager} with an executable field is rejected");
    }
    common::remove(&home);
}

/// "toolkit store keeps other-OS entries" (ToolkitVerification.cs:110).
#[tokio::test]
async fn toolkit_store_keeps_other_os_entries() {
    common::korean();
    let dir = common::temp("other-os");
    let home = common::temp("other-os-home");
    std::fs::write(
        dir.join("toolkit.json"),
        r#"{
  "version": 1,
  "entries": [
    { "displayName": "TypeScript", "id": "typescript", "install": { "kind": "package", "manager": "npm", "name": "typescript" } },
    { "displayName": "ripgrep", "id": "ripgrep", "install": { "kind": "package", "manager": "brew", "name": "ripgrep" } }
  ]
}"#,
    )
    .unwrap();
    let store = ToolkitStore::new(&dir);
    let (entries, error) = store.list();
    assert!(error.is_none());
    let listed = ids(&entries);
    assert!(listed.contains(&"typescript".to_string()) && !listed.contains(&"ripgrep".to_string()), "brew entry hidden from list");
    store.add(&make_entry("nodejs", package("winget", "OpenJS.NodeJS", Some("node.exe")))).unwrap();
    let listed = ids(&ToolkitStore::new(&dir).list().0);
    assert!(
        listed.contains(&"typescript".to_string()) && listed.contains(&"nodejs".to_string()) && !listed.contains(&"ripgrep".to_string())
    );
    let text = read(&dir.join("toolkit.json"));
    assert!(text.contains("ripgrep") && text.contains("brew"), "brew entry preserved in file bytes");
    ToolkitStore::new(&dir).remove("typescript").unwrap();
    let listed = ids(&ToolkitStore::new(&dir).list().0);
    assert!(!listed.contains(&"typescript".to_string()) && !listed.contains(&"ripgrep".to_string()));
    assert!(read(&dir.join("toolkit.json")).contains("ripgrep"), "brew entry byte-identical after remove");
    ToolkitStore::new(&dir)
        .import(
            r#"[
  { "displayName": "fd", "id": "fd", "install": { "kind": "package", "manager": "brew", "name": "fd" } },
  { "displayName": "Setup", "id": "setup", "install": { "kind": "repoScript", "url": "https://github.com/x/y.git", "ref": "v1", "scriptPath": "install.sh" } },
  { "displayName": "Prettier", "id": "prettier", "install": { "kind": "package", "manager": "npm", "name": "prettier" } }
]"#,
        )
        .unwrap();
    let store5 = ToolkitStore::new(&dir);
    let listed = ids(&store5.list().0);
    assert!(listed.contains(&"prettier".to_string()) && !listed.contains(&"fd".to_string()) && !listed.contains(&"setup".to_string()));
    let text = read(&dir.join("toolkit.json"));
    assert!(
        text.contains("\"fd\"") && text.contains("\"setup\"") && text.contains("ripgrep"),
        "imported other-OS entries written to the file"
    );
    let planned: Vec<String> = ToolkitRunner::new(&store5, fake_context(&home)).plan().into_iter().map(|i| i.entry.id).collect();
    assert!(!planned.iter().any(|id| ["fd", "setup", "ripgrep"].contains(&id.as_str())), "other-OS entries never planned");
    store5.approve("prettier", None).unwrap();
    let exported: Value = serde_json::from_str(&store5.export()).unwrap();
    let items = exported.as_array().expect("export is a JSON array like macOS");
    let exported_ids: Vec<&str> = items.iter().map(|e| e["id"].as_str().unwrap()).collect();
    for id in ["ripgrep", "fd", "setup", "prettier", "nodejs"] {
        assert!(exported_ids.contains(&id), "export includes {id}");
    }
    assert!(items.iter().all(|e| e.get("approval").is_none()), "export carries no approval");
    common::remove(&dir);
    common::remove(&home);
}

/// "toolkit approval binds to content hash" (ToolkitVerification.cs:206).
#[test]
fn toolkit_approval_binds_to_content_hash() {
    let dir = common::temp("approval");
    let store = ToolkitStore::new(&dir);
    let entry = make_entry("my-npm", package("npm", "typescript", None));
    store.add(&entry).unwrap();
    assert!(store.get_approval(&entry).is_none(), "unapproved is null");
    store.approve("my-npm", None).unwrap();
    let approval = store.get_approval(&entry).expect("approval set after Approve");
    assert_eq!(approval.content_hash.len(), 64, "SHA-256 is 64 hex chars");
    let changed = ToolkitFileEntry { display_name: "TypeScript (changed)".into(), ..entry.clone() };
    assert!(store.get_approval(&changed).is_none(), "approval invalid after entry change");
    store.add(&changed).unwrap();
    assert!(store.get_approval(&changed).is_none(), "approval cleared after re-add");
    store.approve("my-npm", None).unwrap();
    let renewed = store.get_approval(&changed).expect("re-approved");
    assert_ne!(renewed.content_hash, approval.content_hash, "new hash differs after content change");
    assert!(store.get_approval(&ToolkitStore::bundled()[0]).is_none(), "bundled entry approval is always null");
    let store2 = ToolkitStore::new(&dir);
    let loaded = store2.list().0.into_iter().find(|e| e.id == "my-npm").expect("entry reloaded");
    assert_eq!(store2.get_approval(&loaded).map(|a| a.content_hash), Some(renewed.content_hash), "approval hash survives reload");
    common::remove(&dir);
}

/// "toolkit plan runs only missing approved entries" (ToolkitVerification.cs:253).
#[tokio::test]
async fn toolkit_plan_runs_only_missing_approved_entries() {
    let dir = common::temp("plan");
    let home = common::temp("plan-home");
    let store = ToolkitStore::new(&dir);
    store.add(&make_entry("approved-npm", package("npm", "typescript", None))).unwrap();
    store.add(&make_entry("unapproved-npm", package("npm", "eslint", None))).unwrap();
    store.approve("approved-npm", None).unwrap();
    let runner = ToolkitRunner::new(&store, fake_context(&home));
    let plan = runner.plan();
    let action = |id: &str| plan.iter().find(|i| i.entry.id == id).map(|i| i.action);
    assert_eq!(action("approved-npm"), Some(PlanAction::Run), "approved gets Run action");
    assert_eq!(action("unapproved-npm"), Some(PlanAction::Skip), "unapproved gets Skip action");
    assert_eq!(action("mighty-styles"), Some(PlanAction::Run), "bundled entry in plan when missing");
    let executor = FakeExecutor::default();
    let results = runner.run(&plan, &executor).await;
    assert_eq!(results.iter().find(|r| r.entry_id == "unapproved-npm").map(|r| r.verdict), Some(Verdict::Skipped));
    assert!(executor.log().iter().any(|cmd| cmd.contains(&"typescript".to_string())), "approved entry commands ran");
    common::remove(&dir);
    common::remove(&home);
}

/// "toolkit probes are file-only and run twice and decide the result table" (ToolkitVerification.cs:301).
#[tokio::test]
async fn toolkit_probes_are_file_only_and_run_twice_and_decide_the_result_table() {
    let home = common::temp("probe-home");
    let local = common::temp("probe-local");
    let ctx = ToolkitProbeContext { home_directory: home.clone(), path_directories: vec![], local_app_data: local.clone() };
    let plugin_entry = make_entry("my-plugin", plugin("org/repo", "repo@repo"));
    assert_eq!(probe::probe(&plugin_entry, None, &ctx), ProbeResult::Missing, "plugin missing when no file");
    common::write(&home.join(".claude").join("plugins").join("installed_plugins.json"), r#"{"plugins":{"repo@repo":[{"scope":"user"}]}}"#);
    assert_eq!(probe::probe(&plugin_entry, None, &ctx), ProbeResult::Installed, "plugin installed when user-scope record present");
    let mcp_entry = make_entry("my-mcp", mcp("my-mcp", "/bin/node"));
    assert_eq!(probe::probe(&mcp_entry, None, &ctx), ProbeResult::Missing, "mcp missing when no file");
    std::fs::write(home.join(".claude.json"), r#"{"mcpServers":{"my-mcp":{}}}"#).unwrap();
    assert_eq!(probe::probe(&mcp_entry, None, &ctx), ProbeResult::Installed, "mcp installed when key present");
    let skill_entry = make_entry("my-skill", skill("https://github.com/example/my-skill.git"));
    assert_eq!(probe::probe(&skill_entry, None, &ctx), ProbeResult::Missing, "skill missing when no SKILL.md");
    common::write(&home.join(".claude").join("skills").join("my-skill").join("SKILL.md"), "# My Skill");
    assert_eq!(probe::probe(&skill_entry, None, &ctx), ProbeResult::Installed, "skill installed when SKILL.md present");
    let winget_entry = make_entry("nodejs", package("winget", "OpenJS.NodeJS", Some("node.exe")));
    assert_eq!(probe::probe(&winget_entry, None, &ctx), ProbeResult::Missing, "winget missing when executable absent");
    common::write(&local.join("Microsoft").join("WinGet").join("Links").join("node.exe"), "");
    assert_eq!(probe::probe(&winget_entry, None, &ctx), ProbeResult::Installed, "winget installed when executable in Links dir");
    for entry in [
        make_entry("ripgrep", package("brew", "ripgrep", None)),
        make_entry("my-script", repo("https://github.com/x/y.git", "abc123", "install.sh")),
    ] {
        assert_eq!(probe::probe(&entry, None, &ctx), ProbeResult::Missing, "{} always missing on Windows", entry.id);
    }
    common::remove(&home);
    common::remove(&local);
    // Run twice: the plan probes before; the run probes again after, and that one decides.
    let dir2 = common::temp("probe-twice");
    let home2 = common::temp("probe-twice-home");
    let store2 = ToolkitStore::new(&dir2);
    store2.add(&make_entry("probe-skill", skill("https://github.com/example/probe-skill.git"))).unwrap();
    store2.approve("probe-skill", None).unwrap();
    let runner2 = ToolkitRunner::new(&store2, fake_context(&home2));
    let plan2 = runner2.plan();
    assert!(plan2.iter().any(|i| i.entry.id == "probe-skill" && i.action == PlanAction::Run), "the plan's probe says missing");
    common::write(&home2.join(".claude").join("skills").join("probe-skill").join("SKILL.md"), "# Probe Skill");
    let results2 = runner2.run(&plan2, &FakeExecutor::default()).await;
    assert_eq!(
        results2.iter().find(|r| r.entry_id == "probe-skill").map(|r| r.verdict),
        Some(Verdict::Installed),
        "the run's own probe decides"
    );
    common::remove(&dir2);
    common::remove(&home2);
}

/// "toolkit plan is a step list" (ToolkitVerification.cs:396).
#[tokio::test]
async fn toolkit_plan_is_a_step_list() {
    let home = common::temp("steps-home");
    let ctx = fake_context(&home);
    let plugin_entry = make_entry("my-plugin", plugin("athens96/mighty-styles", "mighty-styles@mighty-styles"));
    let commands = runner::install_commands(&plugin_entry, None, &ctx);
    assert_eq!(commands.len(), 2, "plugin entry produces two install steps");
    assert_eq!(commands[0], ["claude", "plugin", "marketplace", "add", "--scope", "user", "athens96/mighty-styles"]);
    assert_eq!(commands[1], ["claude", "plugin", "install", "mighty-styles@mighty-styles", "--scope", "user", "--json"]);
    let dir = common::temp("steps");
    let store = ToolkitStore::new(&dir);
    store.add(&plugin_entry).unwrap();
    store.approve("my-plugin", None).unwrap();
    let plan = ToolkitRunner::new(&store, ctx.clone()).plan();
    let item = plan.iter().find(|i| i.entry.id == "my-plugin").expect("plugin entry is in the plan");
    assert!(item.commands.len() == 2 && item.commands.iter().all(|cmd| cmd.len() > 1), "each step is a full argv, not a single string");
    common::remove(&dir);
    // One failing step stops later steps of the same entry; later entries still run.
    let step_dir = common::temp("steps-fail");
    let step_store = ToolkitStore::new(&step_dir);
    step_store.add(&make_entry("step-plugin", plugin("org/step-plugin-test", "step@step"))).unwrap();
    step_store.approve("step-plugin", None).unwrap();
    step_store.add(&make_entry("second-npm", package("npm", "eslint", None))).unwrap();
    step_store.approve("second-npm", None).unwrap();
    let step_runner = ToolkitRunner::new(&step_store, ctx);
    let log = Arc::new(Mutex::new(Vec::new()));
    let results =
        step_runner.run(&step_runner.plan(), &SpecificFailExecutor { log: log.clone(), token: "org/step-plugin-test".into() }).await;
    let plugin_result = results.iter().find(|r| r.entry_id == "step-plugin").expect("step-plugin entry has a result");
    assert_eq!(plugin_result.steps.iter().map(|s| s.outcome).collect::<Vec<_>>(), [StepOutcome::Failed, StepOutcome::Skipped]);
    assert!(log.lock().unwrap().iter().any(|cmd| cmd.iter().any(|a| a.contains("eslint") || a == "npm")), "second entry ran");
    common::remove(&step_dir);
    common::remove(&home);
}

/// "bundled entry sorts first and survives an unreadable store" (ToolkitVerification.cs:475).
#[test]
fn bundled_entry_sorts_first_and_survives_an_unreadable_store() {
    common::korean();
    let dir = common::temp("unreadable");
    let path = dir.join("toolkit.json");
    std::fs::write(&path, "not-json{{{").unwrap();
    let store = ToolkitStore::new(&dir);
    let (entries, error) = store.list();
    assert_eq!(error, Some(strings::get("toolkit.error.unreadable")), "the app's own sentence for an unreadable file");
    assert!(entries.len() == 1 && entries[0].id == "mighty-styles" && entries[0].source == ToolkitEntrySource::Bundled);
    assert_eq!(std::fs::read(&path).unwrap(), b"not-json{{{", "toolkit.json is byte-identical after an unreadable load");
    common::remove(&dir);
}

/// "result table separates not attempted, failed and succeeded" (ToolkitVerification.cs:508).
#[tokio::test]
async fn result_table_separates_not_attempted_failed_and_succeeded() {
    let dir = common::temp("table");
    let home = common::temp("table-home");
    let store = ToolkitStore::new(&dir);
    store.add(&make_entry("unapproved-skill", skill("https://github.com/x/unapproved.git"))).unwrap();
    store.add(&make_entry("failing-mcp", mcp("failing-mcp", "/bin/node"))).unwrap();
    store.approve("failing-mcp", None).unwrap();
    store.add(&make_entry("succeeding", skill("https://github.com/x/succeeding.git"))).unwrap();
    store.approve("succeeding", None).unwrap();
    let runner = ToolkitRunner::new(&store, fake_context(&home));
    let results = runner.run(&runner.plan(), &SkillCreatingExecutor::default()).await;
    let verdict = |id: &str| results.iter().find(|r| r.entry_id == id).map(|r| r.verdict);
    assert_eq!(verdict("unapproved-skill"), Some(Verdict::Skipped), "not attempted");
    assert_eq!(verdict("failing-mcp"), Some(Verdict::Failed), "failed");
    assert_eq!(verdict("succeeding"), Some(Verdict::Installed), "succeeded");
    common::remove(&dir);
    common::remove(&home);
}

fn runtime_provider(id: &str, available: bool, version: Option<&str>) -> ProviderRuntime {
    ProviderRuntime {
        id: id.into(),
        name: id.into(),
        available,
        version: version.map(str::to_string),
        detail: strings::get(if available { "provider.available" } else { "provider.notInstalled" }),
        model_catalog: ModelCatalog::new("cli", vec![], ""),
        capabilities: ProviderCapabilities {
            effort: true,
            permission_modes: vec![],
            max_turns: true,
            max_budget_usd: true,
            resume: true,
            fast_mode: false,
            web_search: false,
            network_access: false,
            attachments: false,
        },
    }
}

fn runtime_info(providers: Vec<ProviderRuntime>) -> RuntimeInfo {
    RuntimeInfo {
        platform: "win32".into(),
        app_version: "1.0.0".into(),
        claude_available: providers.iter().any(|p| p.id == "claude" && p.available),
        claude_version: None,
        model_catalog: None,
        providers,
        mods: None,
    }
}

fn make_runtime(available: &[&str], extra: Vec<ProviderRuntime>) -> RuntimeInfo {
    let mut providers: Vec<ProviderRuntime> = mighty_core::wire::PROVIDERS
        .iter()
        .filter(|id| available.contains(id))
        .map(|id| runtime_provider(id, true, Some("2.1.271")))
        .collect();
    providers.extend(extra);
    runtime_info(providers)
}

fn row<'a>(rows: &'a [ComponentStatus], id: &str) -> &'a ComponentStatus {
    rows.iter().find(|row| row.id == id).unwrap()
}

/// "components rows follow installed CLIs" (ToolkitVerification.cs:581).
#[test]
fn components_rows_follow_installed_clis() {
    common::korean();
    let rows = components::section_rows(&make_runtime(&[], vec![]));
    assert_eq!(rows.len(), mighty_core::wire::PROVIDERS.len(), "a row per provider always");
    assert!(rows.iter().all(|row| row.state == "missing"), "missing when not in runtime");
    assert!(components::section_rows(&make_runtime(&mighty_core::wire::PROVIDERS, vec![])).iter().all(|row| row.state == "installed"));
    let old = components::section_rows(&make_runtime(&[], vec![runtime_provider("claude", true, Some("2.1.263"))]));
    assert!(
        row(&old, "claude").state == "attention" && row(&old, "claude").actions.iter().any(|a| a.id == "update"),
        "claude below Mods minimum"
    );
    let min = components::section_rows(&make_runtime(&["codex", "gemini"], vec![runtime_provider("claude", true, Some("2.1.271"))]));
    assert_eq!(row(&min, "claude").state, "installed", "claude at 2.1.271 is installed");
    assert_eq!(components::section_title(), strings::get("settings.components.sectionTitle"));
    assert!(row(&rows, "claude").actions.iter().any(|a| a.id == "copy-command"));
    assert!(components::install_command("claude").unwrap().contains("@anthropic-ai/claude-code"));
    assert!(components::install_command("codex").unwrap().contains("@openai/codex"));
    assert_eq!(components::install_command("gemini"), Some("irm https://antigravity.google/cli/install.ps1 | iex"));
}

/// "windows parity: component actions carry their own titles" (WindowsParityVerification.cs:119).
#[test]
fn windows_parity_component_actions_carry_their_own_titles() {
    common::korean();
    let provider = |id: &str, available: bool, version: Option<&str>| ProviderRuntime {
        detail: String::new(),
        ..runtime_provider(id, available, version)
    };
    let missing = components::section_rows(&runtime_info(vec![]));
    assert!(
        missing.iter().all(|row| row.actions.len() == 1
            && row.actions[0].id == "copy-command"
            && row.actions[0].title == strings::get("settings.components.copyInstallCommand")),
        "a missing CLI's button must say what it does"
    );
    let old = components::section_rows(&runtime_info(vec![
        provider("claude", true, Some("2.1.263")),
        provider("codex", true, Some("0.153.4")),
        provider("gemini", true, Some("1.0.0")),
    ]));
    let claude = row(&old, "claude");
    assert!(
        claude.actions.len() == 1
            && claude.actions[0].id == "update"
            && claude.actions[0].title == strings::get("settings.components.updateCLI")
    );
    let install =
        |id: &str| strings::get_with("settings.components.installInTerminal", &[("command", components::install_command(id).unwrap())]);
    assert!(
        missing.iter().all(|row| row.detail == strings::get("provider.fallback.checkInstall") + &install(&row.id)),
        "the Mac's fallback words and the command"
    );
    let absent =
        components::section_rows(&runtime_info(vec![ProviderRuntime { detail: "not found".into(), ..provider("claude", false, None) }]));
    assert_eq!(row(&absent, "claude").detail, format!("not found{}", install("claude")), "a missing CLI says its runtime's words");
    assert_eq!(
        claude.detail,
        strings::get_with("settings.components.modsNeedsVersion", &[("version", "2.1.271")]),
        "an old Claude names the Mods version"
    );
    let ready_runtime = runtime_info(vec![
        provider("claude", true, Some("2.1.271")),
        provider("codex", true, Some("0.153.4")),
        provider("gemini", true, Some("1.0.0")),
        provider("pi", true, Some("0.73.1")),
        provider("opencode", true, Some("opencode v2.2.7")),
        provider("hermes", true, Some("Hermes Agent v0.23.1 (2026.9.24)")),
    ]);
    let ready = components::section_rows(&ready_runtime);
    let ready_words = [
        "settings.components.readyClaude",
        "settings.components.readyCodex",
        "settings.components.ready",
        "settings.components.ready",
        "settings.components.ready",
        "settings.components.ready",
    ];
    assert_eq!(
        ready.iter().map(|row| row.detail.clone()).collect::<Vec<_>>(),
        ready_words.map(strings::get),
        "each ready CLI says its own ready sentence"
    );
    for language in mighty_core::locale::LANGUAGES {
        let catalogue = mighty_core::locale::catalogue(language);
        for key in ["settings.components.copyInstallCommand", "settings.components.updateCLI"] {
            for state in ["settings.components.statusMissing", "settings.components.statusAttention"] {
                let title = catalogue.get(key).cloned().unwrap_or_default();
                assert!(
                    !title.is_empty() && Some(&title) != catalogue.get(state),
                    "{language}: a button's title must not be a state word: {key}"
                );
            }
        }
    }
    let copied = components::copied_message("npm install -g x");
    assert!(
        copied == strings::get_with("settings.components.copied", &[("command", "npm install -g x")])
            && copied.contains("npm install -g x")
    );
    assert_eq!(components::RECHECK_INTERVAL, std::time::Duration::from_secs(5), "the open section looks again every 5 s");
    assert!(components::needs_recheck(&missing) && components::needs_recheck(&old));
    assert!(!components::needs_recheck(&ready), "with every CLI ready there is nothing to look for");
    // The Mods minimum comes from the runtime when it says one.
    let mut with_mods = runtime_info(vec![provider("claude", true, Some("2.1.263"))]);
    with_mods.mods = Some(ModsInfo { status: "x".into(), minimum_version: "9.9.9".into(), detail: String::new() });
    assert_eq!(
        row(&components::section_rows(&with_mods), "claude").detail,
        strings::get_with("settings.components.modsNeedsVersion", &[("version", "9.9.9")])
    );
}

/// "an old Claude is out of date, not missing" (WindowsCommandVerification.cs:81).
#[test]
fn an_old_claude_is_out_of_date_not_missing() {
    let provider = |id: &str, available: bool, version: Option<&str>| ProviderRuntime {
        detail: String::new(),
        ..runtime_provider(id, available, version)
    };
    let rows = components::section_rows(&runtime_info(vec![provider("claude", false, Some("2.1.263 (Claude Code)"))]));
    let claude = row(&rows, "claude");
    assert!(
        claude.state == "attention" && claude.actions.len() == 1 && claude.actions[0].id == "update",
        "an old Claude must offer its update"
    );
    let absent = components::section_rows(&runtime_info(vec![provider("claude", false, None)]));
    assert_eq!(row(&absent, "claude").state, "missing", "a Claude that is not there is missing");
}

/// "the toolkit executor fails a step that cannot start" (WindowsCommandVerification.cs:68), adapted from reading WinUI's source to
/// running the executor: a program that cannot start is that step failing with why (exit code -1), a failing one its output and
/// code, a step through the platform's `cli_request` and `ProcessHost` with the CLI environment and NoDefaultCurrentDirectoryInExePath.
#[tokio::test]
async fn the_toolkit_executor_fails_a_step_that_cannot_start() {
    let processes = Arc::new(FakeProcessHost::default());
    let environment = Arc::new(FakeShellEnvironment::new([("PATH".to_string(), "C:\\tools".to_string())].into()));
    let cwd = common::temp("executor");
    let resolved = cwd.join("npm.exe").to_string_lossy().into_owned();
    let found = resolved.clone();
    let executor = ProcessToolkitExecutor {
        processes: processes.clone(),
        environment,
        resolve: Arc::new(move |name: &str, _: &std::collections::BTreeMap<String, String>| (name == "npm").then(|| found.clone())),
        working_directory: cwd.clone(),
    };
    let missing = executor.run(&["winget".into(), "install".into()]).await;
    assert_eq!(missing.exit_code, -1, "a program that cannot start fails its step: {missing:?}");
    assert!(!missing.output.is_empty(), "with why");
    processes.push(FakeScript { stdout: b"out ".to_vec(), stderr: b"E404".to_vec(), exit_code: 3, hang: false });
    let failed = executor.run(&["npm".into(), "install".into(), "-g".into(), "x".into()]).await;
    assert_eq!(failed, CommandOutput::failure("out E404", 3), "a failing step keeps its output and code");
    let request = processes.spawned().last().cloned().unwrap();
    assert!(request.program == PathBuf::from(&resolved) && request.args == ["install", "-g", "x"] && request.cwd == cwd);
    assert_eq!(request.env.get("NoDefaultCurrentDirectoryInExePath").map(String::as_str), Some("1"));
    assert_eq!(request.env.get("PATH").map(String::as_str), Some("C:\\tools"), "the CLI environment the PATH was searched in");
    assert_eq!(executor.run(&[]).await, CommandOutput::success(), "an empty argv runs nothing");
    #[cfg(windows)]
    {
        let real = ProcessToolkitExecutor {
            processes: Arc::new(mighty_platform_windows::WindowsProcessHost::new()),
            environment: Arc::new(FakeShellEnvironment::new(Default::default())),
            resolve: Arc::new(|_: &str, _: &std::collections::BTreeMap<String, String>| {
                Some(common::fake_cli().to_string_lossy().into_owned())
            }),
            working_directory: cwd.clone(),
        };
        assert_eq!(real.run(&["fake".into(), "--exit".into(), "4".into(), "why".into()]).await, CommandOutput::failure("why", 4));
        assert_eq!(real.run(&["fake".into(), "--exit".into(), "0".into()]).await, CommandOutput::success());
    }
    common::remove(&cwd);
}

/// "toolkit file round-trip is a fixed point" (ToolkitVerification.cs:874).
#[test]
fn toolkit_file_round_trip_is_a_fixed_point() {
    let canonical = "{\n  \"entries\": [\n    {\"displayName\":\"TypeScript\",\"id\":\"npm-fp\",\"install\":{\"kind\":\"package\",\"manager\":\"npm\",\"name\":\"typescript\"}}\n  ],\n  \"version\": 1\n}\n";
    let dir = common::temp("fixed-point");
    let path = dir.join("toolkit.json");
    std::fs::write(&path, [&[0xEF, 0xBB, 0xBF][..], canonical.as_bytes()].concat()).unwrap();
    let store = ToolkitStore::new(&dir);
    store.add(&make_entry("_fp_dummy_", skill("https://github.com/x/dummy.git"))).unwrap();
    store.remove("_fp_dummy_").unwrap();
    let bytes = std::fs::read(&path).unwrap();
    assert_eq!(&bytes[..3], [0xEF, 0xBB, 0xBF], "written as C#'s File.WriteAllText(…, Encoding.UTF8) writes it");
    assert_eq!(std::str::from_utf8(&bytes[3..]).unwrap(), canonical, "canonical file is a fixed point of save");
    common::remove(&dir);
}

/// "step fetch eligibility is derived" (ToolkitVerification.cs:899).
#[tokio::test]
async fn step_fetch_eligibility_is_derived() {
    let argv = |parts: &[&str]| parts.iter().map(|p| p.to_string()).collect::<Vec<_>>();
    for fetch in [
        argv(&["git", "clone", "url", "dest"]),
        argv(&["git", "ls-remote", "url", "ref"]),
        argv(&["npm", "install", "-g", "pkg"]),
        argv(&["claude", "plugin", "marketplace", "add", "--scope", "user", "src"]),
        argv(&["claude", "plugin", "install", "id", "--scope", "user", "--json"]),
        argv(&["winget", "install", "--exact", "--id", "Pkg.Id"]),
    ] {
        assert!(runner::is_fetch_step(&fetch), "{fetch:?} is a fetch step");
    }
    for other in [argv(&["claude", "mcp", "add", "--scope", "user", "n", "--", "/bin/node"]), argv(&["git", "status"]), vec![]] {
        assert!(!runner::is_fetch_step(&other), "{other:?} is not a fetch step");
    }
    let home = common::temp("fetch-home");
    for (id, install, key, calls) in
        [("retry-npm", package("npm", "retry-pkg", None), "npm install", 2), ("retry-mcp", mcp("mymcp", "/bin/node"), "claude mcp", 1)]
    {
        let dir = common::temp(id);
        let store = ToolkitStore::new(&dir);
        store.add(&make_entry(id, install)).unwrap();
        store.approve(id, None).unwrap();
        let runner = ToolkitRunner::new(&store, fake_context(&home));
        let plan: Vec<ToolkitPlanItem> = runner.plan().into_iter().filter(|i| i.entry.id == id).collect();
        let executor = NetworkFailExecutor::default();
        runner.run(&plan, &executor).await;
        assert_eq!(executor.counts.lock().unwrap().get(key).copied().unwrap_or(0), calls, "{key} called {calls} times");
        common::remove(&dir);
    }
    common::remove(&home);
}

/// The fixture both platforms read (ToolkitVerification.cs:782, MightyCoreTests/ToolkitSharedFormatTests.swift).
const SHARED_FORMAT_FIXTURE: &str = r#"{
  "version": 1,
  "entries": [
    {
      "id": "shared-brew",
      "displayName": "ripgrep",
      "install": {"kind": "package", "manager": "brew", "name": "ripgrep"},
      "approval": {"contentHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
    },
    {
      "id": "shared-npm",
      "displayName": "TypeScript",
      "install": {"kind": "package", "manager": "npm", "name": "typescript"},
      "approval": {"contentHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
    },
    {
      "id": "shared-winget",
      "displayName": "Node.js",
      "install": {"kind": "package", "manager": "winget", "name": "OpenJS.NodeJS", "executable": "node.exe"},
      "approval": {"contentHash": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}
    }
  ]
}"#;

/// "shared-format fixture round-trip" (ToolkitVerification.cs:808).
#[test]
fn shared_format_fixture_round_trip() {
    let dir = common::temp("shared");
    std::fs::write(dir.join("toolkit.json"), SHARED_FORMAT_FIXTURE).unwrap();
    let store = ToolkitStore::new(&dir);
    store.add(&make_entry("tmp-dummy", skill("https://github.com/x/tmp.git"))).unwrap();
    store.remove("tmp-dummy").unwrap();
    let saved: Value = serde_json::from_str(&read(&dir.join("toolkit.json"))).unwrap();
    let entries = saved["entries"].as_array().unwrap();
    let find = |id: &str| entries.iter().find(|e| e["id"] == id).cloned().unwrap_or_else(|| panic!("{id} missing from saved file"));
    for (id, name, manager, package_name, executable, hash) in [
        ("shared-brew", "ripgrep", "brew", "ripgrep", None, 'a'),
        ("shared-npm", "TypeScript", "npm", "typescript", None, 'b'),
        ("shared-winget", "Node.js", "winget", "OpenJS.NodeJS", Some("node.exe"), 'c'),
    ] {
        let entry = find(id);
        assert_eq!(entry["displayName"], name, "{id} displayName preserved");
        assert!(
            entry["install"]["kind"] == "package" && entry["install"]["manager"] == manager && entry["install"]["name"] == package_name
        );
        assert_eq!(entry["install"].get("executable").and_then(Value::as_str), executable);
        assert_eq!(
            entry["approval"]["contentHash"].as_str(),
            Some(hash.to_string().repeat(64).as_str()),
            "{id} approval.contentHash preserved"
        );
    }
    assert!(entries.iter().all(|e| e["id"] != "tmp-dummy"), "removed dummy entry must not be in saved file");
    common::remove(&dir);
}

/// "shared-format platform table" (ToolkitVerification.cs:955).
#[test]
fn shared_format_platform_table() {
    let brew = make_entry("shared-brew", package("brew", "ripgrep", None));
    let npm = make_entry("shared-npm", package("npm", "typescript", None));
    let winget = make_entry("shared-winget", package("winget", "OpenJS.NodeJS", Some("node.exe")));
    assert!(file::is_macos_only(&brew) && !file::is_windows_platform(&brew));
    assert!(!file::is_macos_only(&npm) && file::is_windows_platform(&npm));
    assert!(!file::is_macos_only(&winget) && file::is_windows_platform(&winget));
}

/// The parity entries as C# builds them (`ParityEntry`: install kind "package" for the packages, "plugin" for the plugin).
fn parity_entries() -> Vec<ToolkitFileEntry> {
    vec![
        ToolkitFileEntry::new("shared-brew", "ripgrep", "package", package("brew", "ripgrep", None)),
        ToolkitFileEntry::new("shared-npm", "TypeScript", "package", package("npm", "typescript", None)),
        ToolkitFileEntry::new("shared-winget", "Node.js", "package", package("winget", "OpenJS.NodeJS", Some("node.exe"))),
        ToolkitFileEntry::new("shared-plugin", "My Plugin", "plugin", plugin("owner/repo", "myplugin@marketplace")),
    ]
}

fn hash_fixture() -> Vec<Value> {
    let text = std::fs::read_to_string(common::contracts().join("toolkit-hash-parity.json")).unwrap();
    serde_json::from_str::<Value>(&text).unwrap()["entries"].as_array().unwrap().clone()
}

/// "hash parity canonical bytes match fixture" (ToolkitVerification.cs:1052), against native/contracts/toolkit-hash-parity.json.
#[test]
fn hash_parity_canonical_bytes_match_fixture() {
    let fixture = hash_fixture();
    assert_eq!(fixture.len(), 4);
    for (entry, expected) in parity_entries().iter().zip(&fixture) {
        assert_eq!(expected["id"], entry.id.as_str());
        assert_eq!(store::build_canonical_json(entry), expected["canonicalBytes"].as_str().unwrap(), "{} canonical bytes", entry.id);
        // The fixture's own entry decoded the way a file is read hashes the same bytes.
        let decoded = file::parse_entry(expected).expect("the fixture's entry decodes");
        assert_eq!(store::build_canonical_json(&decoded), expected["canonicalBytes"].as_str().unwrap());
    }
}

/// "hash parity sha256 digests match fixture" (ToolkitVerification.cs:1072).
#[test]
fn hash_parity_sha256_digests_match_fixture() {
    let expected = [
        "60c2f1ee819f3e586e7b4edbc21e33f042f578dbf07e1d4a2b50c8ae81d38c4d",
        "6ed516dbfd00a154262de7a15ad2d96460ce255b15d4bfd25935909d1db1bdae",
        "17bb3a3b5145260f6a69293472356d16f24c5769c57c22cff177ac8e090f4764",
        "8b7b568e58fa4ecfad28cda421b8ba26a4943493892ddae27cef78da4453919f",
    ];
    for ((entry, digest), fixture) in parity_entries().iter().zip(expected).zip(hash_fixture()) {
        assert_eq!(store::canonical_hash(entry), digest, "{} sha256", entry.id);
        assert_eq!(fixture["sha256"], digest, "the fixture says the same");
    }
}

/// "hash parity stale platforms key excluded from hash surface" (ToolkitVerification.cs:1087).
#[test]
fn hash_parity_stale_platforms_key_excluded_from_hash_surface() {
    let without = ToolkitFileEntry::new("shared-winget", "Node.js", "package", package("winget", "OpenJS.NodeJS", Some("node.exe")));
    let json = r#"{"displayName":"Node.js","id":"shared-winget","install":{"executable":"node.exe","kind":"package","manager":"winget","name":"OpenJS.NodeJS"},"platforms":["windows"]}"#;
    let with = file::parse_entry(&serde_json::from_str(json).unwrap()).expect("ParseEntry returned null");
    assert_eq!(store::canonical_hash(&with), store::canonical_hash(&without), "stale platforms key must not change the hash");
    assert_eq!(store::canonical_hash(&with), "17bb3a3b5145260f6a69293472356d16f24c5769c57c22cff177ac8e090f4764");
}

/// "hash parity changing a hashed field changes the digest" (ToolkitVerification.cs:1104).
#[test]
fn hash_parity_changing_a_hashed_field_changes_the_digest() {
    let original = ToolkitFileEntry::new("shared-winget", "Node.js", "package", package("winget", "OpenJS.NodeJS", Some("node.exe")));
    let modified = ToolkitFileEntry::new("shared-winget", "Node.js", "package", package("winget", "OpenJS.NodeJS.LTS", Some("node.exe")));
    assert_ne!(store::canonical_hash(&original), store::canonical_hash(&modified));
}

/// "hash parity round-trip with stale platforms key is byte-identical" (ToolkitVerification.cs:1115).
#[test]
fn hash_parity_round_trip_with_stale_platforms_key_is_byte_identical() {
    let fixture = concat!(
        "{\n  \"entries\": [\n",
        "    {\"displayName\":\"ripgrep\",\"id\":\"shared-brew\",\"install\":{\"kind\":\"package\",\"manager\":\"brew\",\"name\":\"ripgrep\"}},\n",
        "    {\"displayName\":\"TypeScript\",\"id\":\"shared-npm\",\"install\":{\"kind\":\"package\",\"manager\":\"npm\",\"name\":\"typescript\"}},\n",
        "    {\"displayName\":\"Node.js\",\"id\":\"shared-winget\",\"install\":{\"executable\":\"node.exe\",\"kind\":\"package\",\"manager\":\"winget\",\"name\":\"OpenJS.NodeJS\"},\"platforms\":[\"windows\"]},\n",
        "    {\"displayName\":\"My Plugin\",\"id\":\"shared-plugin\",\"install\":{\"kind\":\"plugin\",\"pluginID\":\"myplugin@marketplace\",\"source\":\"owner/repo\"}}\n",
        "  ],\n  \"version\": 1\n}\n"
    );
    let dir = common::temp("stale-platforms");
    std::fs::write(dir.join("toolkit.json"), fixture).unwrap();
    let store = ToolkitStore::new(&dir);
    store.add(&make_entry("_hp_stale_dummy_", skill("https://github.com/x/dummy.git"))).unwrap();
    store.remove("_hp_stale_dummy_").unwrap();
    assert_eq!(read(&dir.join("toolkit.json")), fixture, "byte-identical after a save that does not touch original entries");
    common::remove(&dir);
}

/// "result truth is authoritative: probe decides verdict, steps are explanation" (ToolkitVerification.cs:1145).
#[tokio::test]
async fn result_truth_is_authoritative_probe_decides_verdict_steps_are_explanation() {
    // Case 1: all steps ok and the probe finds the file: Installed.
    let (dir1, home1) = (common::temp("truth1"), common::temp("truth1-home"));
    let ctx1 = fake_context(&home1);
    let store1 = ToolkitStore::new(&dir1);
    let plugin1 = make_entry("truth-plugin", plugin("org/repo", "truth@repo"));
    store1.add(&plugin1).unwrap();
    store1.approve("truth-plugin", None).unwrap();
    common::write(
        &home1.join(".claude").join("plugins").join("installed_plugins.json"),
        r#"{"plugins":{"truth@repo":[{"scope":"user"}]}}"#,
    );
    let plan1 =
        [ToolkitPlanItem { entry: plugin1.clone(), action: PlanAction::Run, commands: runner::install_commands(&plugin1, None, &ctx1) }];
    let results1 = ToolkitRunner::new(&store1, ctx1).run(&plan1, &FakeExecutor::default()).await;
    let r1 = &results1[0];
    assert!(
        r1.verdict == Verdict::Installed && r1.steps.iter().map(|s| s.outcome).collect::<Vec<_>>() == [StepOutcome::Ok, StepOutcome::Ok],
        "case 1"
    );
    // Case 2: all steps ok, the probe finds nothing: Failed, the steps still ok.
    let (dir2, home2) = (common::temp("truth2"), common::temp("truth2-home"));
    let ctx2 = fake_context(&home2);
    let store2 = ToolkitStore::new(&dir2);
    store2.add(&plugin1).unwrap();
    store2.approve("truth-plugin", None).unwrap();
    let plan2 =
        [ToolkitPlanItem { entry: plugin1.clone(), action: PlanAction::Run, commands: runner::install_commands(&plugin1, None, &ctx2) }];
    let results2 = ToolkitRunner::new(&store2, ctx2).run(&plan2, &FakeExecutor::default()).await;
    assert!(
        results2[0].verdict == Verdict::Failed
            && results2[0].steps.iter().all(|s| s.outcome == StepOutcome::Ok)
            && results2[0].steps.len() == 2,
        "case 2"
    );
    // Cases 3 and 4: a failing first or second step; the next entry still runs.
    for (case, source, plugin_id, outcomes, token) in [
        (3, "org/truth-fail-step0", "truth@repo", [StepOutcome::Failed, StepOutcome::Skipped], "org/truth-fail-step0"),
        (4, "org/repo", "truth@fail-step1", [StepOutcome::Ok, StepOutcome::Failed], "truth@fail-step1"),
    ] {
        let (dir, home) = (common::temp(&format!("truth{case}")), common::temp(&format!("truth{case}-home")));
        let store = ToolkitStore::new(&dir);
        store.add(&make_entry("truth-plugin", plugin(source, plugin_id))).unwrap();
        store.add(&make_entry("truth-npm", package("npm", "typescript", None))).unwrap();
        store.approve("truth-plugin", None).unwrap();
        store.approve("truth-npm", None).unwrap();
        let runner = ToolkitRunner::new(&store, fake_context(&home));
        let plan: Vec<ToolkitPlanItem> =
            runner.plan().into_iter().filter(|i| i.entry.id == "truth-plugin" || i.entry.id == "truth-npm").collect();
        let log = Arc::new(Mutex::new(Vec::new()));
        let results = runner.run(&plan, &SpecificFailExecutor { log: log.clone(), token: token.into() }).await;
        let plugin_result: &ToolkitRunItem = results.iter().find(|r| r.entry_id == "truth-plugin").unwrap();
        assert!(
            plugin_result.verdict == Verdict::Failed && plugin_result.steps.iter().map(|s| s.outcome).collect::<Vec<_>>() == outcomes,
            "case {case}"
        );
        assert!(
            results.iter().any(|r| r.entry_id == "truth-npm") && log.lock().unwrap().iter().any(|cmd| cmd.iter().any(|a| a == "npm")),
            "case {case}: the next entry ran"
        );
        common::remove(&dir);
        common::remove(&home);
    }
    // (e) A macOS-only entry written on the Mac with its approval is hidden, never planned, never run.
    let dir_e = common::temp("truth-e");
    let home_e = common::temp("truth-e-home");
    let brew = make_entry("macos-only-brew", package("brew", "ripgrep", None));
    std::fs::write(
        dir_e.join("toolkit.json"),
        format!(
            r#"{{"version":1,"entries":[{{"approval":{{"contentHash":"{}"}},"displayName":"macos-only-brew","id":"macos-only-brew","install":{{"kind":"package","manager":"brew","name":"ripgrep"}}}}]}}"#,
            store::canonical_hash(&brew)
        ),
    )
    .unwrap();
    let store_e = ToolkitStore::new(&dir_e);
    assert!(!store_e.list().0.iter().any(|e| e.id == "macos-only-brew"), "(e) brew hidden from the list on Windows");
    let runner_e = ToolkitRunner::new(&store_e, fake_context(&home_e));
    let plan_e = runner_e.plan();
    assert!(!plan_e.iter().any(|i| i.entry.id == "macos-only-brew"), "(e) brew absent from plan");
    let executor = FakeExecutor::default();
    let results_e = runner_e.run(&plan_e, &executor).await;
    assert!(
        !results_e.iter().any(|r| r.entry_id == "macos-only-brew") && !executor.log().iter().any(|cmd| cmd.iter().any(|a| a == "brew"))
    );
    for folder in [dir1, home1, dir2, home2, dir_e, home_e] {
        common::remove(&folder);
    }
}

/// "composer parity: the toolkit names its tools and says why a file failed" (ComposerParityVerification.cs:503).
#[test]
fn composer_parity_the_toolkit_names_its_tools_and_says_why_a_file_failed() {
    common::korean();
    let entry = |id: &str, name: &str| ToolkitFileEntry::new(id, name, "plugin", plugin(&format!("owner/{id}"), &format!("{id}@market")));
    let plan = [
        ToolkitPlanItem {
            entry: entry("a", "Tool A"),
            action: PlanAction::Run,
            commands: vec![
                ["claude", "plugin", "install", "a@market"].map(String::from).to_vec(),
                ["claude", "plugin", "enable", "a@market"].map(String::from).to_vec(),
            ],
        },
        ToolkitPlanItem { entry: entry("b", "Tool B"), action: PlanAction::Skip, commands: vec![] },
    ];
    let rows = presentation::plan_rows(&plan);
    assert!(
        rows.len() == 2
            && rows[0].name == "Tool A"
            && !rows[0].skipped
            && rows[0].lines == ["claude plugin install a@market", "claude plugin enable a@market"]
    );
    assert!(
        rows[1].name == "Tool B" && rows[1].skipped && rows[1].lines == [format!("→ {}", strings::get("settings.toolkit.verdictSkipped"))]
    );
    let run = ToolkitRunItem { entry_id: "a".into(), verdict: Verdict::Installed, steps: vec![] };
    assert!(presentation::result_name(&run, &[entry("a", "Tool A")]) == "Tool A" && presentation::result_name(&run, &[]) == "a");
    assert_eq!(presentation::error_banner("why it failed"), format!("{} why it failed", strings::get("settings.toolkit.errorBanner")));
    let dir = common::temp("why");
    let path = dir.join("toolkit.json");
    std::fs::write(&path, "not-json{{{").unwrap();
    let broken = ToolkitStore::new(&dir);
    let reason = broken.list().1;
    assert_eq!(reason, Some(strings::get("toolkit.error.unreadable")), "a file that cannot be parsed is reported in the app's words");
    assert_eq!(broken.add(&entry("c", "Tool C")).err(), reason, "a change to an unreadable file is refused with the same reason");
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "not-json{{{", "and the file is left as it was");
    std::fs::write(&path, r#"{"version":2,"entries":[]}"#).unwrap();
    assert_eq!(ToolkitStore::new(&dir).list().1, Some(strings::get("toolkit.error.unreadable")), "a file of another version is unreadable");
    std::fs::write(&path, r#"{"version":1,"entries":[]}"#).unwrap();
    assert_eq!(ToolkitStore::new(&dir).list().1, None, "a readable file reports nothing");
    common::remove(&dir);
}

/// Where the toolkit is kept: the profile's own only with `--allow-state-write` (W6 defaults).
#[test]
fn the_toolkit_is_kept_in_the_profile_only_with_writes() {
    let root = common::temp("store-folder");
    let paths = mighty_platform::AppPaths::rooted(&root);
    assert_eq!(mighty_tools::toolkit::store_folder(&paths, true), root.join("data"));
    assert_eq!(mighty_tools::toolkit::store_folder(&paths, false), root.join("local").join("toolkit").join("default"));
    common::remove(&root);
}
