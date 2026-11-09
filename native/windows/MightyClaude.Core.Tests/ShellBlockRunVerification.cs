using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

// A transcript's shell block run (ShellBlockRuns.cs; the page's half is native/windows/web/src/conversation/shellBlocks.test.ts):
// its key, its record's states, the saved field and its restore, the output's cleaning and limit, and the host's check that the
// command is the one the reply shows.
internal static class ShellBlockRunVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task TheKeyIsTheEntryAndTheBlocksPlace()
    {
        Check(ShellBlockRunSupport.EntryId("entry-1#0") == "entry-1" && ShellBlockRunSupport.EntryId("a#b#12") is null, "entry id and index");
        Check(ShellBlockRunSupport.EntryId("e#3") == "e", "a later block");
        foreach (var bad in new[] { null, "", "#0", "e#", "e#x", "e#-1", "e#12345", "../e#0", "e 1#0" }) Check(!ShellBlockRunSupport.IsKey(bad), "refused: " + bad);
        Check(ShellBlockRunSupport.IsCommand("echo hi") && !ShellBlockRunSupport.IsCommand("  \n") && !ShellBlockRunSupport.IsCommand("a\0b") && !ShellBlockRunSupport.IsCommand(new string('x', 65_537)), "commands");
        return Task.CompletedTask;
    }

    internal static Task TheRecordMovesFromRunningToItsEnd()
    {
        var run = ShellBlockRunSupport.Started("e#0", "ls", "t0");
        Check(run is { State: "running", ExitCode: null, Output: "", EndedAt: null, StartedAt: "t0" }, "started");
        Check(ShellBlockRunSupport.Finished(run, 0, false, "t1") is { State: "completed", ExitCode: 0, EndedAt: "t1" }, "exit 0 completes");
        Check(ShellBlockRunSupport.Finished(run, 2, false) is { State: "failed", ExitCode: 2 }, "another code fails");
        Check(ShellBlockRunSupport.Finished(run, null, false) is { State: "failed", ExitCode: null }, "no code (a signal) fails");
        Check(ShellBlockRunSupport.Finished(run, 1, true) is { State: "stopped", ExitCode: 1 }, "the user's stop is stopped, whatever the code");
        var done = ShellBlockRunSupport.Finished(run, 0, false);
        Check(ReferenceEquals(ShellBlockRunSupport.Finished(done, 1, false), done), "an ended record does not end again");

        var runs = ShellBlockRunSupport.Replace(null, done);
        runs = ShellBlockRunSupport.Replace(runs, ShellBlockRunSupport.Started("e#1", "pwd"));
        runs = ShellBlockRunSupport.Replace(runs, ShellBlockRunSupport.Started("e#0", "ls"));
        Check(runs.Count == 2 && runs[0] is { Key: "e#0", State: "running" } && runs[1].Key == "e#1", "running again replaces the record in its place");
        for (var index = 0; index < 100; index++) runs = ShellBlockRunSupport.Replace(runs, ShellBlockRunSupport.Started("f#" + index, "x"));
        Check(runs.Count == ShellBlockRunSupport.MaximumRuns && runs[^1].Key == "f#99", "the newest records are kept");
        return Task.CompletedTask;
    }

    internal static Task TheFieldIsOptionalAndARestoredRunIsInterrupted()
    {
        var pane = new RunSession { Id = "p", WorkspaceId = "w" };
        Check(!JsonSerializer.Serialize(pane, Wire.Json).Contains("shellRuns", StringComparison.Ordinal), "none writes nothing");
        var withRuns = pane with { ShellBlockRuns = [ShellBlockRunSupport.Started("e#0", "npm test", "t0"), ShellBlockRunSupport.Finished(ShellBlockRunSupport.Started("e#1", "ls", "t0"), 0, false, "t1")] };
        var json = JsonSerializer.Serialize(withRuns, Wire.Json);
        Check(json.Contains("\"shellRuns\":[{\"key\":\"e#0\",\"command\":\"npm test\",\"state\":\"running\"", StringComparison.Ordinal), "saved as shellRuns: " + json);
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("shellRuns");
        Check(JsonSerializer.Deserialize<RunSession>(node.ToJsonString(), Wire.Json)!.ShellBlockRuns is null, "an older save reads as none");
        node["shellRuns"] = JsonNode.Parse("""[{"key":"e#0","command":"ls","state":"completed","exitCode":0,"output":"a","startedAt":"t"},{"key":7},{"key":"e#2","command":"ls","state":"exploded","output":"","startedAt":"t"}]""");
        var damaged = JsonSerializer.Deserialize<RunSession>(node.ToJsonString(), Wire.Json)!;
        var workspace = new Workspace { Id = "w", Path = Path.GetTempPath() };
        var normalized = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [damaged] }, true).Sessions.Single().ShellBlockRuns;
        Check(normalized is [{ Key: "e#0", State: "completed" }], "a damaged record is dropped, the rest kept");
        node["shellRuns"] = "nonsense";
        Check(JsonSerializer.Deserialize<RunSession>(node.ToJsonString(), Wire.Json)!.ShellBlockRuns is null, "a damaged field keeps the pane");

        var shell = withRuns with { Id = "s", Kind = "shell" };
        var restored = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [JsonSerializer.Deserialize<RunSession>(json, Wire.Json)!, shell] }, true).Sessions.ToDictionary(s => s.Id);
        Check(restored["p"].ShellBlockRuns is [{ Key: "e#0", State: "interrupted", EndedAt: null }, { Key: "e#1", State: "completed", ExitCode: 0 }], "restoring, running reads interrupted and an ended run stays");
        Check(restored["s"].ShellBlockRuns is null, "only an agent pane keeps them");
        var live = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [withRuns] }, false).Sessions.Single().ShellBlockRuns;
        Check(live is [{ State: "running" }, _], "a normalize while the app runs leaves a running run running");
        return Task.CompletedTask;
    }

    internal static Task TheOutputIsCleanedAndItsTailKept()
    {
        var ascii = new string('a', 10_000) + new string('b', 10_000);
        var tail = ShellBlockRunSupport.Tail(ascii);
        Check(tail.Length == ShellBlockRunSupport.OutputLimitBytes && tail.EndsWith('b'), "the last 16 KB");
        var wide = string.Concat(Enumerable.Repeat("한😀", 4_000));
        var wideTail = ShellBlockRunSupport.Tail(wide);
        Check(Encoding.UTF8.GetByteCount(wideTail) <= ShellBlockRunSupport.OutputLimitBytes && wide.EndsWith(wideTail, StringComparison.Ordinal) && !char.IsLowSurrogate(wideTail[0]), "counted in UTF-8, never a half pair");
        Check(ReferenceEquals(ShellBlockRunSupport.Tail("short"), "short"), "short output as it is");
        var normalized = ShellBlockRunSupport.Normalized([new("e#0", "x", "completed", 0, ascii, "t")], false)!;
        Check(normalized[0].Output.Length == ShellBlockRunSupport.OutputLimitBytes, "a saved record's output is held to the limit");

        var output = new ShellBlockOutput();
        output.Append("\u001b[32mgreen\u001b[0m\r\n");
        output.Append("progress 10%\rprogress 50%");
        output.Append("\r");
        output.Append("progress 100%\r\ndone\u001b]0;title\u0007\n");
        Check(output.Text == "green\nprogress 100%\ndone\n", "escapes removed and a carriage return's redraw resolved: " + JsonSerializer.Serialize(output.Text));
        var big = new ShellBlockOutput();
        for (var index = 0; index < 200; index++) big.Append(new string('x', 1_000) + "\n");
        Check(Encoding.UTF8.GetByteCount(big.Text) <= ShellBlockRunSupport.OutputLimitBytes, "live output keeps the tail");
        return Task.CompletedTask;
    }

    internal static Task TheHostRunsOnlyWhatTheReplyShows()
    {
        var reply = new LogEntry("r1", "assistant", "Try:\n\n```console\n$ npm ci\nadded 3 packages\n$ npm test\n```\n\nThen `echo one` and:\n\n```sh\necho one\necho two\n```\n\n```ts\nconst a = 1;\n```\n\n> - ```bash\n>   ls\n>   ```\n\nPS> run me\n\n```sh\nstreaming", "t");
        var request = new LogEntry("u1", "user", "```sh\nrm -rf x\n```", "t");
        var pane = new RunSession { Id = "p", WorkspaceId = "w", Logs = [request, reply] };
        Check(ShellBlockScan.Runnable(reply.Text).SequenceEqual(["npm ci\nnpm test", "echo one\necho two", "ls"]), "the page's blocks, in reading order, into quotes and lists, the open fence left out: " + string.Join(" | ", ShellBlockScan.Runnable(reply.Text)));
        Check(ShellBlockRunSupport.Shown(pane, "r1#1", "echo one\necho two"), "a block's command");
        Check(ShellBlockRunSupport.Shown(pane, "r1#0", "npm ci\nnpm test"), "a console block's commands without their prompt");
        Check(ShellBlockRunSupport.Shown(pane, "r1#2", "ls"), "a block in a quoted list");
        Check(!ShellBlockRunSupport.Shown(pane, "r1#1", "echo one"), "only the whole block");
        Check(!ShellBlockRunSupport.Shown(pane, "r1#1", "echo one\nnpm ci"), "a command forged from the reply's other lines");
        Check(!ShellBlockRunSupport.Shown(pane, "r1#1", "added 3 packages"), "a console block's sample output");
        Check(!ShellBlockRunSupport.Shown(pane, "r1#4", "run me"), "prose");
        Check(!ShellBlockRunSupport.Shown(pane, "r1#0", "echo one\necho two"), "another block's command at the wrong place");
        Check(!ShellBlockRunSupport.Shown(pane, "r1#3", "streaming"), "a block whose fence has not closed");
        Check(!ShellBlockRunSupport.Shown(pane, "u1#0", "rm -rf x"), "never a request's block");
        Check(!ShellBlockRunSupport.Shown(pane, "zz#0", "echo one"), "an unknown entry");
        var graph = new RunSession { Id = "g", WorkspaceId = "w", GraphRuns = [new MightyGraphRun { Id = "run", RootEntries = [reply] }] };
        Check(ShellBlockRunSupport.Shown(graph, "r1#1", "echo one\necho two"), "an entry the Mighty view shows from its run");

        Check(ShellBlockScan.Command("Bash title=x", "ls\n\n") == "ls" && ShellBlockScan.Command("PwSh", "dir") == "dir" && ShellBlockScan.Command("ts", "ls") is null && ShellBlockScan.Command(null, "ls") is null, "languages");
        Check(ShellBlockScan.Command("console", "PS> dir\n> cd x\n% ls\nout") == "dir\ncd x" && ShellBlockScan.Command("console", "only output") is null, "console prompts");
        foreach (var hidden in new[] { "\u202E", "\u202A", "\u2066", "\u2069", "\u200B", "\u200F", "\uFEFF", "\u2060", "\u001b[2K", "\r", "\u0000", "\u007F", "\u000B" })
            Check(ShellBlockScan.Command("sh", "echo safe" + hidden + " && rm -rf ~") is null, "a hidden or reordering character gets no run: U+" + ((int)hidden[0]).ToString("X4"));
        Check(ShellBlockScan.Command("sh", "printf 'a\tb'\necho c") == "printf 'a\tb'\necho c", "tab and newline are fine");
        var trojan = new RunSession { Id = "t", WorkspaceId = "w", Logs = [new LogEntry("r2", "assistant", "```sh\necho ok \u202E; rm -rf ~\n```", "t")] };
        Check(!ShellBlockRunSupport.Shown(trojan, "r2#0", "echo ok \u202E; rm -rf ~"), "the host refuses a bidi block");
        return Task.CompletedTask;
    }

    /// <summary>The service's light update: the pane's record replaced, nothing for a pane that is not an agent's, saved and restored interrupted.</summary>
    internal static async Task TheServiceKeepsTheRecordWithThePane()
    {
        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            var state = Path.Combine(directory, "state");
            await using (var service = new DesktopService(state, null, directory))
            {
                await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
                await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "agent", WorkspaceId = workspace.Id }, new RunSession { Id = "shell", WorkspaceId = workspace.Id, Kind = "shell" }] });
                Check(service.UpdateShellBlockRun("agent", "e#0", _ => ShellBlockRunSupport.Started("e#0", "ping localhost")), "started");
                Check(!service.UpdateShellBlockRun("shell", "e#0", _ => ShellBlockRunSupport.Started("e#0", "ls")), "a shell pane keeps none");
                Check(!service.UpdateShellBlockRun("agent", "e#0", run => run), "the same record changes nothing");
                Check(service.UpdateShellBlockRun("agent", "e#0", run => run! with { Output = new string('x', 40_000) }), "output");
                var saved = service.Snapshot.Sessions.Single(s => s.Id == "agent").ShellBlockRuns!.Single();
                Check(saved is { State: "running" } && saved.Output.Length == ShellBlockRunSupport.OutputLimitBytes, "held to the limit");
                await service.UpdateAsync(s => s);
                Check(service.Snapshot.Sessions.Single(s => s.Id == "agent").ShellBlockRuns is [{ State: "running" }], "a full update keeps it running");
            }
            await using (var again = new DesktopService(state, null, directory))
            {
                await again.InitializeAsync();
                Check(again.Snapshot.Sessions.Single(s => s.Id == "agent").ShellBlockRuns is [{ Key: "e#0", State: "interrupted" }], "after the quit it reads interrupted");
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
