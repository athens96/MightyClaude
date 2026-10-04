using System.Collections.Concurrent;
using System.Text.Json;
using MightyClaude.Core;

internal static class CodexApprovalVerification
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Stale or unsupported consent was accepted."); }
    private static string Json(object value) => JsonSerializer.Serialize(value, Wire.Json);
    private static StartRunRequest Request(string? resume = null) => new("codex-pane", "workspace", "claude", "fixture prompt", [], Provider: "codex", Settings: new(PermissionMode: "onRequest", NetworkAccess: true), ResumeId: resume);
    private sealed class Fixture
    {
        internal readonly List<string> Writes = [], Events = [], Failures = [], Warnings = [];
        internal readonly List<ToolPermissionRequest> Requests = [];
        internal bool Done;
        internal readonly CodexApprovalChannel Channel;
        internal Fixture(string? resume = null) => Channel = new(Request(resume), "C:\\work", "fixture prompt", [], Writes.Add, Events.Add, Requests.Add, (_, _) => { }, Warnings.Add, Failures.Add, () => Done = true);
        internal JsonElement Last => JsonDocument.Parse(Writes[^1]).RootElement.Clone();
        internal void Reply(object result) => Channel.Receive(Json(new { id = Last.GetProperty("id").Clone(), result }));
        internal void Ready()
        {
            Channel.Start(); Reply(new { }); Reply(new { thread = new { id = "thread-1" } }); Reply(new { turn = new { id = "turn-1" } });
        }
        internal void Ask(int id = 1, string thread = "thread-1", string turn = "turn-1", string kind = "command", object? decisions = null) => Channel.Receive(Json(new { id, method = "item/commandExecution/requestApproval", @params = new { threadId = thread, turnId = turn, itemId = "item-1", command = "git fetch origin", cwd = "C:\\work", kind, availableDecisions = decisions } }));
        internal void Notice(string method, object parameters) => Channel.Receive(Json(new { method, @params = parameters }));
    }
    internal static Task SettingsAndHandshake()
    {
        var request = Request().Validate();
        Check(ProviderCatalog.NormalizeSettings("codex", request.Settings!) == request.Settings, "onRequest and network setting must survive restore");
        Check(!ProviderCatalog.Capabilities("codex", "0.153.3").PermissionModes.Contains("onRequest") && !ProviderCatalog.Capabilities("codex", "0.153.4-preview").PermissionModes.Contains("onRequest"), "old/prerelease CLIs do not advertise approval support");
        Check(ProviderCatalog.Capabilities("codex", "0.153.4").PermissionModes.Contains("onRequest"), "stable supported CLI advertises approvals");
        Check(ProviderCatalog.PermissionModes("codex").Contains("onRequest"), "Codex menu advertises approvals");
        foreach (var provider in new[] { "claude", "gemini" })
        {
            var rejected = false; try { (request with { Provider = provider }).Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Only Codex accepts onRequest");
        }
        var arguments = ProviderCatalog.Arguments(request, "unused");
        Check(arguments.Contains("app-server") && arguments.Contains("stdio://") && !arguments.Contains("exec") && !arguments.Contains("-"), "approval runs use app-server, not exec");
        Check(arguments.Contains("approval_policy=\"on-request\"") && arguments.Contains("sandbox_mode=\"workspace-write\""), "workspace sandbox and approval policy are explicit");
        Check(ProviderCatalog.Arguments(request with { Settings = new(PermissionMode: "manual") }, "unused").Contains("approval_policy=\"never\""), "manual retains its previous boundary");
        var fixture = new Fixture(); fixture.Channel.Start(); fixture.Channel.Start();
        Check(fixture.Writes.Count == 1 && fixture.Last.Text("method") == "initialize", "start writes one handshake only");
        fixture.Reply(new { });
        Check(fixture.Last.Text("method") == "thread/start", "thread starts after initialization");
        fixture.Reply(new { thread = new { id = "thread-1" } });
        var turn = fixture.Last.GetProperty("params");
        Check(turn.Text("approvalPolicy") == "on-request" && turn.Text("approvalsReviewer") == "user", "the user remains approval reviewer");
        Check(turn.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean(), "network permission reaches the actual turn");
        Check(turn.GetProperty("sandboxPolicy").GetProperty("writableRoots")[0].GetString() == "C:\\work", "the only writable root is the current workspace");
        fixture.Reply(new { turn = new { id = "turn-1" } });
        Check(fixture.Channel.Initialized && !fixture.Done, "initialization is not completion");
        return Task.CompletedTask;
    }
    internal static Task ApprovalScopeAndOnceOnly()
    {
        var f = new Fixture(); f.Ready(); f.Ask(thread: "other");
        Check(f.Requests.Count == 0 && f.Last.TryGetProperty("error", out _), "cross-thread approval is rejected");
        f.Ask(2, turn: "old-turn"); Check(f.Requests.Count == 0, "stale turn is rejected");
        f.Ask(3, decisions: new[] { "accept", "decline", "acceptForSession" });
        var ask = f.Requests.Single(); Check(ask.CanAllow && ask.InputJson.Contains("git fetch origin"), "the complete command is displayed before consent");
        f.Channel.Respond(ask.Id, true);
        Check(f.Last.GetProperty("result").Text("decision") == "accept", "allow grants only this call");
        Reject(() => f.Channel.Respond(ask.Id, true));
        f.Ask(4, kind: "network"); Check(!f.Requests[^1].CanAllow, "network bundles cannot become one-time command grants");
        Reject(() => f.Channel.Respond(f.Requests[^1].Id, true));
        f.Channel.Respond(f.Requests[^1].Id, false);
        f.Ask(5, decisions: new[] { "acceptForSession", "decline" }); Check(!f.Requests[^1].CanAllow, "session grant is never treated as accept");
        f.Channel.CancelAll(); Reject(() => f.Channel.Respond(f.Requests[^1].Id, true));
        return Task.CompletedTask;
    }
    internal static Task FileChangesRequireCompleteDiff()
    {
        var f = new Fixture(); f.Ready();
        f.Notice("item/started", new { threadId = "thread-1", turnId = "turn-1", item = new { id = "file-1", type = "fileChange", changes = new[] { new { path = "C:\\work\\hello.txt", diff = "+hello", kind = new { type = "add" } } } } });
        void Ask(int id, string? grantRoot = null) => f.Channel.Receive(Json(new { id, method = "item/fileChange/requestApproval", @params = new { threadId = "thread-1", turnId = "turn-1", itemId = "file-1", grantRoot } }));
        Ask(1); Check(f.Requests[^1].CanAllow && f.Requests[^1].InputJson.Contains("hello"), "the exact previously observed diff is displayed");
        f.Channel.Respond(f.Requests[^1].Id, true);
        Ask(2, "C:\\"); Check(!f.Requests[^1].CanAllow, "grantRoot cannot grant lasting directory access");
        f.Notice("item/completed", new { threadId = "thread-1", turnId = "turn-1", item = new { id = "file-1", type = "fileChange", changes = Array.Empty<object>() } });
        Check(f.Requests[^1].State == "cancelled", "completed items cancel stale approvals");
        Ask(3); Check(!f.Requests[^1].CanAllow, "a missing diff cannot be approved");
        return Task.CompletedTask;
    }
    internal static Task CancellationMalformedAndBounds()
    {
        var f = new Fixture(); f.Ready(); f.Ask();
        f.Notice("serverRequest/resolved", new { threadId = "thread-1", requestId = 1 });
        Check(f.Requests[^1].State == "cancelled", "server resolution removes the pending question");
        f.Ask(); Check(f.Channel.Failed, "duplicate ids fail closed");
        var eof = new Fixture(); eof.Ready(); eof.Channel.Flush(); Check(eof.Channel.Failed && !eof.Done, "EOF is not success");
        var timeout = new Fixture(); timeout.Channel.Start(); timeout.Channel.InitializationTimedOut(); Check(timeout.Channel.Failed, "handshake timeout fails closed");
        var bad = new Fixture(); bad.Channel.Receive("[]"); Check(bad.Channel.Failed, "malformed envelope fails closed");
        var resume = new Fixture("expected-thread"); resume.Channel.Start(); resume.Reply(new { }); resume.Reply(new { thread = new { id = "other-thread" } }); Check(resume.Channel.Failed, "resume cannot silently choose another thread");
        var many = new Fixture(); many.Ready(); for (int id = 1; id <= 17; id++) many.Ask(id);
        Check(many.Requests.Count == CodexApprovalChannel.MaximumPending && many.Last.GetProperty("result").Text("decision") == "decline", "pending approvals are bounded");
        Check(CodexApprovalChannel.AbsolutePath("C:\\work") && !CodexApprovalChannel.AbsolutePath("C:work"), "drive-relative paths are not full command working directories");
        return Task.CompletedTask;
    }
    internal static Task CompletionAndLegacyEvents()
    {
        var f = new Fixture(); f.Ready();
        f.Notice("item/completed", new { threadId = "thread-1", turnId = "turn-1", item = new { id = "msg", type = "agentMessage", text = "result" } });
        Check(f.Events.Any(e => e.Contains("agent_message") && e.Contains("result")), "agent message reaches the existing transcript parser");
        f.Notice("item/started", new { threadId = "thread-1", turnId = "turn-1", item = new { id = "spawn", type = "collabAgentToolCall", tool = "spawnAgent", senderThreadId = "thread-1", receiverThreadIds = new[] { "child" }, status = "inProgress" } });
        Check(f.Events.Any(e => e.Contains("spawn_agent") && e.Contains("receiver_thread_ids")), "graph receives normalized collaboration items");
        f.Notice("thread/tokenUsage/updated", new { threadId = "thread-1", turnId = "turn-1", tokenUsage = new { last = new { inputTokens = 12, outputTokens = 4 } } });
        f.Notice("turn/completed", new { threadId = "thread-1", turn = new { id = "other", status = "completed" } }); Check(!f.Done, "another turn cannot complete this run");
        f.Notice("turn/completed", new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } });
        f.Channel.Flush(); Check(f.Done && f.Channel.TurnCompleted && !f.Channel.Failed, "a matching completed turn settles cleanly");
        return Task.CompletedTask;
    }
    internal static async Task ActualRunManagerProtocol()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-codex-approval-test-").FullName;
        try
        {
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--codex-approval-fixture")));
            var workspace = new Workspace { Id = "workspace", Path = directory, Name = "Fixture" };
            var final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new ConcurrentQueue<ToolPermissionRequest>(); var logs = new ConcurrentQueue<RunEvent>();
            RunManager? manager = null;
            manager = new RunManager(_ => Task.FromResult(workspace), catalog, "unused", value => { logs.Enqueue(value); if (value.Type == "status" && value.Status is "completed" or "error" or "stopped") final.TrySetResult(value.Status); }, value => { requests.Enqueue(value); if (value.State == "pending") manager!.RespondToToolPermission("codex-pane", value.Id, true); });
            await using (manager)
            {
                await manager.StartAsync(Request());
                var state = await final.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Check(state == "completed", "real child app-server exchange must complete: " + string.Join(" / ", logs.Where(e => e.Entry is not null).Select(e => e.Entry!.Text)));
                Check(requests.Any(r => r.State == "allowed"), "RunManager routes the UI decision to the active Codex channel");
                Check(logs.Any(e => e.Type == "resume" && e.ResumeId == "thread-1"), "thread resume identity reaches DesktopService");
                Check(logs.Any(e => e.Entry?.Text == "fixture complete"), "agent message reaches the final transcript");
            }
            await using var headless = new RunManager(_ => Task.FromResult(workspace), catalog, "unused", _ => { });
            Reject(() => headless.StartAsync(Request()).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static async Task FakeCliAsync(string[] args)
    {
        if (!args.Contains("app-server") || args.Contains("exec")) throw new InvalidOperationException("Fixture expected app-server arguments.");
        async Task<JsonElement> Read() => JsonDocument.Parse(await Console.In.ReadLineAsync() ?? throw new IOException("unexpected stdin EOF")).RootElement.Clone();
        async Task Send(object value) { await Console.Out.WriteLineAsync(Json(value)); await Console.Out.FlushAsync(); }
        async Task Reply(JsonElement ask, object result) => await Send(new { id = ask.GetProperty("id").Clone(), result });
        var initialize = await Read(); Check(initialize.Text("method") == "initialize", "first frame is initialize"); await Reply(initialize, new { });
        Check((await Read()).Text("method") == "initialized", "client acknowledges initialization");
        var thread = await Read(); await Reply(thread, new { thread = new { id = "thread-1" } });
        var turn = await Read(); Check(turn.GetProperty("params").Text("approvalPolicy") == "on-request", "actual turn carries approval policy"); await Reply(turn, new { turn = new { id = "turn-1" } });
        await Send(new { id = 40, method = "item/commandExecution/requestApproval", @params = new { threadId = "thread-1", turnId = "turn-1", itemId = "cmd-1", command = "fixture command", cwd = "C:\\work" } });
        Check((await Read()).GetProperty("result").Text("decision") == "accept", "explicit single-call response arrives");
        await Send(new { method = "item/completed", @params = new { threadId = "thread-1", turnId = "turn-1", item = new { id = "result", type = "agentMessage", text = "fixture complete" } } });
        await Send(new { method = "turn/completed", @params = new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } } });
        Check(await Console.In.ReadLineAsync() is null, "stdin ends only after turn completion");
    }
}
