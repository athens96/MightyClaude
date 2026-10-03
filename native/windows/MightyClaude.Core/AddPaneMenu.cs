namespace MightyClaude.Core;

/// <summary>
/// The "창 추가" menu and the sidebar's way into a folder, as on macOS
/// (WorkspaceView.swift <c>WorkspaceAddMenuItems</c> and the sidebar's 폴더 열기 button;
/// docs/session-resume.md): the agents, a terminal, a browser tab (and on Windows the
/// files pane), then 프로젝트 폴더 열기… at the bottom. While a workspace is listed a
/// folder opens from that menu or Ctrl+O (macOS ⌘O); the sidebar button shows only
/// when no workspace is listed — none yet, or none matching the search.
/// </summary>
public static class AddPaneMenu
{
    public const string Separator = "-";
    public const string AgentPrefix = "agent:";
    public const string Shell = "shell";
    public const string Browser = "browser";
    public const string Files = FilePaneKind.Kind;
    public const string OpenProject = "openProject";
    /// The shared locale key of the bottom entry (macOS <c>L("menu.openProject")</c>).
    public const string OpenProjectKey = "menu.openProject";
    public const string OpenFolderShortcut = "Ctrl+O";
    /// Ctrl+N (macOS ⌘N, <c>menu.newClaudePane</c>) adds a Claude pane at once.
    public const string NewPaneShortcutProvider = "claude";

    /// The menu's entries in order: <c>agent:&lt;provider&gt;</c> for each agent, then the others.
    public static IReadOnlyList<string> Entries() =>
        [.. Wire.Providers.Select(p => AgentPrefix + p), Separator, Shell, Separator, Browser, Files, Separator, OpenProject];

    /// The provider an <c>agent:</c> entry adds, or null for any other entry.
    public static string? AgentProvider(string entry) => entry.StartsWith(AgentPrefix, StringComparison.Ordinal) ? entry[AgentPrefix.Length..] : null;

    /// The sidebar's workspaces for a search: name or path, ignoring case and surrounding spaces (macOS <c>filteredWorkspaces</c>).
    public static IReadOnlyList<Workspace> Filtered(IEnumerable<Workspace> workspaces, string? search)
    {
        var needle = (search ?? "").Trim();
        return workspaces.Where(w => needle.Length == 0 || w.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) || w.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// Whether the sidebar shows its open-folder button: only when the list it shows is empty.
    public static bool ShowsOpenFolderButton(IEnumerable<Workspace> workspaces, string? search) => Filtered(workspaces, search).Count == 0;

    /// <summary>
    /// Whether adding this agent's pane may first ask 새로 시작 / 이어가기…: only a pick
    /// from the menu, and only for an agent whose sessions can be continued (Claude,
    /// Codex). The question itself comes only when the folder has such a session.
    /// Gemini and Ctrl+N start at once.
    /// </summary>
    public static bool MayAskResume(string provider, bool fromMenu) => fromMenu && ResumableSessions.OffersResume("claude", provider);
}
