//! The one PowerShell script the setup terminal runs (C# `SetupScript`, `SetupScriptLabels`, SetupPlan.cs:49-208), built from
//! catalog items only: every value is single-quoted, and nothing the user typed or a file outside the bundled catalog said reaches
//! it. The app never elevates itself and never sees a password; a machine-scope winget package may show Windows' own administrator
//! prompt, which the user answers. Every step runs in a try/catch of its own, so a failed one is said and the next one runs.

use std::collections::BTreeMap;

use mighty_run::strings;

use crate::setup::catalog::{SetupCatalog, SetupItem, SetupPlatform, SetupPlatformEntry, SetupRecipe};
use crate::setup::windows::{combine_all, variable, well_known_folders};

/// The most an official script may be: a larger answer is no installer, and is not read on.
pub const MAX_SCRIPT_BYTES: usize = 16 * 1024 * 1024;

/// npm's public registry: a project's .npmrc cannot point an install elsewhere.
pub const NPM_REGISTRY: &str = "https://registry.npmjs.org/";

/// One item's part of the generated install script; the confirmation shows `body` exactly as the script has it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupScriptStep {
    pub item_id: String,
    pub title: String,
    pub body: String,
}

/// A step's heading: `(index from 1, count, title)`.
pub type StepLabel = Box<dyn Fn(usize, usize, &str) -> String + Send + Sync>;

/// The words the script prints, in the app's language.
pub struct SetupScriptLabels {
    pub step: StepLabel,
    pub failed: String,
    pub done: String,
    pub close: String,
}

impl SetupScriptLabels {
    /// The Mac's words for the steps, and the line that keeps the window open until Enter.
    pub fn localized() -> SetupScriptLabels {
        SetupScriptLabels {
            step: Box::new(|index, count, title| format!("[{index}/{count}] {title}")),
            failed: strings::get("setup.script.failed"),
            done: strings::get("setup.script.done"),
            close: strings::get("setup.windows.script.close"),
        }
    }
}

/// PowerShell's single quotes: the text is taken literally and a quote inside is doubled. PowerShell also reads the typographic
/// quotes (U+2018 to U+201B) as single quotes, so they are doubled too.
pub fn quote(text: &str) -> String {
    let mut quoted = String::with_capacity(text.len() + 2);
    quoted.push('\'');
    for c in text.chars() {
        if matches!(c, '\'' | '\u{2018}' | '\u{2019}' | '\u{201A}' | '\u{201B}') {
            quoted.push(c);
        }
        quoted.push(c);
    }
    quoted.push('\'');
    quoted
}

/// Windows PowerShell by the system's own path, never one found along the PATH (C# `SetupScript.PowerShell` over
/// `WindowsCommand.CommandInterpreter`): `<system folder>\WindowsPowerShell\v1.0\powershell.exe` with the folder Windows reports
/// (`SetupFiles::system_folder`), else `SystemRoot\System32` (else `C:\Windows\System32`).
pub fn power_shell(system_folder: Option<&str>, environment: &BTreeMap<String, String>) -> String {
    let system = match system_folder.filter(|folder| !folder.is_empty()) {
        Some(folder) => folder.to_string(),
        None => combine_all(variable(environment, "SystemRoot").filter(|root| !root.is_empty()).unwrap_or(r"C:\Windows"), &["System32"]),
    };
    combine_all(&system, &["WindowsPowerShell", "v1.0", "powershell.exe"])
}

/// The lines that run before any step, shown in the confirmation too: the home folder as the working folder, the installers'
/// folders after the PATH the window already has, npm's public registry, TLS 1.2, the system's PowerShell, and the two functions
/// every official script goes through (an https-only fetch that follows each redirect itself, and a run of the script as a new file
/// in a PowerShell of its own, the app's options following its path).
pub fn preamble(environment: &BTreeMap<String, String>) -> Vec<String> {
    let folders: Vec<String> = well_known_folders(environment).into_iter().filter(|folder| !folder.contains(';')).collect();
    vec![
        "Set-Location -LiteralPath $HOME".to_string(),
        format!("$env:Path += {}", quote(&format!(";{}", folders.join(";")))),
        format!("$env:npm_config_registry = {}", quote(NPM_REGISTRY)),
        "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12".to_string(),
        "$mightyPowerShell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\\v1.0\\powershell.exe'".to_string(),
        format!(
            "{}{}{}{}{}{}{}",
            "function Get-MightyHttps([string]$Url) { for ($hop = 0; $hop -lt 6; $hop++) { $uri = [Uri]$Url; if ($uri.Scheme -ne 'https') { throw ('not https: ' + $Url) }; ",
            "$request = [Net.HttpWebRequest]::Create($uri); $request.AllowAutoRedirect = $false; $request.UserAgent = 'MightyCode-Setup'; ",
            "try { $response = $request.GetResponse() } catch { $failure = $_.Exception; while ($null -ne $failure -and $failure -isnot [Net.WebException]) { $failure = $failure.InnerException }; if ($null -eq $failure -or $null -eq $failure.Response) { throw }; $response = $failure.Response }; ",
            "try { $code = [int]$response.StatusCode; if ($code -ge 300 -and $code -lt 400) { $Url = ([Uri]::new($uri, [string]$response.Headers['Location'])).AbsoluteUri; continue }; ",
            "if ($code -ne 200) { throw ('HTTP ' + $code + ': ' + $Url) }; $body = $response.GetResponseStream(); $memory = [IO.MemoryStream]::new(); $buffer = New-Object byte[] 65536; ",
            format_args!(
                "try {{ while (($read = $body.Read($buffer, 0, $buffer.Length)) -gt 0) {{ $memory.Write($buffer, 0, $read); if ($memory.Length -gt {MAX_SCRIPT_BYTES}) {{ throw ('larger than 16 MB: ' + $Url) }} }}; "
            ),
            "return [Text.Encoding]::UTF8.GetString($memory.ToArray()).TrimStart([char]0xFEFF) } finally { $memory.Dispose(); $body.Dispose() } } finally { $response.Dispose() } }; throw ('too many redirects: ' + $Url) }",
        ),
        format!(
            "{}{}{}{}",
            "function Invoke-MightyScript([string]$Url, [string[]]$ScriptArguments = @()) { $text = Get-MightyHttps $Url; $file = Join-Path $PSScriptRoot ('step-' + [Guid]::NewGuid().ToString('N') + '.ps1'); ",
            "$stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None); ",
            "try { $bytes = [byte[]]([Text.UTF8Encoding]::new($true).GetPreamble() + [Text.Encoding]::UTF8.GetBytes($text)); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }; ",
            "try { & $mightyPowerShell -NoProfile -ExecutionPolicy Bypass -File $file @ScriptArguments } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue } }",
        ),
    ]
}

/// The steps for `ids` in that order; items that only open a page, or have no Windows entry, are left out.
pub fn steps(ids: &[String], catalog: &SetupCatalog, title: &dyn Fn(&SetupItem) -> String) -> Vec<SetupScriptStep> {
    ids.iter()
        .filter_map(|id| {
            let item = catalog.item(id)?;
            let body = body(item.entry(SetupPlatform::Windows)?)?;
            Some(SetupScriptStep { item_id: id.clone(), title: title(item), body })
        })
        .collect()
}

/// What one Windows entry runs, or None when nothing runs in the terminal.
pub fn body(entry: &SetupPlatformEntry) -> Option<String> {
    match &entry.install {
        // The program is one of the catalog's few (npx here); xcode-select is the Mac's.
        SetupRecipe::Argv { arguments, .. } if !arguments.is_empty() && arguments[0] != "xcode-select" => {
            Some(std::iter::once(arguments[0].clone()).chain(arguments[1..].iter().map(|a| quote(a))).collect::<Vec<_>>().join(" "))
        }
        // Fetched first and run as a file of its own, so its own questions still read the window. The options the app gives it
        // (Hermes's -NonInteractive) are handed to that file, where they bind as the script's parameters.
        SetupRecipe::OfficialScript { url, shell } if shell == "powershell" => {
            let options = SetupCatalog::script_arguments(url.as_str())
                .map(|options| format!(" -ScriptArguments @({})", options.iter().map(|o| quote(o)).collect::<Vec<_>>().join(", ")))
                .unwrap_or_default();
            Some(format!("Invoke-MightyScript {}{options}", quote(url.as_str())))
        }
        // A package that takes over another's command removes that one first; one that is not there is no error, and the
        // install's own exit code is the step's.
        SetupRecipe::NpmGlobal { package } => {
            let replaced =
                SetupCatalog::npm_replaces(package).map(|old| format!("npm uninstall -g {} *> $null; ", quote(old))).unwrap_or_default();
            Some(format!("{replaced}npm install -g {}", quote(package)))
        }
        // An already added marketplace is fine; the install decides. The marketplace, not the app, decides the plugin's version.
        SetupRecipe::ClaudePlugin { marketplace, plugin } => {
            Some(format!("claude plugin marketplace add {}; claude plugin install {} --scope user", quote(marketplace), quote(plugin)))
        }
        // The exact package from the winget source; a machine-scope one may show Windows' own administrator prompt.
        SetupRecipe::Winget { id, scope } => Some(format!(
            "winget install --exact --id {} --source winget --scope {scope} --accept-package-agreements --accept-source-agreements",
            quote(id)
        )),
        _ => None,
    }
}

/// The whole script (C# `SetupScript.Render`). `started_marker` is made first thing, with the PowerShell's process ID in it, and
/// `done_marker` when the steps end for any reason; both are folders made only where nothing is there yet, never through anything
/// already there. The window then waits for Enter, so what each step said can be read. CRLF line ends, one after the last line.
pub fn render(
    steps: &[SetupScriptStep],
    labels: &SetupScriptLabels,
    environment: &BTreeMap<String, String>,
    started_marker: &str,
    done_marker: &str,
) -> String {
    let mut lines = vec![
        "# Written by Mighty Code from its bundled tool catalog (setup-catalog.json).".to_string(),
        format!("$mightyStarted = {}", quote(started_marker)),
        format!("$mightyDone = {}", quote(done_marker)),
        "function New-MightyMarker([string]$Path) { if (Test-Path -LiteralPath $Path) { throw ('already there: ' + $Path) }; $null = [IO.Directory]::CreateDirectory($Path) }".to_string(),
        "try { New-MightyMarker $mightyStarted; [IO.File]::WriteAllText([IO.Path]::Combine($mightyStarted, 'pid'), [string]$PID) } catch { }".to_string(),
        "$mightyFailed = 0".to_string(),
        "try {".to_string(),
    ];
    lines.extend(preamble(environment));
    for (index, step) in steps.iter().enumerate() {
        lines.push(format!(
            "Write-Host ''; Write-Host {} -ForegroundColor Cyan",
            quote(&(labels.step)(index + 1, steps.len(), &step.title))
        ));
        lines.push(format!(
            "try {{ $global:LASTEXITCODE = 0; {}; if ($LASTEXITCODE) {{ throw ('exit code ' + $LASTEXITCODE) }} }} catch {{ $mightyFailed = 1; Write-Host $_.Exception.Message; Write-Host {} -ForegroundColor Yellow }}",
            step.body,
            quote(&labels.failed)
        ));
    }
    lines.push(format!("Write-Host ''; Write-Host {}", quote(&labels.done)));
    // Control-C ends the steps but not the window: the end marker is made and the window still waits for Enter.
    lines.push(format!("}} finally {{ try {{ New-MightyMarker $mightyDone }} catch {{ }}; $null = Read-Host {} }}", quote(&labels.close)));
    lines.push("exit $mightyFailed".to_string());
    lines.join("\r\n") + "\r\n"
}

/// The command the terminal window runs: the system's Windows PowerShell with `-NoProfile`, `-ExecutionPolicy Bypass` (for this
/// process only) and `-File` (the path is never read as code).
pub fn launch_arguments(script_path: &str, system_folder: Option<&str>, environment: &BTreeMap<String, String>) -> Vec<String> {
    vec![
        power_shell(system_folder, environment),
        "-NoProfile".into(),
        "-ExecutionPolicy".into(),
        "Bypass".into(),
        "-File".into(),
        script_path.into(),
    ]
}
