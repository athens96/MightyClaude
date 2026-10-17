using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using static DelegationSupport;
using Providers = MightyClaude.Core.DelegationProviders;

/// <summary>
/// A child of another agent than its parent's, ported from the Mac's DelegationCrossAgentTests and held to
/// native/contracts/fixtures/delegation-modes.json (DelegationModesFixtureTests): which mode it starts in, how delegate names
/// its agent, what the worktree button's menu offers, the pane it opens in, and the child a parent or the human makes.
/// </summary>
internal static class DelegationCrossAgentVerification
{
    private static readonly Lazy<JsonElement> Modes = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.DelegationModes.json";
        using var stream = typeof(DelegationCrossAgentVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    /// <summary>A capability as the fixture spells it: <c>writesAsking</c>, <c>writesUnasked</c>, <c>runsUnasked</c>, <c>fullAccess</c>.</summary>
    private static string Name(ModeCapability capability) => JsonNamingPolicy.CamelCase.ConvertName(capability.ToString());

    internal static Task TheSharedModeTablesHold()
    {
        var fixture = Modes.Value;
        SameList(fixture.GetProperty("agents").EnumerateArray().Select(agent => agent.GetString()!), Wire.Providers, "the agents are the app's three, in order");
        SameList(fixture.GetProperty("capabilities").EnumerateObject().Select(agent => agent.Name), Wire.Providers, "every agent has its capabilities");
        foreach (var provider in fixture.GetProperty("capabilities").EnumerateObject())
        {
            SameList(provider.Value.EnumerateObject().Select(mode => mode.Name).Order(StringComparer.Ordinal), Providers.StartingModes(provider.Name).Order(StringComparer.Ordinal), provider.Name + "'s modes");
            foreach (var mode in provider.Value.EnumerateObject())
                SameList(Providers.Capabilities(provider.Name, mode.Name).Select(Name).Order(StringComparer.Ordinal), mode.Value.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal), $"what {provider.Name} {mode.Name} may do");
        }
        SameList(fixture.GetProperty("widestChildMode").EnumerateObject().Select(agent => agent.Name), Wire.Providers, "every agent is a parent");
        foreach (var parent in fixture.GetProperty("widestChildMode").EnumerateObject())
        {
            SameList(parent.Value.EnumerateObject().Select(mode => mode.Name).Order(StringComparer.Ordinal), Providers.StartingModes(parent.Name).Order(StringComparer.Ordinal), parent.Name + "'s modes as a parent");
            foreach (var parentMode in parent.Value.EnumerateObject())
            {
                SameList(parentMode.Value.EnumerateObject().Select(child => child.Name), Wire.Providers, $"every child agent under {parent.Name} {parentMode.Name}");
                foreach (var child in parentMode.Value.EnumerateObject())
                    Same(Providers.WidestChildMode(child.Name, parent.Name, parentMode.Name), child.Value.ValueKind == JsonValueKind.Null ? null : child.Value.GetString(), $"{child.Name} under {parent.Name} {parentMode.Name}");
            }
        }
        return Task.CompletedTask;
    }

    internal static Task AgentsAndModesAcrossAgents()
    {
        // delegate's agent names an agent by its word, Antigravity also by its provider id, in any case.
        foreach (var (word, provider) in new[] { ("claude", "claude"), ("codex", "codex"), ("antigravity", "gemini"), ("gemini", "gemini"), ("Codex", "codex"), (" ANTIGRAVITY\n", "gemini") })
            Same(Providers.Agent(word), provider, "the agent named " + word.Trim());
        foreach (var word in new[] { "", "gpt", "openai", "agy", "claude code", "codex-cli" }) Same(Providers.Agent(word), null, "no agent is named '" + word + "'");
        SameList(Wire.Providers.Select(Providers.AgentName), ["claude", "codex", "antigravity"], "the tools name the agents by their words");

        // Editing unasked covers editing after asking; everything else is a plain subset.
        static HashSet<ModeCapability> Of(params ModeCapability[] capabilities) => [.. capabilities];
        Check(Providers.IsCovered(Of(ModeCapability.WritesAsking), Of(ModeCapability.WritesUnasked)) && !Providers.IsCovered(Of(ModeCapability.WritesUnasked), Of(ModeCapability.WritesAsking))
            && !Providers.IsCovered(Of(ModeCapability.RunsUnasked), Of(ModeCapability.WritesUnasked)) && Providers.IsCovered(Of(), Of()), "the subset rule");
        // A Claude parent in plan or manual mode gets no Codex or Antigravity child at all.
        foreach (var parentMode in new[] { "plan", "manual" })
            foreach (var (child, modes) in new[] { ("codex", new[] { "manual", "acceptEdits", "onRequest", "fullAccess" }), ("gemini", new[] { "acceptEdits", "fullAccess" }) })
            {
                foreach (var mode in modes) Check(!Providers.IsNoWider(mode, child, parentMode, "claude"), $"{child} {mode} under claude {parentMode}");
                Same(Providers.WidestChildMode(child, "claude", parentMode), null, $"{child} under claude {parentMode}");
            }
        // Claude acceptEdits edits unasked and asks before Bash; auto also runs commands unasked.
        Check(Providers.IsNoWider("onRequest", "codex", "acceptEdits", "claude") && Providers.IsNoWider("acceptEdits", "gemini", "acceptEdits", "claude") && !Providers.IsNoWider("acceptEdits", "codex", "acceptEdits", "claude"), "under Claude acceptEdits");
        Check(Providers.IsNoWider("acceptEdits", "codex", "auto", "claude") && !Providers.IsNoWider("fullAccess", "codex", "auto", "claude"), "under Claude auto");
        // A Codex onRequest parent edits unasked in its sandbox and asks for commands.
        Check(Providers.IsNoWider("acceptEdits", "claude", "onRequest", "codex") && Providers.IsNoWider("manual", "claude", "onRequest", "codex") && !Providers.IsNoWider("auto", "claude", "onRequest", "codex")
            && Providers.IsNoWider("auto", "claude", "acceptEdits", "codex"), "under Codex onRequest and acceptEdits");
        // A read-only Codex edits nothing: only a Claude plan child fits, and it may write once its plan is approved.
        Check(Providers.IsNoWider("plan", "claude", "manual", "codex") && Providers.ChildCanWrite("claude", "plan") && new[] { "manual", "acceptEdits", "auto", "fullAccess" }.All(mode => !Providers.IsNoWider(mode, "claude", "manual", "codex")), "under a read-only Codex");
        Same(Providers.WidestChildMode("claude", "codex", "manual"), "plan", "a read-only Codex's Claude child");
        Same(Providers.WidestChildMode("gemini", "codex", "manual"), null, "a read-only Codex's Antigravity child");
        // A mode that is not its own agent's, on either side, is never no wider.
        Check(!Providers.IsNoWider("onRequest", "claude", "fullAccess", "codex") && !Providers.IsNoWider("acceptEdits", "codex", "onRequest", "claude"), "a mode the agent lacks");
        Same(Providers.WidestChildMode("codex", "claude", "sideways"), null, "an unknown parent mode");
        Same(Providers.WidestChildMode("other", "claude", "fullAccess"), null, "an unknown child agent");
        // One agent's modes compare by its own order, exactly as before.
        foreach (var provider in Wire.Providers)
        {
            var modes = Providers.StartingModes(provider);
            for (var own = 0; own < modes.Count; own++)
                for (var asked = 0; asked < modes.Count; asked++)
                {
                    Same(Providers.IsNoWider(modes[asked], provider, modes[own], provider), asked <= own, $"{provider} {modes[asked]} under {modes[own]}");
                    Same(Providers.IsNoWider(modes[asked], modes[own], provider), asked <= own, $"{provider} {modes[asked]} under {modes[own]} (3 arguments)");
                }
        }
        // The human's child of the parent's agent starts as before; of another, in its widest mode, without the style's plan.
        Same(Providers.HumanChildMode("claude", "claude", "auto", false), "auto", "Claude's own child");
        Same(Providers.HumanChildMode("claude", "claude", "auto", true), "plan", "Claude's own child, plan first");
        Same(Providers.HumanChildMode("codex", "codex", "onRequest", true), "onRequest", "Codex has no plan mode");
        Same(Providers.HumanChildMode("codex", "codex", "manual", false), null, "a read-only Codex's own child");
        Same(Providers.HumanChildMode("gemini", "gemini", "acceptEdits", true), null, "Antigravity's plan child cannot write");
        Same(Providers.HumanChildMode("codex", "claude", "auto", true), "acceptEdits", "Claude auto's Codex child");
        Same(Providers.HumanChildMode("claude", "codex", "onRequest", true), "acceptEdits", "Codex onRequest's Claude child");
        Same(Providers.HumanChildMode("claude", "codex", "manual", false), "plan", "a read-only Codex's Claude child");
        Same(Providers.HumanChildMode("gemini", "claude", "plan", false), null, "Claude plan's Antigravity child");
        // Where the pane's own child cannot write, another agent's may.
        foreach (var (provider, mode) in new[] { ("codex", "manual"), ("gemini", "plan"), ("gemini", "manual") }) Check(Providers.MayHaveHumanChild("claude", provider, mode), $"{provider} {mode} may have a Claude child");
        Check(Providers.MayHaveHumanChild("claude", "claude", "plan") && !Providers.MayHaveHumanChild("shell", "claude", "fullAccess"), "only an agent pane");
        return Task.CompletedTask;
    }

    internal static Task AvailabilityTheMenuAndItsCap()
    {
        const DelegationAgentAvailability Ready = DelegationAgentAvailability.Ready, NotInstalled = DelegationAgentAvailability.NotInstalled, SignedOut = DelegationAgentAvailability.SignedOut;
        // Only what the app last learnt holds an agent back; what it could not read holds nothing back.
        Same(Providers.Availability(null, null), Ready, "nothing known");
        Same(Providers.Availability(true, new CliAccountStatus { Provider = "codex", LoggedIn = true }), Ready, "found and signed in");
        Same(Providers.Availability(true, new CliAccountStatus { Provider = "codex", LoggedIn = null }), Ready, "sign-in unreadable");
        Same(Providers.Availability(false, null), NotInstalled, "not found");
        Same(Providers.Availability(true, new CliAccountStatus { Provider = "codex", Installed = false }), NotInstalled, "says it is not installed");
        Same(Providers.Availability(null, new CliAccountStatus { Provider = "gemini", LoggedIn = false }), SignedOut, "signed out");

        // A Claude parent in auto: Claude is its own agent, so its CLI is not asked about.
        var states = new Dictionary<string, DelegationAgentAvailability> { ["claude"] = NotInstalled, ["codex"] = SignedOut, ["gemini"] = NotInstalled };
        SameList<DelegationChildChoice>(Providers.HumanChildChoices("claude", "auto", false, 0, agent => states[agent]),
            [new("claude", "auto", null), new("codex", "acceptEdits", DelegationChildBlock.SignedOut), new("gemini", "acceptEdits", DelegationChildBlock.NotInstalled)], "Claude auto's menu");
        var ready = Providers.HumanChildChoices("claude", "auto", true, 2, _ => Ready);
        SameList<string?>(ready.Select(choice => choice.Mode), ["plan", "acceptEdits", "acceptEdits"], "plan first stays with Claude's own child");
        Check(ready.All(choice => choice.Blocked is null), "two open children leave room");
        SameList<DelegationChildBlock?>(Providers.HumanChildChoices("claude", "manual", false, 0, _ => Ready).Select(choice => choice.Blocked), [null, DelegationChildBlock.NoMode, DelegationChildBlock.NoMode], "Claude manual gets no Codex or Antigravity child");
        SameList<DelegationChildChoice>(Providers.HumanChildChoices("codex", "manual", false, 0, _ => Ready),
            [new("claude", "plan", null), new("codex", null, DelegationChildBlock.NoMode), new("gemini", null, DelegationChildBlock.NoMode)], "a read-only Codex offers only a Claude plan child");
        var codex = Providers.HumanChildChoices("codex", "onRequest", false, 0, _ => Ready);
        SameList<string?>(codex.Select(choice => choice.Mode), ["acceptEdits", "onRequest", "acceptEdits"], "Codex onRequest's menu");
        Check(codex.All(choice => choice.Blocked is null), "Codex onRequest offers all three");
        SameList<DelegationChildBlock?>(Providers.HumanChildChoices("codex", "fullAccess", false, 3, _ => Ready).Select(choice => choice.Blocked), [DelegationChildBlock.Cap, DelegationChildBlock.Cap, DelegationChildBlock.Cap], "three open children, of any agents: none more");

        // The menu's cap counts the open children the human started from that parent, of every agent.
        DelegationChildRow[] rows =
        [
            new("a", "p", ChildState.Running, StartedByHuman: true, Agent: "codex"), new("b", "p", ChildState.Reported, StartedByHuman: true), new("c", "p", ChildState.Running),
            new("d", "p", ChildState.Closed, StartedByHuman: true, Agent: "gemini"), new("e", "q", ChildState.Running, StartedByHuman: true),
        ];
        Same(DelegationSidebar.OpenHumanChildCount("p", rows), 2, "open human children of p");
        var file = DelegationFile.Of([ChildRecord.Make("x", "p", "/w/x", "main", Commit, "acceptEdits", "k", startedByHuman: true, provider: "codex")]);
        Check(DelegationSidebar.Rows(file).Single() is { Agent: "codex", StartedByHuman: true }, "a row carries the human's mark and the child's agent");

        // The worktree button shows for its menu alone only where the pane's own child cannot write but another's can.
        DelegationChildChoice[] other = [new("codex", null, DelegationChildBlock.NoMode), new("claude", "plan", null)];
        bool MenuOnly(string provider, string mode, IReadOnlyList<DelegationChildChoice> choices, bool isChild = false, bool on = true, bool host = true) =>
            ComposerSecondSends.MenuOnly("claude", provider, mode, isChild, on, host, choices);
        Check(MenuOnly("codex", "manual", other), "a read-only Codex shows the menu alone");
        Check(!MenuOnly("codex", "manual", other, isChild: true) && !MenuOnly("codex", "manual", other, on: false) && !MenuOnly("codex", "manual", other, host: false), "not in a child, with the switch off, or without a checkout");
        Check(!MenuOnly("codex", "acceptEdits", other) && !MenuOnly("claude", "plan", other), "where the pane's own child can write, Ctrl+Enter and the click make one");
        foreach (var (provider, mode) in new[] { ("codex", "manual"), ("gemini", "plan"), ("gemini", "manual") })
        {
            var choices = Providers.HumanChildChoices(provider, mode, false, 0, _ => Ready);
            SameList(choices.Where(choice => choice.Blocked is null).Select(choice => choice.Agent), ["claude"], $"{provider} {mode}: only Claude");
            Same(choices[0].Mode, "plan", $"{provider} {mode}: in plan mode");
            Check(MenuOnly(provider, mode, choices), $"{provider} {mode}: the menu alone");
            var without = Providers.HumanChildChoices(provider, mode, false, 0, agent => agent == "claude" ? NotInstalled : Ready);
            Check(!MenuOnly(provider, mode, without), $"{provider} {mode}: nothing to run, no button");
        }
        // A text for another agent goes back to the composer, never to the pane.
        Check(ComposerSecondSends.ReturnsToComposer("codex", "claude") && ComposerSecondSends.ReturnsToComposer("claude", "gemini")
            && !ComposerSecondSends.ReturnsToComposer(null, "claude") && !ComposerSecondSends.ReturnsToComposer("codex", "codex"), "who gets a text whose child did not start");
        return Task.CompletedTask;
    }

    private static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    internal static Task TheToolTakesAnOptionalAgentAndNamesItEverywhere()
    {
        Same(DelegationNames.Of(DelegationReasonCode.AgentUnknown), "agent_unknown", "agent_unknown on the wire");
        Same(DelegationNames.Of(DelegationReasonCode.AgentUnavailable), "agent_unavailable", "agent_unavailable on the wire");
        // delegate takes an optional agent and no other new argument.
        var delegate_ = DelegationToolManifest.Delegate;
        SameList(delegate_.Arguments.Select(argument => argument.Name), ["task", "mode", "agent"], "delegate's arguments");
        SameList(delegate_.Arguments.Select(argument => argument.Required), [true, true, false], "only agent is optional");
        Check(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["mode"] = "plan" }, delegate_) is null
            && DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["mode"] = "plan", ["agent"] = "codex" }, delegate_) is null, "with or without agent");
        const string problem = "delegate takes exactly these string arguments: task, mode, and optionally agent.";
        Same(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["agent"] = "codex" }, delegate_), problem, "mode is still required");
        Same(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["mode"] = "plan", ["model"] = "opus" }, delegate_), problem, "no other argument");
        Check(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["child"] = "c" }, DelegationToolManifest.ChildStatus) is null, "child_status takes its child");
        Same(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string>(), DelegationToolManifest.ChildStatus), "child_status takes exactly these string arguments: child.", "child_status without its child");
        SameList(DelegationMcp.Definition(delegate_)["inputSchema"]!["required"]!.AsArray().Select(name => name!.GetValue<string>()), ["task", "mode"], "the schema requires task and mode");
        foreach (var agent in new[] { "Claude", "Codex", "Antigravity" }) Check(delegate_.Arguments[1].Description.Contains(agent + " child", StringComparison.Ordinal), "mode names a " + agent + " child's modes");

        // A refusal names what it is about after its one code.
        var detail = DelegationCoordinator.Unavailable("codex", DelegationAgentAvailability.SignedOut) ?? throw new InvalidOperationException("no detail for a signed-out agent");
        Same(detail, "Codex is not signed in on this PC; the user can check it in Mighty Claude's Settings › CLI", "a signed-out agent's detail");
        Check(DelegationCoordinator.Unavailable("gemini", DelegationAgentAvailability.NotInstalled)?.EndsWith("Settings › CLI", StringComparison.Ordinal) == true
            && DelegationCoordinator.Unavailable("codex", DelegationAgentAvailability.Ready) is null, "a missing agent's detail, and none for a ready one");
        var refused = DelegationMcp.Result(DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, detail));
        Same(refused["structuredContent"]!.ToJsonString(), """{"refused":"agent_unavailable"}""", "the refusal is tool data");
        Same(TextOf(refused), $"Refused: agent_unavailable ({detail}). Nothing was changed.", "the refusal's text names the agent");
        Same(TextOf(DelegationMcp.Result(DelegationResponse.Refusal(DelegationReasonCode.AgentUnknown))), "Refused: agent_unknown (agent must be claude, codex or antigravity, or left out for your own). Nothing was changed.", "agent_unknown says what to do");

        // The request key adds the child's agent only when it is not the caller's own.
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits"), "0767d17eeeaaa20c3f5c0e3a11dc260bd005cc116a76ea02adb2c78fb3a48ef7", "the caller's own agent keeps its key");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits", null), "0767d17eeeaaa20c3f5c0e3a11dc260bd005cc116a76ea02adb2c78fb3a48ef7", "no other agent is no fifth part");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits", "codex"), "f051d5f56c52b257a1483760fee991d3622cd74bfb03cd1a90da23be50177e8b", "a Codex child's key");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits", "gemini"), "28e97194accdf6d2519a39c9a1144dda67ee4d149fd3193204edc986fc040e3c", "an Antigravity child's key");

        // delegate's child, list_children and child_status name the child's agent by its word.
        var gemini = ChildRecord.Make("c1", "p", "/w/c1", "main", Commit, "acceptEdits", "k", state: ChildState.Running, provider: "gemini");
        Same(DelegationChildInfo.Of(gemini).Agent, "antigravity", "delegate's child");
        Same(DelegationChildSummary.Of(gemini).Agent, "antigravity", "list_children's row");
        Same(DelegationChildStatus.Of(gemini, null).Agent, "antigravity", "child_status");
        Same(DelegationChildInfo.Of(gemini with { Provider = null }).Agent, "claude", "a Claude child");
        var made = DelegationMcp.Result(DelegationResponse.Delegated(gemini));
        Same(made["structuredContent"]!["child"]!.ToJsonString(), """{"id":"c1","state":"running","agent":"antigravity","branch":"mighty/c1","mode":"acceptEdits"}""", "the child is tool data with its agent");
        Same(TextOf(made), "Child c1 is running, runs antigravity, on the branch mighty/c1, starting in acceptEdits mode.", "the child's text names its agent");
        var listed = DelegationMcp.Result(new(Children: [DelegationChildSummary.Of(gemini)]));
        Same(TextOf(listed), "Your children:\n- c1: antigravity, running, report revision 0", "the list names each agent");
        Same(listed["structuredContent"]!["children"]![0]!.ToJsonString(), """{"id":"c1","agent":"antigravity","state":"running","revision":0}""", "the list is tool data with each agent");
        var status = DelegationMcp.Result(new(Status: DelegationChildStatus.Of(gemini, null)));
        Same(TextOf(status), "Child c1 runs antigravity and is running. It has not reported yet.", "the status names the agent");
        Same(status["structuredContent"]!["status"]!["agent"]!.GetValue<string>(), "antigravity", "the status is tool data with its agent");
        Check(JsonSerializer.Serialize(DelegationChildInfo.Of(gemini), Wire.Json).Contains("\"agent\":\"antigravity\"", StringComparison.Ordinal), "the pane pipe carries the agent");
        return Task.CompletedTask;
    }

    internal static Task AChildsWordsFollowItsAgent()
    {
        var worktree = new ChildWorktree("c1", @"C:\w\c1", @"C:\w\c1", "mighty/c1", "main", Commit);
        var codexOfClaude = DelegationCoordinator.FirstInput("Do it.", worktree, "codex", "claude");
        Check(codexOfClaude.StartsWith("Another Claude pane", StringComparison.Ordinal) && codexOfClaude.Contains("Do not commit", StringComparison.Ordinal), "a Codex child of a Claude pane");
        var claudeOfCodex = DelegationCoordinator.FirstInput("Do it.", worktree, "claude", "codex");
        Check(claudeOfCodex.StartsWith("Another pane", StringComparison.Ordinal) && claudeOfCodex.Contains("commit your work", StringComparison.Ordinal) && !claudeOfCodex.Contains("Do not commit", StringComparison.Ordinal), "a Claude child of a Codex pane");
        // The parent's own agent says what it always said.
        Same(DelegationCoordinator.FirstInput("Do it.", worktree, "codex", "codex"), DelegationCoordinator.FirstInput("Do it.", worktree, "codex"), "a Codex child of a Codex pane");
        Same(DelegationCoordinator.FirstInput("Do it.", worktree, "claude", "claude"), DelegationCoordinator.FirstInput("Do it.", worktree), "a Claude child of a Claude pane");
        var note = DelegationCoordinator.HumanNote(worktree, "gemini", "claude");
        Check(note.Contains("beside another Claude pane", StringComparison.Ordinal) && note.Contains("Do not commit", StringComparison.Ordinal), "the note to an Antigravity child of a Claude pane");
        Check(DelegationCoordinator.HumanNote(worktree, "claude", "codex").Contains("beside another pane so", StringComparison.Ordinal), "the note to a Claude child of a Codex pane");
        Same(DelegationCoordinator.HumanFirstRun("Fix it.", worktree, "gemini", "claude").Input, "Fix it.\n\n" + note, "the human's first run carries that note");
        return Task.CompletedTask;
    }

    internal static Task AChildOfAnotherAgentTakesThatAgentsDefaults()
    {
        var parent = new RunSession { Id = "parent", WorkspaceId = "ws", Kind = "claude", Provider = "claude", Model = "opus", Settings = new(Effort: "high", PermissionMode: "auto"), AgentViewMode = "mighty", GraphViewMode = "timeline", MightyStyle = "superpowers", MightyStyleHash = "h1" };
        var codexPane = new RunSession { Id = "codex-pane", WorkspaceId = "ws", Kind = "claude", Provider = "codex", Model = "gpt-5.6-sol", Settings = new(Effort: "low", PermissionMode: "fullAccess") };
        foreach (var human in new[] { true, false })
        {
            var pane = new DelegationChildPane("c1", "parent", "onRequest", "/w/c1", StartedByHuman: human, Provider: "codex");
            var child = DelegationPanes.ChildSession(pane, "ws", "Fix it", parent, codexPane);
            Check(child.Provider == "codex" && child.Settings.PermissionMode == "onRequest", $"human {human}: a Codex child in its starting mode");
            // The human's child takes what a new Codex pane would; one the parent's delegate made, plain defaults.
            Check(human ? child.Model == "gpt-5.6-sol" && child.Settings.Effort == "low" : child.Model == "default" && child.Settings.Effort == "default", $"human {human}: model and effort");
            Check(child is { AgentViewMode: "mighty", GraphViewMode: "timeline", MightyStyle: null, MightyStyleHash: null }, $"human {human}: the parent's view, no style");
            // With no pane of the agent yet, a new pane's defaults.
            var fresh = DelegationPanes.ChildSession(pane, "ws", "Fix it", parent);
            Check(fresh.Model == "default" && fresh.Settings.Effort == "default", $"human {human}: no pane of the agent yet");
        }
        // A Claude child of a Codex parent the human started: Claude's last-used model and effort, not the Codex model.
        var claude = DelegationPanes.ChildSession(new("c2", "codex-pane", "fullAccess", "/w/c2", StartedByHuman: true), "ws", "t", codexPane, parent);
        Check(claude is { Provider: "claude", Model: "opus", MightyStyle: null } && claude.Settings.Effort == "high", "a Claude child of a Codex pane");
        // The app finds that agent's last-used top-level pane itself.
        var state = new AppSnapshot { Workspaces = [new Workspace { Id = "ws", Path = Path.GetTempPath() }], Sessions = [parent, codexPane], ActiveWorkspaceId = "ws", ActiveSessionId = "parent" };
        var next = DelegationPanes.AddingChild(state, new("c3", "parent", "acceptEdits", Path.GetTempPath(), StartedByHuman: true, Provider: "codex"), "Try it") ?? throw new InvalidOperationException("no child pane");
        Check(next.Sessions.Single(session => session.Id == "c3") is { Provider: "codex", Model: "gpt-5.6-sol" } made && made.Settings is { Effort: "low", PermissionMode: "acceptEdits" }, "the Codex template's model and effort, its own mode");
        return Task.CompletedTask;
    }

    /// <summary>A temp repository whose pane "parent", running <paramref name="Provider"/> in <paramref name="Mode"/>, calls the tools from a run started in that mode.</summary>
    private sealed record CrossPlace(DelegationPlace Files, DelegationTestHost Host, DelegationCoordinator Coordinator, string Provider, string Mode)
    {
        internal byte[] Bytes() => File.ReadAllBytes(Files.Store.FilePath);
        internal Task<DelegationResponse> Call(string tool, params (string Name, string Value)[] arguments) =>
            Coordinator.Handle(Request(tool, arguments), new DelegationCaller("parent", "claude", Provider, Delegation: true, PermissionMode: Mode));
        /// <summary>The human's send of <paramref name="text"/> from the parent's worktree menu, naming <paramref name="agent"/>; null is Ctrl+Enter.</summary>
        internal Task<HumanChildStart> Send(string text, string? agent, bool planFirst = false) =>
            Coordinator.StartHumanChild(text, "parent", new((childId, _) => Task.FromResult<string?>("send-run-of-" + childId), _ => Task.CompletedTask), planFirst, agent);
        internal Task<bool> WaitFor(string id, ChildState state) => Eventually(async () => Coordinator.File.Children.Find(child => child.Id == id)?.State == state && !await Coordinator.IsStarting(id));
        /// <summary>A refused call made nothing: the same file, no worktree, no pane or run.</summary>
        internal void NothingMade(byte[] before) =>
            Check(Bytes().SequenceEqual(before) && !Directory.Exists(Files.Root) && Host.CreatedPanes.Count == 0 && Host.StartedRuns.Count == 0, "the refused call made nothing");
        /// <summary>The child's run finished with its report written.</summary>
        internal async Task Report(ChildRecord child, string runId)
        {
            File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\n\nDone.\n");
            await Coordinator.ChildRunEnded(child.Id, runId, ChildRunEnd.Finished);
        }
        internal void Remove() => Files.Remove();
    }

    private static async Task<CrossPlace> Cross(string provider, string mode)
    {
        var files = new DelegationPlace();
        var store = new DelegationFileStore(files.Profile); store.Save(DelegationFile.Empty);
        var host = new DelegationTestHost(store) { FirstRun = id => "run-of-" + id, MakesPanes = true };
        Directory.CreateDirectory(files.Repo); await files.Repository(1);
        host.Set(new DelegationPaneState("parent", mode, files.Repo, Provider: provider, RunId: "run-1", Activity: DelegationPaneActivity.Running));
        return new(files, host, new(store, host, new ChildWorktreeMaker(files.Root, _ => 50_000_000_000), () => true), provider, mode);
    }

    private static ChildRecord Made(HumanChildStart answer) => answer is HumanChildStart.Started { Child: var child } ? child : throw new InvalidOperationException("No child was made: " + answer);

    internal static async Task AClaudeParentsCodexChildGetsTheAppsCommit()
    {
        var place = await Cross("claude", "auto");
        try
        {
            var start = await place.Files.Git("rev-parse", "HEAD");
            var info = (await place.Call("delegate", ("task", "Add a note."), ("mode", "onRequest"), ("agent", "codex"))).Child ?? throw new InvalidOperationException("delegate made no Codex child");
            Check(info is { Agent: "codex", StartingMode: "onRequest" } && await place.WaitFor(info.Id, ChildState.Running), "a Codex child in onRequest runs");
            var child = place.Coordinator.File.Children.Single();
            Check(child is { Provider: "codex", Agent: "codex", NotifiesParent: true }, "its record runs Codex and notifies its parent");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["codex"], "its pane runs Codex");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Mode), ["onRequest"], "in onRequest");
            SameList(place.Host.AvailabilityAsked, ["codex"], "the host was asked about Codex once");
            var first = place.Host.StartedRuns.Single().Input;
            Check(first.StartsWith("Another Claude pane", StringComparison.Ordinal) && first.Contains("Do not commit", StringComparison.Ordinal), "a Codex child's first words, from a Claude pane");
            // The app commits what it left when its report changed.
            File.WriteAllText(Path.Combine(child.WorktreePath, "note.txt"), "Note\n");
            await place.Report(child, "run-of-" + child.Id);
            var head = await place.Files.GitIn(child.WorktreePath, "rev-parse", "HEAD");
            Check(head != start && await place.Files.GitIn(child.WorktreePath, "log", "-1", "--format=%s") == ChildAutoCommit.Message(child.Id, 1) && place.Coordinator.File.Children.Single().ReportHead == head, "the app committed the Codex child's work");
            // The parent's tools name its agent, and its notice goes to the parent like any other.
            SameList((await place.Call("list_children")).Children!.Select(listed => listed.Agent), ["codex"], "list_children");
            Same((await place.Call("child_status", ("child", child.Id))).Status?.Agent, "codex", "child_status");
            SameList(place.Coordinator.File.Notices.Select(notice => notice.ChildId), [child.Id], "one notice for the parent");
        }
        finally { place.Remove(); }
    }

    internal static async Task ACodexParentsClaudeChildCommitsItsOwnWork()
    {
        var place = await Cross("codex", "acceptEdits");
        try
        {
            var start = await place.Files.Git("rev-parse", "HEAD");
            var info = (await place.Call("delegate", ("task", "Add a note."), ("mode", "acceptEdits"), ("agent", "claude"))).Child ?? throw new InvalidOperationException("delegate made no Claude child");
            Check(info.Agent == "claude" && await place.WaitFor(info.Id, ChildState.Running), "a Claude child runs");
            var child = place.Coordinator.File.Children.Single();
            // A Claude child's record keeps no provider, as every Claude child's.
            Check(child is { Provider: null, Agent: "claude" }, "its record is a Claude child's");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["claude"], "its pane runs Claude");
            var first = place.Host.StartedRuns.Single().Input;
            Check(first.StartsWith("Another pane", StringComparison.Ordinal) && first.Contains("commit your work", StringComparison.Ordinal) && !first.Contains("Do not commit", StringComparison.Ordinal), "a Claude child's first words, from a Codex pane");
            // What it leaves uncommitted stays so: the app never commits for a Claude child.
            File.WriteAllText(Path.Combine(child.WorktreePath, "note.txt"), "Note\n");
            await place.Report(child, "run-of-" + child.Id);
            Same(await place.Files.GitIn(child.WorktreePath, "rev-parse", "HEAD"), start, "no app commit");
            Same(place.Coordinator.File.Children.Single().ReportHead, start, "the report is at the start");
            // The Codex parent still may not merge by tool outside fullAccess.
            Same(await place.Call("merge", ("child", child.Id), ("expected_head", start)), DelegationResponse.Refusal(DelegationReasonCode.MergeHumanOnly), "merges follow the parent's agent");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheWrongAgentOrModeIsRefusedAndNothingIsMade()
    {
        var place = await Cross("claude", "acceptEdits");
        try
        {
            place.Host.SetAgent("gemini", DelegationAgentAvailability.NotInstalled);
            place.Host.SetAgent("codex", DelegationAgentAvailability.SignedOut);
            var before = place.Bytes();
            var unknown = await place.Call("delegate", ("task", "Do it."), ("mode", "acceptEdits"), ("agent", "gpt"));
            Check(unknown.Refused == DelegationReasonCode.AgentUnknown && unknown.Error?.Contains("\"gpt\"", StringComparison.Ordinal) == true, "an unknown agent");
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "acceptEdits"), ("agent", "antigravity")),
                DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, "Antigravity is not installed on this PC; the user can check it in Mighty Claude's Settings › CLI"), "an agent not installed");
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "onRequest"), ("agent", "codex")),
                DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, "Codex is not signed in on this PC; the user can check it in Mighty Claude's Settings › CLI"), "an agent signed out");
            // A mode that is not the agent's, or one in which its child cannot write: an error.
            Same((await place.Call("delegate", ("task", "Do it."), ("mode", "auto"), ("agent", "codex"))).Error, "mode must be one of a Codex child's: manual, acceptEdits, onRequest, fullAccess.", "not a Codex mode");
            Check((await place.Call("delegate", ("task", "Do it."), ("mode", "manual"), ("agent", "codex"))).Error?.StartsWith("A Codex child in manual mode cannot change files", StringComparison.Ordinal) == true, "a read-only Codex child");
            Check((await place.Call("delegate", ("task", "Do it."), ("mode", "plan"), ("agent", "gemini"))).Error?.StartsWith("An Antigravity child in plan mode cannot change files", StringComparison.Ordinal) == true, "an Antigravity plan child");
            // Claude acceptEdits asks before Bash: a Codex child that runs commands unasked, or has full access, is wider.
            place.Host.SetAgent("codex", DelegationAgentAvailability.Ready);
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "acceptEdits"), ("agent", "codex")), DelegationResponse.Refusal(DelegationReasonCode.WiderMode), "Codex acceptEdits is wider");
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "fullAccess"), ("agent", "codex")), DelegationResponse.Refusal(DelegationReasonCode.WiderMode), "Codex fullAccess is wider");
            place.NothingMade(before);
            Check(!place.Host.AvailabilityAsked.Contains("claude"), "an agent the parent runs itself is never asked about");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheCapCountsEveryAgent()
    {
        var place = await Cross("claude", "fullAccess");
        try
        {
            var own = (await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"))).Child ?? throw new InvalidOperationException("no own child");
            var codex = (await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", "codex"))).Child ?? throw new InvalidOperationException("no Codex child");
            Check(own.Id != codex.Id && own.Agent == "claude" && codex.Agent == "codex", "the same call for another agent is another child");
            // Naming the caller's own agent is leaving it out; repeats get the same children.
            Same((await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", "claude"))).Child?.Id, own.Id, "agent claude is the caller's own");
            Same((await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", " "))).Child?.Id, own.Id, "a blank agent is the caller's own");
            Same((await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", "CODEX"))).Child?.Id, codex.Id, "the repeat for Codex");
            var gemini = (await place.Call("delegate", ("task", "Same task."), ("mode", "fullAccess"), ("agent", "antigravity"))).Child ?? throw new InvalidOperationException("no Antigravity child");
            Check(gemini is { Agent: "antigravity", StartingMode: "fullAccess" }, "an Antigravity child in fullAccess");
            Same(await place.Call("delegate", ("task", "A fourth."), ("mode", "acceptEdits"), ("agent", "codex")), DelegationResponse.Refusal(DelegationReasonCode.WidthCap), "three open children of any agents");
            foreach (var info in new[] { own, codex, gemini }) Check(await place.WaitFor(info.Id, ChildState.Running), info.Agent + " runs");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["claude", "codex", "gemini"], "each pane runs its agent");
        }
        finally { place.Remove(); }
    }

    private static Dictionary<string, string> AgentWords(string label) => new() { ["agent"] = label };

    internal static async Task TheHumansMenuStartsTheNamedAgentsChild()
    {
        var place = await Cross("claude", "auto");
        try
        {
            // The parent's style starts in plan, which stays with Claude's own children.
            var codex = Made(await place.Send("Try it in Codex.", "codex", planFirst: true));
            Check(codex is { Provider: "codex", StartingMode: "acceptEdits", StartedByHuman: true }, "a Codex child in its widest mode");
            var gemini = Made(await place.Send("Try it in Antigravity.", "gemini"));
            Check(gemini is { Provider: "gemini", StartingMode: "acceptEdits" }, "an Antigravity child");
            var claude = Made(await place.Send("Plan it in Claude.", null, planFirst: true));
            Check(claude is { Provider: null, StartingMode: "plan" }, "Ctrl+Enter's Claude child in plan");
            foreach (var id in new[] { codex.Id, gemini.Id, claude.Id }) Check(await place.WaitFor(id, ChildState.Running), id + " runs");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["codex", "gemini", "claude"], "each pane runs its agent");
            Check(place.Host.CreatedPanes.All(pane => pane.StartedByHuman), "each is the human's");
            // Three open children the human started, of any agents: the cap.
            Same(await place.Send("One more.", "codex"), new HumanChildStart.Refused(DelegationReasonCode.WidthCap), "the cap");
            // The parent's tools still neither list nor act on them.
            Same((await place.Call("list_children")).Children!.Count, 0, "the parent lists none of them");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheHumansMenuSaysWhy()
    {
        // A Claude pane in manual mode asks before each edit: no Codex child may edit unasked under it.
        var place = await Cross("claude", "manual");
        try
        {
            var before = place.Bytes();
            Same(await place.Send("Try it.", "codex"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNoMode", AgentWords("Codex"))), "no Codex mode fits");
            place.Host.SetAgent("gemini", DelegationAgentAvailability.SignedOut);
            Same(await place.Send("Try it.", "gemini"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentSignedOut", AgentWords("Antigravity"))), "Antigravity is signed out");
            place.Host.SetAgent("codex", DelegationAgentAvailability.NotInstalled);
            Same(await place.Send("Try it.", "codex"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNotInstalledWindows", AgentWords("Codex"))), "Codex is not installed");
            var words = Locale.Get("delegation.human.error.agentNotInstalledWindows", AgentWords("Codex"));
            Check(words.Contains("› CLI", StringComparison.Ordinal) && !words.Contains("Mac", StringComparison.Ordinal), "the words point to Settings › CLI and name no Mac");
            Same(await place.Send("Try it.", "other"), new HumanChildStart.Refused(DelegationReasonCode.AgentUnknown), "no such agent");
            place.NothingMade(before);
        }
        finally { place.Remove(); }

        // A read-only Codex pane gets a Claude child only in plan mode.
        var readOnly = await Cross("codex", "manual");
        try
        {
            var claude = Made(await readOnly.Send("Plan it.", "claude"));
            Check(claude is { Provider: null, StartingMode: "plan" }, "a Claude plan child of a read-only Codex");
            Same(await readOnly.Send("Try it.", "gemini"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNoMode", AgentWords("Antigravity"))), "no Antigravity mode fits");
        }
        finally { readOnly.Remove(); }
    }

    internal static async Task ThePanesOwnAgentIsNeverAskedAbout()
    {
        var place = await Cross("claude", "auto");
        try
        {
            // Even what the app last learnt against Claude does not hold back a child of the running Claude pane.
            place.Host.SetAgent("claude", DelegationAgentAvailability.NotInstalled);
            var first = Made(await place.Send("First.", null));
            var second = Made(await place.Send("Second.", "claude"));
            foreach (var id in new[] { first.Id, second.Id }) Check(await place.WaitFor(id, ChildState.Running), id + " runs");
            Same(place.Host.AvailabilityAsked.Count, 0, "the pane's own agent is never asked about");
            // Another agent is asked about once per send.
            _ = await place.Send("Third.", "codex");
            SameList(place.Host.AvailabilityAsked, ["codex"], "Codex asked once");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheServiceOffersTheMenuAndStartsAnotherAgentsChild()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            await using var s = await DelegationLaunch.Start(place.Profile, Path.Combine(place.Base, "fake"), place.Repo, place.Root, parentMode: "auto");
            // The Claude parent's model, the Codex pane used last, and a read-only Codex pane.
            await s.Service.UpdateAsync(state => state with
            {
                Sessions =
                [
                    .. state.Sessions.Select(pane => pane.Id == DelegationLaunch.Parent ? pane with { Model = "opus", Settings = pane.Settings with { Effort = "high" } } : pane),
                    new RunSession { Id = "codex-manual", WorkspaceId = s.Workspace.Id, Kind = "claude", Provider = "codex", Settings = new(PermissionMode: "manual") },
                    new RunSession { Id = "codex-template", WorkspaceId = s.Workspace.Id, Kind = "claude", Provider = "codex", Model = "gpt-5.6-sol", Settings = new(Effort: "low", PermissionMode: "fullAccess"), Logs = [new("l", "user", "hi", "2099-01-01T00:00:00Z")] },
                ],
            });
            s.Service.AgentAvailabilityNow = provider => provider == "gemini" ? DelegationAgentAvailability.NotInstalled : DelegationAgentAvailability.Ready;
            SameList<DelegationChildChoice>(s.Service.WorktreeChildChoices(s.Pane(DelegationLaunch.Parent), false),
                [new("claude", "auto", null), new("codex", "acceptEdits", null), new("gemini", "acceptEdits", DelegationChildBlock.NotInstalled)], "a Claude pane in auto offers its menu from what the window knows");
            // The read-only Codex pane: its own child cannot write, a Claude plan child can, so the button shows for its menu alone.
            var manual = s.Pane("codex-manual");
            s.Service.RefreshWorktreeReadiness(manual);
            Check(await Eventually(() => s.Service.WorktreeMenuOnly(manual, false)) && s.Service.SecondSend(manual) == ComposerSecondSend.Queue && s.Service.OffersWorktreeMenu(manual, false), "a read-only Codex pane offers the menu alone");
            Check(!s.Service.WorktreeMenuOnly(s.Pane(DelegationLaunch.Parent), false), "a Claude pane's own child can write: not the menu alone");
            // Another agent's child the human starts takes that agent's last-used pane's model and effort, in its widest mode.
            var answer = await s.Service.StartHumanChildAsync(DelegationLaunch.Parent, "Try it in Codex.", [], false, _ => Task.CompletedTask, "codex");
            var child = Made(answer);
            Check(child is { Provider: "codex", StartingMode: "acceptEdits", StartedByHuman: true }, "a Codex child in acceptEdits");
            Check(await Eventually(() => s.Service.Snapshot.Sessions.Any(pane => pane.Id == child.Id)), "its pane opens");
            Check(s.Pane(child.Id) is { Provider: "codex", Model: "gpt-5.6-sol", ParentSessionId: DelegationLaunch.Parent } made && made.Settings is { Effort: "low", PermissionMode: "acceptEdits" }, "it takes the Codex template's model and effort");
            Check(s.Service.DelegationChildren.Single(row => row.Id == child.Id) is { Agent: "codex", StartedByHuman: true }, "its row names its agent");
            Same(await s.Service.StartHumanChildAsync(DelegationLaunch.Parent, "Try it in Antigravity.", [], false, _ => Task.CompletedTask, "gemini"),
                new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNotInstalledWindows", AgentWords("Antigravity"))), "an agent the window found not installed is refused in the app's words");
        }
        finally { place.Remove(); }
    }
}
