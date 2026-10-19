using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// 필요한 도구 on Windows: the shared catalog's closed decoder, the install order, the PowerShell script and its folder, the
/// probes and the first-run rule (M/MightyCoreTests/SetupCatalogTests.swift, SetupPlanTests.swift, SetupProbeTests.swift).
/// Nothing is installed and no script is run: the probes read fixture folders and an injected version reader.
internal static class SetupVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "setup-catalog.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    private static string FixtureText() => File.ReadAllText(Path.Combine(Root(), "native", "contracts", "fixtures", "setup-catalog.json"));

    /// <summary>The fixture with <paramref name="edit"/> applied to its first item (claude).</summary>
    private static SetupCatalog DecodeEditing(Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(FixtureText())!.AsObject();
        edit(root["items"]![0]!.AsObject());
        return SetupCatalogDecoder.Decode(root.ToJsonString());
    }

    private static void SetWindows(JsonObject item, string key, JsonNode value) => item["platforms"]!["windows"]![key] = value;

    private static void Refused(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    internal static Task TheCatalogTheAppCarriesIsTheContractWithAWindowsEntryPerTool()
    {
        var catalog = SetupCatalog.Bundled();
        Check(catalog.Items.Select(item => item.Id).SequenceEqual(SetupCatalogDecoder.Decode(FixtureText()).Items.Select(item => item.Id)), "the embedded catalog must be the contract file");
        var windows = catalog.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null).Select(item => item.Id).ToList();
        Check(windows.SequenceEqual(["claude", "codex", "agy", "git", "node", "uv", "ouroboros", "superpowers", "paperthin", "omc", "tailscale", "gh", "glab"]),
            "every tool Windows can install has a Windows entry; got " + string.Join(", ", windows));
        foreach (var id in new[] { "homebrew", "bun", "gstack" }) Check(catalog.Item(id)?.Entry(SetupPlatform.Windows) is null, id + " stays a Mac tool");
        Check(catalog.Items.Where(item => item.Required).Select(item => item.Id).SequenceEqual(["git"]), "Git is the one required tool");
        Check(catalog.Item("claude")!.Entry(SetupPlatform.Windows)!.Install is SetupRecipe.OfficialScript { Shell: "powershell" } claude && claude.Url.AbsoluteUri == "https://claude.ai/install.ps1", "Claude Code uses its native PowerShell installer");
        Check(catalog.Item("git")!.Entry(SetupPlatform.Windows)!.Install == new SetupRecipe.Winget("Git.Git", "user"), "Git installs with winget in user scope");
        Check(catalog.Item("node")!.Entry(SetupPlatform.Windows)!.Install == new SetupRecipe.Winget("OpenJS.NodeJS.LTS", "machine"), "Node.js has only a machine-scope installer");
        Check(catalog.Item("tailscale")!.Entry(SetupPlatform.Windows)!.Install is SetupRecipe.VendorPage { Url.AbsoluteUri: "https://tailscale.com/download/windows" }, "Tailscale opens its download page");
        Check(catalog.Item("agy")!.Provider == "gemini" && catalog.Item("codex")!.Provider == "codex" && catalog.Item("claude")!.Provider == "claude", "the agent CLIs name their providers");
        // Every key the Windows rows read exists in every language.
        foreach (var language in Locale.Languages)
        {
            using var stream = typeof(Locale).Assembly.GetManifestResourceStream("MightyClaude.Core.Locales." + language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            foreach (var item in catalog.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null))
                Check(strings.GetValueOrDefault(SetupCatalog.TitleKey(item)) is { Length: > 0 } && strings.GetValueOrDefault(SetupCatalog.DetailKey(item)) is { Length: > 0 }, language + " misses " + item.Id);
            foreach (var group in Enum.GetValues<SetupGroup>()) Check(strings.ContainsKey(SetupCatalog.GroupTitleKey(group)), language + " misses a group title");
        }
        return Task.CompletedTask;
    }

    internal static Task TheDecoderRefusesWhatTheMacsRefusesAndKeepsWingetToWindows()
    {
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "officialScript", ["url"] = "https://example.com/install.ps1", ["shell"] = "powershell" })), "a host outside the list");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "officialScript", ["url"] = "http://claude.ai/install.ps1", ["shell"] = "powershell" })), "plain http");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "officialScript", ["url"] = "https://claude.ai.example.com/install.ps1", ["shell"] = "powershell" })), "a look-alike host");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "officialScript", ["url"] = "https://claude.ai/install.ps1?x=$(id)", ["shell"] = "powershell" })), "a query string");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "officialScript", ["url"] = "https://claude.ai/install.ps1", ["shell"] = "bash" })), "Windows runs PowerShell");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "shell", ["command"] = "irm x | iex" })), "an unknown kind");
        Refused(() => DecodeEditing(item => SetWindows(item, "probe", new JsonObject { ["kind"] = "commandLineTools" })), "a Mac probe on Windows");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "brewFormula", ["name"] = "node" })), "Homebrew on Windows");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "argv", ["argv"] = new JsonArray("xcode-select", "--install") })), "xcode-select on Windows");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "argv", ["argv"] = new JsonArray("powershell", "-c", "id") })), "a program outside the list");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "argv", ["argv"] = new JsonArray("npx", "--yes", "skills@1.7.2") })), "an option npx may not take");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "argv", ["argv"] = new JsonArray("npx", "x;calc") })), "an argument with a separator");
        Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "skill", ["repository"] = "https://github.com/garrytan/gstack.git", ["folder"] = "gstack", ["commit"] = new string('a', 40) })), "a pinned skill's bash setup on Windows");
        foreach (var marketplace in new[] { "Q00/..", "./ouroboros", "someone/ouroboros" })
            Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "claudePlugin", ["marketplace"] = marketplace, ["plugin"] = "ouroboros@ouroboros" })), "marketplace " + marketplace);
        foreach (var url in new[] { "https://github.com/someone/gstack.git", "https://github.com/garrytan", "https://github.com/../garrytan/gstack" })
            Refused(() => DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "officialScript", ["url"] = url, ["shell"] = "powershell" })), "repository " + url);
        foreach (var winget in new[] { new JsonObject { ["kind"] = "winget", ["id"] = "Git.Git", ["scope"] = "all" }, new JsonObject { ["kind"] = "winget", ["id"] = "git; calc", ["scope"] = "user" },
                     new JsonObject { ["kind"] = "winget", ["id"] = "Git", ["scope"] = "user" }, new JsonObject { ["kind"] = "winget", ["id"] = "Git.Git", ["scope"] = "user", ["override"] = "/S" } })
            Refused(() => DecodeEditing(item => SetWindows(item, "install", winget)), "winget " + winget.ToJsonString());
        Refused(() => DecodeEditing(item => item["platforms"]!["macos"]!["install"] = new JsonObject { ["kind"] = "winget", ["id"] = "Git.Git", ["scope"] = "user" }), "winget on the Mac");
        Refused(() => DecodeEditing(item => item["label"] = "Install everything"), "a label the app does not know");
        Refused(() => DecodeEditing(item => item["extra"] = true), "an unknown key");
        Refused(() => DecodeEditing(item => item["afterInstall"] = new JsonObject { ["kind"] = "signIn", ["provider"] = "someone" }), "an unknown provider");
        Refused(() => DecodeEditing(item => SetWindows(item, "prerequisites", new JsonArray("claude"))), "an item needing itself");
        // node → paperthin, and paperthin → node, closes a loop on Windows.
        var root = JsonNode.Parse(FixtureText())!.AsObject();
        foreach (var item in root["items"]!.AsArray()) if (item!["id"]!.GetValue<string>() == "node") item["platforms"]!["windows"]!["prerequisites"] = new JsonArray("paperthin");
        Refused(() => SetupCatalogDecoder.Decode(root.ToJsonString()), "a Windows cycle");
        var ok = DecodeEditing(item => SetWindows(item, "install", new JsonObject { ["kind"] = "winget", ["id"] = "Anthropic.ClaudeCode", ["scope"] = "user" }));
        Check(ok.Items[0].Entry(SetupPlatform.Windows)!.Install == new SetupRecipe.Winget("Anthropic.ClaudeCode", "user"), "a winget recipe under windows decodes");
        return Task.CompletedTask;
    }

    internal static Task PrerequisitesComeFirstOnWindows()
    {
        var catalog = SetupCatalog.Bundled();
        IReadOnlyList<string> Order(params string[] picked) => SetupPlan.Order(picked.ToHashSet(), catalog, SetupPlatform.Windows, _ => false);
        Check(Order("paperthin").SequenceEqual(["node", "paperthin"]), "Paperthin needs Node.js first");
        Check(Order("ouroboros").SequenceEqual(["claude", "uv", "ouroboros"]), "Ouroboros needs Claude Code and uv first: " + string.Join(",", Order("ouroboros")));
        Check(Order("glab", "codex", "tailscale", "agy").SequenceEqual(["codex", "agy", "tailscale", "glab"]), "with nothing between them the catalog's order holds");
        Check(Order("homebrew", "gstack", "rm-rf").Count == 0, "a Mac tool or an unknown ID is never planned on Windows");
        Check(SetupPlan.Order(new HashSet<string> { "ouroboros" }, catalog, SetupPlatform.Windows, id => id == "claude").SequenceEqual(["uv", "ouroboros"]), "a prerequisite that is there is not planned");
        Check(SetupPlan.AddedPrerequisites(new HashSet<string> { "paperthin", "omc" }, catalog, SetupPlatform.Windows, _ => false).Order().SequenceEqual(["claude", "node"]), "the added prerequisites are the unpicked ones");
        return Task.CompletedTask;
    }

    internal static Task TheScriptIsPowerShellFromTheCatalogAlone()
    {
        Check(SetupScript.Quote("plain") == "'plain'" && SetupScript.Quote("it's $(id) `x` \"y\"") == "'it''s $(id) `x` \"y\"'", "single quotes keep every value literal");
        Check(SetupScript.Quote("a\u2019b") == "'a\u2019\u2019b'", "PowerShell's typographic quotes are doubled too");
        var environment = new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\me", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
            ["ProgramFiles"] = @"C:\Program Files", ["SystemRoot"] = @"C:\Windows",
        };
        var catalog = SetupCatalog.Bundled();
        var ids = SetupPlan.Order(new HashSet<string> { "claude", "git", "ouroboros", "paperthin", "gh", "tailscale" }, catalog, SetupPlatform.Windows, _ => false);
        var steps = SetupScript.Steps(ids, catalog, SetupCatalog.Title);
        Check(steps.Select(step => step.ItemId).SequenceEqual(["claude", "git", "node", "uv", "ouroboros", "paperthin", "gh"]), "a page-only item has no step: " + string.Join(",", steps.Select(step => step.ItemId)));
        string Body(string id) => steps.Single(step => step.ItemId == id).Body;
        Check(Body("claude") == "Invoke-MightyScript 'https://claude.ai/install.ps1'", "an official script goes through the https-only fetch: " + Body("claude"));
        Check(Body("git") == "winget install --exact --id 'Git.Git' --source winget --scope user --accept-package-agreements --accept-source-agreements", "winget: " + Body("git"));
        Check(Body("gh").Contains("--scope machine", StringComparison.Ordinal), "gh is machine scope");
        Check(Body("ouroboros") == "claude plugin marketplace add 'Q00/ouroboros'; claude plugin install 'ouroboros@ouroboros' --scope user", "plugin: " + Body("ouroboros"));
        Check(Body("paperthin") == "npx 'skills@1.7.2' 'add' 'LilMGenius/paperthin' '--global' '--agent' 'claude-code'", "argv: " + Body("paperthin"));
        var preamble = SetupScript.Preamble(environment);
        Check(preamble[0] == "Set-Location -LiteralPath $HOME", "the home folder is the working folder first");
        Check(preamble[1].StartsWith("$env:Path += ';C:\\Users\\me", StringComparison.Ordinal) && preamble[1].Contains("agy", StringComparison.Ordinal) && preamble[1].Contains("Codex", StringComparison.Ordinal), "the installers' folders follow the PATH: " + preamble[1]);
        Check(preamble.Contains("$env:npm_config_registry = 'https://registry.npmjs.org/'"), "npm's registry is pinned");
        var fetch = preamble.Single(line => line.StartsWith("function Get-MightyHttps", StringComparison.Ordinal));
        Check(fetch.Contains("AllowAutoRedirect = $false", StringComparison.Ordinal) && fetch.Contains("$uri.Scheme -ne 'https'", StringComparison.Ordinal), "each redirect is followed by hand and must stay on https");
        var run = preamble.Single(line => line.StartsWith("function Invoke-MightyScript", StringComparison.Ordinal));
        Check(run.Contains("[IO.FileMode]::CreateNew", StringComparison.Ordinal) && run.Contains("-NoProfile -ExecutionPolicy Bypass -File $file", StringComparison.Ordinal), "a fetched script runs as a new file in a PowerShell of its own");
        var labels = new SetupScriptLabels((index, count, title) => $"[{index}/{count}] {title}", "failed's step", "done", "close");
        var script = SetupScript.Render(steps, labels, environment, @"C:\s\started-1", @"C:\s\done-1");
        Check(script.Contains("$mightyStarted = 'C:\\s\\started-1'", StringComparison.Ordinal) && script.Contains("try { New-MightyMarker $mightyStarted;", StringComparison.Ordinal), "the start marker is made first, never over something already there");
        Check(script.Contains("function New-MightyMarker([string]$Path) { if (Test-Path -LiteralPath $Path) { throw", StringComparison.Ordinal), "a marker is made only where nothing is, and [ ] in its path are plain characters");
        Check(fetch.Contains("$memory.Length -gt 16777216", StringComparison.Ordinal) && !fetch.Contains("ReadToEnd", StringComparison.Ordinal), "a fetched script larger than 16 MB is refused rather than read on");
        Check(script.IndexOf("$mightyStarted", StringComparison.Ordinal) < script.IndexOf("Set-Location", StringComparison.Ordinal), "the start marker comes before anything else runs");
        Check(script.Contains("} finally { try { New-MightyMarker $mightyDone } catch { }; $null = Read-Host 'close' }", StringComparison.Ordinal), "the end marker is made however the steps end, and the window waits for Enter even after Control-C");
        Check(preamble.Contains("$mightyPowerShell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\\v1.0\\powershell.exe'"), "a fetched script runs in the system's PowerShell, wherever SystemRoot points");
        Check(steps.All(step => script.Contains("try { $global:LASTEXITCODE = 0; " + step.Body + "; if ($LASTEXITCODE)", StringComparison.Ordinal)), "each step runs in a try of its own");
        Check(script.Contains("Write-Host 'failed''s step'", StringComparison.Ordinal), "the labels are quoted like every other value");
        Check(SetupScriptLabels.Localized().Close == Locale.Get("setup.windows.script.close") && SetupScriptLabels.Localized().Step(2, 5, "uv") == "[2/5] uv", "the script speaks the app's language");
        foreach (var word in new[] { "RunAs", "sudo", "Start-Process", "-Verb", "Get-Credential", "Set-ExecutionPolicy" }) Check(!script.Contains(word, StringComparison.OrdinalIgnoreCase), "the script never elevates or asks for a password: " + word);
        // On Windows the system folder Windows reports (its own spelling, often C:\WINDOWS\system32); elsewhere SystemRoot's System32.
        var systemPowerShell = Path.Combine(OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : @"C:\Windows" + Path.DirectorySeparatorChar + "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Check(SetupScript.LaunchArguments(@"C:\s\install-1.ps1", environment).SequenceEqual([systemPowerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\s\install-1.ps1"]),
            "the system's PowerShell runs the file with no profile and this process's policy only");
        var plan = CliAccountTerminal.LaunchPlan(SetupScript.LaunchArguments(@"C:\s\install-1.ps1", environment), true, @"C:\s", SetupScript.PowerShell(environment));
        Check(plan.Executable == "wt.exe" && plan.Arguments.SequenceEqual(["new-tab", "-d", @"C:\s", "--", .. SetupScript.LaunchArguments(@"C:\s\install-1.ps1", environment)]), "Windows Terminal opens a tab in the setup folder");
        var found = CliAccountTerminal.LaunchPlan(SetupScript.LaunchArguments(@"C:\s\install-1.ps1", environment), true, @"C:\s", SetupScript.PowerShell(environment), @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe");
        Check(found.Executable == @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", "Windows Terminal is started by the full path found for it");
        Check(CliAccountTerminal.LaunchPlan(SetupScript.LaunchArguments(@"C:\100%\install-1.ps1", environment), true, @"C:\s", null, @"C:\wt.exe").Executable == CliAccountTerminal.ConsoleHost
            && CliAccountTerminal.LaunchPlan(["agy"], true, @"C:\%TEMP%", null, @"C:\wt.exe").Arguments.SequenceEqual(["new-tab", "--", "agy"]),
            "a % that Windows Terminal would expand sends the command to the console host and keeps the folder off its command line");
        return Task.CompletedTask;
    }

    internal static Task TheScriptFolderIsOursAndMarkersNeverFollowLinks()
    {
        var root = Verification.Temp();
        try
        {
            Check(SetupScriptFiles.Folder(null, @"C:\Users\me\AppData\Local") == Path.Combine(@"C:\Users\me\AppData\Local", "MightyClaudeNative", "setup") && SetupScriptFiles.Folder(root, "x") == Path.Combine(root, "setup"),
                "the folder is the app's machine-local one, or the profile's");
            var folder = Path.Combine(root, "setup");
            SetupScriptFiles.PrepareDirectory(folder);
            if (!OperatingSystem.IsWindows()) Check((File.GetUnixFileMode(folder) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, "only this user may open the folder");
            var script = SetupScriptFiles.WriteScript("Write-Host 'ツール'\r\n", folder);
            var bytes = File.ReadAllBytes(script);
            Check(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) == "Write-Host 'ツール'\r\n", "the script is UTF-8 with its mark, so Windows PowerShell reads more than ASCII");
            Check(Path.GetFileName(script).StartsWith("install-", StringComparison.Ordinal) && SetupScriptFiles.WriteScript("x", folder) != script, "each script has a new random name");
            // A link where the folder should be is refused, and so is a marker that is a link.
            var elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
            var linked = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(linked, elsewhere);
            var refused = false;
            try { SetupScriptFiles.PrepareDirectory(linked); } catch (IOException) { refused = true; }
            Check(refused, "a link in place of the setup folder must be refused");
            var (started, done) = SetupScriptFiles.Markers(folder);
            Check(!SetupScriptFiles.MarkerExists(started) && !SetupScriptFiles.RunEnded(started, done, _ => true), "a run that has not started has not ended");
            Directory.CreateSymbolicLink(done, elsewhere);
            Check(!SetupScriptFiles.MarkerExists(done) && !SetupScriptFiles.RunEnded(started, done, _ => true), "a link is no marker");
            // A directory link: Windows removes it with RemoveDirectory (File.Delete is refused), elsewhere it is unlinked, as RemoveLeftovers does.
            if (OperatingSystem.IsWindows()) Directory.Delete(done, false); else File.Delete(done);
            Directory.CreateDirectory(started);
            Check(SetupScriptFiles.RunnerProcessId(started) is null && !SetupScriptFiles.RunEnded(started, done, _ => false), "until the script wrote its process ID the run goes on");
            File.WriteAllText(Path.Combine(started, "pid"), "4242\r\n");
            Check(SetupScriptFiles.RunnerProcessId(started) == 4242, "the script's PowerShell is read from the start marker");
            Check(!SetupScriptFiles.RunEnded(started, done, id => id == 4242), "a run whose PowerShell is alive goes on");
            Check(SetupScriptFiles.RunEnded(started, done, _ => false), "a run whose window was closed has ended");
            Directory.CreateDirectory(done);
            Check(SetupScriptFiles.RunEnded(started, done, _ => true), "the end marker ends the run");
            // Leftovers go; a link is removed itself and what it points to stays.
            File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(folder, "started-old"), elsewhere);
            SetupScriptFiles.RemoveLeftovers(folder);
            Check(!Directory.EnumerateFileSystemEntries(folder).Any() && File.Exists(Path.Combine(elsewhere, "keep.txt")), "leftovers are removed without following a link");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task ProbesReadThePathTheInstallersFoldersPluginsAndSkills()
    {
        var root = Verification.Temp();
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            var local = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            File.WriteAllText(Path.Combine(bin, "claude.exe"), "");
            File.WriteAllText(Path.Combine(other, "claude.exe"), "");
            File.WriteAllText(Path.Combine(bin, "npm.cmd"), "");
            // agy is not on the PATH yet: its installer's folder is looked in.
            Directory.CreateDirectory(Path.Combine(local, "agy", "bin"));
            File.WriteAllText(Path.Combine(local, "agy", "bin", "agy.exe"), "");
            var environment = new Dictionary<string, string> { ["Path"] = bin + Path.PathSeparator + other, ["USERPROFILE"] = home, ["LOCALAPPDATA"] = local };
            var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [Path.Combine(bin, "claude.exe")] = null, [Path.Combine(other, "claude.exe")] = "2.1.300 (Claude Code)" };
            var asked = new List<string>();
            var prober = new SetupProber(environment, (path, _) => { asked.Add(path); return Task.FromResult(versions.GetValueOrDefault(path)); });
            var claude = await prober.ProbeAsync(new SetupProbe.Executable("claude", "2.1.271"));
            Check(claude.State == SetupProbeState.Installed && claude.Version == "2.1.300 (Claude Code)" && asked.Count == 2, "a copy that will not say its version falls back to the next one");
            Check(SetupProber.Executables("npm", environment) is [var npm] && npm.EndsWith("npm.cmd", StringComparison.OrdinalIgnoreCase), "a .cmd shim counts as the program");
            Check((await prober.ProbeAsync(new SetupProbe.Executable("agy", null))).State == SetupProbeState.Installed, "agy is found in its installer's folder before PATH lists it");
            Check((await prober.ProbeAsync(new SetupProbe.Executable("codex", null))).State == SetupProbeState.Missing, "a program nowhere is missing");
            versions[Path.Combine(other, "claude.exe")] = "2.0.0 (Claude Code)";
            var old = await prober.ProbeAsync(new SetupProbe.Executable("claude", "2.1.271"));
            Check(old.State == SetupProbeState.Outdated && old.Minimum == "2.1.271" && old.ShownVersion == "2.0.0 (Claude Code)" && !old.IsInstalled, "an old Claude is out of date, not missing");
            versions[Path.Combine(other, "claude.exe")] = null;
            Check((await prober.ProbeAsync(new SetupProbe.Executable("claude", "2.1.271"))) is { State: SetupProbeState.Unverified, IsInstalled: true }, "with no version read it counts as there, unchecked");
            Check(!SetupProber.Meets("2.1.300-beta.1", "2.1.271") && SetupProber.Meets("v2.1.271", "2.1.271") && !SetupProber.Meets("2.1.9", "2.1.271"), "versions compare by number and a prerelease falls short");
            // Plugins by their whole name in user scope; skills by SKILL.md and the setup markers.
            Directory.CreateDirectory(Path.Combine(home, ".claude", "plugins"));
            File.WriteAllText(Path.Combine(home, ".claude", "plugins", "installed_plugins.json"),
                """{"plugins":{"ouroboros@ouroboros":[{"scope":"user"}],"superpowers-extra@x":[{"scope":"user"}],"oh-my-claudecode@omc":[{"scope":"project"}]}}""");
            Check((await prober.ProbeAsync(new SetupProbe.ClaudePlugin("ouroboros@"))).IsInstalled, "a user-scope plugin counts");
            Check(!(await prober.ProbeAsync(new SetupProbe.ClaudePlugin("superpowers@"))).IsInstalled, "a longer name is another plugin");
            Check(!(await prober.ProbeAsync(new SetupProbe.ClaudePlugin("oh-my-claudecode@"))).IsInstalled, "a project-scope record does not count");
            var skill = Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "re0")).FullName;
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "x");
            Check((await prober.ProbeAsync(new SetupProbe.Skill("re0"))).IsInstalled, "a skill the user placed counts");
            File.WriteAllText(Path.Combine(skill, SetupProber.SkillInstallingMarker), "");
            Check(!(await prober.ProbeAsync(new SetupProbe.Skill("re0"))).IsInstalled, "a placed skill counts only once its setup finished");
            var all = await new SetupProber(environment, (_, _) => Task.FromResult<string?>("1.0.0")).ProbeAllAsync(SetupCatalog.Bundled(), new HashSet<string> { "agy", "homebrew" });
            Check(all.Keys.SequenceEqual(["agy"]), "only the asked items with a Windows entry are probed");
        }
        finally { Directory.Delete(root, true); }
    }

    /// The WinUI app opens the screen from every place the Mac does, and its smoke runs in CI (read from the source, as WinUI does not build here).
    internal static Task TheScreenIsWiredIntoTheApp()
    {
        var winui = ClaudePluginVerification.WinUISource();
        string Source(string file) => File.ReadAllText(Path.Combine(winui, file));
        Check(Source("MainWindow.cs").Contains("BeginAutomaticAppUpdateCheck(); await PresentSetupIfNeeded();", StringComparison.Ordinal), "the launch checks the tools after the update checks have begun");
        Check(Source("MainWindow.cs").Contains("setupWindow?.Close();", StringComparison.Ordinal), "closing the app closes the setup window");
        Check(Source("MainWindow.EmptyStates.cs").Contains("\"welcome-setup\"", StringComparison.Ordinal), "the welcome offers the screen");
        Check(Source("MainWindow.Composer.cs").Contains("\"run-blocked-install-\" + id", StringComparison.Ordinal) && Source("MainWindow.Composer.cs").Contains("owner.OpenSetup(item)", StringComparison.Ordinal), "a pane blocked for a missing CLI offers its install");
        Check(Source("MainWindow.Shell.cs").Contains("\"setup-finished-badge\"", StringComparison.Ordinal), "an install that ended behind another window says so in the status bar");
        var setup = Source("MainWindow.Setup.cs");
        Check(!setup.Contains("dialogOpen", StringComparison.Ordinal), "the setup window holds back neither the phone nor the sign-in recovery");
        Check(setup.Contains("SetupScript.LaunchArguments(script, environment)", StringComparison.Ordinal) && setup.Contains("CliAccountTerminal.LaunchPlan(argv", StringComparison.Ordinal), "the install runs in the sign-in's kind of window");
        Check(setup.Contains("await CliEnvironment.RefreshAsync(true)", StringComparison.Ordinal), "a forced check reads PATH again from the registry");
        Check(setup.Contains("if (SetupRunning || closing || options.SmokeTest || RefuseSetupWhenElevated()) return;", StringComparison.Ordinal)
            && setup.Contains("if (SetupRunning || RefuseSetupWhenElevated() ||", StringComparison.Ordinal), "a smoke run never starts an install, and an elevated app refuses before the confirmation");
        Check(setup.Contains("info.Environment[WindowsCommand.NoCurrentDirectorySearch] = \"1\";", StringComparison.Ordinal), "the install window's shims never run a node planted in the step's folder");
        Check(setup.Contains("if (options.SmokeTest || closing || SetupFirstRun.NeverShow()) return;", StringComparison.Ordinal), "never-show skips the launch probes");
        Check(setup.Contains("if (options.SmokeTest && setupFixedResults is null) { RedrawSetup(); return; }", StringComparison.Ordinal), "a smoke run's 다시 확인 probes nothing of the machine");
        Check(Locale.Get("setup.windows.error.elevated") is { Length: > 0 } elevated && !elevated.StartsWith("setup.", StringComparison.Ordinal), "the elevated refusal is said in words");
        Check(Source("MainWindow.Smoke.cs").Contains("result[\"setupTools\"] = await RunSetupSmoke();", StringComparison.Ordinal), "the GUI smoke drives the screen");
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "test-native-windows.ps1"));
        Check(script.Contains("$result.setupTools.$key", StringComparison.Ordinal) && script.Contains("'cancelledWithoutTerminal'", StringComparison.Ordinal), "CI reads the setup smoke's result");
        return Task.CompletedTask;
    }

    internal static Task RowsAndTheFirstRunFollowTheProbes()
    {
        var catalog = SetupCatalog.Bundled();
        var missing = SetupProbeResult.Missing; var there = SetupProbeResult.Installed("1");
        Check(SetupRows.Derive(null, false, true) == SetupRowState.Checking && SetupRows.Derive(missing, true, false) == SetupRowState.Installing && SetupRows.Derive(missing, true, true) == SetupRowState.Failed
            && SetupRows.Derive(missing, false, true) == SetupRowState.Missing && SetupRows.Derive(new(SetupProbeState.Outdated, "1", "2"), false, true) == SetupRowState.Outdated, "a row's state follows the probe and the run");
        Check(SetupRows.IsSelectable(SetupRowState.Failed, catalog.Item("git")!) && !SetupRows.IsSelectable(SetupRowState.Outdated, catalog.Item("claude")!) && !SetupRows.IsSelectable(SetupRowState.Missing, catalog.Item("tailscale")!),
            "a failed item can be picked again, an old agent CLI gets its update, and a page is no install");
        var results = catalog.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null).ToDictionary(item => item.Id, _ => missing);
        Check(SetupFirstRun.ShouldPresent(catalog, results, false) && !SetupFirstRun.ShouldPresent(catalog, results, true), "nothing installed opens the screen, unless never again was chosen");
        Check(SetupFirstRun.Preselection(catalog, results).Order().SequenceEqual(["claude", "git"]), "the first showing ticks Git and Claude Code");
        results["codex"] = there; results["git"] = there;
        Check(!SetupFirstRun.ShouldPresent(catalog, results, false) && SetupFirstRun.Preselection(catalog, results).Count == 0, "Git and any one agent CLI are enough");
        results["git"] = missing;
        Check(SetupFirstRun.ShouldPresent(catalog, results, false) && SetupFirstRun.Preselection(catalog, results).SequenceEqual(["git"]), "a missing Git opens the screen and is ticked");
        results.Remove("glab");
        Check(!SetupFirstRun.ShouldPresent(catalog, results, false), "until every item has been probed nothing opens");
        var runtime = new[] { new ProviderRuntime("claude", "Claude Code", true, "2.1.300", "", new ModelCatalog("cli", [], ""), new ProviderCapabilities(true, [], true, true, true)) };
        Check(!SetupRuntimeSync.IsStale(runtime, catalog, new Dictionary<string, SetupProbeResult> { ["claude"] = SetupProbeResult.Installed("2.1.300") })
            && SetupRuntimeSync.IsStale(runtime, catalog, new Dictionary<string, SetupProbeResult> { ["codex"] = SetupProbeResult.Installed("0.160.0") }), "a CLI that appeared makes the runtime stale");
        return Task.CompletedTask;
    }
}
