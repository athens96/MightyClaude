using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// The source preview's 변경사항 (FileDiff.cs, FileChanges.cs): native/contracts/fixtures/file-diff.json is the diff the Mac
// (FileDiffTests) is held to as well; the rest are the Windows capture, store, git base and phone route.
internal static class FileChangesVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Temp() => Directory.CreateTempSubdirectory("mighty-changes-").FullName;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.FileDiff.json";
        using var stream = typeof(FileChangesVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static string Generated(JsonElement spec, string side)
    {
        var text = new StringBuilder();
        foreach (var segment in spec.GetProperty(side).EnumerateArray())
        {
            var count = segment[0].GetInt32(); var pattern = segment[1].GetString()!; var first = segment[2].GetInt32();
            for (var n = first; n < first + count; n++) text.Append(pattern.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture))).Append('\n');
        }
        return text.ToString();
    }

    private static string Hunks(IEnumerable<FileDiffHunk> hunks) => JsonSerializer.Serialize(hunks, Wire.Json);
    private static string Hunks(JsonElement hunks) => JsonSerializer.Serialize(JsonSerializer.Deserialize<List<FileDiffHunk>>(hunks.GetRawText(), Wire.Json), Wire.Json);

    private static IReadOnlyList<FilePatchHunk> PatchHunks(JsonElement hunks) => FilePatch.Parse(hunks) ?? throw new InvalidOperationException("the fixture's hunks parse");

    internal static Task TheCutsAndReversePatchesFollowTheFixture()
    {
        var fixture = Fixture.Value;
        foreach (var item in fixture.GetProperty("cuts").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var cut = FileDiff.Cut(FileDiff.Compute(item.GetProperty("old").GetString()!, item.GetProperty("new").GetString()!)!.Hunks, item.GetProperty("lines").GetInt32());
            Check(Hunks(cut) == Hunks(item.GetProperty("expect")), $"cut {name}: {Hunks(cut)}");
        }
        foreach (var item in fixture.GetProperty("reversePatch").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var original = FilePatch.ReverseApply(item.GetProperty("current").GetString()!, PatchHunks(item.GetProperty("hunks")));
            var expect = item.GetProperty("expect");
            Check(expect.ValueKind == JsonValueKind.Null ? original is null : original == expect.GetString(), $"reverse patch {name}: {(original is null ? "null" : JsonSerializer.Serialize(original))}");
        }
        Check(FilePatch.Parse(JsonDocument.Parse("[{\"oldStart\":1}]").RootElement) is null && FilePatch.Parse(JsonDocument.Parse("\"x\"").RootElement) is null, "a malformed structuredPatch is none");
        return Task.CompletedTask;
    }

    internal static Task TheSharedCasesHold()
    {
        var fixture = Fixture.Value;
        Check(fixture.GetProperty("context").GetInt32() == FileDiff.Context && fixture.GetProperty("maximumSideBytes").GetInt32() == FileDiff.MaximumSideBytes
            && fixture.GetProperty("maximumSideLines").GetInt32() == FileDiff.MaximumSideLines && fixture.GetProperty("maximumOutputLines").GetInt32() == FileDiff.MaximumOutputLines, "the fixture's caps are the engine's");
        foreach (var item in fixture.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var generate = item.TryGetProperty("generate", out var spec);
            var oldText = generate ? Generated(spec, "old") : item.GetProperty("old").GetString()!;
            var newText = generate ? Generated(spec, "new") : item.GetProperty("new").GetString()!;
            var result = FileDiff.Compute(oldText, newText);
            var expect = item.GetProperty("expect");
            if (expect.ValueKind == JsonValueKind.Null) { Check(result is null, $"{name}: expected no diff"); continue; }
            Check(result is not null, $"{name}: expected a diff");
            Check(result!.Added == expect.GetProperty("added").GetInt32() && result.Removed == expect.GetProperty("removed").GetInt32(), $"{name}: +{result.Added} -{result.Removed}");
            Check(result.Truncated == expect.GetProperty("truncated").GetBoolean() && result.Unchanged == expect.GetProperty("unchanged").GetBoolean(), $"{name}: truncated {result.Truncated}, unchanged {result.Unchanged}");
            if (expect.TryGetProperty("hunks", out var hunks))
            {
                var actual = JsonSerializer.Serialize(result.Hunks, Wire.Json);
                var wanted = JsonSerializer.Serialize(JsonSerializer.Deserialize<List<FileDiffHunk>>(hunks.GetRawText(), Wire.Json), Wire.Json);
                Check(actual == wanted, $"{name}: hunks\n  got  {actual}\n  want {wanted}");
            }
            else
            {
                Check(result.Hunks.Count == expect.GetProperty("hunkCount").GetInt32(), $"{name}: {result.Hunks.Count} hunks");
                Check(result.Hunks.Sum(h => h.Lines.Count) == expect.GetProperty("lineCount").GetInt32(), $"{name}: {result.Hunks.Sum(h => h.Lines.Count)} lines");
                var first = expect.GetProperty("firstHunk"); var hunk = result.Hunks[0];
                Check(hunk.OldStart == first.GetProperty("oldStart").GetInt32() && hunk.OldLines == first.GetProperty("oldLines").GetInt32()
                    && hunk.NewStart == first.GetProperty("newStart").GetInt32() && hunk.NewLines == first.GetProperty("newLines").GetInt32(), $"{name}: first hunk @@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines}");
                var last = result.Hunks[^1].Lines[^1]; var lastWanted = expect.GetProperty("lastLine");
                Check(last.Op == lastWanted.GetProperty("op").GetString() && last.Text == lastWanted.GetProperty("text").GetString(), $"{name}: last line {last.Op}{last.Text}");
                if (expect.TryGetProperty("contextLines", out var context))
                    Check(result.Hunks.Sum(h => h.Lines.Count(l => l.Op == " ")) == context.GetInt32(), $"{name}: {result.Hunks.Sum(h => h.Lines.Count(l => l.Op == " "))} context lines");
            }
        }
        // Beyond the fixture: the caps, and an edit script too long for Myers' trace.
        Check(FileDiff.Compute(new string('a', FileDiff.MaximumSideBytes + 1), "") is null, "a side over 1 MiB has no diff");
        Check(FileDiff.Compute(string.Concat(Enumerable.Repeat("x\n", FileDiff.MaximumSideLines + 1)), "") is null, "a side over 20,000 lines has no diff");
        var many = FileDiff.Compute(string.Concat(Enumerable.Range(0, 3000).Select(n => "a" + n + "\n")), string.Concat(Enumerable.Range(0, 3000).Select(n => "b" + n + "\n")))!;
        Check(many.Removed == 3000 && many.Added == 3000 && many.Truncated && many.Hunks.Sum(h => h.Lines.Count) == FileDiff.MaximumOutputLines && many.Hunks[0].Lines.Take(3000).All(l => l.Op == "-"), "past the edit distance the middle reads as removed then added");
        var cut = FileDiff.Cut(FileDiff.Compute("a\nb\nc\n", "a\nB\nc\n")!.Hunks, 2);
        Check(cut.Single() is { OldStart: 1, OldLines: 2, NewStart: 1, NewLines: 1 } && cut[0].Lines.Count == 2, "a hunk cut short counts only the lines it keeps");
        return Task.CompletedTask;
    }

    private static string Assistant(string id, string tool, string path) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id = "m-" + id, content = new object[] { new { type = "tool_use", id, name = tool, input = new { file_path = path } } } } });
    private static string Result(string id, object? toolUseResult, bool error = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "user", ["message"] = new { content = new object[] { new { type = "tool_result", tool_use_id = id, is_error = error, content = "ok" } } }, ["tool_use_result"] = toolUseResult });

    internal static async Task TheParserKeepsEachFilesFirstOriginalOfTheRun()
    {
        var directory = Temp();
        try
        {
            var root = Path.Combine(directory, "workspace"); Directory.CreateDirectory(root);
            var file = Path.Combine(root, "a.txt"); File.WriteAllText(file, "v3\n");
            var store = new FileOriginalStore(Path.Combine(directory, "originals"));
            var parser = new OutputParser("claude", (_, _) => { }, _ => { }) { FileOriginal = (path, original) => store.Capture("pane", "run-1", root, path, original) };
            parser.Parse(Assistant("t1", "Edit", file));
            parser.Parse(Result("t1", new { filePath = file, originalFile = "v1\n", structuredPatch = Array.Empty<object>() }));
            parser.Parse(Assistant("t2", "Edit", file));
            parser.Parse(Result("t2", new { filePath = file, originalFile = "v2\n" }));
            Check(store.Find("pane", "run-1", "a.txt") is { Text: "v1\n", Created: false }, "the first original of the run is kept; a later edit never replaces it");
            // A Read's echo, a failed edit and a sub-agent's edit of the transcript shape.
            File.WriteAllText(Path.Combine(root, "b.txt"), "x\n"); File.WriteAllText(Path.Combine(root, "c.txt"), "x\n"); File.WriteAllText(Path.Combine(root, "d.txt"), "new\n");
            parser.Parse(Assistant("t3", "Read", Path.Combine(root, "b.txt")));
            parser.Parse(Result("t3", new { filePath = Path.Combine(root, "b.txt"), originalFile = "read\n" }));
            parser.Parse(Assistant("t4", "Edit", Path.Combine(root, "c.txt")));
            parser.Parse(Result("t4", new { filePath = Path.Combine(root, "c.txt"), originalFile = "failed\n" }, error: true));
            parser.Parse(Assistant("t5", "MultiEdit", Path.Combine(root, "d.txt")));
            parser.Parse(JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "user", ["parent_tool_use_id"] = "agent-1", ["message"] = new { content = new object[] { new { type = "tool_result", tool_use_id = "t5", content = "ok" } } }, ["toolUseResult"] = new { filePath = Path.Combine(root, "d.txt"), originalFile = "old\n" } }));
            Check(store.Find("pane", "run-1", "b.txt") is null, "a Read's result is no original");
            Check(store.Find("pane", "run-1", "c.txt") is null, "a failed edit is no original");
            Check(store.Find("pane", "run-1", "d.txt") is { Text: "old\n" }, "a sub-agent's MultiEdit counts, toolUseResult as transcripts write it");
            // A Write that created its file.
            File.WriteAllText(Path.Combine(root, "new.txt"), "one\ntwo\n");
            parser.Parse(Assistant("t6", "Write", Path.Combine(root, "new.txt")));
            parser.Parse(Result("t6", new Dictionary<string, object?> { ["type"] = "create", ["filePath"] = Path.Combine(root, "new.txt"), ["content"] = "one\ntwo\n", ["originalFile"] = null }));
            Check(store.Find("pane", "run-1", "new.txt") is { Text: "", Created: true }, "a created file's original is empty and marked created");
            // What the CLI really sends for most edits: no originalFile, a structuredPatch to undo against the file on disk.
            File.WriteAllText(Path.Combine(root, "e.txt"), "one\r\nTWO\r\nthree\r\n");
            object Patched(string path, object[] hunks, string oldString = "two") => new Dictionary<string, object?> { ["filePath"] = path, ["oldString"] = oldString, ["newString"] = "TWO", ["originalFile"] = null, ["contentNotInModelContext"] = true, ["structuredPatch"] = hunks, ["userModified"] = false, ["replaceAll"] = false };
            var twoToTWO = new object[] { new { oldStart = 1, oldLines = 3, newStart = 1, newLines = 3, lines = new[] { " one", "-two", "+TWO", " three" } } };
            parser.Parse(Assistant("t7", "Edit", Path.Combine(root, "e.txt")));
            parser.Parse(Result("t7", Patched(Path.Combine(root, "e.txt"), twoToTWO)));
            Check(store.Find("pane", "run-1", "e.txt") is { Text: "one\r\ntwo\r\nthree\r\n", Created: false }, "a structuredPatch is undone against the CRLF file on disk");
            File.WriteAllText(Path.Combine(root, "e.txt"), "ONE\r\nTWO\r\nthree\r\n");
            parser.Parse(Assistant("t8", "Edit", Path.Combine(root, "e.txt")));
            parser.Parse(Result("t8", Patched(Path.Combine(root, "e.txt"), [new { oldStart = 1, oldLines = 1, newStart = 1, newLines = 1, lines = new[] { "-one", "+ONE" } }])));
            Check(store.Find("pane", "run-1", "e.txt")?.Text == "one\r\ntwo\r\nthree\r\n", "a later patch of the path that confirms the first original never replaces it");
            // A patch that does not fit the file settles the path: a later edit with the text never poses as its first original.
            File.WriteAllText(Path.Combine(root, "f.txt"), "x\n");
            parser.Parse(Assistant("t9", "Edit", Path.Combine(root, "f.txt")));
            parser.Parse(Result("t9", Patched(Path.Combine(root, "f.txt"), twoToTWO)));
            parser.Parse(Assistant("t10", "Edit", Path.Combine(root, "f.txt")));
            parser.Parse(Result("t10", new { filePath = Path.Combine(root, "f.txt"), originalFile = "later\n" }));
            Check(store.Find("pane", "run-1", "f.txt") is null, "a patch that does not fit is refused, and the path stays refused");
            // A Write that updated a file without its original is unknown, never a created file.
            File.WriteAllText(Path.Combine(root, "g.txt"), "g\n");
            parser.Parse(Assistant("t11", "Write", Path.Combine(root, "g.txt")));
            parser.Parse(Result("t11", new Dictionary<string, object?> { ["type"] = "update", ["filePath"] = Path.Combine(root, "g.txt"), ["content"] = "g\n", ["originalFile"] = null }));
            Check(store.Find("pane", "run-1", "g.txt") is null, "a Write update without its original or a patch is no created file");
            // A Write update with a structuredPatch and no original is undone like an Edit.
            File.WriteAllText(Path.Combine(root, "w.txt"), "w1\nW2\n");
            parser.Parse(Assistant("t13", "Write", Path.Combine(root, "w.txt")));
            parser.Parse(Result("t13", new Dictionary<string, object?> { ["type"] = "update", ["filePath"] = Path.Combine(root, "w.txt"), ["content"] = "w1\nW2\n", ["originalFile"] = null,
                ["structuredPatch"] = new object[] { new { oldStart = 1, oldLines = 2, newStart = 1, newLines = 2, lines = new[] { " w1", "-w2", "+W2" } } } }));
            Check(store.Find("pane", "run-1", "w.txt") is { Text: "w1\nw2\n", Created: false }, "a Write update's structuredPatch is undone against the file on disk");
            // An Edit with an empty oldString and no patch, or one that does more than add, is unknown.
            File.WriteAllText(Path.Combine(root, "i.txt"), "i\n"); File.WriteAllText(Path.Combine(root, "j.txt"), "J\n");
            parser.Parse(Assistant("t14", "Edit", Path.Combine(root, "i.txt")));
            parser.Parse(Result("t14", new Dictionary<string, object?> { ["filePath"] = Path.Combine(root, "i.txt"), ["oldString"] = "", ["newString"] = "i", ["originalFile"] = null }));
            parser.Parse(Assistant("t15", "Edit", Path.Combine(root, "j.txt")));
            parser.Parse(Result("t15", Patched(Path.Combine(root, "j.txt"), [new { oldStart = 1, oldLines = 1, newStart = 1, newLines = 1, lines = new[] { "-j", "+J" } }], oldString: "")));
            Check(store.Find("pane", "run-1", "i.txt") is null && store.Find("pane", "run-1", "j.txt") is null, "an empty oldString without a patch that only adds is no created file");
            // An Edit with an empty oldString made its file.
            File.WriteAllText(Path.Combine(root, "h.txt"), "made\n");
            parser.Parse(Assistant("t12", "Edit", Path.Combine(root, "h.txt")));
            parser.Parse(Result("t12", Patched(Path.Combine(root, "h.txt"), [new { oldStart = 0, oldLines = 0, newStart = 1, newLines = 2, lines = new[] { "+made", "+more", "\\ No newline at end of file" } }], oldString: "")));
            Check(store.Find("pane", "run-1", "h.txt") is { Text: "", Created: true }, "an Edit with an empty oldString made its file");
            // Refusals are kept with the run: a restart does not forget them.
            Check(!new FileOriginalStore(Path.Combine(directory, "originals")).Capture("pane", "run-1", root, Path.Combine(root, "f.txt"), "again\n"), "a refused path stays refused after a restart");
            var created = await FileChanges.ReadAsync(store, root, "new.txt", "pane");
            Check(created is { Base: "run", RunId: "run-1", Created: true, Added: 2, Removed: 0 } && created.Hunks.Single().Lines.All(l => l.Op == "+"), "a created file reads as all added against its run");
            var changed = await FileChanges.ReadAsync(store, root, "a.txt", "pane", "run-1");
            Check(changed is { Base: "run", Added: 1, Removed: 1 } && changed.Hunks[0].Lines.Select(l => l.Op + l.Text).SequenceEqual(["-v1", "+v3"]), "the diff is the run's first original against the file on disk");
            var wire = Json(changed!);
            Check(wire.GetProperty("base").GetString() == "run" && wire.GetProperty("runId").GetString() == "run-1" && !wire.TryGetProperty("headSha", out _) && wire.GetProperty("hunks")[0].GetProperty("lines")[0].GetProperty("op").GetString() == "-", "the change set's wire shape");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// The CLI may write a file's next edit before the result of the one before is read: the patch undone then keeps the next
    /// edit in. Whatever order the results come in, the original kept is the file before the run's first edit, or none.
    /// </summary>
    internal static Task ALaterEditAlreadyOnDiskNeverLeavesAWrongOriginal()
    {
        var directory = Temp();
        try
        {
            var root = Path.Combine(directory, "workspace"); Directory.CreateDirectory(root);
            var store = new FileOriginalStore(Path.Combine(directory, "originals"));
            var file = Path.Combine(root, "a.txt");
            const string original = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\n";
            // Edit 1 changes line 2; edit 2 changes line 11 (below) or line 2 again after edit 1 changed line 11 (above).
            FileEdit Edit(int oldStart, int newStart, string[] lines) => new(null, false, [new(oldStart, lines.Count(l => l[0] != '+'), newStart, lines.Count(l => l[0] != '-'), lines)]);
            var line2 = Edit(1, 1, [" l1", "-l2", "+L2", " l3", " l4", " l5"]);
            var line11 = Edit(8, 8, [" l8", " l9", " l10", "-l11", "+L11", " l12"]);
            var line11Twice = Edit(8, 8, [" l8", " l9", " l10", "-l11", "+L11a", "+L11b", " l12"]);
            var line2Again = Edit(1, 1, [" l1", "-L2", "+M2", " l3", " l4", " l5"]);

            // In order: each result read before the next edit is written.
            File.WriteAllText(file, original.Replace("l2\n", "L2\n"));
            store.Capture("pane", "in-order", root, file, line2);
            File.WriteAllText(file, original.Replace("l2\n", "L2\n").Replace("l11\n", "L11\n"));
            store.Capture("pane", "in-order", root, file, line11);
            Check(store.Find("pane", "in-order", "a.txt")?.Text == original, "edits read in order keep the first original");

            // Below: edit 2 (line 11) is on disk when edit 1's result is read.
            File.WriteAllText(file, original.Replace("l2\n", "L2\n").Replace("l11\n", "L11\n"));
            store.Capture("pane", "below", root, file, line2);
            store.Capture("pane", "below", root, file, line11);
            Check(store.Find("pane", "below", "a.txt") is not { } below || below.Text == original, "a later edit below, already on disk, never leaves a wrong original");
            Check(store.Find("pane", "below", "a.txt") is null, "the original that took the later edit in is dropped");

            // Above: edit 1 changed line 11 into two lines, edit 2 changes line 2 again; both on disk when edit 1's result is read.
            File.WriteAllText(file, original.Replace("l2\n", "M2\n").Replace("l11\n", "L11a\nL11b\n"));
            store.Capture("pane", "above", root, file, line11Twice);
            store.Capture("pane", "above", root, file, Edit(1, 1, [" l1", "-l2", "+M2", " l3", " l4", " l5"]));
            Check(store.Find("pane", "above", "a.txt") is not { } above || above.Text == original, "a later edit above, already on disk, never leaves a wrong original");

            // Above, with the lines shifted: edit 2 inserted a line above edit 1's hunk before edit 1's result is read.
            File.WriteAllText(file, ("l0\n" + original).Replace("l11\n", "L11\n"));
            store.Capture("pane", "shifted", root, file, line11);
            Check(store.Find("pane", "shifted", "a.txt") is null, "a patch whose lines moved is refused at once");

            // A later edit given with its originalFile confirms (or not) the file the run's last edit left.
            File.WriteAllText(file, original.Replace("l2\n", "L2\n").Replace("l11\n", "L11\n"));
            store.Capture("pane", "with-text", root, file, line2);
            store.Capture("pane", "with-text", root, file, line11 with { Original = original.Replace("l2\n", "L2\n") });
            Check(store.Find("pane", "with-text", "a.txt") is null, "an originalFile that is not the file the last edit left drops an original rebuilt from a patch");
            File.WriteAllText(file, original.Replace("l2\n", "L2\n"));
            store.Capture("pane", "text-ok", root, file, line2);
            File.WriteAllText(file, original.Replace("l2\n", "M2\n"));
            store.Capture("pane", "text-ok", root, file, line2Again with { Original = original.Replace("l2\n", "L2\n") });
            File.WriteAllText(file, original.Replace("l2\n", "N2\n"));
            store.Capture("pane", "text-ok", root, file, Edit(1, 1, [" l1", "-M2", "+N2", " l3", " l4", " l5"]));
            Check(store.Find("pane", "text-ok", "a.txt")?.Text == original, "edits confirmed by their originalFile and their patches keep the first original");

            // A later edit that cannot be confirmed (no patch) drops it too, and the path stays refused; the blob number it
            // frees is never taken over a kept original's.
            File.WriteAllText(file, original.Replace("l2\n", "L2\n"));
            var second = Path.Combine(root, "b.txt"); File.WriteAllText(second, "b\n");
            var third = Path.Combine(root, "c.txt"); File.WriteAllText(third, "c\n");
            Check(store.Capture("pane", "unknown", root, file, line2) && store.Capture("pane", "unknown", root, second, "b-kept\n"), "two originals kept");
            store.Capture("pane", "unknown", root, file, FileEdit.Unknown);
            Check(store.Find("pane", "unknown", "a.txt") is null && !store.Capture("pane", "unknown", root, file, line2), "an unconfirmed later edit leaves the path refused");
            Check(store.Capture("pane", "unknown", root, third, "c-kept\n") && store.Find("pane", "unknown", "b.txt")?.Text == "b-kept\n" && store.Find("pane", "unknown", "c.txt")?.Text == "c-kept\n", "each kept original keeps its own blob");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task TheStoreKeepsItsCapsAndSurvivesARestart()
    {
        var directory = Temp();
        try
        {
            var root = Path.Combine(directory, "workspace"); Directory.CreateDirectory(root);
            var originals = Path.Combine(directory, "originals"); var store = new FileOriginalStore(originals);
            string Touch(string name) { var path = Path.Combine(root, name); File.WriteAllText(path, "now\n"); return path; }
            Check(!store.Capture("pane", "run-1", root, Touch("big.txt"), new string('x', FileOriginalStore.MaximumOriginalBytes + 1)), "an original over 1 MiB is refused");
            Check(!store.Capture("pane", "run-1", root, Touch("big.txt"), "small\n"), "a path refused once in a run stays refused, so a later original never poses as the first");
            for (var n = 0; n < FileOriginalStore.MaximumFilesPerRun; n++) Check(store.Capture("pane", "run-2", root, Touch($"f{n}.txt"), "o\n"), $"file {n} of the run is kept");
            Check(!store.Capture("pane", "run-2", root, Touch("f64.txt"), "o\n"), "the 65th file of a run is refused");
            var megabyte = new string('m', FileOriginalStore.MaximumOriginalBytes);
            for (var n = 0; n < 16; n++) Check(store.Capture("pane", "run-3", root, Touch($"m{n}.txt"), megabyte), $"megabyte {n} of the run is kept");
            Check(!store.Capture("pane", "run-3", root, Touch("m16.txt"), "o\n"), "a run past 16 MiB keeps nothing more");
            Check(store.Capture("other", "run-1", root, Touch("a.txt"), "o\n") && store.Capture("pane", "run-4", root, Touch("a.txt"), "first\n") && store.Capture("pane", "run-5", root, Touch("a.txt"), "second\n"), "runs of panes keep their own");
            // Reopened as after a restart: the newest run that captured the path wins when none is named.
            var reopened = new FileOriginalStore(originals);
            Check(reopened.Find("pane", null, "a.txt") is { RunId: "run-5", Text: "second\n" } && reopened.Find("pane", "run-4", "a.txt") is { Text: "first\n" }, "the originals survive a restart and the newest run answers");
            Check(reopened.Find("pane", "run-9", "a.txt") is null && reopened.Find("nobody", null, "a.txt") is null, "an unknown run or pane has none");
            Check(Directory.EnumerateFiles(originals, "*.tmp", SearchOption.AllDirectories).FirstOrDefault() is null, "no temporary file is left behind");
            reopened.Retain("pane", new HashSet<string> { "run-4" });
            Check(reopened.Runs("pane").SequenceEqual(["run-4"]), "runs the graph let go are dropped");
            reopened.Forget("other");
            Check(reopened.Find("other", null, "a.txt") is null, "a closed pane's originals go");
            reopened.Capture("gone", "run-1", root, Touch("a.txt"), "o\n");
            reopened.Prune(new Dictionary<string, IReadOnlySet<string>> { ["pane"] = new HashSet<string> { "run-4", "run-7" } });
            Check(reopened.Find("gone", null, "a.txt") is null && reopened.Find("pane", "run-4", "a.txt") is not null, "a start keeps only the panes and runs the state names");
            // Paths outside the workspace never reach the store.
            var outside = Path.Combine(directory, "outside.txt"); File.WriteAllText(outside, "x");
            Check(!reopened.Capture("pane", "run-6", root, outside, "x") && !reopened.Capture("pane", "run-6", root, "../outside.txt", "x") && !reopened.Capture("pane", "run-6", root, Path.Combine(root, "missing.txt"), "x"), "a path outside the workspace, or no file, is refused");
            Check(!reopened.Capture("../pane", "run-6", root, Touch("a.txt"), "x"), "a malformed pane id is refused");
            // An original is read only under the folder it was captured in (a delegated child's worktree is another folder).
            var worktree = Path.Combine(directory, "worktree"); Directory.CreateDirectory(worktree); File.WriteAllText(Path.Combine(worktree, "a.txt"), "x\n");
            Check(reopened.Find("pane", "run-4", "a.txt", root) is not null && reopened.Find("pane", "run-4", "a.txt", worktree) is null, "an original captured under another root never counts");
            File.WriteAllText(Path.Combine(worktree, "b.txt"), "x\n");
            Check(!reopened.Capture("pane", "run-4", worktree, Path.Combine(worktree, "b.txt"), "x"), "a run never mixes roots");
            // A notice run's id stands for the run it came from.
            Check(reopened.Find("pane", BackgroundNoticeRuns.RunID("run-4", 1), "a.txt")?.RunId == "run-4", "a notice run reads its source run's originals");
            reopened.Retain("pane", new HashSet<string> { BackgroundNoticeRuns.RunID("run-4", 2) });
            Check(reopened.Runs("pane").SequenceEqual(["run-4"]), "a kept notice run keeps the run it came from");
            // Each pane keeps the originals of its newest 20 runs.
            for (var n = 0; n < FileOriginalStore.MaximumRunsPerPane + 2; n++) Check(reopened.Capture("many", $"r{n}", root, Touch("a.txt"), "o\n"), $"run {n} kept");
            var many = reopened.Runs("many");
            Check(many.Count == FileOriginalStore.MaximumRunsPerPane && many[0] == $"r{FileOriginalStore.MaximumRunsPerPane + 1}" && !many.Contains("r0") && !many.Contains("r1"), "a pane keeps its newest 20 runs");
            // The whole store keeps its budget, the oldest runs going first.
            var small = new FileOriginalStore(Path.Combine(directory, "small"), budget: 3 * 1_000);
            for (var n = 0; n < 5; n++) Check(small.Capture($"p{n}", "run", root, Touch("a.txt"), new string('z', 1_000)), $"pane {n} kept");
            Check(small.Runs("p0").Count == 0 && small.Runs("p1").Count == 0 && small.Runs("p4").Count == 1 && small.Runs("p2").Count == 1, "past the budget the oldest runs go");
            // A crash's temporary files go at the next start.
            var stale = Path.Combine(originals, "x.tmp"); File.WriteAllText(stale, "x");
            reopened.Prune(new Dictionary<string, IReadOnlySet<string>> { ["pane"] = new HashSet<string> { "run-4" } });
            Check(!File.Exists(stale) && Directory.EnumerateFiles(originals, "*.tmp", SearchOption.AllDirectories).FirstOrDefault() is null, "a start removes stale temporary files");
            Check(FileOriginalStore.KeptRuns([new RunSession { Id = "twice" }, new RunSession { Id = "twice" }]).ContainsKey("twice"), "a pane id that appears twice does not stop the start");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static void Git(string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', arguments) + ": " + error);
    }

    internal static async Task WithoutARunOriginalGitsHeadIsTheBase()
    {
        var directory = Temp();
        try
        {
            var repository = Path.Combine(directory, "repo"); var root = Path.Combine(repository, "app"); Directory.CreateDirectory(root);
            Git(repository, "init", "-q");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "one\ntwo\nthree\n");
            File.WriteAllBytes(Path.Combine(root, "binary.dat"), [1, 0, 2]);
            Git(repository, "add", "."); Git(repository, "commit", "-q", "-m", "fixture");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "one\r\nTWO\r\nthree\r\n");
            File.WriteAllText(Path.Combine(root, "untracked.txt"), "a\nb\n");
            File.WriteAllBytes(Path.Combine(root, "binary.dat"), [1, 0, 3]);
            var store = new FileOriginalStore(Path.Combine(directory, "originals"));
            var head = await FileChanges.ReadAsync(store, root, "tracked.txt", "pane");
            Check(head is { Base: "head", Added: 1, Removed: 1, Created: null } && head.HeadSha is { Length: 7 } && head.Hunks[0].Lines.Select(l => l.Op + l.Text).SequenceEqual([" one", "-two", "+TWO", " three"]), "a workspace in a subfolder of a repository diffs against HEAD, CRLF read as LF");
            if (OperatingSystem.IsWindows())
                Check(await FileChanges.ReadAsync(store, root, "TRACKED.txt") is { Base: "head", Created: null, Added: 1 }, "a path in another case reads HEAD's file by the disk's spelling");
            Check(GitHeadFile.NotInHead("fatal: path 'app/a.txt' does not exist in 'HEAD'") && GitHeadFile.NotInHead("fatal: path 'a.txt' exists on disk, but not in 'HEAD'")
                && !GitHeadFile.NotInHead("fatal: not a valid object name") && !GitHeadFile.NotInHead(""), "only git's not-in-HEAD words make a file untracked");
            var untracked = await FileChanges.ReadAsync(store, root, "untracked.txt");
            Check(untracked is { Base: "head", Created: true, Added: 2, Removed: 0 }, "an untracked file reads as all added");
            Check(await FileChanges.ReadAsync(store, root, "binary.dat") is null, "a binary file has no diff");
            Check(await FileChanges.ReadAsync(store, root, "../outside.txt") is null && await FileChanges.ReadAsync(store, root, "missing.txt") is null, "a path outside the workspace or no file has no diff");
            // A run's original wins over HEAD, and a named run that captured nothing falls back to HEAD.
            store.Capture("pane", "run-1", root, Path.Combine(root, "tracked.txt"), "one\nthree\n");
            Check(await FileChanges.ReadAsync(store, root, "tracked.txt", "pane") is { Base: "run", Added: 1, Removed: 0 }, "the run's original wins over HEAD");
            Check(await FileChanges.ReadAsync(store, root, "tracked.txt", "pane", "run-2") is { Base: "head" }, "a run that captured nothing falls back to HEAD");
            var unborn = Path.Combine(directory, "unborn"); Directory.CreateDirectory(unborn); Git(unborn, "init", "-q");
            File.WriteAllText(Path.Combine(unborn, "a.txt"), "x\n");
            Check(await FileChanges.ReadAsync(store, unborn, "a.txt") is { Base: "head", HeadSha: null, Created: true, Added: 1 }, "a repository with no commit yet reads as all added");
            var plain = Path.Combine(directory, "plain"); Directory.CreateDirectory(plain); File.WriteAllText(Path.Combine(plain, "a.txt"), "x\n");
            // The temporary folder is in no repository on a usual machine; one under a checkout cannot show this.
            if (!InsideRepository(plain)) Check(await FileChanges.ReadAsync(store, plain, "a.txt") is null && await GitHeadFile.ReadAsync(plain, "a.txt") is null, "a folder in no repository has no diff");
        }
        finally { ForceDelete(directory); }
    }

    /// <summary>Whether git sees a work tree at or above the folder (a temporary folder can sit under a checkout).</summary>
    private static bool InsideRepository(string folder)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("rev-parse"); info.ArgumentList.Add("--is-inside-work-tree");
        using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        return process.ExitCode == 0 && output.Trim() == "true";
    }

    /// <summary>git marks its objects read-only on Windows.</summary>
    private static void ForceDelete(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }

    internal static async Task ThePhoneAsksForChangesOnlyWithTheCapabilitysArguments()
    {
        var directory = Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            File.WriteAllText(Path.Combine(workspacePath, "a.txt"), "one\nTWO\n");
            File.WriteAllBytes(Path.Combine(workspacePath, "image.png"), [0x89, 0x50, 0x4E, 0x47]);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(workspacePath);
            var other = await service.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(directory, "other")).FullName);
            var worktree = Directory.CreateDirectory(Path.Combine(directory, "worktree")).FullName; File.WriteAllText(Path.Combine(worktree, "a.txt"), "child\nNOW\n");
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "pane", WorkspaceId = workspace.Id, GraphRuns = [new MightyGraphRun { Id = "entry-1", SourceRunID = "run-1", Status = "completed" }] }, new RunSession { Id = "elsewhere", WorkspaceId = other.Id },
                new RunSession { Id = "child", WorkspaceId = workspace.Id, ParentSessionId = "pane", WorkingFolder = worktree }] });
            service.FileOriginals.Capture("child", "run-c", worktree, Path.Combine(worktree, "a.txt"), "child\nthen\n");
            Check(service.RunFolder("child") == worktree && service.RunFolder("pane") == workspacePath, "a child's runs work in its worktree");
            service.FileOriginals.Capture("pane", "run-1", workspacePath, Path.Combine(workspacePath, "a.txt"), "one\ntwo\n");
            service.FileOriginals.Capture("elsewhere", "run-1", workspacePath, Path.Combine(workspacePath, "a.txt"), "x\n");
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([]));
            async Task<(int Status, JsonElement Body)> Route(string path) { var reply = await router.RouteAsync("GET", path, null, "phone", CancellationToken.None); return (reply.Status, Json(reply.Body)); }
            var info = await Route("/m1/info");
            Check(info.Body.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "fileChanges"), "the host lists fileChanges");
            var plain = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt");
            Check(plain.Status == 200 && !plain.Body.TryGetProperty("changes", out _), "without changes=1 the preview is as before");
            var run = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&session=pane&run=run-1");
            Check(run.Status == 200 && run.Body.GetProperty("changes").GetProperty("base").GetString() == "run" && run.Body.GetProperty("changes").GetProperty("added").GetInt32() == 1
                && run.Body.GetProperty("text").GetString() == "one\nTWO\n", "changes=1 with the pane and run carries the run's diff beside the text");
            var byEntry = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&session=pane&run=entry-1");
            Check(byEntry.Body.GetProperty("changes").GetProperty("runId").GetString() == "run-1", "a graph run's id reads the originals of the run it stands for (its sourceRunID)");
            var newest = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&session=pane");
            Check(newest.Body.GetProperty("changes").GetProperty("runId").GetString() == "run-1", "without a run the pane's newest run answers");
            var child = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&session=child");
            Check(child.Body.GetProperty("changes").GetProperty("runId").GetString() == "run-c"
                && child.Body.GetProperty("changes").GetProperty("hunks")[0].GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("op").GetString() + l.GetProperty("text").GetString()).SequenceEqual([" child", "-then", "+NOW"]),
                "a delegated child's changes are read in its worktree, where they were captured");
            Check(child.Body.GetProperty("text").GetString() == "child\nNOW\n", "a delegated child's full view reads the same worktree file as its changes");
            var foreign = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&session=elsewhere");
            Check(foreign.Status == 200 && !foreign.Body.TryGetProperty("changes", out _), "a pane of another workspace lends no original (and this folder is no repository)");
            var image = await Route($"/m1/workspaces/{workspace.Id}/file?path=image.png&changes=1&session=pane");
            Check(image.Status == 200 && !image.Body.TryGetProperty("changes", out _), "only text carries changes");
            Check((await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&session=pane")).Status == 400, "session without changes=1 is refused");
            Check((await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=yes")).Status == 400, "changes is 1 or absent");
            Check((await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&run=..%2Fx")).Status == 400, "a malformed run id is refused");
            Check((await Route($"/m1/workspaces/{workspace.Id}/files?path=&changes=1")).Status == 400, "the folder listing takes no changes");
            Check((await Route($"/m1/workspaces/{workspace.Id}/file?path=..%2Fa.txt&changes=1&session=pane")).Status == 403, "the file route's path checks hold with changes");
            // A reply over the frame cuts the changes with the text.
            var huge = MobileWorkspaceFiles.Fitted(new MobileFilePreview(1, "w", "a", "a", 1, null, "source") { Text = new string('x', 900_000), Changes = new("head", FileDiff.Compute("", string.Concat(Enumerable.Range(0, 4000).Select(n => new string('y', 200) + n + "\n")))!.Hunks, 4000, 0, false, false) });
            Check(JsonSerializer.SerializeToUtf8Bytes(huge, Wire.Json).Length <= MobileWorkspaceFiles.MaximumReplyBytes && huge.Changes!.Truncated && huge.Truncated == true, "a reply over the frame cuts the text and the changes, both marked");
        }
        finally { Directory.Delete(directory, true); }
    }
}
