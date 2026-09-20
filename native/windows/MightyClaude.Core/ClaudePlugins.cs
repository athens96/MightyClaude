using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

// The Claude plugin list of a workspace, read through the installed Claude
// CLI's own plugin subcommands. Reading only: nothing here installs, removes,
// enables, disables or updates a plugin, and nothing adds or refreshes a
// marketplace. Mirrors ClaudePluginService.swift / ClaudePluginModels.swift.
//
// The model types are provider-neutral on purpose: the Codex plugin feature and
// the marketplace feature reuse them unchanged. Their shape is written down in
// docs/windows-plugins.md.

/// Common interface for the Claude and Codex plugin readers. Both read a
/// snapshot of the workspace's installed and available plugins and nothing else.
public interface IPluginReader
{
    Task<ClaudePluginSnapshot> SnapshotAsync(Workspace workspace, CancellationToken cancellation = default);
    void Shutdown();

    /// The two mutations macOS has, and no others: install one available plugin
    /// and refresh one registered marketplace. Both assemble their argument list
    /// out of values the snapshot just returned, run it through the shared
    /// one-shot runner and interpret the answer. There is no uninstall, enable,
    /// disable or marketplace add, because the macOS app has none.
    Task<ClaudePluginOperationResult> InstallAsync(string pluginId, string scope, Workspace workspace, CancellationToken cancellation = default);
    Task<ClaudePluginOperationResult> RefreshMarketplaceAsync(string marketplace, Workspace workspace, CancellationToken cancellation = default);
}

/// CLI settings state for this working directory, not proof that an already
/// running CLI process has loaded the plugin successfully.
public sealed record ClaudeInstalledPlugin
{
    public string PluginId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Marketplace { get; init; }
    public string? Version { get; init; }
    public string Scope { get; init; } = "user";
    public bool? Enabled { get; init; }
    public string? ProjectPath { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// Length-prefixed so no name, scope or path can forge another row's key.
    [JsonIgnore]
    public string Id => string.Join("|", new[] { PluginId, Scope, ProjectPath ?? "" }
        .Select(value => Encoding.UTF8.GetByteCount(value) + ":" + value));
}

public sealed record ClaudeCatalogPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Marketplace { get; init; } = "";
    public string? Version { get; init; }
    public string SourceKind { get; init; } = "unknown";
}

public sealed record ClaudePluginMarketplace(string Name, string SourceKind = "unknown");

public sealed record ClaudePluginSnapshot
{
    /// ready, missing, unsupported, failed, cancelled or remote.
    public string Status { get; init; } = ClaudePluginStatus.Failed;
    public string Detail { get; init; } = "";
    public string? CliVersion { get; init; }
    public IReadOnlyList<ClaudeInstalledPlugin> Installed { get; init; } = [];
    public IReadOnlyList<ClaudeCatalogPlugin> Available { get; init; } = [];
    public IReadOnlyList<ClaudePluginMarketplace> Marketplaces { get; init; } = [];
    public string? UpdatedAt { get; init; }
    /// Bounded CLI output. Only shown behind the diagnostics disclosure.
    public string DiagnosticOutput { get; init; } = "";
}

/// What an install or a marketplace refresh reports: the macOS status word, the
/// macOS sentence and the bounded CLI output that is only shown on request.
public sealed record ClaudePluginOperationResult(string Status, string Detail, string Output = "");

public static class ClaudePluginStatus
{
    public const string Ready = "ready";
    public const string Missing = "missing";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Remote = "remote";
    // Operation results add three words of their own (ClaudePluginService.swift).
    public const string Succeeded = "succeeded";
    public const string Skipped = "skipped";
    public const string Busy = "busy";
}

/// Parsing and shaping that depend only on data — no process, no file system.
/// Core.Tests hands these fixture JSON directly.
public static partial class ClaudePluginSupport
{
    public const int MaximumListingBytes = 8 * 1024 * 1024;
    public const int MaximumMarketplaceBytes = 512 * 1024;
    public const int MaximumRows = 10_000;
    public const int MaximumMarketplaceRows = 256;
    public const int DescriptionCap = 4096;
    public const int VersionCap = 160;
    public const int MessageCap = 1024;
    public const int OutputCap = 16_384;
    public const int PathCap = 16_384;

    private static readonly string[] KnownScopes = ["user", "project", "local", "managed", "session"];
    private static readonly string[] KnownSourceKinds =
        ["github", "git", "git-subdir", "npm", "url", "directory", "file", "command", "zip"];

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\A([0-9]+)\.([0-9]+)\.([0-9]+)(\s|$)")]
    private static partial Regex VersionPattern();

    public static bool Identifier(string? value) =>
        value is { Length: > 0 } && Encoding.UTF8.GetByteCount(value) <= 128 && IdentifierPattern().IsMatch(value);

    /// The automation id of one control in the plugin window.
    public static string AutomationId(string provider, string part) => provider + "-plugin-" + part;

    /// A change this read-only window must never offer. The id's action part is
    /// read one dash-separated word at a time, so the tab that lists installed
    /// plugins ("tab-installed") is not mistaken for an install button and the
    /// list reread ("reload") is not mistaken for a marketplace refresh.
    // "upgrade" is the word the Codex marketplace feature will use
    // (codex plugin marketplace upgrade), so it names a change here too.
    private static readonly string[] ChangingWords =
        ["install", "uninstall", "enable", "disable", "update", "upgrade", "scope", "refresh", "remove", "add"];

    public static bool NamesAChange(string automationId, string provider)
    {
        var prefix = AutomationId(provider, "");
        if (!automationId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return automationId[prefix.Length..].Split('-').Any(ChangingWords.Contains);
    }

    /// "name@marketplace" split, both halves validated as identifiers.
    public static (string Name, string Marketplace)? PluginParts(string? value)
    {
        if (value is null) return null;
        var parts = value.Split('@');
        if (parts.Length != 2 || !Identifier(parts[0]) || !Identifier(parts[1])) return null;
        return (parts[0], parts[1]);
    }

    /// The CLI's minimum supported version for the JSON plugin subcommands.
    public static bool SupportedVersion(string versionText)
    {
        var match = VersionPattern().Match(versionText.Trim());
        if (!match.Success) return false;
        int[] found = [int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)];
        int[] minimum = [2, 1, 268];
        for (var i = 0; i < 3; i++)
        {
            if (found[i] > minimum[i]) return true;
            if (found[i] < minimum[i]) return false;
        }
        return true;
    }

    /// Drops control characters and bounds the length, exactly as macOS display().
    public static string Display(string? value, int limit)
    {
        if (value is null) return "";
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsControl(character) && character is not ('\n' or '\t')) continue;
            builder.Append(character);
            if (builder.Length >= limit) break;
        }
        return builder.ToString();
    }

    /// A project/local record applies when the working folder is that project
    /// or sits inside it. A record from another project is never shown here.
    public static bool Applies(string projectPath, string workingDirectory)
    {
        if (projectPath.Length == 0 || projectPath.Contains('\0') || Encoding.UTF8.GetByteCount(projectPath) > PathCap) return false;
        if (Normalize(projectPath) is not { } root || Normalize(workingDirectory) is not { } cwd) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (cwd.Equals(root, comparison)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return cwd.StartsWith(prefix, comparison);
    }

    private static string? Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return null; }
    }

    public static string SourceKind(JsonElement value)
    {
        var kind = value.ValueKind switch
        {
            JsonValueKind.Object => value.TryGetProperty("source", out var inner) && inner.ValueKind == JsonValueKind.String
                ? inner.GetString() ?? "unknown" : "unknown",
            JsonValueKind.String => value.GetString() ?? "unknown",
            _ => "unknown",
        };
        if (KnownSourceKinds.Contains(kind)) return kind;
        return kind.StartsWith("./", StringComparison.Ordinal) || kind.StartsWith('/') ? "directory" : "unknown";
    }

    private static IReadOnlyList<string> Messages(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Take(16).Select(v => Display(v.GetString(), MessageCap))];
    }

    /// Both CLI answers into one snapshot. Malformed or oversized output is a
    /// failed status, never a ready-but-empty list.
    public static ClaudePluginSnapshot ParseSnapshot(string listing, string marketplaces, string workingDirectory, string? cliVersion)
    {
        ClaudePluginSnapshot Malformed() => new()
        {
            Status = ClaudePluginStatus.Failed,
            Detail = PluginStrings.DetailMalformed,
            CliVersion = cliVersion,
        };

        if (Encoding.UTF8.GetByteCount(listing) > MaximumListingBytes) return Malformed();
        if (Encoding.UTF8.GetByteCount(marketplaces) > MaximumMarketplaceBytes) return Malformed();

        JsonDocument listingDocument, marketDocument;
        try { listingDocument = JsonDocument.Parse(listing); } catch { return Malformed(); }
        using (listingDocument)
        {
            try { marketDocument = JsonDocument.Parse(marketplaces); } catch { return Malformed(); }
            using (marketDocument)
            {
                var root = listingDocument.RootElement;
                var markets = marketDocument.RootElement;
                if (root.ValueKind != JsonValueKind.Object || markets.ValueKind != JsonValueKind.Array) return Malformed();
                if (!root.TryGetProperty("installed", out var installedRows) || installedRows.ValueKind != JsonValueKind.Array) return Malformed();
                if (!root.TryGetProperty("available", out var availableRows) || availableRows.ValueKind != JsonValueKind.Array) return Malformed();
                if (installedRows.GetArrayLength() > MaximumRows || availableRows.GetArrayLength() > MaximumRows) return Malformed();
                if (markets.GetArrayLength() > MaximumMarketplaceRows) return Malformed();

                var marketNames = new HashSet<string>(StringComparer.Ordinal);
                var marketplaceList = new List<ClaudePluginMarketplace>();
                foreach (var row in markets.EnumerateArray())
                {
                    var name = row.Text("name");
                    if (!Identifier(name) || !marketNames.Add(name!)) continue;
                    marketplaceList.Add(new ClaudePluginMarketplace(name!, SourceKind(Property(row, "source"))));
                }
                marketplaceList.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                var catalogIds = new HashSet<string>(StringComparer.Ordinal);
                var available = new List<ClaudeCatalogPlugin>();
                foreach (var row in availableRows.EnumerateArray())
                {
                    var id = row.Text("pluginId");
                    if (PluginParts(id) is not { } parts) continue;
                    if (row.Text("name") != parts.Name || row.Text("marketplaceName") != parts.Marketplace) continue;
                    if (!marketNames.Contains(parts.Marketplace) || !catalogIds.Add(id!)) continue;
                    available.Add(new ClaudeCatalogPlugin
                    {
                        Id = id!,
                        Name = parts.Name,
                        Description = Display(row.Text("description"), DescriptionCap),
                        Marketplace = parts.Marketplace,
                        Version = row.Text("version") is { Length: > 0 } v ? Display(v, VersionCap) : null,
                        SourceKind = SourceKind(Property(row, "source")),
                    });
                }
                available.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var catalog = available.ToDictionary(p => p.Id, StringComparer.Ordinal);

                var installedIds = new HashSet<string>(StringComparer.Ordinal);
                var installed = new List<ClaudeInstalledPlugin>();
                foreach (var row in installedRows.EnumerateArray())
                {
                    var id = row.Text("id");
                    if (PluginParts(id) is not { } parts) continue;
                    var scope = row.Text("scope");
                    if (scope is null || !KnownScopes.Contains(scope)) continue;
                    var projectPath = row.Text("projectPath");
                    if (scope is "project" or "local" && (projectPath is null || !Applies(projectPath, workingDirectory))) continue;
                    var description = Display(row.Text("description"), DescriptionCap);
                    if (description.Length == 0 && catalog.TryGetValue(id!, out var known)) description = known.Description;
                    var value = new ClaudeInstalledPlugin
                    {
                        PluginId = id!,
                        Name = parts.Name,
                        Marketplace = parts.Marketplace,
                        Version = row.Text("version") is { Length: > 0 } v ? Display(v, VersionCap) : null,
                        Scope = scope,
                        Enabled = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("enabled", out var enabled)
                            && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled.GetBoolean() : null,
                        ProjectPath = projectPath is { Length: > 0 } ? projectPath[..Math.Min(projectPath.Length, PathCap)] : null,
                        Description = description,
                        Errors = Messages(row, "errors"),
                        Notes = Messages(row, "notes"),
                    };
                    if (installedIds.Add(value.Id)) installed.Add(value);
                }
                installed.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

                return new ClaudePluginSnapshot
                {
                    Status = ClaudePluginStatus.Ready,
                    Detail = marketplaceList.Count == 0 ? PluginStrings.DetailNoMarketplaces : PluginStrings.DetailReady,
                    CliVersion = cliVersion,
                    Installed = installed,
                    Available = available,
                    Marketplaces = marketplaceList,
                    UpdatedAt = Wire.Now(),
                };
            }
        }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// Codex marketplace JSON has {"marketplaces":[...]} wrapper and uses
    /// marketplaceSource.sourceType for source kind. Available rows are
    /// filtered by installPolicy; installed rows must have "installed":true and
    /// are always user-scope. Mirrors CodexPluginService.parseSnapshot.
    public static ClaudePluginSnapshot ParseCodexSnapshot(string listing, string marketplacesJson, string workingDirectory, string? cliVersion)
    {
        ClaudePluginSnapshot Malformed() => new()
        {
            Status = ClaudePluginStatus.Failed,
            Detail = PluginStrings.DetailMalformed,
            CliVersion = cliVersion,
        };

        if (Encoding.UTF8.GetByteCount(listing) > MaximumListingBytes) return Malformed();
        if (Encoding.UTF8.GetByteCount(marketplacesJson) > MaximumMarketplaceBytes) return Malformed();

        JsonDocument listingDocument, marketDocument;
        try { listingDocument = JsonDocument.Parse(listing); } catch { return Malformed(); }
        using (listingDocument)
        {
            try { marketDocument = JsonDocument.Parse(marketplacesJson); } catch { return Malformed(); }
            using (marketDocument)
            {
                var root = listingDocument.RootElement;
                var marketsRoot = marketDocument.RootElement;
                if (root.ValueKind != JsonValueKind.Object || marketsRoot.ValueKind != JsonValueKind.Object) return Malformed();
                if (!marketsRoot.TryGetProperty("marketplaces", out var marketRows) || marketRows.ValueKind != JsonValueKind.Array) return Malformed();
                if (!root.TryGetProperty("installed", out var installedRows) || installedRows.ValueKind != JsonValueKind.Array) return Malformed();
                if (!root.TryGetProperty("available", out var availableRows) || availableRows.ValueKind != JsonValueKind.Array) return Malformed();
                if (installedRows.GetArrayLength() > MaximumRows || availableRows.GetArrayLength() > MaximumRows) return Malformed();
                if (marketRows.GetArrayLength() > MaximumMarketplaceRows) return Malformed();

                var marketNames = new HashSet<string>(StringComparer.Ordinal);
                var marketplaceList = new List<ClaudePluginMarketplace>();
                foreach (var row in marketRows.EnumerateArray())
                {
                    var name = row.Text("name");
                    if (!Identifier(name) || !marketNames.Add(name!)) return Malformed();
                    marketplaceList.Add(new ClaudePluginMarketplace(name!, CodexSourceKind(Property(row, "marketplaceSource"), Property(row, "source"))));
                }
                marketplaceList.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                // Available rows: must have valid identity (pluginId/name/marketplaceName/installed/enabled);
                // policy != AVAILABLE/INSTALLED_BY_DEFAULT counts as restricted and is excluded.
                var catalogIds = new HashSet<string>(StringComparer.Ordinal);
                var available = new List<ClaudeCatalogPlugin>();
                var restricted = 0;
                foreach (var row in availableRows.EnumerateArray())
                {
                    var id = row.Text("pluginId");
                    if (PluginParts(id) is not { } parts) return Malformed();
                    if (row.Text("name") != parts.Name || row.Text("marketplaceName") != parts.Marketplace) return Malformed();
                    if (row.ValueKind != JsonValueKind.Object ||
                        !row.TryGetProperty("installed", out var instBool) || instBool.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        !row.TryGetProperty("enabled", out var enaBool) || enaBool.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Malformed();
                    if (!catalogIds.Add(id!)) return Malformed();
                    var policy = row.Text("installPolicy");
                    if (policy is not ("AVAILABLE" or "INSTALLED_BY_DEFAULT")) { restricted++; continue; }
                    available.Add(new ClaudeCatalogPlugin
                    {
                        Id = id!,
                        Name = parts.Name,
                        Description = Display(row.Text("description"), DescriptionCap),
                        Marketplace = parts.Marketplace,
                        Version = row.Text("version") is { Length: > 0 } v ? Display(v, VersionCap) : null,
                        SourceKind = CodexSourceKind(Property(row, "source")),
                    });
                }
                available.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var catalog = available.ToDictionary(p => p.Id, StringComparer.Ordinal);

                // Installed rows: must have "installed":true. Scope is always user.
                var installedIds = new HashSet<string>(StringComparer.Ordinal);
                var installed = new List<ClaudeInstalledPlugin>();
                foreach (var row in installedRows.EnumerateArray())
                {
                    var id = row.Text("pluginId");
                    if (PluginParts(id) is not { } parts) return Malformed();
                    if (row.Text("name") != parts.Name || row.Text("marketplaceName") != parts.Marketplace) return Malformed();
                    if (row.ValueKind != JsonValueKind.Object ||
                        !row.TryGetProperty("installed", out var instBool) || instBool.ValueKind != JsonValueKind.True)
                        return Malformed();
                    if (!installedIds.Add(id!)) return Malformed();
                    var description = Display(row.Text("description"), DescriptionCap);
                    if (description.Length == 0 && catalog.TryGetValue(id!, out var known)) description = known.Description;
                    installed.Add(new ClaudeInstalledPlugin
                    {
                        PluginId = id!,
                        Name = parts.Name,
                        Marketplace = parts.Marketplace,
                        Version = row.Text("version") is { Length: > 0 } v ? Display(v, VersionCap) : null,
                        Scope = "user",
                        Enabled = row.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled.GetBoolean() : null,
                        Description = description,
                        Errors = Messages(row, "errors"),
                        Notes = Messages(row, "notes"),
                    });
                }
                installed.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

                var detail = marketplaceList.Count == 0
                    ? CodexPluginStrings.DetailNoMarketplaces
                    : CodexPluginStrings.DetailReady;
                if (restricted > 0)
                    detail += CodexPluginStrings.DetailRestrictedSuffix.Replace("{count}", restricted.ToString());

                return new ClaudePluginSnapshot
                {
                    Status = ClaudePluginStatus.Ready,
                    Detail = detail,
                    CliVersion = cliVersion,
                    Installed = installed,
                    Available = available,
                    Marketplaces = marketplaceList,
                    UpdatedAt = Wire.Now(),
                };
            }
        }
    }

    // Codex source kinds. CodexPluginService.sourceKind reads "sourceType"
    // first, then "source", and accepts its own shorter whitelist: a Codex
    // marketplace registered from a folder reports "local", the curated remote
    // catalog reports "remote". A path becomes "directory"; anything else is
    // "unknown". The Claude whitelist is left alone.
    private static readonly string[] CodexSourceKinds = ["local", "remote", "github", "git", "directory"];

    private static string CodexSourceKind(JsonElement value, JsonElement fallback = default)
    {
        var element = value.ValueKind == JsonValueKind.Object ? value : fallback;
        if (element.ValueKind != JsonValueKind.Object) return "unknown";
        var kind = element.TryGetProperty("sourceType", out var sourceType) && sourceType.ValueKind == JsonValueKind.String
            ? sourceType.GetString() ?? "unknown"
            : element.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                ? source.GetString() ?? "unknown"
                : "unknown";
        if (CodexSourceKinds.Contains(kind)) return kind;
        return kind.StartsWith("./", StringComparison.Ordinal) || kind.StartsWith('/') ? "directory" : "unknown";
    }

    /// The bounded stdout+stderr kept for the diagnostics disclosure.
    public static string Output(CliRunResult result) =>
        Display(result.Output + "\n" + result.ErrorOutput, OutputCap);
}
