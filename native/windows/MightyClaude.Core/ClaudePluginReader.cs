namespace MightyClaude.Core;

// Reads the workspace's Claude plugins through the installed Claude CLI's own
// plugin subcommands and nothing else:
//   claude --version
//   claude plugin list --json --available
//   claude plugin marketplace list --json
// All three run in the workspace folder through the shared one-shot runner
// (docs/windows-settings-groundwork.md). None of them changes anything: no
// install, no remove, no enable, no disable, no marketplace add or update.
//
// A missing CLI, a CLI too old for the subcommand, a timeout and malformed or
// oversized output each become a status with its macOS sentence, never an
// exception the screen has to catch.
public sealed class ClaudePluginReader
{
    /// macOS ClaudePluginService: readTimeout 20s, version probe min(4, read).
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(4);

    private readonly ICliRunner runner;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly Func<string, bool> isExecutable;
    private readonly Func<string, bool> directoryExists;
    private readonly Lock gate = new();
    private Task<ClaudePluginSnapshot>? running;
    private bool closed;

    /// The runner must be built with an output cap of at least
    /// ClaudePluginSupport.MaximumListingBytes; anything larger than the cap is
    /// refused by ParseSnapshot rather than silently truncated into a short list.
    public ClaudePluginReader(
        ICliRunner runner,
        IReadOnlyDictionary<string, string>? environment = null,
        Func<string, bool>? isExecutable = null,
        Func<string, bool>? directoryExists = null)
    {
        this.runner = runner;
        this.environment = Environment(environment ?? Runtime());
        this.isExecutable = isExecutable ?? File.Exists;
        this.directoryExists = directoryExists ?? Directory.Exists;
    }

    /// One read at a time. A second request while a read is running is answered
    /// from that running read instead of starting a second CLI process.
    public Task<ClaudePluginSnapshot> SnapshotAsync(Workspace workspace, CancellationToken cancellation = default)
    {
        if (workspace.Remote is not null)
            return Task.FromResult(new ClaudePluginSnapshot { Status = ClaudePluginStatus.Remote, Detail = PluginStrings.DetailRemote });
        lock (gate)
        {
            if (closed)
                return Task.FromResult(new ClaudePluginSnapshot { Status = ClaudePluginStatus.Cancelled, Detail = PluginStrings.DetailCancelled });
            if (running is { IsCompleted: false }) return running;
            running = ReadAsync(workspace, cancellation);
            return running;
        }
    }

    /// Closing the window stops admitting reads. Nothing is rolled back,
    /// because nothing was changed.
    public void Shutdown() { lock (gate) closed = true; }

    private sealed class PluginFailure(string status, string detail, string output = "") : Exception(detail)
    {
        internal string Status { get; } = status;
        internal string Detail { get; } = detail;
        internal string Output { get; } = output;
    }

    private async Task<ClaudePluginSnapshot> ReadAsync(Workspace workspace, CancellationToken cancellation)
    {
        try
        {
            var cwd = LocalDirectory(workspace);
            var (executable, version) = await CommandAsync(cwd, cancellation);
            var listing = await RunAsync(executable, ["plugin", "list", "--json", "--available"], cwd,
                PluginStrings.DetailListingFailed, cancellation);
            var marketplaces = await RunAsync(executable, ["plugin", "marketplace", "list", "--json"], cwd,
                PluginStrings.DetailMarketplacesFailed, cancellation);
            return ClaudePluginSupport.ParseSnapshot(listing.Output, marketplaces.Output, cwd, version);
        }
        catch (OperationCanceledException)
        {
            return new ClaudePluginSnapshot { Status = ClaudePluginStatus.Cancelled, Detail = PluginStrings.DetailCancelled };
        }
        catch (PluginFailure failure)
        {
            return new ClaudePluginSnapshot { Status = failure.Status, Detail = failure.Detail, DiagnosticOutput = failure.Output };
        }
        catch
        {
            return new ClaudePluginSnapshot { Status = ClaudePluginStatus.Failed, Detail = PluginStrings.DetailIncomplete };
        }
    }

    private async Task<CliRunResult> RunAsync(string executable, string[] arguments, string cwd, string failureDetail, CancellationToken cancellation)
    {
        CliRunResult result;
        try { result = await runner.RunAsync(executable, arguments, ReadTimeout, cancellation, environment, cwd); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailIncomplete, ClaudePluginSupport.Display(error.Message, ClaudePluginSupport.MessageCap)); }
        // A run that ran out of time is a slow CLI, not a short list.
        if (result.TimedOut) throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailIncomplete, ClaudePluginSupport.Output(result));
        if (result.ExitCode != 0) throw new PluginFailure(ClaudePluginStatus.Failed, failureDetail, ClaudePluginSupport.Output(result));
        return result;
    }

    private string LocalDirectory(Workspace workspace)
    {
        var path = workspace.Path;
        if (path.Length == 0 || path.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(path) > ClaudePluginSupport.PathCap || !Path.IsPathRooted(path))
            throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailInvalidWorkspace);
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailInvalidWorkspace); }
        if (!directoryExists(full)) throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailMissingWorkspace);
        return full;
    }

    /// The first claude on PATH that answers --version, gated on the minimum
    /// version the JSON plugin subcommands need.
    private async Task<(string Executable, string Version)> CommandAsync(string cwd, CancellationToken cancellation)
    {
        var present = false;
        foreach (var candidate in Candidates("claude"))
        {
            if (!isExecutable(candidate)) continue;
            present = true;
            CliRunResult result;
            try { result = await runner.RunAsync(candidate, ["--version"], VersionTimeout, cancellation, environment, cwd); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (result.TimedOut || result.ExitCode != 0) continue;
            var version = ClaudePluginSupport.Display(result.Output, ClaudePluginSupport.VersionCap).Trim();
            if (version.Length == 0) continue;
            if (!ClaudePluginSupport.SupportedVersion(version))
                throw new PluginFailure(ClaudePluginStatus.Unsupported, PluginStrings.DetailUnsupported);
            return (candidate, version);
        }
        throw present
            ? new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailUnknownVersion)
            : new PluginFailure(ClaudePluginStatus.Missing, PluginStrings.DetailMissingCli);
    }

    // PATH entries, at most 64, joined with the Windows executable extensions.
    // The bare name is kept last so a fixture without an extension is found.
    private IEnumerable<string> Candidates(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        environment.TryGetValue("PATH", out var path);
        foreach (var entry in (path ?? "").Split(Path.PathSeparator).Take(64))
        {
            if (entry.Length == 0 || entry.Contains('\0') || !Path.IsPathRooted(entry) || !seen.Add(entry)) continue;
            foreach (var extension in new[] { ".cmd", ".exe", ".bat", "" })
                yield return Path.Combine(entry, name + extension);
        }
    }

    private static Dictionary<string, string> Runtime()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) values[key] = value;
        return values;
    }

    // The same switches macOS sets: no auto-update, no telemetry, no background
    // work, no implicit marketplace install and no git credential prompt. A
    // caller-supplied FORCE_AUTOUPDATE_PLUGINS is dropped, so reading the list
    // can never update a plugin behind the user's back.
    internal static IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string> supplied)
    {
        var values = new Dictionary<string, string>(supplied, StringComparer.OrdinalIgnoreCase)
        {
            ["DISABLE_AUTOUPDATER"] = "1",
            ["DISABLE_TELEMETRY"] = "1",
            ["DISABLE_ERROR_REPORTING"] = "1",
            ["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] = "1",
            ["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1",
            ["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        values.Remove("FORCE_AUTOUPDATE_PLUGINS");
        return values;
    }
}
