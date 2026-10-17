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
}
