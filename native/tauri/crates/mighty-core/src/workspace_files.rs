//! The workspace path rules every reader of the user's files shares (C# `WorkspaceFiles.RealPath`, `FollowsLinkTarget`, `UncShare`,
//! `Contains`, `Resolve`; WorkspaceFiles.cs:149-236): `..` collapsed on paper, then every link on the way replaced by its target one
//! component at a time, through `std::fs` and `std::path` so each OS reads its own path rules as .NET does. A link is followed only to a
//! drive or to the share its path starts on, decided before anything reads the target, so looking at a workspace never makes the app
//! reach another machine (and hand it the user's credentials).

use std::io;
use std::path::{Component, Path, PathBuf};

use crate::normalize;

/// `WorkspaceFiles.MaximumPathBytes`.
pub const MAXIMUM_PATH_BYTES: usize = 4_096;
const MAXIMUM_LINK_HOPS: usize = 40;

/// Whether paths compare ignoring case (C# `PathComparison`: OrdinalIgnoreCase on Windows).
fn ignores_case() -> bool {
    std::path::MAIN_SEPARATOR == '\\'
}

fn same(a: &str, b: &str) -> bool {
    if ignores_case() { normalize::ordinal_ignore_case(a, b).is_eq() } else { a == b }
}

/// `Path.GetFullPath`: absolute, `.` and `..` collapsed (missing parts kept). Nothing on disk is read.
pub fn full_path(path: &Path) -> Option<PathBuf> {
    let absolute = std::path::absolute(path).ok()?;
    let mut out = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::Prefix(p) => out.push(p.as_os_str()),
            Component::RootDir => out.push(Component::RootDir.as_os_str()),
            Component::CurDir => {}
            Component::ParentDir => {
                if out.parent().is_some() {
                    out.pop();
                }
            }
            Component::Normal(part) => out.push(part),
        }
    }
    Some(out)
}

/// `"server\share"` of a UNC path (`\\server\share`, `//server/share`, `\\?\UNC\…`, `\\.\UNC\…`), or None for any other path (C#
/// `WorkspaceFiles.UncShare`).
pub fn unc_share(path: &str) -> Option<String> {
    let p = path.replace('/', "\\");
    let rest = if p.get(..8).is_some_and(|head| head.eq_ignore_ascii_case(r"\\?\UNC\") || head.eq_ignore_ascii_case(r"\\.\UNC\")) {
        &p[8..]
    } else if p.starts_with(r"\\?\") || p.starts_with(r"\\.\") {
        return None;
    } else {
        p.strip_prefix(r"\\")?
    };
    let parts: Vec<&str> = rest.split('\\').filter(|s| !s.is_empty()).collect();
    Some(match parts.len() {
        0 => String::new(),
        1 => parts[0].to_string(),
        _ => format!("{}\\{}", parts[0], parts[1]),
    })
}

/// A device path (`\\?\…`, `\\.\…`) to a drive's folders: `\\?\C:\…`. `\\?\GLOBALROOT\Device\Mup\…` (the redirector, that is another
/// machine), `\\?\Volume{…}`, pipes and other devices are not.
fn drive_device(path: &str) -> bool {
    let rest = path.as_bytes().get(4..).unwrap_or_default();
    rest.len() >= 2 && rest[0].is_ascii_alphabetic() && rest[1] == b':' && rest.get(2).is_none_or(|b| *b == b'\\')
}

/// C# `WorkspaceFiles.FollowsLinkTarget`, without its gap: whether [`real_path`] may go on to a link's target (already made absolute)
/// reached from `start`. A UNC target is followed only when the start is on that same share; a device path only when it names a drive
/// (C# lets `\\?\GLOBALROOT\Device\Mup\server\share` through, which is a share by another name); a drive or a rooted path always.
pub fn follows_link_target(target: &str, start: &str) -> bool {
    let p = target.replace('/', "\\");
    match unc_share(&p) {
        Some(share) => unc_share(start).is_some_and(|s| s.eq_ignore_ascii_case(&share)),
        None if p.starts_with(r"\\") => drive_device(&p),
        None => true,
    }
}

/// The link's target, None for a plain or missing item; an error when the link could not be read (the path is then refused rather than
/// taken for a plain folder; C# `TryLinkTarget`).
pub fn link_target(path: &Path) -> io::Result<Option<PathBuf>> {
    match std::fs::symlink_metadata(path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
        // A missing parent folder reads as not found on Windows and as NotADirectory elsewhere: both are "missing" to C#.
        Err(e) => {
            if path.parent().is_some_and(|p| !p.exists()) {
                Ok(None)
            } else {
                Err(e)
            }
        }
        Ok(meta) if meta.file_type().is_symlink() => std::fs::read_link(path).map(Some),
        Ok(_) => Ok(None),
    }
}

/// C# `WorkspaceFiles.RealPath` (WorkspaceFiles.cs:149): `..` collapsed first, then every link on the way (a symbolic link or a
/// junction) replaced by its target; missing parts stay as they are. None when links loop, one could not be read, or one leads off this
/// machine. A drive letter stays as written: `Z:\proj` on a mapped or `subst` drive is not turned into the share or folder behind it.
pub fn real_path(path: &Path) -> Option<PathBuf> {
    walk(path, &mut link_target)
}

/// The walk of [`real_path`], with the link reads passed in.
fn walk(path: &Path, read: &mut dyn FnMut(&Path) -> io::Result<Option<PathBuf>>) -> Option<PathBuf> {
    let start = path.to_string_lossy().into_owned();
    let mut full = full_path(path)?;
    let mut hops = 0;
    loop {
        let components: Vec<Component> = full.components().collect();
        let mut current = PathBuf::new();
        let mut restart: Option<PathBuf> = None;
        for (index, component) in components.iter().enumerate() {
            if matches!(component, Component::Prefix(_) | Component::RootDir) {
                current.push(component.as_os_str());
                continue;
            }
            let next = current.join(component.as_os_str());
            match read(&next) {
                Err(_) => return None,
                Ok(Some(target)) => {
                    hops += 1;
                    if hops > MAXIMUM_LINK_HOPS {
                        return None;
                    }
                    let resolved = full_path(&current.join(&target))?;
                    // Decided on paper, before anything reads the target.
                    if !follows_link_target(&resolved.to_string_lossy(), &start) {
                        return None;
                    }
                    let mut again = resolved;
                    for rest in &components[index + 1..] {
                        again.push(rest.as_os_str());
                    }
                    restart = Some(again);
                    break;
                }
                Ok(None) => current = next,
            }
        }
        match restart {
            None => return Some(current),
            Some(again) => full = full_path(&again)?,
        }
    }
}

fn trim_separator(value: &str) -> &str {
    // `Path.TrimEndingDirectorySeparator` leaves a root ("C:\", "/") alone.
    let trimmed = value.trim_end_matches(['/', std::path::MAIN_SEPARATOR]);
    if trimmed.is_empty() || trimmed.ends_with(':') { value } else { trimmed }
}

/// C# `WorkspaceFiles.Contains` (WorkspaceFiles.cs:213): whether a resolved path is the root or inside it.
pub fn contains(real_path: &Path, real_root: &Path) -> bool {
    relative(real_path, real_root).is_some()
}

/// The part of `path` under `root` (both resolved), its separators as they are: "" for the root itself, None outside it.
pub fn relative(path: &Path, root: &Path) -> Option<String> {
    let root_text = root.to_string_lossy();
    let path_text = path.to_string_lossy();
    let root = trim_separator(&root_text);
    let path = trim_separator(&path_text);
    if same(path, root) {
        return Some(String::new());
    }
    let prefix = if root.ends_with(std::path::MAIN_SEPARATOR) { root.to_string() } else { format!("{root}{}", std::path::MAIN_SEPARATOR) };
    (path.len() >= prefix.len() && path.is_char_boundary(prefix.len()) && same(&path[..prefix.len()], &prefix))
        .then(|| path[prefix.len()..].to_string())
}

/// C# `WorkspaceFiles.Resolve` (WorkspaceFiles.cs:229): the real path of an existing item at `relative` under the root ("" is the root
/// itself), or None when it is missing or its real path leaves the root. Absolute paths, drive letters, NUL and Windows stream names
/// (`a.txt:hidden`) are refused.
pub fn resolve(relative: &str, root: &Path) -> Option<PathBuf> {
    if relative.contains('\0') || relative.contains(':') || relative.len() > MAXIMUM_PATH_BYTES {
        return None;
    }
    if relative.starts_with('/') || relative.starts_with('\\') || Path::new(relative).has_root() {
        return None;
    }
    if !normalize::fully_qualified(&root.to_string_lossy()) {
        return None;
    }
    let real_root = real_path(root)?;
    let candidate = if relative.is_empty() {
        real_root.clone()
    } else {
        real_path(&real_root.join(relative.replace('/', std::path::MAIN_SEPARATOR_STR)))?
    };
    (contains(&candidate, &real_root) && candidate.exists()).then_some(candidate)
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use super::*;

    /// The walk over a made-up disk: `links` by path; every path the walk asked about is kept, so a test sees what was read.
    fn walked(path: &str, links: &[(&str, &str)]) -> (Option<PathBuf>, Vec<String>) {
        let links: HashMap<String, PathBuf> = links.iter().map(|(at, to)| (at.to_string(), PathBuf::from(to))).collect();
        let mut asked = vec![];
        let found = walk(Path::new(path), &mut |at: &Path| {
            let key = at.to_string_lossy().into_owned();
            asked.push(key.clone());
            Ok(links.get(&key).cloned())
        });
        (found, asked)
    }

    #[test]
    fn a_link_is_followed_only_to_a_drive_or_its_own_share() {
        for share in [r"\\server\share\x", "//server/share/x", r"\\?\UNC\server\share\x", r"\\.\unc\server\share", r"\\server"] {
            assert!(!follows_link_target(share, r"C:\work"), "{share} from a drive");
        }
        assert!(follows_link_target(r"\\Server\Share\other", r"\\server\share\work"), "the same share");
        assert!(!follows_link_target(r"\\server\other\x", r"\\server\share\work"), "another share of the same server");
        for device in [
            r"\\?\GLOBALROOT\Device\Mup\server\share",
            r"\\.\GLOBALROOT\Device\Mup\server\share",
            r"\\?\GLOBALROOT\Device\LanmanRedirector\;Z:0000\server\share",
            r"//?/GLOBALROOT/Device/Mup/server/share",
            r"\\?\Volume{8a3e4c1e-0000-0000-0000-100000000000}\x",
            r"\\.\pipe\x",
            r"\\?\",
        ] {
            assert!(!follows_link_target(device, r"\\?\GLOBALROOT\Device\Mup\server\share"), "{device}");
        }
        for local in [r"C:\elsewhere", r"\\?\C:\elsewhere", r"\\.\d:\x", r"\\?\Z:", "/usr/local", r"D:\"] {
            assert!(follows_link_target(local, r"C:\work"), "{local}");
        }
    }

    #[test]
    fn unc_shares_are_read_on_paper() {
        assert_eq!(unc_share(r"\\server\share\a\b").as_deref(), Some(r"server\share"));
        assert_eq!(unc_share(r"\\?\unc\server\share").as_deref(), Some(r"server\share"));
        assert_eq!(unc_share(r"\\server").as_deref(), Some("server"));
        assert_eq!(unc_share(r"\\?\C:\x"), None);
        assert_eq!(unc_share(r"C:\x"), None);
        assert_eq!(unc_share("한\\글"), None, "a name whose eighth byte is inside a character");
    }

    #[cfg(windows)]
    #[test]
    fn a_link_off_this_machine_is_refused_before_its_target_is_read() {
        for target in [r"\\evil.invalid\share", r"\\?\UNC\evil.invalid\share", r"\\?\GLOBALROOT\Device\Mup\evil.invalid\share"] {
            let (found, asked) = walked(r"C:\work\link\secret.txt", &[(r"C:\work\link", target)]);
            assert_eq!(found, None, "{target}");
            assert_eq!(asked, [r"C:\work", r"C:\work\link"], "nothing past the link was asked about: {target}");
        }
        let (found, _) = walked(r"\\server\share\work\link\a.txt", &[(r"\\server\share\work\link", r"\\server\share\real")]);
        assert_eq!(found.as_deref(), Some(Path::new(r"\\server\share\real\a.txt")), "a link on the share to the share");
        let (found, _) = walked(r"C:\work\link\a.txt", &[(r"C:\work\link", r"..\real")]);
        assert_eq!(found.as_deref(), Some(Path::new(r"C:\real\a.txt")), "a relative link, from the link's folder");
        let (found, _) = walked(r"C:\work\a", &[(r"C:\work\a", r"C:\work\b"), (r"C:\work\b", r"C:\work\a")]);
        assert_eq!(found, None, "links that loop");
        let (found, asked) = walked(r"Z:\proj\..\proj\a.txt", &[]);
        assert_eq!(found.as_deref(), Some(Path::new(r"Z:\proj\a.txt")), "a mapped drive keeps its letter");
        assert_eq!(asked, [r"Z:\proj", r"Z:\proj\a.txt"]);
    }

    #[cfg(unix)]
    #[test]
    fn links_are_walked_one_component_at_a_time() {
        let (found, asked) = walked("/work/link/a.txt", &[("/work/link", "../real")]);
        assert_eq!(found.as_deref(), Some(Path::new("/real/a.txt")), "a relative link, from the link's folder");
        assert_eq!(asked, ["/work", "/work/link", "/real", "/real/a.txt"]);
        let (found, _) = walked("/work/a", &[("/work/a", "/work/b"), ("/work/b", "/work/a")]);
        assert_eq!(found, None, "links that loop");
    }

    #[test]
    fn contains_reads_the_root_and_what_is_under_it() {
        let root = full_path(Path::new("ws")).unwrap();
        let inside = root.join("a").join("한글.txt");
        assert!(contains(&root, &root) && contains(&inside, &root));
        assert_eq!(relative(&inside, &root), Some(format!("a{}한글.txt", std::path::MAIN_SEPARATOR)));
        assert_eq!(relative(&root, &root).as_deref(), Some(""));
        assert!(!contains(&full_path(Path::new("ws-other")).unwrap(), &root), "a sibling sharing the name's start");
        assert!(!contains(&full_path(Path::new("ws/..")).unwrap(), &root));
    }
}
