using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MightyClaude.Core;

/// Background Ouroboros executions in the Mighty graph (macOS OuroborosExecutionTests): which tool results name one,
/// what the loopback dashboard's payloads become, when a block is read again, and what the pane's monitor shows.
internal static class OuroborosExecutionVerification
{
    private const string ClaudeStart = "mcp__plugin_ouroboros_ouroboros__ouroboros_start_execute_seed";
    private const string StartText = "Started background execution.\n\nJob ID: job_6bb930bd8b98\nSession ID: orch_1234\nExecution ID: exec_abc123\n\nRuntime Backend: claude\nLive Dashboard: http://localhost:8123/?run=exec_abc123\nUse ouroboros_ac_tree_hud(session_id, cursor) for live progress.\n";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static LogEntry Entry(string id, string tool, string? output, string state = "completed", string timestamp = "2026-09-29T00:00:00Z") =>
        new(id, "system", tool, timestamp, "claude", new AgentActivity("activity-" + id, "claude", "tool", state, tool, tool, output));
    private static MightyGraphRun Run(string id, params LogEntry[] entries) => new() { Id = id, Input = "request", Status = "completed", RootEntries = [.. entries] };
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static OuroborosExecutionSnapshot Snapshot(OuroborosExecutionSnapshot.Sources source, OuroborosExecutionStatus status = OuroborosExecutionStatus.Unknown, int? completed = null, int? total = null) =>
        new(source, status, Completed: completed, Total: total);

    // ── links ─────────────────────────────────────────────────────────────────

    internal static Task StartToolResultsNameTheirExecutions()
    {
        var link = OuroborosExecutionLinks.Extract([Run("run-1", Entry("a", ClaudeStart, StartText))]).Single();
        Check(link is { RunID: "run-1", Tool: "ouroboros_start_execute_seed", JobID: "job_6bb930bd8b98", SessionID: "orch_1234", ExecutionID: "exec_abc123", Key: "exec_abc123", Timestamp: "2026-09-29T00:00:00Z" }
            && link.DashboardURL?.OriginalString == "http://localhost:8123/?run=exec_abc123", "a start names its job, session, execution and loopback dashboard: " + link);
        // Only the four start tools, and only their settled results.
        var status = Entry("s", "mcp__plugin_ouroboros_ouroboros__ouroboros_job_status", "**Execution ID**: exec_other\nExecution ID: exec_other");
        var none = Run("run-1", status, Entry("f", ClaudeStart, StartText, "error"), Entry("r", ClaudeStart, null, "running"), Entry("l", "mcp__x__not_ouroboros_start_execute_seed", StartText));
        Check(OuroborosExecutionLinks.Extract([none]).Count == 0, "another tool, a failed or running start and a lookalike name nothing");
        // Codex names, sub-agent starts and job-only starts, once per execution on the first run that reported it.
        var auto = Entry("auto", "ouroboros.ouroboros_start_auto", "Started background auto session.\n\nStatus: queued\njob_id: job_auto1\nauto_session_id: auto_9\nLive Dashboard: http://127.0.0.1:9000\n");
        var first = new MightyGraphRun { Id = "run-1", Input = "x", Status = "completed", RootEntries = [auto], Agents = [new MightyGraphAgent { Id = "child", Status = "completed", Entries = [Entry("b", ClaudeStart, StartText)] }] };
        var links = OuroborosExecutionLinks.Extract([first, Run("run-2", Entry("c", ClaudeStart, StartText))]);
        Check(links.Select(l => l.Key).SequenceEqual(["job-job_auto1", "exec_abc123"]) && links.All(l => l.RunID == "run-1") && links[0] is { ExecutionID: null, SessionID: "auto_9" }
            && links[0].DashboardURL?.OriginalString == "http://127.0.0.1:9000", "codex names, sub-agents and job-only starts, once each");
        // A pending execution is taken from a loopback run URL only.
        Check(OuroborosExecutionLinks.Parse("Job ID: job_1\nExecution ID: pending\nLive Dashboard: http://localhost:8123/?run=exec_from_url\n", "t", "r")?.ExecutionID == "exec_from_url", "a pending execution is its run URL's");
        var remote = OuroborosExecutionLinks.Parse("Job ID: job_1\nExecution ID: pending\nLive Dashboard: http://evil.example:8123/?run=exec_from_url\n", "t", "r");
        Check(remote is { ExecutionID: null, DashboardURL: null, Key: "job-job_1" }, "a dashboard that is not loopback is dropped with its run");
        return Task.CompletedTask;
    }

    internal static Task IdsThatCouldBreakOutOfAUrlOrAnArgumentAreRejected()
    {
        foreach (var bad in new[] { "-rf", "../etc", "exec/1", "exec 1", "exec?x=1", "exec;rm", "", new string('a', 201), "exec%2F", "pending.1", ":evolve", "evolve:a/b", "exec#1", "exec&run=x", "exec_1\n" })
            Check(!OuroborosExecutionLinks.ValidID(bad), "rejected: " + bad);
        foreach (var good in new[] { "exec_abc123", "job_6bb930bd8b98", "orch-1", "A", "evolve:lin_seed_x:generation:3", new string('a', 200) })
            Check(OuroborosExecutionLinks.ValidID(good), "accepted: " + good);
        Check(OuroborosExecutionLinks.Parse("Job ID: ../x\nExecution ID: -v\n", "t", "r") is null, "ids that are not strict name nothing");
        Check(OuroborosDashboardClient.EnsureArguments("--serve-daemon") is null && OuroborosDashboardClient.EnsureArguments("exec_1")!.SequenceEqual(["-I", "-m", "ouroboros.dashboard_web", "--run", "exec_1"])
            && OuroborosDashboardClient.EnsureArguments(null)!.SequenceEqual(["-I", "-m", "ouroboros.dashboard_web"]), "the daemon's arguments");
        // An evolve id is one argument after --run, and a percent-encoded query item.
        Check(OuroborosDashboardClient.EnsureArguments("evolve:lin:generation:2")!.TakeLast(2).SequenceEqual(["--run", "evolve:lin:generation:2"]), "an evolve id is one argument");
        var url = OuroborosDashboard.EventsURL(new("127.0.0.1", 9), "evolve:lin:generation:2")!;
        Check(url.AbsolutePath == "/events" && OuroborosExecutionLinks.QueryItem(url, "run") == "evolve:lin:generation:2" && !url.Query.Contains("generation:2", StringComparison.Ordinal), "and one encoded query item: " + url);
        // An evolve step without an execution is its job; a planned generation is its own block.
        static string Evolve(string execution) => "Started background evolve_step.\n\nJob ID: job_7f3a2b1c9d0e\nLineage ID: lin_seed_mac_resources_regression\nExecution ID: " + execution
            + "\n\nUse ouroboros_job_status, ouroboros_job_wait, or ouroboros_job_result to monitor it.\n\n<!-- ouroboros-job-observer-v1 base64\neyJqb2Jfb2JzZXJ2ZXIiOnt9fQ==\n-->\n";
        foreach (var placeholder in new[] { "None", "none", "NULL", "Pending" })
            Check(OuroborosExecutionLinks.Parse(Evolve(placeholder), "ouroboros_start_evolve_step", "r") is { ExecutionID: null, Key: "job-job_7f3a2b1c9d0e" }, "a placeholder is no execution: " + placeholder);
        const string evolve = "mcp__plugin_ouroboros_ouroboros__ouroboros_start_evolve_step";
        var links = OuroborosExecutionLinks.Extract([Run("run-1", Entry("a", evolve, Evolve("None").Replace("job_7f3a2b1c9d0e", "job_1"))), Run("run-2", Entry("b", evolve, Evolve("evolve:lin_seed_mac_resources_regression:generation:2")))]);
        Check(links.Select(l => l.Key).SequenceEqual(["job-job_1", "evolve:lin_seed_mac_resources_regression:generation:2"]) && links[1].DashboardURL is null, "two steps of one lineage are two blocks");
        Check(OuroborosExecutionLinks.Parse("Job ID: job_1\nLive Dashboard: http://localhost:1/?run=None\n", "t", "r")?.ExecutionID is null, "a run parameter that is a placeholder names nothing");
        return Task.CompletedTask;
    }

    // ── loopback ──────────────────────────────────────────────────────────────

    internal static Task OnlyPlainHttpOnALoopbackHostWithAPortIsEverAnEndpoint()
    {
        static OuroborosDashboardEndpoint? Endpoint(string text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? OuroborosDashboard.Endpoint(url) : null;
        Check(Endpoint("http://localhost:8123/?run=x") == new OuroborosDashboardEndpoint("localhost", 8123) && Endpoint("http://127.0.0.1:1") is not null && Endpoint("http://[::1]:8123/")?.Host == "::1"
            && Endpoint("http://LOCALHOST:80/") == new OuroborosDashboardEndpoint("localhost", 80), "loopback http with a port");
        foreach (var bad in new[] { "https://localhost:8123", "http://localhost", "http://example.com:8123", "http://127.0.0.2:8123", "http://user:pw@localhost:8123", "file:///tmp/x", "http://0.0.0.0:8123", "http://localhost.evil.com:80", "ftp://localhost:21" })
            Check(Endpoint(bad) is null, "refused: " + bad);
        static OuroborosDashboardEndpoint? State(string json) => OuroborosDashboard.Endpoint(Utf8(json));
        Check(State("""{"host":"127.0.0.1","port":5000,"pid":1,"db_path":"/x"}""") == new OuroborosDashboardEndpoint("127.0.0.1", 5000) && State("""{"host":"0.0.0.0","port":5000}""")?.Host == "127.0.0.1"
            && State("""{"port":5000}""")?.Host == "127.0.0.1", "the daemon's own record, reached on loopback");
        foreach (var bad in new[] { """{"host":"10.0.0.5","port":5000}""", """{"host":"127.0.0.1","port":"5000"}""", """{"host":"127.0.0.1","port":70000}""", "not json", "[5000]" })
            Check(State(bad) is null, "refused record: " + bad);
        var v6 = new OuroborosDashboardEndpoint("::1", 9);
        Check(OuroborosDashboard.EventsURL(v6, "exec_1")?.AbsoluteUri == "http://[::1]:9/events?run=exec_1" && OuroborosDashboard.EventsURL(v6, "a/b") is null && OuroborosDashboard.PageURL(v6, null).AbsoluteUri == "http://[::1]:9/"
            && OuroborosDashboard.PageURL(v6, "exec_1").AbsoluteUri == "http://[::1]:9/?run=exec_1" && OuroborosDashboard.PageURL(v6, "a/b").AbsoluteUri == "http://[::1]:9/" && OuroborosDashboard.RunsURL(v6).AbsoluteUri == "http://[::1]:9/api/runs", "the three addresses");
        return Task.CompletedTask;
    }

    // ── payloads ──────────────────────────────────────────────────────────────

    internal static Task DashboardPayloadsAreReadTolerantly()
    {
        var runs = OuroborosDashboard.Summaries(Utf8("""
            {"runs":[
              {"execution_id":"exec_1","session_id":"orch_1","goal":"Build it","status":"running","completed_count":2,"total_count":5,
               "pending_count":1,"executing_count":2,"failed_count":0,"phase":"Execute","activity":"AC 3","extra":{"x":1}},
              {"execution_id":"exec_2","status":"exploded","completed_count":true,"total_count":-3},
              {"execution_id":"../bad","status":"completed"},
              {"status":"completed"},
              "noise"
            ]}
            """))!;
        Check(runs.Count == 2 && runs["exec_1"] is { Status: OuroborosExecutionStatus.Running, Goal: "Build it", Phase: "Execute", Activity: "AC 3", Completed: 2, Total: 5, Pending: 1, Executing: 2, Failed: 0 }, "a run's summary");
        Check(runs["exec_2"] is { Status: OuroborosExecutionStatus.Unknown, Completed: null, Total: null, Goal: null }, "what cannot be read is absent");
        Check(OuroborosDashboard.Summaries(Utf8("""{"error":"x"}""")) is null && OuroborosDashboard.Summaries(Utf8("<html>")) is null, "another body is not runs");
        Check(OuroborosDashboard.Status("CANCELLED") == OuroborosExecutionStatus.Cancelled && OuroborosDashboard.Status(null) == OuroborosExecutionStatus.Unknown && OuroborosDashboard.Status("3") == OuroborosExecutionStatus.Unknown
            && !OuroborosExecutionStatus.Paused.IsTerminal() && OuroborosExecutionStatus.Failed.IsTerminal(), "statuses");
        // What a block's activity mark is told: nothing read yet is starting, a status that cannot be told is unknown.
        Check(OuroborosDashboard.BlockStatus(null) == "starting" && OuroborosDashboard.BlockStatus(new(OuroborosExecutionSnapshot.Sources.Unreachable)) == "unknown"
            && new[] { OuroborosExecutionStatus.Running, OuroborosExecutionStatus.Paused, OuroborosExecutionStatus.Completed, OuroborosExecutionStatus.Failed, OuroborosExecutionStatus.Cancelled }
                .Select(status => OuroborosDashboard.BlockStatus(new(OuroborosExecutionSnapshot.Sources.Live, status))).SequenceEqual(["running", "waiting", "completed", "error", "stopped"]), "the block's status words");
        // SSE keeps only complete data events.
        Check(OuroborosDashboard.SseData("data: {\"a\":1}\n\n: keep-alive\n\nevent: x\r\ndata: one\r\ndata: two\r\n\r\ndata: partial").SequenceEqual(["{\"a\":1}", "one\ntwo"]) && OuroborosDashboard.SseData("").Count == 0, "complete SSE events only");
        // A board becomes an ordered AC list, unknown columns kept as executing.
        var board = OuroborosDashboard.Board(Utf8("""
            {"meta":{"goal":"G","phase":"P","activity":"A","completed":1,"total":4},
             "columns":{
              "completed":[{"id":"ac_1","title":"First","status":"completed","ac_index":1}],
              "pending":[{"id":"ac_3","status":"pending","ac_index":3}],
              "executing":[{"id":"ac_2","title":"Second","ac_index":2},{"id":"ac_2_1","title":"Sub","ac_index":2,"depth":1}],
              "failed":[{"title":"no id"}],
              "mystery":[{"id":"ac_4","title":"Fourth","ac_index":4}]},
             "providers":[]}
            """))!;
        Check(board.Items.Select(i => i.Id).SequenceEqual(["ac_1", "ac_2", "ac_2_1", "ac_3", "ac_4"])
            && board.Items.Select(i => i.Status).SequenceEqual([OuroborosACStatus.Completed, OuroborosACStatus.Executing, OuroborosACStatus.Executing, OuroborosACStatus.Pending, OuroborosACStatus.Executing])
            && board.Items[3].Title == "ac_3" && board.Items[2].Depth == 1 && board is { Goal: "G", Completed: 1, Total: 4 } && OuroborosDashboard.Board(Utf8("""{"meta":{}}""")) is null, "the board's AC list");
        return Task.CompletedTask;
    }

    internal static Task TheSummaryOwnsStatusAndCountsAndTheBoardFillsTheRest()
    {
        var board = new OuroborosBoard("board goal", "p", null, null, null, [new("1", "a", OuroborosACStatus.Completed), new("2", "b", OuroborosACStatus.Failed)]);
        var summary = new OuroborosRunSummary("exec_1", OuroborosExecutionStatus.Running, Activity: "act", Completed: 5, Total: 9, Pending: 1, Executing: 3, Failed: 0);
        var both = OuroborosExecutionSnapshot.Merge(summary, board, null, null);
        Check(both is { Status: OuroborosExecutionStatus.Running, Completed: 5, Total: 9, Failed: 0, Goal: "board goal", Phase: "p", Activity: "act", Items.Count: 2, Source: OuroborosExecutionSnapshot.Sources.Live }, "the summary's counts over the board's words");
        Check(OuroborosExecutionSnapshot.Merge(null, board, null, null) is { Completed: 1, Total: 2, Failed: 1, Pending: 0 }, "the board alone counts its own cards");
        OuroborosACItem[] kept = [new("x", "x", OuroborosACStatus.Pending)];
        Check(ReferenceEquals(OuroborosExecutionSnapshot.Merge(summary, null, kept, null).Items, kept), "without a board the last list stays");
        // A run the run list dropped takes a settled status from its board.
        static OuroborosBoard Of(OuroborosACStatus[] statuses, int? total = null) => new(null, null, null, null, total, statuses.Select((status, index) => new OuroborosACItem("ac_" + index, "t", status)).ToList());
        static OuroborosExecutionStatus Status(OuroborosBoard value) => OuroborosExecutionSnapshot.Merge(null, value, null, null).Status;
        Check(Status(Of([OuroborosACStatus.Completed, OuroborosACStatus.Completed])) == OuroborosExecutionStatus.Completed && Status(Of([OuroborosACStatus.Completed, OuroborosACStatus.Failed])) == OuroborosExecutionStatus.Failed, "a settled board tells the status");
        // Work still in flight, nothing settled, or ACs the board has not got yet: not known.
        Check(Status(Of([OuroborosACStatus.Completed, OuroborosACStatus.Executing])) == OuroborosExecutionStatus.Unknown && Status(Of([OuroborosACStatus.Completed, OuroborosACStatus.Pending])) == OuroborosExecutionStatus.Unknown
            && Status(Of([])) == OuroborosExecutionStatus.Unknown && Status(Of([OuroborosACStatus.Completed], 3)) == OuroborosExecutionStatus.Unknown, "anything else stays unknown");
        Check(OuroborosExecutionSnapshot.Merge(new("e", OuroborosExecutionStatus.Running), Of([OuroborosACStatus.Completed]), null, null).Status == OuroborosExecutionStatus.Running, "the summary still owns the status");
        // A settled board ends polling like a terminal summary does.
        var poll = new OuroborosExecutionPoll(DateTimeOffset.UnixEpoch, false);
        poll.Record(OuroborosExecutionSnapshot.Merge(null, Of([OuroborosACStatus.Completed]), null, null), DateTimeOffset.UnixEpoch);
        Check(poll.Stopped, "a settled board ends polling");
        return Task.CompletedTask;
    }

    // ── polling ───────────────────────────────────────────────────────────────

    internal static Task PollingIsFastWhileThingsChangeSlowWhenQuietAndStopsWhenTerminal()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_000);
        var poll = new OuroborosExecutionPoll(start, false);
        Check(poll.IsDue(start), "due at once");
        var snapshot = Snapshot(OuroborosExecutionSnapshot.Sources.Live, OuroborosExecutionStatus.Running, 1, 3);
        poll.Record(snapshot, start);
        Check(poll.NextAt == start + OuroborosExecutionPoll.FastInterval && OuroborosExecutionPoll.FastInterval == TimeSpan.FromSeconds(3), "fast while it changes");
        // No change for a minute: back off. A change speeds it up again.
        var quiet = start + OuroborosExecutionPoll.QuietAfter;
        poll.Record(snapshot with { }, quiet);
        Check(poll.NextAt == quiet + OuroborosExecutionPoll.SlowInterval && OuroborosExecutionPoll.SlowInterval == TimeSpan.FromSeconds(15), "slow when quiet");
        snapshot = snapshot with { Completed = 2 };
        poll.Record(snapshot, quiet.AddSeconds(15));
        Check(poll.NextAt == quiet.AddSeconds(15) + OuroborosExecutionPoll.FastInterval, "a change speeds it up");
        snapshot = snapshot with { Status = OuroborosExecutionStatus.Completed };
        poll.Record(snapshot, quiet.AddSeconds(20));
        Check(poll.Stopped && !poll.IsDue(quiet.AddSeconds(1_000)), "a terminal status ends it");
        // An AC list opened afterwards reads once more, then stops again.
        poll.Wake(quiet.AddSeconds(30));
        Check(poll.IsDue(quiet.AddSeconds(30)), "woken");
        poll.Record(snapshot, quiet.AddSeconds(31));
        Check(poll.Stopped, "and stopped again");
        // Job-only starts and unanswered history are read once.
        var now = DateTimeOffset.UtcNow;
        var job = new OuroborosExecutionPoll(now, false); job.Record(Snapshot(OuroborosExecutionSnapshot.Sources.NoExecution), now);
        var history = new OuroborosExecutionPoll(now, true);
        Check(job.Stopped && !history.MayEnsureDaemon, "a job-only start is read once, and history never starts the daemon");
        history.Record(Snapshot(OuroborosExecutionSnapshot.Sources.Unreachable), now);
        var live = new OuroborosExecutionPoll(now, false); live.Record(Snapshot(OuroborosExecutionSnapshot.Sources.Unreachable), now);
        Check(history.Stopped && !live.Stopped, "unanswered history stops; a live execution keeps being read");
        Check(!OuroborosExecutionPoll.IsStale("2026-09-29T00:00:00Z", DateTimeOffset.Parse("2026-09-29T11:00:00Z")) && OuroborosExecutionPoll.IsStale("2026-09-29T00:00:00.123Z", DateTimeOffset.Parse("2026-09-30T00:00:00Z"))
            && OuroborosExecutionPoll.IsStale("garbage", now) && OuroborosExecutionPoll.IsStale("", now) && OuroborosExecutionPoll.IsStale("2026-09-29", DateTimeOffset.Parse("2026-09-29T01:00:00Z")), "a start older than twelve hours, or with no readable time, is history");
        return Task.CompletedTask;
    }

    internal static Task ReadsThatNeverTellTheStatusBackOffAndTheDaemonIsStartedSparingly()
    {
        var start = DateTimeOffset.UnixEpoch;
        var poll = new OuroborosExecutionPoll(start, false);
        var unknown = Snapshot(OuroborosExecutionSnapshot.Sources.Live);
        var at = start;
        for (var read = 1; read <= OuroborosExecutionPoll.UnknownStopAfter; read++)
        {
            poll.Record(unknown, at);
            if (read < OuroborosExecutionPoll.UnknownBackoffAfter) Check(poll.NextAt - at <= OuroborosExecutionPoll.SlowInterval, "the first unknown reads keep their pace");
            else if (read < OuroborosExecutionPoll.UnknownStopAfter) Check(poll.NextAt == at + OuroborosExecutionPoll.UnknownInterval, "then minutes apart");
            at = poll.NextAt ?? at;
        }
        Check(poll.Stopped && OuroborosExecutionPoll.UnknownBackoffAfter == 10 && OuroborosExecutionPoll.UnknownStopAfter == 40 && OuroborosExecutionPoll.UnknownInterval == TimeSpan.FromSeconds(180), "and then they stop");
        // A read that tells the status starts the count again.
        var known = new OuroborosExecutionPoll(start, false);
        for (var read = 1; read < OuroborosExecutionPoll.UnknownBackoffAfter; read++) known.Record(unknown, start);
        known.Record(Snapshot(OuroborosExecutionSnapshot.Sources.Live, OuroborosExecutionStatus.Running), start);
        Check(known.UnknownReads == 0, "a known status resets the count");
        known.Record(unknown, start);
        Check(known.NextAt == start + OuroborosExecutionPoll.FastInterval, "and the pace");
        // An answer that cannot be read is not a status either.
        var unreadable = new OuroborosExecutionPoll(start, false);
        for (var read = 1; read <= OuroborosExecutionPoll.UnknownStopAfter; read++) unreadable.Record(Snapshot(OuroborosExecutionSnapshot.Sources.Unreadable), start);
        Check(unreadable.Stopped, "an unreadable dashboard stops too");
        // The daemon is started at most every few minutes and a few times in all per execution.
        var starts = new OuroborosDaemonStarts();
        Check(starts.Claim("exec_1", start) && !starts.Claim("exec_1", start + OuroborosDaemonStarts.Cooldown - TimeSpan.FromSeconds(1)) && starts.Claim("exec_2", start), "one start per cooldown, per execution");
        at = start;
        for (var count = 1; count < OuroborosDaemonStarts.Maximum; count++) { at += OuroborosDaemonStarts.Cooldown; Check(starts.Claim("exec_1", at), "a later start"); }
        Check(!starts.Claim("exec_1", at + OuroborosDaemonStarts.Cooldown * 10) && OuroborosDaemonStarts.Maximum == 3 && OuroborosDaemonStarts.Cooldown == TimeSpan.FromSeconds(300), "never more than three");
        // Whether a block may ask at all: live and still read, never history or a finished run.
        var finished = new OuroborosExecutionPoll(start, false); finished.Record(Snapshot(OuroborosExecutionSnapshot.Sources.Live, OuroborosExecutionStatus.Failed), start);
        Check(new OuroborosExecutionPoll(start, false).MayEnsureDaemon && !new OuroborosExecutionPoll(start, true).MayEnsureDaemon && !finished.MayEnsureDaemon, "only a live execution asks for the daemon");
        return Task.CompletedTask;
    }

    // ── client ────────────────────────────────────────────────────────────────

    private sealed class FakeTransport(byte[]? runs, byte[]? events, int status = 200) : IOuroborosDashboardTransport
    {
        internal readonly ConcurrentQueue<string> Calls = [];
        internal int Open, MostOpen;
        internal Func<byte[]?>? Runs;
        internal TimeSpan Delay = TimeSpan.Zero;
        public Task<(int Status, byte[] Body)?> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellation)
        {
            Calls.Enqueue("GET " + url.AbsoluteUri);
            return Task.FromResult((Runs is null ? runs : Runs()) is { } body ? ((int, byte[])?)(status, body) : null);
        }
        public async Task<byte[]?> ReadEventsAsync(Uri url, TimeSpan firstEvent, TimeSpan quiet, TimeSpan deadline, int maximumBytes, CancellationToken cancellation)
        {
            Check(firstEvent == OuroborosDashboardClient.FirstEventDeadline && firstEvent < deadline && maximumBytes == OuroborosDashboardClient.MaximumBoardBytes, "the board stream's limits");
            Calls.Enqueue("SSE " + url.AbsoluteUri);
            var open = Interlocked.Increment(ref Open);
            int most; while (open > (most = MostOpen) && Interlocked.CompareExchange(ref MostOpen, open, most) != most) { }
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellation);
            Interlocked.Decrement(ref Open);
            return events ?? (Delay > TimeSpan.Zero ? Utf8("data: {\"meta\":{},\"columns\":{\"completed\":[{\"id\":\"" + OuroborosExecutionLinks.QueryItem(url, "run") + "\"}]}}\n\n") : null);
        }
    }

    private static string Home() { var home = Verification.Temp(); Directory.CreateDirectory(Path.Combine(home, ".ouroboros")); return home; }
    private static Task<bool> Nothing(OuroborosDashboardClient.Launch launch) => throw new InvalidOperationException("nothing to start");

    internal static async Task TheClientReadsTheDaemonsOwnRecordAndOnlyGetsTheTwoEndpoints()
    {
        var home = Home();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, ".ouroboros", "dashboard.json"), """{"host":"127.0.0.1","port":4321,"pid":7,"db_path":"/db"}""");
            const string frame = """{"meta":{"total":1},"columns":{"completed":[{"id":"ac_1","title":"One"}]}}""";
            var transport = new FakeTransport(Utf8("""{"runs":[{"execution_id":"exec_1","status":"completed"}]}"""), Utf8("data: {}\n\ndata: " + frame + "\n\n"));
            var client = new OuroborosDashboardClient(transport, home, Nothing);
            // The record wins over a hint from tool output.
            var endpoint = await client.EndpointAsync(new Uri("http://localhost:9999/"));
            Check(endpoint == new OuroborosDashboardEndpoint("127.0.0.1", 4321), "the daemon's own record wins");
            Check((await client.SummariesAsync(endpoint)).Summaries?["exec_1"].Status == OuroborosExecutionStatus.Completed, "the run's summary");
            Check((await client.BoardAsync(endpoint!, "exec_1"))?.Items.Select(i => i.Title).SequenceEqual(["One"]) == true && await client.BoardAsync(endpoint!, "exec/1") is null, "the newest complete board, and none for an id that is not strict");
            Check(transport.Calls.SequenceEqual(["GET http://127.0.0.1:4321/api/runs", "SSE http://127.0.0.1:4321/events?run=exec_1"]), "only the two endpoints: " + string.Join(", ", transport.Calls));
            // A record too large to be one is not read.
            await File.WriteAllTextAsync(Path.Combine(home, ".ouroboros", "dashboard.json"), """{"host":"127.0.0.1","port":4321,"pad":" """ + new string('x', OuroborosDashboardClient.MaximumStateBytes) + "\"}");
            Check(await client.EndpointAsync(null) is null, "an oversized record is not read");
            // A dashboard that answers without runs is up but unreadable.
            async Task<OuroborosRunsAnswer> Answer(int status, string body) => await new OuroborosDashboardClient(new FakeTransport(Utf8(body), null, status), home, Nothing).SummariesAsync(new("127.0.0.1", 4321));
            Check(await Answer(503, """{"runs":[],"error":"picker_index_contract_unavailable"}""") is { Answered: true, Status: 503, Summaries: null } && await Answer(200, "<html>") is { Answered: true, Summaries: null }
                && await Answer(200, """{"runs":[]}""") is { Answered: true, Summaries.Count: 0 }, "an answer without runs is unreadable, not absent");
            // Boards are read concurrently but bounded.
            var slow = new FakeTransport(null, null) { Delay = TimeSpan.FromMilliseconds(80) };
            var ids = Enumerable.Range(0, 7).Select(n => "exec_" + n).ToList();
            var boards = await new OuroborosDashboardClient(slow, home, Nothing).BoardsAsync(new("127.0.0.1", 1), [.. ids, "exec_0"]);
            Check(boards.Keys.ToHashSet().SetEquals(ids) && boards["exec_3"].Items.Single().Id == "exec_3" && slow.MostOpen > 1 && slow.MostOpen <= OuroborosDashboardClient.BoardConcurrency && slow.Calls.Count == 7, "seven boards, three streams at once: " + slow.MostOpen);
        }
        finally { Directory.Delete(home, true); }
    }

    internal static async Task WithoutARecordOnlyALoopbackHintIsUsedAndTheDaemonIsEnsuredWithoutAShell()
    {
        var home = Home();
        try
        {
            var launches = new List<OuroborosDashboardClient.Launch>();
            var python = Path.Combine(home, "tools", "ouroboros-ai", "python");
            var client = new OuroborosDashboardClient(new FakeTransport(null, null), home, launch => { launches.Add(launch); return Task.FromResult(true); }, [Path.Combine(home, "absent", "python"), python]);
            Check(await client.EndpointAsync(null) is null && await client.EndpointAsync(new Uri("http://example.com:80/")) is null
                && await client.EndpointAsync(new Uri("http://localhost:8123/?run=x")) == new OuroborosDashboardEndpoint("localhost", 8123), "only a loopback hint");
            Check(await client.SummariesAsync(new("localhost", 8123)) is { Answered: false } && await client.SummariesAsync(null) is { Answered: false }, "nothing answered");
            // No tool python installed: nothing is launched.
            Check(!await client.EnsureDaemonAsync("exec_1") && launches.Count == 0, "no python, no start");
            Directory.CreateDirectory(Path.GetDirectoryName(python)!); await File.WriteAllTextAsync(python, "");
            Check(await client.EnsureDaemonAsync("exec_1") && !await client.EnsureDaemonAsync("-x") && launches.Count == 1, "one start, and none for an id that reads as an option");
            // Isolated python, from a directory an agent cannot write, with only the environment it needs.
            var launch = launches[0];
            Check(launch.Executable == python && launch.Arguments.SequenceEqual(["-I", "-m", "ouroboros.dashboard_web", "--run", "exec_1"]) && launch.WorkingDirectory == OuroborosDashboardClient.DaemonDirectory
                && launch.Environment["HOME"] == home && launch.Environment["PYTHONSAFEPATH"] == "1" && launch.Environment["PYTHONNOUSERSITE"] == "1" && !launch.Environment.ContainsKey("PYTHONPATH"), "the start as launched");
            var inherited = new Dictionary<string, string> { ["PYTHONPATH"] = "/tmp/evil", ["PYTHONSTARTUP"] = "/tmp/x.py", ["LANG"] = "ko_KR.UTF-8", ["TMPDIR"] = "/var/tmp/", ["OUROBOROS_DASHBOARD"] = "1", ["HOME"] = "/elsewhere",
                ["PATH"] = "/tmp/bin:/usr/bin", ["DYLD_INSERT_LIBRARIES"] = "/tmp/x.dylib", ["SystemRoot"] = @"C:\Windows", ["APPDATA"] = @"C:\Users\u\AppData\Roaming", ["TEMP"] = @"C:\Temp", ["PATHEXT"] = ".EXE",
                ["ComSpec"] = @"C:\Windows\system32\cmd.exe", ["windir"] = @"C:\Windows", ["NUMBER_OF_PROCESSORS"] = "8", ["PSModulePath"] = @"C:\x" };
            var unix = OuroborosDashboardClient.DaemonEnvironment(home, inherited, windows: false);
            Check(unix.Count == 6 && unix["HOME"] == home && unix["PATH"] == "/usr/bin:/bin:/usr/sbin:/sbin" && unix["LANG"] == "ko_KR.UTF-8" && unix["TMPDIR"] == "/var/tmp/" && unix["PYTHONSAFEPATH"] == "1" && unix["PYTHONNOUSERSITE"] == "1"
                && OuroborosDashboardClient.DaemonEnvironment(home, new Dictionary<string, string>(), false)["LANG"] == "en_US.UTF-8", "the daemon's environment as the Mac gives it");
            var windows = OuroborosDashboardClient.DaemonEnvironment(home, inherited, windows: true);
            // A Python process on Windows also needs the shell, the program extensions and the processor count the standard library reads.
            Check(windows.Keys.ToHashSet().SetEquals(["HOME", "USERPROFILE", "PATH", "SystemRoot", "windir", "ComSpec", "PATHEXT", "APPDATA", "TEMP", "NUMBER_OF_PROCESSORS", "PYTHONSAFEPATH", "PYTHONNOUSERSITE"]) && windows["USERPROFILE"] == home
                && windows["ComSpec"] == @"C:\Windows\system32\cmd.exe" && windows["PATHEXT"] == ".EXE"
                && windows["PATH"] == Path.Combine(@"C:\Windows", "System32") + @";C:\Windows" && windows["SystemRoot"] == @"C:\Windows", "on Windows the system's folders, never the app's PATH or an agent's variables: " + string.Join(",", windows.Keys));
            // Where the tool's own interpreter is looked for: uv first, then pipx.
            Check(OuroborosDashboardClient.ToolPythons(home, false, null).SequenceEqual([Path.Combine(home, ".local", "share", "uv", "tools", "ouroboros-ai", "bin", "python"), Path.Combine(home, ".local", "pipx", "venvs", "ouroboros-ai", "bin", "python")])
                && OuroborosDashboardClient.ToolPythons(home, true, Path.Combine(home, "Roaming")).SequenceEqual([Path.Combine(home, "Roaming", "uv", "tools", "ouroboros-ai", "Scripts", "python.exe"),
                    Path.Combine(home, "pipx", "venvs", "ouroboros-ai", "Scripts", "python.exe"), Path.Combine(home, ".local", "pipx", "venvs", "ouroboros-ai", "Scripts", "python.exe")]), "the tool interpreters");
        }
        finally { Directory.Delete(home, true); }
    }

    /// A dashboard on loopback that answers each request with what <paramref name="respond"/> gives: a delay, a status and a body, then closes.
    private static (TcpListener Listener, int Port, Task Served) Serve(Func<string, (TimeSpan Delay, int Status, string Body)> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var served = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var request = new StringBuilder(); var buffer = new byte[4096];
                            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) is > 0 and var read) request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            var (delay, status, body) = respond(request.ToString().Split(' ').ElementAtOrDefault(1) ?? "");
                            if (delay > TimeSpan.Zero) await Task.Delay(delay);
                            await stream.WriteAsync(Utf8($"HTTP/1.1 {status} X\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
                    }
                });
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port, served);
    }

    internal static async Task TheLoopbackTransportReadsOnlyLoopbackWithinItsLimits()
    {
        var home = Home();
        const string frame = """{"meta":{},"columns":{"executing":[{"id":"ac_1","title":"Doing"}]}}""";
        var (listener, port, _) = Serve(target => target.StartsWith("/api/runs", StringComparison.Ordinal) ? (TimeSpan.Zero, 200, """{"runs":[{"execution_id":"exec_1","status":"running"}]}""")
            : target == "/events?run=exec_1" ? (TimeSpan.Zero, 200, ": hello\n\ndata: " + frame + "\n\n")
            : target == "/events?run=exec_slow" ? (TimeSpan.FromSeconds(5), 200, "data: {}\n\n") : target == "/large" ? (TimeSpan.Zero, 200, new string('x', 2_000)) : (TimeSpan.Zero, 404, "{}"));
        try
        {
            var transport = new LoopbackDashboardTransport();
            var client = new OuroborosDashboardClient(transport, home, Nothing);
            var endpoint = new OuroborosDashboardEndpoint("127.0.0.1", port);
            Check((await client.SummariesAsync(endpoint)).Summaries?["exec_1"].Status == OuroborosExecutionStatus.Running, "runs from a real loopback dashboard");
            Check((await client.BoardAsync(endpoint, "exec_1"))?.Items.Single().Title == "Doing", "a closed event stream's board");
            // Not an event stream it asked for: nothing.
            Check(await client.BoardAsync(endpoint, "exec_2") is null && (await transport.GetAsync(endpoint.Url("/missing"), TimeSpan.FromSeconds(4), default))?.Status == 404, "another status is not a board");
            // Never anything but loopback, before any connection is made.
            Check(await transport.GetAsync(new Uri("http://example.com:80/api/runs"), TimeSpan.FromSeconds(1), default) is null && await transport.ReadEventsAsync(new Uri("https://127.0.0.1:" + port + "/events?run=exec_1"), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1024, default) is null, "never anything but plain http on loopback");
            // An event stream that sends nothing is left after its first-event deadline, not the whole one.
            var started = System.Diagnostics.Stopwatch.StartNew();
            var silent = await transport.ReadEventsAsync(endpoint.Url("/events", "exec_slow"), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(8), 1024, default);
            Check(silent is null && started.Elapsed < TimeSpan.FromSeconds(4), "a silent stream is left early: " + started.Elapsed);
            // A plain answer is read whole in its time or not at all; a cancelled read ends at once.
            Check(await transport.GetAsync(endpoint.Url("/events", "exec_slow"), TimeSpan.FromSeconds(0.3), default) is null, "a slow answer is nothing");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            started.Restart();
            Check(await transport.ReadEventsAsync(endpoint.Url("/events", "exec_slow"), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(8), 1024, cancel.Token) is null && started.Elapsed < TimeSpan.FromSeconds(3), "a cancelled read ends at once");
            Check(LoopbackDashboardTransport.MaximumBytes == 4 * 1024 * 1024 && (await transport.GetAsync(endpoint.Url("/large"), TimeSpan.FromSeconds(4), default))?.Body.Length == 2_000, "a body under the cap is read whole");
            // A stream that stays open and ends its events with CRLF blank lines is read until it goes quiet, not cut at its first-event deadline.
            var open = new TcpListener(IPAddress.Loopback, 0);
            open.Start();
            try
            {
                _ = Task.Run(async () =>
                {
                    using var client = await open.AcceptTcpClientAsync();
                    var stream = client.GetStream(); var request = new StringBuilder(); var buffer = new byte[4096];
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) is > 0 and var read) request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    await stream.WriteAsync(Utf8("HTTP/1.1 200 X\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: {\"meta\":{},\"columns\":{\"pending\":[{\"id\":\"first\"}]}}\r\n\r\n"));
                    await Task.Delay(900);
                    await stream.WriteAsync(Utf8("data: {\"meta\":{},\"columns\":{\"completed\":[{\"id\":\"second\"}]}}\r\n\r\n"));
                    await Task.Delay(4_000);
                });
                var events = await transport.ReadEventsAsync(new OuroborosDashboardEndpoint("127.0.0.1", ((IPEndPoint)open.LocalEndpoint).Port).Url("/events", "exec_1"), TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), 1024 * 1024, default);
                var frames = events is null ? new List<string>() : OuroborosDashboard.SseData(Encoding.UTF8.GetString(events));
                Check(frames.Count == 2 && OuroborosDashboard.Board(Utf8(frames[^1]))?.Items.Single().Id == "second", "CRLF events keep an open stream read until it goes quiet: " + frames.Count);
            }
            finally { open.Stop(); }
        }
        finally { listener.Stop(); Directory.Delete(home, true); }
    }

    // ── the pane's monitor ────────────────────────────────────────────────────

    internal static async Task ThePanesMonitorReadsItsExecutionsAndStartsTheDaemonSparingly()
    {
        var home = Home();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, ".ouroboros", "dashboard.json"), """{"host":"127.0.0.1","port":4321}""");
            var now = DateTimeOffset.Parse("2026-09-29T01:00:00Z");
            var exec = new OuroborosExecutionLink("run-1", "t", "job_1", null, "exec_1", new Uri("http://localhost:8123/?run=exec_1"), "2026-09-29T00:59:00Z");
            var dropped = exec with { ExecutionID = "exec_old", JobID = "job_0" };
            var jobOnly = new OuroborosExecutionLink("run-1", "t", "job_2", Timestamp: "2026-09-29T00:59:00Z");
            OuroborosExecutionLink[] links = [exec, dropped, jobOnly];
            const string frame = """{"meta":{"total":1},"columns":{"completed":[{"id":"ac_1","title":"One"}]}}""";
            var transport = new FakeTransport(Utf8("""{"runs":[{"execution_id":"exec_1","status":"running","goal":"Build it","completed_count":1,"total_count":3}]}"""), Utf8("data: " + frame + "\n\n"));
            var monitor = new OuroborosExecutionMonitor(new OuroborosDashboardClient(transport, home, Nothing), new OuroborosDaemonStarts());
            monitor.Track(links, new HashSet<string>(), now);
            Check(monitor.Due(links, now).Count == 3 && monitor.NextAt(links) == now && monitor.Snapshot("exec_1") is null, "every new block is due at once");
            Check(await monitor.TickAsync(links, new HashSet<string>(), () => now), "the first read changes the blocks");
            // The listed run is its summary; the one the list dropped is read from its board; a job has nothing to look up.
            Check(monitor.Snapshot("exec_1") is { Source: OuroborosExecutionSnapshot.Sources.Live, Status: OuroborosExecutionStatus.Running, Goal: "Build it", Completed: 1, Total: 3, Items: null }
                && monitor.Snapshot("exec_1")!.DashboardURL?.AbsoluteUri == "http://127.0.0.1:4321/?run=exec_1", "the listed run: " + monitor.Snapshot("exec_1"));
            Check(monitor.Snapshot("exec_old") is { Status: OuroborosExecutionStatus.Completed, Items.Count: 1 } && monitor.Snapshot("job-job_2") is { Source: OuroborosExecutionSnapshot.Sources.NoExecution }
                && monitor.Snapshot("job-job_2")!.DashboardURL?.AbsoluteUri == "http://127.0.0.1:4321/", "the dropped run's board and the job-only start");
            Check(transport.Calls.SequenceEqual(["GET http://127.0.0.1:4321/api/runs", "SSE http://127.0.0.1:4321/events?run=exec_old"]), "the board is read only for the run the list dropped: " + string.Join(", ", transport.Calls));
            // The settled and the job-only blocks stop; the running one is read again in three seconds.
            Check(monitor.Due(links, now).Count == 0 && monitor.NextAt(links) == now + OuroborosExecutionPoll.FastInterval && monitor.Due(links, now.AddSeconds(3)).Single().Key == "exec_1", "only the running block is read again");
            // Its AC list opened: read now, with its board.
            HashSet<string> open = ["exec_1"];
            monitor.Track(links, open, now.AddSeconds(1));
            Check(monitor.Due(links, now.AddSeconds(1)).Single().Key == "exec_1" && await monitor.TickAsync([exec], open, () => now.AddSeconds(1)) && monitor.Snapshot("exec_1")!.Items!.Single().Title == "One", "an opened AC list is read at once");
            Check(!await monitor.TickAsync([exec], open, () => now.AddSeconds(4)), "the same answer changes nothing");
            // A block that is gone takes its state with it.
            monitor.Track([exec], open, now.AddSeconds(5));
            Check(monitor.Snapshot("exec_old") is null && monitor.Snapshot("job-job_2") is null && monitor.Snapshot("exec_1") is not null, "a block that is gone takes its state with it");

            // Nothing answers: the daemon is brought up once for a live execution that announced a dashboard, never for history.
            File.Delete(Path.Combine(home, ".ouroboros", "dashboard.json"));
            var launches = new List<OuroborosDashboardClient.Launch>();
            var python = Path.Combine(home, "python"); await File.WriteAllTextAsync(python, "");
            byte[]? answer = null;
            var silent = new FakeTransport(null, null) { Runs = () => answer };
            var history = exec with { ExecutionID = "exec_history", Timestamp = "2026-09-01T00:00:00Z" };
            var quiet = new OuroborosExecutionMonitor(new OuroborosDashboardClient(silent, home, launch => { launches.Add(launch); return Task.FromResult(true); }, [python]), new OuroborosDaemonStarts());
            OuroborosExecutionLink[] waiting = [exec, history];
            quiet.Track(waiting, new HashSet<string>(), now);
            await quiet.TickAsync(waiting, new HashSet<string>(), () => now);
            Check(launches.Count == 1 && launches[0].Arguments.Last() == "exec_1" && quiet.Snapshot("exec_1") is { Source: OuroborosExecutionSnapshot.Sources.Unreachable, Status: OuroborosExecutionStatus.Unknown }
                && quiet.Snapshot("exec_1")!.DashboardURL?.AbsoluteUri == "http://localhost:8123/?run=exec_1", "one start for the live execution; unreachable keeps the link's own page");
            Check(quiet.Due(waiting, now.AddHours(1)).Single().Key == "exec_1", "unanswered history is read once and left");
            await quiet.TickAsync([exec], new HashSet<string>(), () => now.AddSeconds(3));
            Check(launches.Count == 1, "not started again within the cooldown");
            // It answers but cannot read its runs: up, never started again, and the last counts stay under an unknown status.
            answer = Utf8("<html>");
            await quiet.TickAsync([exec], new HashSet<string>(), () => now.AddMinutes(10));
            Check(launches.Count == 1 && quiet.Snapshot("exec_1") is { Source: OuroborosExecutionSnapshot.Sources.Unreadable, Status: OuroborosExecutionStatus.Unknown }, "an unreadable dashboard is up");

            // The "open dashboard" click: the page of the run, once per click, and a failure is remembered.
            var shown = new List<Uri>();
            Check(await monitor.OpenDashboardAsync(exec, url => { shown.Add(url); Check(monitor.Opening("exec_1"), "the click is being handled"); return Task.FromResult(true); }) && shown.Single().AbsoluteUri == "http://localhost:8123/?run=exec_1"
                && !monitor.Opening("exec_1") && !monitor.OpenFailed("exec_1"), "the dashboard page opens: " + string.Join(",", shown));
            await monitor.OpenDashboardAsync(exec, _ => Task.FromResult(false));
            Check(monitor.OpenFailed("exec_1"), "a click that showed nothing says so");
            Check(await monitor.OpenDashboardAsync(jobOnly, _ => Task.FromResult(true)) && monitor.OpenFailed("job-job_2") && monitor.OpenFailed("exec_1"), "with no address there is nothing to open");

            // The read runs away from the monitor's thread and changes no block until it is given to them there.
            await File.WriteAllTextAsync(Path.Combine(home, ".ouroboros", "dashboard.json"), """{"host":"127.0.0.1","port":4321}""");
            OuroborosExecutionLink[] one = [exec];
            var whole = new OuroborosExecutionMonitor(new OuroborosDashboardClient(new FakeTransport(Utf8("""{"runs":[{"execution_id":"exec_1","status":"completed"}]}"""), null), home, Nothing), new OuroborosDaemonStarts());
            whole.Track(one, new HashSet<string>(), now);
            var read = whole.Begin(whole.Due(one, now), new HashSet<string>());
            await Task.Run(() => read.RunAsync(() => now));
            Check(whole.Snapshot("exec_1") is null && whole.Apply(read, now) && whole.Snapshot("exec_1") is { Status: OuroborosExecutionStatus.Completed } && whole.NextAt(one) is null, "a read is given to its blocks only when applied, then settles them");
            // A read cancelled while it waits (the diagram hid, the pane closed) ends at once and leaves its block due.
            var slow = new OuroborosExecutionMonitor(new OuroborosDashboardClient(new FakeTransport(Utf8("""{"runs":[]}"""), null) { Delay = TimeSpan.FromSeconds(5) }, home, Nothing), new OuroborosDaemonStarts());
            slow.Track(one, new HashSet<string>(), now);
            var waited = slow.Begin(slow.Due(one, now), new HashSet<string>());
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var started = System.Diagnostics.Stopwatch.StartNew();
            try { await Task.Run(() => waited.RunAsync(() => now, cancel.Token)); } catch (OperationCanceledException) { }
            Check(started.Elapsed < TimeSpan.FromSeconds(3) && slow.Snapshot("exec_1") is null && slow.Due(one, now).Count == 1, "a cancelled read ends at once and leaves its block due: " + started.Elapsed);
        }
        finally { Directory.Delete(home, true); }
    }
}
