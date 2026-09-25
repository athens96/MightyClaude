using ToolkitFileEntry = MightyClaude.Core.ToolkitFileReader.ToolkitFileEntry;

namespace MightyClaude.Core;

// ── Plan item ────────────────────────────────────────────────────────────────

public sealed class ToolkitPlanItem
{
    public enum PlanAction { Run, Skip }

    public required ToolkitFileEntry Entry { get; init; }
    public required PlanAction Action { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Commands { get; init; }
}

// ── Run result ───────────────────────────────────────────────────────────────

public sealed class ToolkitRunItem
{
    public enum Verdict { Installed, Failed, Skipped }

    public required string EntryId { get; init; }
    public required Verdict RunVerdict { get; init; }
}

// ── Executor ─────────────────────────────────────────────────────────────────

public sealed class ToolkitCommandOutput(int exitCode = 0, string output = "")
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
    public static ToolkitCommandOutput Success { get; } = new(0);
    public static ToolkitCommandOutput Failure(string output = "", int exitCode = 1) => new(exitCode, output);
}

public interface IToolkitRunnerExecutor
{
    ToolkitCommandOutput Run(IReadOnlyList<string> argv);
}

// ── Runner ───────────────────────────────────────────────────────────────────

/// Coordinates the "install missing tools" flow for the toolkit list.
/// Plan() re-probes every entry and returns only missing ones.
/// Verdict after Run() comes from re-probing, not exit codes.
public sealed class ToolkitRunner(ToolkitStore store, ToolkitProbeContext probeContext)
{
    // ── Plan ─────────────────────────────────────────────────────────────────

    /// Returns items for entries that are currently missing.
    /// Bundled and approved user entries get Run; unapproved user entries get Skip.
    public IReadOnlyList<ToolkitPlanItem> Plan()
    {
        var (entries, _) = store.List();
        var items = new List<ToolkitPlanItem>();
        foreach (var entry in entries)
        {
            var approval = store.GetApproval(entry);
            if (ToolkitProbe.Probe(entry, approval, probeContext) == ToolkitProbe.Result.Installed) continue;
            if (entry.Source == ToolkitFileReader.ToolkitEntrySource.User && approval is null)
            {
                items.Add(new ToolkitPlanItem { Entry = entry, Action = ToolkitPlanItem.PlanAction.Skip, Commands = [] });
            }
            else
            {
                var commands = InstallCommands(entry, approval, probeContext);
                if (commands.Count == 0) continue;
                items.Add(new ToolkitPlanItem { Entry = entry, Action = ToolkitPlanItem.PlanAction.Run, Commands = commands });
            }
        }
        return items;
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    /// Executes a plan. Items run in order; one failure does not stop the rest.
    /// Fetch steps are retried once when output matches a network-error pattern.
    /// Verdict comes from re-probing, not exit codes.
    public IReadOnlyList<ToolkitRunItem> Run(IReadOnlyList<ToolkitPlanItem> items, IToolkitRunnerExecutor executor)
    {
        var results = new List<ToolkitRunItem>();
        foreach (var item in items)
        {
            if (item.Action == ToolkitPlanItem.PlanAction.Skip)
            {
                results.Add(new ToolkitRunItem { EntryId = item.Entry.Id, RunVerdict = ToolkitRunItem.Verdict.Skipped });
                continue;
            }
            ExecuteEntry(item, executor);
            var approval = store.GetApproval(item.Entry);
            var probeResult = ToolkitProbe.Probe(item.Entry, approval, probeContext);
            results.Add(new ToolkitRunItem
            {
                EntryId = item.Entry.Id,
                RunVerdict = probeResult == ToolkitProbe.Result.Installed ? ToolkitRunItem.Verdict.Installed : ToolkitRunItem.Verdict.Failed,
            });
        }
        return results;
    }

    private void ExecuteEntry(ToolkitPlanItem item, IToolkitRunnerExecutor executor)
    {
        var commands = item.Commands;
        for (var i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            var isFetch = IsFetchStep(cmd);
            var result = executor.Run(cmd);
            if (result.ExitCode != 0 && isFetch && IsNetworkError(result.Output))
                result = executor.Run(cmd);
        }
    }

    // ── Command building ─────────────────────────────────────────────────────

    /// The one place that turns an entry into argv arrays.
    public static IReadOnlyList<IReadOnlyList<string>> InstallCommands(
        ToolkitFileEntry entry,
        ToolkitFileReader.ToolkitApproval? approval,
        ToolkitProbeContext ctx)
    {
        var home = ctx.HomeDirectory;
        return entry.Install switch
        {
            // plugin: marketplace add, then install with --json
            ToolkitFileReader.PluginSpec ps => [
                ["claude", "plugin", "marketplace", "add", "--scope", "user", ps.Source],
                ["claude", "plugin", "install", ps.PluginId, "--scope", "user", "--json"],
            ],
            // mcp: single command
            ToolkitFileReader.McpSpec ms => [
                [.. new[] { "claude", "mcp", "add", "--scope", "user", ms.Name, "--", ms.Executable }, .. ms.Args],
            ],
            // skill: git clone to %USERPROFILE%\.claude\skills\<name>
            ToolkitFileReader.SkillSpec ss => SkillCommands(ss.Url, home),
            // npm: global install
            ToolkitFileReader.PackageSpec { Manager: "npm" } ps => [
                ["npm", "install", "-g", ps.Name],
            ],
            // winget: exact install command from the constraint table
            ToolkitFileReader.PackageSpec { Manager: "winget" } ps => [
                ["winget", "install", "--exact", "--id", ps.Name,
                 "--source", "winget", "--scope", "user",
                 "--accept-source-agreements", "--accept-package-agreements",
                 "--disable-interactivity"],
            ],
            // brew and repoScript are macOS-only; never build commands for them on Windows.
            _ => [],
        };
    }

    private static IReadOnlyList<IReadOnlyList<string>> SkillCommands(string url, string homeDir)
    {
        var component = new Uri(url, UriKind.Absolute).Segments.LastOrDefault()?.TrimEnd('/') ?? "";
        if (component.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) component = component[..^4];
        if (component.Length == 0) return [];
        var dest = Path.Combine(homeDir, ".claude", "skills", component);
        return [["git", "clone", url, dest]];
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    /// Returns true for fetch commands eligible for network-error retry.
    public static bool IsFetchStep(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0) return false;
        return argv[0] switch
        {
            "git" => argv.Count > 1 && argv[1] is "clone" or "ls-remote",
            "npm" => argv.Count > 1 && argv[1] == "install",
            "claude" when argv.Count > 2 && argv[1] == "plugin" && argv[2] is "marketplace" => true,
            "claude" when argv.Count > 2 && argv[1] == "plugin" && argv[2] == "install" => true,
            _ => false,
        };
    }

    /// Returns true when the output string matches a known network-failure pattern.
    public static bool IsNetworkError(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("could not resolve host") ||
               lower.Contains("connection refused") ||
               lower.Contains("network is unreachable") ||
               lower.Contains("could not connect") ||
               lower.Contains("timed out") ||
               lower.Contains("ssl handshake") ||
               lower.Contains("could not download") ||
               lower.Contains("network error") ||
               lower.Contains("failed to connect") ||
               lower.Contains("no route to host");
    }
}
