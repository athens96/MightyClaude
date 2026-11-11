using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// What the CLI accounts and updates read of OpenCode 2 (npm <c>@opencode/cli</c>, M/MightyCore/OpencodeSupport.swift): the sign-ins
/// <c>opencode auth list --format json --standalone</c> lists (integration names and the kind of connection only, never a credential), and
/// whether <c>opencode --version</c> names OpenCode 1 (npm <c>opencode-ai</c>), whose files, permissions and credentials differ and which the
/// setup screen replaces rather than updates.
/// </summary>
public static class OpencodeAccounts
{
    /// <summary>The first OpenCode 2 release: an older <c>opencode</c> is OpenCode 1.</summary>
    public const int MinimumMajor = OpencodeAgent.MinimumMajor;

    /// <summary>Whether <c>opencode --version</c> (<c>opencode v2.2.7</c>; OpenCode 1 printed <c>1.19.5</c>) names OpenCode 2.</summary>
    public static bool IsSupported(string version) => OpencodeAgent.IsSupported(version);

    /// <summary>The integrations with a live connection, sorted; whether one is a subscription (OAuth) sign-in; whether all are environment keys.</summary>
    public sealed record Listed(IReadOnlyList<string> Integrations, bool Subscription, bool EnvironmentOnly);

    /// <summary>
    /// <c>opencode auth list --format json</c>: <c>[{id, name, connections: [{type: "credential", method, status?} | {type: "env", name}]}]</c>.
    /// An integration counts with at least one connection whose <c>status.status</c> is not <c>needs_auth</c>. Only names, the connection's
    /// type and its method are read. Null for anything that is not that list (OpenCode 1 has no <c>--format</c>).
    /// </summary>
    public static Listed? Connections(string listJson)
    {
        try
        {
            using var document = JsonDocument.Parse(listJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            List<string> names = []; bool subscription = false, stored = false;
            foreach (var row in document.RootElement.EnumerateArray().Take(64))
            {
                if (MetadataJson.Property(row, "connections") is not { ValueKind: JsonValueKind.Array } connections) continue;
                var live = connections.EnumerateArray().Take(64)
                    .Where(connection => connection.ValueKind == JsonValueKind.Object && MetadataJson.Property(connection, "status").Text("status") != "needs_auth").ToList();
                if (live.Count == 0 || (row.Text("name") ?? row.Text("id")) is not { } name) continue;
                names.Add(CliAccountSupport.Clean(name));
                if (live.Any(connection => connection.Text("type") == "credential")) stored = true;
                if (live.Any(connection => connection.Text("type") == "credential" && connection.Text("method") == "oauth")) subscription = true;
            }
            names.Sort(StringComparer.Ordinal);
            return new Listed(names, subscription, names.Count > 0 && !stored);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// OpenCode 2's sign-ins are what <c>opencode auth list</c> lists: credentials in its database, keys in the environment
    /// (M/MightyCore/OpencodeSupport.swift opencodeStatus). With none it still runs opencode Zen's free models, which reads as signed out
    /// with that note: <c>opencode auth login</c> adds a provider. A failed, timed-out or unreadable list stays unknown; OpenCode 1
    /// (<paramref name="version"/>, read when the list is not JSON) asks for OpenCode 2. The app signs nothing out: the row says
    /// <c>opencode auth logout</c> in a terminal does.
    /// </summary>
    public static CliAccountStatus Status(int exitCode, string output, string? version = null, bool timedOut = false)
    {
        if (version is not null && !IsSupported(version))
            return new CliAccountStatus { Provider = "opencode", Detail = Locale.Get("settings.cliAccounts.detailOpencodeV1"), CanSignOut = false };
        if (exitCode != 0 || timedOut || Connections(output) is not { } listed)
            return new CliAccountStatus { Provider = "opencode", Detail = Locale.Get(timedOut ? "settings.cliAccounts.detailOpencodeTimeout" : "settings.cliAccounts.detailOpencodeUnknown"), CanSignOut = false };
        if (listed.Integrations.Count == 0)
            return new CliAccountStatus { Provider = "opencode", LoggedIn = false, Detail = Locale.Get("settings.cliAccounts.detailOpencodeFreeOnly"), CanSignOut = false };
        return new CliAccountStatus
        {
            Provider = "opencode", LoggedIn = true,
            Method = Locale.Get(listed.EnvironmentOnly ? "cliAccounts.method.opencodeEnvironmentKey" : listed.Subscription ? "cliAccounts.method.opencodeSubscription" : "windows.cli.method.apiKey"),
            MethodId = listed.Subscription ? CliAccountMethod.OpencodeSubscription : CliAccountMethod.OpencodeApiKey,
            Account = string.Join(", ", listed.Integrations.Take(16)),
            Detail = Locale.Get("settings.cliAccounts.detailOpencodeSignOut"), CanSignOut = false,
        };
    }
}
