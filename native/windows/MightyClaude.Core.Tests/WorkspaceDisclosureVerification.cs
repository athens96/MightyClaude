using System.Text.Json;
using MightyClaude.Core;

internal static class WorkspaceDisclosureVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task MigrationSelectionAndPersistence()
    {
        var directory = Verification.Temp();
        try
        {
            var firstPath = Path.Combine(directory, "first"); var secondPath = Path.Combine(directory, "second");
            Directory.CreateDirectory(firstPath); Directory.CreateDirectory(secondPath);
            var first = new Workspace { Id = "first", Name = "First", Path = firstPath };
            var second = new Workspace { Id = "second", Name = "Second", Path = secondPath };
            var pane = new RunSession { WorkspaceId = first.Id, Draft = "pending input" };
            var original = new AppSnapshot { Workspaces = [first, second], Sessions = [pane], ActiveWorkspaceId = first.Id, ActiveSessionId = pane.Id };
            Check(WorkspaceDisclosure.Expanded(original).SetEquals([first.Id]), "A legacy null preference opens only the active workspace.");
            var expanded = WorkspaceDisclosure.Toggle(original, second.Id);
            Check(WorkspaceDisclosure.Expanded(expanded).SetEquals([first.Id, second.Id]) && expanded.ActiveWorkspaceId == first.Id && expanded.ActiveSessionId == pane.Id && ReferenceEquals(original.Sessions, expanded.Sessions), "Disclosure does not switch workspaces or mutate panes/drafts.");
            var collapsed = WorkspaceDisclosure.Toggle(WorkspaceDisclosure.Toggle(expanded, first.Id), second.Id);
            Check(collapsed.ExpandedWorkspaceIds is { Count: 0 } && WorkspaceDisclosure.Expanded(collapsed).Count == 0, "Explicit [] means every workspace is collapsed even with an active session.");
            var selected = WorkspaceDisclosure.Open(WorkspaceDisclosure.Open(collapsed, second.Id), first.Id);
            Check(WorkspaceDisclosure.Expanded(selected).SetEquals([first.Id, second.Id]), "Selecting opens one list without closing another.");
            Check(ReferenceEquals(selected, WorkspaceDisclosure.Toggle(selected, "removed")) && ReferenceEquals(selected, WorkspaceDisclosure.Open(selected, "removed")), "Stale workspace buttons cannot introduce unknown IDs.");
            var normalized = StateStore.Normalize(original with { ExpandedWorkspaceIds = [second.Id, "removed", second.Id, first.Id] }, false);
            Check(normalized.ExpandedWorkspaceIds!.SequenceEqual([first.Id, second.Id]), "Persistence prunes removed IDs and deduplicates in stable order.");
            var profile = Path.Combine(directory, "profile"); Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(profile, "workspace-state.json"), JsonSerializer.Serialize(original, Wire.Json));
            var store = new StateStore(profile); await store.LoadAsync();
            await store.SaveAsync(collapsed);
            var restored = await new StateStore(store.DirectoryPath).LoadAsync();
            Check(restored.ExpandedWorkspaceIds is { Count: 0 } && WorkspaceDisclosure.Expanded(restored).Count == 0 && restored.Sessions.Single().Draft == pane.Draft, "All-collapsed state and draft survive a real disk round trip.");
            var json = JsonSerializer.Serialize(original, Wire.Json);
            Check(JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)?.ExpandedWorkspaceIds is null, "Default serialization does not turn legacy fallback into explicit [].");
            foreach (var malformed in new[] { "true", "{}", "[1]", "[\"first\",false]" })
                Check(JsonSerializer.Deserialize<AppSnapshot>("{\"expandedWorkspaceIds\":" + malformed + "}", Wire.Json)?.ExpandedWorkspaceIds is null, "A malformed optional expansion preference must not discard the whole snapshot.");
            await using var service = new DesktopService(Path.Combine(directory, "service"), null, Path.Combine(directory, "plugins")); await service.InitializeAsync();
            var a = await service.AddWorkspaceAsync(firstPath); var b = await service.AddWorkspaceAsync(secondPath);
            Check(WorkspaceDisclosure.Expanded(service.Snapshot).SetEquals([a.Id, b.Id]), "Opening folders expands each without closing the previous workspace.");
            await service.RemoveWorkspaceAsync(a.Id);
            Check(service.Snapshot.ExpandedWorkspaceIds!.SequenceEqual([b.Id]), "Removing a workspace prunes its saved expansion.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
