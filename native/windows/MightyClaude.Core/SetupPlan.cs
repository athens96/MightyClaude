using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace MightyClaude.Core;

/// <summary>
/// Which items an install runs and in what order (M/MightyCore/SetupPlan.swift): the picked ones plus every prerequisite that
/// is not there yet, prerequisites first, the catalog's order breaking ties.
/// </summary>
public static class SetupPlan
{
    public static IReadOnlyList<string> Order(IReadOnlySet<string> selected, SetupCatalog catalog, SetupPlatform platform, Func<string, bool> isSatisfied)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id)
        {
            if (catalog.Item(id)?.Entry(platform) is not { } entry || !included.Add(id)) return;
            foreach (var prerequisite in entry.Prerequisites) if (!isSatisfied(prerequisite)) Include(prerequisite);
        }
        foreach (var item in catalog.Items) if (selected.Contains(item.Id)) Include(item.Id);
        return Topological(included, catalog, platform) ?? catalog.Items.Select(item => item.Id).Where(included.Contains).ToList();
    }

    /// <summary>The prerequisites <see cref="Order"/> adds that were not picked themselves.</summary>
    public static IReadOnlySet<string> AddedPrerequisites(IReadOnlySet<string> selected, SetupCatalog catalog, SetupPlatform platform, Func<string, bool> isSatisfied) =>
        Order(selected, catalog, platform, isSatisfied).Where(id => !selected.Contains(id)).ToHashSet(StringComparer.Ordinal);

    public static bool HasNoCycle(SetupCatalog catalog, SetupPlatform platform) => Topological(catalog.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal), catalog, platform) is not null;

    /// <summary>Kahn's order over <paramref name="ids"/>, earliest catalog item first among the ready ones; null on a cycle.</summary>
    private static List<string>? Topological(IReadOnlySet<string> ids, SetupCatalog catalog, SetupPlatform platform)
    {
        var ordered = catalog.Items.Select(item => item.Id).Where(ids.Contains).ToList();
        var waiting = ordered.ToDictionary(id => id, id => (catalog.Item(id)?.Entry(platform)?.Prerequisites ?? []).Where(ids.Contains).ToHashSet(StringComparer.Ordinal));
        var result = new List<string>();
        while (waiting.Count > 0)
        {
            var next = ordered.FirstOrDefault(id => waiting.TryGetValue(id, out var left) && left.Count == 0);
            if (next is null) return null;
            result.Add(next); waiting.Remove(next);
            foreach (var left in waiting.Values) left.Remove(next);
        }
        return result;
    }
}

/// <summary>One item's part of the generated install script; the confirmation shows <see cref="Body"/> exactly as the script has it.</summary>
public sealed record SetupScriptStep(string ItemId, string Title, string Body);

/// <summary>The words the script prints, in the app's language.</summary>
public sealed record SetupScriptLabels(Func<int, int, string, string> Step, string Failed, string Done, string Close)
{
    /// <summary>The Mac's words for the steps, and the line that keeps the window open until Enter.</summary>
    public static SetupScriptLabels Localized() =>
        new((index, count, title) => $"[{index}/{count}] {title}", Locale.Get("setup.script.failed"), Locale.Get("setup.script.done"), Locale.Get("setup.windows.script.close"));
}

/// <summary>
/// Builds the one PowerShell script the setup terminal runs, from catalog items only (M/MightyCore/SetupPlan.swift SetupScript):
/// every value is single-quoted, and nothing the user typed or a file outside the bundled catalog said reaches it. The app never
/// elevates itself and never sees a password; a machine-scope winget package may show Windows' own administrator prompt, which the
/// user answers. Every step runs in a try/catch of its own, so a failed one is said and the next one runs.
/// </summary>
public static class SetupScript
{
    /// <summary>npm's public registry: a project's .npmrc cannot point an install elsewhere.</summary>
    public const string NpmRegistry = "https://registry.npmjs.org/";

    /// <summary>
    /// PowerShell's single quotes: the text is taken literally and a quote inside is doubled. PowerShell also reads the
    /// typographic quotes (U+2018 to U+201B) as single quotes, so they are doubled too.
    /// </summary>
    public static string Quote(string text)
    {
        var quoted = new StringBuilder("'");
        foreach (var c in text) { if (c is '\'' or '‘' or '’' or '‚' or '‛') quoted.Append(c); quoted.Append(c); }
        return quoted.Append('\'').ToString();
    }

    /// <summary>Windows PowerShell by the system's own path, never one found along the PATH.</summary>
    public static string PowerShell(IReadOnlyDictionary<string, string>? environment = null) =>
        Path.Combine(Path.GetDirectoryName(WindowsCommand.CommandInterpreter(environment))!, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Where the official installers put what they install, which a running process's PATH may not list yet: Claude Code's and uv's
    /// <c>%USERPROFILE%\.local\bin</c>, Codex's <c>%LOCALAPPDATA%\Programs\OpenAI\Codex\bin</c>, Antigravity's <c>%LOCALAPPDATA%\agy\bin</c>,
    /// npm's <c>%APPDATA%\npm</c>, winget's links, Git for Windows (user and machine), Node.js, GitHub CLI and Tailscale.
    /// </summary>
    public static IReadOnlyList<string> WellKnownFolders(IReadOnlyDictionary<string, string> environment)
    {
        var variables = WindowsCommand.Variables(environment);
        var folders = new List<string>();
        // A drive path (C:\…) counts as whole on any system, so the folders read the same where the checks run.
        void Add(string variable, params string[] parts) { if (variables.GetValueOrDefault(variable) is { Length: > 0 } root && (Path.IsPathFullyQualified(root) || root is [_, ':', '\\', ..])) folders.Add(Path.Combine([root, .. parts])); }
        Add("USERPROFILE", ".local", "bin");
        Add("LOCALAPPDATA", "Programs", "OpenAI", "Codex", "bin");
        Add("LOCALAPPDATA", "agy", "bin");
        Add("APPDATA", "npm");
        Add("LOCALAPPDATA", "Microsoft", "WinGet", "Links");
        Add("LOCALAPPDATA", "Programs", "Git", "cmd");
        Add("ProgramFiles", "Git", "cmd");
        Add("ProgramFiles", "nodejs");
        Add("ProgramFiles", "GitHub CLI");
        Add("ProgramFiles", "Tailscale");
        return folders;
    }

    /// <summary>
    /// The lines that run before any step, shown in the confirmation too: the home folder as the working folder (not a project
    /// whose settings could steer an install), the installers' folders after the PATH the window already has, npm's public
    /// registry, TLS 1.2, the system's PowerShell, and the two functions every official script goes through: one fetches over
    /// https only, following each redirect itself so none can lead to http, and one writes the script as a new file in this
    /// folder and runs it in a PowerShell of its own, so an <c>exit</c> in it ends that step and not the whole install.
    /// </summary>
    public static IReadOnlyList<string> Preamble(IReadOnlyDictionary<string, string> environment) =>
    [
        "Set-Location -LiteralPath $HOME",
        "$env:Path += " + Quote(";" + string.Join(";", WellKnownFolders(environment).Where(folder => !folder.Contains(';')))),
        "$env:npm_config_registry = " + Quote(NpmRegistry),
        "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12",
        "$mightyPowerShell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\\v1.0\\powershell.exe'",
        "function Get-MightyHttps([string]$Url) { for ($hop = 0; $hop -lt 6; $hop++) { $uri = [Uri]$Url; if ($uri.Scheme -ne 'https') { throw ('not https: ' + $Url) }; "
            + "$request = [Net.HttpWebRequest]::Create($uri); $request.AllowAutoRedirect = $false; $request.UserAgent = 'MightyClaude-Setup'; "
            + "try { $response = $request.GetResponse() } catch { $failure = $_.Exception; while ($null -ne $failure -and $failure -isnot [Net.WebException]) { $failure = $failure.InnerException }; if ($null -eq $failure -or $null -eq $failure.Response) { throw }; $response = $failure.Response }; "
            + "try { $code = [int]$response.StatusCode; if ($code -ge 300 -and $code -lt 400) { $Url = ([Uri]::new($uri, [string]$response.Headers['Location'])).AbsoluteUri; continue }; "
            + "if ($code -ne 200) { throw ('HTTP ' + $code + ': ' + $Url) }; $reader = [IO.StreamReader]::new($response.GetResponseStream(), [Text.Encoding]::UTF8); "
            + "try { return $reader.ReadToEnd() } finally { $reader.Dispose() } } finally { $response.Dispose() } }; throw ('too many redirects: ' + $Url) }",
        "function Invoke-MightyScript([string]$Url) { $text = Get-MightyHttps $Url; $file = Join-Path $PSScriptRoot ('step-' + [Guid]::NewGuid().ToString('N') + '.ps1'); "
            + "$stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None); "
            + "try { $bytes = [byte[]]([Text.UTF8Encoding]::new($true).GetPreamble() + [Text.Encoding]::UTF8.GetBytes($text)); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }; "
            + "try { & $mightyPowerShell -NoProfile -ExecutionPolicy Bypass -File $file } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue } }",
    ];

    /// <summary>The steps for <paramref name="ids"/> in that order; items that only open a page, or have no Windows entry, are left out.</summary>
    public static IReadOnlyList<SetupScriptStep> Steps(IReadOnlyList<string> ids, SetupCatalog catalog, Func<SetupItem, string> title) =>
        ids.Select(id => catalog.Item(id) is { } item && item.Entry(SetupPlatform.Windows) is { } entry && Body(entry) is { } body ? new SetupScriptStep(id, title(item), body) : null)
            .OfType<SetupScriptStep>().ToList();

    /// <summary>What one Windows entry runs, or null when nothing runs in the terminal.</summary>
    public static string? Body(SetupPlatformEntry entry) => entry.Install switch
    {
        // The program is one of the catalog's few (npx here); xcode-select is the Mac's.
        SetupRecipe.Argv argv when argv.Arguments.Count > 0 && argv.Arguments[0] != "xcode-select" => string.Join(" ", argv.Arguments.Take(1).Concat(argv.Arguments.Skip(1).Select(Quote))),
        // Fetched first and run as a file of its own, so its own questions still read the window.
        SetupRecipe.OfficialScript { Shell: "powershell" } script => "Invoke-MightyScript " + Quote(script.Url.AbsoluteUri),
        SetupRecipe.NpmGlobal npm => "npm install -g " + Quote(npm.Package),
        // An already added marketplace is fine; the install decides. The marketplace, not the app, decides the plugin's version.
        SetupRecipe.ClaudePlugin plugin => $"claude plugin marketplace add {Quote(plugin.Marketplace)}; claude plugin install {Quote(plugin.Plugin)} --scope user",
        // The exact package from the winget source; a machine-scope one may show Windows' own administrator prompt.
        SetupRecipe.Winget winget => $"winget install --exact --id {Quote(winget.Id)} --source winget --scope {winget.Scope} --accept-package-agreements --accept-source-agreements",
        _ => null,
    };

    /// <summary>
    /// The whole script. <paramref name="startedMarker"/> is made first thing, with the PowerShell's process ID in it, and
    /// <paramref name="doneMarker"/> when the steps end for any reason (finished, a failed step, Control-C); both are folders
    /// made with New-Item, which fails rather than follow anything already there. They tell the app the script started and
    /// ended. The window then waits for Enter, so what each step said can be read.
    /// </summary>
    public static string Render(IReadOnlyList<SetupScriptStep> steps, SetupScriptLabels labels, IReadOnlyDictionary<string, string> environment, string startedMarker, string doneMarker)
    {
        var lines = new List<string>
        {
            "# Written by Mighty Claude from its bundled tool catalog (setup-catalog.json).",
            "$mightyStarted = " + Quote(startedMarker),
            "$mightyDone = " + Quote(doneMarker),
            "try { $null = New-Item -ItemType Directory -Path $mightyStarted -ErrorAction Stop; [IO.File]::WriteAllText((Join-Path $mightyStarted 'pid'), [string]$PID) } catch { }",
            "$mightyFailed = 0",
            "try {",
        };
        lines.AddRange(Preamble(environment));
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            lines.Add("Write-Host ''; Write-Host " + Quote(labels.Step(index + 1, steps.Count, step.Title)) + " -ForegroundColor Cyan");
            lines.Add("try { $global:LASTEXITCODE = 0; " + step.Body + "; if ($LASTEXITCODE) { throw ('exit code ' + $LASTEXITCODE) } } catch { $mightyFailed = 1; Write-Host $_.Exception.Message; Write-Host " + Quote(labels.Failed) + " -ForegroundColor Yellow }");
        }
        lines.Add("Write-Host ''; Write-Host " + Quote(labels.Done));
        // Control-C ends the steps but not the window: the end marker is made and the window still waits for Enter.
        lines.Add("} finally { try { $null = New-Item -ItemType Directory -Path $mightyDone -ErrorAction Stop } catch { }; $null = Read-Host " + Quote(labels.Close) + " }");
        lines.Add("exit $mightyFailed");
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// The command the terminal window runs: the system's Windows PowerShell with <c>-NoProfile</c> (no profile script of the
    /// user's changes PATH, aliases or a proxy first), <c>-ExecutionPolicy Bypass</c> (for this one process only, so the app's own
    /// unsigned script runs where the default policy refuses .ps1 files; no policy is changed) and <c>-File</c> (the script is run
    /// from its file, and the path is never read as code).
    /// </summary>
    public static IReadOnlyList<string> LaunchArguments(string scriptPath, IReadOnlyDictionary<string, string>? environment = null) =>
        [PowerShell(environment), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath];
}

/// <summary>
/// The folder the install script and its markers live in (M/MightyCore/SetupPlan.swift SetupScriptFiles), held to what only this
/// user can touch: a real folder (no junction or link), owned by this user, with an access list of this user alone. The script is
/// created new under a random name, and a name that is already there is refused rather than followed.
/// </summary>
public static class SetupScriptFiles
{
    /// <summary><c>%LOCALAPPDATA%\MightyClaudeNative\setup</c>: machine-local, never roamed. A <c>--profile</c> run keeps it in its profile.</summary>
    public static string Folder(string? profileDirectory, string localAppData) =>
        profileDirectory is { Length: > 0 } profile ? Path.Combine(profile, "setup") : Path.Combine(localAppData, "MightyClaudeNative", "setup");

    /// <summary>Creates <paramref name="path"/> for this user alone when it is not there, then checks it.</summary>
    public static void PrepareDirectory(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
                var user = WindowsIdentity.GetCurrent().User!;
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                // Owned by the user even when the app was started as administrator, whose token would otherwise make Administrators the owner.
                security.SetOwner(user);
                new DirectoryInfo(path).Create(security);
            }
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new IOException("setup folder is not a folder: " + path);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("setup folder is not a plain folder: " + path);
        if (OperatingSystem.IsWindows())
        {
            if (info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != WindowsIdentity.GetCurrent().User) throw new IOException("setup folder belongs to another user: " + path);
        }
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("setup folder is open to other users: " + path);
    }

    /// <summary>Writes <paramref name="text"/> to a new file with a random name in <paramref name="folder"/>, as UTF-8 with its mark, which Windows PowerShell needs to read anything but ASCII.</summary>
    public static string WriteScript(string text, string folder)
    {
        var path = Path.Combine(folder, "install-" + Guid.NewGuid().ToString("N") + ".ps1");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        var encoding = new UTF8Encoding(true);
        stream.Write(encoding.GetPreamble());
        stream.Write(encoding.GetBytes(text));
        return path;
    }

    /// <summary>Fresh marker paths in <paramref name="folder"/>, which nothing has made yet.</summary>
    public static (string Started, string Done) Markers(string folder)
    {
        var id = Guid.NewGuid().ToString("N");
        return (Path.Combine(folder, "started-" + id), Path.Combine(folder, "done-" + id));
    }

    /// <summary>True only for a real folder at <paramref name="path"/>: a junction or link there does not count.</summary>
    public static bool MarkerExists(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>The script's PowerShell, as it wrote its process ID into the start marker; null until it has.</summary>
    public static int? RunnerProcessId(string startedMarker)
    {
        if (!MarkerExists(startedMarker)) return null;
        var file = new FileInfo(Path.Combine(startedMarker, "pid"));
        try { return file.Exists && file.LinkTarget is null && file.Length <= 32 && int.TryParse(File.ReadAllText(file.FullName).Trim(), out var id) && id > 0 ? id : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Whether the run is over: the script made its end marker, or it started and its PowerShell is gone without making it (the
    /// window was closed; a Windows Terminal tab leaves the app no process of its own to wait for).
    /// </summary>
    public static bool RunEnded(string startedMarker, string doneMarker, Func<int, bool>? isRunning = null) =>
        MarkerExists(doneMarker) || RunnerProcessId(startedMarker) is { } id && !(isRunning ?? IsPowerShellRunning)(id);

    private static bool IsPowerShellRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited && process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Removes what earlier runs left in <paramref name="folder"/>; a junction or link is removed itself, never followed.</summary>
    public static void RemoveLeftovers(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo directory && directory.LinkTarget is null && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) directory.Delete(true);
                // A junction or a directory link: Windows removes the link with RemoveDirectory, elsewhere it is unlinked.
                else if (entry is DirectoryInfo link) { if (OperatingSystem.IsWindows()) Directory.Delete(link.FullName, false); else File.Delete(link.FullName); }
                else entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
