using MightyClaude.Core;

/// <summary>
/// The Windows "창 추가" menu and open-folder rules, mirroring macOS WorkspaceView.swift
/// (<c>WorkspaceAddMenuItems</c>, the sidebar's 폴더 열기 button shown only for an empty
/// <c>filteredWorkspaces</c>) and AppStore+ResumeSession.swift (only Claude and Codex ask;
/// ⌘N starts at once). docs/session-resume.md.
/// </summary>
internal static class AddPaneMenuVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task TheMenuEndsWithTheThreeWaysToAddAWorkspace()
    {
        var entries = AddPaneMenu.Entries();
        Check(entries.Take(Wire.Providers.Length).Select(AddPaneMenu.AgentProvider).SequenceEqual(Wire.Providers), "agents come first, in provider order");
        Check(entries.TakeLast(4).SequenceEqual([AddPaneMenu.Separator, AddPaneMenu.OpenProject, AddPaneMenu.NewFolder, AddPaneMenu.FromGit]), "기존 폴더 열기…, 새 폴더 만들기… and Git으로 생성… are last, after a separator");
        Check(entries.Count(e => e == AddPaneMenu.OpenProject) == 1 && entries.Count(e => e == AddPaneMenu.NewFolder) == 1 && entries.Count(e => e == AddPaneMenu.FromGit) == 1, "one entry of each way to add a workspace");
        Check(entries.Contains(AddPaneMenu.Shell) && entries.Contains(AddPaneMenu.Browser) && entries.Contains(AddPaneMenu.Files), "terminal, browser and files entries stay");
        Check(entries.Zip(entries.Skip(1)).All(pair => !(pair.First == AddPaneMenu.Separator && pair.Second == AddPaneMenu.Separator)) && entries[0] != AddPaneMenu.Separator, "no empty sections");
        Check(new[] { AddPaneMenu.OpenProject, AddPaneMenu.NewFolder, AddPaneMenu.FromGit, AddPaneMenu.Shell }.All(e => AddPaneMenu.AgentProvider(e) is null), "only agent entries name a provider");
        var text = Locale.Get(AddPaneMenu.OpenProjectKey);
        Check(text is "기존 폴더 열기…" or "Open Existing Folder…", "shared menu.openProject text " + text);
        var newFolder = Locale.Get(AddPaneMenu.NewFolderKey); var fromGit = Locale.Get(AddPaneMenu.FromGitKey);
        Check(newFolder is "새 폴더 만들기…" or "New Folder…", "shared workspace.add.newFolder text " + newFolder);
        Check(fromGit is "Git으로 생성…" or "Create from Git…", "shared workspace.add.fromGit text " + fromGit);
        Check(AddPaneMenu.OpenFolderShortcut == "Ctrl+O", "⌘O maps to Ctrl+O");
        return Task.CompletedTask;
    }

    internal static Task TheOpenFolderButtonShowsOnlyWhenNoWorkspaceIsListed()
    {
        Check(AddPaneMenu.ShowsOpenFolderButton([], null) && AddPaneMenu.ShowsOpenFolderButton([], "x"), "no workspace yet: the button shows");
        var workspaces = new[] { new Workspace { Name = "MightyClaude", Path = @"C:\Work\MightyClaude" }, new Workspace { Name = "Notes", Path = @"D:\Docs\notes" } };
        Check(!AddPaneMenu.ShowsOpenFolderButton(workspaces, null) && !AddPaneMenu.ShowsOpenFolderButton(workspaces, "") && !AddPaneMenu.ShowsOpenFolderButton(workspaces, "   "), "a listed workspace hides the button");
        Check(!AddPaneMenu.ShowsOpenFolderButton(workspaces, "mighty") && !AddPaneMenu.ShowsOpenFolderButton(workspaces, " DOCS "), "a matching name or path keeps it hidden");
        Check(AddPaneMenu.ShowsOpenFolderButton(workspaces, "no-such-folder"), "a search matching nothing shows it");
        // Name and path are matched apart, as on macOS: text spanning both matches nothing.
        Check(AddPaneMenu.Filtered(workspaces, "NotesD:").Count == 0, "name and path are not joined");
        Check(AddPaneMenu.Filtered(workspaces, "o").Count == 2 && AddPaneMenu.Filtered(workspaces, "claude").Single().Name == "MightyClaude", "filter keeps order and matches");
        return Task.CompletedTask;
    }

    internal static Task OnlyClaudeAndCodexFromTheMenuMayAskToResume()
    {
        Check(AddPaneMenu.MayAskResume("claude", fromMenu: true) && AddPaneMenu.MayAskResume("codex", fromMenu: true), "Claude and Codex may ask from the menu");
        Check(AddPaneMenu.MayAskResume("gemini", fromMenu: true), "Antigravity may ask, as its conversations can be continued");
        foreach (var provider in Wire.Providers) Check(!AddPaneMenu.MayAskResume(provider, fromMenu: false), provider + ": Ctrl+N starts at once");
        Check(AddPaneMenu.NewPaneShortcutProvider == "claude", "Ctrl+N adds a Claude pane");
        Check(!AddPaneMenu.MayAskResume("unknown", fromMenu: true), "an unknown agent never asks");
        return Task.CompletedTask;
    }
}
