using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MightyClaude.Core;
using Fixture = MightyStructureVerification;

/// <summary>
/// The Windows host against the Mac's rules for a phone (MobileRemoteExtensionTests, RelayLinkTests and the Mac sources they
/// cover): what a session reply carries, when a phone is told, how the relay link is kept, and what the settings list shows.
/// </summary>
internal static class MobileHostVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    private static string L(string key, params (string Key, string Value)[] values) => Locale.Get(key, values.ToDictionary(v => v.Key, v => v.Value));
    private static string Stamp(int second) => new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddSeconds(second).ToString("O");
    private static IReadOnlyList<RegisteredStyle> Bundled()
    {
        var root = Verification.Temp();
        try { return StyleRegistry.Load(Path.Combine(root, "profile"), Path.Combine(root, "workspace")).Styles; }
        finally { Directory.Delete(root, true); }
    }

    // ── mighty.runs ────────────────────────────────────────────────────────────

    internal static Task RunsCarryTheMacsShapeAndCaps()
    {
        MightyGraphAgent Block(string id, string? kind, string status, string title = "") => new() { Id = id, Kind = kind, Status = status, Title = title };
        var run = new MightyGraphRun { Id = "run-1", Input = "tidy up", Status = "completed", Agents = [Block("a1", null, "running"), Block("a2", "task", "waiting"), Block("a3", "steer", "completed"), Block("a4", "compact", "error"), Block("a5", "question", "stopped"), Block("a6", "wormhole", "idle")] };
        var blocks = MobileMightySupport.Blocks(run, 3);
        Check(blocks.Select(b => b.Kind).SequenceEqual(["main", "agent", "task", "steer", "compact", "question", "agent"]), "a kind this build has never heard of is a sub-agent, not a new word on the wire");
        Check(blocks.Select(b => b.Status).SequenceEqual(["completed", "running", "waiting", "completed", "error", "stopped", "running"]), "every status lands on one of the phone's five words");
        Check(blocks[0].Id == "run-1:main" && blocks[0].Title == L("remote.block.request", ("ordinal", "3")) && blocks[0].Summary == "tidy up", "the request block is numbered in the user's language and carries the request as its summary");
        Check(blocks.Skip(1).Select(b => b.Title).SequenceEqual([Locale.Get("graph.block.agent"), Locale.Get("graph.block.task"), Locale.Get("graph.block.steer"), ContextCompaction.Title, Locale.Get("graph.block.question"), Locale.Get("graph.block.agent")]), "a block's title is the desktop's own");
        Check(MobileMightySupport.Blocks(new() { Id = "r", Agents = [Block("a", "task", "running", "run the tests")] }, 1)[^1].Title == "run the tests", "a named block keeps its name");
        foreach (var raw in new[] { "idle", "starting", "queued", "running", "anything" }) Check(MobileMightySupport.Status(raw) == "running", raw + " reads as running");
        Check(MobileMightySupport.Status("failed") == "error" && MobileMightySupport.Status("cancelled") == "stopped" && MobileMightySupport.Status("interrupted") == "stopped" && MobileMightySupport.Status("waiting") == "waiting", "failed is an error, cancelled and interrupted are stops");

        var cut = MobileMightySupport.Output(new string('가', 3_000));
        Check(cut is { Length: MobileWire.MaximumBlockOutput } && cut.EndsWith('가') && MobileMightySupport.Output(null) is null && MobileMightySupport.Output("   ") is null && MobileMightySupport.Output("답\u0007변") == "답변", "a block's output is 2,000 characters, cleaned, absent when empty");
        // "e" and a combining accent are one character of two code units: ten characters are twenty units, never nineteen.
        var accented = "e" + (char)0x301;
        Check(MobileMightySupport.Output(string.Concat(Enumerable.Repeat(accented, 30)), 10) == string.Concat(Enumerable.Repeat(accented, 10)) && MobileMightySupport.Prefix("a" + accented + "b", 2) == "a" + accented, "the cut falls between characters, never inside one");

        var many = Enumerable.Range(1, 25).Select(i => new MightyGraphRun { Id = "run-" + i, Input = i == 25 ? "/ouroboros:seed" : "request " + i, Status = "completed" }).ToArray();
        var styles = Bundled();
        var wire = MobileMightySupport.Runs(many, input => StylePresentation.RequestPrefix(styles, input));
        Check(wire.Count == MobileWire.MightyRuns && wire[0].Id == "run-6" && wire[^1].Id == "run-25", "the newest twenty runs travel, oldest first");
        Check(wire[0].Blocks[0].Title == L("remote.block.request", ("ordinal", "6")) && wire[^1].Blocks[0].Title == L("remote.block.request", ("ordinal", "25")), "the ordinal keeps counting from the pane's own history");
        Check(wire[^1].Title is { Length: > 0 } && wire[0].Title is null && MobileMightySupport.Runs(many)[^1].Title is null && MobileMightySupport.Runs([]).Count == 0, "a run carries its style's name for the request; a plain pane has none");

        var answer = new string('가', 25_000);
        var two = new[] { new MightyGraphRun { Id = "run-1", Input = "first", Status = "completed", FinalOutput = "earlier answer" }, new MightyGraphRun { Id = "run-2", Input = "second", Status = "completed", FinalOutput = answer } };
        var sent = MobileMightySupport.Runs(two);
        Check(sent[0].Result is null && sent[0].Blocks[0].Output == "earlier answer", "an older run leans on its main block's shorter output");
        Check(sent[1].Result is { Length: MobileWire.MaximumRunResult } && sent[1].Blocks[0].Output is { Length: MobileWire.MaximumBlockOutput }, "only the newest settled run carries its result, at 20,000 characters");
        two[1].Status = "running"; Check(MobileMightySupport.Runs(two)[1].Result is null, "a run still in motion has no result");
        two[1].Status = "failed"; two[1].FinalOutput = "stopped on an error"; Check(MobileMightySupport.Runs(two)[1] is { Status: "error", Result: "stopped on an error" }, "a failed run is an error with what it said");
        Check(MobileMightySupport.Runs([new() { Id = "r", Input = new string('x', 40_000) }])[0].Input.Length == MobileWire.MaximumText, "a request's own text is capped too");

        // A block still in motion carries its recent steps, oldest first; a settled one its duration.
        var live = new MightyGraphRun { Id = "live", Input = "work", Status = "running", RootEntries = [new("t0", "system", "turn", Stamp(0), Activity: new("a0", "claude", "turn", "running", "turn")),
            new("t1", "system", "", Stamp(1), Activity: new("a1", "claude", "read", "completed", "", "Read")), new("t2", "assistant", "  first line\nsecond", Stamp(2)), new("t3", "system", "", Stamp(3), Activity: new("a3", "claude", "edit", "running", "edit Models.cs"))] };
        var steps = MobileMightySupport.Blocks(live, 1)[0];
        Check(steps.Activity is { } lines && lines.SequenceEqual(["Read", "first line", "edit Models.cs"]) && steps.DurationMs is null, "a running block lists its steps: a tool's summary or name, a reply's first line, never the turn's own row");
        live.Status = "completed";
        var done = MobileMightySupport.Blocks(live, 1)[0];
        Check(done.Activity is null && done.DurationMs == 3_000, "a settled block sends no steps and the span of its records");
        var crowded = new MightyGraphRun { Id = "c", Status = "running", RootEntries = [.. Enumerable.Range(0, 12).Select(i => new LogEntry("e" + i, "assistant", "step " + i, Stamp(i)))] };
        Check(MobileMightySupport.Blocks(crowded, 1)[0].Activity is { Count: MobileWire.MaximumBlockActivity } recent && recent[0] == "step 4" && recent[^1] == "step 11", "only the newest eight steps travel");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MobileMightySupport.Runs([new() { Id = "r", Input = "x", Status = "running" }]), Wire.Json));
        var plain = json.RootElement[0];
        Check(plain.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["blocks", "id", "input", "status"]) && plain.GetProperty("blocks")[0].EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["id", "kind", "status", "summary", "title"]), "a field the host has nothing for is absent from the wire");
        return Task.CompletedTask;
    }

    internal static Task RunsCarryTheResultFilesTheDesktopLists()
    {
        var root = Verification.Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs")); Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "docs", "note.md"), "note"); File.WriteAllText(Path.Combine(root, "src", "app.ts"), "app");
            var outside = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-outside.txt"); File.WriteAllText(outside, "outside");
            try
            {
                const string named = "wrote docs/note.md, see src/app.ts:12 and docs/note.md:3 again; not docs/missing.md or https://example.com/src/app.ts";
                MightyGraphRun Run(string id, string status, string? output, bool notice = false) => new() { Id = id, Input = "q", Status = status, FinalOutput = output, NoticeRun = notice ? true : null };
                var runs = new[] { Run("old", "completed", "older named docs/note.md"), Run("new", "completed", named + " and " + outside) };
                var wire = MobileMightySupport.Runs(runs, workspaceRoot: root);
                Check(wire[0].ResultFiles is null, "a run that does not carry its result carries no files either");
                Check(wire[1].ResultFiles is { } files && files.SequenceEqual([new MobileResultFile("docs/note.md"), new MobileResultFile("src/app.ts", 12)]),
                    "the desktop panel's list: real workspace files only, in order, once each, a line where the text gave one; got " + string.Join(", ", wire[1].ResultFiles?.Select(f => f.Path + ":" + f.Line) ?? []));
                Check(wire[1].ResultFiles!.SequenceEqual(MightyGraphBlockModel.FilesFor(runs[1], root).Select(f => new MobileResultFile(f.Path, f.Line))), "the very list the desktop's 결과에 나온 파일 panel draws");
                Check(MobileMightySupport.Runs(runs)[1].ResultFiles is null && MobileMightySupport.Runs(runs, workspaceRoot: null)[1].Result is not null, "no folder on this computer (a remote workspace): the result travels, its files do not");

                // Read from the full answer, not the 20,000-character result the phone gets.
                var tail = MobileMightySupport.Runs([Run("long", "completed", new string('가', 25_000) + " then src/app.ts")], workspaceRoot: root)[0];
                Check(tail.Result is { Length: MobileWire.MaximumRunResult } && tail.ResultFiles is [{ Path: "src/app.ts", Line: null }], "a file named past the clipped result is still listed");

                // Only a successful, finished result.
                Check(MobileMightySupport.Runs([Run("failed", "failed", named)], workspaceRoot: root)[0] is { Result: not null, ResultFiles: null }, "a failed run's result names no files");
                Check(MobileMightySupport.Runs([Run("stopped", "stopped", named)], workspaceRoot: root)[0] is { Result: not null, ResultFiles: null }, "a stopped run's result names no files");
                Check(MobileMightySupport.Runs([Run("running", "running", named)], workspaceRoot: root)[0].ResultFiles is null, "a run in motion names none");
                var open = Run("open", "completed", named); open.Agents = [new() { Id = "a", Status = "running" }];
                Check(MobileMightySupport.Runs([open], workspaceRoot: root)[0].ResultFiles is null, "a run with a block still running is not finished");
                Check(MobileMightySupport.Runs([Run("none", "completed", "nothing to open here")], workspaceRoot: root)[0].ResultFiles is null, "a result that names none carries no list");

                // A notice run after the newest request carries its own, as it carries its result.
                var notices = MobileMightySupport.Runs([Run("request", "completed", "docs/note.md"), Run("request:notice:1", "completed", "src/app.ts", notice: true)], workspaceRoot: root);
                Check(notices[0].ResultFiles is [{ Path: "docs/note.md" }] && notices[1].ResultFiles is [{ Path: "src/app.ts" }], "the newest request and its notice runs each list their own files");

                // The cap: the first 50 of the desktop's list.
                Directory.CreateDirectory(Path.Combine(root, "many"));
                for (var i = 0; i < 60; i++) File.WriteAllText(Path.Combine(root, "many", $"f{i:D2}.txt"), "");
                var crowded = MobileMightySupport.Runs([Run("crowded", "completed", string.Join(" ", Enumerable.Range(0, 60).Select(i => $"many/f{i:D2}.txt")))], workspaceRoot: root)[0].ResultFiles;
                Check(crowded is { Count: MobileWire.MaximumRunResultFiles } && crowded[0].Path == "many/f00.txt" && crowded[^1].Path == "many/f49.txt", "at most 50 files travel, the first ones");

                // A pane's memo looks a result through once: a second reply for the same result reads the kept list.
                var looks = 0;
                var memo = new MobileResultFilesMemo((run, folder) => { looks++; return MobileMightySupport.LookThrough(run, folder); });
                var first = MobileMightySupport.Runs(runs, workspaceRoot: root, resultFiles: memo);
                Check(looks == 1 && first[1].ResultFiles!.SequenceEqual(wire[1].ResultFiles!) && memo.Count == 1, "the first reply looks the result through");
                var second = MobileMightySupport.Runs(runs, workspaceRoot: root, resultFiles: memo);
                Check(looks == 1 && second[1].ResultFiles!.SequenceEqual(wire[1].ResultFiles!), "the next reply for the same result does not look again");
                var cached = new MobileResultFilesMemo(); MobileMightySupport.Runs(runs, workspaceRoot: root, resultFiles: cached);
                File.Move(Path.Combine(root, "src", "app.ts"), Path.Combine(root, "src", "moved.ts"));
                try { Check(MobileMightySupport.Runs(runs, workspaceRoot: root, resultFiles: cached)[1].ResultFiles is [_, { Path: "src/app.ts" }], "a kept list is sent as it was, without the workspace being read"); }
                finally { File.Move(Path.Combine(root, "src", "moved.ts"), Path.Combine(root, "src", "app.ts")); }
                runs[1].FinalOutput += " and docs/note.md once more";
                MobileMightySupport.Runs(runs, workspaceRoot: root, resultFiles: memo);
                Check(looks == 2, "a result that changed is looked through again");
                MobileMightySupport.Runs(runs, workspaceRoot: Path.Combine(root, "docs"), resultFiles: memo);
                Check(looks == 3, "another folder is looked through again");
                runs[1].Status = "running"; MobileMightySupport.Runs(runs, workspaceRoot: root, resultFiles: memo);
                Check(memo.Count == 0, "a run that no longer carries a result is dropped from the memo");
                runs[1].Status = "completed"; runs[1].FinalOutput = named + " and " + outside;

                using var json = JsonDocument.Parse(JsonSerializer.Serialize(wire[1], Wire.Json));
                var list = json.RootElement.GetProperty("resultFiles");
                Check(list.GetArrayLength() == 2 && list[0].EnumerateObject().Select(p => p.Name).SequenceEqual(["path"]) && list[1].GetProperty("path").GetString() == "src/app.ts" && list[1].GetProperty("line").GetInt32() == 12,
                    "the wire shape is resultFiles: [{ path, line? }]");
                using var bare = JsonDocument.Parse(JsonSerializer.Serialize(wire[0], Wire.Json));
                Check(!bare.RootElement.TryGetProperty("resultFiles", out _), "a run without files has no resultFiles on the wire");
            }
            finally { File.Delete(outside); }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task APaneWithoutAGraphIsGroupedFromItsTranscript()
    {
        var pane = new RunSession { Id = "p", Status = "running", Logs = [new("o1", "assistant", "an old reply", Stamp(0)), new("u1", "user", "first request", Stamp(1)), new("r1", "assistant", "first answer", Stamp(2)), new("u2", "user", "second request", Stamp(3)), new("r2", "assistant", "working", Stamp(4))] };
        var runs = MobileMightySupport.SavedRuns(pane);
        Check(runs.Select(r => r.Id).SequenceEqual(["history-p", "u1", "u2"]) && runs.Select(r => r.Status).SequenceEqual(["completed", "completed", "running"]), "replies before the first request stand under a run of their own; only the last run is still running");
        Check(runs[1] is { Input: "first request", FinalOutput: "first answer" } && runs[1].RootEntries.Single().Id == "r1" && runs[2].FinalOutput is null && runs.All(r => r.Agents.Count == 0), "a request's replies are its records and its last reply its answer; no sub-agent is invented");
        var wire = MobileMightySupport.Runs(runs);
        Check(wire.Count == 3 && wire[1].Blocks[0].Output == "first answer" && wire[2].Status == "running" && wire[2].Result is null, "the phone gets runs for a pane that has no graph");
        Check(MobileMightySupport.SavedRuns(pane with { GraphRuns = [] }).Count == 0 && MobileMightySupport.SavedRuns(pane with { Status = "idle" })[^1].Status == "completed", "a saved graph is used as it stands; an idle pane's last run has completed");

        var digest = MobileMightySupport.Digest(pane);
        Check(digest == MobileMightySupport.Digest(pane with { Logs = [.. pane.Logs.SkipLast(1), pane.Logs[^1] with { Text = "working on it" }] }), "a streamed token does not move the digest");
        Check(digest != MobileMightySupport.Digest(pane with { Logs = [.. pane.Logs, new("r3", "assistant", "more", Stamp(5))] }), "a new record moves the digest");
        Check(digest != MobileMightySupport.Digest(pane with { Status = "completed" }), "the last run's status moves the digest");
        var settled = pane with { Status = "completed" };
        Check(MobileMightySupport.Digest(settled) != MobileMightySupport.Digest(settled with { Logs = [.. pane.Logs.SkipLast(1), pane.Logs[^1] with { Text = "the whole answer" }] }), "the newest settled run's answer length moves the digest: that run carries the result");
        // Only the window the payload sends is read.
        var longPane = new RunSession { Id = "l", Logs = [.. Enumerable.Range(0, 60).SelectMany(i => new[] { new LogEntry("u" + i, "user", "q" + i, Stamp(i)), new LogEntry("a" + i, "assistant", "a" + i, Stamp(i)) })] };
        Check(MobileMightySupport.Digest(longPane) == MobileMightySupport.Digest(longPane with { Logs = [new("u0", "user", "changed", Stamp(0)), .. longPane.Logs.Skip(1)] }), "requests older than the newest twenty are not hashed");

        var graph = new[] { new MightyGraphRun { Id = "g", Status = "running", RootEntries = [new("m", "assistant", "reading", Stamp(0))], Agents = [new() { Id = "a", Status = "running" }] } };
        var before = MobileMightySupport.Digest(graph);
        graph[0].RootEntries[0] = graph[0].RootEntries[0] with { Text = "reading the file" }; Check(MobileMightySupport.Digest(graph) == before, "a saved graph's streamed text does not move its digest");
        graph[0].Agents[0].Status = "completed"; Check(MobileMightySupport.Digest(graph) != before, "a block's status moves it");
        return Task.CompletedTask;
    }

    // ── revisions ──────────────────────────────────────────────────────────────

    internal static async Task APhoneIsToldOnlyOfWhatChanged()
    {
        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            var request = new LogEntry("u1", "user", "hello", Stamp(0)); var reply = new LogEntry("r1", "assistant", new string('a', 300), Stamp(1));
            var a = new RunSession { Id = "a", WorkspaceId = workspace.Id, Status = "running", Logs = [request, reply] }; var b = new RunSession { Id = "b", WorkspaceId = workspace.Id };
            await service.UpdateAsync(s => s with { Sessions = [a, b] }); var statusLine = "12:00";
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([], StatusLine: new { text = statusLine }));
            async Task Set(params RunSession[] panes) { await service.UpdateAsync(s => s with { Sessions = [.. panes] }); router.Changed(); router.Observe(); }
            router.Observe(); var first = router.TakeNotifications();
            Check(first.Select(n => n.Scope).SequenceEqual(["state", "session:a", "session:b"]), "the first reading tells the list and every pane: " + string.Join(",", first.Select(n => n.Scope)));
            long state = router.StateRevision, ra = router.SessionRevision("a"), rb = router.SessionRevision("b");

            // The reply grows past what the list previews: the pane moved, the list did not.
            a = a with { Logs = [request, reply with { Text = new string('a', 400) }] }; await Set(a, b);
            var streamed = router.TakeNotifications();
            Check(streamed.Count == 1 && streamed[0] == ("session:a", ra + 1) && router.StateRevision == state && router.SessionRevision("b") == rb, "a streamed reply tells its own pane alone: " + string.Join(",", streamed));
            // The status line is no part of a revision: one that prints a clock would wake the phone on every run.
            statusLine = "12:01"; router.Changed(); router.Observe();
            Check(router.TakeNotifications().Count == 0 && router.SessionRevision("a") == ra + 1, "a status line that moved tells nobody");
            // A pane's status shows in the list too.
            a = a with { Status = "completed" }; await Set(a, b);
            var finished = router.TakeNotifications();
            Check(finished.SequenceEqual([("state", state + 1), ("session:a", ra + 2)]) && router.SessionRevision("b") == rb, "a finished run tells the list and its pane, and no other pane: " + string.Join(",", finished));
            // The summary alone (the title is the list's too) moves the pane's own revision with it.
            b = b with { Title = "renamed" }; await Set(a, b);
            Check(router.TakeNotifications().SequenceEqual([("state", state + 2), ("session:b", rb + 1)]), "a renamed pane tells the list and that pane");
            await Set(a, b); Check(router.TakeNotifications().Count == 0, "reading again with nothing moved tells nobody");

            async Task<JsonElement> Get(string path) { var answer = await router.RouteAsync("GET", path, null, "phone", CancellationToken.None); Check(answer.Status == 200, path + " answers"); return Json(answer.Body); }
            var list = await Get("/m1/state"); var detail = await Get("/m1/sessions/a");
            Check(list.GetProperty("revision").GetInt64() == state + 2 && list.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("revision").GetInt64()).SequenceEqual([ra + 2, rb + 1]), "the list carries its own revision and each pane's");
            Check(detail.GetProperty("revision").GetInt64() == ra + 2 && detail.GetProperty("session").GetProperty("revision").GetInt64() == ra + 2, "a pane's detail carries that pane's revision");
            // A long poll waits on its own scope: another pane moving does not answer it early.
            var started = DateTime.UtcNow; var waiting = Get("/m1/sessions/a?since=" + (ra + 2) + "&wait=1");
            await Task.Delay(100); b = b with { Status = "running" }; await service.UpdateAsync(s => s with { Sessions = [a, b] }); router.Changed();
            var unmoved = await waiting;
            Check(unmoved.GetProperty("revision").GetInt64() == ra + 2 && DateTime.UtcNow - started > TimeSpan.FromMilliseconds(800), "a pane's poll sleeps through another pane's change");
            started = DateTime.UtcNow; var woken = Get("/m1/sessions/a?since=" + (ra + 2) + "&wait=5");
            await Task.Delay(100); a = a with { Logs = [.. a.Logs, new("u2", "user", "again", Stamp(2))] }; await service.UpdateAsync(s => s with { Sessions = [a, b] }); router.Changed();
            Check((await woken).GetProperty("revision").GetInt64() == ra + 3 && DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "a pane's poll answers when that pane moves");
            // A closed pane is forgotten and the list says so.
            router.TakeNotifications(); await Set(b); var closed = router.TakeNotifications();
            Check(closed.Count == 1 && closed[0].Scope == "state" && router.SessionRevision("a") == 1, "a closed pane moves the list and loses its revision");
            // A pane closed between the snapshot and its reading is read from its record, and a poll of another scope still answers.
            using (var closing = new MobileDesktopRouter(service, Path.Combine(directory, "closing"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { Digest = p => p.Id == "b" ? throw new MobileRequestException(404, "Session not found.") : null })
            {
                var polled = await closing.RouteAsync("GET", "/m1/state?since=0&wait=1", null, "phone", CancellationToken.None);
                Check(polled.Status == 200 && closing.TakeNotifications().Select(n => n.Scope).SequenceEqual(["state", "session:b"]), "a pane whose digest cannot be read does not fail the poll: " + polled.Status);
            }
            // A pass that fails all the same is made again at the next call, so its change is not lost.
            var fails = true;
            using (var flaky = new MobileDesktopRouter(service, Path.Combine(directory, "flaky"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { ActiveStyle = _ => fails ? throw new InvalidOperationException("the pane went away") : null })
            {
                var threw = false; try { flaky.Observe(); } catch (InvalidOperationException) { threw = true; }
                fails = false; flaky.Observe();
                Check(threw && flaky.TakeNotifications().Select(n => n.Scope).SequenceEqual(["state", "session:b"]), "a failed pass is read again rather than dropped");
            }

            // The rule itself: what a pane's revision is read from.
            var revisions = new MobileRevisions(); var pane = new RunSession { Id = "x" };
            MobileRevisions.Reading Reading(RunSession value, bool editable = true, int options = 0, int mighty = 0, string summary = "s") => new(value, editable, options, mighty, summary);
            revisions.Observe([], [Reading(pane)]);
            Check(revisions.Observe([], [Reading(pane)]).Count == 0, "the same reading moves nothing");
            Check(revisions.Observe([], [Reading(pane, editable: false)]).SequenceEqual([("session:x", 2)]), "pickers the phone may no longer use move the pane");
            Check(revisions.Observe([], [Reading(pane, false, options: 7)]).SequenceEqual([("session:x", 3)]) && revisions.Observe([], [Reading(pane, false, 7, mighty: 9)]).SequenceEqual([("session:x", 4)]), "the options offered and the Mighty payload's identity move the pane");
            Check(revisions.Observe([], [Reading(pane with { Settings = new(Effort: "high") }, false, 7, 9)]).SequenceEqual([("session:x", 5)]), "a setting the phone shows moves the pane");
            Check(revisions.Observe([new Workspace { Id = "w", Path = workspacePath }], [Reading(pane with { Settings = new(Effort: "high") }, false, 7, 9)]).SequenceEqual([("state", 3)]), "a workspace moves the list alone");
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── what a session reply carries ───────────────────────────────────────────

    internal static async Task ASessionReplyReadsAsTheDesktopDoes()
    {
        var command = MobilePermission.From(new("q1", "run", "tool", "Bash", "{\"command\":\"npm test\",\"description\":\"Run the tests\",\"timeout\":5000}", "npm test"));
        var card = ToolPermissionPresentation.Make("Bash", "{\"command\":\"npm test\",\"description\":\"Run the tests\",\"timeout\":5000}");
        Check(command.Title == card.Title && command.Title == ToolPermissionStrings.TitleBash && command.Headline == "Run the tests", "the phone's card has the desktop card's title and headline");
        Check(command.Fields.Select(f => (f.Label, f.Value)).SequenceEqual(card.Fields.Select(f => (f.Label, f.Value))) && command.Fields[0] == new MobilePermissionField(ToolPermissionStrings.FieldCommand, "npm test") && command is { Plan: null, ReceivedAt: null, Questionnaire: null, CanAllow: true, Summary: "npm test" }, "its fields are the desktop card's, not the raw input");
        var asked = "{\"questions\":[{\"header\":\"Pick\",\"question\":\"Which?\",\"multiSelect\":false,\"options\":[{\"label\":\"A\",\"description\":\"a\"},{\"label\":\"B\",\"description\":\"b\"}]}]}";
        Check(MobilePermission.From(new("q2", "run", "tool", "AskUserQuestion", asked, "question", CanAllow: false, CanAnswerQuestions: true)).Questionnaire is { Questions.Count: 1 } && MobilePermission.From(new("q3", "run", "tool", "AskUserQuestion", asked, "question")).Questionnaire is null, "a question travels as a questionnaire only when it can be answered");
        var planned = MobilePermission.From(new("q4", "run", "tool", ClaudePlanMode.ToolName, "{\"plan\":\"# Plan\\n1. do it\"}", "plan", CanAnswerPlan: true, ReceivedAt: Stamp(0)));
        Check(planned.Plan == "# Plan\n1. do it" && planned.Title == Locale.Get("plan.card.title") && planned.Headline is null && planned.Fields.Single() == new MobilePermissionField(Locale.Get("plan.card.title"), planned.Plan) && planned.ReceivedAt == Stamp(0), "a plan carries its text as the plan, and once more as its one field");

        var picture = new LogEntry("i1", "image", "screenshot.png", Stamp(0), Images: [new("cache-name", "image/png", 10, 10, 100, "tool")]);
        var noted = new LogEntry("s1", "system", "with a picture", Stamp(1), Images: [new("other", "image/png", 10, 10, 100, "tool")]);
        var phone = MobileRemoteSupport.PhoneEntries([picture, noted, new("r", "assistant", "text", Stamp(2))]);
        Check(phone[0] is { Kind: "system", Text: "screenshot.png", Images: null } && phone[1] is { Kind: "system", Images: null } && phone[2].Kind == "assistant", "an image entry reaches the phone as the system line naming it, without references");
        Check(!JsonSerializer.Serialize(phone, Wire.Json).Contains("cache-name", StringComparison.Ordinal), "no cache name travels");

        var limits = MobileRemoteSupport.RateLimits([new("five_hour", 140), new("weekly"), new("daily", 12.5, Stamp(9)), new("monthly", double.NaN)])!;
        Check(limits.Count == 2 && limits[0] == new MobileRateLimit(AccountUsageSupport.WindowLabel("five_hour"), 100) && limits[1] == new MobileRateLimit(AccountUsageSupport.WindowLabel("daily"), 12.5, Stamp(9)) && MobileRemoteSupport.RateLimits([new("weekly")]) is null && MobileRemoteSupport.RateLimits(null) is null, "the account windows travel under the desktop's names, the unmeasured ones left out");

        var entries = Enumerable.Range(0, 10).Select(i => new LogEntry("e" + i, "assistant", "t" + i, Stamp(i))).ToArray();
        var page = MobileRemoteSupport.Page(entries, "e6", 4);
        Check(page.Entries.Select(e => e.Id).SequenceEqual(["e2", "e3", "e4", "e5"]) && page.HasMore && MobileRemoteSupport.Page(entries, "e2", 4) is { HasMore: false, Entries.Count: 2 }, "a page is the entries just before the cursor, newest last");
        Check(MobileRemoteSupport.Page(entries, "gone", 4) is { HasMore: false, Entries.Count: 0 }, "a cursor the host no longer holds answers empty rather than restarting");

        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "pane", WorkspaceId = workspace.Id, Logs = [new("u", "user", "look", Stamp(0)), picture] }, new RunSession { Id = "shell", WorkspaceId = workspace.Id, Kind = "shell" }] });
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { ActiveStyle = _ => null };
            var detail = Json((await router.RouteAsync("GET", "/m1/sessions/pane", null, "phone", CancellationToken.None)).Body);
            Check(detail.GetProperty("entries").EnumerateArray().Select(e => e.Text("kind")).SequenceEqual(["user", "system"]) && !detail.GetProperty("entries")[1].TryGetProperty("images", out _), "the detail's transcript has no image entry");
            var older = Json((await router.RouteAsync("GET", "/m1/sessions/pane/entries?before=i1", null, "phone", CancellationToken.None)).Body);
            Check(older.GetProperty("entries").GetArrayLength() == 1 && !older.GetProperty("hasMore").GetBoolean(), "an older page reads the same way");
            var newest = Json((await router.RouteAsync("GET", "/m1/sessions/pane/entries?limit=1", null, "phone", CancellationToken.None)).Body);
            var gone = Json((await router.RouteAsync("GET", "/m1/sessions/pane/entries?before=gone", null, "phone", CancellationToken.None)).Body);
            Check(newest.GetProperty("entries").EnumerateArray().Single().Text("id") == "i1" && newest.GetProperty("hasMore").GetBoolean() && gone.GetProperty("entries").GetArrayLength() == 0 && !gone.GetProperty("hasMore").GetBoolean(), "without a cursor the page is the newest entries; a cursor the host no longer holds answers empty");
            Check(!detail.TryGetProperty("rateLimits", out _) && !detail.TryGetProperty("usage", out _), "a pane that reported no account window sends no limits");
            var session = detail.GetProperty("session");
            Check(session.Text("styleId") == "cli" && session.Text("mightyStyle") == "cli" && session.Text("agentViewMode") == "plain", "an agent pane that runs no style says cli");
            var shell = Json((await router.RouteAsync("GET", "/m1/state", null, "phone", CancellationToken.None)).Body).GetProperty("sessions")[1];
            Check(shell.GetProperty("terminal").GetBoolean() && !shell.TryGetProperty("styleId", out _) && !shell.TryGetProperty("agentViewMode", out _) && !shell.TryGetProperty("mightyStyle", out _), "a pane that is not an agent's has no view and no style");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// What a phone is told about a pane's provider, by the Mac's rules (MobileRemoteProviderTests): an Antigravity pane on
    /// <c>default</c> names no model and an old report never revives, and no pane tells the phone it steers (<c>canSteer</c> is always
    /// false since 0.4.35, kept for older phones).
    /// </summary>
    internal static async Task APaneTravelsAsItsProviderRuns()
    {
        var capabilities = ProviderCatalog.Capabilities("gemini", "1.2.3");
        Check(ProviderCatalog.Name("gemini") == "Antigravity CLI" && ProviderCatalog.Executable("gemini") == "agy", "the gemini id runs agy");
        Check(!capabilities.Effort && MobileRemoteSupport.EffortOptions(capabilities.Effort, ["low", "high"]) is null && capabilities.PermissionModes.SequenceEqual(["manual", "plan", "acceptEdits", "fullAccess"]), "an Antigravity pane offers its permission modes and no effort");
        Check(DelegationProviders.CanSteer("claude", "claude") && !DelegationProviders.CanSteer("claude", "gemini") && !DelegationProviders.CanSteer("claude", "codex") && !DelegationProviders.CanSteer("shell", "claude"), "only a Claude agent pane takes a delegation notice into its running turn");

        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            SessionUsage Reported(string provider, string model, string selected) => new() { Provider = provider, Model = model, SelectedModel = selected };
            await service.UpdateAsync(s => s with { Sessions = [
                new RunSession { Id = "agy", WorkspaceId = workspace.Id, Provider = "gemini", Model = "default", SessionUsage = Reported("gemini", "gemini-4-argon-medium", "default") },
                new RunSession { Id = "codex", WorkspaceId = workspace.Id, Provider = "codex", Model = "default" },
                new RunSession { Id = "opus", WorkspaceId = workspace.Id, Model = "opus", SessionUsage = Reported("claude", "claude-opus-5-5", "opus") },
                new RunSession { Id = "switched", WorkspaceId = workspace.Id, Model = "sonnet", SessionUsage = Reported("claude", "claude-opus-5-5", "opus") },
                new RunSession { Id = "default", WorkspaceId = workspace.Id, Model = "default" },
                new RunSession { Id = "shell", WorkspaceId = workspace.Id, Kind = "shell" }] });
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { ActiveStyle = _ => null };
            async Task<Dictionary<string, JsonElement>> Panes() => Json((await router.RouteAsync("GET", "/m1/state", null, "phone", CancellationToken.None)).Body).GetProperty("sessions").EnumerateArray().ToDictionary(p => p.Text("id")!);
            var panes = await Panes();
            Check(panes["agy"].Text("provider") == "gemini" && panes["agy"].Text("model") == "default" && !panes["agy"].TryGetProperty("resolvedModel", out _), "an Antigravity pane on default names no model, so the phone reads its default label alone: " + panes["agy"]);
            Check(panes.Values.All(pane => pane.GetProperty("canSteer").GetBoolean() is false), "every pane, Claude too, only queues: canSteer is always false");
            Check(panes["opus"].Text("resolvedModel") == "claude-opus-5-5", "a Claude family alias takes the model reported for it");
            Check(!panes["switched"].TryGetProperty("resolvedModel", out _) && !panes["shell"].TryGetProperty("resolvedModel", out _), "a model reported for an earlier selection never shows, and a command pane names none");
            Check(!panes["default"].TryGetProperty("resolvedModel", out _), "without a catalogue a Claude default resolves to nothing");
            var detail = Json((await router.RouteAsync("GET", "/m1/sessions/agy", null, "phone", CancellationToken.None)).Body).GetProperty("session");
            Check(detail.GetProperty("canSteer").GetBoolean() is false && !detail.TryGetProperty("resolvedModel", out _), "the session reply's summary says the same");

            // The catalogue the CLI listed resolves `default`, as the Mac's chip does.
            router.Catalog = pane => pane.Provider == "claude" ? new ModelCatalog("cli", [new ModelOption("default", "Default", "", "claude-opus-5-5"), new ModelOption("opus", "Opus", "")], "") : null;
            panes = await Panes();
            Check(panes["default"].Text("resolvedModel") == "claude-opus-5-5" && !panes["agy"].TryGetProperty("resolvedModel", out _), "a listed default resolves; an Antigravity default still does not");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static Task SettingsOfferWhatThePaneCanPick()
    {
        var styles = Bundled();
        var offered = MobileRemoteSupport.StyleOptions(styles);
        Check(offered[0] == new MobileStyleOption("cli", "cli") && offered.Skip(1).Select(s => s.Id).SequenceEqual(styles.Where(s => s.Runnable).Select(s => s.Id)) && offered.Skip(1).All(s => s.Source == "bundled" && s.Label.Length > 0), "styles are the CLI, then every style that can run, each with its source");
        Check(!Json(offered)[0].TryGetProperty("source", out _), "cli belongs to no source");
        Check(MobileRemoteSupport.StyleOptions([]).Single().Id == "cli" && MobileRemoteSupport.LegacyStyleOptions([]).Single() == new MobileOption("cli", "CLI"), "a pane outside the Mighty view is offered the CLI alone, so a phone can always go back to free requests");
        Check(MobileWire.MightyStyles.SequenceEqual(["cli", "ouroboros"]) && MobileRemoteSupport.LegacyStyleOptions(styles).Select(s => s.Id).SequenceEqual(["cli", "ouroboros"]), "the old field offers its fixed two, when they can run");
        // v7 dropped the removed style's word: an older phone that still offers it is refused, even were a style there to answer
        // to the id, and a pane still saved in it reads cli, its truth travelling in styleId (macOS theStyleFieldsRideBesideTheFixedVocabulary).
        var node = System.Text.Json.Nodes.JsonNode.Parse(styles.Single(s => s.Id == "ouroboros").Bytes.Span)!; node["id"] = "paperthin"; var reserved = Encoding.UTF8.GetBytes(node.ToJsonString());
        var stranger = new RegisteredStyle(StyleManifestDecoder.Decode(reserved, "bundled"), "bundled", "paperthin.json", null, "h", "preApproved", reserved);
        Check(stranger.Id == "paperthin" && !MobileRemoteSupport.LegacyStyleOptions([.. styles, stranger]).Any(s => s.Id == "paperthin") && MobileRemoteSupport.Style("paperthin") == "cli" && MobileRemoteSupport.Style("claude-plan") == "cli", "the old field has no paperthin word any more");
        try { StyleManifestDecoder.Decode(reserved, "user"); throw new InvalidOperationException("a user style claimed the reserved id paperthin"); }
        catch (StyleManifestException e) { Check(e.Code == "E_RESERVED_ID", "paperthin stays a reserved id: " + e.Code); }
        Check(MobileRemoteSupport.GuidedStylesAvailable("claude", "claude", "mighty") && !MobileRemoteSupport.GuidedStylesAvailable("claude", "claude", "plain") && !MobileRemoteSupport.GuidedStylesAvailable("claude", "codex", "mighty") && !MobileRemoteSupport.GuidedStylesAvailable("shell", "claude", "mighty"), "guided styles need a Claude pane in the Mighty view");
        Check(MobileRemoteSupport.ViewMode("mighty") == "mighty" && MobileRemoteSupport.ViewMode(null) == "plain" && MobileRemoteSupport.ViewMode("default") == "plain" && MobileRemoteSupport.Style("ouroboros") == "ouroboros" && MobileRemoteSupport.Style("gstack") == "cli" && MobileRemoteSupport.Style(null) == "cli", "the wire words for the view and the old style field");

        Check(MobileRemoteSupport.EffortOptions(false, ["low", "high"]) is null, "a provider with no effort setting sends no effort options");
        var efforts = MobileRemoteSupport.EffortOptions(true, ["low", "xhigh"])!;
        Check(efforts.SequenceEqual([new("default", "Auto"), new("low", "Low"), new("xhigh", "XHigh")]), "efforts carry the menu's labels, the default first");
        var settings = Json(new MobileSettings(true, "opus", "manual", null, "plain", "cli", "cli", new([], [], null, [], [])));
        Check(!settings.TryGetProperty("effort", out _) && !settings.GetProperty("options").TryGetProperty("efforts", out _), "effort is absent together with its options");

        // The style a settings request leaves the pane with.
        Check(MobileRemoteSupport.StyleAfter(null, "cli", "ouroboros") == "ouroboros" && MobileRemoteSupport.StyleAfter("cli", "cli", "ouroboros") == "ouroboros", "a phone handing back what it was told leaves the stored style alone");
        Check(MobileRemoteSupport.StyleAfter("cli", "ouroboros", "ouroboros") is null && MobileRemoteSupport.StyleAfter("claude-plan", "ouroboros", "ouroboros") == "claude-plan" && MobileRemoteSupport.StyleAfter("ouroboros", "cli", null) == "ouroboros", "a style other than the one shown is a change");

        var both = MobileRemoteSupport.GuidedRequest(Json(new { styleId = "gstack", actionId = "ship", style = "ouroboros", skill = "seed", text = "  go\n" }));
        Check(both == new MobileGuidedRequest("gstack", "ship", "go"), "the new shape wins when both arrive, and the text is trimmed");
        Check(MobileRemoteSupport.GuidedRequest(Json(new { style = "ouroboros", skill = "seed" })) == new MobileGuidedRequest("ouroboros", "seed", "") && MobileRemoteSupport.GuidedRequest(Json(new { styleId = "ouroboros", skill = "seed" })).Action == "seed", "the older shape and a half-migrated one are read too");
        foreach (var bad in new object[] { new { skill = "seed" }, new { styleId = "cli", actionId = "x" }, new { styleId = "Bad Id", actionId = "x" }, new { styleId = "ouroboros" }, new { styleId = "ouroboros", actionId = "bad action" }, new { style = "ouroboros", actionId = "seed" }, new { styleId = "ouroboros", actionId = "seed", text = new string('x', MobileWire.MaximumText + 1) } })
        {
            var refused = false; try { MobileRemoteSupport.GuidedRequest(Json(bad)); } catch (MobileRequestException ex) { refused = ex.Status == 400; }
            Check(refused, "a malformed style request is a 400: " + JsonSerializer.Serialize(bad));
        }
        Check(MobileRemoteSupport.SingleLine("  first line \r\n\n second\tline  ") == "first line second\tline", "a phone's text for a style button is folded to one line");
        Check(MobileRemoteSupport.ArgumentHint(SlashArgument.Model) == Locale.Get("remote.slashHint.model") && MobileRemoteSupport.ArgumentHint(SlashArgument.Permission) == Locale.Get("remote.slashHint.permission") && MobileRemoteSupport.ArgumentHint(null) is null, "a command's argument hint is in the user's language");
        return Task.CompletedTask;
    }

    internal static Task OlderPhonesStillGetTheOuroborosPayload()
    {
        var styles = Bundled(); var flow = styles.Single(s => s.Id == "ouroboros");
        StylePhase Phase(string id) => flow.Manifest.Phases.Single(p => p.Id == id);
        var seeded = MobileLegacyStyleAdapter.Payload(flow, Phase("seed"), false);
        Check(seeded is { Phase: "seed", Ready: false } ouroboros && ouroboros.Next.Select(s => s.Skill).SequenceEqual(["run", "evaluate", "status"]), "ouroboros carries its phase and that phase's next skills");
        Check(seeded!.All.Select(s => s.Skill).SequenceEqual(["interview", "auto", "seed", "run", "evaluate", "evolve", "ralph", "status", "unstuck", "cancel"]) && seeded.TakesText.SequenceEqual(["interview", "auto", "unstuck"]), "the whole catalogue, and which skills take text");
        Check(seeded.All.Single(s => s.Skill == "seed") is { Title.Length: > 0, Help.Length: > 0 }, "a skill carries its title and help");
        var phases = flow.Manifest.Phases.OrderBy(p => p.Order).Select(p => p.Id).ToArray();
        Check(phases.SequenceEqual(["goal", "interview", "seed", "run", "evaluate", "evolve"]) && phases.Select(id => MobileLegacyStyleAdapter.Payload(flow, Phase(id), true)!.Phase).SequenceEqual(phases), "every phase reports itself");
        var start = MobileLegacyStyleAdapter.Payload(flow, null, true)!;
        Check(start.Phase == "goal" && start.Next.Count == 0 && MobileLegacyStyleAdapter.Payload(flow, Phase("goal"), true)!.Next.Count == 0, "the old field is the phase map alone: empty at the first phase, where the older phone falls back to all");
        // The adapter belongs to the bundled Ouroboros and nothing else: Claude plan, a user style and a catalogue send none.
        Check(styles.Any(s => s.Id == "claude-plan") && styles.Where(s => s.Id != "ouroboros").Append(StyleFixtures.CatalogueStyle).All(s => MobileLegacyStyleAdapter.Payload(s, null, true) is null), "any other style sends none");
        Check(!Json(new { ouroboros = MobileLegacyStyleAdapter.Payload(styles.Single(s => s.Id == "claude-plan"), null, true) }).TryGetProperty("ouroboros", out _) && Json(new { ouroboros = seeded }).TryGetProperty("ouroboros", out _), "the payload a style does not have is absent from the wire");
        return Task.CompletedTask;
    }

    // ── files ──────────────────────────────────────────────────────────────────

    internal static async Task FileRepliesCarryACodeAndTheMacsCaps()
    {
        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(Path.Combine(workspacePath, "src"));
            File.WriteAllText(Path.Combine(workspacePath, "src", "app.py"), "print('안녕')\nprint(2)\n"); File.WriteAllText(Path.Combine(workspacePath, "notes.md"), "# notes\n");
            File.WriteAllBytes(Path.Combine(workspacePath, "blob.bin"), [0, 1, 2, 3, 0, 255]);
            File.WriteAllText(Path.Combine(workspacePath, "long.txt"), string.Concat(Enumerable.Repeat("0123456789abcde\n", MobileWorkspaceFiles.MaximumTextBytes / 16 + 4_096)));
            File.WriteAllText(Path.Combine(directory, "outside.txt"), "outside");
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([]));
            async Task<(int Status, JsonElement Body)> Get(string route, string? path, string? id = null) { var answer = await router.RouteAsync("GET", $"/m1/workspaces/{id ?? workspace.Id}/{route}" + (path is null ? "" : "?path=" + Uri.EscapeDataString(path)), null, "phone", CancellationToken.None); return (answer.Status, Json(answer.Body)); }
            async Task Refused(string route, string? path, int status, string code, string? id = null) { var answer = await Get(route, path, id); Check(answer.Status == status && answer.Body.Text("code") == code && answer.Body.Text("error") is { Length: > 0 }, $"{route} {path} must be refused {status} {code}: {answer.Status} {answer.Body}"); }

            await Refused("file", "missing.txt", 404, "notFound"); await Refused("files", "missing", 404, "notFound"); await Refused("file", "missing/deeper.txt", 404, "notFound");
            await Refused("file", "../outside.txt", 403, "outsideWorkspace"); await Refused("file", "/etc/passwd", 403, "outsideWorkspace"); await Refused("file", "C:\\Windows\\win.ini", 403, "outsideWorkspace");
            await Refused("file", null, 400, "badPath"); await Refused("file", "src//app.py", 400, "badPath"); await Refused("files", "./src", 400, "badPath");
            await Refused("files", "notes.md", 400, "notDirectory"); await Refused("files", "", 404, "workspaceNotFound", "nowhere");
            Check(MobileWorkspaceFiles.NotFound.Message == Locale.Get("remote.files.notFound") && MobileWorkspaceFiles.Superseded is { Status: 409, Code: "superseded" } && MobileWorkspaceFiles.NotReadable is { Status: 403, Code: "notReadable" }, "a refusal has its code and its message in the user's language");

            var root = await Get("files", null);
            var rows = root.Body.GetProperty("entries").EnumerateArray().ToDictionary(e => e.Text("name")!);
            Check(root.Status == 200 && rows["src"].Text("kind") == "folder" && !rows["src"].TryGetProperty("size", out _) && rows["src"].Text("modified") is { Length: 20 } && rows["notes.md"].GetProperty("size").GetInt64() == 8 && rows["notes.md"].Text("relativePath") == "notes.md", "a listing's rows carry their kind, their date and a file's size: " + root.Body);
            var source = (await Get("file", "src/app.py")).Body;
            Check(source.Text("type") == "source" && source.Text("language") == "python" && source.Text("encoding") == "UTF-8" && source.GetProperty("lineCount").GetInt32() == 3 && !source.GetProperty("truncated").GetBoolean() && source.Text("text")!.Contains("안녕"), "a source file carries its language, encoding and line count: " + source);
            var markdown = (await Get("file", "notes.md")).Body;
            Check(markdown.Text("type") == "markdown" && !markdown.TryGetProperty("language", out _), "markdown has no language");
            var cut = (await Get("file", "long.txt")).Body;
            Check(cut.GetProperty("truncated").GetBoolean() && cut.Text("text")!.Length == MobileWorkspaceFiles.MaximumTextBytes && cut.GetProperty("size").GetInt64() > MobileWorkspaceFiles.MaximumTextBytes, "a text file is read up to 512 KiB and marked truncated: " + cut.Text("text")!.Length);
            Check((await Get("file", "blob.bin")).Body is var binary && binary.Text("type") == "unsupported" && binary.Text("reason") == "binary" && (await Get("file", "src")).Body.Text("reason") == "notRegularFile", "what cannot be shown says why in the contract's words");

            // Text that escaping would take over the reply limit is cut by a quarter at a time.
            var escaped = MobileWorkspaceFiles.Fitted(new MobileFilePreview(1, "w", "p", "n", 1, null, "source") { Text = new string('"', MobileWorkspaceFiles.MaximumTextBytes), Truncated = false, LineCount = 1 });
            Check(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(escaped, Wire.Json)) <= MobileWorkspaceFiles.MaximumReplyBytes && escaped is { Truncated: true, Text.Length: < MobileWorkspaceFiles.MaximumTextBytes }, "a reply stays under 768 KiB");
            var listing = MobileWorkspaceFiles.Fitted(new MobileFileListing(1, "w", "", [.. Enumerable.Range(0, 2_000).Select(i => new MobileFileEntry(new string('n', 300) + i, new string('p', 300) + i, "file", 1, null, false))], false));
            Check(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(listing, Wire.Json)) <= MobileWorkspaceFiles.MaximumReplyBytes && listing is { Truncated: true, Entries.Count: > 0 and < 2_000 }, "a listing drops rows from its end to fit");

            // Without a decoder a small picture travels as it is; with one, its thumbnail does.
            File.WriteAllBytes(Path.Combine(workspacePath, "dot.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
            File.WriteAllText(Path.Combine(workspacePath, "leak.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.invalid/a.png\"/></svg>");
            var raw = (await Get("file", "dot.png")).Body;
            Check(raw.Text("type") == "image" && raw.Text("mime") == "image/png" && raw.Text("data") is { Length: > 0 }, "a small picture travels without a decoder: " + raw);
            var asked = new List<string>();
            router.ImagePreview = (name, bytes, _) => { asked.Add(name); return Task.FromResult(name == "dot.png" ? new MobileImageResult(new("image/jpeg", "AAAA", 4_000, 3_000, MobileWorkspaceFiles.MaximumThumbnailPixels, 1_536)) : new MobileImageResult(null, "undecodable")); };
            var drawn = (await Get("file", "dot.png")).Body;
            Check(drawn.Text("mime") == "image/jpeg" && drawn.GetProperty("width").GetInt32() == 4_000 && drawn.GetProperty("thumbnailWidth").GetInt32() == 2_048 && drawn.Text("data") == "AAAA", "an image carries its thumbnail and both sizes");
            var leak = (await Get("file", "leak.svg")).Body;
            Check(leak.Text("type") == "unsupported" && leak.Text("reason") == "undecodable" && asked.SequenceEqual(["dot.png"]), "an svg that would load something besides itself is never drawn");
            Check(MobileWorkspaceFiles.MaximumThumbnailPixels == 2_048 && MobileWorkspaceFiles.ThumbnailLadder[0] == 2_048 && MobileWorkspaceFiles.ThumbnailLadder.SequenceEqual(MobileWorkspaceFiles.ThumbnailLadder.OrderDescending()) && MobileWorkspaceFiles.MaximumThumbnailBytes == 512 * 1_024, "the thumbnail's long side steps down from 2,048 px until it fits 512 KiB");

            // One decode at a time; a newer preview from the same phone takes an older one's place in the queue.
            var slot = new MobilePreviewSlot();
            Check(await slot.Acquire("a", CancellationToken.None), "the first preview decodes at once");
            var older = slot.Acquire("a", CancellationToken.None); var other = slot.Acquire("b", CancellationToken.None); var newer = slot.Acquire("a", CancellationToken.None);
            Check(!await older && slot.Waiting == 2 && !other.IsCompleted && !newer.IsCompleted, "a newer preview from the same phone drops the older one waiting");
            slot.Release(); Check(await other && !newer.IsCompleted, "the next in line decodes, one at a time");
            slot.Release(); Check(await newer, "then the newest of the first phone");
            using var gone = new CancellationTokenSource(); var abandoned = slot.Acquire("c", gone.Token); gone.Cancel();
            var cancelled = false; try { await abandoned; } catch (OperationCanceledException) { cancelled = true; }
            slot.Release(); Check(cancelled && slot.Waiting == 0 && await slot.Acquire("d", CancellationToken.None), "a phone that gave up leaves the queue, and the slot is free again");
            // The route: a preview superseded while it waited is refused with its code.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            router.ImagePreview = async (_, _, _) => { decoding.TrySetResult(); await gate.Task; return new(null, "undecodable"); };
            var held = Get("file", "dot.png"); await decoding.Task; var second = Get("file", "dot.png"); var third = Get("file", "dot.png");
            // Whichever of the two reached the queue first is the one the other replaced; it answers while the first still decodes.
            var replaced = await Task.WhenAny(second, third); var dropped = await replaced;
            Check(!held.IsCompleted && dropped.Status == 409 && dropped.Body.Text("code") == "superseded", "the preview a newer one replaced is refused as superseded: " + dropped.Status);
            gate.SetResult(); await held; Check((await (replaced == second ? third : second)).Status == 200, "the newest preview is answered");
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── the relay link ─────────────────────────────────────────────────────────

    internal static Task LinkPolicyDecidesWhenToRedial()
    {
        var wifi = new RelayNetworkPath(true, ["wifi"], ["192.168.0.1"]); var otherWifi = new RelayNetworkPath(true, ["wifi"], ["10.0.0.1"]);
        var vpn = new RelayNetworkPath(true, ["wifi", "vpn"], ["192.168.0.1"]); var none = new RelayNetworkPath(false, [], []);
        RelayLinkPolicy.PathAction Action(RelayNetworkPath? old, RelayNetworkPath? next, bool forced) => RelayLinkPolicy.Action(old, next, forced);
        Check(Action(null, wifi, false) == RelayLinkPolicy.PathAction.None && Action(null, none, false) == RelayLinkPolicy.PathAction.Offline, "the first report is only a baseline");
        Check(Action(wifi, otherWifi, false) == RelayLinkPolicy.PathAction.Reconnect && Action(wifi, vpn, false) == RelayLinkPolicy.PathAction.Reconnect && Action(none, wifi, false) == RelayLinkPolicy.PathAction.Reconnect && Action(wifi, new(true, ["wifi"], ["192.168.0.1"]), false) == RelayLinkPolicy.PathAction.None, "a different network redials, the same one does not");
        Check(Action(wifi, none, false) == RelayLinkPolicy.PathAction.Offline && Action(none, none, false) == RelayLinkPolicy.PathAction.None && Action(wifi, none, true) == RelayLinkPolicy.PathAction.Offline, "no network is offline, not a retry — also after a wake");
        Check(Action(wifi, null, true) == RelayLinkPolicy.PathAction.Reconnect && Action(wifi, wifi, true) == RelayLinkPolicy.PathAction.Reconnect && Action(wifi, null, false) == RelayLinkPolicy.PathAction.None, "a wake redials on the same network");

        var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        // Two routers on the same 192.168.0.1: wifi → nothing → wifi.
        var dropped = new RelayLinkPolicy.Window(); foreach (var path in new[] { wifi, none, wifi }) dropped.Report(path, wifi, now);
        var quiet = new RelayLinkPolicy.Window(); quiet.Report(wifi, wifi, now);
        var first = new RelayLinkPolicy.Window(); foreach (var path in new[] { otherWifi, wifi }) first.Report(path, null, now);
        Check(RelayLinkPolicy.Action(wifi, dropped) == RelayLinkPolicy.PathAction.Reconnect && RelayLinkPolicy.Action(wifi, quiet) == RelayLinkPolicy.PathAction.None && RelayLinkPolicy.Action(null, first) == RelayLinkPolicy.PathAction.None, "the window remembers a drop that ended on the same path; before any baseline nothing counts as a change");
        var woken = new RelayLinkPolicy.Window(); Check(woken.IsEmpty, "a new window is empty"); woken.Force(now);
        Check(!woken.IsEmpty && woken.Forced && woken.Latest is null && RelayLinkPolicy.Action(wifi, woken) == RelayLinkPolicy.PathAction.Reconnect, "a wake opens the window by itself");
        Check(quiet.Delay(now) == RelayLinkPolicy.PathSettle && quiet.Delay(now.AddSeconds(4)) == TimeSpan.FromSeconds(1) && quiet.Delay(now + RelayLinkPolicy.PathSettleMaxWait + TimeSpan.FromSeconds(1)) == TimeSpan.Zero, "a stream of reports still settles after five seconds");

        var pacing = new RelayLinkPolicy.Pacing();
        Check(pacing.Wait(now) == TimeSpan.Zero, "the first redial is not paced");
        pacing.Record(now); Check(pacing.Wait(now.AddSeconds(1)) == TimeSpan.FromSeconds(4), "the next waits out the five-second gap");
        pacing.Record(now.AddSeconds(5)); Check(pacing.Wait(now.AddSeconds(5)) == TimeSpan.FromSeconds(10), "the gap doubles while redials keep coming");
        pacing.Record(now.AddSeconds(15)); Check(pacing.Wait(now.AddSeconds(15)) == TimeSpan.FromSeconds(20), "and doubles again");
        pacing.Record(now.AddSeconds(35)); Check(pacing.Wait(now.AddSeconds(35)) == RelayLinkPolicy.RestartGapMax, "up to thirty seconds");
        pacing.Record(now.AddSeconds(65)); Check(pacing.Wait(now.AddSeconds(65)) == RelayLinkPolicy.RestartGapMax, "where it stays");
        pacing.Record(now.AddSeconds(200)); Check(pacing.Wait(now.AddSeconds(200)) == RelayLinkPolicy.RestartGapMin, "calm for a while: back to the shortest gap");
        Check(RelayLinkPolicy.KeepaliveInterval == TimeSpan.FromSeconds(20) && RelayLinkPolicy.KeepaliveTimeout == TimeSpan.FromSeconds(10) && RelayLinkPolicy.PathSettle == TimeSpan.FromSeconds(1.5), "the control socket is pinged every 20 s with 10 s to answer; a path settles in 1.5 s");
        var current = RelayNetworkPath.Current();
        Check(current is not null && current.Interfaces.SequenceEqual(current.Interfaces.Order(StringComparer.Ordinal)) && current == RelayNetworkPath.Current(), "the path Windows reports reads the same twice");
        // Only adapters that can carry the socket out make the path: one with a default gateway, loopback and tunnels aside.
        static IReadOnlyList<IPAddress> Via(params string[] gateways) => gateways.Select(IPAddress.Parse).ToArray();
        (string, System.Net.NetworkInformation.NetworkInterfaceType, IReadOnlyList<IPAddress>)[] adapters = [("wifi", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, Via("192.168.0.1", "fe80::1")), ("hyper-v", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, Via()),
            ("docker", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, Via("0.0.0.0")), ("teredo", System.Net.NetworkInformation.NetworkInterfaceType.Tunnel, Via("2001::1")), ("loopback", System.Net.NetworkInformation.NetworkInterfaceType.Loopback, Via("127.0.0.1"))];
        var carried = RelayNetworkPath.From(adapters, true);
        Check(carried == new RelayNetworkPath(true, ["wifi"], ["192.168.0.1", "fe80::1"]), "a switch with no gateway, a tunnel and loopback are no part of the path");
        Check(RelayNetworkPath.From([.. adapters, ("wsl", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, Via())], true) == carried && RelayNetworkPath.From([.. adapters, ("vpn", System.Net.NetworkInformation.NetworkInterfaceType.Ppp, Via("10.8.0.1"))], true) != carried, "starting WSL or Docker is no new path; a VPN that takes the default route is");
        Check(!RelayNetworkPath.From(adapters[1..], true).Satisfied && !RelayNetworkPath.From(adapters, false).Satisfied, "no adapter with a gateway, or no network at all, is offline");

        MobileRelayStatus Status(string detail, string? error = null, int? retry = null) => new(false, 0, detail, error, retry);
        Check(MobileRelayStatusText.Detail(Status("disabled")) == Locale.Get("mobileRemote.status.off") && MobileRelayStatusText.Detail(Status("connecting")) == Locale.Get("mobileRemote.status.connecting") && MobileRelayStatusText.Detail(new(true, 1, "connected")) == Locale.Get("mobileRemote.status.scanQR") && MobileRelayStatusText.Detail(Status("offline")) == Locale.Get("settings.mobileRemote.detail.networkOffline"), "off, connecting, connected and offline each have their line");
        var retry = L("mobileRemote.status.retryIn", ("seconds", "8"));
        Check(MobileRelayStatusText.Detail(Status("reconnecting", null, 8)) == Locale.Get("mobileRemote.status.disconnected") + " " + retry && MobileRelayStatusText.Detail(Status("reconnecting", "host-id-taken", 8)) == Locale.Get("mobileRemote.error.hostIdTaken") + " · " + retry, "a lost link says when it dials again, after why the relay refused it");
        Check(new[] { "rejected", "closed", "unresponsive", "connect-failed" }.Select(MobileRelayStatusText.Reason).SequenceEqual([Locale.Get("mobileRemote.error.relayRejected"), Locale.Get("mobileRemote.error.relayClosed"), Locale.Get("settings.mobileRemote.detail.relayUnresponsive"), Locale.Get("mobileRemote.error.relayConnectFailed")]) && MobileRelayStatusText.Reason(null) is null, "every reason has its words");
        return Task.CompletedTask;
    }

    /// <summary>A relay that only counts control sockets: <paramref name="serve"/> is what each one does once it is open.</summary>
    private static async Task WithRelay(Func<int, WebSocket, CancellationToken, Task> serve, Func<string, Func<int>, CancellationToken, Task> body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var controls = 0;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }); builder.Logging.ClearProviders(); builder.WebHost.UseKestrelCore(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets();
        app.Run(async context =>
        {
            try { using var socket = await context.WebSockets.AcceptWebSocketAsync(); if (!context.Request.Query.ContainsKey("connectionId")) await serve(Interlocked.Increment(ref controls), socket, timeout.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        });
        try { await app.StartAsync(timeout.Token); await body(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(), () => Volatile.Read(ref controls), timeout.Token); }
        finally { timeout.Cancel(); await app.StopAsync(CancellationToken.None); }
    }
    private static async Task Listen(WebSocket socket, CancellationToken token) { var buffer = new byte[1_024]; while ((await socket.ReceiveAsync(buffer, token)).MessageType != WebSocketMessageType.Close) { } }

    internal static async Task TheLinkRedialsAfterAWakeAndWaitsOutNoNetwork()
    {
        var directory = Verification.Temp();
        try
        {
            await WithRelay((_, socket, token) => Listen(socket, token), async (address, controls, token) =>
            {
                var seen = new List<MobileRelayStatus>();
                await using var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, _, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1 }))) { WatchesNetwork = false, PathSettle = TimeSpan.FromMilliseconds(60) };
                host.StatusChanged += status => { lock (seen) seen.Add(status); };
                host.ReconnectSoon(); host.NetworkPathChanged(new(false, [], []));
                await host.ApplyAsync(new(true, address, false, DirectEnabled: false)); await Verification.Until(() => host.Status.Connected && controls() == 1);
                Check(host.Status.Detail == "connected" && seen.Any(s => s.Detail == "connecting"), "the host says it is connecting, then connected; nothing reported while it was off is acted on");
                // A wake dials again on the same network, once the path has settled.
                host.ReconnectSoon(); host.ReconnectSoon(); await Verification.Until(() => controls() == 2 && host.Status.Connected);
                await Task.Delay(250); Check(controls() == 2, "two wakes within the settle are one redial");
                // No network: the host stops dialling and says so.
                host.NetworkPathChanged(new(false, [], [])); await Verification.Until(() => host.Status.Detail == "offline");
                await Task.Delay(1_300); Check(!host.Status.Connected && controls() == 2 && MobileRelayStatusText.Detail(host.Status) == Locale.Get("settings.mobileRemote.detail.networkOffline"), "without a network nothing is dialled");
                // The network returns: dialled at once.
                host.NetworkPathChanged(new(true, ["wifi"], ["192.168.0.1"])); await Verification.Until(() => controls() == 3 && host.Status.Connected);
                // The same network again is no reason to redial; another one is, and so is a drop that ended where it began.
                host.NetworkPathChanged(new(true, ["wifi"], ["192.168.0.1"])); await Task.Delay(300); Check(controls() == 3, "the same path redials nothing");
                await host.ApplyAsync(new(false, address, false)); host.ReconnectSoon(); await Task.Delay(200);
                Check(controls() == 3 && host.Status.Detail == "disabled", "a host that was turned off is not woken");
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task AHalfDeadControlSocketIsReplaced()
    {
        var directory = Verification.Temp();
        try
        {
            // The first control socket is accepted and then never read, so its pings go unanswered — a socket the network left half-open.
            await WithRelay((number, socket, token) => number == 1 ? Task.Delay(Timeout.Infinite, token) : Listen(socket, token), async (address, controls, token) =>
            {
                var seen = new List<MobileRelayStatus>();
                await using var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, _, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1 }))) { WatchesNetwork = false, KeepaliveInterval = TimeSpan.FromMilliseconds(300), KeepaliveTimeout = TimeSpan.FromMilliseconds(300) };
                host.StatusChanged += status => { lock (seen) seen.Add(status); };
                await host.ApplyAsync(new(true, address, false, DirectEnabled: false));
                await Verification.Until(() => controls() == 2 && host.Status.Connected, 15_000);
                MobileRelayStatus? lost; lock (seen) lost = seen.FirstOrDefault(s => s.Detail == "reconnecting");
                Check(lost is { Connected: false, Error: "unresponsive", RetrySeconds: 1 }, "an unanswered ping closes the socket, and the host says the relay did not answer and when it dials again: " + lost);
                Check(MobileRelayStatusText.Detail(lost!) == Locale.Get("settings.mobileRemote.detail.relayUnresponsive") + " · " + L("mobileRemote.status.retryIn", ("seconds", "1")), "in the Mac's words");
            });
            // Nobody listening at all: the relay could not be reached, and the wait doubles.
            var refused = new List<MobileRelayStatus>();
            await using var alone = new MobileRelayHost(Path.Combine(directory, "alone"), "Fixture", [], (_, _, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1 }))) { WatchesNetwork = false };
            alone.StatusChanged += status => { lock (refused) refused.Add(status); };
            await alone.ApplyAsync(new(true, "ws://127.0.0.1:1", false, DirectEnabled: false));
            await Verification.Until(() => { lock (refused) return refused.Count(s => s.Detail == "reconnecting") >= 2; }, 20_000);
            lock (refused) Check(refused.Where(s => s.Detail == "reconnecting").Take(2).Select(s => (s.Error, s.RetrySeconds)).SequenceEqual([("connect-failed", 1), ("connect-failed", 2)]), "a relay that cannot be reached is retried after 1 s, then 2 s");
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── settings › mobile ──────────────────────────────────────────────────────

    internal static Task TheDeviceListMarksArrivalsAndNamesTheLegacyRow()
    {
        var directory = Verification.Temp();
        try
        {
            var identity = new MobileIdentity(directory); var link = identity.PairingUrl(MobileRelayHost.DefaultRelay, "Fixture");
            var key = Uri.UnescapeDataString(new Uri(link).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("key=", StringComparison.Ordinal))[4..]);
            Check(identity.Authenticate(Json(new { type = "auth", pairingKey = key }), true).DeviceId == "legacy" && identity.Authenticate(Json(new { type = "auth", pairingKey = key, clientId = "abcdefghijklmnopqrstuv" }), true).DeviceId is not null, "an old app and a phone pair");
            var legacy = identity.Devices.Single(d => d.Id == "legacy"); var phone = identity.Devices.Single(d => d.Id != "legacy");
            Check(legacy is { Legacy: true, IsNew: true, Connected: false } && legacy.Name == Locale.Get("mobileRemote.device.legacyName"), "the row that stands for every app without a token is named in the user's language: " + legacy.Name);
            Check(phone is { Legacy: false, IsNew: true } && phone.Name == Locale.Get("mobileRemote.device.defaultName"), "a phone that gave no name gets the default one: " + phone.Name);
            var now = DateTimeOffset.UtcNow;
            Check(MobileIdentity.IsNew(now.AddHours(-23).ToString("O"), now) && !MobileIdentity.IsNew(now.AddHours(-25).ToString("O"), now) && !MobileIdentity.IsNew(now.AddMinutes(5).ToString("O"), now) && !MobileIdentity.IsNew("not a date", now), "an arrival is marked for a day");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    // ── a reply that is too large ──────────────────────────────────────────────

    internal static async Task ReplyLimitsAreTheMacs()
    {
        Check(MobileDesktopRouter.MaximumReplyBytes == 1_024 * 1_024 - 32 * 1_024 && MobileWire.MaximumReplyBytes == MobileDesktopRouter.MaximumReplyBytes, "a session reply may be 1 MiB less 32 KiB, as on the Mac");
        Check(MobileRelayHost.MaximumPlaintext == 1_024 * 1_024 - 28 && MobileDesktopRouter.MaximumReplyBytes + 4_096 < MobileRelayHost.MaximumPlaintext && MobileWorkspaceFiles.MaximumReplyBytes == 768 * 1_024, "the envelope and the seal fit the relay's 1 MiB frame; a file reply keeps its own 768 KiB");
        var directory = Verification.Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath);
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "s", WorkspaceId = workspace.Id }] }); var filler = "";
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([], Settings: new { filler }));
            async Task<int> Size(int bytes) { filler = new string('x', bytes); return (await router.RouteAsync("GET", "/m1/sessions/s", null, "phone", CancellationToken.None)).Status; }
            Check(await Size(900 * 1_024) == 200, "a reply the Windows host used to refuse at 768 KiB now travels");
            Check(await Size(MobileDesktopRouter.MaximumReplyBytes) == 413, "a reply over the limit is refused whole rather than sent for the relay to close the connection on");
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── screen viewing ─────────────────────────────────────────────────────────

    private sealed class Screen : IScreenSharePlatform
    {
        internal List<ScreenDisplay> Attached = [new(1, 1920, 1080, true), new(2, 1280, 1024, false, -1280, 0)];
        internal readonly List<int> Switched = []; internal readonly List<string> Stopped = [];
        internal int Halts, Vanish; internal bool FailSwitch;
        public bool Available => true;
        public IReadOnlyList<ScreenDisplay> Displays => Attached.ToArray();
        public ScreenSafety Safety { get; set; } = new(false, false, DateTimeOffset.UtcNow.AddMinutes(-1));
        public Task StartPeerAsync(ScreenLiveSession session, IReadOnlyList<ScreenIceServer> ice, CancellationToken token) => Task.CompletedTask;
        public Task ReceiveSignalAsync(string id, JsonElement signal, CancellationToken token) => Task.CompletedTask;
        public Task StopPeerAsync(string id) { Stopped.Add(id); return Task.CompletedTask; }
        public void Halt() => Halts++;
        /// <summary><see cref="Vanish"/> displays go away just as capture moves to them, as undocking takes one monitor after another.</summary>
        public Task<bool> SetDisplayAsync(int id, CancellationToken token)
        {
            if (FailSwitch) throw new IOException("capture did not start");
            if (Vanish > 0) { Vanish--; Attached.RemoveAll(d => d.DisplayId == id); return Task.FromResult(false); }
            Switched.Add(id); return Task.FromResult(true);
        }
        public void SetRegion(ScreenRegion region) { }
        public Task InjectAsync(string id, JsonElement value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> ShowMarkerAsync(string id, JsonElement value, CancellationToken token) => Task.FromResult(true);
        public Task ReleaseButtonsAsync(string id) => Task.CompletedTask;
        public Task<string?> ReadClipboardAsync(CancellationToken token) => Task.FromResult<string?>("");
        public Task WriteClipboardAsync(string text, CancellationToken token) => Task.CompletedTask;
        public Task SendDataAsync(string id, object value, CancellationToken token) => Task.CompletedTask;
    }

    internal static async Task ADisplayThatDisappearsMovesItsViewersToTheMainOne()
    {
        const string first = "abcdefghijklmnopqrstuv", second = "bbbbbbbbbbbbbbbbbbbbbb";
        ScreenDisplay[] both = [new(1, 1920, 1080, true), new(2, 1280, 1024, false)];
        Check(ScreenSharePolicy.DisplayFor(both, 2)!.DisplayId == 2 && ScreenSharePolicy.DisplayFor(both, 7)!.DisplayId == 1 && ScreenSharePolicy.DisplayFor([both[1]], 7)!.DisplayId == 2 && ScreenSharePolicy.DisplayFor([], 1) is null, "a display that is not attached reads as the main one; only no display at all is none");
        var directory = Verification.Temp();
        try
        {
            var platform = new Screen(); var sent = new List<(string Device, JsonElement Message)>();
            await using var hub = new ScreenShareHub(directory, platform, (device, message) => { lock (sent) sent.Add((device, Json(message))); return Task.CompletedTask; }, (_, _, _) => Task.FromResult(true), _ => true);
            await hub.SetGrantAsync(first, true, "view"); await hub.SetGrantAsync(second, true, "view");
            async Task<JsonElement> Start(string phone, int display) { var reply = await hub.RouteAsync("POST", "sessions", Json(new { mode = "view", network = "wifi", displayId = display, decodes = new[] { "H264" } }), phone, default); Check(reply.Status == 200, "a view session starts"); await reply.AfterSend!(); return Json(reply.Body); }
            var one = await Start(first, 2); var two = await Start(second, 1);
            Check(one.GetProperty("displayId").GetInt32() == 2 && two.GetProperty("displayId").GetInt32() == 2, "one capture serves every phone: a phone that joins sees the display already captured");
            lock (sent) sent.Clear();
            // The second display is unplugged.
            platform.Attached.RemoveAt(1); await hub.DisplayLostAsync();
            Check(hub.Sessions.Count == 2 && hub.Sessions.All(s => s.DisplayId == 1) && platform.Switched.SequenceEqual([1]) && platform.Halts == 0 && platform.Stopped.Count == 0, "the viewers are moved to the main display, not stopped");
            lock (sent) Check(sent.Count == 2 && sent.All(m => m.Message.Text("type") == "screen-grant" && m.Message.Text("grant") == "view" && m.Message.GetProperty("allowed").GetBoolean() && m.Message.GetProperty("displays").GetArrayLength() == 1) && sent.Select(m => m.Device).Order().SequenceEqual([first, second]), "each phone is told the displays there are now, with its own grant");
            // The stream was taken away but its display is still there: capture starts on it again.
            await hub.DisplayLostAsync(); Check(platform.Switched.SequenceEqual([1, 1]) && hub.Sessions.Count == 2, "a stream Windows took away is started again on the same display");
            // A locked PC is never captured, and capture has already stopped: the sessions end at once, as the watcher would end them.
            platform.Safety = new(true, false, DateTimeOffset.UtcNow); await hub.DisplayLostAsync(); Check(platform.Switched.Count == 2 && hub.Sessions.Count == 0, "a locked PC is never switched to, and nobody is left watching a halted capture");
            platform.Safety = new(false, false, DateTimeOffset.UtcNow.AddMinutes(-1)); lock (sent) Check(sent.Where(m => m.Message.Text("type") == "screen-kill").All(m => m.Message.Text("reason") == "lock-screen") && sent.Count(m => m.Message.Text("type") == "screen-kill") == 2, "locking still ends every session, as a kill that says why");

            // Capture cannot start on what is left: the sessions end, and say the display is gone.
            await Start(first, 1); lock (sent) sent.Clear(); platform.FailSwitch = true; await hub.DisplayLostAsync();
            lock (sent) Check(hub.Sessions.Count == 0 && sent.Single(m => m.Message.Text("type") is "screen-session-end" or "screen-kill") is var ended && ended.Message.Text("type") == "screen-session-end" && ended.Message.Text("reason") == "display-gone", "with nowhere to capture the session ends as display-gone, an ordinary end and not a kill");
            platform.FailSwitch = false; await Start(first, 1); platform.Attached.Clear(); lock (sent) sent.Clear(); await hub.DisplayLostAsync();
            lock (sent) Check(hub.Sessions.Count == 0 && sent.Any(m => m.Message.Text("reason") == "display-gone"), "and so it does with no display left");
            await hub.DisplayLostAsync(); Check(hub.Sessions.Count == 0, "with nobody watching a lost display is nothing");
            bool Ended() { lock (sent) return sent.Single(m => m.Message.Text("type") is "screen-session-end" or "screen-kill") is var ended && ended.Message.Text("type") == "screen-session-end" && ended.Message.Text("reason") == "display-gone"; }

            // Undocking takes one monitor after another: the display capture moves to may go as well, and the next is picked once.
            platform.Attached = [new(1, 1920, 1080, true), new(2, 1280, 1024, false), new(3, 800, 600, false)]; platform.Switched.Clear();
            await Start(first, 3); platform.Attached.RemoveAt(2); platform.Vanish = 1; lock (sent) sent.Clear(); await hub.DisplayLostAsync();
            lock (sent) Check(platform.Switched.SequenceEqual([2]) && hub.Sessions.Single().DisplayId == 2 && sent.Single().Message.Text("type") == "screen-grant", "a display that went away while capture moved to it is passed over for the next one");
            platform.Attached.Insert(0, new(1, 1920, 1080, true)); platform.Vanish = 2; lock (sent) sent.Clear(); await hub.DisplayLostAsync();
            Check(hub.Sessions.Count == 0 && platform.Switched.SequenceEqual([2]) && platform.Vanish == 0 && Ended(), "when the one picked next goes too, the sessions end as display-gone rather than watch a halted capture");

            // A capture that keeps ending is started again three times within ten seconds, not a fourth.
            var recent = new List<DateTimeOffset>(); var at = DateTimeOffset.UnixEpoch;
            Check(ScreenSharePolicy.MayRestartCapture(recent, at) && ScreenSharePolicy.MayRestartCapture(recent, at.AddSeconds(1)) && ScreenSharePolicy.MayRestartCapture(recent, at.AddSeconds(2)) && !ScreenSharePolicy.MayRestartCapture(recent, at.AddSeconds(9)) && ScreenSharePolicy.MayRestartCapture(recent, at.AddSeconds(10)), "three restarts in ten seconds, then another once the first has aged out");
            platform.Attached = [new(1, 1920, 1080, true)]; await Start(first, 1); platform.Switched.Clear();
            for (var restart = 0; restart < ScreenSharePolicy.CaptureRestarts; restart++) await hub.DisplayLostAsync();
            Check(platform.Switched.Count == 3 && hub.Sessions.Count == 1, "a stream that ends now and then is started again each time");
            lock (sent) sent.Clear(); await hub.DisplayLostAsync();
            Check(hub.Sessions.Count == 0 && platform.Switched.Count == 3 && Ended(), "one that keeps ending is not started a fourth time: the sessions end as display-gone");
            // A frame that fails is not the stream ending: capture is not started again.
            await Start(first, 1); lock (sent) sent.Clear(); await hub.CaptureFailedAsync();
            Check(hub.Sessions.Count == 0 && platform.Switched.Count == 3 && Ended(), "failed capture ends the sessions as display-gone without starting it again");
        }
        finally { Directory.Delete(directory, true); }
    }
}
