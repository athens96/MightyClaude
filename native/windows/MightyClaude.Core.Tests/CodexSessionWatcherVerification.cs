using System.Collections.Concurrent;
using System.Text.Json;
using MightyClaude.Core;

internal static class CodexSessionWatcherVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static readonly DateTimeOffset Started = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Root = "10000000-0000-4000-8000-000000000001", Child = "10000000-0000-4000-8000-000000000002", Grandchild = "10000000-0000-4000-8000-000000000003", Other = "10000000-0000-4000-8000-000000000004";
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static string Record(string type, object payload, int ordinal = 10, DateTimeOffset? at = null) => JsonSerializer.Serialize(new { timestamp = (at ?? Started).ToString("O"), ordinal, type, payload });
    private static string Metadata(string id, string parent, bool fork = false, int? ordinal = null, bool guardian = false) => Record("session_meta", new
    {
        id, parent_thread_id = parent, agent_nickname = "Worker", agent_path = "/root/worker", forked_from_id = fork ? parent : null,
        subagent_history_start_ordinal = ordinal,
        source = guardian ? (object)new { subagent = "review" } : new { subagent = new { thread_spawn = new { parent_thread_id = parent } } }
    });
    private static string Start(int ordinal = 10, DateTimeOffset? at = null) => Record("event_msg", new { type = "task_started" }, ordinal, at);
    private static string Done(string answer, int ordinal = 20) => Record("event_msg", new { type = "task_complete", last_agent_message = answer }, ordinal);
    private static string Message(string text, string id = "note", int ordinal = 15) => Record("event_msg", new { type = "item_completed", item = new { type = "AgentMessage", id, phase = "commentary", content = new[] { new { type = "Text", text } } } }, ordinal);
    private sealed class Fixture : IDisposable
    {
        internal readonly string Home = Verification.Temp();
        internal string Folder { get; }
        internal Fixture() { Folder = Path.Combine(Home, "sessions", Started.LocalDateTime.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(Folder); }
        internal string Write(string id, params string[] lines)
        {
            var path = Path.Combine(Folder, "rollout-2026-10-01T12-00-00-" + id + ".jsonl");
            File.WriteAllText(path, string.Join('\n', lines) + "\n"); File.SetLastWriteTimeUtc(path, Started.AddSeconds(3).UtcDateTime); return path;
        }
        internal CodexSessionWatcher Watch(CodexSessionWatcher.Limits? limits = null) => new(Home, Root, Started, "watch-run", () => Started.AddMinutes(5), limits);
        public void Dispose() => Directory.Delete(Home, true);
    }
    internal static Task NestedChildrenStayScopedAndPublishLiveModels()
    {
        using var f = new Fixture();
        f.Write(Grandchild, Metadata(Grandchild, Child), Start(), Message("nested reasoning"), Done("nested result"));
        f.Write(Other, Metadata(Other, Guid.NewGuid().ToString()), Start(), Done("unrelated secret"));
        f.Write(Child, Metadata(Child, Root), Start(at: Started.AddHours(-1)), Message("old private answer", "old-message"), Done("old answer"), Start(),
            Record("turn_context", new { model = "gpt-fixture" }),
            Record("response_item", new { type = "agent_message", recipient = "/root/worker", id = "input", content = new[] { new { type = "input_text", text = "Inspect Windows" } } }),
            Record("token_usage_record", new { response_id = "response-1", usage = new { input_tokens = 20, output_tokens = 4, cached_input_tokens = 8 } }),
            Record("token_usage_record", new { response_id = "response-1", usage = new { input_tokens = 20, output_tokens = 4, cached_input_tokens = 8 } }), Message("working now"));
        using var watcher = f.Watch(); var updates = watcher.Poll();
        Check(updates.Count == 2 && updates.All(a => a.Thread != Other), "only proven root descendants enter this run, even when grandchild is discovered first; found " + updates.Count + ", headers " + watcher.FirstLineReads + ", folder " + f.Folder);
        var child = updates.Single(a => a.Thread == Child);
        Check(child.Input == "Inspect Windows" && child.Turns == 1 && child.Usage.Count == 1 && child.Usage[0].Model == "gpt-fixture", "fresh input and per-response model usage are deduplicated");
        Check(!child.Entries.Any(e => e.Text.Contains("old")), "pre-run transcripts are never replayed");
        var tracker = new ExecutionGraphTracker("watch-run", "root prompt", "codex", null, _ => { });
        tracker.Consume(Element(new { type = "thread.started", thread_id = Root }));
        foreach (var update in updates) tracker.CodexSession(update);
        var graph = tracker.BuildRun(includeRunning: true)!;
        Check(graph.Status == "running" && graph.Agents.Count == 2 && graph.Agents.Single(a => a.Entries.Any(e => e.Text == "nested result")).ParentID == graph.Agents.Single(a => a.Input == "Inspect Windows").Id, "live graph preserves nested parents before Finish");
        Check(watcher.Poll().Count == 0, "unchanged poll emits no duplicate transcript or usage");
        return Task.CompletedTask;
    }
    internal static Task ForksDoNotExposeInheritedHistoryOrSealedMessages()
    {
        using var f = new Fixture();
        f.Write(Child, Metadata(Child, Root, true, 50), Start(10), Message("copied parent secret", ordinal: 11), Start(50), Message("own note", ordinal: 51),
            Record("response_item", new { type = "agent_message", recipient = "/root/worker", content = new[] { new { type = "input_text", text = "Sender: parent\nMessage:\n" } } }, 52),
            Record("response_item", new { type = "agent_message", recipient = "/root/worker", content = new[] { new { type = "input_text", text = "gAAAAsealed" } } }, 53), Done("own result", 54));
        f.Write(Grandchild, Metadata(Grandchild, Child, true), Start(), Message("copied history"), Record("event_msg", new { type = "thread_settings_applied" }), Start(), Message("must stay private"), Done("must stay hidden"));
        using var watcher = f.Watch(); var updates = watcher.Poll();
        var child = updates.Single(a => a.Thread == Child); var fork = updates.Single(a => a.Thread == Grandchild);
        Check(child.Input is null && child.Output == "own result" && child.Entries.Count == 1 && child.Entries[0].Text == "own note", "ordinal boundary, sealed body and envelope header are honored");
        Check(fork.State == "completed" && fork.Output is null && fork.Input is null && fork.Entries.Count == 0 && fork.Usage.Count == 0, "legacy forks without trustworthy ordinals expose state only");
        return Task.CompletedTask;
    }
    internal static Task PartialOversizedAndTruncatedRecordsRemainBounded()
    {
        using var f = new Fixture(); var path = f.Write(Child, Metadata(Child, Root), Start());
        using var watcher = f.Watch(new(MaximumLineBytes: 512));
        Check(watcher.Poll().Single().State == "running", "first complete start is visible");
        var done = Done("한글 result"); File.AppendAllText(path, done[..^1]);
        Check(watcher.Poll().Count == 0, "partial JSON record never consumes its offset");
        File.AppendAllText(path, "}\n"); Check(watcher.Poll().Single().Output == "한글 result", "completed UTF-8 line is read exactly once");
        File.AppendAllText(path, new string('x', 1300) + "\n" + Start(30) + "\n" + Done("after oversized", 31) + "\n");
        Check(watcher.Poll().Single().Output == "after oversized", "oversized line is skipped whole and next valid record remains readable");
        File.WriteAllText(path, Metadata(Child, Root) + "\n" + Start() + "\n" + Done("replacement must not be read") + "\n");
        Check(watcher.Poll().Count == 0, "truncated adopted file is permanently closed");
        watcher.Finish(); Check(watcher.Poll().Count == 0, "final read closes every later poll");
        return Task.CompletedTask;
    }
    internal static Task UntrustedMetadataAndLinksNeverBecomeChildren()
    {
        using var f = new Fixture();
        f.Write(Child, Metadata(Other, Root), Start(), Done("wrong filename identity"));
        f.Write(Grandchild, Metadata(Grandchild, Root, guardian: true), Start(), Done("guardian internal"));
        var outside = Path.Combine(f.Home, "outside.jsonl"); File.WriteAllText(outside, Metadata(Other, Root) + "\n" + Start() + "\n" + Done("linked private") + "\n");
        var link = Path.Combine(f.Folder, "rollout-2026-10-01T12-00-00-" + Other + ".jsonl");
        try { File.CreateSymbolicLink(link, outside); } catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { }
        using var watcher = f.Watch(); Check(watcher.Poll().Count == 0, "filename mismatch, guardian sources and symlinks are refused");
        return Task.CompletedTask;
    }
    internal static Task ReusedChildKeepsPreviousAnswersAndResponseUsageOnce()
    {
        using var f = new Fixture(); var path = f.Write(Child, Metadata(Child, Root), Start(), Done("first answer"));
        using var watcher = f.Watch(); var tracker = new ExecutionGraphTracker("watch-run", "request", "codex", null, _ => { });
        tracker.Consume(Element(new { type = "thread.started", thread_id = Root }));
        foreach (var child in watcher.Poll()) tracker.CodexSession(child);
        File.AppendAllText(path, Start(30) + "\n" + Record("event_msg", new { type = "error", message = "tool failed" }, 31) + "\n");
        foreach (var child in watcher.Poll()) tracker.CodexSession(child);
        var resumed = tracker.BuildRun(true)!.Agents.Single();
        Check(resumed.Status == "running" && resumed.ActivityGeneration == 1 && resumed.Entries.Count(e => e.Text == "first answer") == 1, "reopened child increments generation and preserves one prior answer");
        foreach (var child in watcher.Finish()) tracker.CodexSession(child);
        Check(tracker.BuildRun(true)!.Agents.Single().Status == "error", "unfinished child with actual error settles as failed");
        return Task.CompletedTask;
    }
    internal static async Task RunningProcessPublishesGraphBeforeItExits()
    {
        var directory = Verification.Temp(); var workspace = new Workspace { Path = directory }; var events = new ConcurrentQueue<RunEvent>();
        await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "codex", Path.Combine(directory, "fake"), "--hold-run")));
        await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue);
        try
        {
            await manager.StartAsync(new("live-graph", workspace.Id, "claude", "show live progress", [], Provider: "codex"));
            await Verification.Until(() => events.Any(e => e.GraphRun?.Status == "running" && e.GraphRun.FinalOutput == "FAKE_CLI_OK"));
            Check(manager.IsRunning("live-graph") && !events.Any(e => e.Status is "completed" or "error" or "stopped"), "live graph reaches the host before process completion");
            await manager.StopAsync("live-graph");
            Check(events.Last(e => e.Type == "status").Status == "stopped" && events.Last(e => e.GraphRun is not null).GraphRun!.Status == "stopped", "stop publishes one final settled graph and cancels polling");
        }
        finally { await manager.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
