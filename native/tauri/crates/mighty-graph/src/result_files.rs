//! The file paths a final result names (C# `ResultFiles`, MightyCode.Core/ResultFiles.cs; macOS ReferenceLinkSupport). Detection is
//! purely textual; resolution goes through [`ResultFileLookup`] and only ever yields an existing regular file inside the workspace root,
//! so a model-written path cannot reach other files.

use std::collections::HashSet;
use std::path::{Component, Path, PathBuf};
use std::sync::LazyLock;

use mighty_core::normalize::prefix_utf8;
use regex::Regex;

pub const MAXIMUM_RESULT_FILES: usize = 100;
pub const MAXIMUM_RESULT_TEXT_BYTES: usize = 512 * 1024;
pub const MAXIMUM_RESULT_TEXTS: usize = 200;
pub const MAXIMUM_PATH_BYTES: usize = 1_024;
pub const MAXIMUM_MATCHES_PER_TEXT: usize = 200;

/// `PathPattern` without its look-arounds, anchored: a path needs a directory segment or a drive prefix and an ASCII extension. The
/// negative look-ahead `(?![A-Za-z0-9_/\\])` is the last group (consumed, then given back), the look-behind is checked by hand
/// (`path_matches`); the regex crate's leftmost-first match is the one .NET's backtracking finds.
static PATH_PATTERN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"^((?:[A-Za-z]:[/\\](?:[\p{L}\p{N}_.@-]+[/\\])*|(?:\.{1,2}[/\\]|[/\\])?(?:[\p{L}\p{N}_.@-]+[/\\])+)[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,8})(?::(\d{1,6}))?(?:[^A-Za-z0-9_/\\]|\z)",
    )
    .expect("regex")
});
static WEB_PATTERN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"https?://[^\s<>"'`)\]]+"#).expect("regex"));
/// Inline Markdown link: the destination is a reference, its label is not.
static MARKDOWN_LINK: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"\[(?<label>[^\]\[]*)\]\(\s*<?(?<dest>[^)\s<>]*)>?(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?\s*\)"#).expect("regex")
});
static SCHEME: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^([A-Za-z][A-Za-z0-9+.-]*):").expect("regex"));

/// `PathMatch(Path, Line?)`: a plain-text path reference.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PathMatch {
    pub path: String,
    pub line: Option<i32>,
}

/// `ResultFile(Path, Line?)`: a workspace file a result names, relative to the root with forward slashes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResultFile {
    pub path: String,
    pub line: Option<i32>,
}

/// The look-behind of `PathPattern`: no path character right before a match.
fn path_may_start(text: &str, at: usize) -> bool {
    !text[..at]
        .chars()
        .next_back()
        .is_some_and(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '/' | '\\' | '.' | ':' | '@' | '~' | '-'))
}

/// Every `PathPattern` match in `text`, as .NET's `Matches` finds them: (start, end, path, line).
fn path_matches(text: &str) -> Vec<(usize, usize, String, Option<String>)> {
    let mut found = vec![];
    let mut at = 0;
    while at <= text.len() && found.len() < MAXIMUM_MATCHES_PER_TEXT {
        let Some(c) = text[at..].chars().next() else { break };
        if path_may_start(text, at)
            && let Some(captures) = PATH_PATTERN.captures(&text[at..])
        {
            let path = captures.get(1).expect("the path group");
            let line = captures.get(2);
            let end = at + line.map_or(path.end(), |l| l.end());
            found.push((at, end, path.as_str().to_string(), line.map(|l| l.as_str().to_string())));
            at = end.max(at + c.len_utf8());
            continue;
        }
        at += c.len_utf8();
    }
    found
}

/// `ResultFiles.Matches`: plain-text path references, web addresses excluded.
pub fn matches(text: &str) -> Vec<PathMatch> {
    let mut taken = vec![];
    for web in WEB_PATTERN.find_iter(text).take(MAXIMUM_MATCHES_PER_TEXT) {
        let mut end = web.end();
        while end - web.start() > 1 && text[..end].ends_with(['.', ',', ';', ':', '!', '?']) {
            end -= 1;
        }
        taken.push((web.start(), end));
    }
    let mut result = vec![];
    for (start, end, path, line) in path_matches(text) {
        if taken.iter().any(|(s, e)| (*e).min(end) > (*s).max(start)) {
            continue;
        }
        if path.len() > MAXIMUM_PATH_BYTES {
            continue;
        }
        result.push(PathMatch { path, line: line.and_then(|l| l.parse().ok()) });
    }
    result
}

/// `ResultFiles.LocalPath`: a relative or `file:` destination from Markdown link syntax, as a path; none for any other scheme.
pub fn local_path(destination: &str) -> Option<String> {
    if destination.is_empty() {
        return None;
    }
    let bytes = destination.as_bytes();
    let scheme = SCHEME.captures(destination);
    let drive = bytes.len() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && matches!(bytes[2], b'/' | b'\\');
    if let Some(scheme) = &scheme
        && !drive
        && !scheme[1].eq_ignore_ascii_case("file")
    {
        return None;
    }
    let mut value = destination;
    if scheme.is_some() && !drive {
        if !value.get(..7).is_some_and(|head| head.eq_ignore_ascii_case("file://")) {
            return None;
        }
        value = &value[7..];
        // A host is not a local path.
        if !value.starts_with('/') {
            return None;
        }
    }
    let mut clean = percent_encoding::percent_decode_str(value).decode_utf8().map(|v| v.into_owned()).unwrap_or_else(|_| value.to_string());
    let c = clean.as_bytes();
    if c.len() >= 4 && c[0] == b'/' && c[1].is_ascii_alphabetic() && c[2] == b':' && c[3] == b'/' {
        clean.remove(0);
    }
    if clean.is_empty() || clean.contains("://") || clean.contains('\0') {
        return None;
    }
    (clean.len() <= MAXIMUM_PATH_BYTES).then_some(clean)
}

/// What resolving a reference reads of the disk (C# `WorkspaceFiles.RealPath`, `Resolve` and `File.Exists`), so the rule is tested
/// without the platform: the host passes [`StdFiles`].
pub trait ResultFileLookup {
    /// `WorkspaceFiles.RealPath(root)`: the workspace root with every link resolved; none when it cannot be read.
    fn real_root(&self, root: &str) -> Option<String>;
    /// `ResultFiles.Resolve(path, root)`: the real path of an existing regular file `path` names inside `root`; none otherwise.
    fn resolve(&self, path: &str, root: &str) -> Option<String>;
    /// The separator the real paths use.
    fn separator(&self) -> char;
    /// Whether two paths that differ only in case are one file (Windows).
    fn ignores_case(&self) -> bool;
}

/// `ResultFiles.In(texts, root)`: the workspace files a set of final-result texts name, in order, without duplicates, each inside the
/// workspace. Markdown link destinations count as references; their label text does not.
pub fn files_in<S: AsRef<str>>(texts: &[S], root: Option<&str>, lookup: &dyn ResultFileLookup) -> Vec<ResultFile> {
    let Some(base) = root.and_then(|r| lookup.real_root(r)) else { return vec![] };
    let separator = lookup.separator();
    let prefix = if base.ends_with(separator) { base.clone() } else { format!("{base}{separator}") };
    let mut collector = Collector { lookup, base, prefix, seen: HashSet::new(), files: vec![], remaining: 1_000 };
    let mut remaining_bytes = MAXIMUM_RESULT_TEXT_BYTES as i64;
    for source in texts.iter().take(MAXIMUM_RESULT_TEXTS) {
        if remaining_bytes <= 0 || collector.full() {
            break;
        }
        let text = prefix_utf8(source.as_ref(), remaining_bytes as usize);
        remaining_bytes -= text.len() as i64;
        let mut at = 0;
        for link in MARKDOWN_LINK.captures_iter(&text) {
            if collector.full() {
                break;
            }
            let whole = link.get(0).expect("the match");
            if whole.start() > at {
                collector.plain(&text[at..whole.start()]);
            }
            if let Some(destination) = local_path(&link["dest"]) {
                collector.add(&destination, None);
            }
            at = whole.end();
        }
        if at < text.len() {
            collector.plain(&text[at..]);
        }
    }
    collector.files
}

struct Collector<'a> {
    lookup: &'a dyn ResultFileLookup,
    base: String,
    prefix: String,
    seen: HashSet<String>,
    files: Vec<ResultFile>,
    remaining: i32,
}

impl Collector<'_> {
    fn full(&self) -> bool {
        self.remaining <= 0 || self.files.len() >= MAXIMUM_RESULT_FILES
    }

    fn add(&mut self, path: &str, line: Option<i32>) {
        if self.full() {
            return;
        }
        self.remaining -= 1;
        let mut line = line;
        let mut resolved = self.lookup.resolve(path, &self.base);
        // Markdown destinations can carry the same :line convention as plain transcript paths. A real filename containing ':' wins.
        if resolved.is_none()
            && let Some(colon) = path.rfind(':')
        {
            let suffix = &path[colon + 1..];
            if (1..=6).contains(&suffix.len())
                && suffix.bytes().all(|b| b.is_ascii_digit())
                && let Ok(number) = suffix.parse::<i32>()
                && number > 0
            {
                resolved = self.lookup.resolve(&path[..colon], &self.base);
                line = Some(number);
            }
        }
        let Some(resolved) = resolved else { return };
        let key = if self.lookup.ignores_case() { resolved.to_lowercase() } else { resolved.clone() };
        if !self.seen.insert(key) {
            return;
        }
        let relative = resolved.get(self.prefix.len()..).unwrap_or("").replace('\\', "/");
        self.files.push(ResultFile { path: relative, line: line.filter(|l| *l > 0) });
    }

    fn plain(&mut self, text: &str) {
        for found in matches(text) {
            self.add(&found.path, found.line);
            if self.remaining == 0 || self.files.len() == MAXIMUM_RESULT_FILES {
                break;
            }
        }
    }
}

/// The disk as `std::fs` reads it: the real root canonicalized, a reference made absolute against it lexically, refused when it leaves
/// the root, then canonicalized again (every link resolved, the file must exist) and refused when its real path leaves the real root or is
/// no regular file. (C# walks the links one component at a time and refuses a link to another machine's share; canonicalizing resolves the
/// same links and reaches no share a local workspace does not already name.)
#[derive(Debug, Clone, Copy)]
pub struct StdFiles {
    /// Paths compare ignoring case (Windows' file systems).
    pub ignores_case: bool,
}

/// A canonical path without Windows' verbatim prefix for a drive path (`\\?\C:\…` → `C:\…`), as .NET writes it.
fn plain_path(path: PathBuf) -> String {
    let text = path.to_string_lossy().into_owned();
    match text.strip_prefix(r"\\?\") {
        Some(rest) if rest.as_bytes().get(1) == Some(&b':') => rest.to_string(),
        _ => text,
    }
}

/// `Path.GetFullPath`: `.` and `..` collapsed, lexically.
fn lexical(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    out
}

impl StdFiles {
    fn contains(&self, path: &str, root: &str) -> bool {
        let separator = std::path::MAIN_SEPARATOR;
        let trim = |p: &str| -> String {
            let t = p.trim_end_matches(['/', '\\']);
            if t.is_empty() { p.to_string() } else { t.to_string() }
        };
        let (mut path, mut root) = (trim(path), trim(root));
        if self.ignores_case {
            path = path.to_lowercase();
            root = root.to_lowercase();
        }
        path == root || path.starts_with(&format!("{root}{separator}"))
    }
}

impl ResultFileLookup for StdFiles {
    fn real_root(&self, root: &str) -> Option<String> {
        std::fs::canonicalize(root).ok().map(plain_path)
    }

    fn resolve(&self, path: &str, root: &str) -> Option<String> {
        if path.is_empty() || path.contains('\0') || path.len() > MAXIMUM_PATH_BYTES {
            return None;
        }
        let base = lexical(Path::new(root));
        let reference = Path::new(path);
        let candidate = lexical(&if reference.has_root() { reference.to_path_buf() } else { base.join(reference) });
        // Lexical escapes are refused before the target is touched.
        if !self.contains(&candidate.to_string_lossy(), &base.to_string_lossy()) {
            return None;
        }
        let real = plain_path(std::fs::canonicalize(&candidate).ok()?);
        let real_root = self.real_root(root)?;
        (self.contains(&real, &real_root) && Path::new(&real).is_file()).then_some(real)
    }

    fn separator(&self) -> char {
        std::path::MAIN_SEPARATOR
    }

    fn ignores_case(&self) -> bool {
        self.ignores_case
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn look_arounds_hold() {
        let found = matches("see src/a.ts:12, x/y.json and https://h.io/a/b.md; also a/b.c.d and ab/c.verylongextension");
        let paths: Vec<_> = found.iter().map(|m| (m.path.as_str(), m.line)).collect();
        assert_eq!(paths, vec![("src/a.ts", Some(12)), ("x/y.json", None), ("a/b.c.d", None)]);
        assert!(matches("foo/src/a.ts").iter().any(|m| m.path == "foo/src/a.ts"));
        assert!(matches("~src/a.ts").is_empty());
    }

    #[test]
    fn local_paths() {
        assert_eq!(local_path("src/a.ts").as_deref(), Some("src/a.ts"));
        assert_eq!(local_path("https://x/y"), None);
        assert_eq!(local_path("file:///C:/a%20b.txt").as_deref(), Some("C:/a b.txt"));
        assert_eq!(local_path("file://host/a"), None);
    }
}
