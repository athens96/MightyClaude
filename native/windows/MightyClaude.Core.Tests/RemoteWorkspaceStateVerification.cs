using System.Text.Json;
using MightyClaude.Core;

/// <summary>Workspace.remote version 2 on Windows: the same JSON as the Mac, older shapes dropped, a local workspace never read as remote.</summary>
internal static class RemoteWorkspaceStateVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string message) { try { await action(); } catch (Exception) { return; } throw new InvalidOperationException(message); }
    internal static JsonDocument Fixture(string name)
    {
        using var stream = typeof(RemoteWorkspaceStateVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.RemoteV2." + name) ?? throw new InvalidOperationException(name + " is not embedded");
        return JsonDocument.Parse(stream);
    }

    internal static Task SharedReferenceCasesDecideWhatIsKept()
    {
        using var file = Fixture("state-references.json");
        var cases = file.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Count == 12, "the fixture has 12 cases");
        foreach (var item in cases)
            Check(StateStore.KeepsWorkspace(item.GetProperty("workspace")) == item.GetProperty("kept").GetBoolean(), "case: " + item.GetProperty("name").GetString());
        return Task.CompletedTask;
    }

    internal static async Task VersionTwoLoadsAsRemoteAndOlderReferencesAreDropped()
    {
        var directory = Verification.Temp(); var folder = Verification.Temp();
        try
        {
            var local = JsonSerializer.Serialize(folder, Wire.Json);
            var state = "{\"version\":1,\"workspaces\":["
                + "{\"id\":\"local\",\"name\":\"Local\",\"path\":" + local + "},"
                + "{\"id\":\"far\",\"name\":\"Far\",\"path\":\"C:\\\\work\\\\project\",\"remote\":{\"version\":2,\"connectionId\":\"conn-1\",\"workspaceId\":\"ws-project\",\"hostName\":\"studio-pc\"}},"
                + "{\"id\":\"mac\",\"name\":\"Mac\",\"path\":\"/Users/example/site\",\"remote\":{\"version\":2,\"connectionId\":\"conn-2\",\"workspaceId\":\"ws-site\",\"hostName\":\"my-mac\"}},"
                + "{\"id\":\"v20\",\"name\":\"V20\",\"path\":\"C:\\\\work\\\\other\",\"remote\":{\"version\":2.0,\"connectionId\":\"conn-1\",\"workspaceId\":\"ws-other\",\"hostName\":\"studio-pc\"}},"
                + "{\"id\":\"old\",\"name\":\"Old\",\"path\":" + local + ",\"remote\":{\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"vs\",\"name\":\"VS\",\"path\":" + local + ",\"remote\":{\"version\":\"2\",\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"vn\",\"name\":\"VN\",\"path\":" + local + ",\"remote\":{\"version\":null,\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"va\",\"name\":\"VA\",\"path\":" + local + ",\"remote\":[]},"
                + "{\"id\":\"bad\",\"name\":\"Bad\",\"path\":\"C:\\\\work\\\\bad\",\"remote\":{\"version\":2,\"connectionId\":7,\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"dup\",\"name\":\"Dup\",\"path\":\"C:\\\\work\\\\dup\",\"remote\":{\"version\":2,\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"},\"remote\":{\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"cap\",\"name\":\"Cap\",\"path\":" + local + ",\"Remote\":{\"version\":2,\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"vx\",\"name\":\"VX\",\"path\":" + local + ",\"remote\":\"studio-pc\"}],"
                + "\"sessions\":[{\"id\":\"s-local\",\"workspaceId\":\"local\"},{\"id\":\"s-far\",\"workspaceId\":\"far\"},{\"id\":\"s-far-cmd\",\"workspaceId\":\"far\",\"kind\":\"shell\"},{\"id\":\"s-old\",\"workspaceId\":\"old\"},{\"id\":\"s-bad\",\"workspaceId\":\"bad\"},{\"id\":\"s-vx\",\"workspaceId\":\"vx\"}]}";
            await File.WriteAllTextAsync(Path.Combine(directory, "workspace-state.json"), state);
            var store = new StateStore(directory); var loaded = await store.LoadAsync();
            Check(loaded.Workspaces.Select(w => w.Id).SequenceEqual(["local", "far", "mac", "v20", "cap"]), "kept: " + string.Join(",", loaded.Workspaces.Select(w => w.Id)));
            Check(loaded.Sessions.Select(s => s.Id).SequenceEqual(["s-local", "s-far", "s-far-cmd"]), "panes of dropped workspaces go with them");
            var far = store.GetWorkspace("far");
            Check(far.Remote is { Version: 2, ConnectionId: "conn-1", WorkspaceId: "ws-project", HostName: "studio-pc" } && far.IsRemote && far.Path == "C:\\work\\project", "a Windows host path stays the host's");
            Check(store.GetWorkspace("mac").Path == "/Users/example/site", "a Mac host path is valid on Windows too");
            Check(store.GetWorkspace("v20").Remote is { Version: 2, WorkspaceId: "ws-other" }, "version 2.0 is the number 2");
            Check(store.GetWorkspace("local") is { Remote: null, IsRemote: false }, "a local workspace is read as local");
            Check(store.GetWorkspace("cap") is { Remote: null, IsRemote: false }, "only the exact \"remote\" key is read, as on the Mac");
            await store.SaveAsync(loaded);
            var saved = await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json"));
            Check(saved.Contains("\"remote\":{", StringComparison.Ordinal) && saved.Contains("\"version\":2", StringComparison.Ordinal) && !saved.Contains("\"version\":2.0", StringComparison.Ordinal) && !saved.Contains("\"old\"", StringComparison.Ordinal), "saved with the version, without the dropped ones");
            Check(!saved.Contains("isRemote", StringComparison.OrdinalIgnoreCase), "the computed IsRemote is never written");
            var again = await new StateStore(directory).LoadAsync();
            Check(again.Workspaces.Single(w => w.Id == "far").Remote == far.Remote, "a reload reads the same reference");
            Check(again.Workspaces.Single(w => w.Id == "local").Remote is null, "a reload reads the local one as local");
        }
        finally { Directory.Delete(directory, true); Directory.Delete(folder, true); }
    }

    internal static async Task ALocalWorkspaceIsNeverWrittenOrReadAsRemote()
    {
        var directory = Verification.Temp(); var folder = Verification.Temp();
        try
        {
            var store = new StateStore(directory); await store.LoadAsync();
            var local = store.ApproveLocal(folder);
            var remote = store.ApproveRemote("conn-1", "ws-project", "project", "C:\\work\\project", "studio-pc");
            Check(remote is { IsRemote: true, Remote: { Version: RemoteWorkspaceReference.CurrentVersion, ConnectionId: "conn-1", WorkspaceId: "ws-project", HostName: "studio-pc" } }, "a remote workspace carries version 2");
            Check(store.ApproveRemote("conn-1", "ws-project", "project", "C:\\work\\project", "studio-pc").Id == remote.Id, "opening the same host workspace again returns the one listed");
            Check(store.ApproveLocal(folder).Id == local.Id && local.Remote is null, "a local folder stays local");
            // A remote workspace whose host path is also a real local folder here: only its "remote" value keeps it off this disk.
            var twin = store.ApproveRemote("conn-2", "ws-project", "project", folder, "other");
            Check(twin.Id != local.Id && store.ApproveLocal(folder).Id == local.Id, "a remote workspace never takes a local one's place, even at the same path");
            await Reject(() => store.ResolveLocalAsync(remote.Id), "a remote workspace never resolves as a local folder");
            await Reject(() => store.ResolveLocalAsync(twin.Id), "a remote workspace never resolves as a local folder, even when one exists at its path");
            await Reject(() => Task.FromResult(store.ApproveRemote("conn-1", "ws-project", "x", "relative\\path", "studio-pc")), "a relative host path is refused");
            await Reject(() => Task.FromResult(store.ApproveRemote("bad id", "ws-project", "x", "C:\\work", "studio-pc")), "a bad connection id is refused");
            await Reject(() => Task.FromResult(store.ApproveRemote("conn-1", "ws-project", "x", "C:\\work", " ")), "an empty host name is refused");
            var snapshot = new AppSnapshot { Workspaces = [local, remote, twin] };
            await store.SaveAsync(snapshot);
            await Reject(() => store.SaveAsync(snapshot with { Workspaces = [local, remote, twin with { Remote = null }] }), "a remote workspace cannot be saved as local");
            await Reject(() => store.SaveAsync(snapshot with { Workspaces = [local with { Remote = new("conn-9", "ws", "x") }, remote, twin] }), "a local workspace cannot be saved as remote");
            var runs = new RunManager(_ => Task.FromResult(twin), new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)), directory, _ => { });
            try { await runs.StartReachedAsync(new StartRunRequest("pane-1", twin.Id, "shell", "echo hi", [])); throw new InvalidOperationException("a runner never starts in a remote workspace"); }
            catch (InvalidOperationException ex) when (ex.Message == Locale.Get("run.error.approvedWorkspaceOnly")) { }
            await runs.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); Directory.Delete(folder, true); }
    }
}
