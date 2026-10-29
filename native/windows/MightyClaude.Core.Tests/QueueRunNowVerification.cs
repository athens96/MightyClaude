using System.Text.Json;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>
/// 바로 실행 on a queued row (Core QueueRunNow), held to native/contracts/fixtures/queue-run-now.json as the Mac's
/// QueueRunNowFixtureTests hold M/QueueRunNow.swift to it.
/// </summary>
internal static class QueueRunNowVerification
{
    /// <summary>Every case of native/contracts/fixtures/queue-run-now.json decides as the Mac's QueueRunNowFixtureTests.</summary>
    internal static Task EveryCaseDecidesAsTheFixtureSays()
    {
        const string name = "MightyClaude.Core.Tests.QueueRunNow.json";
        using var stream = typeof(QueueRunNowVerification).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded");
        using var document = JsonDocument.Parse(stream);
        var cases = document.RootElement.GetProperty("cases");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases.EnumerateArray())
        {
            var row = item.GetProperty("row"); var p = item.GetProperty("pane"); var label = item.GetProperty("name").GetString();
            bool Flag(string key) => p.GetProperty(key).GetBoolean();
            string Text(string key) => p.GetProperty(key).GetString()!;
            var second = ComposerSecondSends.Choose(Text("kind"), Text("provider"), Text("mode"), Flag("isChild"), Flag("delegationOn"), Flag("canHostWorktree"));
            DelegationChildChoice[] choices = Flag("otherAgentReady") ? [new(Text("provider") == "claude" ? "codex" : "claude", "acceptEdits", null)] : [];
            var menuOnly = ComposerSecondSends.MenuOnly(Text("kind"), Text("provider"), Text("mode"), Flag("isChild"), Flag("delegationOn"), Flag("canHostWorktree"), choices);
            var pane = new QueueRunNow.Pane(Flag("busy"), Flag("heldForUpdate"), second, menuOnly, Flag("waitsOnBackgroundOnly"), p.GetProperty("held").GetInt32(), Flag("noticeTurnRunning"));
            int? heldIndex = row.GetProperty("heldIndex") is { ValueKind: JsonValueKind.Number } index ? index.GetInt32() : null;
            var path = QueueRunNow.Decide(heldIndex, row.GetProperty("text").GetString() ?? "", row.GetProperty("attachments").GetInt32(), pane,
                row.GetProperty("continuation").GetBoolean());
            Check(QueueRunNow.Name(path) == item.GetProperty("expect").GetString(), $"queue run now: {label} (was {QueueRunNow.Name(path)})");
            var wire = item.GetProperty("wire");
            (string RunNow, string? Reason)? expected = wire.ValueKind == JsonValueKind.Null ? null
                : (wire.GetProperty("runNow").GetString()!, wire.TryGetProperty("runNowReason", out var reason) ? reason.GetString() : null);
            Check(QueueRunNow.Wire(path) == expected, $"queue run now wire: {label}");
            // The reason comes only with "unavailable".
            Check(QueueRunNow.Wire(path) is not { } sent || (sent.Reason is null) == (sent.RunNow != "unavailable"), $"queue run now reason: {label}");
            seen.Add(QueueRunNow.Name(path));
        }
        // Every answer has a case, and only the five that do something run.
        foreach (var path in Enum.GetValues<QueueRunNowPath>()) Check(seen.Contains(QueueRunNow.Name(path)), "the fixture has a case for " + QueueRunNow.Name(path));
        Check(Enum.GetValues<QueueRunNowPath>().Where(QueueRunNow.Runs).Select(QueueRunNow.Name).SequenceEqual(["worktree", "agentMenu", "here", "steer", "held"]), "worktree, the agent menu, here, steer and held run");
        // An Antigravity card's continuation never leaves its pane: here when idle, else it waits for its turn (M/QueueRunNowFixtureTests.swift).
        var busy = new QueueRunNow.Pane(true, false, ComposerSecondSend.Worktree, false, true, 0);
        Check(QueueRunNow.Decide(null, "Go on", 0, busy, continuation: true) == QueueRunNowPath.WaitsForTurn && QueueRunNow.Decide(null, "Go on", 0, busy with { Busy = false }, continuation: true) == QueueRunNowPath.Here
            && QueueRunNow.Decide(null, "Go on", 0, busy with { SecondSend = ComposerSecondSend.Queue, MenuOnly = true }, continuation: true) == QueueRunNowPath.WaitsForTurn,
            "a continuation is never sent to a worktree, the agent menu or a steer");
        Check(QueueRunNow.IsContinuation(new("q", "Go on", [], AntigravityGrant: new AntigravityAllowGrant("r", [], false))) && !QueueRunNow.IsContinuation(new("q", "Go on", [])), "a row with an Antigravity grant continues its card");
        // The phone's rows carry what each decided, and nothing for a hidden row.
        DelegationDeliveryItem[] held = [new("n-1", DelegationDeliveryKind.Notice, "c1", "parent", "Notice 1"), new("n-2", DelegationDeliveryKind.Notice, "c2", "parent", "Notice 2")];
        QueuedComposerInput[] queued = [new("q-1", "Human row", [])];
        var answers = new Dictionary<string, QueueRunNowPath> { ["n-1"] = QueueRunNowPath.Held, ["n-2"] = QueueRunNowPath.Hidden, ["q-1"] = QueueRunNowPath.WaitsForHeld };
        SameList(DelegationQueueRows.MobileItems(held, queued, rowId => answers[rowId]),
            [new MobileQueuedItem("n-1", "Notice 1", "here"), new MobileQueuedItem("n-2", "Notice 2"), new MobileQueuedItem("q-1", "Human row", "unavailable", "held")], "each phone row carries its 바로 실행");
        Same(JsonSerializer.Serialize(DelegationQueueRows.MobileItems(held, queued, rowId => answers[rowId])[1], Wire.Json), """{"id":"n-2","text":"Notice 2"}""", "a row without a button carries neither field");
        Same(JsonSerializer.Serialize(DelegationQueueRows.MobileItems(held, queued, rowId => answers[rowId])[2], Wire.Json), """{"id":"q-1","text":"Human row","runNow":"unavailable","runNowReason":"held"}""", "an unavailable row says why");
        // 바로 실행 here moves the row to the head of the queue; a row whose worktree failed goes back once, at the head.
        var buffer = new QueuedInputBuffer(); var earlier = buffer.Add("First", []); var later = buffer.Add("Second", []);
        Check(buffer.MoveToFront(later.Id) && buffer.Items.Select(i => i.Id).SequenceEqual([later.Id, earlier.Id]) && !buffer.MoveToFront("missing"), "바로 실행 here moves the row to the head");
        buffer.Remove(earlier.Id); buffer.Insert(0, earlier); buffer.Insert(0, earlier);
        Check(buffer.Items.Select(i => i.Id).SequenceEqual([earlier.Id, later.Id]), "a row put back goes to the head, once");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A steer into a running notice turn would lose its answer: that turn's result ends the steer's turn too, and the steer's own
    /// result then replaces the request's (M/QueueRunNowFixtureTests.swift aRunningNoticeTurnTakesNoSteer). While a notice run after the
    /// newest request runs, its rows wait for their turn.
    /// </summary>
    internal static Task ARunningNoticeTurnTakesNoSteer()
    {
        var request = new MightyGraphRun { Id = "r", Status = "completed" };
        var running = new MightyGraphRun { Id = "r" + BackgroundNoticeRuns.Marker + "1", Status = "running", NoticeRun = true };
        var answered = new MightyGraphRun { Id = "r" + BackgroundNoticeRuns.Marker + "1", Status = "completed", FinalOutput = "Done.", NoticeRun = true };
        Check(MightyGraphSupport.NoticeTurnRunning([request, running]), "a running notice run after the request is a notice turn");
        Check(!MightyGraphSupport.NoticeTurnRunning([request, answered]), "an answered notice run is not");
        Check(!MightyGraphSupport.NoticeTurnRunning([request]) && !MightyGraphSupport.NoticeTurnRunning([]), "no notice run, no notice turn");
        // An older request's notice run left running never holds the newest request's queue.
        Check(!MightyGraphSupport.NoticeTurnRunning([request, running, new MightyGraphRun { Id = "r2", Status = "completed" }]), "only the newest request's notice runs count");
        // Its answer is the edge that offers delegation's waiting items again; no other change is.
        Check(MightyGraphSupport.NoticeTurnEnded([request, running], [request, answered]) && !MightyGraphSupport.NoticeTurnEnded([request, running], [request, running])
            && !MightyGraphSupport.NoticeTurnEnded([request, answered], [request, answered]) && !MightyGraphSupport.NoticeTurnEnded([request], [request, running]), "a notice turn's end is its answer");
        // The pane carries it: a human row waits for its turn instead of steering, and so do the held items, which a busy pane takes
        // only as a steer. Idle, or with no notice turn, the held items go as before.
        var pane = new QueueRunNow.Pane(true, false, ComposerSecondSend.Queue, false, true, 0, MightyGraphSupport.NoticeTurnRunning([request, running]));
        Check(QueueRunNow.Decide(null, "Next", 0, pane) == QueueRunNowPath.WaitsForTurn, "a row waits for its turn while a notice turn runs");
        Check(QueueRunNow.Decide(0, "", 0, pane) == QueueRunNowPath.WaitsForTurn && QueueRunNow.Decide(1, "", 0, pane) == QueueRunNowPath.Hidden, "the held items wait for a running notice turn too");
        Check(QueueRunNow.Decide(null, "Next", 0, pane with { NoticeTurnRunning = false }) == QueueRunNowPath.Steer && QueueRunNow.Decide(0, "", 0, pane with { NoticeTurnRunning = false }) == QueueRunNowPath.Held
            && QueueRunNow.Decide(0, "", 0, pane with { Busy = false }) == QueueRunNowPath.Held, "with no notice turn, or idle, the held items go as before");
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the window does with 바로 실행, read from the WinUI source (WinUI does not build here), as the Mac's AppStore does: the phone is
    /// never told a failed start ran, the queue's start says whether it took the row, a pane held by a manual update or plugin change (or
    /// recording a child's end) never offers a here that does nothing, no steer goes into a running notice turn, a put-back row leaves a
    /// queue an error paused alone, a stop draws its rows as stopping, run next with nothing held counts only a run that took the row,
    /// and delegation's held items wait for a notice turn instead of steering into it.
    /// </summary>
    internal static Task TheWindowRunsNowOnlyWhatCanRun()
    {
        var folder = ClaudePluginVerification.WinUISource();
        string Source(string file) => File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n");
        static string Member(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);
            Check(at >= 0, "the member is there: " + signature);
            var end = text.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            return text[at..(end < 0 ? text.Length : end)];
        }
        var remote = Member(Source("MainWindow.MobileRemote.cs"), "internal async Task<object?> MobileRunQueuedNow(");
        Check(remote.Contains("if (failure is not null) throw new MobileRequestException(409, failure);", StringComparison.Ordinal)
            && remote.IndexOf("if (failure is not null)", StringComparison.Ordinal) < remote.IndexOf("return wire.RunNow;", StringComparison.Ordinal), "a failed start answers the phone with its failure, never ok");
        var queue = Source("MainWindow.QueuedComposer.cs");
        Check(queue.Contains("private async Task<bool> StartNextQueuedInput()", StringComparison.Ordinal)
            && Member(queue, "internal async Task<QueueRunNowPath?> RunQueuedNow(").Contains("return await StartNextQueuedInput() ? path : null;", StringComparison.Ordinal), "a here the queue's start did not take ran nothing");
        var take = Member(queue, "private async Task<bool> TakeNextQueuedInput(");
        Check(!take.Contains("return;", StringComparison.Ordinal) && take.Contains("return true;", StringComparison.Ordinal), "every way the queue's start ends says whether it took the row");
        var pane = Member(queue, "private QueueRunNow.Pane RunNowPane(");
        Check(pane.Contains("var holds = owner.BackgroundUpdateHolds(pane) || owner.ManualUpdateReason(pane) is not null;", StringComparison.Ordinal)
            && pane.Contains("var recording = owner.service.Delegation?.EndingRuns.Contains(id) == true;", StringComparison.Ordinal)
            && pane.Contains("MightyGraphSupport.NoticeTurnRunning(pane.GraphRuns ?? [])", StringComparison.Ordinal), "a held, recording or notice-answering pane offers no here or steer that would not run");
        Check(Member(queue, "private async Task<bool> ReleaseHeldNow(").Contains("case DelegationRelease.NothingHeld: if (!await StartNextQueuedInput()) return false;", StringComparison.Ordinal), "run next with nothing held is true only when a run took the first row");
        var deliver = File.ReadAllText(Path.Combine(Path.GetDirectoryName(folder)!, "MightyClaude.Core", "DesktopService.Delegation.cs")).Replace("\r\n", "\n");
        Check(Member(deliver, "async Task<string?> IDelegationHost.Deliver(").Contains("!MightyGraphSupport.NoticeTurnRunning(s.GraphRuns ?? [])", StringComparison.Ordinal), "delegation never steers into a running notice turn");
        // What waited for that turn is offered again when it ends, not only at the run's end.
        var service = File.ReadAllText(Path.Combine(Path.GetDirectoryName(folder)!, "MightyClaude.Core", "DesktopService.cs")).Replace("\r\n", "\n");
        var receiveAt = service.IndexOf("private void Receive(RunEvent value)", StringComparison.Ordinal);
        Check(receiveAt >= 0, "Receive is there");
        // A service member: to its closing brace at member indent, past the lock's own.
        var receiveEnd = receiveAt < 0 ? -1 : service.IndexOf("\n    }\n", receiveAt, StringComparison.Ordinal);
        var receive = receiveAt < 0 || receiveEnd < 0 ? "" : service[receiveAt..receiveEnd];
        Check(receive.Contains("noticeTurnEnded = MightyGraphSupport.NoticeTurnEnded(before,", StringComparison.Ordinal)
            && receive.IndexOf("var before = ", StringComparison.Ordinal) < receive.IndexOf("snapshot = snapshot.Apply(value);", StringComparison.Ordinal)
            && receive.Contains("|| noticeTurnEnded) OfferDelegationPending();", StringComparison.Ordinal), "a notice turn's end offers delegation's waiting items again");
        Check(Member(queue, "private async Task<bool> SteerQueuedInput(").Contains("MightyGraphSupport.NoticeTurnRunning(Session.GraphRuns ?? [])", StringComparison.Ordinal), "no row steers into a running notice turn");
        Check(Member(queue, "internal async Task PutBackQueuedRow(").Contains("Session.Status is not (\"running\" or \"error\")", StringComparison.Ordinal), "a put-back row leaves a queue an error paused");
        var stop = Member(queue, "private Task StopActiveRun(");
        var stopsAt = stop.IndexOf("stopping = true;", StringComparison.Ordinal);
        Check(stopsAt >= 0 && stopsAt < stop.IndexOf("RenderQueuedInputs();", StringComparison.Ordinal), "a stop draws its rows as stopping");
        return Task.CompletedTask;
    }
}
