//! The slash commands a composer completes after a leading `/` (C# `SlashCommands.cs`, `SlashCommandStrings.cs`): the app's
//! built-ins per provider, the skills and commands scanned from the CLIs' folders (read through [`SlashFiles`], so tests use
//! fixtures), the filter, the palette's state and what choosing a row does, and the 30-second catalogue cache.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use mighty_core::locale::Locale;
use mighty_core::normalize;

use crate::text;

/// C# `SlashCommandAction` (SlashCommands.cs:9): what choosing a built-in does in the app instead of sending prompt text.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SlashCommandAction {
    OpenPlugins,
    NewConversation,
    ShowUsage,
    OpenSettings,
    Rename,
    Help,
    SetModel,
    SetPermission,
}

impl SlashCommandAction {
    /// The enum member's name, as `ToString()` writes it (the page's `action`).
    pub fn name(self) -> &'static str {
        match self {
            SlashCommandAction::OpenPlugins => "OpenPlugins",
            SlashCommandAction::NewConversation => "NewConversation",
            SlashCommandAction::ShowUsage => "ShowUsage",
            SlashCommandAction::OpenSettings => "OpenSettings",
            SlashCommandAction::Rename => "Rename",
            SlashCommandAction::Help => "Help",
            SlashCommandAction::SetModel => "SetModel",
            SlashCommandAction::SetPermission => "SetPermission",
        }
    }

    /// `Enum.TryParse<SlashCommandAction>` as `slashAction` reads it (MainWindow.WebShell.Composer.cs:107): the exact member name.
    pub fn parse(value: &str) -> Option<SlashCommandAction> {
        [
            SlashCommandAction::OpenPlugins,
            SlashCommandAction::NewConversation,
            SlashCommandAction::ShowUsage,
            SlashCommandAction::OpenSettings,
            SlashCommandAction::Rename,
            SlashCommandAction::Help,
            SlashCommandAction::SetModel,
            SlashCommandAction::SetPermission,
        ]
        .into_iter()
        .find(|a| a.name() == value)
    }
}

/// C# `SlashArgument` (SlashCommands.cs:12): a built-in whose argument the palette completes after `/name `.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SlashArgument {
    Model,
    Permission,
}

impl SlashArgument {
    pub fn name(self) -> &'static str {
        match self {
            SlashArgument::Model => "Model",
            SlashArgument::Permission => "Permission",
        }
    }
}

/// C# `SlashCommandOrigin` (SlashCommands.cs:15).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SlashCommandOrigin {
    App,
    Project,
    User,
    Plugin,
}

impl SlashCommandOrigin {
    pub fn name(self) -> &'static str {
        match self {
            SlashCommandOrigin::App => "App",
            SlashCommandOrigin::Project => "Project",
            SlashCommandOrigin::User => "User",
            SlashCommandOrigin::Plugin => "Plugin",
        }
    }
}

/// C# `SlashCommand` (SlashCommands.cs:21).
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct SlashCommand {
    pub invocation: String,
    pub description: String,
    pub source: String,
    pub origin: SlashCommandOrigin,
    pub action: Option<SlashCommandAction>,
    pub argument: Option<SlashArgument>,
    pub action_arg: Option<String>,
}

impl SlashCommand {
    pub fn new(invocation: &str, description: &str, source: &str, origin: SlashCommandOrigin) -> Self {
        SlashCommand {
            invocation: invocation.into(),
            description: description.into(),
            source: source.into(),
            origin,
            action: None,
            argument: None,
            action_arg: None,
        }
    }

    pub fn with_action(mut self, action: SlashCommandAction, arg: Option<&str>) -> Self {
        self.action = Some(action);
        self.action_arg = arg.map(str::to_string);
        self
    }

    pub fn id(&self) -> &str {
        &self.invocation
    }
}

/// C# `SlashCommandStrings` (SlashCommandStrings.cs): the palette's copy, read from the shared locale files with the keys macOS
/// reads. C# reads each once at first use; here each is read when asked, in the language given.
pub mod strings {
    use mighty_core::locale::Locale;

    pub fn app_source(l: &Locale) -> String {
        l.get("slash.source.app")
    }
    pub fn model_source(l: &Locale) -> String {
        l.get("composer.label.model")
    }
    pub fn permission_source(l: &Locale) -> String {
        l.get("composer.label.permission")
    }
    pub fn user_skill_source(l: &Locale) -> String {
        l.get("slash.source.userSkill")
    }
    pub fn project_skill_source(l: &Locale) -> String {
        l.get("slash.source.projectSkill")
    }
    pub fn user_command_source(l: &Locale) -> String {
        l.get("slash.source.userCommand")
    }
    pub fn project_command_source(l: &Locale) -> String {
        l.get("slash.source.projectCommand")
    }
    pub fn codex_skill_source(l: &Locale) -> String {
        l.get("slash.source.codexSkill")
    }
    pub fn antigravity_skill_source(l: &Locale) -> String {
        l.get("slash.source.antigravitySkill")
    }
    pub fn palette_move(l: &Locale) -> String {
        l.get("slash.palette.move")
    }
    pub fn palette_select(l: &Locale) -> String {
        l.get("slash.palette.select")
    }
    pub fn palette_dismiss(l: &Locale) -> String {
        l.get("slash.palette.dismiss")
    }
    pub fn palette_count_template(l: &Locale) -> String {
        l.get("slash.palette.count")
    }
    pub fn palette_no_description(l: &Locale) -> String {
        l.get("slash.palette.noDescription")
    }
    pub fn palette_action_tooltip(l: &Locale) -> String {
        l.get("slash.palette.actionHelp")
    }
    pub fn palette_argument_tooltip(l: &Locale) -> String {
        l.get("slash.palette.argumentHelp")
    }
    pub fn palette_current_suffix(l: &Locale) -> String {
        l.get("slash.currentSuffix")
    }
    pub fn note_new_conversation_running(l: &Locale) -> String {
        l.get("slash.note.newConversationRunning")
    }
    pub fn note_new_conversation_nothing_to_resume(l: &Locale) -> String {
        l.get("remote.error.nothingToResume")
    }
    pub fn note_model_running(l: &Locale) -> String {
        l.get("slash.note.modelRunning")
    }
    pub fn note_model_already_template(l: &Locale) -> String {
        l.get("slash.note.modelAlready")
    }
    pub fn note_permission_running(l: &Locale) -> String {
        l.get("slash.note.permissionRunning")
    }
    pub fn note_permission_already_template(l: &Locale) -> String {
        l.get("slash.note.permissionAlready")
    }
}

// ── the files the catalogue reads ───────────────────────────────────────────────────────────────────────────────────────

/// What an entry of a folder is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EntryKind {
    File,
    Directory,
    Other,
}

/// The file reads the catalogue makes (C# `Directory.Exists`, `GetDirectories`, `GetFileSystemEntries`, `File.Exists`,
/// `FileInfo.Length`, `File.ReadAllBytes`), so tests can hand it a fixture tree.
pub trait SlashFiles {
    fn is_dir(&self, path: &Path) -> bool;
    fn is_file(&self, path: &Path) -> bool;
    /// The names in a folder with what each is; an error when it cannot be read.
    fn entries(&self, dir: &Path) -> std::io::Result<Vec<(String, EntryKind)>>;
    /// A file's length, None when it is not there.
    fn len(&self, path: &Path) -> Option<u64>;
    fn read(&self, path: &Path) -> std::io::Result<Vec<u8>>;
}

/// The real file system.
#[derive(Debug, Clone, Copy, Default)]
pub struct DiskFiles;

impl SlashFiles for DiskFiles {
    fn is_dir(&self, path: &Path) -> bool {
        path.is_dir()
    }
    fn is_file(&self, path: &Path) -> bool {
        path.is_file()
    }
    fn entries(&self, dir: &Path) -> std::io::Result<Vec<(String, EntryKind)>> {
        let mut out = Vec::new();
        for entry in std::fs::read_dir(dir)? {
            let entry = entry?;
            let name = entry.file_name().to_string_lossy().into_owned();
            // `Directory.Exists`/`File.Exists` follow links, as `metadata` does.
            let kind = match std::fs::metadata(entry.path()) {
                Ok(m) if m.is_dir() => EntryKind::Directory,
                Ok(m) if m.is_file() => EntryKind::File,
                _ => EntryKind::Other,
            };
            out.push((name, kind));
        }
        Ok(out)
    }
    fn len(&self, path: &Path) -> Option<u64> {
        std::fs::metadata(path).ok().filter(|m| m.is_file()).map(|m| m.len())
    }
    fn read(&self, path: &Path) -> std::io::Result<Vec<u8>> {
        std::fs::read(path)
    }
}

// ── the catalogue ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `SlashCommandCatalog.MaximumCommands`.
pub const MAXIMUM_COMMANDS: usize = 400;

fn utf16_len(value: &str) -> usize {
    value.encode_utf16().count()
}

/// C# `ToLowerInvariant` (simple, one-to-one case mapping).
pub(crate) fn lower_invariant(value: &str) -> String {
    value.chars().map(|c| if c.is_ascii() { c.to_ascii_lowercase() } else { c.to_lowercase().next().unwrap_or(c) }).collect()
}

/// `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` (SlashCommands.cs:39).
fn valid_name(value: &str) -> Option<&str> {
    let b = value.as_bytes();
    let ok = !b.is_empty()
        && b.len() <= 64
        && b[0].is_ascii_alphanumeric()
        && b[1..].iter().all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'_' | b'-'));
    ok.then_some(value)
}

/// C# `SlashCommandCatalog.Query` (SlashCommands.cs:43): the token being completed, or None once a space follows the command.
pub fn query(draft: &str) -> Option<&str> {
    let rest = draft.strip_prefix('/')?;
    if rest.chars().any(char::is_whitespace) || utf16_len(rest) > 80 {
        return None;
    }
    Some(rest)
}

/// C# `SlashCommandCatalog.ArgumentQuery` (SlashCommands.cs:54): `"/model cla"` gives `("model", "cla")`.
pub fn argument_query(draft: &str) -> Option<(&str, &str)> {
    if !draft.starts_with('/') || utf16_len(draft) > 160 {
        return None;
    }
    let rest = &draft[1..];
    let space = rest.find(' ')?;
    let (command, query) = (&rest[..space], &rest[space + 1..]);
    if query.chars().any(char::is_whitespace) {
        return None;
    }
    Some((valid_name(command)?, query))
}

/// C# `SlashCommandCatalog.Builtins` (SlashCommands.cs:72): the app's commands with the CLI names each provider's users know.
pub fn catalog_builtins(provider: &str, l: &Locale) -> Vec<SlashCommand> {
    let source = strings::app_source(l);
    let app = |name: &str, key: &str, action: Option<SlashCommandAction>, argument: Option<SlashArgument>| SlashCommand {
        invocation: name.into(),
        description: l.get(key),
        source: source.clone(),
        origin: SlashCommandOrigin::App,
        action,
        argument,
        action_arg: None,
    };
    use SlashCommandAction::*;
    let model = || app("model", "slash.builtin.model", None, Some(SlashArgument::Model));
    let rename = || app("rename", "slash.builtin.rename", Some(Rename), None);
    let help = || app("help", "slash.builtin.help", Some(Help), None);
    let permission = |name: &str| app(name, "slash.builtin.permission", None, Some(SlashArgument::Permission));
    match provider {
        "claude" => vec![
            app("plugin", "slash.builtin.plugin", Some(OpenPlugins), None),
            model(),
            permission("permissions"),
            app("clear", "slash.builtin.newConversation", Some(NewConversation), None),
            app("cost", "slash.builtin.usage", Some(ShowUsage), None),
            app("usage", "slash.builtin.usage", Some(ShowUsage), None),
            app("config", "slash.builtin.settings", Some(OpenSettings), None),
            rename(),
            help(),
        ],
        "codex" => vec![
            app("plugins", "slash.builtin.plugin", Some(OpenPlugins), None),
            model(),
            permission("approvals"),
            app("new", "slash.builtin.newConversation", Some(NewConversation), None),
            app("status", "slash.builtin.usage", Some(ShowUsage), None),
            app("settings", "slash.builtin.settings", Some(OpenSettings), None),
            rename(),
            help(),
        ],
        "gemini" => vec![
            model(),
            permission("permissions"),
            app("clear", "slash.builtin.newConversation", Some(NewConversation), None),
            app("usage", "slash.builtin.usage", Some(ShowUsage), None),
            app("config", "slash.builtin.settings", Some(OpenSettings), None),
            rename(),
            help(),
        ],
        "pi" => vec![
            model(),
            permission("permissions"),
            app("new", "slash.builtin.newConversation", Some(NewConversation), None),
            app("session", "slash.builtin.usage", Some(ShowUsage), None),
            app("settings", "slash.builtin.settings", Some(OpenSettings), None),
            rename(),
            help(),
        ],
        "opencode" | "hermes" => vec![
            model(),
            permission("permissions"),
            app("new", "slash.builtin.newConversation", Some(NewConversation), None),
            app("usage", "slash.builtin.usage", Some(ShowUsage), None),
            app("settings", "slash.builtin.settings", Some(OpenSettings), None),
            rename(),
            help(),
        ],
        _ => vec![],
    }
}

fn provider_label(provider: &str) -> &str {
    match provider {
        "claude" => "Claude",
        "codex" => "Codex",
        "gemini" => "Antigravity",
        "pi" => "pi",
        "opencode" => "opencode",
        "hermes" => "Hermes",
        other => other,
    }
}

/// C# `SlashCommandCatalog.HelpText` (SlashCommands.cs:128): one line per built-in.
pub fn help_text(provider: &str, l: &Locale) -> String {
    let lines: Vec<String> = catalog_builtins(provider, l).iter().map(|c| format!("/{} · {}", c.invocation, c.description)).collect();
    format!(
        "{}\n{}\n{}",
        l.get_with("slash.help.headerTemplate", &[("provider", provider_label(provider))]),
        lines.join("\n"),
        l.get("slash.help.footer")
    )
}

/// C# `SlashCommandCatalog.Filter` (SlashCommands.cs:138): prefix matches first, then after-colon prefix matches, then
/// substring matches of invocation or description.
pub fn filter(commands: &[SlashCommand], query: &str) -> Vec<SlashCommand> {
    let needle = lower_invariant(query);
    if needle.is_empty() {
        return commands.to_vec();
    }
    let lowered: Vec<String> = commands.iter().map(|c| lower_invariant(&c.invocation)).collect();
    let prefix: Vec<usize> = (0..commands.len()).filter(|&i| lowered[i].starts_with(&needle)).collect();
    // C# keeps sets of the records themselves: an equal record elsewhere in the list counts as taken too.
    let prefix_set: HashSet<&SlashCommand> = prefix.iter().map(|&i| &commands[i]).collect();
    let after_colon: Vec<usize> = (0..commands.len())
        .filter(|&i| {
            !prefix_set.contains(&commands[i]) && commands[i].invocation.split(':').skip(1).any(|p| lower_invariant(p).starts_with(&needle))
        })
        .collect();
    let colon_set: HashSet<&SlashCommand> = after_colon.iter().map(|&i| &commands[i]).collect();
    let contains: Vec<usize> = (0..commands.len())
        .filter(|&i| {
            !prefix_set.contains(&commands[i])
                && !colon_set.contains(&commands[i])
                && (lowered[i].contains(&needle) || lower_invariant(&commands[i].description).contains(&needle))
        })
        .collect();
    prefix.into_iter().chain(after_colon).chain(contains).map(|i| commands[i].clone()).collect()
}

/// C# `SlashCommandCatalog.Commands` (SlashCommands.cs:159): everything available to `provider` for a pane in `workspace`,
/// project entries shadowing user entries of the same invocation, sorted ignoring case, at most 400.
pub fn commands(provider: &str, workspace: Option<&Path>, home: &Path, files: &dyn SlashFiles, l: &Locale) -> Vec<SlashCommand> {
    let mut found = Vec::new();
    let user = SlashCommandOrigin::User;
    let project = SlashCommandOrigin::Project;
    match provider {
        "claude" => {
            found.extend(skills(files, &home.join(".claude").join("skills"), &strings::user_skill_source(l), user, ""));
            found.extend(command_files(files, &home.join(".claude").join("commands"), &strings::user_command_source(l), user, ""));
            found.extend(plugin_commands(files, home, l));
            if let Some(workspace) = workspace {
                found.extend(skills(files, &workspace.join(".claude").join("skills"), &strings::project_skill_source(l), project, ""));
                found.extend(command_files(
                    files,
                    &workspace.join(".claude").join("commands"),
                    &strings::project_command_source(l),
                    project,
                    "",
                ));
            }
        }
        "codex" => {
            found.extend(skills(files, &home.join(".codex").join("skills"), &strings::codex_skill_source(l), user, ""));
            if let Some(workspace) = workspace {
                found.extend(skills(files, &workspace.join(".codex").join("skills"), &strings::project_skill_source(l), project, ""));
            }
        }
        "gemini" => {
            // agy's own skills, then the user's, then the workspace's `.agents/skills` (AntigravityRecords.Folder).
            let agy = home.join(".gemini").join("antigravity-cli");
            let source = strings::antigravity_skill_source(l);
            found.extend(skills(files, &agy.join("builtin").join("skills"), &source, user, ""));
            found.extend(skills(files, &agy.join("skills"), &source, user, ""));
            if let Some(workspace) = workspace {
                found.extend(skills(files, &workspace.join(".agents").join("skills"), &strings::project_skill_source(l), project, ""));
            }
        }
        "pi" => {
            // PiAgent.Directory(home, no environment): ~/.pi/agent.
            found.extend(skills(files, &home.join(".pi").join("agent").join("skills"), &l.get("slash.source.piSkill"), user, ""));
            if let Some(workspace) = workspace {
                found.extend(skills(files, &workspace.join(".pi").join("skills"), &strings::project_skill_source(l), project, ""));
            }
        }
        _ => {}
    }
    // Later sources (project) win over earlier ones; a dictionary keeps the first insertion's place.
    let mut order: Vec<String> = Vec::new();
    let mut by_invocation: HashMap<String, SlashCommand> = HashMap::new();
    for command in found {
        if !by_invocation.contains_key(&command.invocation) {
            order.push(command.invocation.clone());
        }
        by_invocation.insert(command.invocation.clone(), command);
    }
    let mut list: Vec<SlashCommand> = order.into_iter().filter_map(|key| by_invocation.remove(&key)).collect();
    list.sort_by(|a, b| ordinal_ignore_case(&a.invocation, &b.invocation));
    list.truncate(MAXIMUM_COMMANDS);
    list
}

/// `StringComparer.OrdinalIgnoreCase`: UTF-16 units compared upper-cased.
pub(crate) fn ordinal_ignore_case(a: &str, b: &str) -> std::cmp::Ordering {
    let upper = |s: &str| -> Vec<u16> {
        s.chars()
            .flat_map(|c| {
                let u = if c.is_ascii() {
                    c.to_ascii_uppercase()
                } else {
                    c.to_uppercase().next().filter(|_| c.to_uppercase().count() == 1).unwrap_or(c)
                };
                let mut buf = [0u16; 2];
                u.encode_utf16(&mut buf).to_vec()
            })
            .collect()
    };
    upper(a).cmp(&upper(b))
}

fn file_name(name: &str) -> &str {
    name
}

fn sorted_names(mut names: Vec<(String, EntryKind)>) -> Vec<(String, EntryKind)> {
    names.retain(|(n, _)| !n.starts_with('.'));
    names.sort_by(|a, b| normalize::ordinal(&a.0, &b.0));
    names.truncate(MAXIMUM_COMMANDS);
    names
}

/// `<dir>/<name>/SKILL.md`; the frontmatter name wins over the folder name (SlashCommands.cs:204).
fn skills(files: &dyn SlashFiles, directory: &Path, source: &str, origin: SlashCommandOrigin, prefix: &str) -> Vec<SlashCommand> {
    if !files.is_dir(directory) {
        return vec![];
    }
    let Ok(entries) = files.entries(directory) else { return vec![] };
    let folders: Vec<(String, EntryKind)> = entries.into_iter().filter(|(_, k)| *k == EntryKind::Directory).collect();
    let mut result = Vec::new();
    for (folder, _) in sorted_names(folders) {
        let Some(text) = read_file(files, &directory.join(&folder).join("SKILL.md")) else { continue };
        let fields = frontmatter(&text);
        let name = fields.get("name").and_then(|n| valid_name(n)).or_else(|| valid_name(file_name(&folder)));
        let Some(name) = name else { continue };
        let description = fields.get("description").map(|d| clean(d)).unwrap_or_default();
        result.push(SlashCommand::new(&format!("{prefix}{name}"), &description, source, origin));
    }
    result
}

/// `<dir>/<name>.md` and `<dir>/<group>/<name>.md`, invoked as `group:name` (SlashCommands.cs:230).
fn command_files(files: &dyn SlashFiles, directory: &Path, source: &str, origin: SlashCommandOrigin, prefix: &str) -> Vec<SlashCommand> {
    if !files.is_dir(directory) {
        return vec![];
    }
    let mut result = Vec::new();
    let Ok(entries) = files.entries(directory) else { return result };
    for (name, kind) in sorted_names(entries) {
        let entry = directory.join(&name);
        let is_md = name.len() >= 3 && name.is_char_boundary(name.len() - 3) && name[name.len() - 3..].eq_ignore_ascii_case(".md");
        if is_md && kind == EntryKind::File {
            let base = without_extension(&name);
            let Some(base) = valid_name(base) else { continue };
            let Some(text) = read_file(files, &entry) else { continue };
            let fields = frontmatter(&text);
            let description = match fields.get("description") {
                Some(d) => clean(d),
                None => first_line(&text),
            };
            result.push(SlashCommand::new(&format!("{prefix}{base}"), &description, source, origin));
        } else if kind == EntryKind::Directory {
            let Some(group) = valid_name(&name) else { continue };
            result.extend(command_files(files, &entry, source, origin, &format!("{prefix}{group}:")));
        }
    }
    result
}

/// `Path.GetFileNameWithoutExtension`.
fn without_extension(name: &str) -> &str {
    match name.rfind('.') {
        Some(dot) => &name[..dot],
        None => name,
    }
}

/// `Path.IsPathRooted` for the OS the paths are read on.
fn path_rooted(value: &str) -> bool {
    let path = Path::new(value);
    path.has_root() || path.is_absolute() || matches!(path.components().next(), Some(std::path::Component::Prefix(_)))
}

/// Installed Claude plugins (`installed_plugins.json`): each plugin's `skills/` and `commands/` invoked as `<plugin>:<name>`
/// (SlashCommands.cs:265).
fn plugin_commands(files: &dyn SlashFiles, home: &Path, l: &Locale) -> Vec<SlashCommand> {
    let registry = home.join(".claude").join("plugins").join("installed_plugins.json");
    if !files.is_file(&registry) {
        return vec![];
    }
    let Ok(bytes) = files.read(&registry) else { return vec![] };
    let Ok(root) = serde_json::from_slice::<serde_json::Value>(&bytes) else { return vec![] };
    let Some(plugins) = root.get("plugins").and_then(|p| p.as_object()) else { return vec![] };
    let mut entries: Vec<(&String, &serde_json::Value)> = plugins.iter().collect();
    entries.sort_by(|a, b| normalize::ordinal(a.0, b.0));
    let mut result = Vec::new();
    for (key, value) in entries {
        let Some(plugin) = valid_name(key.split('@').next().unwrap_or("")) else { continue };
        let Some(installs) = value.as_array() else { continue };
        let path = installs
            .iter()
            .filter_map(|install| install.get("installPath").and_then(|p| p.as_str()))
            .find(|candidate| !candidate.is_empty() && path_rooted(candidate));
        let Some(path) = path else { continue };
        let source = l.get_with("slash.pluginSourceTemplate", &[("plugin", plugin)]);
        let root = PathBuf::from(path);
        let prefix = format!("{plugin}:");
        result.extend(skills(files, &root.join("skills"), &source, SlashCommandOrigin::Plugin, &prefix));
        result.extend(command_files(files, &root.join("commands"), &source, SlashCommandOrigin::Plugin, &prefix));
    }
    result
}

/// C# `SlashCommandCatalog.Frontmatter` (SlashCommands.cs:303): `---\nkey: value\n---` at the top; quoted values unquoted,
/// folded (`>`) and literal (`|`) values skipped. Keys keep their first place; a later value replaces an earlier one.
pub fn frontmatter(text: &str) -> HashMap<String, String> {
    let lines: Vec<&str> = text.split('\n').collect();
    let mut fields = HashMap::new();
    if lines.first().map(|l| dotnet_trim(l)) != Some("---") {
        return fields;
    }
    for line in lines.iter().skip(1).take(200) {
        if dotnet_trim(line) == "---" {
            break;
        }
        if line.starts_with(' ') || line.starts_with('\t') {
            continue;
        }
        let Some(colon) = line.find(':') else { continue };
        let key = dotnet_trim(&line[..colon]);
        let mut value = dotnet_trim(&line[colon + 1..]);
        let first = value.chars().next();
        let last = value.chars().last();
        if utf16_len(value) >= 2 && first == last && matches!(first, Some('"') | Some('\'')) {
            value = &value[1..value.len() - 1];
        }
        if !key.is_empty() && !value.is_empty() && !value.starts_with('>') && !value.starts_with('|') {
            fields.insert(key.to_string(), value.to_string());
        }
    }
    fields
}

/// C# `string.Trim()`: white space as `char.IsWhiteSpace` counts it.
pub(crate) fn dotnet_trim(value: &str) -> &str {
    value.trim_matches(char::is_whitespace)
}

/// C# `ReadFile` (SlashCommands.cs:325): at most 512 KiB, read as `File.ReadAllText(path, UTF8)` reads it.
fn read_file(files: &dyn SlashFiles, path: &Path) -> Option<String> {
    let length = files.len(path)?;
    if length > 512 * 1024 {
        return None;
    }
    files.read(path).ok().map(|bytes| text::read_all_text(&bytes))
}

/// C# `Clean` (SlashCommands.cs:341): one line, trimmed, at most 240 UTF-16 units.
fn clean(value: &str) -> String {
    let joined = value.replace('\n', " ");
    let s = dotnet_trim(&joined);
    text::prefix_utf16(s, 240)
}

/// C# `FirstLine` (SlashCommands.cs:348): the first line that is neither blank nor a heading, after the frontmatter.
fn first_line(text: &str) -> String {
    let body = if frontmatter(text).is_empty() {
        text
    } else {
        match text.find("\n---") {
            Some(at) => &text[at + 4..],
            None => text,
        }
    };
    let line = body.split('\n').find(|l| !dotnet_trim(l).is_empty() && !l.trim_start_matches(char::is_whitespace).starts_with('#'));
    clean(line.unwrap_or(""))
}

// ── choosing ────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `SlashChoiceEffect` (SlashCommands.cs:380).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SlashChoiceEffect {
    Insert,
    AppAction,
    ArgumentCompletion,
}

/// C# `SlashChoice` (SlashCommands.cs:384).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SlashChoice {
    pub draft: String,
    pub effect: SlashChoiceEffect,
    pub action: Option<SlashCommandAction>,
    pub action_arg: Option<String>,
}

/// C# `SlashSubmission` (SlashCommands.cs:390).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SlashSubmission {
    pub command: Option<SlashCommand>,
    pub mismatch: Option<String>,
}

/// C# `SlashPaletteState` (SlashCommands.cs:395).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SlashPaletteState {
    pub commands: Vec<SlashCommand>,
    pub highlighted_index: i32,
}

impl SlashPaletteState {
    pub fn closed() -> Self {
        SlashPaletteState::default()
    }
    pub fn open(commands: Vec<SlashCommand>) -> Self {
        SlashPaletteState { commands, highlighted_index: 0 }
    }
    pub fn is_open(&self) -> bool {
        !self.commands.is_empty()
    }
    /// macOS reads the highlight as `min(paletteIndex, count - 1)`.
    pub fn safe_index(&self) -> usize {
        if self.commands.is_empty() {
            return 0;
        }
        (self.highlighted_index.max(0) as usize).min(self.commands.len() - 1)
    }
    pub fn highlighted(&self) -> Option<&SlashCommand> {
        self.commands.get(self.safe_index()).filter(|_| !self.commands.is_empty())
    }
    pub fn move_up(&self) -> Self {
        if self.commands.is_empty() {
            return self.clone();
        }
        let count = self.commands.len();
        SlashPaletteState { highlighted_index: ((self.safe_index() + count - 1) % count) as i32, ..self.clone() }
    }
    pub fn move_down(&self) -> Self {
        if self.commands.is_empty() {
            return self.clone();
        }
        let count = self.commands.len();
        SlashPaletteState { highlighted_index: ((self.safe_index() + 1) % count) as i32, ..self.clone() }
    }
    pub fn choose(&self) -> SlashChoice {
        choose(self.highlighted())
    }
}

/// C# `SlashPaletteState.Choose(SlashCommand?)` (SlashCommands.cs:410).
pub fn choose(command: Option<&SlashCommand>) -> SlashChoice {
    let Some(command) = command else {
        return SlashChoice { draft: String::new(), effect: SlashChoiceEffect::Insert, action: None, action_arg: None };
    };
    if let Some(action) = command.action {
        return SlashChoice {
            draft: String::new(),
            effect: SlashChoiceEffect::AppAction,
            action: Some(action),
            action_arg: command.action_arg.clone(),
        };
    }
    SlashChoice {
        draft: format!("/{} ", command.invocation),
        effect: if command.argument.is_none() { SlashChoiceEffect::Insert } else { SlashChoiceEffect::ArgumentCompletion },
        action: None,
        action_arg: None,
    }
}

/// The choices a built-in's argument completes to (`/model ` lists the models), invoked as `"name value"`.
pub type ArgumentChoices<'a> = &'a dyn Fn(SlashArgument, &str) -> Vec<SlashCommand>;

/// C# `SlashPalette` (SlashCommands.cs:422).
pub mod palette {
    use super::*;

    /// macOS shows at most 60 rows.
    pub const MAXIMUM_ROWS: usize = 60;

    /// No action is universally unavailable (SlashCommands.cs:429).
    pub const UNAVAILABLE_ACTIONS: [SlashCommandAction; 0] = [];

    /// The built-ins the Windows palette offers.
    pub fn builtins(provider: &str, l: &Locale) -> Vec<SlashCommand> {
        catalog_builtins(provider, l).into_iter().filter(|c| c.action.is_none_or(|a| !UNAVAILABLE_ACTIONS.contains(&a))).collect()
    }

    /// The draft while it is a `/name` being typed or a built-in's `/name arg`, else None (SlashCommands.cs:440).
    pub fn draft<'a>(kind: &str, draft: &'a str, dismissed_for: Option<&str>) -> Option<&'a str> {
        if kind == "shell" || dismissed_for == Some(draft) {
            return None;
        }
        (query(draft).is_some() || argument_query(draft).is_some()).then_some(draft)
    }

    /// What the list shows for `draft` (SlashCommands.cs:450).
    pub fn rows(
        provider: &str,
        kind: &str,
        draft: &str,
        scanned: &[SlashCommand],
        choices: Option<ArgumentChoices>,
        l: &Locale,
    ) -> Vec<SlashCommand> {
        if kind == "shell" {
            return vec![];
        }
        let builtins = builtins(provider, l);
        if let Some(q) = query(draft) {
            let names: HashSet<&str> = builtins.iter().map(|c| c.invocation.as_str()).collect();
            let mut all = builtins.clone();
            all.extend(scanned.iter().filter(|c| !names.contains(c.invocation.as_str())).cloned());
            return filter(&all, q);
        }
        let Some((command, q)) = argument_query(draft) else { return vec![] };
        let argument = builtins.iter().find(|c| c.invocation == command).and_then(|c| c.argument);
        let (Some(argument), Some(choices)) = (argument, choices) else { return vec![] };
        filter(&choices(argument, command), &format!("{command} {q}"))
    }

    /// The whole state for a draft, the highlight on the first row (SlashCommands.cs:468).
    pub fn state(
        provider: &str,
        kind: &str,
        draft_text: &str,
        dismissed_for: Option<&str>,
        scanned: &[SlashCommand],
        choices: Option<ArgumentChoices>,
        l: &Locale,
    ) -> SlashPaletteState {
        let Some(text) = draft(kind, draft_text, dismissed_for) else { return SlashPaletteState::closed() };
        let mut rows = rows(provider, kind, text, scanned, choices, l);
        if rows.is_empty() {
            return SlashPaletteState::closed();
        }
        rows.truncate(MAXIMUM_ROWS);
        SlashPaletteState::open(rows)
    }

    /// The built-in the trimmed draft names exactly (SlashCommands.cs:478).
    pub fn typed_builtin(provider: &str, draft: &str, l: &Locale) -> Option<SlashCommand> {
        let text = dotnet_trim(draft);
        let name = query(text).or_else(|| argument_query(text).map(|(c, _)| c))?;
        builtins(provider, l).into_iter().find(|c| c.invocation == name)
    }

    /// A send of a built-in typed out in full (SlashCommands.cs:487): None when the draft is ordinary text for the CLI.
    pub fn submitted(
        provider: &str,
        kind: &str,
        draft: &str,
        has_attachments: bool,
        choices: Option<ArgumentChoices>,
        l: &Locale,
    ) -> Option<SlashSubmission> {
        if kind == "shell" || has_attachments {
            return None;
        }
        let builtin = typed_builtin(provider, draft, l)?;
        let Some(argument) = builtin.argument else { return Some(SlashSubmission { command: Some(builtin), mismatch: None }) };
        let q = argument_query(dotnet_trim(draft)).map(|(_, q)| q).unwrap_or("");
        let listed = rows(provider, kind, &format!("/{} ", builtin.invocation), &[], choices, l);
        let wanted = format!("{} {}", builtin.invocation, q);
        if let Some(exact) = listed.into_iter().find(|c| c.invocation == wanted) {
            return Some(SlashSubmission { command: Some(exact), mismatch: None });
        }
        if q.is_empty() {
            return Some(SlashSubmission { command: Some(builtin), mismatch: None });
        }
        // ExecutionGraphSupport.Prefix(query, 80): the first 80 text elements.
        let shown = mighty_core::wire::text_elements(q, 80).0;
        let values = [("query", shown), ("command", builtin.invocation.as_str())];
        let key = if argument == SlashArgument::Model { "slash.note.noModelMatch" } else { "slash.note.noPermissionMatch" };
        Some(SlashSubmission { command: None, mismatch: Some(l.get_with(key, &values)) })
    }

    /// The model rows `/model ` lists (SlashCommands.cs:502).
    pub fn model_choices(command: &str, options: &[(String, String)], current: &str, l: &Locale) -> Vec<SlashCommand> {
        let suffix = strings::palette_current_suffix(l);
        let source = strings::model_source(l);
        options
            .iter()
            .map(|(value, display)| SlashCommand {
                invocation: format!("{command} {value}"),
                description: format!("{display}{}", if value == current { suffix.as_str() } else { "" }),
                source: source.clone(),
                origin: SlashCommandOrigin::App,
                action: Some(SlashCommandAction::SetModel),
                argument: None,
                action_arg: Some(value.clone()),
            })
            .collect()
    }

    /// The permission-mode rows `/permissions ` lists (SlashCommands.cs:511).
    pub fn permission_choices(
        command: &str,
        modes: &[String],
        current: &str,
        label: &dyn Fn(&str) -> String,
        l: &Locale,
    ) -> Vec<SlashCommand> {
        let suffix = strings::palette_current_suffix(l);
        let source = strings::permission_source(l);
        modes
            .iter()
            .map(|mode| SlashCommand {
                invocation: format!("{command} {mode}"),
                description: format!("{}{}", label(mode), if mode == current { suffix.as_str() } else { "" }),
                source: source.clone(),
                origin: SlashCommandOrigin::App,
                action: Some(SlashCommandAction::SetPermission),
                argument: None,
                action_arg: Some(mode.clone()),
            })
            .collect()
    }

    /// A row's description, or the shared "no description" text (SlashCommands.cs:519).
    pub fn description(command: &SlashCommand, l: &Locale) -> String {
        if command.description.is_empty() { strings::palette_no_description(l) } else { command.description.clone() }
    }

    /// The footer's count (SlashCommands.cs:523).
    pub fn count_label(count: usize, l: &Locale) -> String {
        strings::palette_count_template(l).replace("{count}", &count.to_string())
    }
}

// ── the cache ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `SlashCatalogCache` (SlashCommands.cs:530): scanned catalogues kept 30 seconds per provider and workspace.
#[derive(Debug, Default)]
pub struct SlashCatalogCache {
    cache: HashMap<String, CacheEntry>,
}

#[derive(Debug, Clone)]
struct CacheEntry {
    commands: Vec<SlashCommand>,
    scanned_at: Instant,
    offers_ask: bool,
}

const TTL: Duration = Duration::from_secs(30);

impl SlashCatalogCache {
    fn key(provider: &str, workspace: Option<&str>) -> String {
        format!("{provider}|{}", workspace.unwrap_or(""))
    }

    pub fn is_stale(&self, provider: &str, workspace: Option<&str>) -> bool {
        self.is_stale_at(provider, workspace, Instant::now())
    }

    pub fn is_stale_at(&self, provider: &str, workspace: Option<&str>, now: Instant) -> bool {
        self.cache.get(&Self::key(provider, workspace)).is_none_or(|e| now.saturating_duration_since(e.scanned_at) > TTL)
    }

    pub fn get(&self, provider: &str, workspace: Option<&str>) -> Option<&[SlashCommand]> {
        self.get_at(provider, workspace, Instant::now())
    }

    pub fn get_at(&self, provider: &str, workspace: Option<&str>, now: Instant) -> Option<&[SlashCommand]> {
        self.cache
            .get(&Self::key(provider, workspace))
            .filter(|e| now.saturating_duration_since(e.scanned_at) <= TTL)
            .map(|e| e.commands.as_slice())
    }

    pub fn set(&mut self, provider: &str, workspace: Option<&str>, commands: Vec<SlashCommand>) {
        self.set_at(provider, workspace, commands, Instant::now());
    }

    pub fn set_at(&mut self, provider: &str, workspace: Option<&str>, commands: Vec<SlashCommand>, now: Instant) {
        let offers_ask = commands.iter().any(|c| c.invocation == crate::ask_targets::SKILL);
        self.cache.insert(Self::key(provider, workspace), CacheEntry { commands, scanned_at: now, offers_ask });
    }

    /// Whether the last scan, however old, found oh-my-claudecode's ask; None before the first scan.
    pub fn offers_ask(&self, provider: &str, workspace: Option<&str>) -> Option<bool> {
        self.cache.get(&Self::key(provider, workspace)).map(|e| e.offers_ask)
    }

    pub fn clear_provider(&mut self, provider: &str) {
        let prefix = format!("{provider}|");
        self.cache.retain(|key, _| !key.starts_with(&prefix));
    }
}
