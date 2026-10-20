using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>native/contracts/fixtures/remote-v2/ read by the Windows Core: every body decodes into the Windows wire type and encodes back to the Mac's own JSON (null values aside).</summary>
internal static class RemoteWireFixtureVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static JsonNode Node(string name)
    {
        using var stream = typeof(RemoteWireFixtureVerification).Assembly.GetManifestResourceStream(ResourcePrefix + name) ?? throw new InvalidOperationException(name + " is not embedded");
        return JsonNode.Parse(stream)!;
    }
    /// <summary>Nulls removed, so an explicit null and a missing key compare the same (the Mac's rule in RemoteWireFixtureTests).</summary>
    internal static JsonNode? Plain(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(p => p.Value is not null).Select(p => KeyValuePair.Create(p.Key, Plain(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Plain).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
    internal static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Plain(a), Plain(b));
    private static T Decode<T>(string name) => Node(name).Deserialize<T>(RemoteWire.Json) ?? throw new InvalidOperationException(name + " decodes to null");
    private static void RoundTrips<T>(string name) => Check(Same(Node(name), JsonSerializer.SerializeToNode(Decode<T>(name), RemoteWire.Json)), name + " changed through " + typeof(T).Name + ": " + JsonSerializer.Serialize(Decode<T>(name), RemoteWire.Json));

    internal static Task EveryBodyIsTheMacsShape()
    {
        RoundTrips<RemoteWireInfo>("info.json");
        RoundTrips<RemoteWireInfo>("info-darwin.json");
        RoundTrips<RemoteWireStart>("start.json");
        RoundTrips<RemoteWireStart>("start-codex.json");
        RoundTrips<RemoteWireJob>("start-accepted.json");
        RoundTrips<RemoteWirePoll>("poll.json");
        RoundTrips<RemoteWirePoll>("poll-gap.json");
        RoundTrips<RemoteWirePoll>("poll-malformed.json");
        RoundTrips<RemoteWirePoll>("poll-requests.json");
        RoundTrips<RemoteWireSteer>("steer.json");
        RoundTrips<RemoteWirePermission>("permission.json");
        RoundTrips<RemoteWireQuestions>("questions.json");
        RoundTrips<RemoteWirePlan>("plan-approved.json");
        RoundTrips<RemoteWirePlan>("plan-revised.json");
        RoundTrips<RemoteWireAnswer>("answer.json");
        RoundTrips<RemoteWireAnswer>("answer-declined.json");
        RoundTrips<RemoteWireError>("error.json");
        Check(!Decode<RemoteWireAnswer>("answer-declined.json").Accepted && Decode<RemoteWireAnswer>("answer.json").Accepted, "accepted reads as sent");
        Check(Decode<RemoteWireError>("error.json") is { Protocol: RemoteProtocol.Version, Error.Length: > 0 }, "a host's error carries the protocol");
        Check(Node("stop.json") is JsonObject { Count: 0 }, "stop's body is {}");
        // The HTTP layer under the routes answers 400/413/431 with {error} alone: read, and written back, without a protocol.
        var bare = JsonNode.Parse("{\"error\":\"Request Header Fields Too Large\"}")!;
        var layer = bare.Deserialize<RemoteWireError>(RemoteWire.Json)!;
        Check(layer.Protocol is null && layer.Error == "Request Header Fields Too Large" && JsonNode.DeepEquals(bare, JsonSerializer.SerializeToNode(layer, RemoteWire.Json)), "an HTTP-layer error needs no protocol");
        // Poll events are kept as the JSON objects received, so every value, both timestamp forms included, is written back as it came
        // (compared as JSON values: whitespace and key order are not kept).
        foreach (var name in new[] { "poll.json", "poll-gap.json", "poll-malformed.json", "poll-requests.json" })
            Check(JsonNode.DeepEquals(Node(name)["events"], JsonSerializer.SerializeToNode(Decode<RemoteWirePoll>(name), RemoteWire.Json)!["events"]), name + " events change through RemoteWirePoll");
        var stamps = Decode<RemoteWirePoll>("poll.json").Events.Select(e => e.Event.ToJsonString()).ToList();
        Check(stamps.Any(s => s.Contains("\"2026-10-17T01:02:04Z\"")) && stamps.Any(s => s.Contains("\"2026-10-17T01:02:03.456Z\"")), "whole-second and millisecond timestamps both survive");
        // A client built from a pane's request writes the same body the Mac's client does.
        var start = Decode<RemoteWireStart>("start.json");
        var pane = start.ToRequest("pane-1", "ws-project");
        Check(Same(Node("start.json"), JsonSerializer.SerializeToNode(RemoteWireStart.From(pane, "ws-project"), RemoteWire.Json)), "From(ToRequest(start)) is start");
        var codex = Decode<RemoteWireStart>("start-codex.json");
        Check(Same(Node("start-codex.json"), JsonSerializer.SerializeToNode(RemoteWireStart.From(codex.ToRequest("pane-2", "ws-project"), "ws-project"), RemoteWire.Json)), "From(ToRequest(start-codex)) is start-codex");
        // Same() reads a null and a missing key alike, so what the Mac writes as an explicit null, or leaves out, is pinned here.
        foreach (var name in new[] { "start.json", "start-codex.json" })
        {
            var encoded = JsonSerializer.SerializeToNode(Decode<RemoteWireStart>(name), RemoteWire.Json)!.AsObject();
            var settings = encoded["request"]!["settings"]!.AsObject();
            Check(settings.TryGetPropertyValue("maxTurns", out var turns) && turns is null && settings.TryGetPropertyValue("maxBudgetUsd", out var budget) && budget is null, name + ": maxTurns and maxBudgetUsd are explicit nulls");
            Check(JsonNode.DeepEquals(Node(name)["request"]!["settings"], settings), name + ": settings are written exactly as the Mac writes them: " + settings.ToJsonString());
            Check(encoded["registeredModels"] is JsonArray && encoded["request"]!["registeredModels"] is null, name + ": registeredModels rides beside the request, always");
        }
        var plainSettings = JsonSerializer.SerializeToNode(new RunSettings(), RemoteWire.Json)!.AsObject();
        Check(!plainSettings.ContainsKey("fastMode") && !plainSettings.ContainsKey("webSearch") && !plainSettings.ContainsKey("networkAccess"), "fastMode, webSearch and networkAccess appear only when not their defaults");
        var codexRequest = JsonSerializer.SerializeToNode(codex, RemoteWire.Json)!["request"]!.AsObject();
        Check(!codexRequest.ContainsKey("attachments") && !codexRequest.ContainsKey("resumeId") && codex.RegisteredModels.Count == 0, "an empty attachment list and no resume id are left out");
        var bareStart = RemoteWireStart.From(new StartRunRequest("pane-2", "local-ws", "shell", "dir", []), "ws-project");
        Check(JsonSerializer.SerializeToNode(bareStart, RemoteWire.Json)!["registeredModels"] is JsonArray { Count: 0 }, "registeredModels is always sent, even empty");
        Check(JsonSerializer.SerializeToNode(bareStart, RemoteWire.Json)!["request"]!["registeredModels"] is null, "the request itself never carries registeredModels");
        Check(!JsonSerializer.SerializeToNode(RemoteWireStart.From(new StartRunRequest("pane-3", "local-ws", "claude", "hi", [], Attachments: []), "ws-project"), RemoteWire.Json)!["request"]!.AsObject().ContainsKey("attachments"), "no attachments is no key");
        // Text goes out as the Mac's JSONEncoder writes it: non-ASCII and HTML characters as they are, never \uXXXX. RemoteWire.Json only.
        const string text = "<b>Tom & Jerry's</b> + caf\u00E9 \uD55C\uAE00";
        Check(JsonSerializer.Serialize(text, RemoteWire.Json) == "\"" + text + "\"" && JsonSerializer.Serialize(text, Wire.Json) != "\"" + text + "\"", "only the remote wire writes text unescaped: " + JsonSerializer.Serialize(text, RemoteWire.Json));
        foreach (var name in new[] { "info.json", "info-darwin.json" })
            Check(!System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(Decode<RemoteWireInfo>(name), RemoteWire.Json)).Contains("\\u", StringComparison.Ordinal), name + " is written back without \\u escapes, as its sample holds none");
        var many = new StartRunRequest("pane-4", "local-ws", "claude", "hi", Enumerable.Range(0, 80).Select(i => new RegisteredModelEntry("model-" + i)).ToList());
        Check(RemoteWireStart.From(many, "ws-project").RegisteredModels.Count == 64, "a client sends at most 64 registered models, the host's own cap");
        return Task.CompletedTask;
    }

    private const string ResourcePrefix = "MightyClaude.Core.Tests.RemoteV2.";
    /// <summary>Files the index does not name as a route's body: other bodies of the same routes (the Mac's RemoteWireFixtureTests.auxiliary).</summary>
    private static readonly string[] Auxiliary = ["endpoints.json", "info-darwin.json", "start-codex.json", "poll-gap.json", "poll-malformed.json", "poll-requests.json",
        "plan-approved.json", "answer-declined.json", "graph-run-event.json", "state-references.json"];

    /// <summary>native/contracts/fixtures/remote-v2/ in this checkout.</summary>
    private static string Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "remote-v2", "endpoints.json"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("the repository root was not found"), "native", "contracts", "fixtures", "remote-v2");
    }

    /// <summary>The Mac's theIndexNamesEveryRouteAndEveryFileIsAccountedFor, on the same folder: every route is named, every file is a route's body or listed as another body, and every file is the one these checks read.</summary>
    internal static Task TheIndexNamesEveryRouteAndEveryFileIsAccountedFor()
    {
        var folder = Folder();
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "endpoints.json")))!.AsArray();
        var routes = index.Select(entry => entry?["name"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Check(routes.SetEquals(["info", "start", "events", "stop", "steer", "permission", "questions", "plan", "refused"]), "the index names every route: " + string.Join(", ", routes.Order(StringComparer.Ordinal)));
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index)
        {
            Check(entry?["path"] is JsonValue path && path.TryGetValue<string>(out var route) && route.StartsWith(RemoteProtocol.PathPrefix, StringComparison.Ordinal), "every route is under " + RemoteProtocol.PathPrefix + ": " + entry?.ToJsonString());
            foreach (var key in new[] { "request", "response" })
                if (entry![key] is JsonValue value && value.TryGetValue<string>(out var name)) { named.Add(name); Check(File.Exists(Path.Combine(folder, name)), name + " is in the folder"); }
        }
        var files = Directory.EnumerateFiles(folder).Select(file => Path.GetFileName(file)).ToHashSet(StringComparer.Ordinal);
        var accounted = named.Union(Auxiliary).ToHashSet(StringComparer.Ordinal);
        Check(!named.Overlaps(Auxiliary), "no route's body is also listed as another body");
        Check(files.SetEquals(accounted), "unaccounted: " + string.Join(", ", files.Except(accounted).Concat(accounted.Except(files)).Order(StringComparer.Ordinal)));
        var embedded = typeof(RemoteWireFixtureVerification).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n => n[ResourcePrefix.Length..]).ToHashSet(StringComparer.Ordinal);
        Check(embedded.SetEquals(files), "the checks read every file of the folder and nothing else: " + string.Join(", ", embedded.Except(files).Concat(files.Except(embedded)).Order(StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    internal static Task EverySampleIsAcceptedByTheWindowsChecks()
    {
        var info = Decode<RemoteWireInfo>("info.json");
        RemoteWorkspaceValidation.Info(info);
        Check(info.Runtime.Platform == "win32" && info.Workspaces.All(RemoteWorkspaceValidation.SharedWorkspace), "a Windows path and a Mac path are both a host's folder");
        var darwin = Decode<RemoteWireInfo>("info-darwin.json");
        RemoteWorkspaceValidation.Info(darwin);
        Check(darwin.Runtime.Platform == "darwin" && darwin.Runtime.Providers.Single(p => p.Id == "claude").ModelCatalog.Models[0] is { ResolvedModel: "claude-opus-4-1", SupportsEffort: true, SupportedEffortLevels.Length: 5 }, "a Mac host's /info reads in full");
        var bad = info with { Runtime = info.Runtime with { Platform = "plan9" } };
        try { RemoteWorkspaceValidation.Info(bad); throw new InvalidOperationException("an unknown platform must be refused"); } catch (InvalidDataException) { }
        var start = Decode<RemoteWireStart>("start.json");
        var request = start.ToRequest("job-1", "ws-project").Validate();
        Check(request.PermissionModeOverride == "plan" && request.TodoProgress is { Items.Count: 1 } && request.PhaseModels is { ClaudeSubagentDefault: "claude-sonnet-4-5" } && request.RegisteredModels.Single().Name == "claude-opus-4-1", "the extras ride beside the request");
        Check(RemoteWorkspaceValidation.PhaseModels(start.PhaseModels!), "the sample's phase models are valid");
        Check(!RemoteWorkspaceValidation.PhaseModels(start.PhaseModels! with { ClaudeMain = "bad model!" }) && !RemoteWorkspaceValidation.PhaseModels(start.PhaseModels! with { ClaudeMainEffort = "ludicrous" }), "a bad model or effort is refused");
        var claude = info.Runtime.Providers.Single(p => p.Id == "claude").Capabilities;
        Check(RemoteWorkspaceValidation.CapabilityProblem(request, claude) is null, "the host supports the sample's settings");
        Check(RemoteWorkspaceValidation.CapabilityProblem(request with { Settings = request.Settings! with { FastMode = true } }, claude) is not null, "a setting the host lacks is refused");
        // A Codex onRequest run with fastMode, live web search and network access, against a Mac host's and a Windows host's Codex.
        var codexStart = Decode<RemoteWireStart>("start-codex.json");
        var codex = codexStart.ToRequest("job-2", "ws-project").Validate();
        Check(codex.Provider == "codex" && codex.Settings is { PermissionMode: "onRequest", FastMode: true, WebSearch: "live", NetworkAccess: true, MaxTurns: null, MaxBudgetUsd: null } && codex.RegisteredModels.Count == 0, "the Codex sample reads in full");
        Check(RemoteWorkspaceValidation.PhaseModels(codexStart.PhaseModels!) && codexStart.PhaseModels is { CodexMainEffort: "medium", CodexSubagentEffort: "low", CodexPlanModeReasoningEffort: "high" }, "the Codex sample's phase models are valid");
        Check(RemoteWorkspaceValidation.CapabilityProblem(codex, darwin.Runtime.Providers.Single(p => p.Id == "codex").Capabilities) is null
            && RemoteWorkspaceValidation.CapabilityProblem(codex, info.Runtime.Providers.Single(p => p.Id == "codex").Capabilities) is null, "both hosts' Codex take the Codex sample");
        Check(RemoteWorkspaceValidation.CapabilityProblem(codex, claude) is not null, "Claude's capabilities refuse the Codex sample");
        var codexCaps = darwin.Runtime.Providers.Single(p => p.Id == "codex").Capabilities;
        Check(RemoteWorkspaceValidation.CapabilityProblem(codex, codexCaps with { NetworkAccess = false }) is not null && RemoteWorkspaceValidation.CapabilityProblem(codex, codexCaps with { WebSearch = false }) is not null
            && RemoteWorkspaceValidation.CapabilityProblem(codex with { Settings = codex.Settings! with { PermissionMode = "manual" } }, codexCaps) is not null, "network access needs the host's support and acceptEdits or onRequest; live search needs the host's support");
        Check(Decode<RemoteWirePlan>("plan-approved.json").Decision() == PlanDecision.ApproveAutoEdit && Decode<RemoteWirePlan>("plan-revised.json").Decision() is { Kind: "revise", Feedback: "Keep the tests" }, "plan outcomes map to decisions");
        Check((Decode<RemoteWirePlan>("plan-revised.json") with { Feedback = null }).Decision() is null, "revised without feedback is no decision");
        Check(RemoteWirePlan.From("job-1", "perm-3", PlanDecision.Revise("Keep the tests")) == Decode<RemoteWirePlan>("plan-revised.json"), "a revise decision writes the sample");
        Check(RemoteWirePlan.From("job-1", "perm-3", PlanDecision.ApproveAutoEdit) == Decode<RemoteWirePlan>("plan-approved.json"), "an approve decision writes the sample");
        // The answer samples answer the sample requests: questions.json perm-2, the plan samples perm-3, permission.json poll.json's perm-1.
        var requests = Decode<RemoteWirePoll>("poll-requests.json").Events.Select(e => e.Event["permission"]!.AsObject()).ToList();
        Check(requests.Select(r => (string?)r["id"]).SequenceEqual(["perm-2", "perm-3", "perm-4"]), "poll-requests holds perm-2, perm-3 and perm-4");
        var answers = Decode<RemoteWireQuestions>("questions.json");
        var ask = requests[0];
        Check((string?)ask["toolName"] == "AskUserQuestion" && (bool)ask["canAnswerQuestions"]! && !(bool)ask["canAllow"]! && answers.RequestId == (string?)ask["id"] && answers.RunId == (string?)ask["runId"], "questions.json answers perm-2");
        var questionnaire = UserQuestionnaire.Parse((string)ask["inputJSON"]!) ?? throw new InvalidOperationException("perm-2's inputJSON is a questionnaire");
        Check(questionnaire.ValidateAnswers(answers.Answers) is { Count: 1 } settled && settled["Which file?"] == "README.md, and the docs", "the sample answers settle the sample questions");
        var plan = requests[1];
        foreach (var name in new[] { "plan-approved.json", "plan-revised.json" })
            Check((string?)plan["toolName"] == ClaudePlanMode.ToolName && (bool)plan["canAnswerPlan"]! && !(bool)plan["canAllow"]! && Decode<RemoteWirePlan>(name) is { } answer && answer.RequestId == (string?)plan["id"] && answer.RunId == (string?)plan["runId"] && answer.Decision() is not null, name + " answers perm-3");
        var blocked = requests[2];
        Check(blocked["reason"] is JsonValue && blocked["blockedPath"] is JsonValue && !(bool)blocked["canAnswerQuestions"]! && !(bool)blocked["canAnswerPlan"]!, "perm-4 carries a reason and a blocked path");
        var perm1 = Decode<RemoteWirePoll>("poll.json").Events.Single(e => (string?)e.Event["type"] == "permission").Event["permission"]!;
        Check(Decode<RemoteWirePermission>("permission.json") is { RequestId: "perm-1", Allow: true } p && p.RequestId == (string?)perm1["id"] && p.RunId == (string?)perm1["runId"], "permission.json answers poll.json's perm-1");
        var key = new string('A', 43);
        Check(RemoteWorkspaceValidation.Key(key) && !RemoteWorkspaceValidation.Key(key[..42]) && !RemoteWorkspaceValidation.Key(key + "+") && !RemoteWorkspaceValidation.Key(new string('A', 129)), "key shape");
        Check(RemoteWorkspaceValidation.KeysMatch(key, key) && !RemoteWorkspaceValidation.KeysMatch(key, key + "B") && !RemoteWorkspaceValidation.KeysMatch("", key), "keys compare whole");
        Check(RemoteWorkspaceValidation.PermissionMode("auto", "claude") && !RemoteWorkspaceValidation.PermissionMode("auto", "codex") && RemoteWorkspaceValidation.PermissionMode("onRequest", "codex") && !RemoteWorkspaceValidation.PermissionMode("onRequest", "gemini"), "permission modes per provider");
        Check(new RemoteHttpFailure(429, "busy").Transient && new RemoteHttpFailure(503, "busy").Transient && !new RemoteHttpFailure(401, "key").Transient, "429 and 503 are passing failures");
        return Task.CompletedTask;
    }

    /// <summary>The JSON node of a sample with one change made to it.</summary>
    private static JsonNode Changed(string name, Action<JsonNode> change) { var node = Node(name); change(node); return node; }
    /// <summary>Refused as the Mac's Codable refuses it: a JsonException while the body is read, never anything later.</summary>
    private static void Refused<T>(JsonNode body, string message) => Refused<T>(body.ToJsonString(), message);
    private static void Refused<T>(string body, string message)
    {
        try { JsonSerializer.Deserialize<T>(body, RemoteWire.Json); }
        catch (JsonException) { return; }
        throw new InvalidOperationException(message + " must be refused: " + body);
    }
    private static void InfoRefused(RemoteWireInfo info, string message)
    {
        try { RemoteWorkspaceValidation.Info(info); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message + " must be refused");
    }

    internal static Task BodiesAreReadAsStrictlyAsTheMacReadsThem()
    {
        // Member names match exactly, numbers are numbers.
        Refused<RemoteWireAnswer>("{\"protocol\":\"2\",\"accepted\":true}", "a protocol sent as a string");
        Refused<RemoteWirePoll>(Changed("poll.json", n => n["cursor"] = "11"), "a cursor sent as a string");
        Refused<RemoteWireAnswer>("{\"Protocol\":2,\"accepted\":true}", "a member spelled in another case");
        Refused<RemoteWireStart>(Changed("start.json", n => { var request = n["request"]!.AsObject(); var kind = request["kind"]!.DeepClone(); request.Remove("kind"); request["Kind"] = kind; }), "a request member spelled in another case");
        // Members the Mac requires are required.
        Refused<RemoteWireAnswer>("{\"protocol\":2}", "an answer without accepted");
        Refused<RemoteWirePoll>(Changed("poll.json", n => n.AsObject().Remove("events")), "a poll without events");
        Refused<RemoteWirePoll>(Changed("poll.json", n => n.AsObject().Remove("lastCursor")), "a poll without lastCursor");
        Refused<RemoteWireStart>(Changed("start-codex.json", n => n.AsObject().Remove("registeredModels")), "a start without registeredModels");
        Refused<RemoteWireStart>(Changed("start.json", n => n["request"]!.AsObject().Remove("model")), "a request without model");
        Refused<RemoteWireStart>(Changed("start.json", n => n["phaseModels"]!.AsObject().Remove("claudeMain")), "phase models without claudeMain");
        Refused<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!["providers"]![0]!.AsObject().Remove("capabilities")), "a provider without capabilities");
        Refused<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!["providers"]![0]!["capabilities"]!.AsObject().Remove("resume")), "capabilities without resume");
        Refused<RemoteWireQuestions>(Changed("questions.json", n => n["answers"]!["Which file?"]!.AsObject().Remove("selectedOptions")), "an answer without selectedOptions");
        // A null where the Mac's type is not optional is refused, also inside a list or a dictionary.
        Refused<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!["appVersion"] = null), "a null appVersion");
        Refused<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!["providers"]![1]!["detail"] = null), "a null provider detail");
        Refused<RemoteWireInfo>(Changed("info-darwin.json", n => n["runtime"]!["providers"]![0]!["modelCatalog"]!["models"]![1]!["displayName"] = null), "a null model displayName");
        Refused<RemoteWireInfo>(Changed("info-darwin.json", n => n["runtime"]!["providers"]![0]!["modelCatalog"]!["models"]![1]!.AsObject().Remove("description")), "a model without description");
        Refused<RemoteWirePoll>(Changed("poll.json", n => n["events"]!.AsArray().Add(null)), "a null entry in events");
        Refused<RemoteWirePoll>(Changed("poll.json", n => n["events"]![0]!["event"] = null), "a null event");
        Refused<RemoteWirePoll>(Changed("poll.json", n => n["events"]![0]!["event"] = "status"), "an event that is not an object");
        Refused<RemoteWireInfo>(Changed("info.json", n => n["workspaces"]!.AsArray().Add(null)), "a null shared workspace");
        Refused<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!["providers"]![0]!["capabilities"]!["permissionModes"]!.AsArray().Add(null)), "a null permission mode");
        Refused<RemoteWireStart>(Changed("start.json", n => n["registeredModels"]!.AsArray().Add(null)), "a null registered model");
        Refused<RemoteWireQuestions>(Changed("questions.json", n => n["answers"]!["Which file?"] = null), "a null answer");
        // A key repeated in one object is refused while the body is read; keys differing only in case are two keys, as on the Mac.
        Refused<RemoteWireAnswer>("{\"protocol\":2,\"accepted\":true,\"accepted\":false}", "a repeated member");
        Refused<RemoteWirePoll>("{\"protocol\":2,\"cursor\":1,\"lastCursor\":1,\"gap\":false,\"done\":false,\"events\":[{\"cursor\":1,\"event\":{\"sessionId\":\"job-1\",\"type\":\"status\",\"type\":\"log\"}}]}", "an event with a repeated key");
        Refused<RemoteWirePoll>("{\"protocol\":2,\"cursor\":1,\"lastCursor\":1,\"gap\":false,\"done\":false,\"events\":[{\"cursor\":1,\"event\":{\"sessionId\":\"job-1\",\"type\":\"log\",\"entry\":{\"id\":\"a\",\"id\":\"b\"}}}]}", "an event with a key repeated deeper in it");
        var colliding = JsonSerializer.Deserialize<RemoteWirePoll>("{\"protocol\":2,\"cursor\":1,\"lastCursor\":1,\"gap\":false,\"done\":false,\"events\":[{\"cursor\":1,\"event\":{\"sessionId\":\"job-1\",\"type\":\"status\",\"Type\":\"log\",\"status\":\"running\",\"entry\":{\"id\":\"a\",\"ID\":\"b\"}}}]}", RemoteWire.Json)!;
        var ev = colliding.Events.Single().Event;
        Check((string?)ev["type"] == "status" && (string?)ev["Type"] == "log" && ev.Count == 5 && (string?)ev["entry"]!["id"] == "a" && ev["TYPE"] is null, "keys differing only in case are distinct and read exactly, never throwing");
        // What the Mac's decoders default is defaulted here too.
        var bare = JsonSerializer.Deserialize<RemoteWireStart>("{\"request\":{\"sessionId\":\"p\",\"workspaceId\":\"w\",\"kind\":\"claude\",\"input\":\"hi\",\"model\":\"default\",\"attachments\":[]},\"registeredModels\":[{\"name\":\"m\"}]}", RemoteWire.Json)!;
        Check(bare.Request is { Provider: "claude", Settings: { Effort: "default", PermissionMode: "manual" }, Attachments: null, ResumeId: null } && bare.RegisteredModels.Single() is { SupportsEffort: false, SupportedEffortLevels.Length: 0 }, "provider, settings and the model's flags default as on the Mac");
        Check(JsonSerializer.Deserialize<RemoteWireStart>(Changed("start.json", n => n["request"]!["provider"] = null), RemoteWire.Json)!.Request.Provider == "claude", "a null provider is claude, as on the Mac");
        var written = JsonSerializer.SerializeToNode(bare, RemoteWire.Json)!;
        Check(written["request"]!["provider"]!.GetValue<string>() == "claude" && written["request"]!["settings"] is JsonObject && written["request"]!["attachments"] is null && written["registeredModels"]![0]!["supportedEffortLevels"] is JsonArray { Count: 0 }, "the defaults are written as the Mac writes them");
        var noProviders = JsonSerializer.Deserialize<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!.AsObject().Remove("providers")), RemoteWire.Json)!;
        RemoteWorkspaceValidation.Info(noProviders);
        Check(noProviders.Runtime.Providers is null && JsonSerializer.Deserialize<RemoteWireInfo>(Changed("info.json", n => n["runtime"]!["providers"] = null), RemoteWire.Json)!.Runtime.Providers is null, "runtime.providers is optional, as on the Mac");
        Check(JsonSerializer.Deserialize<RemoteWireError>("{\"error\":\"Payload Too Large\"}", RemoteWire.Json)!.Protocol is null, "an HTTP-layer error still needs no protocol");
        return Task.CompletedTask;
    }

    internal static Task InfoRefusesWhatTheMacRefuses()
    {
        var info = Decode<RemoteWireInfo>("info.json");
        RemoteWorkspaceValidation.Info(info);
        var many = Enumerable.Range(0, RemoteProtocol.MaximumSharedWorkspaces + 1).Select(i => new RemoteSharedWorkspace("ws-" + i, "w" + i, "C:\\work\\w" + i)).ToList();
        RemoteWorkspaceValidation.Info(info with { Workspaces = many.Take(RemoteProtocol.MaximumSharedWorkspaces).ToList() });
        InfoRefused(info with { Workspaces = many }, "more than 64 shared workspaces");
        InfoRefused(info with { Workspaces = [info.Workspaces[0], info.Workspaces[0] with { Name = "again" }] }, "a repeated workspace id");
        InfoRefused(info with { HostName = new string('h', 121) }, "a host name over 120 characters");
        RemoteWorkspaceValidation.Info(info with { HostName = new string('h', 120) });
        InfoRefused(info with { HostName = "" }, "an empty host name");
        InfoRefused(info with { Protocol = 1 }, "another protocol");
        var claude = info.Runtime.Providers[0]; var codex = info.Runtime.Providers[1];
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude with { Id = "cursor" }] } }, "an unknown provider id");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [codex with { Capabilities = codex.Capabilities with { PermissionModes = [.. codex.Capabilities.PermissionModes, "auto"] } }] } }, "auto on codex");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude with { Capabilities = claude.Capabilities with { PermissionModes = ["onRequest"] } }] } }, "onRequest on claude");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude with { ModelCatalog = claude.ModelCatalog with { Models = [claude.ModelCatalog.Models[0] with { Value = "bad model!" }] } }] } }, "an invalid model value");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude, codex, info.Runtime.Providers[2], claude] } }, "more providers than there are");
        // Nulls the Mac's decoder refuses, should a value be built in code rather than read.
        InfoRefused(info with { Runtime = info.Runtime with { AppVersion = null! } }, "a null appVersion");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude with { Detail = null! }] } }, "a null provider detail");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude with { ModelCatalog = claude.ModelCatalog with { Models = [claude.ModelCatalog.Models[0] with { DisplayName = null! }] } }] } }, "a null model displayName");
        InfoRefused(info with { Runtime = info.Runtime with { Providers = [claude with { ModelCatalog = claude.ModelCatalog with { Models = [claude.ModelCatalog.Models[0] with { Description = null! }] } }] } }, "a null model description");
        return Task.CompletedTask;
    }

    internal static Task TheHostKeyNeverLeavesTheState()
    {
        var key = "k3yK3yk3yK3yk3yK3yk3yK3yk3yK3yk3yK3yk3yK3yQ";
        var state = new RemoteHostState { Enabled = true, Addresses = ["100.101.102.103:43137"], Port = 43137, Key = key, WorkspaceIds = ["ws-project"], ActiveRuns = 1 };
        Check(RemoteWorkspaceValidation.Key(key), "the sample key has the key's shape");
        var text = state.ToString(); var json = JsonSerializer.Serialize(state, Wire.Json) + JsonSerializer.Serialize(state, RemoteWire.Json);
        Check(!text.Contains(key) && !json.Contains(key) && !json.Contains("\"key\""), "the key is never printed or serialized: " + text + " " + json);
        Check(text.Contains("<redacted>") && text.Contains("100.101.102.103:43137") && text.Contains("ws-project") && text.Contains("ActiveRuns = 1"), "the rest of the state still prints: " + text);
        Check(!(state with { Key = null }).ToString().Contains("<redacted>"), "no key prints as null");
        Check(state == state with { } && state != state with { Key = "other" }, "the key still takes part in equality");
        return Task.CompletedTask;
    }

    // Task 5: run events, tool requests and graph nodes as the wire carries them (RemoteWireEvents).

    private static readonly System.Text.RegularExpressions.Regex WholeSeconds = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\z"), Milliseconds = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\z");
    private static JsonObject WireEvent(string name, int cursor) => Decode<RemoteWirePoll>(name).Events.Single(e => e.Cursor == cursor).Event;
    private static JsonObject Edited(JsonObject wire, Action<JsonObject> change) { var copy = wire.DeepClone().AsObject(); change(copy); return copy; }
    private static JsonObject Inner(JsonObject wire, string key) => wire[key]!.AsObject();
    private static RemoteIncoming ReadAsPane(JsonObject wire, bool graphRuns = false) => RemoteWireEvents.Read(wire, "job-1", "pane-1", graphRuns);

    internal static Task EveryWireEventReadsAndWritesBack()
    {
        foreach (var name in new[] { "poll.json", "poll-gap.json", "poll-requests.json" })
            foreach (var item in Decode<RemoteWirePoll>(name).Events)
            {
                var incoming = RemoteWireEvents.Read(item.Event, "job-1", "pane-1", graphRuns: false);
                Check(incoming.Usable, name + " cursor " + item.Cursor + " is usable");
                if (item.Event["type"]!.GetValue<string>() == "graph") { Check(incoming.Ignored && incoming.Event is null && incoming.Permission is null, "a Mac graph node is ignored silently"); continue; }
                if (incoming.Permission is { } permission)
                {
                    Check(permission.RunId == "pane-1" && incoming.HostRunId == "job-1" && permission.InputJson == (string?)item.Event["permission"]!["inputJSON"], "a tool request is the pane's, its host run kept");
                    Check(Same(item.Event, RemoteWireEvents.FromPermission(permission with { RunId = incoming.HostRunId! }, "job-1")), "the permission writes back as the Mac's: " + RemoteWireEvents.FromPermission(permission, "job-1")?.ToJsonString());
                    continue;
                }
                var value = incoming.Event!;
                Check(value.SessionId == "pane-1", "an event becomes the pane's");
                if (value.Type == "status" && value.Status == "error") Check(value.Reason == "authentication", "auth on the wire is the Windows sign-in reason");
                Check(JsonNode.DeepEquals(Plain(item.Event), Plain(RemoteWireEvents.FromRunEvent(value, "job-1", graphRuns: false))), name + " cursor " + item.Cursor + " writes back as the Mac's, every timestamp as it came: " + RemoteWireEvents.FromRunEvent(value, "job-1", false)?.ToJsonString());
            }
        // The question, plan and blocked-path requests (poll-requests.json) as Windows tool requests.
        var requests = Decode<RemoteWirePoll>("poll-requests.json").Events.Select(e => ReadAsPane(e.Event).Permission!).ToList();
        Check(requests[0] is { Id: "perm-2", ToolName: "AskUserQuestion", ToolUseId: "toolu_2", CanAnswerQuestions: true, CanAnswerPlan: false, CanAllow: false, State: "pending", Summary: "Which file?", ReceivedAt: "2026-10-17T01:05:00.000Z", Reason: null, BlockedPath: null }
            && UserQuestionnaire.Parse(requests[0].InputJson) is not null, "perm-2 is a question the pane answers");
        Check(requests[1] is { Id: "perm-3", ToolName: ClaudePlanMode.ToolName, CanAnswerPlan: true, CanAllow: false, CanAnswerQuestions: false } && requests[1].Plan == "# Plan\n1. Read"
            && PlanApprovalRequest.From("pane-1", requests[1]) is { Plan: "# Plan\n1. Read", RequestId: "perm-3" }, "perm-3 is a plan the pane answers and can never allow");
        Check(requests[2] is { Id: "perm-4", ToolName: "Write", CanAllow: true, CanAnswerQuestions: false, CanAnswerPlan: false, Reason: "The path is outside the workspace.", BlockedPath: "/Users/example/elsewhere" }, "perm-4 keeps its reason and blocked path");
        var malformed = Decode<RemoteWirePoll>("poll-malformed.json").Events.Select(e => ReadAsPane(e.Event).Usable).ToList();
        Check(malformed.SequenceEqual([true, false, true]), "only the malformed event is unusable");
        Check(!RemoteWireEvents.Read(Decode<RemoteWirePoll>("poll.json").Events[0].Event, "job-2", "pane-1", false).Usable, "another job's event is unusable");
        var graphRun = Node("graph-run-event.json")["event"]!.AsObject();
        Check(!RemoteWireEvents.Read(graphRun, "job-1", "pane-1", graphRuns: false).Usable, "graph_run is unusable to a client that did not ask");
        var run = RemoteWireEvents.Read(graphRun, "job-1", "pane-1", graphRuns: true).Event;
        Check(run is { Type: "graph_run", GraphRun.Id: "gr-1", SessionId: "pane-1" }, "graph_run reaches a client that asked");
        Check(RemoteWireEvents.FromRunEvent(run!, "job-1", graphRuns: false) is null && Same(graphRun, RemoteWireEvents.FromRunEvent(run!, "job-1", graphRuns: true)), "graph_run travels only on request, as the sample");
        // A tracker node of a Windows host writes the Mac's graph event; a status reason other than a lost sign-in stays home.
        var graph = WireEvent("poll.json", 10);
        Check(JsonNode.DeepEquals(Plain(graph), Plain(RemoteWireEvents.FromGraphNode(Inner(graph, "graph").Deserialize<ExecutionGraphNode>(RemoteWire.Json)!, "job-1"))), "a tracker node writes the sample's graph event: " + RemoteWireEvents.FromGraphNode(Inner(graph, "graph").Deserialize<ExecutionGraphNode>(RemoteWire.Json)!, "job-1")?.ToJsonString());
        // A value RemoteWire.Json cannot write (a null where the type is not nullable) does not travel; the host leaves it out.
        Check(RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "log", new LogEntry("log-9", "output", null!, "2026-10-17T01:02:03Z")), "job-9", false) is null, "a log the wire cannot carry is null, never a throw");
        Check(RemoteWireEvents.FromPermission(new ToolPermissionRequest("r1", "pane-9", "t1", "Bash", null!, "ls"), "job-9") is null, "a request the wire cannot carry is null, never a throw");
        Check(RemoteWireEvents.FromGraphNode(new ExecutionGraphNode("node-9", "bad run!", null, "main", "running", "Request"), "job-9") is null, "a graph node the Mac's client would not normalize does not travel");
        Check(RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "plan", Plan: new PlanRecord("plan-9", "job-9", "# Plan", null!, "2026-10-17T01:02:03.000Z", PlanOutcome.ApprovedAuto)), "job-9", false) is null
            && RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "status", Status: "paused"), "job-9", false) is null, "an event a client would refuse does not travel");
        Check(RemoteWireEvents.FromRunEvent(RunEvent.State("pane-9", "error", "quota"), "job-9", false) is { } quota && !quota.ContainsKey("reason") && (string?)quota["sessionId"] == "job-9", "only auth travels as a reason");
        Check(RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "permission", Permission: requests[2]), "job-9", false) is null && RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "steer"), "job-9", false) is null, "tool requests travel as FromPermission, and a type the Mac does not read never travels");
        return Task.CompletedTask;
    }

    internal static Task EachWireEventIsRefusedAloneAsTheMacRefusesIt()
    {
        JsonObject Of(int cursor) => WireEvent("poll.json", cursor);
        var status = Of(1); var resume = Of(2); var log = Of(3); var activity = Of(4); var usage = Of(5); var permission = Of(6); var plan = Of(7); var todos = Of(8); var background = Of(9);
        var refused = new (string What, JsonObject Wire)[]
        {
            ("no sessionId", Edited(status, e => e.Remove("sessionId"))),
            ("no type", Edited(status, e => e.Remove("type"))),
            ("a number for a sessionId", Edited(status, e => e["sessionId"] = 1)),
            ("a null type", Edited(status, e => e["type"] = null)),
            ("an unknown type", Edited(status, e => e["type"] = "steer")),
            ("a mistyped status", Edited(status, e => e["status"] = 1)),
            ("an unknown status", Edited(status, e => e["status"] = "paused")),
            ("a reason other than auth", Edited(status, e => e["reason"] = "quota")),
            ("a mistyped resumeId", Edited(resume, e => e["resumeId"] = 5)),
            ("a resume without its id", Edited(resume, e => e.Remove("resumeId"))),
            ("an entry that is not an object", Edited(log, e => e["entry"] = "text")),
            ("a log without its entry", Edited(log, e => e.Remove("entry"))),
            ("an entry without a kind", Edited(log, e => Inner(e, "entry").Remove("kind"))),
            ("an entry without text", Edited(log, e => Inner(e, "entry").Remove("text"))),
            ("an entry without a timestamp", Edited(log, e => Inner(e, "entry").Remove("timestamp"))),
            ("an entry whose text is null", Edited(log, e => Inner(e, "entry")["text"] = null)),
            ("an entry whose id is a number", Edited(log, e => Inner(e, "entry")["id"] = 3)),
            ("an entry with a mistyped provider", Edited(log, e => Inner(e, "entry")["provider"] = 1)),
            ("an entry with an unknown provider", Edited(log, e => Inner(e, "entry")["provider"] = "llama")),
            ("an entry longer than the Mac takes", Edited(log, e => Inner(e, "entry")["text"] = new string('a', 131_073))),
            ("an entry whose activity is not valid", Edited(log, e => Inner(e, "entry")["activity"] = Edited(Inner(activity, "activity"), a => a["kind"] = "dance"))),
            ("a mistyped activity on any event", Edited(log, e => e["activity"] = 5)),
            ("an activity without its summary", Edited(activity, e => Inner(e, "activity").Remove("summary"))),
            ("an activity with a mistyped tool name", Edited(activity, e => Inner(e, "activity")["toolName"] = 7)),
            ("an activity that is not valid", Edited(activity, e => Inner(e, "activity")["state"] = "dancing")),
            ("an activity event without one", Edited(activity, e => e.Remove("activity"))),
            ("a mistyped permission on any event", Edited(log, e => e["permission"] = "allow")),
            ("a permission without its state", Edited(permission, e => Inner(e, "permission").Remove("state"))),
            ("a permission without canAllow", Edited(permission, e => Inner(e, "permission").Remove("canAllow"))),
            ("a permission with a mistyped canAllow", Edited(permission, e => Inner(e, "permission")["canAllow"] = "yes")),
            ("a permission with a mistyped canAnswerPlan", Edited(permission, e => Inner(e, "permission")["canAnswerPlan"] = 1)),
            ("a permission without inputJSON", Edited(permission, e => Inner(e, "permission").Remove("inputJSON"))),
            ("a permission spelling inputJSON the Windows way", Edited(permission, e => { var p = Inner(e, "permission"); var input = p["inputJSON"]!; p.Remove("inputJSON"); p["inputJson"] = input; })),
            ("a permission with a bad run id", Edited(permission, e => Inner(e, "permission")["runId"] = "bad id")),
            ("a permission with too much input", Edited(permission, e => Inner(e, "permission")["inputJSON"] = new string('a', 65_537))),
            ("a permission event without one", Edited(permission, e => e.Remove("permission"))),
            ("a plan without decidedAt", Edited(plan, e => Inner(e, "plan").Remove("decidedAt"))),
            ("a plan with an unknown outcome", Edited(plan, e => Inner(e, "plan")["outcome"] = "maybe")),
            ("a todo list with a null item", Edited(todos, e => Inner(e, "todos")["items"] = new JsonArray((JsonNode?)null))),
            ("a todo item without its status", Edited(todos, e => Inner(e, "todos")["items"]![0]!.AsObject().Remove("status"))),
            ("background work without turnEnded", Edited(background, e => Inner(e, "background").Remove("turnEnded"))),
            ("a background task without its status", Edited(background, e => Inner(e, "background")["tasks"]![0]!.AsObject().Remove("status"))),
            ("a mistyped background", Edited(background, e => e["background"] = 1)),
            ("an event of another job", Edited(log, e => e["sessionId"] = "job-2")),
        };
        foreach (var (what, wire) in refused) Check(!ReadAsPane(wire).Usable, what + " is refused");
        // A usage the Mac's decoder cannot read, or its client cannot normalize, is left out without a word (RemoteClientService.local).
        var silent = new (string What, JsonObject Wire)[]
        {
            ("a usage event with a mistyped usage", Edited(usage, e => e["usage"] = "lots")),
            ("a usage event the client cannot normalize", Edited(usage, e => Inner(e, "usage")["provider"] = "llama")),
            ("a usage event whose count is a string", Edited(usage, e => Inner(e, "usage")["inputTokens"] = "12")),
            ("a usage event whose count is not whole", Edited(usage, e => Inner(e, "usage")["inputTokens"] = 12.5)),
            ("a usage event whose cost is a string", Edited(usage, e => Inner(e, "usage")["costUSD"] = "0.01")),
            ("a usage event whose model is a number", Edited(usage, e => Inner(e, "usage")["model"] = 4)),
            ("a usage event without updatedAt", Edited(usage, e => Inner(e, "usage").Remove("updatedAt"))),
            ("a usage event without its source", Edited(usage, e => Inner(e, "usage").Remove("source"))),
            ("a usage event with a null provider", Edited(usage, e => Inner(e, "usage")["provider"] = null)),
            ("a usage event whose rate-limit window has no kind", Edited(usage, e => Inner(e, "usage")["rateLimits"] = new JsonArray(new JsonObject { ["percentUsed"] = 5 }))),
            ("a usage event whose rate limits are not a list", Edited(usage, e => Inner(e, "usage")["rateLimits"] = "full")),
            ("a usage event without one", Edited(usage, e => e.Remove("usage"))),
        };
        foreach (var (what, wire) in silent) Check(ReadAsPane(wire) is { Usable: true, Event: null, Permission: null, Ignored: true }, what + " is left out silently");
        Check(!RemoteWireEvents.Read(silent[0].Wire, "job-2", "pane-1", false).Usable, "another job's usage is still unusable");
        Check(ReadAsPane(Edited(usage, e => { Inner(e, "usage")["model"] = null; Inner(e, "usage")["rateLimits"] = new JsonArray(new JsonObject { ["kind"] = "five_hour", ["percentUsed"] = 5 }); })).Event?.Usage is { InputTokens: 1200, RateLimits: [{ Kind: "five_hour", PercentUsed: 5 }] }, "a usage with a null model and a rate-limit window still reads");
        // What the Mac's decoder lets go (try?) only leaves that value out; the event still reads.
        var lenient = new (string What, JsonObject Wire)[]
        {
            ("a mistyped usage on a log", Edited(log, e => e["usage"] = "lots")),
            ("a mistyped plan on a log", Edited(log, e => e["plan"] = 1)),
            ("a mistyped reason", Edited(Of(11), e => e["reason"] = 5)),
            ("a mistyped entry activity", Edited(log, e => Inner(e, "entry")["activity"] = "busy")),
            ("mistyped pictures", Edited(log, e => Inner(e, "entry")["images"] = "lots")),
            ("an unknown key", Edited(log, e => e["future"] = 1)),
            ("a key in another case", Edited(status, e => e["Type"] = "log")),
            ("a permission without the answer flags", Edited(permission, e => { Inner(e, "permission").Remove("canAnswerQuestions"); Inner(e, "permission").Remove("canAnswerPlan"); })),
        };
        foreach (var (what, wire) in lenient) Check(ReadAsPane(wire).Usable, what + " still reads");
        var foreignActivity = Edited(status, e => e["entry"] = Edited(Inner(log, "entry"), entry => entry["activity"] = Edited(Inner(activity, "activity"), a => a["kind"] = "dance")));
        Check(ReadAsPane(foreignActivity).Event is { Type: "status", Entry.Activity: null }, "an entry activity that is not valid is only left out on an event other than a log");
        var longActivity = Edited(activity, e => Inner(e, "activity")["durationMs"] = 1e20);
        Check(ReadAsPane(longActivity).Event?.Activity is { Id: "act-1", DurationMs: null }, "an out-of-range duration reads as none, as the Mac's decoder reads it");
        Check(ReadAsPane(lenient[2].Wire).Event is { Type: "status", Status: "completed", Reason: null } && ReadAsPane(lenient[3].Wire).Event?.Entry is { Activity: null, Text: "Reading README.md." }
            && ReadAsPane(lenient[6].Wire).Event is { Type: "status", Status: "running" } && ReadAsPane(lenient[7].Wire).Permission is { CanAnswerQuestions: false, CanAnswerPlan: false, CanAllow: true }, "the lenient values read as the Mac reads them");
        // A picture names a file on the host: the entry reads as its text alone.
        var picture = Edited(log, e => { Inner(e, "entry")["kind"] = "image"; Inner(e, "entry")["images"] = new JsonArray(JsonSerializer.SerializeToNode(new AgentImageRef(new string('a', 64), "image/png", 1, 1, 10, "tool"), RemoteWire.Json)); });
        Check(ReadAsPane(picture).Event?.Entry is { Kind: "system", Images: null, Text: "Reading README.md." }, "a host's picture reads as its text");
        // Read never throws, whatever the object holds.
        foreach (var odd in new[] { "{}", "{\"sessionId\":{},\"type\":[]}", "{\"sessionId\":\"job-1\",\"type\":\"log\",\"entry\":[1,2]}", "{\"sessionId\":\"job-1\",\"type\":\"permission\",\"permission\":{\"inputJSON\":{}}}", "{\"sessionId\":\"job-1\",\"type\":\"background\",\"background\":{\"tasks\":[1],\"turnEnded\":true}}" })
            Check(!ReadAsPane(JsonNode.Parse(odd)!.AsObject()).Usable, odd + " is unusable");
        return Task.CompletedTask;
    }

    internal static Task TimestampsImagesAndPendingRequestsAreBounded()
    {
        // A Windows host writes each time in the form the Mac writes it: whole seconds for log, usage and graph times, milliseconds for request, plan and background times.
        var log = RemoteWireEvents.FromRunEvent(RunEvent.Log("job-9", "output", "hi"), "job-9", false)!;
        var stamp = log["entry"]!["timestamp"]!.GetValue<string>();
        Check(WholeSeconds.IsMatch(stamp), "a Windows log time travels in whole seconds: " + stamp);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var request = RemoteWireEvents.FromPermission(new ToolPermissionRequest("r1", "pane-9", "t1", "Bash", "{}", "ls", ReceivedAt: now), "job-9")!;
        Check(Milliseconds.IsMatch((string)request["permission"]!["receivedAt"]!) && (string?)request["permission"]!["runId"] == "job-9" && request["permission"]!["inputJSON"] is JsonValue && request["permission"]!["inputJson"] is null && (string?)request["sessionId"] == "job-9", "a request travels as the Mac's, its time in milliseconds: " + request.ToJsonString());
        var planned = RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "plan", Plan: new PlanRecord("plan-9", "job-9", "# Plan", now, now, PlanOutcome.ApprovedAuto)), "job-9", false)!;
        Check(Milliseconds.IsMatch((string)planned["plan"]!["receivedAt"]!) && Milliseconds.IsMatch((string)planned["plan"]!["decidedAt"]!), "plan times travel in milliseconds: " + planned.ToJsonString());
        var work = RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "background", Background: new BackgroundWork([new BackgroundTask("task-9", "shell", "npm test", now, "completed", EndedAt: now)])), "job-9", false)!;
        Check(Milliseconds.IsMatch((string)work["background"]!["tasks"]![0]!["startedAt"]!) && Milliseconds.IsMatch((string)work["background"]!["tasks"]![0]!["endedAt"]!), "background times travel in milliseconds: " + work.ToJsonString());
        var used = RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "usage", Usage: new SessionUsage { InputTokens = 1, UpdatedAt = now }), "job-9", false)!;
        Check(WholeSeconds.IsMatch((string)used["usage"]!["updatedAt"]!), "a usage time travels in whole seconds: " + used.ToJsonString());
        var node = RemoteWireEvents.FromGraphNode(new ExecutionGraphNode("node-9", "act-9", "node-1", "agent", "running", "Agent") { UpdatedAt = now, Entries = [RunEvent.Log("job-9", "assistant", "hi").Entry!] }, "job-9")!;
        Check((string?)node["type"] == "graph" && (string?)node["sessionId"] == "job-9" && WholeSeconds.IsMatch((string)node["graph"]!["updatedAt"]!) && WholeSeconds.IsMatch((string)node["graph"]!["entries"]![0]!["timestamp"]!), "graph times travel in whole seconds: " + node.ToJsonString());
        // A time already in one of the Mac's forms travels as it is, so a received sample writes back unchanged.
        var kept = RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "background", Background: new BackgroundWork([new BackgroundTask("task-9", "shell", "npm test", "2026-10-17T01:02:08Z")])), "job-9", false)!;
        var keptLog = RemoteWireEvents.FromRunEvent(new RunEvent("pane-9", "log", new LogEntry("log-9", "output", "hi", "2026-10-17T01:02:03.456Z")), "job-9", false)!;
        Check((string?)kept["background"]!["tasks"]![0]!["startedAt"] == "2026-10-17T01:02:08Z" && (string?)keptLog["entry"]!["timestamp"] == "2026-10-17T01:02:03.456Z", "a Mac-form time is never rewritten");
        Check(RemoteWireEvents.MacTimestamp("2026-10-17T10:02:03.1234567+09:00") == "2026-10-17T01:02:03.123Z" && RemoteWireEvents.MacTimestamp("2026-10-17T10:02:03.9999999+09:00", milliseconds: false) == "2026-10-17T01:02:03Z"
            && RemoteWireEvents.MacTimestamp("not a time") is null && RemoteWireEvents.MacTimestamp("2026-10-17") is null, "offsets become UTC, cut to the form asked for");
        var said = RemoteWireEvents.FromRunEvent(RunEvent.Log("job-9", "output", "2026-10-17T10:02:03.1234567+09:00"), "job-9", false)!;
        Check((string?)said["entry"]!["text"] == "2026-10-17T10:02:03.1234567+09:00", "a time inside text is never rewritten");
        var picture = RunEvent.Log("job-9", "image", "a picture") with { };
        picture = picture with { Entry = picture.Entry! with { Images = [new AgentImageRef("h", "image/png", 1, 1, 10, "tool")] } };
        Check(RemoteWireEvents.FromRunEvent(picture, "job-9", false)!["entry"]!["images"] is null, "pictures name host files and stay home");
        Check(RemoteWireEvents.FromGraphNode(new ExecutionGraphNode("node-9", "act-9", "node-1", "agent", "running", "Agent") { Entries = [picture.Entry! with { Kind = "assistant" }] }, "job-9")!["graph"]!["entries"]![0]!["images"] is null, "a graph node's pictures stay home too");
        var pending = new RemotePendingPermissions(); var evicted = new List<ToolPermissionRequest>();
        for (var i = 0; i < 40; i++) evicted.AddRange(pending.Admit(new ToolPermissionRequest("r" + i, "pane-1", "t" + i, "Bash", "{}", "ls")));
        Check(evicted.Select(r => r.Id).SequenceEqual(Enumerable.Range(0, 8).Select(i => "r" + i)) && evicted.All(r => r.State == "cancelled"), "the oldest are withdrawn: " + string.Join(",", evicted.Select(r => r.Id)));
        Check(pending.Count("pane-1") == RemoteProtocol.MaximumPendingPermissions && pending.Admit(new ToolPermissionRequest("r1", "pane-2", "t1", "Bash", "{}", "ls")).Count == 0 && pending.Count("pane-2") == 1, "the newest 32 wait, per pane");
        Check(pending.Admit(new ToolPermissionRequest("r39", "pane-1", "t39", "Bash", "{}", "ls", State: "allowed")).Count == 0 && pending.Count("pane-1") == 31, "a settled request leaves the count");
        pending.Forget("pane-1"); Check(pending.Count("pane-1") == 0 && pending.Count("pane-2") == 1, "a finished run forgets its requests");
        return Task.CompletedTask;
    }
}
