namespace MightyClaude.Core;

/// <summary>
/// The status line trust rule, identical to macOS AppStore+StatusLine.swift: a
/// <c>statusLine</c> that comes from the workspace runs only after the user allows it for
/// that workspace, the stored value is the config fingerprint per workspace id, a changed
/// command asks again, and a user-level config runs without a prompt. The store is the
/// defaulted <see cref="AppSnapshot.TrustedStatusLines"/> field, so Version stays 1.
/// </summary>
public static class StatusLineTrust
{
    public static bool IsTrusted(StatusLineConfig config, IReadOnlyDictionary<string, string>? trusted, string workspaceId)
        => !config.FromWorkspace || (trusted is not null && trusted.TryGetValue(workspaceId, out var fingerprint) && fingerprint == config.Fingerprint);

    public static bool IsTrusted(AppSnapshot snapshot, StatusLineConfig config, string workspaceId)
        => IsTrusted(config, snapshot.TrustedStatusLines, workspaceId);

    /// <summary>이 워크스페이스에서 허용: records the fingerprint, so an edited command asks again.</summary>
    public static AppSnapshot Trust(AppSnapshot snapshot, StatusLineConfig config, string workspaceId)
    {
        var trusted = snapshot.TrustedStatusLines is null ? [] : new Dictionary<string, string>(snapshot.TrustedStatusLines);
        trusted[workspaceId] = config.Fingerprint;
        return snapshot with { TrustedStatusLines = trusted };
    }

    /// <summary>
    /// What a pane shows: the command it may run now, and the workspace command still
    /// waiting for an answer. An untrusted workspace command is never run.
    /// </summary>
    public static (StatusLineConfig? Config, StatusLineConfig? Untrusted) Resolve(StatusLineConfig? discovered, AppSnapshot snapshot, string workspaceId)
    {
        if (discovered is null) return (null, null);
        return IsTrusted(snapshot, discovered, workspaceId) ? (discovered, null) : (null, discovered);
    }
}
