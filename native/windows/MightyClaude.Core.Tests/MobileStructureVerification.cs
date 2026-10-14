using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using Fixture = MightyStructureVerification;

/// <summary>
/// <c>mighty.structure</c> against the checks of the Mac's MobileRemoteStructureTests: the desktop's structure view as the
/// phone receives it (the same columns, rows and cards <see cref="MightyStructure.Make"/> builds, bounded for the wire), and
/// the session route that sends it only when asked and changed.
/// </summary>
internal static class MobileStructureVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Json(object? value) => JsonSerializer.Serialize(value, Wire.Json);
    private static int Bytes(string? text) => text is null ? int.MaxValue : Encoding.UTF8.GetByteCount(text);
    private static MobileStructure Free(params MightyGraphRun[] runs) => MobileStructure.Make(MightyStructure.Make(runs, null, null), runs.Length) ?? throw new InvalidOperationException("no structure");

    internal static Task CarriesTheDesktopShape()
    {
        var style = Fixture.Style("ouroboros");
        var runs = new[] { Fixture.Run("r1", "ooo interview app"), Fixture.Run("r2", "more questions"), Fixture.Run("r3", "ooo seed", "running", [Fixture.Agent("a1", status: "running"), Fixture.Agent("a2", "a1")]) };
        runs[2].RootEntries = [Fixture.Said("m", "writing the seed"), Fixture.Tool("t", "seed.yaml")];
        var shape = MightyStructure.Make(runs, style, Fixture.Phase(style, "seed"));
        var wire = MobileStructure.Make(shape, runs.Length, id => id == "r3" ? "Seed" : null)!;
        Check(wire is { V: 1, Shape: "phases", Rows: null, Omitted: null }, "a styled pane travels as versioned phases");
        var columns = wire.Columns!; var desktop = shape.Columns!;
        Check(columns.Select(c => c.Id).SequenceEqual(desktop.Select(c => c.Id)) && columns.Select(c => c.Title).SequenceEqual(desktop.Select(c => c.Phase.Title)) && columns.Select(c => c.Step).SequenceEqual(desktop.Select(c => MightyStructure.Word(c.Step))), "the phone gets the desktop's columns and their steps");
        Check(Json(columns.Select(c => c.Items.Select(i => i.RunId))) == Json(desktop.Select(c => c.Items.Select(i => i.RunId))), "each request stands where the desktop drew it");
        var card = columns.Single(c => c.Id == "seed").Items[0]; var source = desktop.Single(c => c.Id == "seed").Items[0];
        Check(card is { Ordinal: 3, Title: "Seed" } && card.Status == source.Status && card.Finished == source.Finished && card.Request == source.Request?.Text && card.Message == source.Message?.Text, "a card carries its number, title, status and texts");
        Check(card.Activity.Select(a => a.Text).SequenceEqual(source.Activity.Select(a => a.Text)) && card.Tally == new MobileStructure.Tally(source.Tally.Agents, source.Tally.Tools), "a card carries its tool calls and tally");
        Check(Json(card.Clusters.Select(c => c.Nodes.Select(n => n.Id))) == "[[\"a1\",\"a2\"]]" && card.Clusters[0].Nodes.Select(n => n.Depth).SequenceEqual([0, 1]) && columns.Single(c => c.Id == "interview").Items[0].Title is null, "a card carries its sub-agent trees; an unnamed request has no title");

        var free = new[] { Fixture.Run("done", final: "All done\n\n```swift\nlet x = 1\n```"), Fixture.Run("failed", status: "error"), Fixture.Run("live", status: "running") };
        var rows = Free(free).Rows!; var drawn = MightyStructure.Make(free, null, null).Rows!;
        Check(Free(free) is { Shape: "free", Columns: null } && rows.Select(r => r.Item.RunId).SequenceEqual(drawn.Select(r => r.Item.RunId)) && rows.Select(r => r.Item.Ordinal).SequenceEqual([1, 2, 3]), "a free pane travels as the desktop's rows");
        Check(rows.Select(r => r.Request + r.Execution + r.Result).SequenceEqual(drawn.Select(r => MightyStructure.Word(r.Request) + MightyStructure.Word(r.Execution) + MightyStructure.Word(r.Result))) && rows[0].Item.Answer == drawn[0].Item.Answer?.Text, "a row carries its three steps and its answer");

        var compact = Free(Fixture.Run("r1")); using var json = JsonDocument.Parse(Json(compact)); var root = json.RootElement;
        Check(root.GetProperty("v").GetInt32() == 1 && root.GetProperty("shape").GetString() == "free" && !root.TryGetProperty("columns", out _) && !root.TryGetProperty("omitted", out _), "the wire shape is versioned and leaves out what is absent");
        var row = root.GetProperty("rows")[0];
        Check(row.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["execution", "item", "request", "result"]) && row.GetProperty("item").GetProperty("runId").GetString() == "r1" && !row.GetProperty("item").TryGetProperty("omittedNodes", out _), "a row is its item and three steps");
        Check(Json(JsonSerializer.Deserialize<MobileStructure>(Json(compact), Wire.Json)) == Json(compact), "the wire shape reads back as it was written");

        Check(MobileStructure.Make(MightyStructure.Make([], null, null), 0) is null, "nothing to draw is no structure");
        var cache = new MobileStructureCache();
        Check(cache.Structure("p", [], null, null) == new MobileStructureCache.Built(null, 0), "nothing to draw has the tag 0");
        // A style with phases still shows its bar before the first request.
        var empty = cache.Structure("q", [], style, Fixture.Phase(style, "goal")).Value;
        Check(empty?.Columns is { Count: > 0 } bar && bar.All(c => c.Items.Count == 0) && bar[0].Step == "current", "a style's phases travel before the first request");
        return Task.CompletedTask;
    }

    /// <summary><c>native/contracts/fixtures/mobile-structure.json</c>, which the Mac's and the phone's tests read too: every field the phone reads is one this type writes, and nothing more.</summary>
    internal static Task SharedFixtureIsTheWireShape()
    {
        using var resource = typeof(MobileStructureVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.MobileStructure.json") ?? throw new InvalidOperationException("the shared fixture is not embedded");
        var fixture = JsonNode.Parse(resource)!.AsObject();
        foreach (var name in new[] { "styled", "free" })
        {
            var raw = fixture[name] ?? throw new InvalidOperationException("the fixture has no " + name);
            var decoded = raw.Deserialize<MobileStructure>(Wire.Json) ?? throw new InvalidOperationException(name + " does not decode");
            Check(JsonNode.DeepEquals(JsonNode.Parse(Json(decoded)), JsonNode.Parse(raw.ToJsonString())), name + " changed through the wire type: " + Json(decoded));
        }
        return Task.CompletedTask;
    }

    internal static Task BoundsAndBudget()
    {
        var text = string.Concat(Enumerable.Repeat("가나다라 ", 2_000));
        var agent = Fixture.Agent("a"); agent.Input = text; agent.Entries = [Fixture.Said("o", text), Fixture.Tool("t", new string('x', 900))];
        var live = Fixture.Run("r", text, "running", [agent]); live.RootEntries = [Fixture.Said("m", text)];
        var item = Free(live).Rows![0].Item; var node = item.Clusters[0].Nodes[0];
        Check(Bytes(item.Request) <= MobileStructure.RequestBytes && item.Request!.EndsWith('…') && Bytes(item.Message) <= MobileStructure.MessageBytes, "a request and a message are capped under the desktop's bounds");
        Check(Bytes(node.Prompt) <= MobileStructure.PromptBytes && Bytes(node.Output) <= MobileStructure.OutputBytes && Bytes(node.Activity?.Text) <= MobileStructure.ActivityBytes, "a sub-agent's texts are capped");
        // The answer is Markdown: a fence the cut leaves open is closed inside the cap.
        var answer = Free(Fixture.Run("d", final: "intro\n```\n" + string.Concat(Enumerable.Repeat("code line\n", 1_000)))).Rows![0].Item.Answer;
        Check(Bytes(answer) <= MobileStructure.AnswerBytes && answer!.EndsWith("\n```", StringComparison.Ordinal) && MightyStructure.OpenFence(answer) is null, "a cut answer closes the fence it leaves open, inside its cap");

        var many = Free(Enumerable.Range(1, 25).Select(i => Fixture.Run("r" + i)).ToArray());
        Check(many.Rows is { Count: MobileStructure.MaximumItems } newest && newest[0].Item is { RunId: "r6", Ordinal: 6 } && newest[^1].Item.RunId == "r25" && many.Omitted == 5, "only the newest requests travel, the rest counted");

        // One chain eight deep, then wide siblings: 8 + 70 blocks.
        var wide = Enumerable.Range(0, 8).Select(i => Fixture.Agent("c" + i, i == 0 ? null : "c" + (i - 1))).Concat(Enumerable.Range(0, 70).Select(i => Fixture.Agent("w" + i))).ToArray();
        var crowded = Free(Fixture.Run("r", agents: wide)).Rows![0].Item; var nodes = crowded.Clusters.SelectMany(c => c.Nodes).ToList();
        Check(nodes.Count == MobileStructure.MaximumNodes && crowded.OmittedNodes == 78 - MobileStructure.MaximumNodes && nodes.Max(n => n.Depth) == MobileStructure.MaximumDepth, "a request carries at most sixty nodes");
        // The tally still counts the whole request.
        Check(nodes.Take(8).Select(n => n.Depth).SequenceEqual([0, 1, 2, 3, 4, 5, 6, 6]) && crowded.Tally.Agents == 78, "a deep node is drawn at the depth cap; the tally counts the whole request");

        // Each request near the per-text caps with sixty full nodes: far over the budget together.
        var quoted = string.Concat(Enumerable.Repeat("\"quoted\" ", 600));
        MightyGraphRun Heavy(string id) => Fixture.Run(id, quoted, agents: Enumerable.Range(0, 60).Select(i =>
        {
            var block = Fixture.Agent(id + "-" + i); block.Input = quoted; block.Entries = [Fixture.Said("o" + i, quoted)];
            return block;
        }).ToArray(), final: quoted);
        var six = Enumerable.Range(1, 6).Select(i => Heavy("r" + i)).ToArray(); var budgeted = Free(six); var carried = budgeted.Rows!;
        Check(Encoding.UTF8.GetByteCount(Json(budgeted)) <= MobileStructure.BudgetBytes && carried[^1].Item.RunId == "r6" && carried.Count < six.Length, "the whole value stays under the budget: " + Encoding.UTF8.GetByteCount(Json(budgeted)));
        Check(carried.Select(r => r.Item.RunId).SequenceEqual(six.TakeLast(carried.Count).Select(r => r.Id)) && budgeted.Omitted == six.Length - carried.Count, "the budget drops older requests first");

        var trees = MobileStructure.ItemFor(MightyStructure.ItemFor(Fixture.Run("r", agents: [Fixture.Agent("a"), Fixture.Agent("b", "a"), Fixture.Agent("c")]), 0), null);
        Check(Json(trees.Clusters.Select(c => c.Nodes.Select(n => n.Id))) == "[[\"a\",\"b\"],[\"c\"]]", "a request's trees travel root first");
        var two = MobileStructure.Keeping(trees, 2); var none = MobileStructure.Keeping(trees, 0);
        Check(Json(two.Clusters.Select(c => c.Nodes.Select(n => n.Id))) == "[[\"a\",\"b\"]]" && two.OmittedNodes == 1 && none.Clusters.Count == 0 && none.OmittedNodes == 3 && Json(MobileStructure.Keeping(trees, 9)) == Json(trees), "a request keeps its first nodes, the rest counted as left out");
        // One request whose sixty nodes alone are past the budget: it travels, trimmed to fit.
        var escaped = string.Concat(Enumerable.Repeat("\"\\", 2_000));
        var big = Fixture.Run("big", escaped, agents: Enumerable.Range(0, 60).Select(i =>
        {
            var block = Fixture.Agent("n" + i); block.Title = escaped; block.Input = escaped; block.Entries = [Fixture.Said("o" + i, escaped), Fixture.Tool("t" + i, escaped)];
            return block;
        }).ToArray(), final: escaped);
        var fitted = Free(big); var sent = fitted.Rows![0].Item; var count = sent.Clusters.Sum(c => c.Nodes.Count);
        Check(Encoding.UTF8.GetByteCount(Json(fitted)) <= MobileStructure.BudgetBytes && count is > 0 and < 60 && sent.OmittedNodes == 60 - count, "a request over the budget alone keeps its first nodes: " + count);

        var same = Free(Fixture.Run("same"), Fixture.Run("other"), Fixture.Run("same", "again"));
        Check(same.Rows!.Select(r => r.Item.RunId).SequenceEqual(["other", "same"]) && same.Rows![^1].Item.Ordinal == 3 && same.Omitted is null, "a repeated run id travels once, as its newest request");

        // A family emoji is one character of many scalars: never split.
        const string family = "👨‍👩‍👧‍👦";
        var cut = MobileStructure.Capped(string.Concat(Enumerable.Repeat(family, 40)), 100); var elements = new List<string>();
        for (var walker = StringInfo.GetTextElementEnumerator(cut); walker.MoveNext();) elements.Add(walker.GetTextElement());
        Check(Bytes(cut) <= 100 && cut.EndsWith('…') && elements.Count > 1 && elements.SkipLast(1).All(e => e == family), "a cut falls between characters");
        // A long fence early on: the shorter second cut still ends inside it, and its close fits.
        var fence = new string('`', 40);
        var closed = MobileStructure.Capped("intro\n" + fence + "\n" + string.Concat(Enumerable.Repeat("code\n", 200)), 300, markdown: true);
        Check(Bytes(closed) <= 300 && closed.EndsWith("\n" + fence, StringComparison.Ordinal) && MightyStructure.OpenFence(closed) is null, "a cut closes the fence it leaves open, inside the cap");
        return Task.CompletedTask;
    }

    internal static Task CacheRebuildsOnlyWhenTheStructureMoves()
    {
        var cache = new MobileStructureCache();
        var run = Fixture.Run("r", status: "running"); run.RootEntries = [Fixture.Said("m", "reading")];
        var first = cache.Structure("p", [run], null, null);
        Check(cache.Builds == 1 && first.Tag == MobileStructure.Tag(first.Value) && first.Tag is > 0 and <= 999_999_999_999, "a built value carries its tag");
        // A streamed token that moves nothing drawn reuses the value.
        run.RootEntries[0] = run.RootEntries[0] with { Text = "reading the file" };
        Check(ReferenceEquals(cache.Structure("p", [run], null, null), first) && cache.Builds == 1, "a streamed token that moves nothing drawn reuses the value");
        // A new block moves the structure.
        run.Agents.Add(Fixture.Agent("a"));
        Check(cache.Structure("p", [run], null, null).Value?.Rows?[0].Item.Clusters.Count == 1 && cache.Builds == 2, "a new block moves the structure");
        // So does the style's key (its request titles), and another pane has its own.
        cache.Structure("p", [run], null, null, key: "other"); Check(cache.Builds == 3, "another key rebuilds");
        cache.Structure("q", [run], null, null); Check(cache.Builds == 4, "another pane has its own value");
        cache.Retain(new HashSet<string> { "q" }); cache.Structure("p", [run], null, null, key: "other");
        Check(cache.Builds == 5, "a forgotten pane is built again");

        var style = Fixture.Style("claude-plan"); var plans = new MobileStructureCache(); var runs = new[] { Fixture.Run("r1", source: "s1") };
        var planning = plans.Structure("p", runs, style, Fixture.Phase(style, "plan")).Value;
        var approved = plans.Structure("p", runs, style, Fixture.Phase(style, "execute"), [Fixture.Record("p1", "s1", PlanOutcome.ApprovedAuto)]).Value;
        Check(planning is not null && approved is not null && Json(planning) != Json(approved) && plans.Builds == 2, "the pane's phase and plans move the structure");
        Check(approved!.Columns!.Single(c => c.Id == "execute").Items.Select(i => i.RunId).SequenceEqual(["r1"]), "an approved plan's request travels under the executing phase");
        return Task.CompletedTask;
    }

    internal static async Task RouteSendsTheStructureOnlyWhenAskedAndChanged()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-mobile-structure-").FullName;
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "s", WorkspaceId = workspace.Id }] });
            var filler = ""; var asked = 0;
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null),
                _ => new([], Settings: new { filler }, Mighty: new { style = "cli", runs = new[] { "kept" } }));
            // A request whose nodes are near their caps, so the value is a good part of the reply limit.
            var escaped = string.Concat(Enumerable.Repeat("\"\\", 2_000));
            var value = Free(Fixture.Run("r", escaped, agents: Enumerable.Range(0, 20).Select(i => { var block = Fixture.Agent("n" + i); block.Input = escaped; block.Entries = [Fixture.Said("o" + i, escaped)]; return block; }).ToArray(), final: escaped));
            var built = new MobileStructureCache.Built(value, MobileStructure.Tag(value));
            router.Structure = _ => { asked++; return built; };
            async Task<(int Status, JsonElement Mighty)> Call(string path)
            {
                var reply = await router.RouteAsync("GET", path, null, "phone", CancellationToken.None); var body = JsonSerializer.SerializeToElement(reply.Body, Wire.Json);
                return (reply.Status, body.TryGetProperty("mighty", out var mighty) ? mighty : default);
            }
            // Not asked (an older phone, or the chat view): neither field, and nothing is built.
            var plain = await Call("/m1/sessions/s?since=0&wait=0");
            Check(plain.Status == 200 && !plain.Mighty.TryGetProperty("structure", out _) && !plain.Mighty.TryGetProperty("structureTag", out _) && asked == 0, "a phone that did not ask gets neither field");
            // Asked with another tag: the value and its tag.
            var sent = await Call("/m1/sessions/s?since=0&wait=0&structure=0");
            Check(sent.Status == 200 && sent.Mighty.GetProperty("structure").GetRawText() == Json(value) && sent.Mighty.GetProperty("structureTag").GetInt64() == built.Tag && asked == 1, "a phone that asked gets the value and its tag");
            Check(sent.Mighty.GetProperty("runs")[0].GetString() == "kept" && sent.Mighty.GetProperty("style").GetString() == "cli", "the Mighty payload keeps its own fields beside the structure");
            // Asked with the tag it holds: the tag alone.
            var held = await Call("/m1/sessions/s?since=0&wait=0&structure=" + built.Tag);
            Check(held.Status == 200 && !held.Mighty.TryGetProperty("structure", out _) && held.Mighty.GetProperty("structureTag").GetInt64() == built.Tag, "a phone that holds the value gets the tag alone");
            // The state route has no structure, and the tag is a number.
            Check((await Call("/m1/state?since=0&wait=0&structure=0")).Status == 400 && (await Call("/m1/sessions/s?structure=x")).Status == 400 && MobileDesktopRouter.Capabilities.Contains("structure"), "the structure query is the session detail's alone; the host advertises it");

            // Past the reply limit the structure goes first; the tag stays, so the phone knows it is behind.
            var size = Encoding.UTF8.GetByteCount(Json(value));
            Check(size > 64 * 1_024, "the fixture's structure must be large enough to pass the limit: " + size);
            filler = new string('x', MobileDesktopRouter.MaximumReplyBytes - size / 2);
            var shed = await Call("/m1/sessions/s?since=0&wait=0&structure=0");
            Check(shed.Status == 200 && !shed.Mighty.TryGetProperty("structure", out _) && shed.Mighty.GetProperty("structureTag").GetInt64() == built.Tag && shed.Mighty.GetProperty("runs")[0].GetString() == "kept", "a reply over the limit sheds the structure first and keeps its tag");
            // A reply too large without it is still refused whole.
            filler = new string('x', MobileDesktopRouter.MaximumReplyBytes + 1);
            Check((await Call("/m1/sessions/s?since=0&wait=0&structure=0")).Status == 413, "a reply too large without the structure is still refused");
        }
        finally { Directory.Delete(directory, true); }
    }
}
