using MightyClaude.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// The window's one way to a workspace's folder (RemoteWorkspaceFeatures.LocalPath): a remote workspace's path is its host's, so it is
/// never opened, read, run in or handed to a CLI here (M/AppStore+RemoteWorkspaces.swift and the remote == nil guards beside each use).
/// RemoteDesktopVerification scans these sources so no other read of a workspace's Path comes back.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The folder on this PC; null for a remote workspace.</summary>
    internal static string? LocalPath(Workspace? workspace) => RemoteWorkspaceFeatures.LocalPath(workspace);

    /// <summary>The folder on this PC, or why a remote workspace has none here (its pane runs on its host, without file-reading features).</summary>
    internal static string LocalFolder(Workspace workspace) => LocalPath(workspace)
        ?? throw new InvalidOperationException(Locale.Get("remote.pane.agentNote", new Dictionary<string, string> { ["host"] = workspace.Remote?.HostName ?? "" }));
}
