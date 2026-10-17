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
}
