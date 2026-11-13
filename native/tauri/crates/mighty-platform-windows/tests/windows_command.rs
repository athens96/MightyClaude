//! C# `WindowsCommandVerification` (MightyCode.Core.Tests/WindowsCommandVerification.cs): the toolkit's commands reach their
//! programs on Windows. The lookups run over fixture folders. Then the argv a real `.cmd` shim and a real program receive.
#![cfg(windows)]

mod common;

use std::collections::BTreeMap;
use std::time::Duration;

use common::*;
use mighty_platform::{PlatformError, ProcessHost};
use mighty_platform_windows::WindowsProcessHost;
use mighty_platform_windows::command::{
    NO_CURRENT_DIRECTORY_SEARCH, batch_command_line, command_interpreter, file_exists, find, is_batch, launch, variables,
};

/// A disk ignores case: the extension comes back as PATHEXT spells it (WindowsCommandVerification.cs:8-9).
fn same(found: Option<String>, expected: &str) -> bool {
    found.is_some_and(|found| found.eq_ignore_ascii_case(expected))
}

/// C# "bare names are found along the PATH with PATHEXT" (WindowsCommandVerification.cs:11-41).
#[tokio::test]
async fn bare_names_are_found_along_the_path_with_pathext() {
    let root = temp("find");
    let folder = |name: &str| {
        let path = root.join(name);
        std::fs::create_dir_all(&path).unwrap();
        path.to_string_lossy().into_owned()
    };
    let (first, npm, git) = (folder("first"), folder("npm"), folder("git"));
    for file in [format!(r"{npm}\npm.cmd"), format!(r"{npm}\npm"), format!(r"{git}\git.exe"), format!(r"{git}\git.cmd")] {
        std::fs::write(file, "").unwrap();
    }
    let path = ["relative", &first, &npm, &git].join(";");
    let exists = file_exists;
    // The extensionless shell script npm leaves beside npm.cmd is not something Windows can start.
    assert!(
        same(find("npm", Some(&path), Some(".COM;.EXE;.BAT;.CMD"), &exists), &format!(r"{npm}\npm.cmd")),
        "npm must be found as its .cmd shim"
    );
    // PATHEXT's order decides between two copies in one folder; an empty PATHEXT reads as Windows' default.
    assert!(same(find("git", Some(&path), Some(".CMD;.EXE"), &exists), &format!(r"{git}\git.cmd")), "PATHEXT's order must decide");
    assert!(same(find("git", Some(&path), None, &exists), &format!(r"{git}\git.exe")), "no PATHEXT must read as .COM;.EXE;.BAT;.CMD");
    assert!(
        same(find("git.exe", Some(&path), None, &exists), &format!(r"{git}\git.exe")),
        "a name that has its extension is found as written"
    );
    assert!(find("winget", Some(&path), None, &exists).is_none(), "a program on no folder is not found");
    // A rooted path stands as it is; a relative path or a folder on the PATH that is not rooted is never searched.
    assert_eq!(find(&format!(r"{git}\git.exe"), Some(""), None, &exists), Some(format!(r"{git}\git.exe")), "a full path must be kept");
    assert!(find(r"relative\npm", Some(&path), None, &exists).is_none(), "a relative path must not be resolved");
    assert!(find("npm", Some("relative;npm"), None, &exists).is_none(), "folders that are not rooted must be skipped");
    assert!(is_batch(r"C:\x\npm.CMD") && is_batch("a.bat") && !is_batch("git.exe"), "batch files are .cmd and .bat");
    let variables = variables([("Path", "p"), ("PathExt", ".EXE")]);
    assert!(variables.get("PATH") == Some("p") && variables.get("PATHEXT") == Some(".EXE"), "Windows names its variables in any case");
    remove_when_free(&root).await;
}

/// C# "batch files run through cmd with every argument quoted" (WindowsCommandVerification.cs:43-67).
#[test]
fn batch_files_run_through_cmd_with_every_argument_quoted() {
    let arguments: Vec<String> = ["install", "-g", "a&b|c<d>e^(f)", r"C:\dir\"].map(String::from).into();
    let line = batch_command_line(r"C:\Program Files\nodejs\npm.cmd", &arguments).unwrap();
    assert_eq!(
        line, "\"C:\\Program Files\\nodejs\\npm.cmd\" \"install\" \"-g\" \"a&b|c<d>e^(f)\" \"C:\\dir\\\\\"",
        "each argument must stand in its own quotes, metacharacters kept inside them and a closing backslash doubled"
    );
    for unsafe_argument in ["a\"b", "%PATH%", "x!y!", "a\nb", "a\rb"] {
        let refused = batch_command_line("npm.cmd", &[unsafe_argument.to_string()]);
        assert!(
            matches!(refused, Err(PlatformError::Invalid(_))),
            "an argument cmd would expand or end early must be refused: {unsafe_argument:?}"
        );
    }
    // A batch launch runs cmd.exe with the shim's own line, and neither it nor a direct launch looks in the working folder for a
    // bare name.
    let system_root = variables([("SystemRoot", r"D:\Win")]);
    let temp = std::env::temp_dir();
    let batch = launch(r"C:\npm\claude.cmd", &["plugin".into(), "list".into()], &temp, None, Some(&system_root)).unwrap();
    assert!(
        batch.program == command_interpreter(Some(&system_root))
            && batch.args.is_empty()
            && batch.shell_command.as_deref() == Some("\"C:\\npm\\claude.cmd\" \"plugin\" \"list\"")
            && batch.env.get(NO_CURRENT_DIRECTORY_SEARCH).map(String::as_str) == Some("1"),
        "a batch launch must go through cmd.exe with NoDefaultCurrentDirectoryInExePath set"
    );
    let environment = BTreeMap::from([("PATH".to_string(), "x".to_string())]);
    let direct = launch(r"C:\git\git.exe", &["--version".into()], &temp, Some(&environment), None).unwrap();
    assert!(
        direct.program.to_string_lossy() == r"C:\git\git.exe"
            && direct.shell_command.is_none()
            && direct.args == ["--version"]
            && direct.env.get(NO_CURRENT_DIRECTORY_SEARCH).map(String::as_str) == Some("1")
            && direct.env.get("PATH").map(String::as_str) == Some("x"),
        "a direct launch keeps its arguments and environment and sets NoDefaultCurrentDirectoryInExePath too"
    );
    // On Windows the system folder Windows reports wins over any SystemRoot the environment claims.
    let system = std::env::var("SystemRoot").unwrap();
    assert!(
        command_interpreter(Some(&system_root)).to_string_lossy().eq_ignore_ascii_case(&format!(r"{system}\system32\cmd.exe")),
        "cmd.exe comes from the system folder"
    );
}

/// The arguments that pass through a batch file and arrive as they were sent: spaces, every cmd metacharacter inside quotes, a
/// closing backslash, Korean, an empty one.
fn tricky_arguments() -> Vec<String> {
    [
        "plain",
        "with space",
        "amp&ersand",
        "caret^here",
        "pipe|lt<gt>",
        "paren(s)",
        r"trailing\",
        r"two\\",
        r"mid\dle",
        "한글 인자",
        "",
        "semi;colon,comma=equals",
        "tab\there",
    ]
    .map(String::from)
    .into()
}

async fn received(file: &std::path::Path) -> Vec<String> {
    until(|| file.exists(), 10_000, "the args file").await;
    let text = std::fs::read_to_string(file).unwrap();
    text.split('\0').map(String::from).collect()
}

/// A `.cmd` shim (as npm writes them: the program, then `%*`) in a folder whose name holds spaces, `&`, `^` and parentheses
/// receives exactly the arguments C# `BatchCommandLine` passes (tests/parity holds C#'s own lines for the same inputs); what C#
/// refuses (`"`, `%`, `!`, line breaks) is refused before anything starts.
#[tokio::test(flavor = "multi_thread")]
async fn a_cmd_shim_receives_exactly_the_arguments_csharp_passes() {
    let root = temp("cmd-argv");
    let folder = root.join("shim dir & ^carets (x)");
    std::fs::create_dir_all(&folder).unwrap();
    let out = folder.join("args.txt");
    let shim = folder.join("tool.cmd");
    std::fs::write(&shim, format!("@\"{}\" --print-args \"{}\" %*\r\n", fake_text(), out.to_string_lossy())).unwrap();
    let arguments = tricky_arguments();
    let request = launch(&shim.to_string_lossy(), &arguments, &root, None, None).unwrap();
    assert!(request.program.to_string_lossy().to_lowercase().ends_with(r"\cmd.exe"));
    let child = WindowsProcessHost::new().spawn(request).await.unwrap();
    assert_eq!(tokio::time::timeout(Duration::from_secs(15), child.wait()).await.unwrap().unwrap(), 0);
    child.close().await.unwrap();
    assert_eq!(received(&out).await, arguments);

    for refused in ["say \"hi\"", "%PATH%", "bang!", "two\nlines", "cr\rhere"] {
        let launched = launch(&shim.to_string_lossy(), &[refused.to_string()], &root, None, None);
        assert!(matches!(launched, Err(PlatformError::Invalid(_))), "{refused:?} must not reach cmd.exe");
    }
    remove_when_free(&root).await;
}

/// A program started directly gets each argument back as `quote_windows` (C# `ChildProcess.QuoteWindows`) wrote it, quotes and
/// backslash runs included.
#[tokio::test(flavor = "multi_thread")]
async fn a_program_receives_exactly_the_arguments_it_was_given() {
    let root = temp("direct-argv");
    let out = root.join("args.txt");
    let mut arguments = tricky_arguments();
    arguments.extend(["say \"hi\"", r#"back\"quote"#, r"\\server\share\", "%PATH%", "bang!", "two\nlines"].map(String::from));
    let mut all = vec!["--print-args".to_string(), out.to_string_lossy().into_owned()];
    all.extend(arguments.iter().cloned());
    let request = launch(&fake_text(), &all, &root, None, None).unwrap();
    assert!(request.shell_command.is_none());
    let child = WindowsProcessHost::new().spawn(request).await.unwrap();
    assert_eq!(child.wait().await.unwrap(), 0);
    child.close().await.unwrap();
    assert_eq!(received(&out).await, arguments);
    remove_when_free(&root).await;
}
