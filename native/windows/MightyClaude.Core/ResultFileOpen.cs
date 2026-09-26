namespace MightyClaude.Core;

/// <summary>
/// Opening a file a final result named. It hangs off DesktopService — the call
/// reads <c>service.OpenResultFile(path, workspaceRoot)</c> — but lives in its
/// own file so DesktopService.cs is not touched, which the locale gate would
/// then hold to its older hardcoded messages.
/// </summary>
public static class ResultFileOpen
{
    /// <summary>
    /// Opens a file named in a final result with the user's default app. The
    /// path is resolved against the workspace root by ResultFiles.Resolve, so a
    /// path outside the workspace — or one that is not an existing regular file
    /// — is never opened. Returns false when nothing was opened.
    /// </summary>
    public static bool OpenResultFile(this DesktopService service, string path, string? workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (ResultFiles.Resolve(path, workspaceRoot) is not { } resolved) return false;
        using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(resolved) { UseShellExecute = true });
        return true;
    }
}
