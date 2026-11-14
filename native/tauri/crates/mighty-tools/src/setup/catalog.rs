//! The first-run setup screen's tools (필요한 도구), read from the catalog the app ships (C# `SetupCatalog`, `SetupCatalogDecoder`,
//! SetupCatalog.cs): `native/contracts/fixtures/setup-catalog.json`, carried as it is. The decoder is closed, as the Mac's is: an
//! unknown key, kind, host or program is an error, so the catalog can only ask for what this crate knows how to probe and install.
//!
//! The JSON is read with serde_json: a key repeated in one object keeps its last value, as `JsonElement.TryGetProperty` reads it;
//! a platform repeated under `platforms` is that one platform here, where C# also says it repeats (the catalog is the app's own).

use std::sync::{Arc, OnceLock};

use regex::Regex;
use serde_json::{Map, Value};
use url::Url;

use mighty_run::strings;

/// An item's group, in the screen's order.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum SetupGroup {
    Agent,
    Prerequisite,
    Style,
    Network,
    Dev,
}

impl SetupGroup {
    /// `Enum.GetValues<SetupGroup>()`.
    pub const ALL: [SetupGroup; 5] = [SetupGroup::Agent, SetupGroup::Prerequisite, SetupGroup::Style, SetupGroup::Network, SetupGroup::Dev];
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum SetupPlatform {
    MacOS,
    Windows,
}

impl SetupPlatform {
    pub const ALL: [SetupPlatform; 2] = [SetupPlatform::MacOS, SetupPlatform::Windows];

    fn name(self) -> &'static str {
        match self {
            SetupPlatform::MacOS => "macos",
            SetupPlatform::Windows => "windows",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SetupAfterInstallKind {
    None,
    Reprobe,
    SignIn,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupAfterInstall {
    pub kind: SetupAfterInstallKind,
    pub provider: Option<String>,
}

/// How an item is found. The probe, never an exit code, decides whether it is there.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SetupProbe {
    /// On the PATH (and on Windows in the folders the official installers use); with a minimum, `--version` must reach it.
    Executable { name: String, minimum_version: Option<String> },
    /// A user-scope Claude plugin whose ID starts with `prefix` (`ouroboros@`).
    ClaudePlugin { prefix: String },
    /// `~/.claude/skills/<name>/SKILL.md`.
    Skill { name: String },
    /// Mac only: `xcode-select -p`, then git.
    CommandLineTools,
    /// Mac only: an app bundle, or its executable on the PATH.
    AppBundle { name: String, executable_name: Option<String> },
}

/// How an item is installed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SetupRecipe {
    /// One of [`SetupCatalog::ALLOWED_FLAGS`]' programs with fixed arguments.
    Argv {
        arguments: Vec<String>,
        finishes_outside: bool,
    },
    /// A fixed https script on an allow-listed host; PowerShell on Windows.
    OfficialScript {
        url: Url,
        shell: String,
    },
    BrewFormula {
        name: String,
    },
    BrewCask {
        name: String,
    },
    NpmGlobal {
        package: String,
    },
    ClaudePlugin {
        marketplace: String,
        plugin: String,
    },
    /// Mac only: a repository pinned to a commit in `~/.claude/skills/<folder>`, then a script inside it.
    Skill {
        repository: Url,
        folder: String,
        commit: String,
        setup: Option<String>,
    },
    /// Windows only: `winget install` of an exact package ID from the winget source, in user or machine scope.
    Winget {
        id: String,
        scope: String,
    },
    /// Nothing runs: the app opens the page.
    VendorPage {
        url: Url,
    },
}

impl SetupRecipe {
    pub fn runs_in_terminal(&self) -> bool {
        !matches!(self, SetupRecipe::VendorPage { .. })
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupPlatformEntry {
    pub probe: SetupProbe,
    pub install: SetupRecipe,
    pub prerequisites: Vec<String>,
    pub source: Url,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupItem {
    pub id: String,
    pub group: SetupGroup,
    pub label_key: String,
    pub description_key: String,
    pub required: bool,
    pub after_install: SetupAfterInstall,
    /// The catalog's platforms in its order.
    pub platforms: Vec<(SetupPlatform, SetupPlatformEntry)>,
}

impl SetupItem {
    /// The agent CLI's provider ID when this item is one.
    pub fn provider(&self) -> Option<&str> {
        match self.after_install.kind {
            SetupAfterInstallKind::SignIn => self.after_install.provider.as_deref(),
            _ => None,
        }
    }

    pub fn entry(&self, platform: SetupPlatform) -> Option<&SetupPlatformEntry> {
        self.platforms.iter().find(|(p, _)| *p == platform).map(|(_, entry)| entry)
    }
}

/// Why the catalog did not decode: `setup-catalog.json: …` (C# `InvalidDataException`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CatalogError(pub String);

impl std::fmt::Display for CatalogError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for CatalogError {}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupCatalog {
    pub items: Vec<SetupItem>,
}

/// The catalog the app carries: the contract file itself, embedded at build time (C# embeds it, MightyCode.Core.csproj:20).
const BUNDLED: &str = include_str!("../../../../../contracts/fixtures/setup-catalog.json");

impl SetupCatalog {
    /// Hosts an official script, a vendor page, a marketplace URL or a skill's repository may live on. The catalog cannot add one.
    pub const ALLOWED_HOSTS: [&'static str; 9] = [
        "claude.ai",
        "chatgpt.com",
        "antigravity.google",
        "raw.githubusercontent.com",
        "astral.sh",
        "bun.com",
        "github.com",
        "tailscale.com",
        "hermes-agent.nousresearch.com",
    ];
    /// The only programs an `argv` install may start, each with the only options it may be given.
    pub const ALLOWED_FLAGS: [(&'static str, &'static [&'static str]); 2] =
        [("xcode-select", &["--install"]), ("npx", &["--global", "--agent"])];
    /// The GitHub repositories a URL or a marketplace's owner/repo short form may name: only those the catalog uses.
    pub const ALLOWED_REPOSITORIES: [&'static str; 7] = [
        "Homebrew/install",
        "Q00/ouroboros",
        "anthropics/claude-plugins-community",
        "Yeachan-Heo/oh-my-claudecode",
        "garrytan/gstack",
        "badlogic/pi-mono",
        "NousResearch/hermes-agent",
    ];
    /// npm packages whose install replaces an older package owning the same command (OpenCode 2 takes `opencode` over from
    /// OpenCode 1, which is uninstalled first; one that is not there is no error).
    pub const NPM_REPLACES: [(&'static str, &'static str); 1] = [("@opencode/cli", "opencode-ai")];
    /// Agent CLIs an out-of-date copy cannot update into (OpenCode 1's own upgrade stays on 1): their row may be picked again.
    pub const REINSTALLED_WHEN_OUTDATED: [&'static str; 1] = ["opencode"];
    /// The options an official script is run with, by its URL: Hermes's install.ps1 otherwise ends in its own setup wizard and
    /// installs its gateway as a service; `-NonInteractive` skips both.
    pub const SCRIPT_ARGUMENTS: [(&'static str, &'static [&'static str]); 1] =
        [("https://hermes-agent.nousresearch.com/install.ps1", &["-NonInteractive"])];
    /// Every label and description key a catalog item may name, as the Mac keeps them.
    pub const KNOWN_KEYS: [&'static str; 34] = [
        "setup.item.claude.title",
        "setup.item.claude.detail",
        "setup.item.codex.title",
        "setup.item.codex.detail",
        "setup.item.agy.title",
        "setup.item.agy.detail",
        "setup.item.pi.title",
        "setup.item.pi.detail",
        "setup.item.opencode.title",
        "setup.item.opencode.detail",
        "setup.item.hermes.title",
        "setup.item.hermes.detail",
        "setup.item.git.title",
        "setup.item.git.detail",
        "setup.item.homebrew.title",
        "setup.item.homebrew.detail",
        "setup.item.node.title",
        "setup.item.node.detail",
        "setup.item.bun.title",
        "setup.item.bun.detail",
        "setup.item.uv.title",
        "setup.item.uv.detail",
        "setup.item.ouroboros.title",
        "setup.item.ouroboros.detail",
        "setup.item.omc.title",
        "setup.item.omc.detail",
        "setup.item.gstack.title",
        "setup.item.gstack.detail",
        "setup.item.tailscale.title",
        "setup.item.tailscale.detail",
        "setup.item.gh.title",
        "setup.item.gh.detail",
        "setup.item.glab.title",
        "setup.item.glab.detail",
    ];
    /// Where Windows words an item's title differently from the Mac (its installer, its folder).
    const WINDOWS_TITLES: [(&'static str, &'static str); 1] = [("git", "setup.windows.item.git.title")];
    const WINDOWS_DETAILS: [(&'static str, &'static str); 7] = [
        ("codex", "setup.windows.item.codex.detail"),
        ("agy", "setup.windows.item.agy.detail"),
        ("git", "setup.windows.item.git.detail"),
        ("node", "setup.windows.item.node.detail"),
        ("gh", "setup.windows.item.gh.detail"),
        ("glab", "setup.windows.item.glab.detail"),
        // Its PowerShell installer puts it in %LOCALAPPDATA%\hermes and brings its own Git: the Mac's words name ~/.hermes and Git first.
        ("hermes", "setup.windows.item.hermes.detail"),
    ];

    pub fn item(&self, id: &str) -> Option<&SetupItem> {
        self.items.iter().find(|item| item.id == id)
    }

    pub fn group_title_key(group: SetupGroup) -> &'static str {
        match group {
            SetupGroup::Agent => "setup.group.agent",
            SetupGroup::Prerequisite => "setup.group.prerequisite",
            SetupGroup::Style => "setup.group.style",
            SetupGroup::Network => "setup.group.network",
            SetupGroup::Dev => "setup.group.dev",
        }
    }

    pub fn title_key(item: &SetupItem) -> &str {
        Self::WINDOWS_TITLES.iter().find(|(id, _)| *id == item.id).map(|(_, key)| *key).unwrap_or(&item.label_key)
    }

    pub fn detail_key(item: &SetupItem) -> &str {
        Self::WINDOWS_DETAILS.iter().find(|(id, _)| *id == item.id).map(|(_, key)| *key).unwrap_or(&item.description_key)
    }

    pub fn title(item: &SetupItem) -> String {
        strings::get(Self::title_key(item))
    }

    pub fn detail(item: &SetupItem) -> String {
        strings::get(Self::detail_key(item))
    }

    pub fn allowed_flags(program: &str) -> Option<&'static [&'static str]> {
        Self::ALLOWED_FLAGS.iter().find(|(name, _)| *name == program).map(|(_, flags)| *flags)
    }

    pub fn npm_replaces(package: &str) -> Option<&'static str> {
        Self::NPM_REPLACES.iter().find(|(name, _)| *name == package).map(|(_, replaced)| *replaced)
    }

    pub fn script_arguments(url: &str) -> Option<&'static [&'static str]> {
        Self::SCRIPT_ARGUMENTS.iter().find(|(name, _)| *name == url).map(|(_, options)| *options)
    }

    /// The catalog the app carries, decoded once.
    pub fn bundled() -> Result<Arc<SetupCatalog>, CatalogError> {
        static BUNDLED_CATALOG: OnceLock<Result<Arc<SetupCatalog>, CatalogError>> = OnceLock::new();
        BUNDLED_CATALOG.get_or_init(|| decode(BUNDLED).map(Arc::new)).clone()
    }
}

// ── the decoder ───────────────────────────────────────────────────────────────────────────────────────────────────────────

fn fail(message: impl AsRef<str>) -> CatalogError {
    CatalogError(format!("setup-catalog.json: {}", message.as_ref()))
}

type R<T> = Result<T, CatalogError>;

/// C# `SetupCatalogDecoder.Decode`.
pub fn decode(json: &str) -> R<SetupCatalog> {
    let root: Value = serde_json::from_str(json).map_err(|_| fail("the catalog is not a JSON object"))?;
    let Some(root) = root.as_object() else { return Err(fail("the catalog is not a JSON object")) };
    keys(root, &["schema", "note", "items"], "catalog")?;
    if root.get("schema").and_then(Value::as_i64) != Some(1) || !root.get("schema").is_some_and(|v| v.is_i64() || v.is_u64()) {
        return Err(fail("schema must be 1"));
    }
    let raw_items = match root.get("items") {
        Some(Value::Array(items)) if (1..=64).contains(&items.len()) => items,
        _ => return Err(fail("items must be 1 to 64 objects")),
    };
    let items = raw_items.iter().enumerate().map(|(index, raw)| item(raw, &format!("items[{index}]"))).collect::<R<Vec<_>>>()?;
    let ids: Vec<&str> = items.iter().map(|item| item.id.as_str()).collect();
    let mut distinct = ids.clone();
    distinct.sort_unstable();
    distinct.dedup();
    if distinct.len() != ids.len() {
        return Err(fail("item IDs repeat"));
    }
    for item in &items {
        for (platform, entry) in &item.platforms {
            for prerequisite in &entry.prerequisites {
                if *prerequisite == item.id || !ids.contains(&prerequisite.as_str()) {
                    return Err(fail(format!("{}: unknown prerequisite {prerequisite} on {}", item.id, platform.name())));
                }
            }
        }
    }
    let catalog = SetupCatalog { items };
    for platform in SetupPlatform::ALL {
        if !crate::setup::plan::has_no_cycle(&catalog, platform) {
            return Err(fail(format!("prerequisites form a cycle on {}", platform.name())));
        }
    }
    Ok(catalog)
}

fn item(raw: &Value, path: &str) -> R<SetupItem> {
    let Some(raw) = raw.as_object() else { return Err(fail(format!("{path} is not an object"))) };
    keys(raw, &["id", "group", "label", "description", "need", "afterInstall", "platforms"], path)?;
    let id = string(raw, "id", path)?;
    if !patterns().plain_id.is_match(&id) {
        return Err(fail(format!("{path}.id is not a plain ID")));
    }
    let group = match string(raw, "group", path)?.as_str() {
        "agent" => SetupGroup::Agent,
        "prerequisite" => SetupGroup::Prerequisite,
        "style" => SetupGroup::Style,
        "network" => SetupGroup::Network,
        "dev" => SetupGroup::Dev,
        _ => return Err(fail(format!("{path}.group is unknown"))),
    };
    let label = string(raw, "label", path)?;
    let description = string(raw, "description", path)?;
    if !SetupCatalog::KNOWN_KEYS.contains(&label.as_str()) || !SetupCatalog::KNOWN_KEYS.contains(&description.as_str()) {
        return Err(fail(format!("{path} names a label key the app does not know")));
    }
    let need = string(raw, "need", path)?;
    if need != "required" && need != "optional" {
        return Err(fail(format!("{path}.need must be required or optional")));
    }
    let after = after_install(raw, &format!("{path}.afterInstall"))?;
    let raw_platforms = match raw.get("platforms") {
        Some(Value::Object(platforms)) if !platforms.is_empty() => platforms,
        _ => return Err(fail(format!("{path}.platforms is missing"))),
    };
    let mut platforms: Vec<(SetupPlatform, SetupPlatformEntry)> = Vec::new();
    for (name, value) in raw_platforms {
        let platform = match name.as_str() {
            "macos" => SetupPlatform::MacOS,
            "windows" => SetupPlatform::Windows,
            _ => return Err(fail(format!("{path}.platforms.{name} is not a platform"))),
        };
        if platforms.iter().any(|(p, _)| *p == platform) {
            return Err(fail(format!("{path}.platforms.{name} repeats")));
        }
        platforms.push((platform, entry(value, platform, &format!("{path}.platforms.{name}"))?));
    }
    Ok(SetupItem {
        id,
        group,
        label_key: label,
        description_key: description,
        required: need == "required",
        after_install: after,
        platforms,
    })
}

fn after_install(parent: &Map<String, Value>, path: &str) -> R<SetupAfterInstall> {
    let Some(raw) = parent.get("afterInstall").and_then(Value::as_object) else { return Err(fail(format!("{path} is missing"))) };
    match string(raw, "kind", path)?.as_str() {
        "none" => {
            keys(raw, &["kind"], path)?;
            Ok(SetupAfterInstall { kind: SetupAfterInstallKind::None, provider: None })
        }
        "reprobe" => {
            keys(raw, &["kind"], path)?;
            Ok(SetupAfterInstall { kind: SetupAfterInstallKind::Reprobe, provider: None })
        }
        "signIn" => {
            keys(raw, &["kind", "provider"], path)?;
            let provider = string(raw, "provider", path)?;
            if !mighty_core::wire::PROVIDERS.contains(&provider.as_str()) {
                return Err(fail(format!("{path}.provider is not an agent")));
            }
            Ok(SetupAfterInstall { kind: SetupAfterInstallKind::SignIn, provider: Some(provider) })
        }
        kind => Err(fail(format!("{path}.kind {kind} is unknown"))),
    }
}

fn entry(raw: &Value, platform: SetupPlatform, path: &str) -> R<SetupPlatformEntry> {
    let Some(raw) = raw.as_object() else { return Err(fail(format!("{path} is not an object"))) };
    keys(raw, &["probe", "install", "prerequisites", "source"], path)?;
    let prerequisites: Vec<String> = match raw.get("prerequisites") {
        Some(Value::Array(values)) if values.iter().all(Value::is_string) => {
            values.iter().filter_map(Value::as_str).map(str::to_string).collect()
        }
        _ => return Err(fail(format!("{path}.prerequisites must be distinct IDs"))),
    };
    let mut distinct = prerequisites.clone();
    distinct.sort_unstable();
    distinct.dedup();
    if distinct.len() != prerequisites.len() {
        return Err(fail(format!("{path}.prerequisites must be distinct IDs")));
    }
    let source_text = string(raw, "source", path)?;
    let source = match Url::parse(&source_text) {
        Ok(url) if url.scheme() == "https" && url.host_str().is_some_and(|host| !host.is_empty()) => url,
        _ => return Err(fail(format!("{path}.source must be an https URL"))),
    };
    let Some(probe) = raw.get("probe").and_then(Value::as_object) else { return Err(fail(format!("{path}.probe is missing"))) };
    let Some(install) = raw.get("install").and_then(Value::as_object) else { return Err(fail(format!("{path}.install is missing"))) };
    Ok(SetupPlatformEntry {
        probe: probe_of(probe, platform, &format!("{path}.probe"))?,
        install: recipe(install, platform, &format!("{path}.install"))?,
        prerequisites,
        source,
    })
}

fn probe_of(raw: &Map<String, Value>, platform: SetupPlatform, path: &str) -> R<SetupProbe> {
    let p = patterns();
    match string(raw, "kind", path)?.as_str() {
        "executable" => {
            keys(raw, &["kind", "name", "minimumVersion"], path)?;
            let name = plain_name(raw, "name", path)?;
            let mut minimum = None;
            if raw.contains_key("minimumVersion") {
                let value = string(raw, "minimumVersion", path)?;
                if !p.version.is_match(&value) {
                    return Err(fail(format!("{path}.minimumVersion is not a version")));
                }
                minimum = Some(value);
            }
            Ok(SetupProbe::Executable { name, minimum_version: minimum })
        }
        "claudePlugin" => {
            keys(raw, &["kind", "prefix"], path)?;
            let prefix = string(raw, "prefix", path)?;
            if !p.plugin_prefix.is_match(&prefix) {
                return Err(fail(format!("{path}.prefix must name a whole plugin (name@)")));
            }
            Ok(SetupProbe::ClaudePlugin { prefix })
        }
        "skill" => {
            keys(raw, &["kind", "name"], path)?;
            Ok(SetupProbe::Skill { name: folder_name(raw, "name", path)? })
        }
        "commandLineTools" => {
            keys(raw, &["kind"], path)?;
            if platform != SetupPlatform::MacOS {
                return Err(fail(format!("{path}: commandLineTools is a Mac probe")));
            }
            Ok(SetupProbe::CommandLineTools)
        }
        "appBundle" => {
            keys(raw, &["kind", "name", "executable"], path)?;
            let bundle = string(raw, "name", path)?;
            if platform != SetupPlatform::MacOS || !p.app_bundle.is_match(&bundle) {
                return Err(fail(format!("{path}.name must be an app bundle's name")));
            }
            let executable = if raw.contains_key("executable") { Some(plain_name(raw, "executable", path)?) } else { None };
            Ok(SetupProbe::AppBundle { name: bundle, executable_name: executable })
        }
        kind => Err(fail(format!("{path}.kind {kind} is unknown"))),
    }
}

fn recipe(raw: &Map<String, Value>, platform: SetupPlatform, path: &str) -> R<SetupRecipe> {
    let p = patterns();
    match string(raw, "kind", path)?.as_str() {
        "argv" => {
            keys(raw, &["kind", "argv", "finishesOutside"], path)?;
            let argv: Vec<String> = match raw.get("argv") {
                Some(Value::Array(values)) if values.iter().all(Value::is_string) => {
                    values.iter().filter_map(Value::as_str).map(str::to_string).collect()
                }
                _ => return Err(fail(format!("{path}.argv must start with an allowed program"))),
            };
            let flags = match argv.first() {
                Some(program) if argv.len() <= 16 => SetupCatalog::allowed_flags(program),
                _ => None,
            };
            let Some(flags) = flags else { return Err(fail(format!("{path}.argv must start with an allowed program"))) };
            if argv[0] == "xcode-select" && platform != SetupPlatform::MacOS {
                return Err(fail(format!("{path}.argv: xcode-select is a Mac program")));
            }
            for argument in &argv[1..] {
                if !p.argv_argument.is_match(argument) {
                    return Err(fail(format!("{path}.argv has an argument with characters the catalog does not allow")));
                }
                if argument.starts_with('-') && !flags.contains(&argument.as_str()) {
                    return Err(fail(format!("{path}.argv gives {} an option it may not take: {argument}", argv[0])));
                }
            }
            let mut outside = false;
            if let Some(value) = raw.get("finishesOutside") {
                let Some(flag) = value.as_bool() else { return Err(fail(format!("{path}.finishesOutside must be true or false"))) };
                outside = flag;
            }
            Ok(SetupRecipe::Argv { arguments: argv, finishes_outside: outside })
        }
        "officialScript" => {
            keys(raw, &["kind", "url", "shell"], path)?;
            let url = allowed_url_of(raw, "url", path)?;
            let shell = string(raw, "shell", path)?;
            let runs = match platform {
                SetupPlatform::MacOS => shell == "bash" || shell == "sh",
                SetupPlatform::Windows => shell == "powershell",
            };
            if !runs {
                return Err(fail(format!("{path}.shell {shell} is not one this platform runs")));
            }
            Ok(SetupRecipe::OfficialScript { url, shell })
        }
        "brewFormula" => {
            keys(raw, &["kind", "name"], path)?;
            if platform != SetupPlatform::MacOS {
                return Err(fail(format!("{path}: Homebrew is a Mac install")));
            }
            Ok(SetupRecipe::BrewFormula { name: package_name(raw, "name", path)? })
        }
        "brewCask" => {
            keys(raw, &["kind", "name"], path)?;
            if platform != SetupPlatform::MacOS {
                return Err(fail(format!("{path}: Homebrew is a Mac install")));
            }
            Ok(SetupRecipe::BrewCask { name: package_name(raw, "name", path)? })
        }
        "npmGlobal" => {
            keys(raw, &["kind", "package"], path)?;
            Ok(SetupRecipe::NpmGlobal { package: package_name(raw, "package", path)? })
        }
        "claudePlugin" => {
            keys(raw, &["kind", "marketplace", "plugin"], path)?;
            let marketplace = string(raw, "marketplace", path)?;
            if p.repository_short_form.is_match(&marketplace) {
                if marketplace.split('/').any(|part| part == "." || part == "..")
                    || !SetupCatalog::ALLOWED_REPOSITORIES.contains(&marketplace.as_str())
                {
                    return Err(fail(format!("{path}.marketplace is not a repository the catalog may use")));
                }
            } else {
                allowed_url(&marketplace, &format!("{path}.marketplace"))?;
            }
            let plugin = string(raw, "plugin", path)?;
            if !p.plugin_id.is_match(&plugin) {
                return Err(fail(format!("{path}.plugin must be name@marketplace")));
            }
            Ok(SetupRecipe::ClaudePlugin { marketplace, plugin })
        }
        "skill" => {
            keys(raw, &["kind", "repository", "folder", "commit", "setup"], path)?;
            // A skill's setup script is a POSIX shell script: Windows has no shell the app could vouch for to run it.
            if platform != SetupPlatform::MacOS {
                return Err(fail(format!("{path}: a pinned skill is a Mac install")));
            }
            let repository = allowed_url_of(raw, "repository", path)?;
            let folder = folder_name(raw, "folder", path)?;
            let commit = string(raw, "commit", path)?;
            if !p.commit_sha.is_match(&commit) {
                return Err(fail(format!("{path}.commit must be a full commit SHA")));
            }
            let mut setup = None;
            if raw.contains_key("setup") {
                let value = string(raw, "setup", path)?;
                if value.starts_with('/')
                    || value.split('/').any(|part| part.is_empty() || part == "." || part == "..")
                    || !p.setup_path.is_match(&value)
                {
                    return Err(fail(format!("{path}.setup must be a path inside the clone")));
                }
                setup = Some(value);
            }
            Ok(SetupRecipe::Skill { repository, folder, commit, setup })
        }
        "winget" => {
            keys(raw, &["kind", "id", "scope"], path)?;
            if platform != SetupPlatform::Windows {
                return Err(fail(format!("{path}: winget is a Windows install")));
            }
            let id = string(raw, "id", path)?;
            if !p.winget_id.is_match(&id) {
                return Err(fail(format!("{path}.id is not a winget package ID")));
            }
            let scope = string(raw, "scope", path)?;
            if scope != "user" && scope != "machine" {
                return Err(fail(format!("{path}.scope must be user or machine")));
            }
            Ok(SetupRecipe::Winget { id, scope })
        }
        "vendorPage" => {
            keys(raw, &["kind", "url"], path)?;
            Ok(SetupRecipe::VendorPage { url: allowed_url_of(raw, "url", path)? })
        }
        kind => Err(fail(format!("{path}.kind {kind} is unknown"))),
    }
}

// ── fields ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// The first key, in ordinal order, that is not allowed.
fn keys(raw: &Map<String, Value>, allowed: &[&str], path: &str) -> R<()> {
    let mut extra: Vec<&String> = raw.keys().filter(|name| !allowed.contains(&name.as_str())).collect();
    extra.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
    match extra.first() {
        Some(name) => Err(fail(format!("{path} has an unknown key {name}"))),
        None => Ok(()),
    }
}

fn string(raw: &Map<String, Value>, key: &str, path: &str) -> R<String> {
    match raw.get(key) {
        Some(value) => text(value, &format!("{path}.{key}")),
        None => Err(fail(format!("{path}.{key} must be a string"))),
    }
}

/// A BMP character of the Unicode general category Format (Cf), as `CharUnicodeInfo.GetUnicodeCategory(char)` reads a UTF-16
/// unit: characters beyond the BMP arrive in C# as surrogates, which are not Format.
fn format_character(c: char) -> bool {
    matches!(c as u32,
        0x00AD | 0x0600..=0x0605 | 0x061C | 0x06DD | 0x070F | 0x0890..=0x0891 | 0x08E2 | 0x180E | 0x200B..=0x200F | 0x202A..=0x202E
        | 0x2060..=0x2064 | 0x2066..=0x206F | 0xFEFF | 0xFFF9..=0xFFFB)
}

fn text(value: &Value, path: &str) -> R<String> {
    let Some(text) = value.as_str().filter(|text| (1..=512).contains(&text.encode_utf16().count())) else {
        return Err(fail(format!("{path} must be a string")));
    };
    if text.chars().any(|c| c.is_control() || format_character(c)) {
        return Err(fail(format!("{path} has a control character")));
    }
    Ok(text.to_string())
}

fn plain_name(raw: &Map<String, Value>, key: &str, path: &str) -> R<String> {
    let name = string(raw, key, path)?;
    if patterns().program_name.is_match(&name) { Ok(name) } else { Err(fail(format!("{path}.{key} is not a program name"))) }
}

fn folder_name(raw: &Map<String, Value>, key: &str, path: &str) -> R<String> {
    let name = string(raw, key, path)?;
    if patterns().folder.is_match(&name) && name != ".." { Ok(name) } else { Err(fail(format!("{path}.{key} is not a folder name"))) }
}

fn package_name(raw: &Map<String, Value>, key: &str, path: &str) -> R<String> {
    let name = string(raw, key, path)?;
    if patterns().package.is_match(&name) { Ok(name) } else { Err(fail(format!("{path}.{key} is not a package name"))) }
}

fn allowed_url_of(raw: &Map<String, Value>, key: &str, path: &str) -> R<Url> {
    let text = string(raw, key, path)?;
    allowed_url(&text, &format!("{path}.{key}"))
}

/// C# `AllowedUrl`: the text is `https://` and only the characters a plain address has (so no user, port, query or escape), the
/// host on the list; on github.com and raw.githubusercontent.com only the catalog's repositories.
fn allowed_url(text: &str, path: &str) -> R<Url> {
    let refused = || fail(format!("{path} must be an https URL on an allowed host"));
    if !patterns().url.is_match(text) {
        return Err(refused());
    }
    let url = Url::parse(text).map_err(|_| refused())?;
    let host = url.host_str().unwrap_or("").to_ascii_lowercase();
    if url.scheme() != "https"
        || !SetupCatalog::ALLOWED_HOSTS.contains(&host.as_str())
        || !url.username().is_empty()
        || url.port().is_some()
    {
        return Err(refused());
    }
    // GitHub hosts many owners: only the repositories the catalog uses are let in.
    if host == "github.com" || host == "raw.githubusercontent.com" {
        let parts: Vec<&str> = url.path().split('/').filter(|part| !part.is_empty()).collect();
        // The text, not the normalised address: a dot segment is refused before the URL folds it away.
        if parts.len() < 2 || text.split('/').any(|part| part == "." || part == "..") {
            return Err(fail(format!("{path} does not name a repository")));
        }
        let name = parts[1].strip_suffix(".git").unwrap_or(parts[1]);
        let repository = format!("{}/{name}", parts[0]);
        if !SetupCatalog::ALLOWED_REPOSITORIES.contains(&repository.as_str()) {
            return Err(fail(format!("{path} names a repository the catalog may not use")));
        }
    }
    Ok(url)
}

struct Patterns {
    plain_id: Regex,
    version: Regex,
    plugin_prefix: Regex,
    app_bundle: Regex,
    argv_argument: Regex,
    repository_short_form: Regex,
    plugin_id: Regex,
    commit_sha: Regex,
    setup_path: Regex,
    winget_id: Regex,
    program_name: Regex,
    folder: Regex,
    package: Regex,
    url: Regex,
}

/// The decoder's patterns, each anchored at both ends (`\z`) as C#'s are.
fn patterns() -> &'static Patterns {
    static PATTERNS: OnceLock<Patterns> = OnceLock::new();
    PATTERNS.get_or_init(|| {
        let r = |pattern: &str| Regex::new(pattern).expect("a valid pattern");
        Patterns {
            plain_id: r(r"^[a-z][a-z0-9-]{0,31}\z"),
            version: r(r"^[0-9]+(\.[0-9]+){0,2}\z"),
            plugin_prefix: r(r"^[a-z0-9][a-z0-9._-]{0,63}@\z"),
            app_bundle: r(r"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}\.app\z"),
            argv_argument: r(r"^[A-Za-z0-9@._/:=+-]{1,128}\z"),
            repository_short_form: r(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}/[A-Za-z0-9._-]{1,100}\z"),
            plugin_id: r(r"^[a-z0-9][a-z0-9._-]{0,63}@[a-z0-9][a-z0-9._-]{0,63}\z"),
            commit_sha: r(r"^[0-9a-f]{40}\z"),
            setup_path: r(r"^[A-Za-z0-9._/-]{1,128}\z"),
            winget_id: r(r"^[A-Za-z0-9][A-Za-z0-9-]{0,63}(\.[A-Za-z0-9][A-Za-z0-9-]{0,63}){1,3}\z"),
            program_name: r(r"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\z"),
            folder: r(r"^[a-z0-9][a-z0-9._-]{0,63}\z"),
            package: r(r"^@?[a-z0-9][a-z0-9._-]{0,99}(/[a-z0-9][a-z0-9._-]{0,99})?\z"),
            url: r(r"^https://[A-Za-z0-9./_-]+\z"),
        }
    })
}
