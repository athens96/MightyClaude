using System.Text;

namespace MightyClaude.Core;

// Reads the workspace's Codex plugins through the installed Codex CLI's own
// plugin subcommands and its user-level registry, and nothing else:
//   codex --version
//   codex plugin list --help / plugin add --help
//   codex plugin marketplace list --help / plugin marketplace upgrade --help
//   codex plugin list --json --available
//   codex plugin marketplace list --json
// Every call runs in the workspace folder through the shared one-shot runner
// (docs/windows-settings-groundwork.md). None of them changes anything: no
// install, no remove, no enable, no disable, no marketplace add or upgrade.
//
// The version number is never used as a gate. This CLI feature is still moving,
// so — exactly as CodexPluginService.command does — the help output of each
// plugin subcommand is read first and the flags this build actually offers are
// confirmed before any of them is used. A build that is missing one becomes the
// macOS "unsupported" sentence instead of a confusing parse failure.
//
// A missing CLI, a CLI without the JSON plugin commands, a timeout and
// malformed or oversized output each become a status with its macOS sentence,
// never an exception the screen has to catch.
public sealed class CodexPluginReader : IPluginReader
{
    /// macOS CodexPluginService: readTimeout 20s, probes at min(4, read).
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// The four help probes and the flags each answer must name, in the order
    /// CodexPluginService.command runs them.
    public static readonly (string[] Arguments, string[] Flags)[] Capabilities =
    [
        (["plugin", "list", "--help"], ["--json", "--available"]),
        (["plugin", "add", "--help"], ["--json"]),
        (["plugin", "marketplace", "list", "--help"], ["--json"]),
        (["plugin", "marketplace", "upgrade", "--help"], ["--json"]),
    ];

    private readonly ICliRunner runner;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly Func<string, bool> isExecutable;
    private readonly Func<string, bool> directoryExists;
    private readonly Lock gate = new();
    private Task<ClaudePluginSnapshot>? running;
    private bool closed;
    private bool operating;

    /// The runner must be built with an output cap of at least
    /// ClaudePluginSupport.MaximumListingBytes; anything larger than the cap is
    /// refused by ParseCodexSnapshot rather than truncated into a short list.
    public CodexPluginReader(
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

    /// macOS CodexPluginService: the operation timeout is 180 seconds.
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(180);

    /// codex plugin add <id> --json. Codex installs at user level only, so any
    /// other scope is refused before a snapshot is read.
    public Task<ClaudePluginOperationResult> InstallAsync(string pluginId, string scope, Workspace workspace, CancellationToken cancellation = default)
    {
        if (ClaudePluginSupport.PluginParts(pluginId) is null || scope != "user")
            return Refused(workspace, PluginStrings.InstallBadIdOrScope);
        return OperateAsync(pluginId, null, workspace, cancellation);
    }

    /// codex plugin marketplace upgrade <name> --json, for a registered Git source.
    public Task<ClaudePluginOperationResult> RefreshMarketplaceAsync(string marketplace, Workspace workspace, CancellationToken cancellation = default)
    {
        if (!ClaudePluginSupport.Identifier(marketplace))
            return Refused(workspace, PluginStrings.MarketplaceBadName);
        return OperateAsync(null, marketplace, workspace, cancellation);
    }

    private Task<ClaudePluginOperationResult> Refused(Workspace workspace, string detail) =>
        Task.FromResult(workspace.Remote is not null
            ? new ClaudePluginOperationResult(ClaudePluginStatus.Remote, PluginStrings.DetailRemote)
            : new ClaudePluginOperationResult(ClaudePluginStatus.Failed, detail));

    private Task<ClaudePluginOperationResult> OperateAsync(
        string? pluginId, string? marketplace, Workspace workspace, CancellationToken cancellation)
    {
        if (workspace.Remote is not null)
            return Task.FromResult(new ClaudePluginOperationResult(ClaudePluginStatus.Remote, PluginStrings.DetailRemote));
        lock (gate)
        {
            if (closed)
                return Task.FromResult(new ClaudePluginOperationResult(ClaudePluginStatus.Cancelled, PluginStrings.OperationCancelled));
            if (operating)
                return Task.FromResult(new ClaudePluginOperationResult(ClaudePluginStatus.Busy, PluginStrings.OperationBusy));
            operating = true;
        }
        return RunOperationAsync(pluginId, marketplace, workspace, cancellation);
    }

    private async Task<ClaudePluginOperationResult> RunOperationAsync(
        string? pluginId, string? marketplace, Workspace workspace, CancellationToken cancellation)
    {
        try
        {
            var cwd = LocalDirectory(workspace);
            var (executable, version) = await CommandAsync(cwd, cancellation);
            var snapshot = await ListAsync(executable, cwd, version, cancellation);
            if (snapshot.Status != ClaudePluginStatus.Ready)
                return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, snapshot.Detail, snapshot.DiagnosticOutput);
            cancellation.ThrowIfCancellationRequested();

            string[] arguments;
            if (pluginId is not null)
            {
                if (snapshot.Installed.Any(p => p.PluginId == pluginId))
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Skipped, CodexPluginStrings.InstallSkipped);
                if (!snapshot.Available.Any(p => p.Id == pluginId))
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, CodexPluginStrings.InstallNotFound);
                arguments = ["plugin", "add", pluginId, "--json"];
            }
            else
            {
                if (snapshot.Marketplaces.FirstOrDefault(m => m.Name == marketplace) is not { } selected)
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.MarketplaceNotRegistered);
                if (selected.SourceKind != "git")
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Skipped, CodexPluginStrings.MarketplaceNotGit);
                arguments = ["plugin", "marketplace", "upgrade", marketplace!, "--json"];
            }

            CliRunResult result;
            try { result = await runner.RunAsync(executable, arguments, OperationTimeout, cancellation, environment, cwd); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.DetailIncomplete,
                    ClaudePluginSupport.Display(error.Message, ClaudePluginSupport.MessageCap));
            }
            cancellation.ThrowIfCancellationRequested();
            var output = ClaudePluginSupport.Output(result);
            if (result.TimedOut)
                return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.DetailIncomplete, output);

            System.Text.Json.JsonDocument document;
            try { document = System.Text.Json.JsonDocument.Parse(result.Output); }
            catch { return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, CodexPluginStrings.OperationFailed, output); }
            using (document)
            {
                var root = document.RootElement;
                if (result.ExitCode != 0 || root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, CodexPluginStrings.OperationFailed, output);

                if (pluginId is not null)
                {
                    var parts = ClaudePluginSupport.PluginParts(pluginId)!.Value;
                    // OS-bound substitution, recorded in docs/windows-plugins.md:
                    // macOS asks installedPath to start with "/", Windows asks
                    // Path.IsPathRooted, the same "an absolute path" question.
                    var installedPath = root.Text("installedPath");
                    if (root.Text("pluginId") != pluginId || root.Text("name") != parts.Name
                        || root.Text("marketplaceName") != parts.Marketplace
                        || installedPath is null || installedPath.Contains('\0') || !Path.IsPathRooted(installedPath))
                        return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.InstallUnconfirmed, output);
                    var verified = await ListAsync(executable, cwd, version, cancellation);
                    if (!verified.Installed.Any(p => p.PluginId == pluginId))
                        return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, CodexPluginStrings.InstallVerifyFailed, output);
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Succeeded, CodexPluginStrings.InstallSucceeded, output);
                }

                if (!Names(root, "selectedMarketplaces").SequenceEqual([marketplace!])
                    || !root.TryGetProperty("upgradedRoots", out var roots) || roots.ValueKind != System.Text.Json.JsonValueKind.Array
                    || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != System.Text.Json.JsonValueKind.Array
                    || errors.GetArrayLength() != 0)
                    return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, CodexPluginStrings.MarketplaceRefreshUnconfirmed, output);
                return new ClaudePluginOperationResult(ClaudePluginStatus.Succeeded, PluginStrings.MarketplaceRefreshSucceeded, output);
            }
        }
        catch (OperationCanceledException)
        {
            return new ClaudePluginOperationResult(ClaudePluginStatus.Cancelled, PluginStrings.OperationCancelled);
        }
        catch (PluginFailure failure)
        {
            return new ClaudePluginOperationResult(
                failure.Status == ClaudePluginStatus.Cancelled ? ClaudePluginStatus.Cancelled : ClaudePluginStatus.Failed,
                failure.Detail, failure.Output);
        }
        catch
        {
            return new ClaudePluginOperationResult(ClaudePluginStatus.Failed, PluginStrings.DetailIncomplete);
        }
        finally { lock (gate) operating = false; }
    }

    private static IReadOnlyList<string> Names(System.Text.Json.JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == System.Text.Json.JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

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
            return await ListAsync(executable, cwd, version, cancellation);
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

    /// The two read subcommands and the parse, shared by the list and by the
    /// re-read an install or a marketplace upgrade does before it assembles
    /// its arguments.
    private async Task<ClaudePluginSnapshot> ListAsync(string executable, string cwd, string? version, CancellationToken cancellation)
    {
        var listing = await RunAsync(executable, ["plugin", "list", "--json", "--available"], cwd,
            CodexPluginStrings.DetailListingFailed, cancellation);
        var marketplaces = await RunAsync(executable, ["plugin", "marketplace", "list", "--json"], cwd,
            PluginStrings.DetailMarketplacesFailed, cancellation);
        var snapshot = ClaudePluginSupport.ParseCodexSnapshot(listing.Output, marketplaces.Output, cwd, version);
        // A CLI that still answered but warned on stderr: keep the warning
        // and say the list may be stale, as macOS readSnapshot does.
        var warnings = ClaudePluginSupport.Display((listing.ErrorOutput + "\n" + marketplaces.ErrorOutput).Trim(), ClaudePluginSupport.OutputCap);
        if (warnings.Length == 0 || snapshot.Status != ClaudePluginStatus.Ready) return snapshot;
        return snapshot with { DiagnosticOutput = warnings, Detail = snapshot.Detail + CodexPluginStrings.DetailWarningSuffix };
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
        if (path.Length == 0 || path.Contains('\0') || Encoding.UTF8.GetByteCount(path) > ClaudePluginSupport.PathCap || !Path.IsPathRooted(path))
            throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailInvalidWorkspace);
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailInvalidWorkspace); }
        if (!directoryExists(full)) throw new PluginFailure(ClaudePluginStatus.Failed, PluginStrings.DetailMissingWorkspace);
        return full;
    }

    /// The first codex on PATH that answers --version, then the capability
    /// probe. The version is shown in the header only; it never gates anything,
    /// because this CLI feature is still moving and the help output is the
    /// honest answer to "does this build support it".
    private async Task<(string Executable, string Version)> CommandAsync(string cwd, CancellationToken cancellation)
    {
        var present = false;
        foreach (var candidate in Candidates("codex"))
        {
            if (!isExecutable(candidate)) continue;
            present = true;
            CliRunResult result;
            try { result = await runner.RunAsync(candidate, ["--version"], ProbeTimeout, cancellation, environment, cwd); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (result.TimedOut || result.ExitCode != 0) continue;
            var version = ClaudePluginSupport.Display(result.Output, ClaudePluginSupport.VersionCap).Trim();
            if (version.Length == 0) continue;
            await ProbeAsync(candidate, cwd, cancellation);
            return (candidate, version);
        }
        throw present
            ? new PluginFailure(ClaudePluginStatus.Failed, CodexPluginStrings.DetailUnknownVersion)
            : new PluginFailure(ClaudePluginStatus.Missing, CodexPluginStrings.DetailMissingCli);
    }

    /// Asks the CLI itself which plugin subcommands and flags it has. Reading
    /// --help changes nothing. A probe that fails, times out or does not name
    /// every flag this screen would use is the macOS "unsupported" sentence.
    private async Task ProbeAsync(string executable, string cwd, CancellationToken cancellation)
    {
        foreach (var (arguments, flags) in Capabilities)
        {
            CliRunResult probe;
            try { probe = await runner.RunAsync(executable, arguments, ProbeTimeout, cancellation, environment, cwd); }
            catch (OperationCanceledException) { throw; }
            catch { throw new PluginFailure(ClaudePluginStatus.Unsupported, CodexPluginStrings.DetailUnsupported); }
            if (probe.TimedOut || probe.ExitCode != 0 || !flags.All(flag => probe.Output.Contains(flag, StringComparison.Ordinal)))
                throw new PluginFailure(ClaudePluginStatus.Unsupported, CodexPluginStrings.DetailUnsupported, ClaudePluginSupport.Output(probe));
        }
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

    // The one switch macOS CodexPluginService sets: git must never stop and ask
    // for a password while a read is running. Nothing else is forced, so the
    // user's own CODEX_HOME still decides which user-level registry is read.
    internal static IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string> supplied) =>
        new Dictionary<string, string>(supplied, StringComparer.OrdinalIgnoreCase) { ["GIT_TERMINAL_PROMPT"] = "0" };
}
