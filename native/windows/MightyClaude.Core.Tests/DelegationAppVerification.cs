using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// Delegation connected to the app's run path, ported from the Mac's DelegationSwitchTests (attachment, run path, gate and server
/// surface suites) and DelegationAppHostTests: the binding and its MCP config, the pipe's delegation calls, the headless
/// <c>--agent-delegation-mcp</c> server, the real run path with a fake Claude, and the desktop service as the coordinator's host.
/// The last checks hold the delegation Core to the Windows-only rules its reviews asked for.
/// </summary>
internal static class DelegationAppVerification
{
    private static readonly string Executable = Path.Combine(Path.GetTempPath(), "Mighty Claude", "MightyClaude.exe");
    private static readonly DelegationRequest Delegate = Request("delegate", ("task", "Write the release notes"), ("mode", "plan"));

    private static JsonObject Servers(List<string> arguments)
    {
        Check(arguments.Count(argument => argument == "--mcp-config") == 1, "one --mcp-config");
        return (JsonObject)JsonNode.Parse(arguments[arguments.IndexOf("--mcp-config") + 1])!["mcpServers"]!;
    }

    internal static Task TheBindingAttachesTheServerOnlyToAClaudeRunWithTheSwitchOn()
    {
        var registry = new AgentIOBindings(); var workspace = new Workspace { Id = "ws", Path = Path.GetTempPath() };
        // Off, the default: byte for byte the config before delegation.
        var off = registry.Bind("pane-a", workspace, "claude", "pipe", Executable); var arguments = new List<string>(); off.AppendArguments(arguments);
        Same(arguments[1], "{\"mcpServers\":{\"mighty-terminal\":{\"type\":\"stdio\",\"command\":" + JsonSerializer.Serialize(Executable, Wire.Json) + ",\"args\":[\"--agent-io-mcp\"]}}}", "switched off, the config is the one before");
        Check(!off.Delegation && off.DelegationHandler is null && !string.Join(" ", arguments).Contains(DelegationMcp.ServerName, StringComparison.Ordinal), "a binding no run connected is detached");
        // On: the delegation server beside mighty-terminal, on the same token, which reaches it only through the environment.
        var on = registry.Bind("pane-b", workspace, "claude", "pipe", Executable); on.ConnectDelegation(new DelegationIOHandler(() => true), true); arguments.Clear(); on.AppendArguments(arguments);
        var servers = Servers(arguments);
        SameList(servers.Select(server => server.Key).Order(StringComparer.Ordinal), [DelegationMcp.ServerName, AgentIOBinding.ServerName], "both servers");
        Same(servers[DelegationMcp.ServerName]!.ToJsonString(), "{\"type\":\"stdio\",\"command\":" + JsonSerializer.Serialize(Executable, Wire.Json) + ",\"args\":[\"--agent-delegation-mcp\"]}", "the delegation server is the app binary's headless mode, with no env of its own");
        Same(servers[AgentIOBinding.ServerName]!["args"]!.ToJsonString(), "[\"--agent-io-mcp\"]", "mighty-terminal is as it was");
        Check(on.Delegation && on.Kind == "claude" && on.Provider == "claude" && !string.Join(" ", arguments).Contains(on.Environment[AgentIOBinding.TokenKey], StringComparison.Ordinal) && !arguments.Contains("--strict-mcp-config"), "the token stays in the environment");
        // A Codex run never names it, even when the run path asks.
        var codex = registry.Bind("pane-c", workspace, "codex", "pipe", Executable); codex.ConnectDelegation(new DelegationIOHandler(() => true), true); arguments.Clear(); codex.AppendArguments(arguments);
        Check(!codex.Delegation && codex.DelegationHandler is not null && !string.Join(" ", arguments).Contains(DelegationMcp.ServerName, StringComparison.Ordinal) && !string.Join(" ", arguments).Contains(DelegationMcp.HeadlessArgument, StringComparison.Ordinal), "a Codex run gets no delegation server");
        // Only the saved record's absolute folder, and only for an agent pane, may be a run's working folder.
        foreach (var bad in new[] { new StartRunRequest("p", "ws", "claude", "hi", []) { WorkingFolder = "relative\\child" }, new StartRunRequest("p", "ws", "shell", "dir", []) { WorkingFolder = Path.GetTempPath() } })
            Check(Throws<ArgumentException>(() => bad.Validate()), "a working folder that is not an agent pane's full path is refused");
        Check(new StartRunRequest("p", "ws", "claude", "hi", []) { WorkingFolder = Path.GetTempPath() }.Validate().WorkingFolder == Path.GetTempPath() && !JsonSerializer.Serialize(new StartRunRequest("p", "ws", "claude", "hi", []) { WorkingFolder = "C:\\x" }, Wire.Json).Contains("workingFolder", StringComparison.Ordinal), "a working folder is never on the wire");
        return Task.CompletedTask;
    }

    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

    /// <summary>A delegation handler that answers by the tool's name, so every answer's shape crosses the pipe.</summary>
    private sealed class Canned(TimeSpan delay) : IDelegationRequestHandler
    {
        internal readonly List<(DelegationRequest Request, DelegationCaller Caller)> Calls = [];
        public async Task<DelegationResponse> Handle(DelegationRequest request, DelegationCaller caller)
        {
            lock (Calls) Calls.Add((request, caller));
            await Task.Delay(delay);
            return Answers[request.Tool];
        }
        internal static readonly Dictionary<string, DelegationResponse> Answers = new()
        {
            ["delegate"] = DelegationResponse.Delegated(ChildRecord.Make("c1", "parent", "C:\\w\\c1", "main", Commit, "plan", "key")),
            ["list_children"] = new(Children: [new("c1", ChildState.Reported, 2), new("c2", ChildState.Running, 0)]),
            ["child_status"] = new(Status: new("c1", ChildState.Reported, 1, Commit, "# Report 한글", false)),
            ["merge"] = new(Merged: new("c1", MergeKind.ToolFastForward, "main", Commit, new('b', 40), new('b', 40))),
            ["follow_up"] = new(FollowUp: new("f1", "c1", DeliveryLane.Held, 1)),
            ["discard"] = DelegationResponse.Refusal(DelegationReasonCode.DiscardHumanOnly),
            ["slow"] = DelegationResponse.Failure("slow answer"),
        };
    }

    /// <summary>A delegation handler whose calls wait until it is opened.</summary>
    private sealed class Gated : IDelegationRequestHandler
    {
        internal readonly TaskCompletionSource Open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Entered;
        public async Task<DelegationResponse> Handle(DelegationRequest request, DelegationCaller caller) { Interlocked.Increment(ref Entered); await Open.Task; return DelegationResponse.Failure("released"); }
    }

    internal static async Task ThePipeCarriesDelegationCallsAndAnswersAsTheMacDoes()
    {
        var terminal = new List<string>();
        await using var pipe = new AgentIOPipe((binding, _, _, _) => { lock (terminal) terminal.Add(binding.PaneId); return Task.FromResult<object>(new { owner = binding.PaneId }); });
        var workspace = new Workspace { Id = "ws", Path = Path.GetTempPath() };
        AgentIOBinding Bind(string pane, string provider, IDelegationRequestHandler? handler, bool attached)
        {
            var binding = pipe.Bindings.Bind(pane, workspace, provider, pipe.Name, Executable); if (handler is not null) binding.ConnectDelegation(handler, attached); return binding;
        }
        Task<DelegationResponse> Send(DelegationRequest request, AgentIOBinding binding) => AgentIOPipe.CallDelegation(pipe.Name, binding.Environment[AgentIOBinding.TokenKey], request, CancellationToken.None);
        var on = new DelegationIOHandler(() => true);
        // Switched on, a pane that is not a Claude pane is refused with claude_only whatever it names.
        var codex = Bind("codex", "codex", on, true);
        foreach (var tool in new[] { "delegate", "list_children", "child_status", "merge", "follow_up", "discard", "no_such_tool" })
            Same(await Send(tool == "delegate" ? Delegate : Request(tool, ("child", "c1")), codex), DelegationResponse.Refusal(DelegationReasonCode.ClaudeOnly), "codex " + tool);
        Check((await Send(Delegate, Bind("claude", "claude", on, true))).Refused is null, "a Claude pane with the server attached is not refused as a non-Claude pane");
        // Switched off, every call reaches nothing; a Claude run started while it was off stays detached; so does a run no service connected.
        var off = new DelegationIOHandler(() => false);
        foreach (var binding in new[] { Bind("codex-off", "codex", off, true), Bind("claude-off", "claude", off, true), Bind("claude-detached", "claude", on, false), Bind("claude-alone", "claude", null, false) })
            Same(await Send(Delegate, binding), DelegationResponse.Failure(DelegationIOHandler.DetachedMessage), binding.PaneId + " reaches nothing");
        // Forged and revoked tokens reach no pane.
        Same(await AgentIOPipe.CallDelegation(pipe.Name, "forged", Delegate, CancellationToken.None), DelegationResponse.Failure(AgentIOPipe.UnknownTokenMessage), "a forged token");
        var revoked = Bind("revoked", "claude", on, true); pipe.Bindings.Revoke("revoked");
        Same(await Send(Delegate, revoked), DelegationResponse.Failure(AgentIOPipe.UnknownTokenMessage), "a revoked token");
        Same(await AgentIOPipe.CallDelegation("not-a-pipe", "x", Delegate, CancellationToken.None), DelegationResponse.Failure("Mighty Claude's agent socket path is invalid."), "an invalid pipe name is never opened");
        // A malformed delegation call is answered, and the terminal calls on the shared pipe are served as before.
        var alone = pipe.Bindings.Bind("alone", workspace, "claude", pipe.Name, Executable);
        await using (var raw = new NamedPipeClientStream(".", pipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await raw.ConnectAsync(10_000);
            await AgentIOPipe.WritePacket(raw, Encoding.UTF8.GetBytes("{\"token\":\"" + alone.Environment[AgentIOBinding.TokenKey] + "\",\"delegation\":{\"tool\":5,\"arguments\":{}}}"), CancellationToken.None);
            Same(JsonSerializer.Deserialize<DelegationResponse>(await AgentIOPipe.ReadPacket(raw, CancellationToken.None), Wire.Json), DelegationResponse.Failure("The request to Mighty Claude was malformed."), "a malformed call");
        }
        var served = await AgentIOPipe.Call(pipe.Name, alone.Environment[AgentIOBinding.TokenKey], "read_latest_output", JsonSerializer.SerializeToElement(new { handle = "h" }), CancellationToken.None);
        Check(served.GetProperty("ok").GetBoolean() && served.GetProperty("result").GetProperty("owner").GetString() == "alone" && terminal.SequenceEqual(["alone"]), "a terminal call is served, and no delegation call reached the terminal tools");
        // No input ends a listener: a token that is not text, sent more times than there are listeners, and a call with no
        // arguments are answered as malformed, and the terminal tools are served after them.
        foreach (var packet in Enumerable.Repeat("{\"token\":\"\\ud800\",\"delegation\":{}}", 17).Append("{\"token\":\"" + alone.Environment[AgentIOBinding.TokenKey] + "\",\"delegation\":{\"tool\":\"list_children\"}}"))
        {
            await using var raw = new NamedPipeClientStream(".", pipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await raw.ConnectAsync(10_000);
            await AgentIOPipe.WritePacket(raw, Encoding.UTF8.GetBytes(packet), CancellationToken.None);
            Same(JsonSerializer.Deserialize<DelegationResponse>(await AgentIOPipe.ReadPacket(raw, CancellationToken.None), Wire.Json), DelegationResponse.Failure("The request to Mighty Claude was malformed."), "a malformed call: " + packet[..Math.Min(40, packet.Length)]);
        }
        served = await AgentIOPipe.Call(pipe.Name, alone.Environment[AgentIOBinding.TokenKey], "read_latest_output", JsonSerializer.SerializeToElement(new { handle = "h" }), CancellationToken.None);
        Check(served.GetProperty("ok").GetBoolean(), "a terminal call is served after seventeen malformed packets");
        // Long delegation calls never hold the listeners: twenty panes' calls and as many as one pane may have going wait while a
        // terminal call is served, and one call more from that pane is refused.
        var gated = new Gated(); var crowded = Bind("crowded", "claude", gated, true);
        var waiting = Enumerable.Range(0, 20).Select(i => Send(Request("list_children"), Bind("busy-" + i, "claude", gated, true)))
            .Concat(Enumerable.Range(0, AgentIOPipe.DelegationCallsPerToken).Select(_ => Send(Request("list_children"), crowded))).ToList();
        await Verification.Until(() => Volatile.Read(ref gated.Entered) == waiting.Count, 30_000);
        Same(await Send(Request("list_children"), crowded), DelegationResponse.Failure(AgentIOPipe.BusyMessage), "one call more than a pane may have going is refused");
        var clock = Stopwatch.StartNew();
        served = await AgentIOPipe.Call(pipe.Name, alone.Environment[AgentIOBinding.TokenKey], "read_latest_output", JsonSerializer.SerializeToElement(new { handle = "h" }), CancellationToken.None);
        Check(served.GetProperty("ok").GetBoolean() && clock.Elapsed < TimeSpan.FromSeconds(10), "a terminal call is served while twenty-four delegation calls wait");
        gated.Open.SetResult();
        foreach (var call in waiting) Same(await call, DelegationResponse.Failure("released"), "each waiting call gets its answer");
        // Every answer crosses the pipe whole, with its caller and string arguments.
        var canned = new Canned(TimeSpan.Zero); var caller = Bind("caller", "claude", canned, true);
        foreach (var (tool, expected) in Canned.Answers.Where(answer => answer.Key != "slow"))
            Same(JsonSerializer.Serialize(await Send(Request(tool, ("child", "c1"), ("text", "한글 \"quoted\"")), caller), Wire.Json), JsonSerializer.Serialize(expected, Wire.Json), tool + " crosses the pipe");
        Check(canned.Calls.All(call => call.Caller == new DelegationCaller("caller", "claude", "claude", true) && call.Request.Arguments["text"] == "한글 \"quoted\"" && call.Request.Arguments.Count == 2), "the caller and the arguments are the pane's");
        // An authenticated delegation call may take past the terminal tools' 30 s: the coordinator answers within 45.
        var slow = Bind("slow", "claude", new Canned(TimeSpan.FromSeconds(31)), true);
        Same(await Send(Request("slow"), slow), DelegationResponse.Failure("slow answer"), "a call answered after 31 s still gets its answer");
    }

    internal static async Task TheHeadlessServerListsTheSixToolsAndForwardsOverThePipe()
    {
        var canned = new Canned(TimeSpan.Zero);
        await using var pipe = new AgentIOPipe((_, _, _, _) => throw new InvalidOperationException());
        var binding = pipe.Bindings.Bind("parent", new Workspace { Id = "ws", Path = Path.GetTempPath() }, "claude", pipe.Name, Executable); binding.ConnectDelegation(canned, true);
        var self = Verification.Self(DelegationMcp.HeadlessArgument);
        var info = ChildProcess.StartInfo(self.Binary, self.Prefix, Path.GetTempPath(), binding.Environment.ToDictionary());
        await using var server = ChildProcess.Start(info);
        async Task<JsonNode> Ask(string line) { await server.Input.WriteLineAsync(line); await server.Input.FlushAsync(); return JsonNode.Parse(await server.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) ?? "null")!; }
        var hello = await Ask("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");
        Check((string?)hello["result"]?["serverInfo"]?["name"] == DelegationMcp.ServerName && (string?)hello["result"]?["protocolVersion"] == "2025-03-26" && hello["result"]?["capabilities"]?["tools"] is not null, "initialize names the delegation server");
        var tools = await Ask("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        SameList(tools["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]), DelegationToolManifest.All.Select(tool => tool.Name), "exactly the six tools");
        Check(!tools["result"]!["tools"]!.AsArray().Any(tool => AgentIOTools.All.Any(terminal => terminal.Name == (string?)tool!["name"])), "the terminal tools stay on mighty-terminal alone");
        var call = await Ask("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delegate","arguments":{"task":"Write the release notes","mode":"plan"}}}""");
        Check(call["result"]?["structuredContent"]?["child"]?["id"]?.GetValue<string>() == "c1" && canned.Calls.Single() is var (request, caller) && request.Tool == "delegate" && request.Arguments["task"] == "Write the release notes" && request.Arguments["mode"] == "plan" && caller.PaneId == "parent" && caller.Delegation, "the call reached the app with its arguments, on the pane's token");
        server.Input.Close();
        Same(await server.Completion.WaitAsync(TimeSpan.FromSeconds(25)), 0, "the server exits when stdin closes");

        // Calls that are not well formed never reach the app; one with a lone surrogate is answered all the same.
        var forwarded = 0; var surface = new DelegationMcp((_, _) => { forwarded++; return Task.FromResult(DelegationResponse.Refusal(DelegationReasonCode.ClaudeOnly)); });
        string ToolCall(string name, string arguments) => "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}";
        foreach (var name in new[] { "Delegate", "../delegate", "", new string('a', 65), "1delegate" }) Same((int?)JsonNode.Parse((await surface.Handle(ToolCall(name, "{}")))!)!["error"]?["code"], -32602, "the name " + name);
        foreach (var arguments in new[] { """{"task":42}""", """{"task":["a"]}""", """{"Task":"x"}""", "\"not an object\"" }) Check((bool?)JsonNode.Parse((await surface.Handle(ToolCall("delegate", arguments)))!)!["result"]?["isError"] == true, "the arguments " + arguments);
        Check(await surface.Handle(ToolCall("delegate", """{"task":"\ud800","mode":"plan"}""")) is { } lone && JsonNode.Parse(lone)!["error"] is not null, "a lone surrogate is answered with an error");
        Check((bool?)JsonNode.Parse((await new DelegationMcp(null).Handle(ToolCall("delegate", """{"task":"x","mode":"plan"}""")))!)!["result"]?["isError"] == true && forwarded == 0, "nothing malformed reached the app, and a server without a pane connection answers alone");
    }

    internal static async Task TheRealRunPathAttachesTheServerAndRunsAChildInItsWorktree()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            await using var s = await DelegationLaunch.Start(place.Profile, Path.Combine(place.Base, "fake"), place.Repo, place.Root, "acceptEdits", switchOn: false);
            s.Script(place.Repo, [Scenario.Say("Hello.")], [Scenario.Say("Again.")]);
            // Off, the default: the Claude pane's run names mighty-terminal alone, and its binding is detached.
            var first = await s.HumanSend("Say hello.");
            Same(await s.Ended(DelegationLaunch.Parent, first), "completed", "the switched-off run");
            var argv = s.Log(place.Repo).Where(entry => (string?)entry["event"] == "argv").ToList();
            var config = JsonNode.Parse(Scenario.Option(argv[0], "--mcp-config")!)!["mcpServers"]!.AsObject();
            Check(config.Select(server => server.Key).SequenceEqual([AgentIOBinding.ServerName]) && s.Bindings[DelegationLaunch.Parent] is { Delegation: false } && !s.Log(place.Repo).Any(entry => (string?)entry["event"] == "mcp"), "switched off, one server");
            // On, read at the next run start: the run gets the delegation server, and the fake Claude reaches its six tools.
            s.SwitchOn = true;
            var second = await s.HumanSend("Say it again.");
            Same(await s.Ended(DelegationLaunch.Parent, second), "completed", "the switched-on run");
            argv = s.Log(place.Repo).Where(entry => (string?)entry["event"] == "argv").ToList();
            config = JsonNode.Parse(Scenario.Option(argv[1], "--mcp-config")!)!["mcpServers"]!.AsObject();
            Check(config.Select(server => server.Key).Order(StringComparer.Ordinal).SequenceEqual([DelegationMcp.ServerName, AgentIOBinding.ServerName]) && s.Bindings[DelegationLaunch.Parent] is { Delegation: true, Kind: "claude", Provider: "claude" } binding
                && !string.Join("\n", argv[1]["arguments"]!.AsArray().Select(item => (string?)item)).Contains(binding.Environment[AgentIOBinding.TokenKey], StringComparison.Ordinal), "switched on, both servers, and the token stays out of argv");
            SameList(s.Log(place.Repo).Single(entry => (string?)entry["event"] == "mcp")["tools"]!.AsArray().Select(tool => (string?)tool), DelegationToolManifest.All.Select(tool => tool.Name), "the CLI reached the six tools on the pane's token");
            // A Codex pane's run, and a shell pane's, never get the delegation server: a shell pane gets no binding at all.
            await s.Service.UpdateAsync(state => state with { Sessions = [.. state.Sessions, new RunSession { Id = "codex", WorkspaceId = s.Workspace.Id, Provider = "codex" }, new RunSession { Id = "shell", WorkspaceId = s.Workspace.Id, Kind = "shell" }] });
            await s.Service.StartAsync(new("codex", s.Workspace.Id, "claude", "hi", [], Provider: "codex"));
            await s.Ended("codex", await s.Awaited("the codex run", () => s.Runs("codex").FirstOrDefault()));
            var codexArguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(s.FakeRoot, "codex.args")))!;
            Check(s.Bindings["codex"] is { Delegation: false } && codexArguments.Any(argument => argument.Contains(AgentIOBinding.ServerName, StringComparison.Ordinal)) && !codexArguments.Any(argument => argument.Contains(DelegationMcp.ServerName, StringComparison.Ordinal)), "a Codex run names mighty-terminal alone");
            await s.Service.StartAsync(new("shell", s.Workspace.Id, "shell", "echo shell", []));
            await s.Ended("shell", await s.Awaited("the shell run", () => s.Runs("shell").FirstOrDefault()));
            Check(!s.Bindings.ContainsKey("shell"), "a shell pane gets no binding");

            // A child's run and its binding work in its worktree folder, never in its parent's checkout.
            var worktree = Path.Combine(place.Root, "child-1"); Directory.CreateDirectory(worktree);
            await s.Service.UpdateAsync(state => state with { Sessions = [.. state.Sessions, DelegationPanes.ChildSession(new("child-1", DelegationLaunch.Parent, "plan", worktree), s.Workspace.Id, "Child")] });
            s.Script(worktree, [Scenario.Say("Working in my worktree.")]);
            await s.Service.StartAsync(s.Request("Write the notes.", "child-1"));
            Same(await s.Ended("child-1", await s.Awaited("the child's run", () => s.Runs("child-1").FirstOrDefault())), "completed", "the child's run");
            Check(s.Bindings["child-1"] is { Delegation: true } child && DelegationPaths.Same(child.WorkspacePath, worktree) && child.WorkspaceId == s.Workspace.Id, "the binding the terminal tool's pane is made from names the worktree");
            Check(s.Prompts(worktree).SequenceEqual(["Write the notes."]) && s.Log(worktree).Any(entry => (string?)entry["event"] == "argv"), "Claude worked in the worktree");
            Check(Scenario.Option(s.Log(worktree).First(entry => (string?)entry["event"] == "argv"), "--permission-mode") == "plan", "in the child's own mode");
            // Its worktree gone, it refuses to run, with worktree_missing, and never falls back to the parent's checkout.
            RemoveFolder(worktree);
            var refused = false;
            try { await s.Service.StartAsync(s.Request("Again.", "child-1")); } catch (InvalidOperationException ex) { refused = ex.Message == Locale.Get("run.blocked.worktreeMissing") && ex.Message.Contains("worktree_missing", StringComparison.Ordinal); }
            Check(refused && s.Runs("child-1").Count == 1 && s.Pane("child-1").Status == "completed", "a child without its worktree refuses to run");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheServiceHostsDelegationAndOffersTheWindowItsEntryPoints()
    {
        // A child pane goes beside its parent as a tab: its parent's group keeps showing what it showed.
        var workspace = new Workspace { Id = "w", Path = Path.GetTempPath() };
        var parent = new RunSession { Id = "parent", WorkspaceId = "w" }; var other = new RunSession { Id = "other", WorkspaceId = "w" };
        var state = new AppSnapshot { Workspaces = [workspace], Sessions = [parent, other], ActiveWorkspaceId = "w", ActiveSessionId = "other", PaneLayouts = new() { ["w"] = new() { Id = "g", SessionIds = ["parent", "other"], SelectedSessionId = "other" } }, PaneLayoutModes = new() { ["w"] = "tabs" } };
        var pane = new DelegationChildPane("child", "parent", "acceptEdits", Path.Combine(Path.GetTempPath(), "wt", "child"));
        var next = DelegationPanes.AddingChild(state, pane, "Write the release notes\nfor 0.5") ?? throw new InvalidOperationException("no child pane");
        var child = next.Sessions.Single(session => session.Id == "child");
        Check(child is { ParentSessionId: "parent", TitleMode: PaneTitle.Fixed, Kind: "claude", Provider: "claude", Title: "Write the release notes for 0.5" } && child.Settings == new RunSettings(PermissionMode: "acceptEdits") && child.WorkingFolder == pane.Folder, "the child pane, titled by its task");
        Check(next.PaneLayouts!["w"] is { Kind: "tabs", SelectedSessionId: "other" } tabs && tabs.SessionIds.SequenceEqual(["parent", "child", "other"]) && next.PaneLayoutModes!["w"] == "tabs" && next.ActiveSessionId == "other", "a tab right after its parent, the group showing what it showed");
        var grid = DelegationPanes.AddingChild(state with { PaneLayouts = null, PaneLayoutModes = null, Layout = "grid" }, pane, null)!;
        Check(grid.PaneLayoutModes!["w"] == "custom" && PaneLayout.Groups(grid.PaneLayouts!["w"]).Single(group => group.SessionIds.Contains("parent")).SessionIds.SequenceEqual(["parent", "child"]) && grid.Sessions.Single(session => session.Id == "child").Title == ProviderCatalog.Name("claude"), "a preset layout is kept as it showed, the child a tab beside its parent");
        Check(DelegationPanes.AddingChild(state with { PaneLayoutModes = new() { ["w"] = "focus" } }, pane, null)!.PaneLayoutModes!["w"] == "focus", "focus stays focus");
        Check(DelegationPanes.AddingChild(state, pane with { ParentSessionId = "gone" }, null) is null && DelegationPanes.AddingChild(next, pane, null) is null
            && DelegationPanes.AddingChild(state with { Sessions = [.. state.Sessions, .. Enumerable.Range(0, 126).Select(index => new RunSession { Id = "p" + index, WorkspaceId = "w" })] }, pane, null) is null, "no pane without its parent, twice, or past the cap");
        // A child saved running comes back stopped, and a new pane never takes its settings from a child.
        var restored = StateStore.Normalize(next with { Sessions = next.Sessions.Select(session => session.Id == "child" ? session with { Status = "running" } : session).ToList() }, true).Sessions.Single(session => session.Id == "child");
        Check(restored is { Status: "stopped", ParentSessionId: "parent" } && SessionTemplate.Find(new RunSession { Id = "fresh", WorkspaceId = "w" }, [restored with { Logs = [new("l", "user", "hi", "2099-01-01T00:00:00Z")] }]) is null, "a child is never restored running, nor a template");

        // The service at launch: the interrupted sweep and held items, a parent close that asks, a workspace that may not go,
        // a send no run took, and card actions one at a time.
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            var running = ChildRecord.Make("c1", DelegationLaunch.Parent, Path.Combine(place.Root, "c1"), "main", Commit, "plan", "key-1", state: ChildState.Running) with { RunId = "run-old" };
            var reported = ChildRecord.Make("c2", DelegationLaunch.Parent, Path.Combine(place.Root, "c2"), "main", Commit, "plan", "key-2", reportRevision: 1, reportHead: Commit, state: ChildState.Reported, parentCheckout: Path.Combine(place.Base, "gone"));
            var notice = new Notice("n1", "c2", 1, NoticeKind.Reported, DeliveryLane.Pending);
            new DelegationFileStore(place.Profile).Save(DelegationFile.Of([running, reported]) with { Notices = [notice] });
            await using var s = await DelegationLaunch.Start(place.Profile, Path.Combine(place.Base, "fake"), place.Repo, place.Root);
            Check(s.File.Children.Single(record => record.Id == "c1").State == ChildState.Interrupted && s.Service.HeldItems(DelegationLaunch.Parent).Single().Id == "n1", "launch interrupts the runs the quit killed and holds what was pending");
            Check(s.Service.ParentCloseQuestion(DelegationLaunch.Parent) == new PendingParentClose(DelegationLaunch.Parent, "Parent", 2) && s.Service.ParentCloseQuestion("nobody") is null, "a parent with open children asks first");
            // The phone counts the held row in the pane's summary, and its removal is refused with held_not_removable, changing nothing.
            using (var router = new MobileDesktopRouter(s.Service, Path.Combine(place.Base, "mobile"), "Fixture", (_, _) => throw new InvalidOperationException("no desktop action"), _ => new([])) { QueueCount = _ => 0 })
            {
                var listed = await router.RouteAsync("GET", "/m1/state", null, "phone", CancellationToken.None);
                Check(JsonSerializer.SerializeToNode(listed.Body, Wire.Json)?["sessions"]?[0]?["queued"]?.GetValue<int>() == 1, "the held row is counted in the summary");
                var refused = await router.RouteAsync("POST", "/m1/sessions/" + DelegationLaunch.Parent + "/queue/n1/remove", null, "phone", CancellationToken.None);
                Check(refused.Status == 409 && (string?)JsonSerializer.SerializeToNode(refused.Body, Wire.Json)?["code"] == "held_not_removable" && s.Service.HeldItems(DelegationLaunch.Parent).Single().Id == "n1", "a held row's removal is refused");
            }
            // A held row swapped for another moves the pane's revision though the count stays.
            var revisions = new MobileRevisions(); var parentPane = s.Pane(DelegationLaunch.Parent); Workspace[] spaces = [s.Workspace];
            revisions.Observe(spaces, [new(parentPane, true, 0, 0, "summary", "n1")]); var revision = revisions.Session(DelegationLaunch.Parent);
            Check(revisions.Observe(spaces, [new(parentPane, true, 0, 0, "summary", "n2")]).Any(note => note.Scope == "session:" + DelegationLaunch.Parent) && revisions.Session(DelegationLaunch.Parent) > revision
                && revisions.Observe(spaces, [new(parentPane, true, 0, 0, "summary", "n2")]).Count == 0, "a swapped held row is news for the phone, the same rows are not");
            Same(s.Service.WorkspaceRemovalRefusal(s.Workspace.Id), DelegationReasonCode.WorkspaceHasChildren, "the workspace has children");
            var removal = false;
            try { await s.Service.RemoveWorkspaceAsync(s.Workspace.Id); } catch (InvalidOperationException ex) { removal = ex.Message == Locale.Get("delegation.workspace.hasChildren", new Dictionary<string, string> { ["code"] = "workspace_has_children" }); }
            Check(removal && s.Service.Snapshot.Workspaces.Count == 1, "its removal is refused and changes nothing");
            SameList(s.Service.SidebarTree(s.Service.Snapshot, s.Workspace.Id).Children[DelegationLaunch.Parent].Select(row => row.Id), ["c1", "c2"], "the sidebar lists both under the parent");
            // A send whose start is refused takes nothing: the held notice stays held, and the refusal is the caller's to show.
            var thrown = false;
            try { await s.Service.SendAsync(s.Request(new string('x', 100_000))); } catch (ArgumentException) { thrown = true; }
            Check(thrown && s.Service.HeldItems(DelegationLaunch.Parent).Single().Id == "n1" && s.File.Notices.Single().Lane == DeliveryLane.Held && s.Runs(DelegationLaunch.Parent).Count == 0, "a send no run took leaves the held items held");
            // Card actions: a discard confirmation that cannot list nested worktrees, and the answer kept for the card.
            var pending = await s.Service.AskToDiscardChildAsync("c2") ?? throw new InvalidOperationException("no confirmation");
            Check(pending.Nested is null && pending.Branch == "mighty/c2" && !s.Service.IsCardBusy("c2"), "with the parent's folder gone, the confirmation cannot list nested worktrees and offers no discard");
            // What a window's handler throws goes no further: the caller is answered, the card is not left busy, and the next handler still hears it.
            var heard = 0; void Throwing() => throw new InvalidOperationException("window"); void Counting() => Interlocked.Increment(ref heard);
            s.Service.DelegationChanged += Throwing; s.Service.DelegationChanged += Counting;
            try
            {
                Check(await s.Service.AskToDiscardChildAsync("c2") is not null && !s.Service.IsCardBusy("c2"), "a throwing handler leaves the card's action answered and the card free");
                await Verification.Until(() => Volatile.Read(ref heard) >= 2);
            }
            finally { s.Service.DelegationChanged -= Throwing; s.Service.DelegationChanged -= Counting; }
            Check(await s.Service.MergeChildAsync("c2") is { Action: DelegationCardAction.Merge, Result: DelegationCardResult.Refused or DelegationCardResult.Failed } note && s.Service.CardNote("c2") == note, "a card's answer is kept for it");
            // Closing a child pane reaches the coordinator once the pane is gone.
            await s.Service.UpdateAsync(snapshot => snapshot with { Sessions = [.. snapshot.Sessions, DelegationPanes.ChildSession(new("c1", DelegationLaunch.Parent, "plan", Path.Combine(place.Root, "c1")), s.Workspace.Id, "Child")] });
            var terminals = new List<string>(); s.Service.EndAgentTerminal = id => { lock (terminals) terminals.Add(id); return Task.CompletedTask; };
            Check(await s.Service.CloseSessionAsync("c1") == new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged) && s.File.Children.Single(record => record.Id == "c1").State == ChildState.Closed && !s.Service.Snapshot.Sessions.Any(session => session.Id == "c1"), "a child's close closes its record");
            Check(terminals.SequenceEqual(["c1"]), "the child's agent terminal is ended before its close reaches the coordinator");
        }
        finally { place.Remove(); }
    }

    internal static Task TheWindowSaysEachStateActionAndAnswerInTheAppsLanguage()
    {
        foreach (var state in Enum.GetValues<ChildState>()) Check(DelegationText.State(state) is { Length: > 0 } words && !words.StartsWith("delegation.", StringComparison.Ordinal), state + " has its words");
        foreach (var action in Enum.GetValues<DelegationCardAction>()) Check(!DelegationText.Action(action).StartsWith("delegation.", StringComparison.Ordinal), action + " has its button");
        var row = new DelegationChildRow("c1", "p", ChildState.Reported, "Task", "main");
        Check(DelegationText.Note(new(DelegationCardAction.Merge, new DelegationCardResult.Done()), row) is { Tone: DesignTone.Done } merged && merged.Text.Contains("main", StringComparison.Ordinal), "a merge names the branch it went into");
        Check(DelegationText.Note(new(DelegationCardAction.Discard, new DelegationCardResult.Done()), row) is null && DelegationText.Note(new(DelegationCardAction.Discard, new DelegationCardResult.Reconfirm(["x"])), row) is null, "a discard done or asked again says nothing on the card");
        Check(DelegationText.Note(new(DelegationCardAction.Merge, new DelegationCardResult.Failed("fatal: not a git repository")), row) is { Tone: DesignTone.Err } failed && failed.Text.Contains("fatal: not a git repository", StringComparison.Ordinal), "git's output is the detail of a failure");
        Check(DelegationText.Note(new(DelegationCardAction.Undo, new DelegationCardResult.Refused(DelegationReasonCode.WorkspaceHasChildren)), row) is { Tone: DesignTone.Wait } refused && refused.Text.Contains("workspace_has_children", StringComparison.Ordinal), "a refusal with no words of its own ends in its code");
        var pending = new PendingChildDiscard("c1", "Task", "mighty/c1", ["C:\\wt\\c1\\inner"]);
        Check(DelegationText.DiscardMessage(pending).Contains("C:\\wt\\c1\\inner", StringComparison.Ordinal) && DelegationText.DiscardTitle(pending) != DelegationText.DiscardTitle(pending with { Nested = null }), "the confirmation names the nested worktrees, and says when it cannot");
        Check(DelegationText.CloseNote(new ChildCleanupOutcome.Kept(ChildCleanupHold.Link)) is { Length: > 0 } && DelegationText.CloseNote(new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged)) is null && DelegationText.CloseNote(null) is null, "only the link hold is said on the card");
        Check(DelegationText.ParentCloseMessage(new("p", "Parent pane", 2)).Contains("Parent pane", StringComparison.Ordinal), "the parent close names its pane");
        return Task.CompletedTask;
    }

    // The delegation Core's Windows-only rules.

    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", link, target }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        process.StandardOutput.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null, "the junction was made");
    }

    internal static async Task AJunctionInAChildWorktreeNeverReachesWhatItPointsTo()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            File.WriteAllText(Path.Combine(place.Repo, ".gitignore"), "node_modules/\n"); await place.Git("add", "-A"); await place.Git("commit", "-q", "-m", "ignore");
            var outside = Path.Combine(place.Base, "outside"); Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "sentinel.txt"); File.WriteAllText(sentinel, "keep me");
            // A merged child with an ignored node_modules linked to the outside folder: cleanup keeps everything.
            var (child, head) = await place.ReportedChild("c1");
            // Only a directory link holds: a folder's files, a file link where Windows lets one be made, and what lies past a junction never do.
            var plain = Path.Combine(place.Base, "plain"); Directory.CreateDirectory(Path.Combine(plain, "sub")); File.WriteAllText(Path.Combine(plain, "sub", "a.txt"), "a");
            try { File.CreateSymbolicLink(Path.Combine(plain, "file-link.txt"), Path.Combine(plain, "sub", "a.txt")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Check(!ChildCleanup.HasLinks(plain) && ChildCleanup.Links(plain).Count == 0, "files and file links are no directory link");
            Junction(Path.Combine(plain, "sub", "j"), outside); Directory.CreateDirectory(Path.Combine(outside, "inner")); Junction(Path.Combine(outside, "inner", "k"), plain);
            Check(ChildCleanup.HasLinks(plain) && ChildCleanup.Links(plain).SequenceEqual([Path.Combine(plain, "sub", "j")]), "a junction is one, and the walk never goes past it");
            Directory.Delete(Path.Combine(outside, "inner", "k")); Directory.Delete(Path.Combine(outside, "inner"));
            Directory.CreateDirectory(Path.Combine(child.WorktreePath, "node_modules")); Junction(Path.Combine(child.WorktreePath, "node_modules", "linked"), outside);
            var merge = ((ChildMergeOutcome.Merged)await ChildMerge.ToolMerge(child, head, place.Repo)).Record;
            Same(await ChildCleanup.CleanUp(child.Applied(ChildStateEvent.Merge), merge, place.Repo, null), new ChildCleanupOutcome.Kept(ChildCleanupHold.Link), "cleanup holds for the junction");
            Check(File.Exists(sentinel) && Directory.Exists(child.WorktreePath) && (await place.MightyBranches()).Contains(child.Branch, StringComparison.Ordinal), "the outside folder, the worktree and the branch stay");
            // A discard unlinks the junction itself first, then removes everything of the child's.
            Check(await ChildCleanup.Discard(child, place.Repo, place.Root) is ChildDiscardOutcome.Discarded && File.Exists(sentinel) && File.ReadAllText(sentinel) == "keep me" && !Directory.Exists(child.WorktreePath), "the discard leaves the outside folder whole");
            // Only the child's own branch is ever deleted by force.
            var (second, _) = await place.ReportedChild("c2");
            Check(await ChildCleanup.Discard(second with { Branch = "main" }, place.Repo, place.Root) is ChildDiscardOutcome.Failed { Message: var why } && why == Locale.Get("delegation.card.error.branchMisplaced")
                && Directory.Exists(second.WorktreePath) && await place.Git("rev-parse", "--verify", "-q", "refs/heads/main") != "", "a record naming another branch is refused before anything goes");
        }
        finally { place.Remove(); }
    }

    /// <summary>A host whose every call throws.</summary>
    private sealed class ThrowingHost(DelegationPaneState? parent = null) : IDelegationHost
    {
        internal int Delivers;
        public Task<bool> CreatePane(DelegationChildPane pane) => throw new IOException("host");
        public Task<string?> StartRun(string sessionId, string input) => throw new IOException("host");
        public Task<string?> Deliver(string input, string sessionId, DeliveryRoute route) { Interlocked.Increment(ref Delivers); throw new IOException("host"); }
        public Task<DelegationPaneState?> PaneState(string sessionId) => parent is not null && sessionId == parent.SessionId ? Task.FromResult<DelegationPaneState?>(parent) : throw new IOException("host");
        public Task StopRun(string sessionId) => throw new IOException("host");
    }

    internal static async Task AHostThatThrowsIsAHostThatDidNotTakeTheCall()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            var child = await place.Child("c1") with { RunId = "r1" };
            var store = place.Store; store.Save(DelegationFile.Of([child]));
            var host = new ThrowingHost(new("parent", "auto", place.Repo, RunId: "run-p", Activity: DelegationPaneActivity.Finished));
            var coordinator = new DelegationCoordinator(store, host, place.Maker, () => true);
            // A run's end whose notice the host's delivery throws on: the notice stays pending, never saved delivered.
            File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\n");
            Check(await coordinator.ChildRunEnded("c1", "r1", ChildRunEnd.Finished) is { Lane: DeliveryLane.Pending } && host.Delivers > 0 && store.Load().Notices.Single().Lane == DeliveryLane.Pending, "an item the host threw on is not delivered");
            // Every tool call is answered; a delegate whose pane the host throws on fails to start instead of staying creating.
            foreach (var tool in DelegationToolManifest.All) Check(await coordinator.Handle(Request(tool.Name, [.. tool.Arguments.Select(argument => (argument.Name, argument.Name == "mode" ? "plan" : argument.Name == "child" ? "c1" : "x"))]), Parent) is not null, tool.Name + " is answered");
            Check(await Eventually(() => coordinator.File.Children.Any(record => record.Id != "c1" && record.State == ChildState.Failed)) && !coordinator.File.Children.Any(record => record.State == ChildState.Creating), "a start the host threw on fails, with no child left creating");
        }
        finally { place.Remove(); }
        // A failed answer is still one answer, and one event that failed never stops the ones after it.
        Same(await DelegationCoordinator.Answer(5, DelegationResponse.Failure("late"), () => throw new IOException("work")), DelegationResponse.Failure(DelegationCoordinator.NoAnswerMessage), "work that throws is answered");
        var seen = new List<string>();
        var pump = new DelegationRunEventPump(change => { if (change is DelegationRunEvent.Started { RunId: "r1" }) throw new IOException("handler"); lock (seen) seen.Add(((DelegationRunEvent.Started)change).RunId); return Task.CompletedTask; });
        pump.Send(new DelegationRunEvent.Started("c", "r1")); pump.Send(new DelegationRunEvent.Started("c", "r2")); await pump.Finish();
        SameList(seen, ["r2"], "the pump goes on after a failed event");
    }

    internal static async Task GitEndsOnItsDeadlineAndReadsTheWindowsWay()
    {
        var folder = Verification.Temp();
        try
        {
            // A process git started keeps its output open after git exits: the deadline still ends the call.
            var timer = Stopwatch.StartNew();
            await DelegationGit.Run(["-c", "alias.hold=!sleep 60 &", "hold"], folder, 3);
            Check(timer.Elapsed < TimeSpan.FromSeconds(30), "a held pipe ends with the deadline, after " + timer.Elapsed);
            Check(Path.IsPathFullyQualified(DelegationGit.NoHooks) && Path.IsPathFullyQualified(DelegationGit.Executable!), "git and its hooks folder are absolute");
            // The hooks folder is the profile's, never the install folder, and git does not run while anything is in it.
            var hooks = DelegationGit.NoHooks;
            try
            {
                DelegationGit.UseProfile(Path.Combine(folder, "hooks-profile"));
                Check(DelegationGit.NoHooks == Path.Combine(folder, "hooks-profile", "git-no-hooks") && !DelegationGit.NoHooks.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "the hooks folder is under the profile");
                Check(await DelegationGit.Run(["--version"], folder) is { ExitCode: 0 }, "git runs while the hooks folder is empty");
                File.WriteAllText(Path.Combine(DelegationGit.NoHooks, "pre-commit"), "exit 1");
                Check(await DelegationGit.Run(["--version"], folder) is null, "git does not run while the hooks folder holds a file");
            }
            finally { DelegationGit.UseProfile(Path.GetDirectoryName(hooks)!); }
            Check(ChildWorktreeMaker.AvailableBytes(folder) > 0 && ChildWorktreeMaker.AvailableBytes(Path.Combine(folder, "not", "made")) > 0, "free space is read on the folder's own volume");

            // REPORT.md by its byte-order mark, hashed as its bytes.
            var report = Path.Combine(folder, "REPORT.md"); const string text = "# Report\n\nDone, 한글.\n";
            foreach (var encoding in new Encoding[] { new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF8Encoding(true), new UTF8Encoding(false) })
            {
                var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(); File.WriteAllBytes(report, bytes);
                var read = ChildReportFile.Read(report) ?? throw new InvalidOperationException("no report");
                Check(read.Digest == Convert.ToHexStringLower(SHA256.HashData(bytes)) && DelegationCopy.Of("c", DelegationCopyKind.Report, 1, read.Start, read.TotalBytes).Text == text, encoding.WebName + " reads as its words");
            }
            File.WriteAllBytes(report, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(" \n")]);
            Check(ChildReportFile.Read(report) is null, "a UTF-16 report of whitespace is no report");

            // The delegation file is read again while another program holds it, and a read that still fails is unreadable.
            // The reader lets go once the load has been refused, so the load has waited it out whatever the machine's pace.
            FileStream? held = null; var refusals = 0;
            var store = new DelegationFileStore(Path.Combine(folder, "profile"), 40, () => { if (Interlocked.Increment(ref refusals) == 1) held?.Dispose(); }); store.Save(DelegationFile.Empty);
            held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            Check(store.Load() == DelegationFile.Empty && refusals >= 1, "a load waits out a reader");
            await using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(Throws<DelegationFileException>(() => new DelegationFileStore(store.Folder, 3).Load()), "a read refused past its tries is unreadable");
        }
        finally { RemoveFolder(folder); }
    }
}
