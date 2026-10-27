using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/background-output.json: how a host turns the end of a background run's output file into the lines it
// shows. The Mac (BackgroundOutputTailTests) and the phone (jest) read the same file. Also the Windows side of what the lines and a
// run's notice reply reach: the phone's blocks and structure nodes (never the output file), the strip's kind words and last line,
// and the kind words themselves.
internal static class BackgroundOutputVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.BackgroundOutput.json";
        using var stream = typeof(BackgroundOutputVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    private static string Shown(IEnumerable<string> lines) => "[" + string.Join(" | ", lines) + "]";

    internal static Task TheConstantsAreTheFixtures()
    {
        Check(Fixture.Value.GetProperty("maxBytes").GetInt32() == BackgroundOutputTail.MaxBytes, "maxBytes");
        Check(Fixture.Value.GetProperty("maxLines").GetInt32() == BackgroundOutputTail.MaxLines, "maxLines");
        Check(Fixture.Value.GetProperty("maxLineChars").GetInt32() == BackgroundOutputTail.MaxLineChars, "maxLineChars");
        return Task.CompletedTask;
    }

    internal static Task EveryCaseHolds()
    {
        var cases = Fixture.Value.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Count >= 10, "the fixture has every case; got " + cases.Count);
        foreach (var entry in cases)
        {
            var name = entry.GetProperty("name").GetString()!;
            var data = Convert.FromBase64String(entry.GetProperty("base64").GetString()!);
            var expected = entry.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToList();
            var lines = BackgroundOutputTail.Lines(data, entry.GetProperty("truncated").GetBoolean());
            Check(lines.SequenceEqual(expected), $"{name}: expected {Shown(expected)}, got {Shown(lines)}");
        }
        return Task.CompletedTask;
    }

    internal static Task ReadTakesTheEndOfAFileAnotherProcessWrites()
    {
        var root = Verification.Temp();
        try
        {
            Check(BackgroundOutputTail.Read(Path.Combine(root, "missing.output")) is null, "a missing file reads as none");
            var shortFile = Path.Combine(root, "short.output");
            File.WriteAllText(shortFile, "one\ntwo\n");
            Check(BackgroundOutputTail.Read(shortFile) is { } read && read.SequenceEqual(new[] { "one", "two" }), "a short file reads whole");
            // Longer than maxBytes: the read starts inside a line, which is dropped.
            var longFile = Path.Combine(root, "long.output");
            var text = new StringBuilder();
            for (var index = 0; text.Length <= BackgroundOutputTail.MaxBytes + 100; index++) text.Append("line ").Append(index).Append('\n');
            File.WriteAllText(longFile, text.ToString());
            var all = text.ToString().TrimEnd('\n').Split('\n');
            var tail = BackgroundOutputTail.Read(longFile);
            Check(tail is not null && tail.SequenceEqual(all.TakeLast(BackgroundOutputTail.MaxLines)), "a long file reads its last lines; got " + Shown(tail ?? []));
            // The run still holds the file open for writing.
            var open = Path.Combine(root, "open.output");
            using (var writer = new FileStream(open, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                writer.Write(Encoding.UTF8.GetBytes("building\r50%\r100%\n"));
                writer.Flush();
                Check(BackgroundOutputTail.Read(open) is { } progress && progress.SequenceEqual(new[] { "100%" }), "a file still being written reads, its overwritten line as its last segment");
            }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static MightyGraphRun RunWith(params MightyGraphAgent[] agents) => new() { Id = "run-1", Input = "Build it", Status = "completed", Provider = "claude", Agents = [.. agents] };

    internal static Task ThePhoneGetsTheLinesAndTheReplyButNeverTheFile()
    {
        const string file = "/tmp/example/tasks/a1b2c3.output";
        var task = new MightyGraphAgent { Id = "graph-task", Title = "Build", Input = "make all", Status = "completed", Kind = "task", Background = true, OutputFile = file, NoticeReply = "The build passed." };
        var agent = new MightyGraphAgent { Id = "graph-agent", Title = "Survey", Input = "Survey the docs", Status = "running", Background = true };
        var foreground = new MightyGraphAgent { Id = "graph-front", Title = "Look", Input = "Look", Status = "completed" };
        var lines = Enumerable.Range(1, 8).Select(index => "step " + index).ToList();
        var tails = new Dictionary<string, IReadOnlyList<string>> { ["graph-task"] = lines, ["graph-agent"] = ["not a run"] };
        var blocks = MobileMightySupport.Blocks(RunWith(task, agent, foreground), 1, tails);
        var run = blocks.Single(b => b.Id == "graph-task");
        Check(run.Background == true && run.NoticeReply == "The build passed.", "a task block carries background and its notice reply");
        Check(run.OutputTail is { } tail && tail.SequenceEqual(lines.TakeLast(BackgroundOutputTail.MaxLines)), "a task block carries at most the last six lines");
        var sub = blocks.Single(b => b.Id == "graph-agent");
        Check(sub.Background == true && sub.OutputTail is null && sub.NoticeReply is null, "a background sub-agent says so, and carries no lines");
        Check(blocks.Single(b => b.Id == "graph-front") is { Background: null, OutputTail: null, NoticeReply: null }, "a foreground agent carries none of it");
        Check(MobileMightySupport.Blocks(RunWith(task), 1).Single(b => b.Id == "graph-task").OutputTail is null, "no lines read: no outputTail");
        var json = JsonSerializer.Serialize(MobileMightySupport.Runs([RunWith(task, agent, foreground)], outputTails: tails), Wire.Json);
        Check(!json.Contains("outputFile", StringComparison.Ordinal) && !json.Contains(file, StringComparison.Ordinal), "the output file never reaches the phone: " + json);
        Check(json.Contains("\"outputTail\"", StringComparison.Ordinal) && json.Contains("\"noticeReply\"", StringComparison.Ordinal) && json.Contains("\"background\":true", StringComparison.Ordinal), "the wire names the fields: " + json);

        // The structure node the phone draws carries the same, and no file either.
        var item = MobileStructure.ItemFor(MightyStructure.ItemFor(RunWith(task, agent), 0), null, null, tails);
        var nodes = item.Clusters.SelectMany(cluster => cluster.Nodes).ToList();
        var node = nodes.Single(n => n.Id == "graph-task");
        Check(node.Background == true && node.NoticeReply == "The build passed." && node.OutputTail is { Count: BackgroundOutputTail.MaxLines }, "a task node carries background, its lines and its reply");
        Check(nodes.Single(n => n.Id == "graph-agent") is { Background: true, OutputTail: null }, "a background sub-agent node says so, without lines");
        var structure = JsonSerializer.Serialize(item, Wire.Json);
        Check(!structure.Contains(file, StringComparison.Ordinal), "the output file never reaches the structure: " + structure);
        return Task.CompletedTask;
    }

    internal static Task TheKindWordsAndTheStripFollowTheBlock()
    {
        Check(MightyGraphSupport.BackgroundLabel(true, "running") == Locale.Get("graph.background.runWaiting"), "a running run waits on its notice");
        Check(MightyGraphSupport.BackgroundLabel(false, "running") == Locale.Get("graph.background.agentWaiting"), "a running sub-agent waits for its reply");
        Check(MightyGraphSupport.BackgroundLabel(true, "completed") == Locale.Get("graph.background.runEnded", new Dictionary<string, string> { ["status"] = Locale.Get("graph.block.taskNotice.completed") }), "an ended run says how");
        Check(MightyGraphSupport.BackgroundLabel(false, "failed") == Locale.Get("graph.background.agentEnded", new Dictionary<string, string> { ["status"] = Locale.Get("graph.block.taskNotice.failed") }), "a failed sub-agent says so");
        Check(MightyGraphSupport.BackgroundLabel(true, "unknown") == Locale.Get("graph.background.runEnded", new Dictionary<string, string> { ["status"] = Locale.Get("styles.state.taskStatus.unknown") }), "an unknown end says unknown");
        Check(MightyGraphSupport.BackgroundLabel(new MightyGraphAgent { Id = "s", Kind = "steer", Status = "completed" }) is null, "a steer is no background block");
        Check(MightyGraphSupport.BackgroundLabel(new MightyGraphAgent { Id = "f", Status = "running" }) is null, "a foreground agent is none either");

        // A request whose turn ended waits on a background sub-agent as on a task.
        var waiting = RunWith(new MightyGraphAgent { Id = "a", Status = "running", Background = true }, new MightyGraphAgent { Id = "t", Kind = "task", Status = "running" }, new MightyGraphAgent { Id = "f", Status = "running" });
        Check(MightyGraphSupport.BackgroundWaiting(waiting) == 2, "backgroundWaiting counts background agents and tasks, not a foreground agent");

        // The strip's rows meet the latest request's blocks by tool use id.
        const string runId = "run-1";
        var shellBlock = new MightyGraphAgent { Id = ExecutionGraphSupport.AgentNodeID(runId, "toolu_shell"), Kind = "task", Status = "running", Background = true, OutputFile = "/tmp/example/x.output" };
        var agentBlock = new MightyGraphAgent { Id = ExecutionGraphSupport.AgentNodeID(runId, "toolu_agent"), Status = "running", Background = true };
        var run = new MightyGraphRun { Id = runId, SourceRunID = runId, Status = "completed", Agents = [shellBlock, agentBlock] };
        var work = new BackgroundWork([
            new BackgroundTask("b1", "shell", "make all", "2026-10-27T00:00:00Z", ToolUseId: "toolu_shell"),
            new BackgroundTask("b2", "agent", "Survey", "2026-10-27T00:00:01Z", ToolUseId: "toolu_agent"),
            new BackgroundTask("b3", "shell", "npm test", "2026-10-27T00:00:02Z"),
            new BackgroundTask("b4", "other", "watch", "2026-10-27T00:00:03Z"),
        ], TurnEnded: true);
        var output = new Dictionary<string, IReadOnlyList<string>> { [shellBlock.Id] = ["compiling", "linking"] };
        var rows = PlanCardSupport.BackgroundStripRows(work, run, output);
        Check(rows.Count == 4, "one row per task");
        Check(rows[0] is { KindText: { } runWords, Tail: "linking" } && runWords == Locale.Get("graph.background.runWaiting"), "a run's row takes its block's words and its last line");
        Check(rows[1] is { KindText: { } agentWords, Tail: null } && agentWords == Locale.Get("graph.background.agentWaiting"), "a sub-agent's row takes its block's words, no line");
        Check(rows[2] is { KindText: { } shellWords, Tail: null } && shellWords == Locale.Get("graph.background.runWaiting"), "a shell task without a block reads as a run by its kind");
        Check(rows[3] is { KindText: null, Tail: null }, "another kind keeps the task list's words");
        Check(rows[3].Detail(DateTimeOffset.UtcNow).Contains(rows[3].Task.KindTitle, StringComparison.Ordinal), "and its detail says them");
        Check(PlanCardSupport.BackgroundStripRows(null, run).Count == 0, "no work, no rows");
        return Task.CompletedTask;
    }
}
