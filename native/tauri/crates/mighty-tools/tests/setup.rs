//! 필요한 도구 on Windows (C# SetupVerification.cs, OpencodeHermesSetupVerification.cs, PiVerification.cs:278, AntigravityVerification
//! .cs:158's setup rows): the shared catalog's closed decoder, the install order, the PowerShell script and its folder, the probes
//! and the first-run rule, and the screen's state (MainWindow.Setup.cs, MainWindow.SetupSmoke.cs). Nothing is installed and no
//! script runs: the probes read fixture folders through a finder and an injected version reader, the folder goes through the
//! `SetupFiles` port (its fake everywhere, Windows' own on Windows), the install window is a recording one.
//!
//! Not here: `CliAccountTerminal.LaunchPlan` (SetupVerification.cs:168-174) is W6-A's terminal launcher; "the screen is wired into
//! the app" (L1560) becomes the host's registry and smoke checks in phase 2; `CliDiscovery.Files` and `HermesInstallFolder`'s
//! discovery half (L1558-1559) are mighty-platform-windows' (tests/cli_discovery.rs).

mod common;

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_platform::fake::RecordingTerminalLauncher;
use mighty_platform::setup::FakeSetupFiles;
use mighty_platform::{PrefsStore, SetupFiles};
use mighty_run::model::{ModelCatalog, ProviderCapabilities, ProviderRuntime, RuntimeInfo};
use mighty_run::strings;
use mighty_tools::setup::catalog::{self, CatalogError, SetupCatalog, SetupGroup, SetupPlatform, SetupProbe, SetupRecipe};
use mighty_tools::setup::components;
use mighty_tools::setup::files;
use mighty_tools::setup::plan;
use mighty_tools::setup::probe::{
    self, Finder, SKILL_INSTALLING_MARKER, SetupFirstRun, SetupProbeResult, SetupProbeState, SetupProber, SetupRowState, SetupRows,
    SetupRuntimeSync, VersionReader,
};
use mighty_tools::setup::screen::{self, Gate, InstallStart, Refresh, SetupScreen, WatchTick};
use mighty_tools::setup::script::{self, SetupScriptLabels};
use mighty_tools::setup::windows::{hermes_install_folder, well_known_folders};
use serde_json::{Map, Value, json};
use url::Url;

fn fixture_text() -> String {
    common::fixture("setup-catalog.json")
}

fn bundled() -> Arc<SetupCatalog> {
    SetupCatalog::bundled().expect("the bundled catalog decodes")
}

/// The fixture with `edit` applied to its first item (claude).
fn decode_editing(edit: impl FnOnce(&mut Map<String, Value>)) -> Result<SetupCatalog, CatalogError> {
    let mut root: Value = serde_json::from_str(&fixture_text()).unwrap();
    edit(root["items"][0].as_object_mut().unwrap());
    catalog::decode(&root.to_string())
}

fn set_windows(item: &mut Map<String, Value>, key: &str, value: Value) {
    item["platforms"]["windows"][key] = value;
}

fn refused(result: Result<SetupCatalog, CatalogError>, message: &str) {
    assert!(result.is_err(), "the decoder took {message}");
}

fn ids(items: &[String]) -> Vec<&str> {
    items.iter().map(String::as_str).collect()
}

fn set(items: &[&str]) -> HashSet<String> {
    items.iter().map(|s| s.to_string()).collect()
}

fn environment(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
    pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
}

fn windows_environment() -> BTreeMap<String, String> {
    environment(&[
        ("USERPROFILE", r"C:\Users\me"),
        ("LOCALAPPDATA", r"C:\Users\me\AppData\Local"),
        ("APPDATA", r"C:\Users\me\AppData\Roaming"),
        ("ProgramFiles", r"C:\Program Files"),
        ("SystemRoot", r"C:\Windows"),
    ])
}

fn labels() -> SetupScriptLabels {
    SetupScriptLabels {
        step: Box::new(|index, count, title| format!("[{index}/{count}] {title}")),
        failed: "failed's step".into(),
        done: "done".into(),
        close: "close".into(),
    }
}

fn plain_labels() -> SetupScriptLabels {
    SetupScriptLabels { failed: "failed".into(), ..labels() }
}

fn runtime(id: &str, name: &str, available: bool, version: Option<&str>) -> ProviderRuntime {
    ProviderRuntime {
        id: id.into(),
        name: name.into(),
        available,
        version: version.map(str::to_string),
        detail: String::new(),
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

/// Versions by full path (ignoring case, as Windows compares paths), with every path asked recorded. The checks over real folders
/// read through Windows' own finder, so off Windows nothing uses it.
#[cfg_attr(not(windows), allow(dead_code))]
#[derive(Clone, Default)]
struct Versions {
    known: Arc<Mutex<HashMap<String, Option<String>>>>,
    asked: Arc<Mutex<Vec<String>>>,
}

#[cfg_attr(not(windows), allow(dead_code))]
impl Versions {
    fn set(&self, path: &Path, version: Option<&str>) {
        self.known.lock().unwrap().insert(path.to_string_lossy().to_lowercase(), version.map(str::to_string));
    }
    fn reader(&self) -> VersionReader {
        let this = self.clone();
        Arc::new(move |path: String| {
            this.asked.lock().unwrap().push(path.clone());
            let answer = this.known.lock().unwrap().get(&path.to_lowercase()).cloned().flatten();
            Box::pin(async move { answer })
        })
    }
    fn asked(&self) -> Vec<String> {
        self.asked.lock().unwrap().clone()
    }
}

/// A finder over a fixed answer per name (the checks that do not look at real folders).
fn fixed_finder(copies: &[(&str, &[&str])]) -> Finder {
    let copies: HashMap<String, Vec<String>> =
        copies.iter().map(|(name, paths)| (name.to_string(), paths.iter().map(|p| p.to_string()).collect())).collect();
    Arc::new(move |name: &str, _: &BTreeMap<String, String>| copies.get(name).cloned().unwrap_or_default())
}

// ── SetupVerification ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// "setup: the catalog the app carries is the contract with a Windows entry per tool" (SetupVerification.cs:39).
#[test]
fn setup_the_catalog_the_app_carries_is_the_contract_with_a_windows_entry_per_tool() {
    common::korean();
    let catalog = bundled();
    let contract = catalog::decode(&fixture_text()).unwrap();
    assert_eq!(
        catalog.items.iter().map(|i| &i.id).collect::<Vec<_>>(),
        contract.items.iter().map(|i| &i.id).collect::<Vec<_>>(),
        "the embedded catalog must be the contract file"
    );
    let windows: Vec<&str> = catalog.items.iter().filter(|i| i.entry(SetupPlatform::Windows).is_some()).map(|i| i.id.as_str()).collect();
    assert_eq!(
        windows,
        ["claude", "codex", "agy", "pi", "opencode", "hermes", "git", "node", "uv", "ouroboros", "omc", "tailscale", "gh", "glab"]
    );
    for id in ["homebrew", "bun", "gstack"] {
        assert!(catalog.item(id).unwrap().entry(SetupPlatform::Windows).is_none(), "{id} stays a Mac tool");
    }
    assert_eq!(
        catalog.items.iter().filter(|i| i.required).map(|i| i.id.as_str()).collect::<Vec<_>>(),
        ["git"],
        "Git is the one required tool"
    );
    assert!(
        matches!(&catalog.item("claude").unwrap().entry(SetupPlatform::Windows).unwrap().install,
            SetupRecipe::OfficialScript { url, shell } if shell == "powershell" && url.as_str() == "https://claude.ai/install.ps1"),
        "Claude Code uses its native PowerShell installer"
    );
    assert_eq!(
        catalog.item("git").unwrap().entry(SetupPlatform::Windows).unwrap().install,
        SetupRecipe::Winget { id: "Git.Git".into(), scope: "user".into() },
        "Git installs with winget in user scope"
    );
    assert_eq!(
        catalog.item("node").unwrap().entry(SetupPlatform::Windows).unwrap().install,
        SetupRecipe::Winget { id: "OpenJS.NodeJS.LTS".into(), scope: "machine".into() },
        "Node.js has only a machine-scope installer"
    );
    assert!(
        matches!(&catalog.item("tailscale").unwrap().entry(SetupPlatform::Windows).unwrap().install,
            SetupRecipe::VendorPage { url } if url.as_str() == "https://tailscale.com/download/windows"),
        "Tailscale opens its download page"
    );
    assert_eq!(catalog.item("agy").unwrap().provider(), Some("gemini"));
    assert_eq!(catalog.item("codex").unwrap().provider(), Some("codex"));
    assert_eq!(catalog.item("claude").unwrap().provider(), Some("claude"), "the agent CLIs name their providers");
    // Every key the Windows rows read exists in every language.
    for language in mighty_core::locale::LANGUAGES {
        let strings = mighty_core::locale::catalogue(language);
        for item in catalog.items.iter().filter(|i| i.entry(SetupPlatform::Windows).is_some()) {
            let has = |key: &str| strings.get(key).is_some_and(|text| !text.is_empty());
            assert!(has(SetupCatalog::title_key(item)) && has(SetupCatalog::detail_key(item)), "{language} misses {}", item.id);
        }
        for group in SetupGroup::ALL {
            assert!(strings.contains_key(SetupCatalog::group_title_key(group)), "{language} misses a group title");
        }
    }
}

/// "setup: the decoder refuses what the Mac's refuses and keeps winget to Windows" (SetupVerification.cs:65).
#[test]
fn setup_the_decoder_refuses_what_the_macs_refuses_and_keeps_winget_to_windows() {
    let script = |url: &str, shell: &str| json!({"kind": "officialScript", "url": url, "shell": shell});
    refused(
        decode_editing(|i| set_windows(i, "install", script("https://example.com/install.ps1", "powershell"))),
        "a host outside the list",
    );
    refused(decode_editing(|i| set_windows(i, "install", script("http://claude.ai/install.ps1", "powershell"))), "plain http");
    refused(
        decode_editing(|i| set_windows(i, "install", script("https://claude.ai.example.com/install.ps1", "powershell"))),
        "a look-alike host",
    );
    refused(decode_editing(|i| set_windows(i, "install", script("https://claude.ai/install.ps1?x=$(id)", "powershell"))), "a query string");
    refused(decode_editing(|i| set_windows(i, "install", script("https://claude.ai/install.ps1", "bash"))), "Windows runs PowerShell");
    refused(decode_editing(|i| set_windows(i, "install", json!({"kind": "shell", "command": "irm x | iex"}))), "an unknown kind");
    refused(decode_editing(|i| set_windows(i, "probe", json!({"kind": "commandLineTools"}))), "a Mac probe on Windows");
    refused(decode_editing(|i| set_windows(i, "install", json!({"kind": "brewFormula", "name": "node"}))), "Homebrew on Windows");
    refused(
        decode_editing(|i| set_windows(i, "install", json!({"kind": "argv", "argv": ["xcode-select", "--install"]}))),
        "xcode-select on Windows",
    );
    refused(
        decode_editing(|i| set_windows(i, "install", json!({"kind": "argv", "argv": ["powershell", "-c", "id"]}))),
        "a program outside the list",
    );
    refused(
        decode_editing(|i| set_windows(i, "install", json!({"kind": "argv", "argv": ["npx", "--yes", "skills@1.7.2"]}))),
        "an option npx may not take",
    );
    refused(
        decode_editing(|i| set_windows(i, "install", json!({"kind": "argv", "argv": ["npx", "x;calc"]}))),
        "an argument with a separator",
    );
    refused(
        decode_editing(|i| {
            set_windows(
                i,
                "install",
                json!({"kind": "skill", "repository": "https://github.com/garrytan/gstack.git", "folder": "gstack", "commit": "a".repeat(40)}),
            )
        }),
        "a pinned skill's bash setup on Windows",
    );
    for marketplace in ["Q00/..", "./ouroboros", "someone/ouroboros"] {
        refused(
            decode_editing(|i| {
                set_windows(i, "install", json!({"kind": "claudePlugin", "marketplace": marketplace, "plugin": "ouroboros@ouroboros"}))
            }),
            &format!("marketplace {marketplace}"),
        );
    }
    for url in ["https://github.com/someone/gstack.git", "https://github.com/garrytan", "https://github.com/../garrytan/gstack"] {
        refused(decode_editing(|i| set_windows(i, "install", script(url, "powershell"))), &format!("repository {url}"));
    }
    for winget in [
        json!({"kind": "winget", "id": "Git.Git", "scope": "all"}),
        json!({"kind": "winget", "id": "git; calc", "scope": "user"}),
        json!({"kind": "winget", "id": "Git", "scope": "user"}),
        json!({"kind": "winget", "id": "Git.Git", "scope": "user", "override": "/S"}),
    ] {
        let text = winget.to_string();
        refused(decode_editing(|i| set_windows(i, "install", winget)), &format!("winget {text}"));
    }
    refused(
        decode_editing(|i| i["platforms"]["macos"]["install"] = json!({"kind": "winget", "id": "Git.Git", "scope": "user"})),
        "winget on the Mac",
    );
    refused(decode_editing(|i| i["label"] = json!("Install everything")), "a label the app does not know");
    refused(
        decode_editing(|i| {
            i.insert("extra".into(), json!(true));
        }),
        "an unknown key",
    );
    refused(decode_editing(|i| i["afterInstall"] = json!({"kind": "signIn", "provider": "someone"})), "an unknown provider");
    refused(decode_editing(|i| set_windows(i, "prerequisites", json!(["claude"]))), "an item needing itself");
    // claude → ouroboros, and ouroboros → claude, closes a loop on Windows.
    let mut root: Value = serde_json::from_str(&fixture_text()).unwrap();
    for item in root["items"].as_array_mut().unwrap() {
        if item["id"] == "claude" {
            item["platforms"]["windows"]["prerequisites"] = json!(["ouroboros"]);
        }
    }
    assert!(catalog::decode(&root.to_string()).is_err(), "a Windows cycle");
    let ok =
        decode_editing(|i| set_windows(i, "install", json!({"kind": "winget", "id": "Anthropic.ClaudeCode", "scope": "user"}))).unwrap();
    assert_eq!(
        ok.items[0].entry(SetupPlatform::Windows).unwrap().install,
        SetupRecipe::Winget { id: "Anthropic.ClaudeCode".into(), scope: "user".into() },
        "a winget recipe under windows decodes"
    );
    // An npx argv with the options npx may take, and a skill folder, decode; the step runs the program bare with every argument quoted.
    let skills = decode_editing(|i| {
        set_windows(
            i,
            "install",
            json!({"kind": "argv", "argv": ["npx", "skills@1.7.2", "add", "acme/kit", "--global", "--agent", "claude-code"]}),
        );
        set_windows(i, "probe", json!({"kind": "skill", "name": "kit"}));
    })
    .unwrap();
    let entry = skills.items[0].entry(SetupPlatform::Windows).unwrap().clone();
    assert_eq!(
        entry.install,
        SetupRecipe::Argv {
            arguments: ["npx", "skills@1.7.2", "add", "acme/kit", "--global", "--agent", "claude-code"].map(String::from).to_vec(),
            finishes_outside: false
        },
        "an npx argv recipe under windows decodes"
    );
    assert_eq!(entry.probe, SetupProbe::Skill { name: "kit".into() }, "a skill probe under windows decodes");
    assert_eq!(script::body(&entry).as_deref(), Some("npx 'skills@1.7.2' 'add' 'acme/kit' '--global' '--agent' 'claude-code'"));
}

/// "setup: prerequisites come first on Windows" (SetupVerification.cs:112).
#[test]
fn setup_prerequisites_come_first_on_windows() {
    let catalog = bundled();
    let order = |picked: &[&str]| plan::order(&set(picked), &catalog, SetupPlatform::Windows, &|_| false);
    assert_eq!(ids(&order(&["omc"])), ["claude", "omc"], "oh-my-claudecode needs Claude Code first");
    assert_eq!(ids(&order(&["ouroboros"])), ["claude", "uv", "ouroboros"], "Ouroboros needs Claude Code and uv first");
    assert_eq!(ids(&order(&["glab", "codex", "tailscale", "agy"])), ["codex", "agy", "tailscale", "glab"], "the catalog's order holds");
    assert!(order(&["homebrew", "gstack", "rm-rf"]).is_empty(), "a Mac tool or an unknown ID is never planned on Windows");
    assert_eq!(
        ids(&plan::order(&set(&["ouroboros"]), &catalog, SetupPlatform::Windows, &|id| id == "claude")),
        ["uv", "ouroboros"],
        "a prerequisite that is there is not planned"
    );
    let mut added: Vec<String> =
        plan::added_prerequisites(&set(&["ouroboros", "omc"]), &catalog, SetupPlatform::Windows, &|_| false).into_iter().collect();
    added.sort();
    assert_eq!(ids(&added), ["claude", "uv"], "the added prerequisites are the unpicked ones");
}

/// "setup: the script is PowerShell from the catalog alone" (SetupVerification.cs:125). The window it opens in
/// (`CliAccountTerminal.LaunchPlan`, lines 168-174) is W6-A's terminal launcher.
#[test]
fn setup_the_script_is_powershell_from_the_catalog_alone() {
    common::korean();
    assert_eq!(script::quote("plain"), "'plain'");
    assert_eq!(script::quote("it's $(id) `x` \"y\""), "'it''s $(id) `x` \"y\"'", "single quotes keep every value literal");
    assert_eq!(script::quote("a\u{2019}b"), "'a\u{2019}\u{2019}b'", "PowerShell's typographic quotes are doubled too");
    let environment = windows_environment();
    let catalog = bundled();
    let order = plan::order(&set(&["claude", "git", "ouroboros", "omc", "gh", "tailscale"]), &catalog, SetupPlatform::Windows, &|_| false);
    let steps = script::steps(&order, &catalog, &SetupCatalog::title);
    assert_eq!(
        steps.iter().map(|s| s.item_id.as_str()).collect::<Vec<_>>(),
        ["claude", "git", "uv", "ouroboros", "omc", "gh"],
        "a page-only item has no step"
    );
    let body = |id: &str| steps.iter().find(|s| s.item_id == id).unwrap().body.clone();
    assert_eq!(
        body("claude"),
        "Invoke-MightyScript 'https://claude.ai/install.ps1'",
        "an official script goes through the https-only fetch"
    );
    assert_eq!(
        body("git"),
        "winget install --exact --id 'Git.Git' --source winget --scope user --accept-package-agreements --accept-source-agreements"
    );
    assert!(body("gh").contains("--scope machine"), "gh is machine scope");
    assert_eq!(
        body("ouroboros"),
        "claude plugin marketplace add 'Q00/ouroboros'; claude plugin install 'ouroboros@ouroboros' --scope user"
    );
    assert_eq!(
        body("omc"),
        "claude plugin marketplace add 'https://github.com/Yeachan-Heo/oh-my-claudecode'; claude plugin install 'oh-my-claudecode@omc' --scope user"
    );
    let preamble = script::preamble(&environment);
    assert_eq!(preamble[0], "Set-Location -LiteralPath $HOME", "the home folder is the working folder first");
    assert!(
        preamble[1].starts_with("$env:Path += ';C:\\Users\\me") && preamble[1].contains("agy") && preamble[1].contains("Codex"),
        "{}",
        preamble[1]
    );
    assert!(preamble.contains(&"$env:npm_config_registry = 'https://registry.npmjs.org/'".to_string()), "npm's registry is pinned");
    let fetch = preamble.iter().find(|l| l.starts_with("function Get-MightyHttps")).unwrap();
    assert!(fetch.contains("AllowAutoRedirect = $false") && fetch.contains("$uri.Scheme -ne 'https'"), "each redirect is followed by hand");
    let run = preamble.iter().find(|l| l.starts_with("function Invoke-MightyScript")).unwrap();
    assert!(
        run.contains("[IO.FileMode]::CreateNew") && run.contains("-NoProfile -ExecutionPolicy Bypass -File $file"),
        "a fetched script runs as a new file"
    );
    let text = script::render(&steps, &labels(), &environment, r"C:\s\started-1", r"C:\s\done-1");
    assert!(
        text.contains("$mightyStarted = 'C:\\s\\started-1'") && text.contains("try { New-MightyMarker $mightyStarted;"),
        "the start marker is made first"
    );
    assert!(
        text.contains("function New-MightyMarker([string]$Path) { if (Test-Path -LiteralPath $Path) { throw"),
        "a marker is made only where nothing is"
    );
    assert!(fetch.contains("$memory.Length -gt 16777216") && !fetch.contains("ReadToEnd"), "a fetched script larger than 16 MB is refused");
    assert!(text.find("$mightyStarted").unwrap() < text.find("Set-Location").unwrap(), "the start marker comes before anything else runs");
    assert!(
        text.contains("} finally { try { New-MightyMarker $mightyDone } catch { }; $null = Read-Host 'close' }"),
        "the end marker is made however the steps end, and the window waits for Enter"
    );
    assert!(
        preamble.contains(
            &"$mightyPowerShell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\\v1.0\\powershell.exe'".to_string()
        ),
        "a fetched script runs in the system's PowerShell"
    );
    assert!(
        steps.iter().all(|s| text.contains(&format!("try {{ $global:LASTEXITCODE = 0; {}; if ($LASTEXITCODE)", s.body))),
        "each step runs in a try of its own"
    );
    assert!(text.contains("Write-Host 'failed''s step'"), "the labels are quoted like every other value");
    assert!(text.ends_with("exit $mightyFailed\r\n") && text.contains("\r\n"), "CRLF line ends, one after the last line");
    let localized = SetupScriptLabels::localized();
    assert_eq!(localized.close, strings::get("setup.windows.script.close"));
    assert_eq!((localized.step)(2, 5, "uv"), "[2/5] uv", "the script speaks the app's language");
    for word in ["RunAs", "sudo", "Start-Process", "-Verb", "Get-Credential", "Set-ExecutionPolicy"] {
        assert!(!text.to_lowercase().contains(&word.to_lowercase()), "the script never elevates or asks for a password: {word}");
    }
    // The system folder Windows reports wins; without one, SystemRoot's System32.
    let system = r"C:\WINDOWS\system32";
    assert_eq!(
        script::launch_arguments(r"C:\s\install-1.ps1", Some(system), &environment),
        [
            r"C:\WINDOWS\system32\WindowsPowerShell\v1.0\powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            r"C:\s\install-1.ps1"
        ],
        "the system's PowerShell runs the file with no profile and this process's policy only"
    );
    assert_eq!(script::power_shell(None, &environment), r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
    assert_eq!(script::power_shell(None, &BTreeMap::new()), r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
    #[cfg(windows)]
    {
        let reported = mighty_platform_windows::WindowsSetupFiles.system_folder().expect("Windows names its system folder");
        assert_eq!(script::power_shell(Some(&reported), &environment), format!(r"{reported}\WindowsPowerShell\v1.0\powershell.exe"));
    }
}

/// The checks of "setup: the script folder is ours and markers never follow links" (SetupVerification.cs:178) over one port.
fn folder_checks(files: &dyn SetupFiles, name: &str) {
    let root = common::temp(name);
    assert_eq!(
        files::folder(None, Path::new(r"C:\Users\me\AppData\Local")),
        Path::new(r"C:\Users\me\AppData\Local").join("MightyCode").join("setup")
    );
    assert_eq!(
        files::folder(Some(&root), Path::new("x")),
        root.join("setup"),
        "the folder is the app's machine-local one, or the profile's"
    );
    let folder = root.join("setup");
    files::prepare_directory(files, &folder).expect("the folder is prepared");
    let script = files::write_script("Write-Host 'ツール'\r\n", &folder).unwrap();
    let bytes = std::fs::read(&script).unwrap();
    assert_eq!(&bytes[..3], [0xEF, 0xBB, 0xBF], "the script is UTF-8 with its mark");
    assert_eq!(std::str::from_utf8(&bytes[3..]).unwrap(), "Write-Host 'ツール'\r\n");
    assert!(script.file_name().unwrap().to_string_lossy().starts_with("install-"));
    assert_ne!(files::write_script("x", &folder).unwrap(), script, "each script has a new random name");
    // A link where the folder should be is refused, and so is a marker that is a link.
    let elsewhere = root.join("elsewhere");
    std::fs::create_dir_all(&elsewhere).unwrap();
    let linked = root.join("linked");
    common::directory_link(&linked, &elsewhere);
    assert!(files::prepare_directory(files, &linked).is_err(), "a link in place of the setup folder must be refused");
    let (started, done) = files::markers(&folder);
    assert!(
        !files::marker_exists(files, &started) && !files::run_ended(files, &started, &done, Some(&|_| true)),
        "a run that has not started has not ended"
    );
    common::directory_link(&done, &elsewhere);
    assert!(!files::marker_exists(files, &done) && !files::run_ended(files, &started, &done, Some(&|_| true)), "a link is no marker");
    // A directory link goes with remove_dir on Windows and is unlinked elsewhere.
    if std::fs::remove_dir(&done).is_err() {
        std::fs::remove_file(&done).unwrap();
    }
    std::fs::create_dir(&started).unwrap();
    assert!(
        files::runner_process_id(files, &started).is_none() && !files::run_ended(files, &started, &done, Some(&|_| false)),
        "until the script wrote its pid the run goes on"
    );
    std::fs::write(started.join("pid"), "4242\r\n").unwrap();
    assert_eq!(files::runner_process_id(files, &started), Some(4242), "the script's PowerShell is read from the start marker");
    assert!(!files::run_ended(files, &started, &done, Some(&|id| id == 4242)), "a run whose PowerShell is alive goes on");
    assert!(files::run_ended(files, &started, &done, Some(&|_| false)), "a run whose window was closed has ended");
    std::fs::create_dir(&done).unwrap();
    assert!(files::run_ended(files, &started, &done, Some(&|_| true)), "the end marker ends the run");
    // Leftovers go; a link is removed itself and what it points to stays.
    std::fs::write(elsewhere.join("keep.txt"), "x").unwrap();
    common::directory_link(&folder.join("started-old"), &elsewhere);
    files::remove_leftovers(files, &folder);
    assert!(
        std::fs::read_dir(&folder).unwrap().next().is_none() && elsewhere.join("keep.txt").exists(),
        "leftovers are removed without following a link"
    );
    let _ = std::fs::remove_dir(&linked).or_else(|_| std::fs::remove_file(&linked));
    common::remove(&root);
}

/// "setup: the script folder is ours and markers never follow links" (SetupVerification.cs:178), over the port's fake and, on
/// Windows, Windows' own (the user-only access list written for real).
#[test]
fn setup_the_script_folder_is_ours_and_markers_never_follow_links() {
    let fake = FakeSetupFiles::default();
    folder_checks(&fake, "folder-fake");
    // The port's process check stands in when the check gives none.
    let root = common::temp("folder-running");
    let (started, done) = files::markers(&root);
    std::fs::create_dir(&started).unwrap();
    std::fs::write(started.join("pid"), "77").unwrap();
    fake.set_running(77, true);
    assert!(!files::run_ended(&fake, &started, &done, None), "the port says its PowerShell runs");
    fake.set_running(77, false);
    assert!(files::run_ended(&fake, &started, &done, None), "the port says it is gone");
    common::remove(&root);
    #[cfg(windows)]
    folder_checks(&mighty_platform_windows::WindowsSetupFiles, "folder-windows");
}

/// "setup: probes read the PATH, the installers' folders, plugins and skills" (SetupVerification.cs:223). The programs are found
/// by Windows' own finder (mighty-platform-windows `setup_probe::executables`), so that half runs on Windows; plugins, skills and the
/// version rules run everywhere.
#[tokio::test]
async fn setup_probes_read_the_path_the_installers_folders_plugins_and_skills() {
    let root = common::temp("probes");
    let home = root.join("home");
    std::fs::create_dir_all(&home).unwrap();
    #[cfg(windows)]
    {
        let bin = root.join("bin");
        let other = root.join("other");
        let local = root.join("local");
        for folder in [&bin, &other, &local.join("agy").join("bin")] {
            std::fs::create_dir_all(folder).unwrap();
        }
        std::fs::write(bin.join("claude.exe"), "").unwrap();
        std::fs::write(other.join("claude.exe"), "").unwrap();
        std::fs::write(bin.join("npm.cmd"), "").unwrap();
        // agy is not on the PATH yet: its installer's folder is looked in.
        std::fs::write(local.join("agy").join("bin").join("agy.exe"), "").unwrap();
        let (path, home_text, local_text) =
            (format!("{};{}", bin.display(), other.display()), home.to_string_lossy().into_owned(), local.to_string_lossy().into_owned());
        let env = environment(&[("Path", path.as_str()), ("USERPROFILE", home_text.as_str()), ("LOCALAPPDATA", local_text.as_str())]);
        let versions = Versions::default();
        versions.set(&bin.join("claude.exe"), None);
        versions.set(&other.join("claude.exe"), Some("2.1.300 (Claude Code)"));
        let finder: Finder = Arc::new(mighty_platform_windows::cli_copies);
        let prober = SetupProber::new(env.clone(), finder.clone(), versions.reader());
        let claude = prober.probe(&SetupProbe::Executable { name: "claude".into(), minimum_version: Some("2.1.271".into()) }).await;
        assert!(
            claude.state == SetupProbeState::Installed
                && claude.version.as_deref() == Some("2.1.300 (Claude Code)")
                && versions.asked().len() == 2,
            "a copy that will not say its version falls back to the next one: {claude:?}"
        );
        let npm = prober.executables("npm");
        assert!(npm.len() == 1 && npm[0].to_lowercase().ends_with("npm.cmd"), "a .cmd shim counts as the program: {npm:?}");
        assert_eq!(
            prober.probe(&SetupProbe::Executable { name: "agy".into(), minimum_version: None }).await.state,
            SetupProbeState::Installed
        );
        assert_eq!(
            prober.probe(&SetupProbe::Executable { name: "codex".into(), minimum_version: None }).await.state,
            SetupProbeState::Missing
        );
        versions.set(&other.join("claude.exe"), Some("2.0.0 (Claude Code)"));
        let old = prober.probe(&SetupProbe::Executable { name: "claude".into(), minimum_version: Some("2.1.271".into()) }).await;
        assert!(
            old.state == SetupProbeState::Outdated
                && old.minimum.as_deref() == Some("2.1.271")
                && old.shown_version() == Some("2.0.0 (Claude Code)")
                && !old.is_installed(),
            "an old Claude is out of date, not missing"
        );
        versions.set(&other.join("claude.exe"), None);
        let unread = prober.probe(&SetupProbe::Executable { name: "claude".into(), minimum_version: Some("2.1.271".into()) }).await;
        assert!(unread.state == SetupProbeState::Unverified && unread.is_installed(), "with no version read it counts as there, unchecked");
        let always: VersionReader = Arc::new(|_| Box::pin(async { Some("1.0.0".to_string()) }));
        let all = SetupProber::new(env, finder, always).probe_all(&bundled(), Some(&set(&["agy", "homebrew"]))).await;
        assert_eq!(all.keys().collect::<Vec<_>>(), ["agy"], "only the asked items with a Windows entry are probed");
    }
    assert!(!probe::meets("2.1.300-beta.1", "2.1.271") && probe::meets("v2.1.271", "2.1.271") && !probe::meets("2.1.9", "2.1.271"));
    // Plugins by their whole name in user scope; skills by SKILL.md and the setup markers.
    let home_text = home.to_string_lossy().into_owned();
    let env = environment(&[("USERPROFILE", home_text.as_str())]);
    let none: VersionReader = Arc::new(|_| Box::pin(async { None }));
    let prober = SetupProber::new(env, fixed_finder(&[]), none);
    common::write(
        &home.join(".claude").join("plugins").join("installed_plugins.json"),
        r#"{"plugins":{"ouroboros@ouroboros":[{"scope":"user"}],"omc-extra@x":[{"scope":"user"}],"oh-my-claudecode@omc":[{"scope":"project"}]}}"#,
    );
    let plugin = |prefix: &str| SetupProbe::ClaudePlugin { prefix: prefix.into() };
    assert!(prober.probe(&plugin("ouroboros@")).await.is_installed(), "a user-scope plugin counts");
    assert!(!prober.probe(&plugin("omc@")).await.is_installed(), "a longer name is another plugin");
    assert!(!prober.probe(&plugin("oh-my-claudecode@")).await.is_installed(), "a project-scope record does not count");
    let skill = home.join(".claude").join("skills").join("kit");
    common::write(&skill.join("SKILL.md"), "x");
    assert!(prober.probe(&SetupProbe::Skill { name: "kit".into() }).await.is_installed(), "a skill the user placed counts");
    std::fs::write(skill.join(SKILL_INSTALLING_MARKER), "").unwrap();
    assert!(
        !prober.probe(&SetupProbe::Skill { name: "kit".into() }).await.is_installed(),
        "a placed skill counts only once its setup finished"
    );
    common::remove(&root);
}

/// "setup: rows and the first run follow the probes" (SetupVerification.cs:297).
#[test]
fn setup_rows_and_the_first_run_follow_the_probes() {
    let catalog = bundled();
    let missing = SetupProbeResult::missing();
    let there = SetupProbeResult::installed(Some("1"));
    assert_eq!(SetupRows::derive(None, false, true), SetupRowState::Checking);
    assert_eq!(SetupRows::derive(Some(&missing), true, false), SetupRowState::Installing);
    assert_eq!(SetupRows::derive(Some(&missing), true, true), SetupRowState::Failed);
    assert_eq!(SetupRows::derive(Some(&missing), false, true), SetupRowState::Missing);
    assert_eq!(
        SetupRows::derive(Some(&SetupProbeResult::new(SetupProbeState::Outdated, Some("1"), Some("2"))), false, true),
        SetupRowState::Outdated
    );
    assert!(SetupRows::is_selectable(SetupRowState::Failed, catalog.item("git").unwrap()), "a failed item can be picked again");
    assert!(!SetupRows::is_selectable(SetupRowState::Outdated, catalog.item("claude").unwrap()), "an old agent CLI gets its update");
    assert!(!SetupRows::is_selectable(SetupRowState::Missing, catalog.item("tailscale").unwrap()), "a page is no install");
    let mut results: HashMap<String, SetupProbeResult> =
        catalog.items.iter().filter(|i| i.entry(SetupPlatform::Windows).is_some()).map(|i| (i.id.clone(), missing.clone())).collect();
    assert!(SetupFirstRun::should_present(&catalog, &results, false) && !SetupFirstRun::should_present(&catalog, &results, true));
    let sorted = |picked: HashSet<String>| {
        let mut picked: Vec<String> = picked.into_iter().collect();
        picked.sort();
        picked
    };
    assert_eq!(
        ids(&sorted(SetupFirstRun::preselection(&catalog, &results))),
        ["claude", "git"],
        "the first showing ticks Git and Claude Code"
    );
    results.insert("codex".into(), there.clone());
    results.insert("git".into(), there.clone());
    assert!(
        !SetupFirstRun::should_present(&catalog, &results, false) && SetupFirstRun::preselection(&catalog, &results).is_empty(),
        "Git and any one agent CLI are enough"
    );
    results.insert("git".into(), missing.clone());
    assert!(SetupFirstRun::should_present(&catalog, &results, false));
    assert_eq!(ids(&sorted(SetupFirstRun::preselection(&catalog, &results))), ["git"], "a missing Git opens the screen and is ticked");
    results.remove("glab");
    assert!(!SetupFirstRun::should_present(&catalog, &results, false), "until every item has been probed nothing opens");
    let runtime = [runtime("claude", "Claude Code", true, Some("2.1.300"))];
    let claude: HashMap<String, SetupProbeResult> = [("claude".to_string(), SetupProbeResult::installed(Some("2.1.300")))].into();
    let codex: HashMap<String, SetupProbeResult> = [("codex".to_string(), SetupProbeResult::installed(Some("0.160.0")))].into();
    assert!(
        !SetupRuntimeSync::is_stale(&runtime, &catalog, &claude) && SetupRuntimeSync::is_stale(&runtime, &catalog, &codex),
        "a CLI that appeared makes the runtime stale"
    );
}

/// "다시 보지 않기" is the PrefsStore's `setup.neverShow` DWORD (SetupFirstRun.NeverShow/SetNeverShow over the registry).
#[test]
fn setup_never_show_is_the_shared_setting() {
    let prefs = mighty_platform::fake::MemoryPrefs::default();
    assert!(!SetupFirstRun::never_show(&prefs), "nothing stored is off");
    prefs.set("setup.neverShow", mighty_platform::PrefValue::Text("1".into())).unwrap();
    assert!(!SetupFirstRun::never_show(&prefs), "only a DWORD counts");
    SetupFirstRun::set_never_show(&prefs).unwrap();
    assert!(SetupFirstRun::never_show(&prefs) && prefs.get("setup.neverShow").unwrap() == Some(mighty_platform::PrefValue::Int(1)));
}

// ── OpencodeHermesSetupVerification ───────────────────────────────────────────────────────────────────────────────────────

const HERMES_VERSION: &str = "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882";

fn hermes_output() -> String {
    format!(
        "{HERMES_VERSION}\nInstall directory: C:\\Users\\me\\AppData\\Local\\hermes\\hermes-agent\nInstall method: git\nPython: 3.14.9\nOpenAI SDK: 2.54.0\nUp to date\n"
    )
}

/// "setup: opencode and Hermes are in the contract with a Windows entry beside the Mac's" (OpencodeHermesSetupVerification.cs:39).
#[test]
fn setup_opencode_and_hermes_are_in_the_contract_with_a_windows_entry_beside_the_macs() {
    let catalog = catalog::decode(&fixture_text()).unwrap();
    let agents: Vec<&str> = catalog.items.iter().filter(|i| i.group == SetupGroup::Agent).map(|i| i.id.as_str()).collect();
    assert_eq!(agents, ["claude", "codex", "agy", "pi", "opencode", "hermes"], "opencode and Hermes follow pi among the agents");
    let opencode = catalog.item("opencode").unwrap();
    let hermes = catalog.item("hermes").unwrap();
    assert!(opencode.provider() == Some("opencode") && hermes.provider() == Some("hermes") && !opencode.required && !hermes.required);
    assert!(opencode.label_key == "setup.item.opencode.title" && opencode.description_key == "setup.item.opencode.detail");
    assert!(hermes.label_key == "setup.item.hermes.title" && hermes.description_key == "setup.item.hermes.detail", "the Mac's keys");
    for platform in SetupPlatform::ALL {
        let entry = opencode.entry(platform).unwrap();
        assert_eq!(entry.probe, SetupProbe::Executable { name: "opencode".into(), minimum_version: Some("2.0.0".into()) });
        assert_eq!(entry.install, SetupRecipe::NpmGlobal { package: "@opencode/cli".into() });
        assert_eq!(entry.prerequisites, ["node"]);
        assert_eq!(
            entry.source,
            Url::parse("https://github.com/anomalyco/opencode").unwrap(),
            "OpenCode 2 from npm after Node.js on {platform:?}"
        );
    }
    let mac = hermes.entry(SetupPlatform::MacOS).unwrap();
    let windows = hermes.entry(SetupPlatform::Windows).unwrap();
    assert_eq!(mac.probe, SetupProbe::Executable { name: "hermes".into(), minimum_version: Some("0.23.0".into()) });
    assert!(
        matches!(&mac.install, SetupRecipe::OfficialScript { url, shell } if shell == "bash" && url.as_str() == "https://hermes-agent.nousresearch.com/install.sh")
    );
    assert_eq!(mac.prerequisites, ["git"], "the Mac's entry stays as it was");
    assert_eq!(windows.probe, SetupProbe::Executable { name: "hermes".into(), minimum_version: Some("0.23.0".into()) });
    assert!(
        matches!(&windows.install, SetupRecipe::OfficialScript { url, shell } if shell == "powershell" && url.as_str() == "https://hermes-agent.nousresearch.com/install.ps1")
    );
    assert!(
        windows.prerequisites.is_empty() && windows.source == Url::parse("https://github.com/NousResearch/hermes-agent").unwrap(),
        "nothing goes first on Windows"
    );
    for key in ["setup.item.opencode.title", "setup.item.opencode.detail", "setup.item.hermes.title", "setup.item.hermes.detail"] {
        assert!(SetupCatalog::KNOWN_KEYS.contains(&key));
    }
    assert!(
        mighty_core::wire::PROVIDERS.contains(&"opencode") && mighty_core::wire::PROVIDERS.contains(&"hermes"),
        "the keys and providers are known"
    );
    assert!(
        SetupCatalog::ALLOWED_HOSTS.contains(&"hermes-agent.nousresearch.com")
            && SetupCatalog::ALLOWED_REPOSITORIES.contains(&"NousResearch/hermes-agent")
    );
    let carried = bundled();
    assert!(
        carried.item("opencode").unwrap().entry(SetupPlatform::Windows).is_some()
            && carried.item("hermes").unwrap().entry(SetupPlatform::Windows).is_some()
    );
    assert!(
        SetupCatalog::title_key(opencode) == "setup.item.opencode.title"
            && SetupCatalog::detail_key(opencode) == "setup.item.opencode.detail"
    );
    assert!(
        SetupCatalog::title_key(hermes) == "setup.item.hermes.title"
            && SetupCatalog::detail_key(hermes) == "setup.windows.item.hermes.detail"
    );
    for language in mighty_core::locale::LANGUAGES {
        let detail = mighty_core::locale::catalogue(language).get("setup.windows.item.hermes.detail").cloned().unwrap_or_default();
        assert!(
            detail.contains(r"%LOCALAPPDATA%\hermes") && !detail.contains("~/.hermes") && detail.contains("PowerShell"),
            "{language}: Hermes's Windows detail"
        );
    }
}

/// "setup: opencode's step removes OpenCode 1 first and an old opencode may be installed again" (OpencodeHermesSetupVerification.cs:86).
#[test]
fn setup_opencodes_step_removes_opencode_1_first_and_an_old_opencode_may_be_installed_again() {
    common::korean();
    let catalog = bundled();
    let opencode = catalog.item("opencode").unwrap();
    assert_eq!(
        script::body(opencode.entry(SetupPlatform::Windows).unwrap()).as_deref(),
        Some("npm uninstall -g 'opencode-ai' *> $null; npm install -g '@opencode/cli'")
    );
    assert!(
        SetupCatalog::NPM_REPLACES.len() == 1 && SetupCatalog::npm_replaces("@opencode/cli") == Some("opencode-ai"),
        "only OpenCode 2 replaces a package"
    );
    assert_eq!(
        script::body(catalog.item("pi").unwrap().entry(SetupPlatform::Windows).unwrap()).as_deref(),
        Some("npm install -g '@mariozechner/pi-coding-agent'")
    );
    assert_eq!(ids(&plan::order(&set(&["opencode"]), &catalog, SetupPlatform::Windows, &|_| false)), ["node", "opencode"]);
    assert_eq!(ids(&plan::order(&set(&["opencode"]), &catalog, SetupPlatform::Windows, &|_| true)), ["opencode"]);
    let old = SetupProbeResult::new(SetupProbeState::Outdated, Some("1.19.5"), Some("2.0.0"));
    assert_eq!(SetupRows::derive(Some(&old), false, true), SetupRowState::Outdated);
    assert!(
        SetupRows::is_selectable(SetupRowState::Outdated, opencode) && SetupRows::is_selectable(SetupRowState::Missing, opencode),
        "an old opencode may be picked"
    );
    assert_eq!(SetupRows::derive(Some(&old), true, false), SetupRowState::Installing, "the picked old opencode reads as installing");
    for id in ["claude", "pi", "hermes"] {
        assert!(!SetupRows::is_selectable(SetupRowState::Outdated, catalog.item(id).unwrap()), "{id} gets the CLI update instead");
    }
    let steps =
        script::steps(&plan::order(&set(&["opencode"]), &catalog, SetupPlatform::Windows, &|_| false), &catalog, &SetupCatalog::title);
    let text = script::render(&steps, &plain_labels(), &windows_environment(), r"C:\s\started-1", r"C:\s\done-1");
    assert!(text.contains(
        "try { $global:LASTEXITCODE = 0; npm uninstall -g 'opencode-ai' *> $null; npm install -g '@opencode/cli'; if ($LASTEXITCODE)"
    ));
    assert_eq!(components::install_command("opencode"), Some("npm install -g @opencode/cli"), "the copyable command");
}

/// "setup: Hermes's step runs its PowerShell installer with -NonInteractive from its allowed host" (OpencodeHermesSetupVerification.cs:110).
#[test]
fn setup_hermess_step_runs_its_powershell_installer_with_noninteractive_from_its_allowed_host() {
    let catalog = bundled();
    let body = script::body(catalog.item("hermes").unwrap().entry(SetupPlatform::Windows).unwrap()).unwrap();
    assert_eq!(body, "Invoke-MightyScript 'https://hermes-agent.nousresearch.com/install.ps1' -ScriptArguments @('-NonInteractive')");
    assert_eq!(
        script::body(catalog.item("claude").unwrap().entry(SetupPlatform::Windows).unwrap()).as_deref(),
        Some("Invoke-MightyScript 'https://claude.ai/install.ps1'")
    );
    assert!(
        SetupCatalog::SCRIPT_ARGUMENTS.len() == 1
            && SetupCatalog::script_arguments("https://hermes-agent.nousresearch.com/install.ps1") == Some(&["-NonInteractive"][..]),
        "only Hermes's installer takes options"
    );
    let preamble = script::preamble(&windows_environment());
    let run = preamble.iter().find(|l| l.starts_with("function Invoke-MightyScript")).unwrap();
    assert!(
        run.starts_with("function Invoke-MightyScript([string]$Url, [string[]]$ScriptArguments = @()) {")
            && run.contains("& $mightyPowerShell -NoProfile -ExecutionPolicy Bypass -File $file @ScriptArguments }"),
        "the options follow the script's file"
    );
    assert_eq!(ids(&plan::order(&set(&["hermes"]), &catalog, SetupPlatform::Windows, &|_| false)), ["hermes"]);
    assert_eq!(ids(&plan::order(&set(&["hermes"]), &catalog, SetupPlatform::MacOS, &|_| false)), ["git", "hermes"]);
    assert!(
        !SetupRows::is_selectable(SetupRowState::Outdated, catalog.item("hermes").unwrap())
            && SetupRows::is_selectable(SetupRowState::Missing, catalog.item("hermes").unwrap())
    );
    let steps = script::steps(&["git".to_string(), "hermes".to_string()], &catalog, &|item| item.id.clone());
    let text = script::render(&steps, &plain_labels(), &windows_environment(), r"C:\s\started-1", r"C:\s\done-1");
    assert!(text.contains(&format!("try {{ $global:LASTEXITCODE = 0; {body}; if ($LASTEXITCODE)")));
    assert_eq!(
        components::install_command("hermes"),
        Some("& ([scriptblock]::Create((irm https://hermes-agent.nousresearch.com/install.ps1))) -NonInteractive"),
        "the copyable command passes the same option through a scriptblock"
    );
    let mut root: Value = serde_json::from_str(&fixture_text()).unwrap();
    let install = |root: &mut Value, value: Value| {
        for item in root["items"].as_array_mut().unwrap() {
            if item["id"] == "hermes" {
                item["platforms"]["windows"]["install"] = value.clone();
            }
        }
    };
    for url in [
        "https://hermes-agent.nousresearch.com.example.org/install.ps1",
        "http://hermes-agent.nousresearch.com/install.ps1",
        "https://hermes-assets.nousresearch.com/install.ps1",
        "https://github.com/someone/hermes-agent/raw/main/scripts/install.ps1",
    ] {
        install(&mut root, json!({"kind": "officialScript", "url": url, "shell": "powershell"}));
        assert!(catalog::decode(&root.to_string()).is_err(), "the decoder took {url}");
    }
    install(
        &mut root,
        json!({"kind": "officialScript", "url": "https://raw.githubusercontent.com/NousResearch/hermes-agent/main/scripts/install.ps1", "shell": "powershell"}),
    );
    let decoded = catalog::decode(&root.to_string()).unwrap();
    assert!(
        matches!(&decoded.item("hermes").unwrap().entry(SetupPlatform::Windows).unwrap().install,
            SetupRecipe::OfficialScript { url, .. } if url.host_str() == Some("raw.githubusercontent.com")),
        "Hermes's own repository is one the catalog may name"
    );
    install(&mut root, json!({"kind": "officialScript", "url": "https://hermes-agent.nousresearch.com/install.ps1", "shell": "bash"}));
    assert!(catalog::decode(&root.to_string()).is_err(), "install.ps1 in bash");
}

/// "setup: Windows finds opencode and Hermes where their installers put them" (OpencodeHermesSetupVerification.cs:150): the setup
/// probe's half (Hermes's installer folder among the installers' folders and the preamble, the probe over real folders on Windows).
/// `CliDiscovery.Files` is mighty-platform-windows' (tests/cli_discovery.rs).
#[tokio::test]
async fn setup_windows_finds_opencode_and_hermes_where_their_installers_put_them() {
    let env = windows_environment();
    let hermes_folder = r"C:\Users\me\AppData\Local\hermes\bin";
    assert_eq!(hermes_install_folder(&env).as_deref(), Some(hermes_folder), "Hermes's installer folder");
    let mut moved = env.clone();
    moved.insert("hermes_home".into(), r"D:\hermes".into());
    assert_eq!(hermes_install_folder(&moved).as_deref(), Some(r"D:\hermes\bin"), "HERMES_HOME's bin, named in any case");
    let mut relative = env.clone();
    relative.insert("HERMES_HOME".into(), "hermes".into());
    assert_eq!(hermes_install_folder(&relative).as_deref(), Some(hermes_folder), "a relative HERMES_HOME is not the folder");
    assert_eq!(hermes_install_folder(&BTreeMap::new()), None, "no folder when the environment names none");
    assert!(well_known_folders(&env).contains(&hermes_folder.to_string()) && script::preamble(&env)[1].contains(hermes_folder));
    let mut home = env.clone();
    home.insert("HERMES_HOME".into(), r"D:\hermes".into());
    assert!(well_known_folders(&home).contains(&r"D:\hermes\bin".to_string()), "HERMES_HOME moves it");
    #[cfg(windows)]
    {
        let root = common::temp("hermes-folders");
        let local = root.join("local");
        let shims = root.join("npm");
        let installed = local.join("hermes").join("bin");
        std::fs::create_dir_all(&installed).unwrap();
        std::fs::create_dir_all(&shims).unwrap();
        std::fs::write(installed.join("hermes.exe"), "").unwrap();
        std::fs::write(shims.join("opencode.cmd"), "").unwrap();
        let (shims_text, local_text, root_text) =
            (shims.to_string_lossy().into_owned(), local.to_string_lossy().into_owned(), root.to_string_lossy().into_owned());
        let environment =
            environment(&[("Path", shims_text.as_str()), ("LOCALAPPDATA", local_text.as_str()), ("USERPROFILE", root_text.as_str())]);
        let versions = Versions::default();
        versions.set(&installed.join("hermes.exe"), Some(&hermes_output()));
        versions.set(&shims.join("opencode.cmd"), Some("1.19.5"));
        let finder: Finder = Arc::new(mighty_platform_windows::cli_copies);
        let prober = |env: &BTreeMap<String, String>| SetupProber::new(env.clone(), finder.clone(), versions.reader());
        let hermes = SetupProbe::Executable { name: "hermes".into(), minimum_version: Some("0.23.0".into()) };
        let opencode = SetupProbe::Executable { name: "opencode".into(), minimum_version: Some("2.0.0".into()) };
        let found = prober(&environment).probe(&hermes).await;
        assert!(
            found.state == SetupProbeState::Installed
                && found.version.as_deref() == Some(HERMES_VERSION)
                && found.shown_version() == Some(HERMES_VERSION)
        );
        let old = prober(&environment).probe(&opencode).await;
        assert!(
            old.state == SetupProbeState::Outdated && old.version.as_deref() == Some("1.19.5") && old.minimum.as_deref() == Some("2.0.0")
        );
        assert!(
            versions.asked().last().unwrap().eq_ignore_ascii_case(&shims.join("opencode.cmd").to_string_lossy()),
            "an npm OpenCode 1's shim is found"
        );
        versions.set(&shims.join("opencode.cmd"), Some("opencode v2.2.7"));
        let new = prober(&environment).probe(&opencode).await;
        assert!(new.state == SetupProbeState::Installed && new.version.as_deref() == Some("opencode v2.2.7"), "OpenCode 2 is there");
        let hermes_home = root.join("hermes-home").join("bin");
        std::fs::create_dir_all(&hermes_home).unwrap();
        std::fs::write(hermes_home.join("hermes.cmd"), "").unwrap();
        versions.set(&hermes_home.join("hermes.cmd"), Some(&hermes_output()));
        versions.asked.lock().unwrap().clear();
        let mut elsewhere = environment.clone();
        elsewhere.insert("HERMES_HOME".into(), root.join("hermes-home").to_string_lossy().into_owned());
        let moved = prober(&elsewhere).probe(&hermes).await;
        assert!(moved.state == SetupProbeState::Installed && moved.version.as_deref() == Some(HERMES_VERSION));
        let asked = versions.asked();
        assert!(
            asked.len() == 1 && asked[0].eq_ignore_ascii_case(&hermes_home.join("hermes.cmd").to_string_lossy()),
            "HERMES_HOME's bin is the folder: {asked:?}"
        );
        common::remove(&root);
    }
}

/// "setup: only Hermes's first line is its version wherever Windows reads one" (OpencodeHermesSetupVerification.cs:196): the setup
/// probe's half (`CliDiscovery.VersionText` is mighty-run's and mighty-platform-windows').
#[tokio::test]
async fn setup_only_hermess_first_line_is_its_version_wherever_windows_reads_one() {
    assert_eq!(mighty_run::providers::version_text("hermes", &hermes_output()), HERMES_VERSION);
    assert_eq!(probe::version_numbers(HERMES_VERSION), Some(vec![0, 23, 1]));
    assert!(
        probe::meets(HERMES_VERSION, "0.23.0") && !probe::meets(HERMES_VERSION, "0.24.0"),
        "the line holds the version the minimum is read against"
    );
    let runtime = [runtime("hermes", "Hermes CLI", true, Some(HERMES_VERSION))];
    let results: HashMap<String, SetupProbeResult> = [("hermes".to_string(), SetupProbeResult::installed(Some(HERMES_VERSION)))].into();
    assert!(!SetupRuntimeSync::is_stale(&runtime, &bundled(), &results), "the probe and the runtime agree");
    // The prober keeps only the first line of what Hermes prints.
    let output = hermes_output();
    let reader: VersionReader = Arc::new(move |_| {
        let output = output.clone();
        Box::pin(async move { Some(output) })
    });
    let prober = SetupProber::new(BTreeMap::new(), fixed_finder(&[("hermes", &[r"C:\h\hermes.exe"])]), reader);
    let found = prober.probe(&SetupProbe::Executable { name: "hermes".into(), minimum_version: Some("0.23.0".into()) }).await;
    assert_eq!(found.version.as_deref(), Some(HERMES_VERSION));
}

/// "pi: the setup item installs pi with npm after Node and then signs in" (PiVerification.cs:278).
#[test]
fn pi_the_setup_item_installs_pi_with_npm_after_node_and_then_signs_in() {
    let catalog = catalog::decode(&fixture_text()).unwrap();
    let item = catalog.item("pi").unwrap();
    assert!(
        item.group == SetupGroup::Agent
            && !item.required
            && item.provider() == Some("pi")
            && item.label_key == "setup.item.pi.title"
            && item.description_key == "setup.item.pi.detail"
    );
    for platform in SetupPlatform::ALL {
        let entry = item.entry(platform).unwrap();
        assert!(matches!(&entry.probe, SetupProbe::Executable { name, .. } if name == "pi"));
        assert_eq!(entry.install, SetupRecipe::NpmGlobal { package: "@mariozechner/pi-coding-agent".into() });
        assert!(
            entry.prerequisites == ["node"] && entry.source == Url::parse("https://github.com/badlogic/pi-mono").unwrap(),
            "{platform:?} entry"
        );
    }
    assert_eq!(ids(&plan::order(&set(&["pi"]), &catalog, SetupPlatform::Windows, &|_| false)), ["node", "pi"]);
    assert_eq!(ids(&plan::order(&set(&["pi"]), &catalog, SetupPlatform::Windows, &|_| true)), ["pi"]);
    assert_eq!(components::install_command("pi"), Some("npm install -g @mariozechner/pi-coding-agent"));
}

/// The setup rows of "antigravity: the model list, labels, install and a fake agy's status, update and models"
/// (AntigravityVerification.cs:158, README Pending inputs): agy's item, its install and the copyable command.
#[test]
fn antigravity_the_model_list_labels_and_install_setup_rows() {
    let catalog = bundled();
    let agy = catalog.item("agy").unwrap();
    let entry = agy.entry(SetupPlatform::Windows).unwrap();
    assert!(agy.provider() == Some("gemini") && entry.probe == SetupProbe::Executable { name: "agy".into(), minimum_version: None });
    assert_eq!(script::body(entry).as_deref(), Some("Invoke-MightyScript 'https://antigravity.google/cli/install.ps1'"));
    assert_eq!(
        components::install_command("gemini"),
        Some("irm https://antigravity.google/cli/install.ps1 | iex"),
        "Antigravity's own Windows installer"
    );
}

// ── the screen (MainWindow.Setup.cs, MainWindow.SetupSmoke.cs) ────────────────────────────────────────────────────────────

/// The smoke's screen over fixed probes (MainWindow.SetupSmoke.cs): the first-run decision, the rows, the picks and the plan in
/// install order, the confirmation, a cancelled confirmation that opens no window, and the blocked pane's install.
#[test]
fn setup_screen_over_the_smokes_fixed_probes() {
    common::korean();
    let mut screen = SetupScreen::new();
    let catalog = screen.load_catalog().unwrap();
    screen.fixed_results = Some(screen::smoke_fixed_results(&catalog));
    let Refresh::Fixed(results) = screen.begin_refresh(false, None, Gate::Smoke) else { panic!("the smoke reads its fixed results") };
    assert_eq!(screen.begin_refresh(true, None, Gate::Smoke), Refresh::Queued, "a forced refresh while one runs waits");
    let done = screen.finish_refresh(results, None, false, Instant::now());
    assert!(done.again && !done.runtime_stale, "the queued one runs next, and fixed results never make the runtime stale");
    assert!(
        SetupFirstRun::should_present(&catalog, &screen.results, false) && !SetupFirstRun::should_present(&catalog, &screen.results, true),
        "firstRunDecision"
    );
    assert!(screen.needs_attention() && screen.shell_flags() == (false, true), "the welcome offers the screen while Git is missing");
    assert!(screen.open(Some("agy")));
    screen.preselect_once();
    screen.toggle("gh");
    let expected = [
        ("claude", SetupRowState::Outdated),
        ("codex", SetupRowState::Installed),
        ("agy", SetupRowState::Missing),
        ("git", SetupRowState::Missing),
        ("node", SetupRowState::Missing),
        ("gh", SetupRowState::Missing),
        ("tailscale", SetupRowState::Missing),
        ("glab", SetupRowState::Missing),
    ];
    for (id, state) in expected {
        assert_eq!(screen.state_of(catalog.item(id).unwrap()), state, "the {id} row");
    }
    assert!(screen.items().iter().all(|item| !["homebrew", "gstack", "bun"].contains(&item.id.as_str())), "the Mac's tools are not listed");
    for (id, ticked) in [("git", true), ("agy", true), ("gh", true), ("glab", false)] {
        assert_eq!(screen.selection.contains(id) || screen.added().contains(id), ticked, "{id}'s box");
    }
    assert_eq!(ids(&screen.plan_ids()), ["agy", "git", "gh"], "plan order");
    let confirmation = screen.confirmation(&windows_environment()).expect("the plan has steps to confirm");
    assert_eq!(confirmation.steps.iter().map(|s| s.item_id.as_str()).collect::<Vec<_>>(), ["agy", "git", "gh"]);
    assert!(confirmation.steps.iter().all(|s| !s.body.to_lowercase().contains("runas") && !s.body.contains("sudo")), "a step elevates");
    assert!(confirmation.preamble[0].starts_with("Set-Location -LiteralPath $HOME"), "the working-folder line first");
    // A cancelled confirmation starts nothing; a smoke never installs even when asked.
    let window = RecordingTerminalLauncher::default();
    let files = FakeSetupFiles::default();
    let local = common::temp("screen-smoke");
    assert_eq!(
        screen.start_install(&confirmation.steps, Gate::Smoke, &windows_environment(), &files, &window, None, &local, Instant::now()),
        InstallStart::Ignored
    );
    assert!(screen.run.is_none() && screen.terminals_opened == 0 && window.opened().is_empty(), "cancelledWithoutTerminal");
    // A pane whose CLI is missing offers its install; an old Claude does not, and a shell never.
    let mut runtime = RuntimeInfo {
        platform: "win32".into(),
        app_version: "1.0.0".into(),
        claude_available: false,
        claude_version: None,
        model_catalog: None,
        providers: vec![runtime("gemini", "Antigravity CLI", false, None), runtime("claude", "Claude Code", false, Some("2.0.0"))],
        mods: None,
    };
    assert_eq!(screen.item_for_missing_cli("claude", "gemini", false, Some(&runtime)).as_deref(), Some("agy"));
    assert_eq!(screen.item_for_missing_cli("shell", "gemini", false, Some(&runtime)), None);
    assert_eq!(screen.item_for_missing_cli("claude", "claude", false, Some(&runtime)), None, "an old Claude gets its update");
    assert_eq!(screen.item_for_missing_cli("claude", "gemini", true, Some(&runtime)), None, "a remote pane's CLI is on its host");
    runtime.providers.clear();
    assert_eq!(screen.item_for_missing_cli("claude", "codex", false, None), None, "no runtime read yet offers nothing");
    common::remove(&local);
}

/// An install over the fakes: refused read-only and elevated, then written, opened, watched and finished (MainWindow.Setup.cs
/// `StartSetupInstall`, `WatchSetupRun`, `FinishSetupRun`).
#[test]
fn setup_screen_installs_through_the_ports_only_when_allowed() {
    common::korean();
    let mut screen = SetupScreen::new();
    let catalog = screen.load_catalog().unwrap();
    let results: HashMap<String, SetupProbeResult> = screen::smoke_fixed_results(&catalog);
    screen.finish_refresh(results, None, true, Instant::now());
    screen.toggle("gh");
    let env = windows_environment();
    let steps = screen.confirmation(&env).unwrap().steps;
    let window = RecordingTerminalLauncher::default();
    let files = FakeSetupFiles::default();
    files.set_system_folder(Some(r"C:\WINDOWS\system32"));
    let local = common::temp("screen-install");
    let now = Instant::now();
    assert_eq!(
        screen.start_install(&steps, Gate::ReadOnly, &env, &files, &window, None, &local, now),
        InstallStart::NotAllowed,
        "read-only installs nothing"
    );
    assert!(files.prepared().is_empty() && window.opened().is_empty());
    files.set_elevated(true);
    assert!(
        screen.may_confirm(&files, &env).is_none()
            && screen.message.as_deref() == Some(strings::get("setup.windows.error.elevated").as_str()),
        "an elevated app refuses before the confirmation"
    );
    assert_eq!(screen.start_install(&steps, Gate::Allowed, &env, &files, &window, None, &local, now), InstallStart::Refused);
    files.set_elevated(false);
    let refusing = RecordingTerminalLauncher::default();
    refusing.refuse();
    assert_eq!(screen.start_install(&steps, Gate::Allowed, &env, &files, &refusing, None, &local, now), InstallStart::Refused);
    let why = mighty_platform::PlatformError::Refused("the terminal did not start".into()).to_string();
    assert_eq!(screen.message.as_deref(), Some(strings::get_with("setup.windows.error.terminal", &[("error", &why)]).as_str()));
    assert_eq!(screen.start_install(&steps, Gate::Allowed, &env, &files, &window, None, &local, now), InstallStart::Started);
    let folder = local.join("MightyCode").join("setup");
    let opened = window.opened();
    assert_eq!(opened.len(), 1);
    let (argv, at, variables) = (&opened[0].argv, opened[0].directory.as_ref(), &opened[0].environment);
    assert!(
        at == Some(&folder)
            && argv[0] == r"C:\WINDOWS\system32\WindowsPowerShell\v1.0\powershell.exe"
            && argv[1..5] == ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]
            && opened[0].resolved.as_deref() == Some(argv[0].as_str())
            && opened[0].prefer_terminal_app,
        "the system's PowerShell in the setup folder, Windows Terminal when it is there: {:?}",
        opened[0]
    );
    assert_eq!(
        variables.get("NoDefaultCurrentDirectoryInExePath").map(String::as_str),
        Some("1"),
        "the step's shims never run a planted node"
    );
    let script = std::fs::read(&argv[5]).unwrap();
    assert!(
        script.starts_with(&[0xEF, 0xBB, 0xBF]) && String::from_utf8_lossy(&script).contains("winget install --exact --id 'GitHub.cli'")
    );
    assert!(screen.running() && screen.terminals_opened == 1 && screen.message.is_none());
    assert_eq!(screen.state_of(catalog.item("gh").unwrap()), SetupRowState::Installing);
    assert!(screen.begin_refresh(false, None, Gate::Allowed) != Refresh::Nothing);
    screen.abort_refresh(now);
    // The watcher: nothing for 5 s, the run's items then; the not-started hint after 20 s; the end marker ends it.
    let run = screen.run.clone().unwrap();
    assert_eq!(screen.watch_tick(now + Duration::from_secs(2), &files), WatchTick::Running { probe: None, redraw: false });
    let WatchTick::Running { probe: Some(items), .. } = screen.watch_tick(now + Duration::from_secs(6), &files) else {
        panic!("the run's items are probed")
    };
    assert_eq!(items, set(&["gh"]));
    assert_eq!(
        screen.watch_tick(now + Duration::from_secs(21), &files),
        WatchTick::Running { probe: Some(set(&["gh"])), redraw: true },
        "the not-started hint, and the items again"
    );
    assert_eq!(screen.watch_tick(now + Duration::from_secs(22), &files), WatchTick::Running { probe: None, redraw: false });
    std::fs::create_dir(&run.started).unwrap();
    assert_eq!(
        screen.watch_tick(now + Duration::from_secs(23), &files),
        WatchTick::Running { probe: None, redraw: true },
        "the script started"
    );
    std::fs::create_dir(&run.done).unwrap();
    assert_eq!(screen.watch_tick(now + Duration::from_secs(24), &files), WatchTick::Ended);
    assert!(screen.finish_run() && !screen.finish_run() && !screen.running());
    let gh = SetupProbeResult::missing();
    screen.finish_refresh([("gh".to_string(), gh)], None, true, now);
    assert_eq!(screen.state_of(catalog.item("gh").unwrap()), SetupRowState::Failed, "what the probe still misses after the run failed");
    assert!(!screen.after_finish(false, false) && screen.shell_flags().0, "behind another window the status bar says the install is over");
    assert!(screen.open(None) && !screen.shell_flags().0, "opening the screen clears the badge");
    common::remove(&local);
}

/// The timed probe: due at 30 s while something is missing, never while one runs, never in a smoke.
#[test]
fn setup_screen_polls_while_something_is_missing() {
    let mut screen = SetupScreen::new();
    let catalog = screen.load_catalog().unwrap();
    let now = Instant::now();
    assert!(screen.poll_due(now, Gate::Allowed) && !screen.poll_due(now, Gate::Smoke), "nothing probed yet");
    screen.finish_refresh(screen::smoke_fixed_results(&catalog), None, true, now);
    assert!(
        !screen.poll_due(now + Duration::from_secs(29), Gate::Allowed) && screen.poll_due(now + Duration::from_secs(31), Gate::Allowed)
    );
    let all: Vec<(String, SetupProbeResult)> =
        screen.items().iter().map(|item| (item.id.clone(), SetupProbeResult::installed(Some("1")))).collect();
    screen.finish_refresh(all, None, true, now);
    assert!(!screen.poll_due(now + Duration::from_secs(31), Gate::Allowed), "with everything there nothing is looked for");
}
