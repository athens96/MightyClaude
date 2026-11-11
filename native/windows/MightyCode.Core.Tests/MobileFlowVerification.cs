using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// The role view a phone that asked for it gets (<see cref="MobileFlow"/>, <c>roles=1</c>). native/contracts/fixtures/mobile-roles.json holds, for the
/// same saved runs, what the Mac sends (MightyCoreTests MobileFlowTests); this sends the same, and the phone (mobile/src/__tests__/flow-roles.test.ts)
/// renders it.
/// </summary>
internal static class MobileFlowVerification
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyCode.Core.Tests.MobileRoles.json";
        using var stream = typeof(MobileFlowVerification).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded in MightyCode.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static string Json(object? value) => JsonSerializer.Serialize(value, Wire.Json);

    private static T Korean<T>(Func<T> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "ko"; Locale.ResetCache();
        try { return body(); } finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private static List<MightyGraphRun> Runs(JsonElement item) =>
        JsonSerializer.Deserialize<List<MightyGraphRun>>(item.GetProperty("runs").GetRawText(), Wire.Json) ?? throw new InvalidOperationException("no runs");

    private static Dictionary<string, IReadOnlyList<string>> Tails(JsonElement item) =>
        item.TryGetProperty("outputTails", out var tails) ? tails.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(l => l.GetString() ?? "").ToList()) : [];

    /// <summary>The Mighty payload a host sends for a case, the role view in it, cut to the keys the fixture lists.</summary>
    private static JsonObject Projection(JsonElement item)
    {
        var runs = Runs(item);
        var roles = new MobileFlow.Roles((item.TryGetProperty("provider", out var provider) ? provider.GetString() : "claude") == "claude");
        var wire = MobileMightySupport.Runs(runs, outputTails: Tails(item), roles: roles);
        var mighty = JsonNode.Parse(Json(new { style = "cli", runs = wire, fanOut = roles.FanOut ? true : (bool?)null, log = roles.FanOut ? MobileFlow.Log(runs) : null }))!.AsObject();
        var output = new JsonObject();
        foreach (var key in new[] { "fanOut", "log" }) if (mighty[key] is { } value) output[key] = value.DeepClone();
        var projected = new JsonArray();
        foreach (var run in mighty["runs"]!.AsArray())
        {
            var kept = new JsonObject();
            foreach (var key in new[] { "id", "status", "returning", "progress" }) if (run![key] is { } value) kept[key] = value.DeepClone();
            var blocks = new JsonArray();
            foreach (var block in run!["blocks"]!.AsArray())
            {
                var shown = new JsonObject();
                foreach (var key in new[] { "id", "kind", "title", "status", "background", "role", "card", "delegate" }) if (block![key] is { } value) shown[key] = value.DeepClone();
                blocks.Add(shown);
            }
            kept["blocks"] = blocks;
            projected.Add(kept);
        }
        output["runs"] = projected;
        return output;
    }

    internal static Task TheSharedFixtureIsWhatWindowsSends()
    {
        var cases = Fixture.Value.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Count >= 2, "the fixture has its cases; got " + cases.Count);
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var made = Korean(() => Projection(item));
            var expect = JsonNode.Parse(item.GetProperty("expect").GetRawText());
            Check(JsonNode.DeepEquals(made, expect), $"{name} differs; Windows sends: {made.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}");
        }
        return Task.CompletedTask;
    }

    /// <summary>A phone that does not ask (an older one) gets the payload it always got; another agent's pane says who each block is and fans nothing out.</summary>
    internal static Task OlderPhonesAndOtherAgents()
    {
        var item = Fixture.Value.GetProperty("cases")[0]; var runs = Runs(item);
        Korean(() =>
        {
            var plain = MobileMightySupport.Runs(runs, outputTails: Tails(item));
            var text = Json(plain);
            foreach (var key in new[] { "role", "card", "delegate", "returning", "progress" }) Check(!text.Contains("\"" + key + "\"", StringComparison.Ordinal), key + " went to an older phone");
            Check(Json(plain.Select(r => r.Blocks.Select(b => b.Id))) == """[["r1:main"],["r1:notice:1:main","r1:notice:1:bg"],["r2:main","scout","deep"]]""", "a request's background blocks wait for their notice run: " + Json(plain.Select(r => r.Blocks.Select(b => b.Id))));
            var claude = MobileMightySupport.Runs(runs, roles: new MobileFlow.Roles(true));
            Check(claude[2].Blocks.Select(b => b.Id).SequenceEqual(["r2:main", "scout", "ci", "wt", "deep"]), "the fan-out is the request's blocks as they were recorded");
            var other = MobileMightySupport.Runs(runs, roles: new MobileFlow.Roles(false));
            Check(other[2].Blocks.Select(b => b.Id).SequenceEqual(["r2:main", "scout", "deep"]) && other[2].Blocks.All(b => b.Card is null && b.Delegate is null) && other[2].Returning is null
                && other[2].Blocks.Select(b => b.Role?.Name).SequenceEqual(["lead", "subagent", "subagent"]), "another agent's pane says who each block is and fans nothing out");
            return 0;
        });
        return Task.CompletedTask;
    }

    /// <summary>The long poll wakes for what the role view draws: a background block's new step and figures, and a consultation.</summary>
    internal static Task DigestMovesWithTheFanOutAndTheAdvisor()
    {
        var item = Fixture.Value.GetProperty("cases")[0];
        var start = MobileMightySupport.Digest(Runs(item));
        var stepped = Runs(item); stepped[2].Agents[2].Entries.Add(new("wt-step", "assistant", "초안", "2026-11-10T10:06:00Z"));
        Check(MobileMightySupport.Digest(stepped) != start, "a background block's step moves the digest");
        var counted = Runs(item); counted[2].Agents[2].Figures = new GraphAgentFigures("2026-11-10T10:05:00Z", ToolCalls: 2);
        Check(MobileMightySupport.Digest(counted) != start, "a background block's figures move the digest");
        var consulted = Runs(item); consulted[2].Advisor = consulted[2].Advisor! with { Calls = [.. consulted[2].Advisor!.Consultations, new GraphAdvisorCall("srvtoolu_2", StartedAt: "2026-11-10T10:06:00Z")] };
        Check(MobileMightySupport.Digest(consulted) != start, "a consultation moves the digest");
        return Task.CompletedTask;
    }

    /// <summary><c>roles=1</c> reaches the host's role extras on the session detail alone; an older phone's poll asks for none, and the list route refuses the name.</summary>
    internal static async Task RouteTakesRolesOnTheSessionDetailAlone()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-mobile-roles-").FullName;
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "s", WorkspaceId = workspace.Id }] });
            var roleReads = 0;
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null),
                _ => new([], Mighty: new { style = "cli", runs = Array.Empty<object>() }))
            { RoleExtras = _ => { roleReads++; return new([], Mighty: new { style = "cli", runs = Array.Empty<object>(), fanOut = true }); } };
            async Task<(int Status, JsonElement Mighty)> Call(string path)
            {
                var reply = await router.RouteAsync("GET", path, null, "phone", CancellationToken.None); var body = JsonSerializer.SerializeToElement(reply.Body, Wire.Json);
                return (reply.Status, body.TryGetProperty("mighty", out var mighty) ? mighty : default);
            }
            var plain = await Call("/m1/sessions/s?since=0&wait=0");
            Check(plain.Status == 200 && !plain.Mighty.TryGetProperty("fanOut", out _) && roleReads == 0, "an older phone gets the plain payload");
            var roles = await Call("/m1/sessions/s?since=0&wait=0&roles=1");
            Check(roles.Status == 200 && roles.Mighty.GetProperty("fanOut").GetBoolean() && roleReads == 1, "a phone that asked gets the role view");
            var none = await Call("/m1/sessions/s?since=0&wait=0&roles=0&structure=0");
            Check(none.Status == 200 && !none.Mighty.TryGetProperty("fanOut", out _) && roleReads == 1, "roles=0 asks for none, beside a structure tag");
            Check((await Call("/m1/state?since=0&wait=0&roles=1")).Status == 400 && (await Call("/m1/sessions/s?roles=yes")).Status == 400 && MobileDesktopRouter.Capabilities.Contains("roles"),
                "the roles query is the session detail's alone, a number; the host advertises it");
        }
        finally { Directory.Delete(directory, true); }
    }
}
