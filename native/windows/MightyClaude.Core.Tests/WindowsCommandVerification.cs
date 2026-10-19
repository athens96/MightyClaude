using MightyClaude.Core;

/// The toolkit's commands reach their programs on Windows (WindowsCommand, CliRunner), and an old Claude reads as out of
/// date rather than missing (ComponentSection). The lookups run over fixture folders, so they mean the same on this Mac.
internal static class WindowsCommandVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    // A Mac's disk, like Windows', ignores case: the extension comes back as PATHEXT spells it.
    private static bool Same(string? found, string expected) => string.Equals(found, expected, StringComparison.OrdinalIgnoreCase);

    internal static Task BareNamesAreFoundAlongThePathWithPathExt()
    {
        var root = Verification.Temp();
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var npm = Directory.CreateDirectory(Path.Combine(root, "npm")).FullName;
            var git = Directory.CreateDirectory(Path.Combine(root, "git")).FullName;
            File.WriteAllText(Path.Combine(npm, "npm.cmd"), "");
            File.WriteAllText(Path.Combine(npm, "npm"), "");
            File.WriteAllText(Path.Combine(git, "git.exe"), "");
            File.WriteAllText(Path.Combine(git, "git.cmd"), "");
            var path = string.Join(Path.PathSeparator, ["relative", first, npm, git]);
            // The extensionless shell script npm leaves beside npm.cmd is not something Windows can start.
            Check(Same(WindowsCommand.Find("npm", path, ".COM;.EXE;.BAT;.CMD"), Path.Combine(npm, "npm.cmd")), "npm must be found as its .cmd shim");
            // PATHEXT's order decides between two copies in one folder; an empty PATHEXT reads as Windows' default.
            Check(Same(WindowsCommand.Find("git", path, ".CMD;.EXE"), Path.Combine(git, "git.cmd")), "PATHEXT's order must decide");
            Check(Same(WindowsCommand.Find("git", path, null), Path.Combine(git, "git.exe")), "no PATHEXT must read as .COM;.EXE;.BAT;.CMD");
            Check(Same(WindowsCommand.Find("git.exe", path, null), Path.Combine(git, "git.exe")), "a name that has its extension is found as written");
            Check(WindowsCommand.Find("winget", path, null) is null, "a program on no folder is not found");
            // A rooted path stands as it is; a relative path or a folder on the PATH that is not rooted is never searched.
            Check(WindowsCommand.Find(Path.Combine(git, "git.exe"), "", null) == Path.Combine(git, "git.exe"), "a full path must be kept");
            Check(WindowsCommand.Find(Path.Combine("relative", "npm"), path, null) is null, "a relative path must not be resolved");
            Check(WindowsCommand.Find("npm", "relative" + Path.PathSeparator + "npm", null) is null, "folders that are not rooted must be skipped");
            Check(WindowsCommand.IsBatch(@"C:\x\npm.CMD") && WindowsCommand.IsBatch("a.bat") && !WindowsCommand.IsBatch("git.exe"), "batch files are .cmd and .bat");
            var variables = WindowsCommand.Variables([new("Path", "p"), new("PathExt", ".EXE")]);
            Check(variables["PATH"] == "p" && variables["PATHEXT"] == ".EXE", "Windows names its variables in any case");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(root, true); }
    }

    internal static Task BatchFilesRunThroughCmdWithEveryArgumentQuoted()
    {
        var line = WindowsCommand.BatchCommandLine(@"C:\Program Files\nodejs\npm.cmd", ["install", "-g", "a&b|c<d>e^(f)", @"C:\dir\"]);
        Check(line == "\"C:\\Program Files\\nodejs\\npm.cmd\" \"install\" \"-g\" \"a&b|c<d>e^(f)\" \"C:\\dir\\\\\"",
            "each argument must stand in its own quotes, metacharacters kept inside them and a closing backslash doubled: " + line);
        foreach (var unsafeArgument in new[] { "a\"b", "%PATH%", "x!y!", "a\nb", "a\rb" })
        {
            var refused = false;
            try { WindowsCommand.BatchCommandLine("npm.cmd", [unsafeArgument]); }
            catch (ArgumentException) { refused = true; }
            Check(refused, "an argument cmd would expand or end early must be refused: " + unsafeArgument);
        }
        // A batch launch runs cmd.exe with the shim's own line, and neither it nor a direct launch looks in the working folder for a bare name.
        var variables = new Dictionary<string, string> { ["SystemRoot"] = @"D:\Win" };
        var (batch, shimLine) = WindowsCommand.Launch(@"C:\npm\claude.cmd", ["plugin", "list"], Path.GetTempPath(), null, variables);
        Check(batch.FileName == WindowsCommand.CommandInterpreter(variables) && batch.ArgumentList.Count == 0 && shimLine == "\"C:\\npm\\claude.cmd\" \"plugin\" \"list\""
            && batch.Environment[WindowsCommand.NoCurrentDirectorySearch] == "1", "a batch launch must go through cmd.exe with NoDefaultCurrentDirectoryInExePath set");
        var (direct, none) = WindowsCommand.Launch(@"C:\git\git.exe", ["--version"], Path.GetTempPath(), new Dictionary<string, string> { ["PATH"] = "x" });
        Check(direct.FileName == @"C:\git\git.exe" && none is null && direct.ArgumentList.SequenceEqual(["--version"]) && direct.Environment[WindowsCommand.NoCurrentDirectorySearch] == "1" && direct.Environment["PATH"] == "x",
            "a direct launch keeps its arguments and environment and sets NoDefaultCurrentDirectoryInExePath too");
        // On Windows the system folder Windows reports wins over any SystemRoot the environment claims; elsewhere (these checks on a Mac) SystemRoot as given.
        var expectedInterpreter = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : Path.Combine(@"D:\Win", "System32", "cmd.exe");
        Check(WindowsCommand.CommandInterpreter(new Dictionary<string, string> { ["SystemRoot"] = @"D:\Win" }) == expectedInterpreter, "cmd.exe comes from the system folder");
        return Task.CompletedTask;
    }

    /// The toolkit's executor resolves through CliRunner and fails a step that cannot start (MainWindow.Settings.cs CliToolkitExecutor).
    internal static Task ToolkitExecutorFailsAStepThatCannotStart()
    {
        var source = File.ReadAllText(Path.Combine(ClaudePluginVerification.WinUISource(), "MainWindow.Settings.cs"));
        Check(source.Contains("environment: CliEnvironment.Current()") && source.Contains("return ToolkitCommandOutput.Failure(ex.Message, -1);"),
            "the toolkit executor must use the PATH Windows keeps now and turn a launch failure into a failed step");
        var runner = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ClaudePluginVerification.WinUISource())!, "MightyClaude.Core", "CliRunner.cs"));
        Check(runner.Contains("WindowsCommand.Find(executable") && runner.Contains("WindowsCommand.Launch(resolved, arguments, cwd, env, lookup)"),
            "CliRunner must find bare names along the PATH and run a batch file through cmd.exe");
        return Task.CompletedTask;
    }

    internal static Task AnOldClaudeIsOutOfDateNotMissing()
    {
        static ProviderRuntime Provider(string id, bool available, string? version) => new(id, id, available, version, "", new ModelCatalog("cli", [], ""), new ProviderCapabilities(true, [], true, true, true));
        // ProviderCatalog counts a Claude below the Mods minimum unavailable; the row still says it needs the update.
        var rows = ComponentSection.SectionRows(new RuntimeInfo("win32", "1.0.0", true, "2.1.263", null, [Provider("claude", false, "2.1.263 (Claude Code)")], null));
        var claude = rows.First(row => row.Id == "claude");
        Check(claude.State == "attention" && claude.Actions is [{ Id: "update" }], "an old Claude must offer its update, not its install; got " + claude.State);
        var absent = ComponentSection.SectionRows(new RuntimeInfo("win32", "1.0.0", false, null, null, [Provider("claude", false, null)], null)).First(row => row.Id == "claude");
        Check(absent.State == "missing", "a Claude that is not there is missing");
        return Task.CompletedTask;
    }
}
