using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// opencode and Hermes in the shared setup catalog on Windows (M/MightyCoreTests/OpencodeTests.swift and HermesTests.swift, Setup):
/// their Windows entries beside the Mac's, the steps the install script runs for them, the installer's allowed host, where Windows
/// finds each CLI and the version it reads. Nothing is installed; the probes read fixture folders through an injected version
/// reader, and the one process started is a stand-in hermes (off Windows) that only prints its version.
internal static class OpencodeHermesSetupVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "setup-catalog.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    private static string FixtureText() => File.ReadAllText(Path.Combine(Root(), "native", "contracts", "fixtures", "setup-catalog.json"));

    private static void Refused(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("the decoder took " + message);
    }

    /// <summary>What Hermes 0.23.1 prints for <c>--version</c> (docs/hermes-agent.md): its version line, then its install, Python and update status.</summary>
    private const string HermesVersion = "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882";
    private const string HermesOutput = HermesVersion + "\nInstall directory: C:\\Users\\me\\AppData\\Local\\hermes\\hermes-agent\nInstall method: git\nPython: 3.14.9\nOpenAI SDK: 2.54.0\nUp to date\n";

    private static readonly Dictionary<string, string> WindowsEnvironment = new()
    {
        ["USERPROFILE"] = @"C:\Users\me", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
        ["ProgramFiles"] = @"C:\Program Files", ["SystemRoot"] = @"C:\Windows",
    };

    internal static Task TheContractHoldsBothWithAWindowsEntryBesideTheMacs()
    {
        var catalog = SetupCatalogDecoder.Decode(FixtureText());
        var agents = catalog.Items.Where(item => item.Group == SetupGroup.Agent).Select(item => item.Id).ToList();
        Check(agents.SequenceEqual(["claude", "codex", "agy", "pi", "opencode", "hermes"]), "opencode and Hermes follow pi among the agents: " + string.Join(", ", agents));
        var opencode = catalog.Item("opencode")!; var hermes = catalog.Item("hermes")!;
        Check(opencode.Provider == "opencode" && hermes.Provider == "hermes" && !opencode.Required && !hermes.Required, "both offer their sign-in after the install, and neither is required");
        Check(opencode.LabelKey == "setup.item.opencode.title" && opencode.DescriptionKey == "setup.item.opencode.detail" && hermes.LabelKey == "setup.item.hermes.title" && hermes.DescriptionKey == "setup.item.hermes.detail", "the Mac's keys");
        foreach (var platform in new[] { SetupPlatform.MacOS, SetupPlatform.Windows })
        {
            var entry = opencode.Entry(platform)!;
            Check(entry.Probe == new SetupProbe.Executable("opencode", "2.0.0") && entry.Install == new SetupRecipe.NpmGlobal("@opencode/cli") && entry.Prerequisites.SequenceEqual(["node"])
                && entry.Source == new Uri("https://github.com/anomalyco/opencode"), "OpenCode 2 from npm after Node.js on " + platform);
        }
        var mac = hermes.Entry(SetupPlatform.MacOS)!; var windows = hermes.Entry(SetupPlatform.Windows)!;
        Check(mac.Probe == new SetupProbe.Executable("hermes", "0.23.0") && mac.Install is SetupRecipe.OfficialScript { Shell: "bash" } sh && sh.Url.AbsoluteUri == "https://hermes-agent.nousresearch.com/install.sh"
            && mac.Prerequisites.SequenceEqual(["git"]), "the Mac's entry stays as it was: install.sh after the command line tools");
        Check(windows.Probe == new SetupProbe.Executable("hermes", "0.23.0") && windows.Install is SetupRecipe.OfficialScript { Shell: "powershell" } ps && ps.Url.AbsoluteUri == "https://hermes-agent.nousresearch.com/install.ps1"
            && windows.Prerequisites.Count == 0 && windows.Source == new Uri("https://github.com/NousResearch/hermes-agent"), "Windows runs install.ps1, which stages its own Git, uv and Python: nothing goes first");
        // The closed decoder knows both: their keys, their providers, the installer's host and its repository.
        Check(new[] { "setup.item.opencode.title", "setup.item.opencode.detail", "setup.item.hermes.title", "setup.item.hermes.detail" }.All(SetupCatalog.KnownKeys.Contains)
            && Wire.Providers.Contains("opencode") && Wire.Providers.Contains("hermes"), "the keys and providers are known");
        Check(SetupCatalog.AllowedHosts.Contains("hermes-agent.nousresearch.com") && SetupCatalog.AllowedRepositories.Contains("NousResearch/hermes-agent"), "Hermes's host and repository are allowed");
        Check(SetupCatalog.Bundled().Item("opencode")?.Entry(SetupPlatform.Windows) is not null && SetupCatalog.Bundled().Item("hermes")?.Entry(SetupPlatform.Windows) is not null, "the app carries both");
        // A row's words: opencode's are the Mac's; Hermes's Windows detail names its Windows folder and asks for no Git first.
        Check(SetupCatalog.TitleKey(opencode) == "setup.item.opencode.title" && SetupCatalog.DetailKey(opencode) == "setup.item.opencode.detail", "opencode reads the Mac's words");
        Check(SetupCatalog.TitleKey(hermes) == "setup.item.hermes.title" && SetupCatalog.DetailKey(hermes) == "setup.windows.item.hermes.detail", "Hermes reads its Windows detail");
        foreach (var language in Locale.Languages)
        {
            using var stream = typeof(Locale).Assembly.GetManifestResourceStream("MightyCode.Core.Locales." + language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            var detail = strings.GetValueOrDefault("setup.windows.item.hermes.detail") ?? "";
            Check(detail.Contains(@"%LOCALAPPDATA%\hermes", StringComparison.Ordinal) && !detail.Contains("~/.hermes", StringComparison.Ordinal) && detail.Contains("PowerShell", StringComparison.Ordinal), language + ": Hermes's Windows detail");
        }
        return Task.CompletedTask;
    }

    internal static Task OpencodesStepRemovesOpenCode1FirstAndAnOldOneMayBeInstalledAgain()
    {
        var catalog = SetupCatalog.Bundled();
        var opencode = catalog.Item("opencode")!;
        Check(SetupScript.Body(opencode.Entry(SetupPlatform.Windows)!) == "npm uninstall -g 'opencode-ai' *> $null; npm install -g '@opencode/cli'",
            "OpenCode 1 (npm opencode-ai) owns the same command and goes first, quietly, whether or not it is there: " + SetupScript.Body(opencode.Entry(SetupPlatform.Windows)!));
        Check(SetupCatalog.NpmReplaces.Count == 1 && SetupCatalog.NpmReplaces["@opencode/cli"] == "opencode-ai", "only OpenCode 2 replaces a package");
        Check(SetupScript.Body(catalog.Item("pi")!.Entry(SetupPlatform.Windows)!) == "npm install -g '@mariozechner/pi-coding-agent'", "a package that replaces none is installed alone");
        Check(SetupPlan.Order(new HashSet<string> { "opencode" }, catalog, SetupPlatform.Windows, _ => false).SequenceEqual(["node", "opencode"]), "Node.js first, then opencode");
        Check(SetupPlan.Order(new HashSet<string> { "opencode" }, catalog, SetupPlatform.Windows, _ => true).SequenceEqual(["opencode"]), "with Node.js there, opencode alone");
        // An OpenCode 1 reads out of date and may be picked again, since its own upgrade stays on 1; other old agent CLIs get their update.
        var old = new SetupProbeResult(SetupProbeState.Outdated, "1.19.5", "2.0.0");
        Check(SetupRows.Derive(old, false, true) == SetupRowState.Outdated && SetupRows.IsSelectable(SetupRowState.Outdated, opencode) && SetupRows.IsSelectable(SetupRowState.Missing, opencode), "an old opencode may be picked for install");
        Check(SetupRows.Derive(old, true, false) == SetupRowState.Installing, "the picked old opencode reads as installing while the script runs");
        Check(new[] { "claude", "pi", "hermes" }.All(id => !SetupRows.IsSelectable(SetupRowState.Outdated, catalog.Item(id)!)), "other old agent CLIs get the CLI update instead");
        var steps = SetupScript.Steps(SetupPlan.Order(new HashSet<string> { "opencode" }, catalog, SetupPlatform.Windows, _ => false), catalog, SetupCatalog.Title);
        var script = SetupScript.Render(steps, new SetupScriptLabels((index, count, title) => $"[{index}/{count}] {title}", "failed", "done", "close"), WindowsEnvironment, @"C:\s\started-1", @"C:\s\done-1");
        Check(script.Contains("try { $global:LASTEXITCODE = 0; npm uninstall -g 'opencode-ai' *> $null; npm install -g '@opencode/cli'; if ($LASTEXITCODE)", StringComparison.Ordinal),
            "the step runs in a try of its own, the install's exit code deciding it");
        Check(ComponentSection.InstallCommand("opencode") == "npm install -g @opencode/cli", "the copyable command");
        return Task.CompletedTask;
    }

    internal static Task HermessStepRunsItsPowerShellInstallerNonInteractive()
    {
        var catalog = SetupCatalog.Bundled();
        var body = SetupScript.Body(catalog.Item("hermes")!.Entry(SetupPlatform.Windows)!);
        Check(body == "Invoke-MightyScript 'https://hermes-agent.nousresearch.com/install.ps1' -ScriptArguments @('-NonInteractive')",
            "fetched over https only and run as a file of its own with -NonInteractive, which skips its setup wizard and gateway service: " + body);
        Check(SetupScript.Body(catalog.Item("claude")!.Entry(SetupPlatform.Windows)!) == "Invoke-MightyScript 'https://claude.ai/install.ps1'", "another official script gets no option");
        Check(SetupCatalog.ScriptArguments.Count == 1 && SetupCatalog.ScriptArguments["https://hermes-agent.nousresearch.com/install.ps1"].SequenceEqual(["-NonInteractive"]), "only Hermes's installer takes options");
        var run = SetupScript.Preamble(WindowsEnvironment).Single(line => line.StartsWith("function Invoke-MightyScript", StringComparison.Ordinal));
        Check(run.StartsWith("function Invoke-MightyScript([string]$Url, [string[]]$ScriptArguments = @()) {", StringComparison.Ordinal)
            && run.Contains("& $mightyPowerShell -NoProfile -ExecutionPolicy Bypass -File $file @ScriptArguments }", StringComparison.Ordinal),
            "the options follow the script's file, where they bind as its own parameters");
        Check(SetupPlan.Order(new HashSet<string> { "hermes" }, catalog, SetupPlatform.Windows, _ => false).SequenceEqual(["hermes"]), "nothing goes before Hermes on Windows");
        Check(SetupPlan.Order(new HashSet<string> { "hermes" }, catalog, SetupPlatform.MacOS, _ => false).SequenceEqual(["git", "hermes"]), "the Mac's installer wants git first");
        Check(!SetupRows.IsSelectable(SetupRowState.Outdated, catalog.Item("hermes")!) && SetupRows.IsSelectable(SetupRowState.Missing, catalog.Item("hermes")!), "a missing Hermes is installed, an old one updates itself");
        var steps = SetupScript.Steps(["git", "hermes"], catalog, SetupCatalog.Title);
        var script = SetupScript.Render(steps, new SetupScriptLabels((index, count, title) => $"[{index}/{count}] {title}", "failed", "done", "close"), WindowsEnvironment, @"C:\s\started-1", @"C:\s\done-1");
        Check(script.Contains("try { $global:LASTEXITCODE = 0; " + body + "; if ($LASTEXITCODE)", StringComparison.Ordinal), "the step runs in a try of its own, its exit code deciding it");
        Check(ComponentSection.InstallCommand("hermes") == "& ([scriptblock]::Create((irm https://hermes-agent.nousresearch.com/install.ps1))) -NonInteractive",
            "the copyable command passes the same option through a scriptblock, which irm | iex cannot");
        // The installer's host is allowed and only it: a look-alike host, plain http, another host of Nous Research's and another owner's repository are refused.
        var root = JsonNode.Parse(FixtureText())!.AsObject();
        void Install(string url) => root["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "hermes")!["platforms"]!["windows"]!["install"] = new JsonObject { ["kind"] = "officialScript", ["url"] = url, ["shell"] = "powershell" };
        foreach (var url in new[] { "https://hermes-agent.nousresearch.com.example.org/install.ps1", "http://hermes-agent.nousresearch.com/install.ps1", "https://hermes-assets.nousresearch.com/install.ps1", "https://github.com/someone/hermes-agent/raw/main/scripts/install.ps1" })
        {
            Install(url);
            Refused(() => SetupCatalogDecoder.Decode(root.ToJsonString()), url);
        }
        Install("https://raw.githubusercontent.com/NousResearch/hermes-agent/main/scripts/install.ps1");
        Check(SetupCatalogDecoder.Decode(root.ToJsonString()).Item("hermes")!.Entry(SetupPlatform.Windows)!.Install is SetupRecipe.OfficialScript { Url.Host: "raw.githubusercontent.com" }, "Hermes's own repository is one the catalog may name");
        Install("https://hermes-agent.nousresearch.com/install.ps1");
        root["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "hermes")!["platforms"]!["windows"]!["install"]!["shell"] = "bash";
        Refused(() => SetupCatalogDecoder.Decode(root.ToJsonString()), "install.ps1 in bash");
        return Task.CompletedTask;
    }

    internal static async Task WindowsFindsOpencodeAndHermesWhereTheirInstallersPutThem()
    {
        var local = @"C:\Users\me\AppData\Local"; var npm = @"C:\Users\me\AppData\Roaming\npm"; var tools = @"C:\tools";
        var hermesFolder = Path.Combine(local, "hermes", "bin");
        Check(CliDiscovery.HermesInstallFolder(WindowsEnvironment) == hermesFolder, "Hermes's installer folder: " + CliDiscovery.HermesInstallFolder(WindowsEnvironment));
        Check(CliDiscovery.HermesInstallFolder(new Dictionary<string, string>(WindowsEnvironment) { ["hermes_home"] = @"D:\hermes" }) == Path.Combine(@"D:\hermes", "bin"), "HERMES_HOME's bin, the variable named in any case");
        Check(CliDiscovery.HermesInstallFolder(new Dictionary<string, string>(WindowsEnvironment) { ["HERMES_HOME"] = "hermes" }) == hermesFolder, "a relative HERMES_HOME is not the folder");
        Check(CliDiscovery.HermesInstallFolder(new Dictionary<string, string>()) is null, "no folder when the environment names none");
        // ProviderCatalog's discovery: folder by folder in the PATH's order, as a terminal finds the command.
        var opencode = CliDiscovery.Files("opencode", [tools, npm], true, WindowsEnvironment);
        Check(opencode.SequenceEqual([Path.Combine(tools, "opencode.exe"), Path.Combine(tools, "node_modules", "@opencode", "cli", "bin", "opencode.exe"), Path.Combine(tools, "opencode.cmd"),
            Path.Combine(npm, "opencode.exe"), Path.Combine(npm, "node_modules", "@opencode", "cli", "bin", "opencode.exe"), Path.Combine(npm, "opencode.cmd")]),
            "opencode's binary, npm's copy of OpenCode 2's binary, then the shim: " + string.Join(", ", opencode));
        var hermes = CliDiscovery.Files("hermes", [tools], true, WindowsEnvironment);
        Check(hermes.SequenceEqual([Path.Combine(tools, "hermes.exe"), Path.Combine(tools, "hermes.cmd"), Path.Combine(hermesFolder, "hermes.exe"), Path.Combine(hermesFolder, "hermes.cmd")]),
            "hermes.exe, then its .cmd launcher, the installer's folder last: " + string.Join(", ", hermes));
        Check(CliDiscovery.Files("hermes", [hermesFolder.ToUpperInvariant()], true, WindowsEnvironment).Count == 2, "the installer's folder already on the PATH is tried once");
        Check(CliDiscovery.Files("pi", [npm], true, WindowsEnvironment).SequenceEqual([Path.Combine(npm, "pi.cmd")]) && CliDiscovery.Files("codex", [tools], true, WindowsEnvironment).SequenceEqual([Path.Combine(tools, "codex.exe")])
            && CliDiscovery.Files("gemini", [tools], true, WindowsEnvironment).SequenceEqual([Path.Combine(tools, "agy.exe")]) && CliDiscovery.Files("claude", [tools], true, WindowsEnvironment).SequenceEqual([Path.Combine(tools, "claude.exe")]),
            "the other CLIs are found as before");
        Check(CliDiscovery.Files("hermes", ["/usr/local/bin"], false, WindowsEnvironment).SequenceEqual([Path.Combine("/usr/local/bin", "hermes")]) && CliDiscovery.Files("opencode", ["/usr/local/bin"], false, WindowsEnvironment).SequenceEqual([Path.Combine("/usr/local/bin", "opencode")]),
            "off Windows each folder's bare name, and no Windows folder");
        var source = File.ReadAllText(Path.Combine(Root(), "native", "windows", "MightyCode.Core", "ProviderCatalog.cs"));
        Check(source.Contains("CliDiscovery.Files(provider, directories, OperatingSystem.IsWindows(), CliEnvironment.Current())", StringComparison.Ordinal), "ProviderCatalog's discovery tries those files");
        // The setup probe and the install window look in Hermes's folder too.
        Check(SetupScript.WellKnownFolders(WindowsEnvironment).Contains(hermesFolder) && SetupScript.Preamble(WindowsEnvironment)[1].Contains(hermesFolder, StringComparison.Ordinal), "the installers' folders include Hermes's");
        Check(SetupScript.WellKnownFolders(new Dictionary<string, string>(WindowsEnvironment) { ["HERMES_HOME"] = @"D:\hermes" }).Contains(Path.Combine(@"D:\hermes", "bin")), "HERMES_HOME moves it");
        // On real folders: Hermes in its installer's folder before the PATH lists it, opencode's npm shim, an OpenCode 1 out of date.
        var root = Verification.Temp();
        try
        {
            var localData = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
            var shims = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
            var installed = Directory.CreateDirectory(Path.Combine(localData, "hermes", "bin")).FullName;
            File.WriteAllText(Path.Combine(installed, "hermes.exe"), "");
            File.WriteAllText(Path.Combine(shims, "opencode.cmd"), "");
            var environment = new Dictionary<string, string> { ["Path"] = shims, ["LOCALAPPDATA"] = localData, ["USERPROFILE"] = root };
            var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [Path.Combine(installed, "hermes.exe")] = HermesOutput, [Path.Combine(shims, "opencode.cmd")] = "1.19.5" };
            var asked = new List<string>();
            Task<string?> Read(string path, CancellationToken _) { asked.Add(path); return Task.FromResult(versions.GetValueOrDefault(path)); }
            var found = await new SetupProber(environment, Read).ProbeAsync(new SetupProbe.Executable("hermes", "0.23.0"));
            Check(found is { State: SetupProbeState.Installed, Version: HermesVersion } && found.ShownVersion == HermesVersion, "Hermes in its installer's folder, its first line the version: " + found.Version);
            var old = await new SetupProber(environment, Read).ProbeAsync(new SetupProbe.Executable("opencode", "2.0.0"));
            // A copy is named with PATHEXT's spelling (opencode.CMD), which a case-insensitive disk finds: paths compare as Windows compares them.
            Check(old is { State: SetupProbeState.Outdated, Version: "1.19.5", Minimum: "2.0.0" } && string.Equals(asked.Last(), Path.Combine(shims, "opencode.cmd"), StringComparison.OrdinalIgnoreCase),
                "an npm OpenCode 1's shim is found and reads out of date: " + old.State + " " + old.Version + " from " + asked.LastOrDefault());
            versions[Path.Combine(shims, "opencode.cmd")] = "opencode v2.2.7";
            Check(await new SetupProber(environment, Read).ProbeAsync(new SetupProbe.Executable("opencode", "2.0.0")) is { State: SetupProbeState.Installed, Version: "opencode v2.2.7" }, "OpenCode 2 is there");
            // HERMES_HOME's bin instead, with the .cmd launcher Hermes writes when it cannot make the .exe.
            var home = Directory.CreateDirectory(Path.Combine(root, "hermes-home", "bin")).FullName;
            File.WriteAllText(Path.Combine(home, "hermes.cmd"), "");
            versions[Path.Combine(home, "hermes.cmd")] = HermesOutput;
            asked.Clear();
            var moved = await new SetupProber(new Dictionary<string, string>(environment) { ["HERMES_HOME"] = Path.Combine(root, "hermes-home") }, Read).ProbeAsync(new SetupProbe.Executable("hermes", "0.23.0"));
            Check(moved is { State: SetupProbeState.Installed, Version: HermesVersion } && asked.SequenceEqual([Path.Combine(home, "hermes.cmd")], StringComparer.OrdinalIgnoreCase), "HERMES_HOME's bin is the folder: " + string.Join(", ", asked));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task OnlyHermessFirstLineIsItsVersionWhereverWindowsReadsOne()
    {
        Check(CliDiscovery.VersionText("hermes", HermesOutput) == HermesVersion, "the first line: " + CliDiscovery.VersionText("hermes", HermesOutput));
        Check(CliDiscovery.VersionText("hermes", "\r\n  " + HermesVersion + "  \r\nInstall method: git\r\n") == HermesVersion, "Windows line ends and blank lines");
        Check(CliDiscovery.VersionText("hermes", "") == "" && CliDiscovery.VersionText("hermes", null) == "", "nothing printed is no version");
        Check(CliDiscovery.VersionText("claude", "2.1.300 (Claude Code)\n") == "2.1.300 (Claude Code)" && CliDiscovery.VersionText("codex", " codex-cli 0.160.0 ") == "codex-cli 0.160.0"
            && CliDiscovery.VersionText("opencode", "opencode v2.2.7\r\n") == "opencode v2.2.7", "any other CLI's output is trimmed, as before");
        Check(SetupProber.VersionNumbers(HermesVersion) is [0, 23, 1] && SetupProber.Meets(HermesVersion, "0.23.0") && !SetupProber.Meets(HermesVersion, "0.24.0"), "the line holds the version the minimum is read against");
        // The setup row and the runtime read the same line, so a probe does not keep the runtime stale.
        var runtime = new[] { new ProviderRuntime("hermes", "Hermes CLI", true, HermesVersion, "", new ModelCatalog("cli", [], ""), new ProviderCapabilities(true, [], true, true, true)) };
        Check(!SetupRuntimeSync.IsStale(runtime, SetupCatalog.Bundled(), new Dictionary<string, SetupProbeResult> { ["hermes"] = SetupProbeResult.Installed(HermesVersion) }), "the probe and the runtime agree");
        var source = File.ReadAllText(Path.Combine(Root(), "native", "windows", "MightyCode.Core", "ProviderCatalog.cs"));
        Check(source.Contains("CliDiscovery.VersionText(Executable(provider), text)", StringComparison.Ordinal), "ProviderCatalog's discovery keeps the same line");
        // The CLI update reads the same line: a stand-in hermes (off Windows) on the PATH that only prints its version.
        if (OperatingSystem.IsWindows()) return;
        var root = Verification.Temp();
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var hermes = Path.Combine(bin, "hermes");
            await File.WriteAllTextAsync(hermes, "#!/bin/sh\n[ \"$1\" = --version ] || exit 2\nprintf '%s\\n' '" + HermesVersion + "' 'Install directory: /x/.hermes/hermes-agent' 'Install method: git' 'Up to date'\n");
            File.SetUnixFileMode(hermes, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var installation = await new CliUpdateService(new CliRunner(), new Dictionary<string, string> { ["PATH"] = bin + ":/usr/bin:/bin" }, root).InspectAsync("hermes");
            Check(installation.Version == HermesVersion && installation.ExecutablePath == hermes, "the CLI update reads Hermes's first line: " + installation.Version);
        }
        finally { Directory.Delete(root, true); }
    }
}
