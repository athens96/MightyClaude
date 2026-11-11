using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// The Mighty graph of an ACP agent's run (opencode, Hermes): every shared recording (native/contracts/fixtures/opencode-acp-*.jsonl,
/// hermes-acp-*.jsonl) replayed through the channel, the stream parser and the graph tracker wired as <see cref="RunManager"/> wires them, against
/// native/contracts/fixtures/acp-graphs.json, which the Mac's AcpGraphTests produce from the same recordings: one block per request with the streamed
/// answer, opencode's tokens and Hermes's none, a sub-agent's text kept out, the run's end.
/// </summary>
internal static class AcpGraphVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyCode.Core.Tests.AcpGraphs.json";
        using var stream = typeof(AcpGraphVerification).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded in MightyCode.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static T Korean<T>(Func<T> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "ko"; Locale.ResetCache();
        try { return body(); } finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    /// <summary>What the tracker emitted last for each block of a case, in the order the blocks first came, and how the run ended.</summary>
    private static JsonObject Blocks(JsonElement item)
    {
        var provider = item.GetProperty("provider").GetString()!; var hermes = provider == "hermes";
        var fields = item.GetProperty("request");
        var request = AcpVerification.Request(provider, fields.TryGetProperty("input", out var input) ? input.GetString()! : "hi there",
            settings: new RunSettings(PermissionMode: fields.TryGetProperty("permissionMode", out var mode) ? mode.GetString()! : "manual"),
            resume: fields.TryGetProperty("resumeId", out var resume) ? resume.GetString() : null);
        var emitted = new List<ExecutionGraphNode>();
        ExecutionGraphTracker? tracker = null;
        // As RunManager wires a run: the parser's shown reply is the graph's answer, a tool's activity goes by the graph first.
        var parser = new OutputParser(provider, (_, _) => { }, _ => { }, activityNamespace: "run-" + provider, answer: text => tracker?.AntigravityAnswer(text),
            graphActivity: (value, toolId) => tracker?.Activity(value, toolId) == true);
        tracker = new ExecutionGraphTracker("run-" + provider, request.Input, provider, null, emitted.Add);
        var channel = new AcpChannel("run-" + provider, request.ResumeId, "/tmp/project", AcpChannel.PromptText(request.Input, [], null), [],
            hermes ? HermesAgent.ConfigValues(request) : OpencodeAgent.ConfigValues(request), _ => { },
            line => { using (var document = JsonDocument.Parse(line)) tracker.Consume(document.RootElement); parser.Parse(line); },
            _ => { }, (p, state) => parser.PermissionActivity(p, state), _ => { }, _ => { }, () => { }, freeChoices: hermes);
        channel.Start();
        foreach (var line in AcpVerification.Fixture(provider, item.GetProperty("recording").GetString()!)) channel.Receive(line);
        parser.Flush(); parser.FinishActivities(false);
        // As the run ends (RunManager): completed when the turn completed and nothing failed.
        var state = channel.TurnCompleted && !channel.Failed && !parser.Failed ? "completed" : "error";
        tracker.Finish(state);
        var order = new List<string>(); var last = new Dictionary<string, ExecutionGraphNode>(StringComparer.Ordinal);
        foreach (var node in emitted) { if (!last.ContainsKey(node.Id)) order.Add(node.Id); last[node.Id] = node; }
        var blocks = new JsonArray();
        foreach (var node in order.Select(id => last[id]))
        {
            var block = new JsonObject { ["kind"] = node.Kind, ["state"] = node.State, ["title"] = node.Title };
            if (node.Input is { } asked) block["input"] = asked;
            if (node.Output is { } answer) block["output"] = answer;
            if (node.Usage is { } usage)
                block["usage"] = new JsonObject { ["inputTokens"] = usage.InputTokens, ["outputTokens"] = usage.OutputTokens, ["cacheReadTokens"] = usage.CacheReadTokens, ["cacheCreationTokens"] = usage.CacheCreationTokens };
            blocks.Add(block);
        }
        return new JsonObject { ["state"] = state, ["blocks"] = blocks };
    }

    internal static Task EveryRecordingIsTheMacsGraph()
    {
        var cases = Fixture.Value.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Count == 16, "the fixture has its sixteen recordings; got " + cases.Count);
        foreach (var item in cases)
        {
            var name = item.GetProperty("provider").GetString() + "-acp-" + item.GetProperty("recording").GetString();
            var made = Korean(() => Blocks(item));
            Check(JsonNode.DeepEquals(made, JsonNode.Parse(item.GetProperty("expect").GetRawText())),
                $"{name} differs; Windows makes: {made.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}");
        }
        return Task.CompletedTask;
    }

    /// <summary>The panes that draw the graph: opencode and Hermes beside the four, as on the Mac (MightyGraphSupport.providers), and their request
    /// block is titled by the agent's own name.</summary>
    internal static Task OpencodeAndHermesPanesDrawTheGraph()
    {
        Check(MightyGraphSupport.Providers.SequenceEqual(["claude", "codex", "gemini", "pi", "opencode", "hermes"]), "every agent's pane draws the graph");
        foreach (var provider in new[] { "opencode", "hermes" })
        {
            Check(MightyGraphViewModel.SupportsMighty(new RunSession { Kind = "claude", Provider = provider }) && MightyGraphViewModel.ViewMode("claude", provider) == "mighty", provider + " opens in the Mighty view");
            var nodes = new List<ExecutionGraphNode>();
            _ = new ExecutionGraphTracker("r", "hi", provider, null, nodes.Add);
            Check(nodes.Single().Title == ProviderMark.Label(provider), provider + "'s request block carries its name: " + nodes.Single().Title);
        }
        return Task.CompletedTask;
    }
}
