using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// The remote mirror on Windows (docs/remote-workspaces.md "그 컴퓨터의 창 그대로"): native/contracts/fixtures/remote-mirror/ read as the
/// Mac reads it (RemoteMirrorWireTests), the bounds and revisions both sides keep, what is never saved, the host's routes over loopback,
/// and a Windows client mirroring a Windows host end to end.
/// </summary>
internal static class RemoteMirrorVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private const string ResourcePrefix = "MightyCode.Core.Tests.RemoteMirror.";

    private static JsonNode Node(string name)
    {
        using var stream = typeof(RemoteMirrorVerification).Assembly.GetManifestResourceStream(ResourcePrefix + name) ?? throw new InvalidOperationException(name + " is not embedded");
        return JsonNode.Parse(stream)!;
    }
    private static T Decode<T>(string name) => Node(name).Deserialize<T>(RemoteWire.Json) ?? throw new InvalidOperationException(name + " decodes to null");
    private static RemoteMirrorSession FixtureSession() => RemoteMirrorSupport.Read(Decode<RemoteWireMirrorDetail>("detail.json").Session) ?? throw new InvalidOperationException("detail.json's session does not read");

    private static string Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "remote-mirror", "endpoints.json"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("the repository root was not found"), "native", "contracts", "fixtures", "remote-mirror");
    }

    /// <summary>The Mac's theIndexNamesEveryRouteAndEveryFileIsAccountedFor, and every file is embedded here.</summary>
    internal static Task TheIndexNamesEveryRouteAndEveryFileIsAccountedFor()
    {
        var index = Node("endpoints.json").AsArray().Select(e => e!.AsObject()).ToList();
        Check(index.Select(e => e["name"]!.GetValue<string>()).ToHashSet().SetEquals(["list", "detail", "submit", "stop", "permission", "questions", "plan", "close", "create"]), "every route is named");
        var named = new HashSet<string>(StringComparer.Ordinal) { "endpoints.json", "state-unchanged.json" };
        foreach (var entry in index)
        {
            Check(entry["path"]!.GetValue<string>().StartsWith(RemoteMirrorRoutes.Prefix, StringComparison.Ordinal), "every path is under " + RemoteMirrorRoutes.Prefix);
            foreach (var key in new[] { "request", "response" }) if (entry[key]?.GetValue<string>() is { } name) named.Add(name);
        }
        var files = Directory.GetFiles(Folder()).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Check(files.SetEquals(named), "unaccounted: " + string.Join(", ", files.Except(named).Concat(named.Except(files))));
        var embedded = typeof(RemoteMirrorVerification).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n => n[ResourcePrefix.Length..]).ToHashSet(StringComparer.Ordinal);
        Check(embedded.SetEquals(files), "the checks read every file of the folder: " + string.Join(", ", embedded.Except(files).Concat(files.Except(embedded))));
        return Task.CompletedTask;
    }

    /// <summary>The Mac's theSamplesDecodeValidateAndRoundTrip: every sample reads into the Windows types, passes the client's checks and writes back as the Mac wrote it.</summary>
    internal static Task TheSamplesDecodeValidateAndRoundTrip()
    {
        var state = Decode<RemoteWireMirrorState>("state.json");
        Check(RemoteMirrorSupport.Valid(state), "state.json passes the client's checks");
        Check(state.HostName == "studio-mac" && state.Workspaces.Select(w => w.Id).SequenceEqual(["ws-project"]) && state.Sessions.Select(s => s.Id).SequenceEqual(["pane-1", "pane-2"]), "the list reads");
        Check(state.Sessions[0] is { Pending: 1, Queued: 1, Branch: "mighty/pane-1" } && state.Sessions[1].Branch is null, "counts and branch read");
        Check(RemoteWireFixtureVerification.Same(Node("state.json"), JsonSerializer.SerializeToNode(state, RemoteWire.Json)), "the list writes back as the Mac wrote it: " + JsonSerializer.Serialize(state, RemoteWire.Json));
        var unchanged = Decode<RemoteWireMirrorState>("state-unchanged.json");
        Check(RemoteMirrorSupport.Valid(unchanged) && unchanged.Unchanged == true && unchanged.Sessions.Count == 0, "the unchanged answer reads");
        Check(RemoteWireFixtureVerification.Same(Node("state-unchanged.json"), JsonSerializer.SerializeToNode(unchanged, RemoteWire.Json)), "the unchanged answer writes back");
        var detail = Decode<RemoteWireMirrorDetail>("detail.json");
        var session = FixtureSession();
        Check(RemoteMirrorSupport.Valid(detail, session, "pane-1"), "detail.json passes the client's checks");
        Check(!RemoteMirrorSupport.Valid(detail, session, "pane-2"), "a detail answers for the pane asked about only");
        Check(session.Logs.Count == 2 && session.GraphRuns.Count == 1 && session.Permissions.Single().ToolName == "Write" && session.Permissions[0].InputJson.Contains("notes.txt", StringComparison.Ordinal),
            "entries, runs and the request read (inputJSON as InputJson)");
        Check(session.Queued.SequenceEqual([new RemoteMirrorQueued("queued-1", "Then update CHANGELOG.md")]) && session.Base == "main" && session.Branch == "mighty/pane-1", "queue and branch read");
        Check(session.Settings.PermissionMode == "acceptEdits" && session.Settings.Effort == "high" && session.TodoProgress?.Items.Count == 1 && session.StartedAt == "2026-11-13T01:00:01Z", "settings, checklist and clock read");
        // What a Windows host writes for it is the Mac's own JSON (nulls aside).
        var written = RemoteMirrorSupport.Write(session);
        Check(RemoteWireFixtureVerification.Same(Node("detail.json")["session"], written), "a Windows host writes the Mac's session: " + written.ToJsonString());
        Check(written["permissions"]![0]!["inputJSON"] is not null && written["permissions"]![0]!["inputJson"] is null, "the request's input travels as inputJSON");
        var again = RemoteMirrorSupport.Read(written)!;
        Check(JsonSerializer.Serialize(again, RemoteWire.Json) == JsonSerializer.Serialize(session, RemoteWire.Json), "what Windows writes reads back the same");
        var wire = new RemoteWireMirrorDetail(RemoteProtocol.Version, RemoteMirrorProtocol.Version, detail.Revision, written);
        Check(RemoteWireFixtureVerification.Same(Node("detail.json"), JsonSerializer.SerializeToNode(wire, RemoteWire.Json)), "the whole answer writes as the Mac's");

        var submit = Decode<RemoteWireMirrorSubmit>("submit.json");
        Check(submit.Text == "Then update CHANGELOG.md" && submit.Attachments?.Count == 1 && AttachmentSupport.Validate(submit.Attachments) is { Count: 1 }, "the send reads with its file");
        Check(RemoteWireFixtureVerification.Same(Node("submit.json"), JsonSerializer.SerializeToNode(submit, RemoteWire.Json)), "the send writes back");
        Check(JsonNode.Parse("{\"text\":\"hi\"}").Deserialize<RemoteWireMirrorSubmit>(RemoteWire.Json)!.Attachments is null, "a send may leave its files out");
        Check(Decode<RemoteWireMirrorOutcome>("outcome.json").Outcome == "queued", "the outcome reads");
        var permission = Decode<RemoteWireMirrorPermission>("permission.json");
        Check(permission is { RequestId: "perm-4", RunId: "job-1", Allow: true, Always: false }, "the permission answer reads");
        Check(JsonNode.Parse("{\"requestId\":\"r\",\"runId\":\"j\",\"allow\":false}").Deserialize<RemoteWireMirrorPermission>(RemoteWire.Json)!.Always is null, "always may be left out, as the Mac's client does");
        Check(Decode<RemoteWireMirrorQuestions>("questions.json").Answers["Which file?"].SelectedOptions.SequenceEqual(["README.md"]), "the questions answer reads");
        Check(Decode<RemoteWirePlan>("plan.json").Decision() is { Kind: "revise", Feedback: "Keep the tests" }, "the plan answer reads");
        Check(Decode<RemoteWireMirrorCreate>("create.json").Provider == "claude" && Decode<RemoteWireMirrorCreated>("created.json").SessionId == "pane-3", "create and created read");
        Check(Decode<RemoteWireAnswer>("answer.json").Accepted && Node("empty.json") is JsonObject { Count: 0 }, "the answer and the empty body read");
        foreach (var (name, type) in new (string, Type)[] { ("outcome.json", typeof(RemoteWireMirrorOutcome)), ("permission.json", typeof(RemoteWireMirrorPermission)), ("questions.json", typeof(RemoteWireMirrorQuestions)), ("plan.json", typeof(RemoteWirePlan)), ("create.json", typeof(RemoteWireMirrorCreate)), ("created.json", typeof(RemoteWireMirrorCreated)) })
            Check(RemoteWireFixtureVerification.Same(Node(name), JsonSerializer.SerializeToNode(Node(name).Deserialize(type, RemoteWire.Json), type, RemoteWire.Json)), name + " writes back as the Mac wrote it");
        return Task.CompletedTask;
    }

    /// <summary>The Mac's aClientRefusesListsOutsideTheRules, and the bodies are read as strictly as the Mac reads them.</summary>
    internal static Task AClientRefusesListsOutsideTheRules()
    {
        var state = Decode<RemoteWireMirrorState>("state.json");
        Check(!RemoteMirrorSupport.Valid(state with { Mirror = 2 }), "another mirror version is refused");
        Check(!RemoteMirrorSupport.Valid(state with { Protocol = 1 }), "another protocol is refused");
        RemoteWireMirrorState Pane(Func<RemoteMirrorSummary, RemoteMirrorSummary> change) => state with { Sessions = [change(state.Sessions[0]), state.Sessions[1]] };
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Status = "thinking" })), "an unknown status is refused");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { WorkspaceId = "ws-elsewhere" })), "a pane of an unlisted workspace is refused");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Id = "pane-2" })), "a pane listed twice is refused");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Kind = "shell" })), "a command pane is refused");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Id = "../pane" })), "an id that is no identifier is refused");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Provider = "other" })), "an unknown agent is refused");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Pending = -1 })) && !RemoteMirrorSupport.Valid(Pane(s => s with { Queued = 10_001 })), "counts are bounded");
        Check(!RemoteMirrorSupport.Valid(Pane(s => s with { Title = new string('t', 201) })) && !RemoteMirrorSupport.Valid(Pane(s => s with { Revision = new string('a', 65) })), "titles and revisions are bounded");
        Check(!RemoteMirrorSupport.Valid(state with { Sessions = Enumerable.Range(0, RemoteMirrorProtocol.MaximumSessions + 1).Select(i => state.Sessions[1] with { Id = "pane-x" + i }).ToList() }), "more than 128 panes are refused");
        Check(!RemoteMirrorSupport.Valid(state with { HostName = "" }) && !RemoteMirrorSupport.Valid(state with { HostName = new string('h', 121) }), "the host's name is bounded");
        Check(RemoteMirrorSupport.Valid(state with { Unchanged = true, Sessions = [state.Sessions[0] with { Status = "thinking" }] }), "an unchanged answer's lists mean nothing");
        var detail = Decode<RemoteWireMirrorDetail>("detail.json"); var session = FixtureSession();
        Check(!RemoteMirrorSupport.Valid(detail, session with { Logs = Enumerable.Range(0, RemoteMirrorProtocol.MaximumEntries + 1).Select(i => new LogEntry("log-" + i, "user", "x", "2026-11-13T01:00:01Z")).ToList() }, "pane-1"), "more than 80 entries are refused");
        Check(!RemoteMirrorSupport.Valid(detail, session with { Logs = [session.Logs[0] with { Kind = "secret" }] }, "pane-1"), "an entry kind the transcript never keeps is refused");
        Check(!RemoteMirrorSupport.Valid(detail, session with { Logs = [session.Logs[0] with { Text = new string('x', 131_073) }] }, "pane-1"), "an entry over 128 KiB is refused");
        Check(!RemoteMirrorSupport.Valid(detail, session with { Permissions = [session.Permissions[0] with { RunId = "bad id" }] }, "pane-1"), "a request whose run id is no identifier is refused");
        Check(!RemoteMirrorSupport.Valid(detail with { Mirror = 2 }, session, "pane-1"), "another mirror version is refused");
        // Strict as the Mac's decoder: a required member missing, or a list with a null in it, is a bad answer.
        var missing = Node("state.json").AsObject(); missing["sessions"]![0]!.AsObject().Remove("pending");
        Check(Fails(() => missing.Deserialize<RemoteWireMirrorState>(RemoteWire.Json)), "a summary without its pending count is refused");
        var nulled = Node("state.json").AsObject(); nulled["sessions"]!.AsArray().Add(null);
        Check(Fails(() => nulled.Deserialize<RemoteWireMirrorState>(RemoteWire.Json)), "a null pane is refused");
        var noLogs = Node("detail.json")["session"]!.AsObject(); noLogs.Remove("logs");
        Check(RemoteMirrorSupport.Read(noLogs) is null, "a session without its entries does not read");
        // What a Windows pane can do without is left out rather than the whole pane refused.
        var odd = Node("detail.json")["session"]!.AsObject(); odd["graphRuns"] = JsonNode.Parse("[{\"id\":5}]"); odd["todoProgress"] = JsonNode.Parse("{\"items\":7}");
        Check(RemoteMirrorSupport.Read(odd) is { GraphRuns.Count: 0, TodoProgress: null }, "a graph or checklist that does not read here is left out");
        return Task.CompletedTask;
    }

    private static bool Fails(Action action) { try { action(); return false; } catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return true; } }

    /// <summary>The Mac's clientIdsAreStablePerConnectionAndValidIdentifiers, with the Mac's own digests (SHA-256 of connection, NUL, host id).</summary>
    internal static Task ClientIdsAreTheMacsAndStablePerConnection()
    {
        var id = RemoteMirrorSupport.ClientId("conn-1", "pane-1");
        Check(id == "mirror-eb9714c66ef76323034182e28c6ad01f" && RemoteMirrorSupport.ClientId("conn-1", "ws-project") == "mirror-7525e887f2f07c8b09409bdf8509ed76", "the ids are the Mac's: " + id);
        Check(id == RemoteMirrorSupport.ClientId("conn-1", "pane-1") && id != RemoteMirrorSupport.ClientId("conn-2", "pane-1") && id != RemoteMirrorSupport.ClientId("conn-1", "pane-2"), "stable per connection and host id");
        Check(RemoteMirrorSupport.ClientId("a", "bc") != RemoteMirrorSupport.ClientId("ab", "c"), "the separator keeps two pairs apart");
        Check(id.StartsWith("mirror-", StringComparison.Ordinal) && id.Length == 39 && Wire.Identifier(id), "mirror- and 32 hex digits, a valid identifier");
        return Task.CompletedTask;
    }

    private static RunSession Pane(string id = "pane-1", string workspace = "ws", int logs = 3) => new()
    {
        Id = id, WorkspaceId = workspace, Title = "Host pane", Status = "running", Provider = "claude",
        Logs = Enumerable.Range(0, logs).Select(i => new LogEntry("log-" + i, i % 2 == 0 ? "user" : "assistant", "text " + i, "2026-11-13T01:00:0" + (i % 10) + ".1234567+00:00")).ToList(),
        RunTiming = new AgentRunTiming(new DateTimeOffset(2026, 11, 13, 1, 0, 1, 250, TimeSpan.Zero), new DateTimeOffset(2026, 11, 13, 1, 0, 9, TimeSpan.Zero)),
    };

    /// <summary>The host's detail is bounded as the phone's (80 entries, 20 runs, 32 requests, 16 queued of 500 characters, 1.5 MiB) and a revision moves whenever the detail does.</summary>
    internal static Task DetailsAreBoundedAndRevisionsMoveWithThem()
    {
        var request = new ToolPermissionRequest("perm-1", "pane-1", "toolu_1", "Bash", "{\"command\":\"ls\"}", "ls", ReceivedAt: "2026-11-13T01:00:05.1234567+00:00");
        var long_ = Pane(logs: 100) with
        {
            GraphRuns = Enumerable.Range(0, 25).Select(i => new MightyGraphRun { Id = "run-" + i, Input = "ask " + i, Status = "completed", Provider = "claude" }).ToList(),
            Logs = [.. Pane(logs: 99).Logs, new LogEntry("log-image", "image", "[picture]", "2026-11-13T01:00:00Z", Images: [new AgentImageRef("key", "image/png", 1, 1, 10, "tool")])],
        };
        var queued = Enumerable.Range(0, 20).Select(i => new RemoteMirrorQueued("queued-" + i, new string('q', 600))).ToList();
        var session = RemoteMirrorSupport.Session(long_, [request, request with { Id = "perm-2", State = "allowed" }], queued, "mighty/pane-1", "main");
        Check(session.Logs.Count == 80 && session.HasOlder && session.Logs[^1].Kind == "system" && session.Logs[^1].Images is null, "the newest 80 entries, a picture as its line without its reference");
        Check(session.GraphRuns.Count == 20 && session.GraphRuns[0].Id == "run-5", "the newest 20 runs");
        Check(session.Permissions.Select(p => p.Id).SequenceEqual(["perm-1"]), "only waiting requests travel");
        Check(session.Queued.Count == 16 && session.Queued.All(q => q.Text.Length == 500), "16 queued requests of 500 characters");
        Check(session is { Branch: "mighty/pane-1", Base: "main", StartedAt: "2026-11-13T01:00:01Z", FinishedAt: null } && session.CreatedAt.EndsWith('Z'), "the clock in whole seconds as the Mac's formatter writes it");
        var written = RemoteMirrorSupport.Write(session);
        Check(written["logs"]![0]!["timestamp"]!.GetValue<string>().EndsWith("Z", StringComparison.Ordinal) && written["permissions"]![0]!["receivedAt"]!.GetValue<string>() == "2026-11-13T01:00:05.123Z",
            "times inside travel in the Mac's forms: " + written["permissions"]![0]!.ToJsonString());
        Check(written["startedAt"]!.GetValue<string>() == "2026-11-13T01:00:01Z", "the session's own clock stays whole seconds (the Mac's startedAt reader takes no fraction)");
        // The budget: runs go first (one stays), then entries (one stays), then texts are cut, last the graph.
        var big = Pane(logs: 6) with { Logs = Enumerable.Range(0, 6).Select(i => new LogEntry("log-" + i, "assistant", new string('a', 100_000), "2026-11-13T01:00:01Z")).ToList(), GraphRuns = [new MightyGraphRun { Id = "run-a", Input = "x" }, new MightyGraphRun { Id = "run-b", Input = "y" }] };
        var fitted = RemoteMirrorSupport.Fitted(RemoteMirrorSupport.Session(big, [], []), 250_000);
        Check(fitted.GraphRuns.Count == 1 && fitted.GraphRuns[0].Id == "run-b" && fitted.Logs.Count == 2 && fitted.HasOlder, "the oldest runs and entries go first: " + fitted.Logs.Count + "/" + fitted.GraphRuns.Count);
        var cut = RemoteMirrorSupport.Fitted(RemoteMirrorSupport.Session(big, [], []), 40_000);
        Check(cut.Logs.Count == 1 && Encoding.UTF8.GetByteCount(cut.Logs[0].Text) == 32 * 1024 && cut.GraphRuns.Count == 1, "then the text is cut to 32 KiB, the graph kept while it fits");
        var tight = RemoteMirrorSupport.Fitted(RemoteMirrorSupport.Session(big, [], []), 20_000);
        Check(tight.Logs.Count == 1 && tight.GraphRuns.Count == 0, "and last the graph emptied");
        Check(RemoteMirrorSupport.Utf8Prefix("가나다", 7) == "가나", "a cut never splits a character");
        // A revision moves whenever the detail does, and stays while nothing changed.
        var pane = Pane();
        var revision = RemoteMirrorSupport.Revision(pane, [], [], null);
        Check(revision.Length == 24 && revision == RemoteMirrorSupport.Revision(pane with { }, [], [], null), "24 hex digits, equal for the same pane");
        foreach (var (what, other) in new (string, string)[]
        {
            ("a streamed answer", RemoteMirrorSupport.Revision(pane with { Logs = [.. pane.Logs.Take(2), pane.Logs[2] with { Text = pane.Logs[2].Text + "!" }] }, [], [], null)),
            ("the status", RemoteMirrorSupport.Revision(pane with { Status = "completed" }, [], [], null)),
            ("a request waiting", RemoteMirrorSupport.Revision(pane, [request], [], null)),
            ("a queued request", RemoteMirrorSupport.Revision(pane, [], [queued[0]], null)),
            ("the branch", RemoteMirrorSupport.Revision(pane, [], [], "mighty/x")),
            ("the settings", RemoteMirrorSupport.Revision(pane with { Settings = pane.Settings with { FastMode = true } }, [], [], null)),
            ("the clock", RemoteMirrorSupport.Revision(pane with { RunTiming = pane.RunTiming!.Finish() }, [], [], null)),
            ("the checklist", RemoteMirrorSupport.Revision(pane with { TodoProgress = new([new TodoItem("Read", "in_progress", Id: "1")]) }, [], [], null)),
        })
            Check(other != revision, what + " moves the revision");
        var workspaces = new List<RemoteSharedWorkspace> { new("ws", "project", "C:\\project") };
        var summary = new RemoteMirrorSummary("pane-1", "ws", "Host pane", "claude", "claude", "default", "running", revision, 0, 0);
        var list = RemoteMirrorSupport.StateRevision(workspaces, [summary]);
        Check(list != RemoteMirrorSupport.StateRevision(workspaces, [summary with { Revision = "other" }]) && list != RemoteMirrorSupport.StateRevision([workspaces[0] with { Name = "renamed" }], [summary]), "the list's revision moves with a pane's and a workspace's name");
        return Task.CompletedTask;
    }

    /// <summary>Mirrors live in the state while connected and never reach the state file; the 128-pane limit counts only this PC's panes.</summary>
    internal static async Task MirrorsAreNeverSavedAndOnlyThisPCsPanesCount()
    {
        var directory = Verification.Temp();
        try
        {
            var store = new StateStore(directory); await store.LoadAsync();
            var local = store.ApproveLocal(Directory.CreateDirectory(Path.Combine(directory, "project")).FullName);
            var mirror = new Workspace { Id = RemoteMirrorSupport.ClientId("conn", "ws-far"), Name = "far", Path = "/Users/example/far", Remote = new RemoteWorkspaceReference("conn", "ws-far", "studio-mac") { Mirror = true } };
            var reference = new RemoteMirrorReference("conn", "pane-far", "ws-far", "studio-mac");
            var own = Enumerable.Range(0, PaneLayout.MaximumSessions - 1).Select(i => new RunSession { Id = "own-" + i, WorkspaceId = local.Id }).ToList();
            var mirrored = Enumerable.Range(0, 40).Select(i => new RunSession { Id = RemoteMirrorSupport.ClientId("conn", "pane-" + i), WorkspaceId = mirror.Id, Title = "far " + i, Mirror = reference with { HostSessionId = "pane-" + i } }).ToList();
            var snapshot = new AppSnapshot { Workspaces = [local, mirror], Sessions = [.. own, .. mirrored], ActiveWorkspaceId = mirror.Id, ActiveSessionId = mirrored[0].Id, ExpandedWorkspaceIds = [local.Id, mirror.Id],
                PaneLayoutModes = new() { [mirror.Id] = "tabs" } };
            var normalized = StateStore.Normalize(snapshot, false);
            Check(normalized.Sessions.Count == own.Count + mirrored.Count && normalized.Sessions.Count(s => s.Mirror is not null) == 40 && normalized.Workspaces.Single(w => w.Id == mirror.Id).Remote!.Mirror,
                "the state holds the mirror's workspace and panes beside this PC's, flags and references kept: " + normalized.Sessions.Count);
            Check(PaneLayout.HasRoom(normalized) && normalized.OwnSessionCount() == 127, "the mirror's panes leave this PC's 128th pane free");
            Check(!PaneLayout.HasRoom(normalized with { Sessions = [.. normalized.Sessions, new RunSession { Id = "own-last", WorkspaceId = local.Id }] }), "this PC's 129th pane is refused");
            var stripped = snapshot.WithoutMirrors();
            Check(stripped.Workspaces.Select(w => w.Id).SequenceEqual([local.Id]) && stripped.Sessions.All(s => s.Mirror is null) && stripped.ActiveWorkspaceId == local.Id && stripped.ActiveSessionId is null
                && stripped.ExpandedWorkspaceIds!.SequenceEqual([local.Id]) && !stripped.PaneLayoutModes!.ContainsKey(mirror.Id), "without mirrors: workspaces, panes, layouts, expansion and active ids");
            Check(ReferenceEquals(stripped.WithoutMirrors(), stripped), "a snapshot without mirrors is itself");
            await store.SaveAsync(snapshot);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json"));
            Check(!text.Contains("mirror-", StringComparison.Ordinal) && !text.Contains("studio-mac", StringComparison.Ordinal) && !text.Contains("\"mirror\"", StringComparison.Ordinal), "the state file holds nothing of the mirror");
            var reloaded = new StateStore(directory); var loaded = await reloaded.LoadAsync();
            Check(loaded.Workspaces.Select(w => w.Id).SequenceEqual([local.Id]) && loaded.Sessions.Count == own.Count, "a restart has no mirror");
            Check(JsonSerializer.Serialize(mirrored[0], Wire.Json).Contains("pane-far", StringComparison.Ordinal) is false && JsonSerializer.Serialize(mirror, Wire.Json).Contains("\"mirror\"", StringComparison.Ordinal) is false, "the references are never in the JSON");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>The host's panes as a check hands them over: one shared workspace's two panes and one private pane, each recorded when acted on.</summary>
    private sealed class FakeMirrorHost(string shared, string hidden) : IRemoteMirrorHost
    {
        internal readonly ConcurrentQueue<string> Actions = new();
        internal string Mode = "manual", Provider = "claude";
        internal Exception? Refuse;
        private RemoteMirrorSummary Summary(string id, string workspace) => new(id, workspace, "Pane " + id, "claude", id == "pane-2" ? Provider : "claude", "default", "idle", "rev-" + id + Mode, 0, 0);
        public Task<IReadOnlyList<RemoteMirrorSummary>> SummariesAsync(IReadOnlySet<string> workspaceIds) =>
            Task.FromResult<IReadOnlyList<RemoteMirrorSummary>>(new[] { Summary("pane-1", shared), Summary("pane-2", shared), Summary("pane-hidden", hidden) }.Where(s => workspaceIds.Contains(s.WorkspaceId)).ToList());
        public Task<RemoteMirrorPane?> DetailAsync(string sessionId, IReadOnlySet<string> workspaceIds) =>
            Task.FromResult<RemoteMirrorPane?>(sessionId == "pane-hidden" ? null : new("rev-" + sessionId, RemoteMirrorSupport.Session(Pane(sessionId, shared), [], [])));
        public Task<RemoteMirrorPaneInfo?> PaneInfoAsync(string sessionId) => Task.FromResult<RemoteMirrorPaneInfo?>(sessionId switch
        {
            "pane-1" => new(shared, Mode, "claude"),
            "pane-2" => new(shared, "manual", Provider),
            "pane-hidden" => new(hidden, "manual", "claude"),
            _ => null,
        });
        private Task Did(string what) { if (Refuse is { } refusal) throw refusal; Actions.Enqueue(what); return Task.CompletedTask; }
        public async Task<string> SubmitAsync(string sessionId, string text, IReadOnlyList<RunAttachment> attachments, CancellationToken token) { await Did($"submit {sessionId} {text} {attachments.Count}"); return "queued"; }
        public Task StopAsync(string sessionId, CancellationToken token) => Did("stop " + sessionId);
        public Task CloseAsync(string sessionId, CancellationToken token) => Did("close " + sessionId);
        public Task PermissionAsync(string sessionId, string requestId, string runId, bool allow, bool always, CancellationToken token) => Did($"permission {sessionId} {requestId} {runId} {allow} {always}");
        public Task AnswersAsync(string sessionId, string requestId, string runId, IReadOnlyDictionary<string, UserQuestionAnswer> answers, CancellationToken token) => Did($"answers {sessionId} {requestId} {string.Join(",", answers.Keys)}");
        public Task PlanAsync(string sessionId, string requestId, string runId, PlanDecision decision, CancellationToken token) => Did($"plan {sessionId} {requestId} {decision.Kind}");
        public async Task<string> CreateSessionAsync(string workspaceId, string provider, CancellationToken token) { await Did($"create {workspaceId} {provider}"); return "pane-3"; }
    }

    private static async Task<(int Status, JsonNode? Body)> Ask(RemoteTarget target, string key, HttpMethod method, string path, string? body = null)
    {
        try
        {
            var data = await RemoteTransport.RequestAsync(target, key, method, path, body is null ? null : Encoding.UTF8.GetBytes(body));
            return (200, JsonNode.Parse(data));
        }
        catch (RemoteHttpFailure failure) { return (failure.Status, JsonNode.Parse(JsonSerializer.Serialize(new { error = failure.Message }))); }
    }

    /// <summary>
    /// The host's mirror routes over loopback (the Mac's serveMirror): only the shared workspaces' agent panes, since and unchanged,
    /// 404 for a pane outside them, the switch's 403s (full access, a Windows host's Codex and Antigravity, always), 404 before the app
    /// hands its panes over, the bodies read strictly, a send's 12 MiB body.
    /// </summary>
    internal static async Task TheHostServesOnlySharedAgentPanes()
    {
        await using var fixture = await RemoteHostVerification.MakeHost(windowsHost: true);
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var target = RemoteClientVerification.Loopback(state.Port!.Value); var key = state.Key!;
        var before = await Ask(target, key, HttpMethod.Get, "/v2/mirror");
        Check(before.Status == 404 && before.Body!["error"]!.GetValue<string>() == Locale.Get("remote.mirror.error.unavailable"), "no mirror before the app hands its panes over: " + before.Status);
        var host = new FakeMirrorHost(fixture.Shared.Id, fixture.Private.Id);
        fixture.Host.SetMirror(host);
        var list = await Ask(target, key, HttpMethod.Get, "/v2/mirror");
        var listed = list.Body!.Deserialize<RemoteWireMirrorState>(RemoteWire.Json)!;
        Check(list.Status == 200 && RemoteMirrorSupport.Valid(listed) && listed.HostName == "studio-pc" && listed.Workspaces.Select(w => w.Id).SequenceEqual([fixture.Shared.Id]) && listed.Sessions.Select(s => s.Id).SequenceEqual(["pane-1", "pane-2"]),
            "only the shared workspace and its panes are listed: " + list.Body!.ToJsonString());
        var same = await Ask(target, key, HttpMethod.Get, "/v2/mirror?since=" + listed.Revision);
        Check(same.Status == 200 && same.Body!["unchanged"]!.GetValue<bool>() && same.Body["sessions"]!.AsArray().Count == 0 && same.Body["revision"]!.GetValue<string>() == listed.Revision, "a current since is answered unchanged with empty lists");
        Check((await Ask(target, key, HttpMethod.Get, "/v2/mirror?since=00")).Body!["sessions"]!.AsArray().Count == 2, "an old since gets the whole list");
        foreach (var query in new[] { "?since=XYZ", "?since=" + new string('a', 65), "?other=1", "?since=ab?x=1" })
            Check((await Ask(target, key, HttpMethod.Get, "/v2/mirror" + query)).Status == 400, "a list query that is not one since in hex is refused: " + query);
        var detail = await Ask(target, key, HttpMethod.Get, "/v2/mirror/sessions/pane-1");
        var read = detail.Body!.Deserialize<RemoteWireMirrorDetail>(RemoteWire.Json)!;
        Check(detail.Status == 200 && RemoteMirrorSupport.Valid(read, RemoteMirrorSupport.Read(read.Session), "pane-1"), "a shared pane's detail passes the client's checks");
        foreach (var path in new[] { "/v2/mirror/sessions/pane-hidden", "/v2/mirror/sessions/nope", "/v2/mirror/sessions/pane-hidden/stop" })
            Check((await Ask(target, key, path.EndsWith("stop", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, path, "{}")).Status == 404, "a pane outside the shared workspaces is unknown: " + path);
        Check((await Ask(target, key, HttpMethod.Get, "/v2/mirror/sessions/pane-1?x=1")).Status == 400, "no query on a pane's route");
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/rename", "{}")).Status == 404, "an unknown action is no route");

        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", "{\"text\":\"Then update CHANGELOG.md\"}")).Body!["outcome"]!.GetValue<string>() == "queued", "a send answers the host's word");
        var withFile = await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", Node("submit.json").ToJsonString());
        Check(withFile.Status == 200 && host.Actions.Contains("submit pane-1 Then update CHANGELOG.md 1"), "a send carries its files: " + string.Join(" | ", host.Actions));
        foreach (var bad in new[] { "{\"text\":\"  \"}", "{\"text\":\"a\\u0000b\"}", "{}", "{\"text\":\"" + new string('x', 400_001) + "\",\"attachments\":[" + Node("submit.json")["attachments"]![0]!.ToJsonString() + "]}" })
            Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", bad)).Status == 400, "a send outside the rules is refused: " + bad[..Math.Min(bad.Length, 40)]);
        var badFile = Node("submit.json").AsObject(); badFile["attachments"]![0]!["mediaType"] = "image/png";
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", badFile.ToJsonString())).Status == 400, "a file whose type is not what it says is refused");
        host.Mode = "fullAccess";
        var full = await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", "{\"text\":\"hi\"}");
        Check(full.Status == 403 && full.Body!["error"]!.GetValue<string>() == Locale.Get("remote.host.error.fullAccessOff"), "a full-access pane waits for the switch: " + full.Status);
        host.Provider = "codex";
        var codex = await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-2/submit", "{\"text\":\"hi\"}");
        Check(codex.Status == 403 && codex.Body!["error"]!.GetValue<string>() == RemoteWindowsStrings.HostAgentNeedsFullAccess("codex"), "a Windows host's Codex pane waits for the switch: " + codex.Status);
        Check(RemoteHostService.MirrorRefusal(new("ws", "manual", "gemini"), false, windowsHost: true) == RemoteWindowsStrings.HostAgentNeedsFullAccess("gemini")
            && RemoteHostService.MirrorRefusal(new("ws", "manual", "codex"), false, windowsHost: false) is null && RemoteHostService.MirrorRefusal(new("ws", "acceptEdits", "claude"), false, windowsHost: true) is null,
            "Antigravity on a Windows host waits too; a Mac host confines them, and Claude keeps its modes");
        var always = await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/permission", "{\"requestId\":\"perm-1\",\"runId\":\"pane-1\",\"allow\":true,\"always\":true}");
        Check(always.Status == 403 && always.Body!["error"]!.GetValue<string>() == Locale.Get("remote.host.error.antigravityAlways"), "항상 허용 waits for the switch");
        fixture.Host.SetAllowsFullAccess(true);
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", "{\"text\":\"full\"}")).Status == 200 && (await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-2/submit", "{\"text\":\"codex\"}")).Status == 200, "the switch lets them run");
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/permission", Node("permission.json").ToJsonString())).Status == 200
            && (await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/permission", "{\"requestId\":\"perm-5\",\"runId\":\"pane-1\",\"allow\":true,\"always\":true}")).Status == 200, "answers reach the app");
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/questions", Node("questions.json").ToJsonString())).Status == 200, "a questions answer reaches the app");
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/plan", Node("plan.json").ToJsonString())).Status == 200, "a plan answer reaches the app");
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/plan", "{\"runId\":\"j\",\"requestId\":\"r\",\"outcome\":\"revised\"}")).Status == 400, "a revise without words is refused");
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/stop", "")).Status == 200 && (await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/close", "{}")).Status == 200, "stop and close read no body");
        var created = await Ask(target, key, HttpMethod.Post, $"/v2/mirror/workspaces/{fixture.Shared.Id}/sessions", Node("create.json").ToJsonString());
        Check(created.Status == 200 && created.Body!["sessionId"]!.GetValue<string>() == "pane-3", "a new pane in a shared workspace answers its id");
        Check((await Ask(target, key, HttpMethod.Post, $"/v2/mirror/workspaces/{fixture.Private.Id}/sessions", "{\"provider\":\"claude\"}")).Status == 403, "a workspace not shared takes no new pane");
        Check((await Ask(target, key, HttpMethod.Post, $"/v2/mirror/workspaces/{fixture.Shared.Id}/sessions", "{\"provider\":\"other\"}")).Status == 400, "an unknown agent is refused");
        Check(host.Actions.Contains("permission pane-1 perm-4 job-1 True False") && host.Actions.Contains("permission pane-1 perm-5 pane-1 True True") && host.Actions.Contains("answers pane-1 perm-2 Which file?")
            && host.Actions.Contains("plan pane-1 perm-3 revise") && host.Actions.Contains("stop pane-1") && host.Actions.Contains("close pane-1") && host.Actions.Contains($"create {fixture.Shared.Id} claude"),
            "every action reached the app with the client's ids: " + string.Join(" | ", host.Actions));
        host.Refuse = new InvalidOperationException("Request is no longer pending.");
        var refused = await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/stop", "{}");
        Check(refused.Status == 409 && refused.Body!["error"]!.GetValue<string>() == "Request is no longer pending.", "what the app refuses is a conflict with its words");
        // The 12 MiB body only for a send, decided from the head.
        var head = new Dictionary<string, string> { ["authorization"] = "Bearer " + key, ["x-mighty-remote-version"] = "2" };
        Check(RemoteHostService.BodyLimit(new("POST", "/v2/mirror/sessions/pane-1/submit", head, [], "127.0.0.1"), key, true) == AttachmentSupport.MaximumRequestBytes
            && RemoteHostService.BodyLimit(new("POST", "/v2/mirror/sessions/pane-1/stop", head, [], "127.0.0.1"), key, true) == RemoteProtocol.MaximumRequestBytes
            && RemoteHostService.BodyLimit(new("POST", "/v2/mirror/sessions/bad id/submit", head, [], "127.0.0.1"), key, true) == RemoteProtocol.MaximumRequestBytes, "only a send gets the attachment body");
        Check(RemoteMirrorRoutes.IsSubmit("/v2/mirror/sessions/pane-1/submit") && !RemoteMirrorRoutes.IsSubmit("/v2/mirror/sessions/pane-1/submit?x") && !RemoteMirrorRoutes.IsSubmit("/v2/mirror/workspaces/a/submit"), "the send's route is exactly it");
        // Over 512 KiB without files: refused even though the listener read it.
        var padded = "{\"text\":\"" + new string('x', 390_000) + "\",\"pad\":\"" + new string(' ', 200_000) + "\"}";
        Check((await Ask(target, key, HttpMethod.Post, "/v2/mirror/sessions/pane-1/submit", padded)).Status is 400 or 413, "a body past 512 KiB without files is refused");
        fixture.Host.SetMirror(null);
        Check((await Ask(target, key, HttpMethod.Get, "/v2/mirror")).Status == 404, "taken back, the mirror is gone");
    }

    /// <summary>
    /// A Windows client mirrors a Windows host end to end: connecting adds the shared workspace and its agent panes (deterministic ids,
    /// never the private workspace's, never a command pane), the detail of the pane looked at comes with its waiting request as a card,
    /// sends, answers, stops, a new pane and a close go to the host through the phone's handlers, nothing reaches the state file, and a
    /// disconnect stops the mirror while the panes stay.
    /// </summary>
    internal static async Task AWindowsClientMirrorsAWindowsHost()
    {
        var root = Verification.Temp();
        try
        {
            // The host: this PC's own panes, the phone's router with a desktop action that records what it is asked.
            await using var hostService = new DesktopService(Path.Combine(root, "host"), null, root, delegationEnabled: () => false); await hostService.InitializeAsync();
            var shared = await hostService.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            var hidden = await hostService.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(root, "private")).FullName);
            await hostService.UpdateAsync(s => s with { Sessions = [Pane("host-1", shared.Id) with { Status = "idle", RunTiming = null }, new RunSession { Id = "host-cmd", WorkspaceId = shared.Id, Kind = "shell" }, new RunSession { Id = "host-hidden", WorkspaceId = hidden.Id }] });
            var actions = new ConcurrentQueue<string>();
            async Task<object?> Act(MobileDesktopAction action, CancellationToken token)
            {
                actions.Enqueue(action.Action + " " + action.SessionId + " " + (action.Body.ValueKind == JsonValueKind.Object ? action.Body.Text("text") ?? action.Body.Text("provider") : null));
                switch (action.Action)
                {
                    case "submit": return new SubmitReceipt("queued");
                    case "create":
                        var id = "host-new";
                        await hostService.UpdateAsync(s => s with { Sessions = [.. s.Sessions, new RunSession { Id = id, WorkspaceId = action.SessionId, Provider = action.Body.Text("provider") ?? "claude" }] });
                        return id;
                    case "close": await hostService.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != action.SessionId).ToList() }); return null;
                    default: return null;
                }
            }
            using var router = new MobileDesktopRouter(hostService, Path.Combine(root, "mobile"), "Fixture", Act, _ => new([]));
            var queued = new ConcurrentDictionary<string, IReadOnlyList<RemoteMirrorQueued>>();
            var options = hostService.HostOptions("studio-pc", "1.2.3", _ => null, _ => { }, allowLoopbackForTests: true) with { Runtime = () => Task.FromResult(RemoteWireFixtureVerification.Node("info.json")["runtime"].Deserialize<RuntimeInfo>(Wire.Json)!) };
            await using var host = new RemoteHostService(options);
            host.SetMirror(new RemoteMirrorDesktopHost(hostService, () => router, id => queued.GetValueOrDefault(id) ?? [], work => work()));
            var sharing = await host.StartAsync([shared.Id], 0);

            // The client.
            await using var service = new DesktopService(Path.Combine(root, "client"), null, root, delegationEnabled: () => false) { MirrorLiveInterval = TimeSpan.FromMilliseconds(100), MirrorIdleInterval = TimeSpan.FromMilliseconds(200) };
            await service.InitializeAsync();
            var near = await service.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(root, "near")).FullName);
            var client = service.AttachRemote(Path.Combine(root, "client"), new MemoryRemoteKeyVault(), new() { AllowLoopbackForTests = true });
            var permissions = new ConcurrentQueue<ToolPermissionRequest>(); service.ToolPermissionChanged += permissions.Enqueue;
            var info = await client.ConnectAsync("", "127.0.0.1:" + sharing.Port, sharing.Key!);
            var workspaceId = RemoteMirrorSupport.ClientId(info.Id, shared.Id); var paneId = RemoteMirrorSupport.ClientId(info.Id, "host-1");
            await Verification.Until(() => service.Snapshot.Sessions.Any(s => s.Id == paneId), 10000);
            var state = service.Snapshot;
            var workspace = state.Workspaces.Single(w => w.Id == workspaceId);
            Check(workspace.Remote is { Mirror: true } remote && remote.ConnectionId == info.Id && remote.WorkspaceId == shared.Id && remote.HostName == "studio-pc" && workspace.Path == shared.Path, "connecting adds the shared workspace as a mirror");
            Check(state.Sessions.Where(s => s.Mirror is not null).Select(s => s.Id).SequenceEqual([paneId]) && state.Sessions.Single(s => s.Id == paneId).Mirror == new RemoteMirrorReference(info.Id, "host-1", shared.Id, "studio-pc"),
                "only the shared workspace's agent pane, under its deterministic id: " + string.Join(",", state.Sessions.Select(s => s.Id)));
            Check(service.MirrorLink(info.Id) is { Phase: "live" } && service.IsMirrorWorkspace(workspaceId) && service.IsRemoteWorkspace(workspaceId), "the mirror is live and its workspace a remote one");

            // A waiting request on the host comes with the pane's detail once the user looks at it.
            var request = new ToolPermissionRequest("perm-1", "host-1", "toolu_1", "Bash", "{\"command\":\"ls\"}", "ls", ReceivedAt: "2026-11-13T01:00:05.000Z");
            RaisePermission(hostService, request);
            queued["host-1"] = [new RemoteMirrorQueued("queued-1", "Then update CHANGELOG.md")];
            await hostService.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == "host-1" ? p with { Status = "running", Logs = [.. p.Logs, new LogEntry("log-new", "assistant", "Reading the file.", Wire.Now())] } : p).ToList() });
            await service.UpdateAsync(s => s with { ActiveWorkspaceId = workspaceId, ActiveSessionId = paneId });
            await Verification.Until(() => permissions.Any(p => p.RunId == paneId && p.Id == "perm-1" && p.State == "pending") && service.Snapshot.Sessions.Single(s => s.Id == paneId).Logs.Any(l => l.Id == "log-new"), 10000);
            var mirrored = service.Snapshot.Sessions.Single(s => s.Id == paneId);
            Check(mirrored.Status == "running" && service.MirrorCounts(paneId) is { Pending: 1, Queued: 1 } counts && counts.QueuedTexts!.SequenceEqual(["Then update CHANGELOG.md"]), "the detail, the waiting request and the queue are the pane's");

            // Actions go to the host through the phone's handlers.
            // The answer goes to the host's phone handler, which refuses it there (the fixture's pane runs nothing): its words come back.
            var failures = new ConcurrentQueue<(string Pane, string Message)>(); service.MirrorFailed += (pane, message) => failures.Enqueue((pane, message));
            service.RespondToToolPermission(paneId, "perm-1", true);
            await Verification.Until(() => failures.Any(f => f.Pane == paneId && f.Message == "Request is no longer pending."), 5000);
            Check(await service.MirrorSubmitAsync(paneId, "Then update CHANGELOG.md", []) == "queued" && actions.Contains("submit host-1 Then update CHANGELOG.md"), "a send is the host pane's next request: " + string.Join(" | ", actions));
            await service.StartAsync(new StartRunRequest(paneId, workspaceId, "claude", "via start", []));
            Check(actions.Contains("submit host-1 via start") && !service.IsSessionRunning(paneId), "a start of a mirrored pane is a send to its host, nothing runs here");
            await service.StopAsync(paneId);
            Check(actions.Any(a => a.StartsWith("stop host-1", StringComparison.Ordinal)), "a stop reaches the host");
            var made = await service.CreateMirrorPaneAsync(workspaceId, "claude", "codex");
            Check(made == RemoteMirrorSupport.ClientId(info.Id, "host-new") && actions.Contains($"create {shared.Id} codex"), "a new pane is made on the host and shows here: " + made);
            await RemoteClientVerification.RejectAsync(() => service.CreateMirrorPaneAsync(workspaceId, "shell", "claude"), "no command pane on the host", ex => ex.Message == Locale.Get("remote.mirror.error.commandPane"));
            await service.CloseMirrorPaneAsync(made!);
            Check(actions.Any(a => a.StartsWith("close host-new", StringComparison.Ordinal)) && !service.Snapshot.Sessions.Any(s => s.Id == made), "a close closes the host's pane and it leaves here");
            await service.RenameSessionAsync(paneId, "mine now");
            Check(service.Snapshot.Sessions.Single(s => s.Id == paneId).Title != "mine now", "a mirrored pane keeps its host's name");

            // Nothing of the mirror reaches the client's state file.
            await service.UpdateAsync(s => s);
            await Verification.Until(() => service.SaveStatus == "saved");
            var saved = await File.ReadAllTextAsync(Path.Combine(root, "client", "workspace-state.json"));
            Check(saved.Contains(near.Id, StringComparison.Ordinal) && !saved.Contains(workspaceId, StringComparison.Ordinal) && !saved.Contains(paneId, StringComparison.Ordinal) && !saved.Contains("Reading the file.", StringComparison.Ordinal),
                "the state file holds this PC's workspace and nothing of the host's");

            // A disconnect stops the mirror; the panes stay as last shown and sends wait for a new connect.
            await client.DisconnectAsync(info.Id);
            await Verification.Until(() => service.MirrorLink(info.Id) is { Phase: "off" });
            Check(service.Snapshot.Sessions.Any(s => s.Id == paneId) && service.MirrorBlockedReason(service.MirrorReference(paneId)!) == Locale.Get("remote.mirror.error.disconnected", new Dictionary<string, string> { ["host"] = "studio-pc" }),
                "the panes stay and say their computer is not connected");
            await RemoteClientVerification.RejectAsync(() => service.MirrorSubmitAsync(paneId, "late", []), "a send waits for a new connect");
            // Removing the connection takes its mirror away.
            await client.RemoveAsync(info.Id);
            await Verification.Until(() => !service.Snapshot.Workspaces.Any(w => w.Id == workspaceId) && !service.Snapshot.Sessions.Any(s => s.Id == paneId));
            Check(permissions.Any(p => p.Id == "perm-1" && p.RunId == paneId && p.State != "pending"), "its cards are withdrawn");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A tool request of the host's own pane, as its runner would report it (the phone's router keeps it).</summary>
    private static void RaisePermission(DesktopService service, ToolPermissionRequest request)
    {
        var field = typeof(DesktopService).GetField("ToolPermissionChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        (field.GetValue(service) as Action<ToolPermissionRequest>)?.Invoke(request);
    }

    /// <summary>A host that answers the mirror's list 404 (an older Mighty Code) leaves the connection up, stops the mirror and says so; the button falls back to 열기.</summary>
    internal static async Task AnOlderHostStopsTheMirrorOnly()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var sharing = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var root = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(root, "client"), null, root, delegationEnabled: () => false); await service.InitializeAsync();
            var client = service.AttachRemote(Path.Combine(root, "client"), new MemoryRemoteKeyVault(), new() { AllowLoopbackForTests = true });
            var info = await client.ConnectAsync("", "127.0.0.1:" + sharing.Port, sharing.Key!);
            await Verification.Until(() => service.MirrorLink(info.Id) is { Phase: "off" }, 10000);
            var link = service.MirrorLink(info.Id)!;
            Check(link.Unsupported && link.Error == Locale.Get("remote.mirror.error.olderHost", new Dictionary<string, string> { ["host"] = info.Name }) && client.Connection(info.Id)!.Status == RemoteConnectionStatus.Connected,
                "the mirror stops with the older-host words and the connection stays: " + link);
            Check(!service.Snapshot.Workspaces.Any(w => w.Remote?.Mirror == true), "no mirror workspace");
            var opened = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Shared.Id);
            Check(opened.Remote is { Mirror: false }, "열기 still opens the host's workspace for this PC's own remote panes");
        }
        finally { Directory.Delete(root, true); }
    }
}
