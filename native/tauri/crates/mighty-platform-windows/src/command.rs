//! How a program named by its bare name (`claude`, `npm`, `git`, `winget`) starts on Windows (C# `WindowsCommand`, and
//! `ChildProcess.QuoteWindows`). CreateProcessW is handed the program's full path as its application name, and with one it neither
//! looks along the PATH nor runs a `.cmd` shim, as npm's `npm.cmd` and `claude.cmd` are: the name is found along the PATH with each
//! PATHEXT extension first, and a batch file is run by `cmd.exe /d /s /c` with every argument quoted.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use mighty_platform::{PlatformError, Result, SpawnRequest};

use crate::Variables;
use crate::win::{combine, ends_with_ignore_case, fold, system_directory};

/// Set in a launch's environment, it stops cmd.exe (and CreateProcess for a bare name) looking in the working folder first: an
/// npm shim such as `claude.cmd` runs the bare `node`, and a repository the command starts in must not be able to plant a
/// `node.exe` there (NeedCurrentDirectoryForExePathW). WindowsCommand.cs:16.
pub const NO_CURRENT_DIRECTORY_SEARCH: &str = "NoDefaultCurrentDirectoryInExePath";

/// What PATHEXT says when it says nothing (Windows' own default, the script hosts left out). WindowsCommand.cs:19.
pub const DEFAULT_PATH_EXT: &str = ".COM;.EXE;.BAT;.CMD";

/// One argument as the Microsoft C runtime (and `CommandLineToArgvW`) splits it back (C# `ChildProcess.QuoteWindows`,
/// ChildProcess.cs:93-103): as it is when it is not empty and holds no white space or quote; else in quotes, a quote escaped,
/// and a backslash run doubled before a quote or the closing quote.
pub fn quote_windows(argument: &str) -> String {
    if !argument.is_empty() && !argument.chars().any(|c| c.is_whitespace() || c == '"') {
        return argument.to_string();
    }
    let mut result = String::from("\"");
    let mut slashes = 0usize;
    for c in argument.chars() {
        if c == '\\' {
            slashes += 1;
            continue;
        }
        let run = if c == '"' { slashes * 2 + 1 } else { slashes };
        result.extend(std::iter::repeat_n('\\', run));
        slashes = 0;
        result.push(c);
    }
    result.extend(std::iter::repeat_n('\\', slashes * 2));
    result.push('"');
    result
}

/// The command line CreateProcessW gets (ChildProcess.cs:150): the program and every argument quoted and joined by spaces; or,
/// with a shell command, `"<cmd.exe>" /d /s /c "<command>"` (the arguments are not used).
pub fn command_line(program: &str, arguments: &[String], shell_command: Option<&str>) -> String {
    match shell_command {
        Some(command) => format!("{} /d /s /c \"{command}\"", quote_windows(program)),
        None => std::iter::once(program).chain(arguments.iter().map(String::as_str)).map(quote_windows).collect::<Vec<_>>().join(" "),
    }
}

/// PATHEXT's extensions as `Find` and `Copies` read them (WindowsCommand.cs:30-31): the default when it is empty or white space;
/// split on `;`, entries trimmed, empty ones dropped; only those that start with `.` and have more after it.
fn extensions(path_ext: Option<&str>) -> Vec<String> {
    let text = match path_ext {
        Some(text) if !text.trim().is_empty() => text,
        _ => DEFAULT_PATH_EXT,
    };
    text.split(';')
        .map(str::trim)
        .filter(|entry| !entry.is_empty())
        .filter(|entry| entry.starts_with('.') && entry.len() > 1)
        .map(String::from)
        .collect()
}

/// `Path.IsPathFullyQualified`: a drive and a root, or a UNC/device path.
fn fully_qualified(path: &str) -> bool {
    Path::new(path).is_absolute()
}

/// `File.Exists`: a file (not a folder) is there.
pub fn file_exists(path: &str) -> bool {
    Path::new(path).is_file()
}

/// The full path `name` runs as (WindowsCommand.cs:26-43): a rooted path as it is, when it is there; a bare name along `path`
/// (64 folders at most, rooted ones only) with each extension of `path_ext`, the name as written first when it already ends in
/// one of them. None when nothing is found. `exists` stands in for `File.Exists` (the checks use fixture folders).
pub fn find(name: &str, path: Option<&str>, path_ext: Option<&str>, exists: &dyn Fn(&str) -> bool) -> Option<String> {
    if name.is_empty() || name.contains('\0') {
        return None;
    }
    let extensions = extensions(path_ext);
    let named = extensions.iter().any(|extension| ends_with_ignore_case(name, extension));
    let candidates = |stem: &str| -> Vec<String> {
        let suffixed = extensions.iter().map(|extension| format!("{stem}{extension}"));
        if named { std::iter::once(stem.to_string()).chain(suffixed).collect() } else { suffixed.collect() }
    };
    if fully_qualified(name) {
        if exists(name) {
            return Some(name.to_string());
        }
        return candidates(name).into_iter().find(|candidate| exists(candidate));
    }
    if name.contains(['/', '\\', ':']) {
        return None;
    }
    for folder in path.unwrap_or("").split(';').take(64) {
        let directory = folder.trim().trim_matches('"');
        if directory.is_empty() || directory.contains('\0') || !fully_qualified(directory) {
            continue;
        }
        if let Some(found) = candidates(&combine(directory, name)).into_iter().find(|candidate| exists(candidate)) {
            return Some(found);
        }
    }
    None
}

/// Every copy of `name` in `folders`, in order and once each (the first PATHEXT extension found in a folder): the setup screen
/// asks each in turn for its version (WindowsCommand.cs:49-62). 80 folders at most.
pub fn copies<'a>(
    name: &str,
    folders: impl IntoIterator<Item = &'a str>,
    path_ext: Option<&str>,
    exists: &dyn Fn(&str) -> bool,
) -> Vec<String> {
    let extensions = extensions(path_ext);
    let mut seen = std::collections::BTreeSet::new();
    let mut copies = Vec::new();
    for folder in folders.into_iter().take(80) {
        let directory = folder.trim().trim_matches('"');
        if directory.is_empty() || directory.contains('\0') || !fully_qualified(directory) {
            continue;
        }
        let found =
            extensions.iter().map(|extension| combine(directory, &format!("{name}{extension}"))).find(|candidate| exists(candidate));
        if let Some(found) = found
            && seen.insert(fold(&found))
        {
            copies.push(found);
        }
    }
    copies
}

/// An environment read as Windows reads it, names in any case (`Path` is `PATH`); later entries win (WindowsCommand.cs:65-70).
pub fn variables<K: Into<String>, V: Into<String>>(environment: impl IntoIterator<Item = (K, V)>) -> Variables {
    environment.into_iter().collect()
}

/// A batch file, which only cmd.exe can run (WindowsCommand.cs:85).
pub fn is_batch(path: &str) -> bool {
    ends_with_ignore_case(path, ".cmd") || ends_with_ignore_case(path, ".bat")
}

/// cmd.exe by the system's own path, never one found along the PATH (WindowsCommand.cs:91-96): the system folder Windows reports,
/// which no environment variable of the user's can move; `SystemRoot` (else `C:\Windows`) only when Windows gives no folder.
pub fn command_interpreter(environment: Option<&Variables>) -> PathBuf {
    if let Some(system) = system_directory().filter(|system| !system.as_os_str().is_empty()) {
        return system.join("cmd.exe");
    }
    let root = environment.and_then(|variables| variables.get("SystemRoot")).filter(|root| !root.is_empty()).unwrap_or(r"C:\Windows");
    PathBuf::from(combine(&combine(root, "System32"), "cmd.exe"))
}

/// The text `cmd.exe /d /s /c "…"` runs for `batch` with `arguments` (WindowsCommand.cs:98-114). /s takes the outer quotes away and
/// runs the rest as written, so the batch file and every argument stand in double quotes of their own, where cmd reads
/// `& | < > ^ ( )` as plain characters; a backslash run before a closing quote is doubled, as the program the batch hands its
/// arguments to splits them. What cmd would still expand or end a quoted argument with (`"`, `%`, `!`, a line break, NUL) is
/// refused: such an argument cannot be passed through a batch file safely.
pub fn batch_command_line(batch: &str, arguments: &[String]) -> Result<String> {
    fn quote(value: &str) -> Result<String> {
        if value.contains(['"', '%', '!', '\r', '\n', '\0']) {
            return Err(PlatformError::Invalid(
                "an argument holding \", %, ! or a line break cannot be passed through a batch file safely".into(),
            ));
        }
        let trailing = value.len() - value.trim_end_matches('\\').len();
        Ok(format!("\"{value}{}\"", "\\".repeat(trailing)))
    }
    let quoted = std::iter::once(batch).chain(arguments.iter().map(String::as_str)).map(quote).collect::<Result<Vec<_>>>()?;
    Ok(quoted.join(" "))
}

/// How a resolved program is started (C# `WindowsCommand.Launch`, WindowsCommand.cs:76-82, which `CliRunner` uses): a batch file
/// through cmd.exe with its own command line ([`batch_command_line`]), anything else directly; either way with
/// [`NO_CURRENT_DIRECTORY_SEARCH`] set. `environment` is laid over the app's (`SpawnRequest::env`), then `NO_COLOR=1`
/// (`ChildProcess.StartInfo`). `variables` names `SystemRoot` for the rare system without a system folder.
pub fn launch(
    resolved: &str,
    arguments: &[String],
    working_directory: &Path,
    environment: Option<&BTreeMap<String, String>>,
    variables: Option<&Variables>,
) -> Result<SpawnRequest> {
    let batch = is_batch(resolved);
    let program = if batch { command_interpreter(variables) } else { PathBuf::from(resolved) };
    let mut env = environment.cloned().unwrap_or_default();
    // ChildProcess.StartInfo sets NO_COLOR after the caller's environment, so it always wins.
    set_ignoring_case(&mut env, "NO_COLOR", "1");
    set_ignoring_case(&mut env, NO_CURRENT_DIRECTORY_SEARCH, "1");
    Ok(SpawnRequest {
        program,
        args: if batch { vec![] } else { arguments.to_vec() },
        cwd: working_directory.to_path_buf(),
        env,
        shell_command: if batch { Some(batch_command_line(resolved, arguments)?) } else { None },
    })
}

/// `map[name] = value` on a dictionary that ignores case: an entry already there in another spelling takes the value.
fn set_ignoring_case(map: &mut BTreeMap<String, String>, name: &str, value: &str) {
    let key = map.keys().find(|key| crate::win::equal_ignore_case(key, name)).cloned().unwrap_or_else(|| name.to_string());
    map.insert(key, value.to_string());
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quoting_follows_the_c_runtime_rules() {
        assert_eq!(quote_windows("plain"), "plain");
        assert_eq!(quote_windows(""), "\"\"");
        assert_eq!(quote_windows("a b"), "\"a b\"");
        assert_eq!(quote_windows("a\"b"), "\"a\\\"b\"");
        assert_eq!(quote_windows(r"C:\dir\"), r"C:\dir\");
        assert_eq!(quote_windows(r"C:\my dir\"), "\"C:\\my dir\\\\\"");
        assert_eq!(quote_windows("a\\\"b c"), "\"a\\\\\\\"b c\"");
        assert_eq!(quote_windows("tab\there"), "\"tab\there\"");
    }

    #[test]
    fn a_shell_command_line_quotes_only_the_interpreter() {
        let line = command_line(r"C:\Windows\system32\cmd.exe", &["ignored".into()], Some("\"C:\\npm\\claude.cmd\" \"plugin\""));
        assert_eq!(line, "C:\\Windows\\system32\\cmd.exe /d /s /c \"\"C:\\npm\\claude.cmd\" \"plugin\"\"");
        assert_eq!(command_line(r"C:\Program Files\x.exe", &["a b".into(), "c".into()], None), "\"C:\\Program Files\\x.exe\" \"a b\" c");
    }
}
