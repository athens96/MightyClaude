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
        var count = spec.GetProperty(side).GetInt32(); var pattern = spec.GetProperty("line").GetString()!;
        return string.Concat(Enumerable.Range(1, count).Select(n => pattern.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\n"));
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
            var created = await FileChanges.ReadAsync(store, root, "new.txt", "pane");
            Check(created is { Base: "run", RunId: "run-1", Created: true, Added: 2, Removed: 0 } && created.Hunks.Single().Lines.All(l => l.Op == "+"), "a created file reads as all added against its run");
            var changed = await FileChanges.ReadAsync(store, root, "a.txt", "pane", "run-1");
            Check(changed is { Base: "run", Added: 1, Removed: 1 } && changed.Hunks[0].Lines.Select(l => l.Op + l.Text).SequenceEqual(["-v1", "+v3"]), "the diff is the run's first original against the file on disk");
            var wire = Json(changed!);
            Check(wire.GetProperty("base").GetString() == "run" && wire.GetProperty("runId").GetString() == "run-1" && !wire.TryGetProperty("headSha", out _) && wire.GetProperty("hunks")[0].GetProperty("lines")[0].GetProperty("op").GetString() == "-", "the change set's wire shape");
        }
        finally { Directory.Delete(directory, true); }
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
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "pane", WorkspaceId = workspace.Id }, new RunSession { Id = "elsewhere", WorkspaceId = other.Id }] });
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
            var newest = await Route($"/m1/workspaces/{workspace.Id}/file?path=a.txt&changes=1&session=pane");
            Check(newest.Body.GetProperty("changes").GetProperty("runId").GetString() == "run-1", "without a run the pane's newest run answers");
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
