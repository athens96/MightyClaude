using MightyClaude.Core;

internal static class WorkDashboardVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static RunSession Pane(string id, string status = "idle", string kind = "claude") => new() { Id = id, WorkspaceId = "w", Kind = kind, Status = status, CreatedAt = "2026-10-01T09:00:00Z" };
    private static ToolPermissionRequest Request(string id, bool question = false, string state = "pending") => new(id, "r", id, question ? "AskUserQuestion" : "Bash", "{}", "fixture", State: state, CanAnswerQuestions: question);

    // Ported from the Mac/phone fixture: a waiting pane also counts as running,
    // whereas the waiting number counts individual requests, not panes.
    internal static Task CountsAndAttentionMatchMac()
    {
        var sessions = new[] { Pane("run", "running"), Pane("asking", "running"), Pane("done", "completed"), Pane("err", "error"), Pane("shell", "running", "shell"), Pane("io", "running", "agent-terminal"), Pane("files", "completed", "files") };
        WorkDashboard.Attention Attention(string id) => WorkDashboard.Pending(id switch
        {
            "asking" => [Request("q1", true), Request("q2", true), Request("p1")],
            "done" => [Request("old", state: "allowed")],
            "io" => [Request("ignored")],
            _ => [],
        });
        Check(WorkDashboard.Count(sessions, Attention) == new WorkDashboard.Stats(3, 3, 1), "Mac dashboard fixture counts changed");
        Check(WorkDashboard.Count([], Attention) == new WorkDashboard.Stats(0, 0, 0), "empty dashboard is zero");
        Check(WorkDashboard.WorkspaceBadges(sessions, Attention) == new WorkDashboard.Badges(2, 1, 1, 2, 1), "waiting panes must not also inflate running workspace badges");
        Check(WorkDashboard.WorkspaceBadges([Pane("a", "failed"), Pane("b", "cancelled")], _ => new()) == new WorkDashboard.Badges(Errors: 1, Stopped: 1), "status aliases share Mac design tones");
        Check(WorkDashboard.MakeCard(Pane("asking", "error"), new(1)).DisplayStatus == "waiting", "a question is actionable even if the run reported error");
        return Task.CompletedTask;
    }

    internal static Task CardsRespectUsageIdentityAndStablePriority()
    {
        var pane = Pane("own") with { Provider = "codex", SessionUsage = new() { Provider = "codex", ContextUsedTokens = 41, ContextWindowTokens = 100 } };
        Check(WorkDashboard.MakeCard(pane, new()).ContextPercent == 41, "direct own-provider context preserved");
        Check(WorkDashboard.MakeCard(pane with { Provider = "claude" }, new()).ContextPercent is null, "provider switch cannot relabel old usage");
        Check(WorkDashboard.MakeCard(pane with { SessionUsage = new() { Provider = "codex", ContextUsedTokens = 41 } }, new()).ContextPercent is null, "missing window is unknown, not zero");
        var start = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        Check(WorkDashboard.MakeCard(pane with { RunTiming = new(start, start.AddSeconds(-1)) }, new()).Timing is null, "invalid timing is absent");
        Check(WorkDashboard.MakeCard(pane with { Model = " default " }, new()).Model is null, "default is not a concrete model");
        var cards = new[] { Pane("idle"), Pane("stop", "interrupted"), Pane("old", "completed"), Pane("new", "completed") with { CreatedAt = "2026-10-01T10:00:00Z" }, Pane("err", "failed"), Pane("run", "running"), Pane("wait", "running") }.Select(s => WorkDashboard.MakeCard(s, s.Id == "wait" ? new(1) : new()));
        Check(WorkDashboard.Ordered(cards).Select(c => c.Session.Id).SequenceEqual(new[] { "wait", "run", "err", "new", "old", "stop", "idle" }), "priority matches Mac and phone");
        Check(WorkDashboard.Ordered(new[] { "a", "b", "c" }.Select(id => WorkDashboard.MakeCard(Pane(id), new()))).Select(c => c.Session.Id).SequenceEqual(new[] { "a", "b", "c" }), "equal rank/time retains source order");
        return Task.CompletedTask;
    }

    internal static Task ActivityAndAgeRemainBoundedAndTruthful()
    {
        var now = DateTimeOffset.Parse("2026-10-01T10:20:00Z");
        var pane = Pane("tool", "error") with { Logs = [new("entry", "error", "\n  first line\nsecond line", "2026-10-01T10:17:00Z")] };
        var card = WorkDashboard.MakeCard(pane, new());
        Check(card.LastActivity == "first line" && card.ActivityIsError && card.UpdatedAt == now.AddMinutes(-3), "newest meaningful line and source time retained");
        Check(WorkDashboard.SidebarDetail(card, now).Contains("first line", StringComparison.Ordinal), "failed run exposes actual reason");
        Check(!WorkDashboard.SidebarDetail(WorkDashboard.MakeCard(pane with { Status = "completed" }, new()), now).Contains("first line", StringComparison.Ordinal), "old error cannot label successful run failed");
        var unicode = string.Concat(Enumerable.Repeat("👩‍💻", 250));
        card = WorkDashboard.MakeCard(pane with { Logs = [new("entry", "assistant", unicode, "2026-10-01T10:17:00Z")] }, new());
        Check(card.LastActivity is { } text && !char.IsHighSurrogate(text[^1]) && new System.Globalization.StringInfo(text).LengthInTextElements <= 200, "Unicode activity is capped without broken UTF-16");
        Check(WorkDashboard.Since(now.AddMinutes(10), now) == new WorkDashboard.Age("now", 0), "future activity never negative");
        Check(WorkDashboard.Since(now.AddHours(-26), now) == new WorkDashboard.Age("days", 1), "largest whole unit");
        Check(WorkDashboard.SidebarDetail(WorkDashboard.MakeCard(Pane("shell", kind: "shell"), new()), now) == "", "non-agent does not inherit provider metadata");
        return Task.CompletedTask;
    }

    internal static Task SettingsSelectionSurvivesPersistence()
    {
        var saved = StateStore.Normalize(new AppSnapshot { SettingsPane = "styles", SidebarWidth = 999 }, false);
        Check(saved.SettingsPane == "styles" && saved.SidebarWidth == DesignMetrics.Layout.SidebarMax, "valid category survives and sidebar bounded by the sidebar column's max");
        saved = StateStore.Normalize(saved with { SettingsPane = "unknown", SidebarWidth = double.NaN }, false);
        Check(saved.SettingsPane == "general" && saved.SidebarWidth == DesignMetrics.Layout.SidebarDefault, "old/corrupt selection safely defaults");
        Check(SettingsNavigation.Categories.Select(c => c.Id).SequenceEqual(new[] { "general", "models", "styles", "tools", "cli", "mobile", "companion", "about" }), "Mac settings categories preserve navigation order");
        Check(SettingsNavigation.Categories.SelectMany(c => c.Sections).Distinct().Count() == SettingsSections.MacOrder.Count, "every Mac settings section belongs to one category");
        return Task.CompletedTask;
    }

    internal static async Task SidebarCollapsedPersistsAndKeepsWidth()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSnapshot>("""{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300}""", Wire.Json)!;
        Check(!legacy.SidebarCollapsed && !new AppSnapshot().SidebarCollapsed, $"old state without the field must open the sidebar; got collapsed={legacy.SidebarCollapsed}");
        var normalized = StateStore.Normalize(new AppSnapshot { SidebarCollapsed = true, SidebarWidth = 300 }, false);
        Check(normalized.SidebarCollapsed && normalized.SidebarWidth == 300, $"normalize must keep the fold and the width; got collapsed={normalized.SidebarCollapsed} width={normalized.SidebarWidth}");
        var directory = Verification.Temp();
        try
        {
            foreach (var collapsed in new[] { true, false })
            {
                var store = new StateStore(directory); await store.LoadAsync();
                await store.SaveAsync(new AppSnapshot { SidebarCollapsed = collapsed, SidebarWidth = 310 });
                var text = await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json"));
                Check(!collapsed || text.Contains("\"sidebarCollapsed\":true"), $"the saved file must name sidebarCollapsed as true; got {text}");
                var restored = await new StateStore(directory).LoadAsync();
                Check(restored.SidebarCollapsed == collapsed && restored.SidebarWidth == 310, $"reload must restore collapsed={collapsed} width=310; got collapsed={restored.SidebarCollapsed} width={restored.SidebarWidth}");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // The background work line above the composer: older state shows it folded, both switches survive a reload, only a
    // JSON boolean counts, and a hidden line draws nothing in either view (M/SnapshotPreferencesTests, M/PlanCardTests).
    internal static async Task BackgroundWorkLinePersistsAndHides()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSnapshot>("""{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300}""", Wire.Json)!;
        Check(legacy.ShowsBackgroundWork is null && legacy.BackgroundWorkOpen is null && new AppSnapshot().ShowsBackgroundWork is null && new AppSnapshot().BackgroundWorkOpen is null,
            $"old state without the fields must show the line folded; got shows={legacy.ShowsBackgroundWork} open={legacy.BackgroundWorkOpen}");
        var invalid = System.Text.Json.JsonSerializer.Deserialize<AppSnapshot>("""{"version":1,"showsBackgroundWork":0,"backgroundWorkOpen":"true"}""", Wire.Json)!;
        Check(invalid.ShowsBackgroundWork is null && invalid.BackgroundWorkOpen is null, $"only a JSON boolean counts; got shows={invalid.ShowsBackgroundWork} open={invalid.BackgroundWorkOpen}");
        var normalized = StateStore.Normalize(new AppSnapshot { ShowsBackgroundWork = false, BackgroundWorkOpen = true }, false);
        Check(normalized.ShowsBackgroundWork == false && normalized.BackgroundWorkOpen == true, $"normalize must keep both; got shows={normalized.ShowsBackgroundWork} open={normalized.BackgroundWorkOpen}");
        var directory = Verification.Temp();
        try
        {
            foreach (var (shows, open) in new[] { (false, true), (true, false) })
            {
                var store = new StateStore(directory); await store.LoadAsync();
                await store.SaveAsync(new AppSnapshot { ShowsBackgroundWork = shows, BackgroundWorkOpen = open });
                var restored = await new StateStore(directory).LoadAsync();
                Check(restored.ShowsBackgroundWork == shows && restored.BackgroundWorkOpen == open, $"reload must restore shows={shows} open={open}; got shows={restored.ShowsBackgroundWork} open={restored.BackgroundWorkOpen}");
            }
        }
        finally { Directory.Delete(directory, true); }
        var work = new BackgroundWork([new BackgroundTask("a", "agent", "look", "2026-10-06T01:00:00.000Z")], TurnEnded: true);
        foreach (var mighty in new[] { false, true })
            Check(PlanCardSupport.ShowsBackgroundStrip(work, mighty, false) && PlanCardSupport.ShowsBackgroundStrip(work, mighty, false, enabled: true) && !PlanCardSupport.ShowsBackgroundStrip(work, mighty, false, enabled: false),
                $"a hidden line draws nothing (mighty={mighty})");
    }
}
