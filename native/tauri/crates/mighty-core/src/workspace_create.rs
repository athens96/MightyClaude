//! The rules for adding a workspace from a new folder or a Git address (C# `WorkspaceCreate`, MightyCode.Core/WorkspaceCreate.cs, and
//! the window's `CheckCreate`, MainWindow.WorkspaceCreate.cs:60), held to native/contracts/fixtures/workspace-create.json as the Mac's
//! and C#'s are. Name problems and Git error kinds are the fixture's camelCase names: `empty`, `badCharacter`, `reserved`,
//! `trailingDotOrSpace`, `dots`, `tooLong`; `gitMissing`, `invalidURL`, `auth`, `notFound`, `network`, `other`. Running git is the
//! host's (it goes through the platform's `ProcessHost`).

use std::path::{Path, PathBuf};

use crate::locale::Locale;

/// One step of a clone (C# `CloneProgress`): `starting`, `receiving`, `resolving` or `checkout`, git's percent, and the whole clone's
/// share done (0–1).
#[derive(Debug, Clone, PartialEq)]
pub struct CloneProgress {
    pub phase: String,
    pub percent: i32,
    pub fraction: f64,
}

impl CloneProgress {
    pub fn new(phase: &str, percent: i32, fraction: f64) -> CloneProgress {
        CloneProgress { phase: phase.to_string(), percent, fraction }
    }
}

const SCHEMES: [&str; 5] = ["https://", "http://", "ssh://", "git://", "file://"];

/// `SchemeOf`: the known scheme the address starts with, in any case.
fn scheme_of(url: &str) -> Option<&'static str> {
    SCHEMES.into_iter().find(|scheme| url.get(..scheme.len()).is_some_and(|start| start.eq_ignore_ascii_case(scheme)))
}

/// `^[^/\s@]+@[^/\s:]+:\S+$`: scp-style `user@host:path`.
fn scp_address(url: &str) -> bool {
    let Some((user, rest)) = url.split_once('@') else { return false };
    let Some((host, path)) = rest.split_once(':') else { return false };
    !user.is_empty()
        && !user.contains(|c: char| c == '/' || c.is_whitespace())
        && !host.is_empty()
        && !host.contains(|c: char| c == '/' || c.is_whitespace())
        && !path.is_empty()
        && !path.contains(char::is_whitespace)
}

/// `^[A-Za-z]:[\\/]`: a drive path.
fn drive_path(url: &str) -> bool {
    let bytes = url.as_bytes();
    bytes.len() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && matches!(bytes[2], b'\\' | b'/')
}

/// `LooksLikeGitUrl`: a known scheme with more after it, scp-style `user@host:path`, or a local or UNC path.
pub fn looks_like_git_url(raw: &str) -> bool {
    let url = raw.trim();
    if url.is_empty() || url.contains(char::is_whitespace) {
        return false;
    }
    if let Some(scheme) = scheme_of(url) {
        return url.len() > scheme.len();
    }
    scp_address(url) || url.starts_with('/') || url.starts_with(r"\\") || drive_path(url)
}

/// `RepoName`: the folder name an address suggests, its last path piece without `.git`; none when it is no Git address or names no
/// repository.
pub fn repo_name(raw: &str) -> Option<String> {
    if !looks_like_git_url(raw) {
        return None;
    }
    let mut url = raw.trim();
    if let Some(cut) = url.find(['?', '#']) {
        url = &url[..cut];
    }
    url = url.trim_end_matches(['/', '\\']);
    if url.len() >= 4 && url.get(url.len() - 4..).is_some_and(|end| end.eq_ignore_ascii_case(".git")) {
        url = &url[..url.len() - 4];
    }
    let after_scheme = scheme_of(url).map(|scheme| &url[scheme.len()..]);
    let (path, separators): (&str, &[char]) = match after_scheme {
        Some(after) => (after.find('/').map_or("", |slash| &after[slash..]), &['/', '\\']),
        None => (url, &['/', '\\', ':']),
    };
    path.split(separators).rfind(|piece| !piece.is_empty()).map(str::to_string)
}

const RESERVED: [&str; 22] = [
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
    "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
];

/// `CheckName`: why `raw` cannot name a new folder, or none when it can. `windows` adds Windows' own rules.
pub fn check_name(raw: &str, windows: bool) -> Option<&'static str> {
    let name = raw.trim();
    if name.is_empty() {
        return Some("empty");
    }
    if name == "." || name == ".." {
        return Some("dots");
    }
    if name.len() > 255 {
        return Some("tooLong");
    }
    if name.chars().any(|c| matches!(c, '/' | '\\' | ':') || c.is_control()) {
        return Some("badCharacter");
    }
    if !windows {
        return None;
    }
    if name.chars().any(|c| matches!(c, '<' | '>' | '"' | '|' | '?' | '*')) {
        return Some("badCharacter");
    }
    let base = name.split('.').next().unwrap_or("").to_uppercase();
    if RESERVED.contains(&base.as_str()) {
        return Some("reserved");
    }
    if name.ends_with('.') || name.ends_with(' ') {
        return Some("trailingDotOrSpace");
    }
    None
}

const ERRORS: [(&str, &[&str]); 4] = [
    (
        "auth",
        &[
            "authentication failed",
            "could not read username",
            "could not read password",
            "permission denied (publickey",
            "access denied",
            "error: 401",
            "error: 403",
        ],
    ),
    ("notFound", &["not found", "error: 404", "does not appear to be a git repository"]),
    (
        "network",
        &[
            "could not resolve host",
            "could not resolve hostname",
            "connection refused",
            "timed out",
            "network is unreachable",
            "failed to connect",
            "connection reset",
        ],
    ),
    ("invalidURL", &["is not supported", "not a valid", "invalid url"]),
];

/// `ErrorKind`: what git's error text says went wrong: `auth`, `notFound`, `network`, `invalidURL` or `other`.
pub fn error_kind(stderr: &str) -> &'static str {
    let text = stderr.to_ascii_lowercase();
    ERRORS.iter().find(|(_, needles)| needles.iter().any(|needle| text.contains(needle))).map_or("other", |(kind, _)| kind)
}

/// `\d{1,3}(?=%)`: the first one to three digits a `%` follows.
fn percent_in(line: &str) -> Option<i32> {
    let chars: Vec<char> = line.chars().collect();
    for start in 0..chars.len() {
        for length in (1..=3).rev() {
            let end = start + length;
            if end < chars.len() && chars[start..end].iter().all(char::is_ascii_digit) && chars[end] == '%' {
                return chars[start..end].iter().collect::<String>().parse().ok();
            }
        }
    }
    None
}

/// `Progress`: the clone's progress a line of `git clone --progress` stderr gives, or none for any other line.
pub fn progress(raw: &str) -> Option<CloneProgress> {
    let line = raw.trim();
    let make = |phase: &str, start: f64, span: f64| {
        let percent = percent_in(line)?.min(100);
        // Math.Round(start + span * percent / 100, 3, MidpointRounding.AwayFromZero).
        let fraction = ((start + span * f64::from(percent) / 100.0) * 1000.0).round() / 1000.0;
        Some(CloneProgress::new(phase, percent, fraction))
    };
    if line.starts_with("Receiving objects:") {
        return make("receiving", 0.0, 0.7);
    }
    if line.starts_with("Resolving deltas:") {
        return make("resolving", 0.7, 0.2);
    }
    if line.starts_with("Updating files:") || line.starts_with("Checking out files:") {
        return make("checkout", 0.9, 0.1);
    }
    if line.starts_with("Cloning into") || line.starts_with("remote:") {
        return Some(CloneProgress::new("starting", 0, 0.0));
    }
    None
}

/// `NameMessage`: what the create sheets say about a folder name that cannot be used (`check_name`'s answer, or `exists`).
pub fn name_message(problem: &str, locale: &Locale) -> String {
    locale.get(match problem {
        "empty" => "workspace.name.error.empty",
        "badCharacter" => "workspace.name.error.badCharacter",
        "reserved" => "workspace.name.error.reserved",
        "trailingDotOrSpace" => "workspace.name.error.trailingDotOrSpace",
        "dots" => "workspace.name.error.dots",
        "tooLong" => "workspace.name.error.tooLong",
        _ => "workspace.name.error.exists",
    })
}

/// `ErrorMessage`: what the Git sheet says about a failed check or clone; `detail` is git's own text, shown for `other`.
pub fn error_message(kind: &str, detail: Option<&str>, locale: &Locale) -> String {
    match kind {
        "gitMissing" => locale.get("workspace.git.error.gitMissing"),
        "invalidURL" => locale.get("workspace.git.error.invalidURL"),
        "auth" => locale.get("workspace.git.error.auth"),
        "notFound" => locale.get("workspace.git.error.notFound"),
        "network" => locale.get("workspace.git.error.network"),
        _ => locale.get_with("workspace.git.error.other", &[("message", detail.unwrap_or(""))]),
    }
}

/// `ProgressMessage`: the clone's progress line.
pub fn progress_message(progress: &CloneProgress, locale: &Locale) -> String {
    let percent = progress.percent.to_string();
    match progress.phase.as_str() {
        "receiving" => locale.get_with("workspace.git.progress.receiving", &[("percent", &percent)]),
        "resolving" => locale.get_with("workspace.git.progress.resolving", &[("percent", &percent)]),
        "checkout" => locale.get_with("workspace.git.progress.checkout", &[("percent", &percent)]),
        _ => locale.get("workspace.git.progress.starting"),
    }
}

/// `Target`: the new workspace's folder, `name` trimmed, in `location` (`Path.Combine`: a rooted name stands alone).
pub fn target(location: &str, name: &str) -> PathBuf {
    Path::new(location).join(name.trim())
}

/// `Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))`: the folder holding `path`, none for a root.
pub fn parent_folder(path: &Path) -> Option<PathBuf> {
    path.parent().filter(|parent| !parent.as_os_str().is_empty()).map(Path::to_path_buf)
}

/// `DefaultLocation`: the last location used if it is still a folder, else the active workspace's parent folder, else home.
pub fn default_location(last: Option<&str>, active_workspace_path: Option<&str>, home: &str) -> String {
    if let Some(last) = last.filter(|last| Path::new(last).is_dir()) {
        return last.to_string();
    }
    if let Some(parent) = active_workspace_path.and_then(|path| parent_folder(Path::new(path))).filter(|parent| parent.is_dir()) {
        return parent.to_string_lossy().into_owned();
    }
    home.to_string()
}

/// The window's `CheckCreate`: what a name in a location gives, with the first reason it cannot be made, worded (the Mac's order:
/// name, location, an existing file or folder).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateCheck {
    pub problem: Option<&'static str>,
    pub target: String,
    pub exists: bool,
    pub location_missing: bool,
    pub message: Option<String>,
}

impl CreateCheck {
    /// `CanCreate`.
    pub fn can_create(&self) -> bool {
        self.problem.is_none() && !self.exists && !self.location_missing
    }
}

/// `CheckCreate(location, name)` with Windows' name rules where `windows` (C# always applies them).
pub fn check_create(location: &str, name: &str, windows: bool, locale: &Locale) -> CreateCheck {
    let problem = check_name(name, windows);
    let target = target(location, name);
    let location_missing = !Path::new(location).is_dir();
    let exists = problem.is_none() && target.exists();
    let message = match problem {
        Some(problem) => Some(name_message(problem, locale)),
        None if location_missing => Some(locale.get("workspace.location.error.missing")),
        None if exists => Some(name_message("exists", locale)),
        None => None,
    };
    CreateCheck { problem, target: target.to_string_lossy().into_owned(), exists, location_missing, message }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percents_are_found_as_the_regex_finds_them() {
        assert_eq!(percent_in("Receiving objects:  42% (420/1000)"), Some(42));
        assert_eq!(percent_in("1234%"), Some(234), "the leftmost one to three digits a % follows");
        assert_eq!(percent_in("no percent"), None);
        assert_eq!(percent_in("100% done"), Some(100));
    }

    #[test]
    fn scp_addresses_need_a_user_a_host_and_a_path() {
        assert!(scp_address("git@github.com:a/b.git"));
        assert!(scp_address("a@b@c:d"), "the host may hold an @, as the regex lets it");
        assert!(!scp_address("@host:path"));
        assert!(!scp_address("git@:path"));
        assert!(!scp_address("git@host:"));
        assert!(!scp_address("a/b@host:path"));
    }

    #[test]
    fn a_location_defaults_to_the_last_then_the_active_parent_then_home() {
        let root = std::env::temp_dir().join(format!("mighty-create-default-{}", uuid::Uuid::new_v4()));
        let work = root.join("work");
        std::fs::create_dir_all(&work).unwrap();
        let (root_text, work_text) = (root.to_string_lossy().into_owned(), work.to_string_lossy().into_owned());
        assert_eq!(default_location(Some(&work_text), None, "home"), work_text);
        assert_eq!(default_location(Some("Z:\\no\\such\\folder"), Some(&work_text), "home"), root_text);
        assert_eq!(default_location(None, Some(&format!("{work_text}{}", std::path::MAIN_SEPARATOR)), "home"), root_text);
        assert_eq!(default_location(None, None, "home"), "home");
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn a_check_names_the_first_problem() {
        let en = Locale::new("en");
        let root = std::env::temp_dir().join(format!("mighty-create-check-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(root.join("taken")).unwrap();
        let location = root.to_string_lossy().into_owned();
        let free = check_create(&location, "  new-one ", true, &en);
        assert!(free.can_create() && free.message.is_none());
        assert_eq!(free.target, root.join("new-one").to_string_lossy());
        let taken = check_create(&location, "taken", true, &en);
        assert!(taken.exists && !taken.can_create());
        assert_eq!(taken.message, Some(en.get("workspace.name.error.exists")));
        let bad = check_create(&location, "a?b", true, &en);
        assert_eq!((bad.problem, bad.exists), (Some("badCharacter"), false));
        let nowhere = check_create(&root.join("missing").to_string_lossy(), "x", true, &en);
        assert!(nowhere.location_missing);
        assert_eq!(nowhere.message, Some(en.get("workspace.location.error.missing")));
        std::fs::remove_dir_all(&root).unwrap();
    }
}
