using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>
/// The panel every style projects, against the goldens the macOS StyleGoldenContractTests record in <c>styles/golden</c>:
/// the same five fixed inputs (M/StyleGoldenSupport.swift projection), compared whole, byte for byte — for the repository
/// manifests, the bundled styles and the test-only fixtures of <c>styles/fixtures</c>. (v7 dropped
/// <c>capabilityOpen</c>: with no built-in feature left it was <c>lastGroup</c> again, byte for byte.)
/// </summary>
internal static class StyleGoldenVerification
{
    private const string Manifests = "MightyClaude.Core.Tests.Styles.repository.", Goldens = "MightyClaude.Core.Tests.Styles.golden.";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    private static T Korean<T>(Func<T> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "ko"; Locale.ResetCache();
        try { return body(); }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private static Dictionary<string, byte[]> Resources(string prefix)
    {
        var assembly = Assembly.GetExecutingAssembly(); var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!; using var output = new MemoryStream(); stream.CopyTo(output);
            found[name[prefix.Length..]] = output.ToArray();
        }
        return found;
    }

    /// <summary>A plan three of seven items done, read by the engine's own checklist parser.</summary>
    private const string Checklist = "- [x] one\n- [x] two\n- [x] three\n- [ ] four\n- [ ] five\n- [ ] six\n- [ ] seven\n";
    /// <summary>Two sub-agents started and one ordinary tool call.</summary>
    private static readonly (string, string)[] Events = [("subagent.start", "plan review"), ("tool.call", "Read"), ("subagent.start", "task 1")];

    /// <summary>The <c>withState</c> reading (M/StyleGoldenSupport.swift stateReading): every file source parses the checklist, the run events aggregate, and a style that reads the plan stage reads the fixed run state.</summary>
    private static StyleStateReading? StateReading(RegisteredStyle style, string stage = "executing")
    {
        if (!style.Manifest.Root.TryGetProperty("stateSources", out var sources)) return null;
        var (todos, work) = ClaudePlanStyleVerification.Fixture();
        return Korean(() =>
        {
            var files = StyleManifest.Items(sources, "files").Select(source => StyleStateEngine.Parse(Checklist, StyleManifest.Text(source, "parser")!, StyleManifest.Text(source, "widget")!)).ToArray();
            return style.Evaluator.ReadsPlanState ? StyleStateEngine.Reading(style.Manifest, files, Events, stage, todos, work) : StyleStateEngine.Reading(style.Manifest, files, Events);
        });
    }

    private static JsonObject Projection(RegisteredStyle style)
    {
        var manifest = style.Manifest;
        var ready = new StylePrerequisiteResult(true, [], null, null);
        var probe = StyleManifest.Items(manifest.Root.GetProperty("prerequisites"), "probes").FirstOrDefault();
        var missing = new StylePrerequisiteResult(false, StyleManifest.Text(probe, "missing") is { } line ? [line] : [], StyleManifest.Text(probe, "hint"), null);
        // No built-in feature exists to read (§1.8), so no input carries a state.
        var none = new Dictionary<string, string>();
        StylePanel Make(string[]? prompts = null, string? group = null, StylePrerequisiteResult? prerequisites = null, StyleStateReading? state = null, bool running = false) =>
            StylePanelProjection.Make(style, prompts ?? [], group, none, prerequisites ?? ready, running, null, state);
        var cases = new Dictionary<string, StylePanel>
        {
            ["empty"] = Make(),
            ["afterFirstAction"] = Make(prompts: [manifest.Actions.FirstOrDefault()?.Prompt("") ?? ""]),
            ["notReady"] = Make(prerequisites: missing),
            ["firstGroup"] = Make(group: manifest.Groups.FirstOrDefault()?.Id),
            ["lastGroup"] = Make(group: manifest.Groups.LastOrDefault()?.Id),
        };
        if (StateReading(style) is { } reading) cases["withState"] = Make(state: reading);
        // §1.17: one panel per plan stage for a style that reads it; a plan waiting for its answer is a running turn.
        if (style.Evaluator.ReadsPlanState)
            foreach (var stage in StyleStateEngine.PlanStages)
                cases["planStage" + char.ToUpperInvariant(stage[0]) + stage[1..]] = Make(state: StateReading(style, stage) ?? new(new Dictionary<int, StyleFileState>(), [], stage), running: stage == "awaitingApproval");
        var projection = new JsonObject();
        foreach (var (name, panel) in cases) projection[name] = JsonSerializer.SerializeToNode(panel, Wire.Json);
        return projection;
    }

    private static void Compare(RegisteredStyle style, IReadOnlyDictionary<string, byte[]> goldens, HashSet<string> compared)
    {
        var name = style.Id + ".panel.json";
        Check(goldens.TryGetValue(name, out var expected), "styles/golden has no " + name + ": record it on the Mac (MIGHTY_STYLE_GOLDEN=record)");
        var produced = Encoding.UTF8.GetString(StylePanelProjection.Canonical(Projection(style)));
        // A Windows checkout may hold the golden with CRLF line ends; the bytes compared are the ones the Mac wrote.
        var golden = new UTF8Encoding(false, true).GetString(expected!).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (produced != golden)
        {
            string[] ours = produced.Split('\n'), theirs = golden.Split('\n');
            var at = Enumerable.Range(0, Math.Min(ours.Length, theirs.Length)).FirstOrDefault(i => ours[i] != theirs[i], Math.Min(ours.Length, theirs.Length));
            string Around(string[] lines) => string.Join("\n", lines.Skip(Math.Max(0, at - 6)).Take(10));
            throw new InvalidOperationException($"{name} differs from the golden at line {at + 1} ({ours.Length} lines against {theirs.Length}).\nproduced:\n{Around(ours)}\ngolden:\n{Around(theirs)}");
        }
        compared.Add(name);
    }

    /// <summary>Every manifest in <c>styles/</c> and every bundled style projects exactly its golden, and no golden is left uncompared (macOS StyleGoldenContractTests).</summary>
    internal static Task EveryStyleMatchesItsGolden()
    {
        var goldens = Resources(Goldens); var manifests = Resources(Manifests); var compared = new HashSet<string>(StringComparer.Ordinal);
        Check(goldens.Count > 0 && manifests.Count > 0, $"the goldens ({goldens.Count}) and the repository manifests ({manifests.Count}) must both be embedded from styles/");
        foreach (var (file, bytes) in manifests)
        {
            var manifest = StyleManifestDecoder.Decode(bytes, "user");
            Check(file == manifest.Id + ".json", file + " must be named after its id " + manifest.Id);
            Compare(new RegisteredStyle(manifest, "user", file, null, Convert.ToHexStringLower(SHA256.HashData(bytes)), "approved", bytes), goldens, compared);
        }
        var root = Verification.Temp();
        try
        {
            var bundled = StyleRegistry.Load(Path.Combine(root, "profile"), Path.Combine(root, "workspace")).Styles.Where(s => s.Source == "bundled").ToArray();
            Check(bundled.Length > 0, "the bundled styles did not load");
            Check(bundled.Select(s => s.Id).Order(StringComparer.Ordinal).SequenceEqual(["claude-plan", "ouroboros"]), "the bundled styles are Claude plan and Ouroboros: " + string.Join(", ", bundled.Select(s => s.Id)));
            foreach (var style in bundled) Compare(style, goldens, compared);
            // No state source, no plan stage: those cases are not recorded at all (macOS theBundledStylesHaveGoldens).
            var ouroboros = bundled.Single(s => s.Id == "ouroboros");
            Check(StateReading(ouroboros) is null && !Projection(ouroboros).Any(c => c.Key == "withState" || c.Key.StartsWith("planStage", StringComparison.Ordinal)), "a style without state sources has no withState and no plan-stage case");
        }
        finally { Directory.Delete(root, true); }
        // The test-only manifests of styles/fixtures, decoded as an approved user style under the test vocabulary (the only one
        // deck decodes under) exactly as the Mac does, and compared whole like the bundled styles (macOS theFixtureManifestsHaveGoldens).
        {
            var fixtures = StyleFixtures.Files();
            Check(fixtures.Keys.Order(StringComparer.Ordinal).SequenceEqual(["deck.json", "loop.json"]), "styles/fixtures holds deck and loop: " + string.Join(", ", fixtures.Keys));
            var projections = new List<JsonObject>();
            foreach (var file in fixtures.Keys.Order(StringComparer.Ordinal))
            {
                var style = StyleFixtures.Style(file[..^".json".Length]);
                Check(file == style.Id + ".json", file + " must be named after its id " + style.Id);
                projections.Add(Projection(style));
                Compare(style, goldens, compared);
            }
            // lastGroup is not firstGroup again for at least one golden, so the byGroup input is not vacuous.
            Check(projections.Any(p => !JsonNode.DeepEquals(p["firstGroup"], p["lastGroup"])), "at least one golden's lastGroup differs from its firstGroup");
            var deck = Projection(StyleFixtures.CatalogueStyle);
            Check(deck["firstGroup"]!["next"]!.AsArray().Select(n => (string?)n).SequenceEqual(["tidy", "hate"]) && deck["lastGroup"]!["next"]!.AsArray().Select(n => (string?)n).SequenceEqual(["plan", "loop", "memo", "work", "nba"]), "the first and last group draw their own actions");
            Check(deck["empty"]!["actions"]!.AsArray().Single(a => (string?)a!["id"] == "nba")!["flags"]!.AsArray().Select(n => (string?)n).SequenceEqual(["userInvoked", "readOnly"]), "an action's two flags are recorded in order");
        }
        // §1.16: a style with file and run-event sources records what the engine makes of them — the plan file as a bar and the
        // subagent.start count as a label — and the file state moves it to the phase a current plan means. No bundled or
        // repository style declares either kind since v7, so the loop fixture stands in; its golden is compared above (macOS
        // aStyleWithFileAndRunEventSourcesRecordsItsReading).
        {
            var stateful = StyleFixtures.StatefulStyle;
            var withState = StateReading(stateful) ?? throw new InvalidOperationException("the fixture declares state sources");
            Check(withState.Widgets.Count == 2 && withState.Widgets[0] is { Kind: "progressBar", Value: 3, Total: 7 } && withState.Widgets[1] is { Kind: "label", Text: "서브에이전트 2회 시작" }, "a plan file reads as a bar and the sub-agents as a label");
            Check(stateful.Evaluator.CurrentPhase([], withState.Files)?.Id == "execute", "a current plan moves the style to execute");
            var recorded = Projection(stateful);
            Check((string?)recorded["withState"]?["phase"]?["id"] == "execute" && recorded["withState"]?["widgets"]?.AsArray().Count == 2, "the withState case records the reading's phase and widgets");
            Check((string?)recorded["empty"]?["phase"]?["id"] == "brainstorm" && recorded["empty"]?["widgets"] is null, "the empty case reads nothing");
            Check(!recorded.Any(c => c.Key.StartsWith("planStage", StringComparison.Ordinal)), "a style that does not read the plan stage records no plan-stage case");
        }
        var left = goldens.Keys.Where(name => !compared.Contains(name)).ToArray();
        Check(left.Length == 0, "goldens no style produced: " + string.Join(", ", left));
        Check(compared.Count == 6, "the six goldens of styles/golden are compared whole; compared " + string.Join(", ", compared.Order()));
        return Task.CompletedTask;
    }

    /// <summary>The golden's serialisation is fixed (macOS theGoldenSerialisationRulesAreFixed).</summary>
    internal static Task TheSerialisationRulesAreFixed()
    {
        var root = Verification.Temp();
        try
        {
            var style = StyleRegistry.Load(Path.Combine(root, "profile"), Path.Combine(root, "workspace")).Styles.Single(s => s.Id == "ouroboros");
            var panel = StylePanelProjection.Make(style, [], null, new Dictionary<string, string>(), new(true, [], null, null));
            var data = StylePanelProjection.Serialise(panel); var text = Encoding.UTF8.GetString(data);
            Check(text.EndsWith("}\n", StringComparison.Ordinal) && !text.EndsWith("}\n\n", StringComparison.Ordinal), "one trailing newline");
            Check(!text.Contains("\\/", StringComparison.Ordinal) && text.Contains("add Q00/ouroboros", StringComparison.Ordinal) && !text.Contains('\r'), "no escaped slash, no carriage return");
            Check(text.StartsWith("{\n  \"actions\" : [", StringComparison.Ordinal), "keys sorted, two-space indent");
            Check(StylePanelProjection.Serialise(panel).SequenceEqual(data), "the same panel serialises to the same bytes");
            // What the phone is sent is the same panel through the wire's own serialiser: the same keys and values.
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(panel, Wire.Json));
            Check(StylePanelProjection.Canonical(JsonNode.Parse(wire.RootElement.GetRawText())!).SequenceEqual(data), "the wire payload is the golden's shape");
            Check(Encoding.UTF8.GetString(StylePanelProjection.Canonical(JsonNode.Parse("{\"b\":[],\"a\":{},\"C\":\"x/\\\"y\\n\"}")!)) == "{\n  \"a\" : {\n\n  },\n  \"b\" : [\n\n  ],\n  \"C\" : \"x/\\\"y\\n\"\n}\n", "empty arrays and objects, case-blind key order and escapes as the Mac writes them");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
