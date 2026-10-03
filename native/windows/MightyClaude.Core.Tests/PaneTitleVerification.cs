using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// Pane auto-titles (macOS PaneTitleTests.swift): an agent pane's title follows its latest request,
/// shortened to 40 characters, until a rename fixes it; 자동 hands it back to the latest request.
/// </summary>
internal static class PaneTitleVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static LogEntry User(string text) => new(Wire.Id(), "user", text, Wire.Now());
    private static RunSession Agent(string provider = "claude", params string[] requests) =>
        new() { WorkspaceId = "ws", Title = ProviderCatalog.Name(provider), Provider = provider, Logs = requests.Select(User).ToList() };

    internal static Task shortenedFollowsTheMacOSRule()
    {
        Check(PaneTitle.Shortened("") is null && PaneTitle.Shortened("   ") is null && PaneTitle.Shortened("\n\n\t  \n") is null, "empty or whitespace-only input must keep the previous title");
        Check(PaneTitle.Shortened("hello\nworld") == "hello world", "a line break must collapse to one space");
        Check(PaneTitle.Shortened("hello  \n\n  world") == "hello world", "whitespace runs must collapse to one space");
        Check(PaneTitle.Shortened("  leading and trailing  ") == "leading and trailing", "the title must be trimmed");
        var exactly40 = new string('a', 40);
        Check(PaneTitle.Shortened(exactly40) == exactly40, "40 characters must stay whole");
        Check(PaneTitle.Shortened(new string('a', 41)) == exactly40 + "…", "41 characters must be cut to 40 and end in …");
        var cut = PaneTitle.Shortened("This is a really long request that definitely exceeds forty characters")!;
        Check(cut.EndsWith('…') && cut[..^1].Length == 40, "a long request must keep exactly 40 characters before …");
        Check(PaneTitle.Shortened("/ouroboros:run seed") == "/ouroboros:run seed", "a slash command must become the title");
        var slash = PaneTitle.Shortened("/ouroboros:run seed with a very long set of additional arguments here")!;
        Check(slash.StartsWith("/ouroboros:run", StringComparison.Ordinal) && slash.EndsWith('…'), "a long slash command must be cut like any request");
        Check(PaneTitle.Shortened(new string('한', 40)) == new string('한', 40), "40 Korean characters must stay whole");
        Check(PaneTitle.Shortened(new string('한', 41)) == new string('한', 40) + "…", "41 Korean characters must be cut to 40");
        var emoji = string.Concat(Enumerable.Repeat("🎉", 41));
        Check(PaneTitle.Shortened(emoji) == string.Concat(Enumerable.Repeat("🎉", 40)) + "…", "characters are counted like a Swift Character, never splitting an emoji");
        return Task.CompletedTask;
    }

    internal static Task titleFollowsTheLatestRequestUntilARenameFixesIt()
    {
        var pane = Agent();
        Check(pane.TitleMode is null && PaneTitle.FollowsRequests(pane), "a new agent pane must start automatic");
        pane = PaneTitle.Requested(pane, "first request");
        Check(pane.Title == "first request", "the first request must title the pane");
        pane = PaneTitle.Requested(pane, "second request\nwith a second line");
        Check(pane.Title == "second request with a second line", "the latest request must retitle the pane");
        pane = PaneTitle.Requested(pane, "   ");
        Check(pane.Title == "second request with a second line", "an empty or attachment-only request must keep the previous title");
        pane = PaneTitle.Requested(pane, new string('x', 60));
        Check(pane.Title == new string('x', 40) + "…", "a long request must be cut to 40 characters");

        var workspace = new Workspace();
        var session = Agent() with { WorkspaceId = workspace.Id };
        var renamed = RenameSupport.RenameSession(new AppSnapshot { Workspaces = [workspace], Sessions = [session] }, session.Id, "내 실행 창").Sessions.Single();
        Check(renamed.TitleMode == PaneTitle.Fixed, "a rename must fix the title");
        Check(PaneTitle.Requested(renamed, "a later request").Title == "내 실행 창", "a fixed title must survive a new request");

        foreach (var kind in new[] { "shell", "browser", FilePaneKind.Kind })
        {
            var other = new RunSession { WorkspaceId = "ws", Kind = kind, Title = "터미널" };
            Check(PaneTitle.Requested(other, "ls -la").Title == "터미널", kind + " panes must keep their titles");
        }
        return Task.CompletedTask;
    }

    internal static async Task aRequestRetitlesTheAgentPaneThroughDesktopService()
    {
        var directory = Verification.Temp(); var workspacePath = Verification.Temp();
        await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
        var service = new DesktopService(directory, null, "", catalog);
        try
        {
            await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(workspacePath);
            var agent = new RunSession { WorkspaceId = workspace.Id, Title = ProviderCatalog.Name("claude") };
            var fixedPane = new RunSession { WorkspaceId = workspace.Id, Title = "고정한 이름", TitleMode = PaneTitle.Fixed };
            await service.UpdateAsync(s => s with { Sessions = [agent, fixedPane] });
            var request = "Windows 창 제목이 최근 요청을 따라가는지 확인하고 40자에서 잘리는지 봐 줘";
            // The provider is absent, so the run fails after the request is logged; the title has already moved.
            try { await service.StartAsync(new(agent.Id, workspace.Id, "claude", request, [])); } catch (Exception) { }
            try { await service.StartAsync(new(fixedPane.Id, workspace.Id, "claude", request, [])); } catch (Exception) { }
            Check(service.Snapshot.Sessions.Single(s => s.Id == agent.Id).Title == PaneTitle.Shortened(request), "a sent request must retitle an automatic agent pane");
            Check(service.Snapshot.Sessions.Single(s => s.Id == fixedPane.Id).Title == "고정한 이름", "a sent request must leave a fixed title alone");

            await service.RenameSessionAsync(agent.Id, "이름 지정");
            Check(service.Snapshot.Sessions.Single(s => s.Id == agent.Id) is { Title: "이름 지정", TitleMode: PaneTitle.Fixed }, "renaming through the service must fix the title");
            await service.SetSessionAutoTitleAsync(agent.Id);
            Check(service.Snapshot.Sessions.Single(s => s.Id == agent.Id) is { TitleMode: PaneTitle.Automatic } back && back.Title == PaneTitle.Shortened(request), "자동 must retitle the pane from its latest request at once");
        }
        finally { await service.DisposeAsync(); Directory.Delete(directory, true); Directory.Delete(workspacePath, true); }
    }

    internal static Task automaticChoiceRetitlesFromTheLatestTypedRequest()
    {
        var workspace = new Workspace();
        var pane = Agent("claude", "first request", "fix the build\n\nfirst line kept\n\n첨부: a.png (1,024 bytes)", "첨부: b.txt (12 bytes)") with { WorkspaceId = workspace.Id, Title = "My pane", TitleMode = PaneTitle.Fixed };
        var codex = Agent("codex") with { WorkspaceId = workspace.Id, Title = "renamed", TitleMode = PaneTitle.Fixed };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [pane, codex] };
        Check(PaneTitle.Tooltip(pane) == "fix the build\n\nfirst line kept", "the attachment line and attachment-only entries must not title the pane");
        var auto = PaneTitle.SetAutomatic(snapshot, pane.Id).Sessions.Single(s => s.Id == pane.Id);
        Check(auto is { TitleMode: PaneTitle.Automatic, Title: "fix the build first line kept" }, "자동 must retitle from the latest typed request");
        Check(PaneTitle.Help(auto) == "fix the build\n\nfirst line kept", "an automatic title's hover text must be the whole request");
        Check(PaneTitle.Help(pane) == "My pane", "a fixed title's hover text must be the title itself");
        var fallback = PaneTitle.SetAutomatic(snapshot, codex.Id).Sessions.Single(s => s.Id == codex.Id);
        Check(fallback.Title == ProviderCatalog.Name("codex"), "with no request, 자동 must fall back to the provider's default name");
        try { PaneTitle.SetAutomatic(snapshot, "no-such-id"); throw new InvalidOperationException("an unknown pane must be refused"); }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }

    internal static async Task restoreRetitlesAutomaticAgentPanesOnly()
    {
        var native = Verification.Temp(); var workspacePath = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = workspacePath };
            var legacy = Agent("claude", "refactor the auth module") with { WorkspaceId = workspace.Id, Title = "Old Custom Name" };
            var auto = Agent("claude", "This is a really long request that definitely exceeds forty characters") with { WorkspaceId = workspace.Id, TitleMode = PaneTitle.Automatic };
            var empty = Agent("codex") with { WorkspaceId = workspace.Id, Title = "stale" };
            var attachmentOnly = Agent("claude", "첨부: file.txt (1 bytes)") with { WorkspaceId = workspace.Id, Title = "stale" };
            var resumed = Agent("claude") with { WorkspaceId = workspace.Id, Title = "이어 가던 세션", ResumeId = "resume-1" };
            var fixedPane = Agent("claude", "some request") with { WorkspaceId = workspace.Id, Title = "My Custom Name", TitleMode = PaneTitle.Fixed };
            var shell = new RunSession { WorkspaceId = workspace.Id, Kind = "shell", Title = "My Shell", Logs = [User("git status")] };
            var unknownMode = Agent("claude", "explain closures") with { WorkspaceId = workspace.Id, TitleMode = "weird" };
            var snap = new AppSnapshot { Workspaces = [workspace], Sessions = [legacy, auto, empty, attachmentOnly, resumed, fixedPane, shell, unknownMode] };
            await File.WriteAllTextAsync(Path.Combine(native, "workspace-state.json"), JsonSerializer.Serialize(snap, Wire.Json));
            Check(JsonSerializer.Serialize(fixedPane, Wire.Json).Contains("\"titleMode\":\"fixed\"", StringComparison.Ordinal), "the mode must be saved under the macOS field titleMode");
            var restored = (await new StateStore(native).LoadAsync()).Sessions.ToDictionary(s => s.Id);
            Check(restored[legacy.Id].Title == "refactor the auth module", "a pane saved before title modes must become automatic and follow its request");
            Check(restored[auto.Id].Title == PaneTitle.Shortened(auto.Logs[0].Text), "an automatic pane must be retitled from its request, cut to 40");
            Check(restored[empty.Id].Title == ProviderCatalog.Name("codex"), "an automatic pane with no request must show the provider default");
            Check(restored[attachmentOnly.Id].Title == ProviderCatalog.Name("claude"), "an attachment-only request must not title the pane");
            Check(restored[resumed.Id].Title == "이어 가던 세션", "a pane that continued a session keeps its title until its own request");
            Check(restored[fixedPane.Id] is { Title: "My Custom Name", TitleMode: PaneTitle.Fixed }, "a fixed title must survive a restart");
            Check(restored[shell.Id].Title == "My Shell", "a shell pane must never be retitled");
            Check(restored[unknownMode.Id] is { TitleMode: null, Title: "explain closures" }, "an unknown mode must load as automatic");
        }
        finally { Directory.Delete(native, true); Directory.Delete(workspacePath, true); }
    }
}
