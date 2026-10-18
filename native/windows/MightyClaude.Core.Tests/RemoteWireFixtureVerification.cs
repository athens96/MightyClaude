using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>native/contracts/fixtures/remote-v2/ read by the Windows Core: every body decodes into the Windows wire type and encodes back to the Mac's own JSON (null values aside).</summary>
internal static class RemoteWireFixtureVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static JsonNode Node(string name)
    {
        using var stream = typeof(RemoteWireFixtureVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.RemoteV2." + name) ?? throw new InvalidOperationException(name + " is not embedded");
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
}
