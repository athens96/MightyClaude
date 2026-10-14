using System.Buffers.Binary;
using System.Text.Json;
using MightyClaude.Core;

internal static class CompanionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static Task AnimationAndCarousel()
    {
        Check(CompanionPet.Frame(0, .29, false) == 1 && CompanionPet.Frame(0, 100, true) == 0, "durations/reduced motion");
        for (var row = 0; row < 9; row++) for (var tick = 0; tick < 100; tick++) Check(CompanionPet.Frame(row, tick / 30.0, false) < CompanionPet.FrameCounts[row], "frame bounds");
        Check(CompanionAnimation.Row("running", new("r", "claude", "read", "running", "read")) == 8, "review animation");
        Check(CompanionAnimation.Row("running", new("r", "claude", "command", "waiting", "blocked")) == 6, "waiting priority");
        Check(CompanionAnimation.Row("completed", null, true) == 4 && CompanionAnimation.Row("error", null) == 5, "completion/failure animation");
        string[] active = ["a", "b", "c"];
        Check(CompanionCarousel.Shown("b", active, "a") == "b" && CompanionCarousel.Shown("gone", active, "a") == "a", "pin clears after completion");
        Check(CompanionCarousel.Step("c", active, 1) == "a" && CompanionCarousel.Step("a", active, -1) == "c" && CompanionCarousel.Step(null, active, -1) == "c", "wrapping pages");
        Check(CompanionCarousel.Step("a", ["a"], 1) is null && CompanionCarousel.Position("b", active) == 2, "page visibility");
        return Task.CompletedTask;
    }
    /// <summary>Which buttons the bubble offers for an approval and for a finished plan (M/AgentCompanionViews.swift:339-365, 421-436).</summary>
    internal static Task BubbleButtons()
    {
        static string Ids(IReadOnlyList<CompanionBubbleButton> buttons) => string.Join(",", buttons.Select(b => b.Id));
        var approval = CompanionBubble.Approval(true);
        Check(Ids(approval) == "open,review,deny,allow", "an approval: open and the whole request, then Deny and Allow once");
        Check(approval[0] is { Leading: true, Icon: false } && approval[1] is { Leading: true, Icon: true } && approval[2] is { Leading: false, Prominent: false } && approval[3] is { Prominent: true, Disabled: false }, "open at the left, Allow once prominent");
        Check(approval[2].Label == ToolPermissionStrings.ButtonDeny && approval[3].Label == ToolPermissionStrings.ButtonAllowOnce && approval[0].Label == Locale.Get("companion.button.open"), "the pane card's own words");
        var blocked = CompanionBubble.Approval(false);
        Check(Ids(blocked) == "open,deny,allow" && blocked[2] is { Prominent: true, Disabled: true }, "a request that cannot be allowed keeps Allow once, off");
        var plan = CompanionBubble.Plan(false);
        Check(Ids(plan) == "open,plan-review,plan-cancel,plan-revise,plan-approve-confirm,plan-approve" && plan.Count == 6, "a plan's six buttons, in the Mac's order");
        Check(plan.Take(2).All(b => b is { Icon: true, Leading: true, OwnRow: false }) && plan.Skip(2).Take(2).All(b => b is { Icon: false, Leading: false, OwnRow: false }) && plan.Skip(4).All(b => b.OwnRow), "two symbols at the left, Cancel and Request changes at the right, each approval on its own row");
        Check(plan.Count(b => b.Prominent) == 1 && plan[5].Prominent && plan.All(b => !b.Disabled), "only the approval that edits by itself is prominent");
        Check(plan.Select(b => b.Label).SequenceEqual(new[] { "companion.button.open", "companion.plan.review", "plan.card.cancel", "plan.card.revise", "plan.card.approveConfirm", "plan.card.approveAuto" }.Select(key => Locale.Get(key))), "each button's own words");
        Check(CompanionBubble.Plan(true).All(b => b.Disabled), "every button is off while an answer is on its way");
        return Task.CompletedTask;
    }
    /// <summary>The plan as the bubble draws it: blocks in small type, the whole plan (M/AgentMarkdownView.swift compact).</summary>
    internal static Task PlanBlocks()
    {
        var plan = "# Ship the **pet**\n\nFirst line\nsecond `line` of *one* paragraph.\n\n1. Draw the plan\n2) Route its buttons\n   over two lines\n- [x] done\n- [ ] not `yet`\n  - nested_snake_case item\n\n> quoted\n> words\n\n```cs\nvar a = \"*not bold*\";\n```\n---\n| a | b |\n|---|---|\n\nSee [the doc](https://example.com/x) and ![a picture](p.png) \\*as is\\*";
        var blocks = CompanionBubble.PlanBlocks(plan);
        string Shape(CompanionPlanBlock b) => b.Kind + (b.Level > 0 ? b.Level.ToString() : "") + (b.Marker is null ? "" : "[" + b.Marker + "]") + ":" + b.Text;
        var shapes = blocks.Select(Shape).ToArray();
        Check(shapes.SequenceEqual(new[]
        {
            "heading1:Ship the pet", "paragraph:First line second line of one paragraph.", "item[1.]:Draw the plan", "item[2.]:Route its buttons over two lines", "item[☑]:done", "item[☐]:not yet",
            "item1[•]:nested_snake_case item", "quote:quoted words", "code:var a = \"*not bold*\";", "rule:", "code:| a | b |\n|---|---|", "paragraph:See the doc and a picture *as is*",
        }), "the plan's blocks:\n" + string.Join("\n", shapes));
        string Runs(CompanionPlanBlock b) => string.Join(",", (b.Spans ?? []).Select(s => s.Style + "=" + b.Text.Substring(s.Start, s.Length)));
        Check(Runs(blocks[0]) == "bold=pet" && Runs(blocks[1]) == "code=line,italic=one" && Runs(blocks[5]) == "code=yet" && blocks[6].Spans is null && blocks[8].Spans is null, "bold, italic and code runs; an underscore inside a word is the word's");
        Check(Runs(CompanionBubble.PlanBlocks("**a `b` *c***")[0]) is var nested && nested.Contains("code=b", StringComparison.Ordinal) && nested.Contains("bold=a b c", StringComparison.Ordinal), "runs inside runs: " + nested);
        // Nothing is dropped: an unclosed mark or fence stays as written, and a plan is never cut short.
        Check(CompanionBubble.PlanBlocks("a * b ** c _ d ` e")[0].Text == "a * b ** c _ d ` e" && CompanionBubble.PlanBlocks("```\nopen").Single() is { Kind: "code", Text: "open" }, "stray marks are words");
        var many = string.Join("\n", Enumerable.Range(1, 400).Select(i => i + ". step " + i));
        Check(CompanionBubble.PlanBlocks(many) is { Count: 400 } all && all[^1].Text == "step 400" && all[^1].Marker == "400.", "the whole plan, every step");
        Check(CompanionBubble.PlanBlocks("# Learn C#")[0].Text == "Learn C#" && CompanionBubble.PlanBlocks("## Closed ##")[0].Text == "Closed" && CompanionBubble.PlanBlocks("#").Count == 0, "a heading keeps a # of its own words and drops only a closing run");
        Check(CompanionBubble.PlanBlocks("") is { Count: 0 } && CompanionBubble.PlanBlocks(" \n\n") is { Count: 0 }, "an empty plan draws nothing");
        Check(CompanionBubble.PlanBlocks(new string('*', 60_000)) is { Count: >= 1 }, "a plan of stray marks is still read");
        var huge = "# " + new string('가', CompanionBubble.MaximumPlanBytes / 3 + 1);
        Check(CompanionBubble.PlanBlocks(huge).Single() is { Kind: "paragraph" } literal && literal.Text == huge, "a source past the render bound is one literal paragraph");
        return Task.CompletedTask;
    }
    internal static Task CatalogBoundaries()
    {
        var root = Verification.Temp(); Directory.CreateDirectory(root);
        try
        {
            // Header-only fixture intentionally verifies metadata, not platform decoding.
            var png = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13); "IHDR"u8.CopyTo(png.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 1536); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 1872); png[24] = 8; png[25] = 6;
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source); File.WriteAllBytes(Path.Combine(source, "sheet.png"), png);
            void Manifest(string path, int version = 1) => File.WriteAllText(Path.Combine(source, "pet.json"), JsonSerializer.Serialize(new { displayName = "한글 펫", spritesheetPath = path, spriteVersionNumber = version }));
            Manifest("sheet.png"); var pet = CompanionPet.Load(source, "test"); Check(pet.Width == 1536 && pet.Height == 1872 && pet.Name == "한글 펫", "valid manifest metadata");
            foreach (var path in new[] { "../outside.png", "/tmp/outside.png", "C:\\outside.png", "\\\\server\\image.png", "sheet.png:stream" })
            {
                Manifest(path); try { CompanionPet.Load(source, "test"); throw new InvalidOperationException("escaped pet path accepted: " + path); } catch (IOException) { }
            }
            Manifest("sheet.png", 3); try { CompanionPet.Load(source, "test"); throw new InvalidOperationException("wrong version accepted"); } catch (IOException) { }
            Manifest("sheet.png"); png[25] = 2; File.WriteAllBytes(Path.Combine(source, "sheet.png"), png);
            try { CompanionPet.Load(source, "test"); throw new InvalidOperationException("opaque sheet accepted"); } catch (IOException) { }
            var profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
            var prefs = new CompanionPreferences(ReducedMotion: true, SelectedPet: "local:pet", Left: -100, Top: 200); prefs.Save(profile); Check(CompanionPreferences.Load(profile) == prefs, "preference roundtrip");
            var installed = pet.Install(profile); Check(installed.Id.StartsWith("local:", StringComparison.Ordinal) && installed.Image.SequenceEqual(pet.Image), "import uses validated byte snapshot");
            var catalog = CompanionPet.Catalog(Path.Combine(root, "missing"), profile, Path.Combine(root, "no-codex")); Check(catalog.Count == 1 && catalog[0].Image.Length == 0, "catalog does not retain all atlases");
            if (!OperatingSystem.IsWindows())
            {
                var linked = Path.Combine(root, "linked"); Directory.CreateDirectory(linked); Directory.CreateSymbolicLink(Path.Combine(linked, "pets"), Path.Combine(profile, "pets"));
                try { pet.Install(linked); throw new InvalidOperationException("linked import destination accepted"); } catch (IOException) { }
            }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
