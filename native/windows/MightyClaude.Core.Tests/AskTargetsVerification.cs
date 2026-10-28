using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/ask-targets.json: the composer's "send to" rules, which the Mac (AskTargetsTests) and the phone (jest)
// read too — the target list, which targets a pane offers, which one a send uses, and the text it sends. Also the Windows side of
// what the choice reaches: the saved pane, the restore and a provider switch, the pane's automatic title, and the phone's detail and action.
internal static class AskTargetsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.AskTargets.json";
        using var stream = typeof(AskTargetsVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    private static List<JsonElement> Cases(string key)
    {
        var cases = Fixture.Value.GetProperty(key).EnumerateArray().ToList();
        Check(cases.Count > 0, key + " has no cases");
        return cases;
    }

    private static string? Optional(JsonElement entry, string key) => entry.GetProperty(key).ValueKind == JsonValueKind.Null ? null : entry.GetProperty(key).GetString();
    private static HashSet<string> Set(JsonElement entry, string key) => entry.GetProperty(key).EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
    private static AskTarget? Target(string? id) => id is null ? null : AskTargets.All.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("no target " + id);
    private static string Shown(IEnumerable<string> ids) => "[" + string.Join(", ", ids) + "]";

    internal static Task TheTargetsAndSkillAreTheFixtures()
    {
        Check(Fixture.Value.GetProperty("skill").GetString() == AskTargets.Skill, "skill");
        var targets = Fixture.Value.GetProperty("targets").EnumerateArray().ToList();
        Check(targets.Select(t => t.GetProperty("id").GetString()).SequenceEqual(AskTargets.All.Select(t => t.Id)), "ids, in order");
        Check(targets.Select(t => t.GetProperty("label").GetString()).SequenceEqual(AskTargets.All.Select(t => t.Label)), "labels");
        Check(targets.Select(t => t.GetProperty("executable").GetString()).SequenceEqual(AskTargets.All.Select(t => t.Executable)), "executables");
        Check(!AskTargets.All.Any(t => t.Id is "claude" or "muse"), "neither claude nor muse is a target");
        return Task.CompletedTask;
    }

    internal static Task EveryAvailableEffectiveAndWrapCaseHolds()
    {
        foreach (var entry in Cases("available"))
        {
            var name = entry.GetProperty("name").GetString()!;
            var expected = entry.GetProperty("expect").EnumerateArray().Select(id => id.GetString()!).ToList();
            var result = AskTargets.Available(entry.GetProperty("isClaudeAgent").GetBoolean(), Set(entry, "slash"), Set(entry, "installed")).Select(t => t.Id).ToList();
            Check(result.SequenceEqual(expected), $"available {name}: expected {Shown(expected)}, got {Shown(result)}");
        }
        foreach (var entry in Cases("effective"))
        {
            var name = entry.GetProperty("name").GetString()!;
            var ids = Set(entry, "available");
            var result = AskTargets.Effective(Optional(entry, "saved"), AskTargets.All.Where(t => ids.Contains(t.Id)).ToList());
            Check(result?.Id == Optional(entry, "expect"), $"effective {name}: expected {Optional(entry, "expect") ?? "null"}, got {result?.Id ?? "null"}");
        }
        foreach (var entry in Cases("wrap"))
        {
            var name = entry.GetProperty("name").GetString()!;
            var result = AskTargets.Wrap(entry.GetProperty("text").GetString()!, Target(Optional(entry, "target")));
            Check(result == Optional(entry, "expect"), $"wrap {name}: expected {Optional(entry, "expect") ?? "null"}, got {result ?? "null"}");
        }
        // Wrapping is said once: an ask command (any slash command) goes as it is, so a send wrapped before it was queued is not wrapped again.
        var codex = Target("codex");
        var wrapped = AskTargets.Wrap("review this", codex)!;
        Check(AskTargets.Wrap(wrapped, codex) == wrapped && AskTargets.Wrap(wrapped, Target("grok")) == wrapped, "a wrapped send is never wrapped again");
        return Task.CompletedTask;
    }

    /// <summary>A saved target falls back to Claude only once both the PATH read and the pane's catalogue are in, and only when the target is not offered.</summary>
    internal static Task AChoiceFallsBackOnlyWhenKnownToBeGone()
    {
        IReadOnlyList<AskTarget> codex = AskTargets.All.Where(t => t.Id == "codex").ToList();
        Check(AskTargets.FellBack("gemini", codex, probed: true, scanned: true), "a target not offered falls back");
        Check(AskTargets.FellBack("codex", [], probed: true, scanned: true), "nothing offered falls back");
        Check(!AskTargets.FellBack("codex", codex, probed: true, scanned: true), "an offered target stays");
        Check(!AskTargets.FellBack(null, [], probed: true, scanned: true), "Claude never falls back");
        Check(!AskTargets.FellBack("gemini", codex, probed: false, scanned: true), "not before the PATH was read");
        Check(!AskTargets.FellBack("gemini", codex, probed: true, scanned: false), "not before the catalogue was scanned");

        // A PATH that is not the login shell's, or a read finding nothing after one that found some, never takes a CLI away.
        static HashSet<string> S(params string[] items) => items.ToHashSet(StringComparer.Ordinal);
        var all = AskTargets.Probed(S("codex"), S("codex", "agy"), loginShell: true);
        Check(all.Installed.SetEquals(S("codex", "agy")) && all.Conclusive, "a full read is taken");
        var fewer = AskTargets.Probed(S("codex", "agy"), S("agy"), loginShell: true);
        Check(fewer.Installed.SetEquals(S("agy")) && fewer.Conclusive, "a read with fewer is taken");
        var first = AskTargets.Probed(S(), S(), loginShell: true);
        Check(first.Installed.Count == 0 && first.Conclusive, "a first empty read is conclusive");
        var hiccup = AskTargets.Probed(S("codex"), S(), loginShell: true);
        Check(hiccup.Installed.SetEquals(S("codex")) && !hiccup.Conclusive, "an empty read after a full one is a hiccup");
        var fallback = AskTargets.Probed(S("codex"), S("agy"), loginShell: false);
        Check(fallback.Installed.SetEquals(S("codex", "agy")) && !fallback.Conclusive, "another PATH only adds");
        return Task.CompletedTask;
    }

    /// <summary>A wrapped send titles its pane by the question; anything else as it is.</summary>
    internal static Task TheQuestionTitlesThePane()
    {
        var wrapped = AskTargets.Wrap("  왜 느린지 봐줘 ", Target("codex"))!;
        Check(AskTargets.Question(wrapped) == "왜 느린지 봐줘", "the question inside: " + AskTargets.Question(wrapped));
        Check(AskTargets.Question("/oh-my-claudecode:ask muse hi") == "/oh-my-claudecode:ask muse hi", "an id that is no target is left");
        Check(AskTargets.Question("그냥 질문") == "그냥 질문", "plain text is left");
        var pane = new RunSession { Id = "p", WorkspaceId = "w", Logs = [new("u", "user", wrapped, Wire.Now())] };
        Check(PaneTitle.AutoTitle(pane, "Claude") == "왜 느린지 봐줘", "the restored title is the question: " + PaneTitle.AutoTitle(pane, "Claude"));
        Check(PaneTitle.Requested(pane with { Logs = [] }, wrapped).Title == "왜 느린지 봐줘", "a send's title is the question");
        Check(PaneTitle.Requested(pane with { Logs = [] }, "/clear").Title == "/clear", "a slash command titles as before");
        return Task.CompletedTask;
    }

    /// <summary>The choice round-trips on a pane; an old save without it, or a damaged one, reads as Claude; restoring and a provider switch keep it only on a Claude agent pane.</summary>
    internal static Task TheChoiceIsSavedPerPaneAndKeptOnlyOnAClaudeAgentPane()
    {
        var session = new RunSession { Id = "p", WorkspaceId = "w", AskTarget = "codex" };
        var json = JsonSerializer.Serialize(session, Wire.Json);
        Check(json.Contains("\"askTarget\":\"codex\"", StringComparison.Ordinal), "saved as askTarget: " + json);
        Check(JsonSerializer.Deserialize<RunSession>(json, Wire.Json)!.AskTarget == "codex", "read back");
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("askTarget");
        Check(JsonSerializer.Deserialize<RunSession>(node.ToJsonString(), Wire.Json)!.AskTarget is null, "an old save reads as Claude");
        node["askTarget"] = 7;
        Check(JsonSerializer.Deserialize<RunSession>(node.ToJsonString(), Wire.Json)!.AskTarget is null, "a damaged value reads as Claude and keeps the pane");
        Check(!JsonSerializer.Serialize(new RunSession { Id = "q", WorkspaceId = "w" }, Wire.Json).Contains("askTarget", StringComparison.Ordinal), "Claude writes nothing");

        var workspace = new Workspace { Id = "w", Path = Path.GetTempPath() };
        var claude = new RunSession { Id = "claude", WorkspaceId = "w", AskTarget = "codex" };
        var stale = new RunSession { Id = "stale", WorkspaceId = "w", AskTarget = "no-such-target" };
        var codex = new RunSession { Id = "codex", WorkspaceId = "w", Provider = "codex", AskTarget = "codex" };
        var shell = new RunSession { Id = "shell", WorkspaceId = "w", Kind = "shell", AskTarget = "codex" };
        var restored = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [claude, stale, codex, shell] }, true).Sessions.ToDictionary(s => s.Id, s => s.AskTarget);
        Check(restored["claude"] == "codex", "a Claude agent pane keeps its choice");
        Check(restored["stale"] == "no-such-target", "one no longer offered is kept, and reads as Claude when sent");
        Check(restored["codex"] is null && restored["shell"] is null, "every other pane drops it");
        Check(SessionTemplate.SwitchProvider(claude, "codex").AskTarget is null, "another provider starts back on Claude");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The phone's detail carries the targets and the choice only for a pane that offers any; its setAskTarget action passes a
    /// target id or null to the desktop and refuses any other body (M/MightyCore/Remote/MobileRemoteModels.swift, MobileRemoteService.swift).
    /// </summary>
    internal static async Task ThePhoneSeesTheSwitchOnlyWhereItIsOffered()
    {
        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "offers", WorkspaceId = workspace.Id, AskTarget = "codex" }, new RunSession { Id = "plain", WorkspaceId = workspace.Id, AskTarget = "codex" }] });
            var actions = new List<MobileDesktopAction>();
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (action, _) => { actions.Add(action); return Task.FromResult<object?>(null); },
                pane => pane.Id == "offers" ? new([], AskTargets: [new("codex", "Codex"), new("antigravity", "Antigravity")], AskTarget: "codex") : new([], AskTargets: [], AskTarget: "codex"));
            async Task<JsonElement> Get(string path) { var answer = await router.RouteAsync("GET", path, null, "phone", CancellationToken.None); Check(answer.Status == 200, path + " answers"); return JsonSerializer.SerializeToElement(answer.Body, Wire.Json); }

            var offers = await Get("/m1/sessions/offers");
            Check(offers.TryGetProperty("askTargets", out var targets) && targets.EnumerateArray().Select(t => t.GetProperty("id").GetString() + "=" + t.GetProperty("label").GetString()).SequenceEqual(["codex=Codex", "antigravity=Antigravity"]), "the targets travel in the menu's order: " + offers);
            Check(offers.GetProperty("askTarget").GetString() == "codex", "with the choice in use");
            var plain = await Get("/m1/sessions/plain");
            Check(!plain.TryGetProperty("askTargets", out _) && !plain.TryGetProperty("askTarget", out _), "a pane that offers none sends neither field: " + plain);

            async Task<int> Post(string body) => (await router.RouteAsync("POST", "/m1/sessions/offers/setAskTarget", JsonDocument.Parse(body).RootElement, "phone", CancellationToken.None)).Status;
            Check(await Post("""{"target":"antigravity"}""") == 200 && actions[^1] is { Action: "setAskTarget" } chosen && chosen.Body.Text("target") == "antigravity", "a target id goes to the desktop");
            Check(await Post("""{"target":null}""") == 200 && actions[^1].Body.GetProperty("target").ValueKind == JsonValueKind.Null, "null (Claude) goes too");
            var count = actions.Count;
            Check(await Post("""{"target":7}""") == 400 && actions.Count == count, "any other value is refused before the desktop sees it");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
