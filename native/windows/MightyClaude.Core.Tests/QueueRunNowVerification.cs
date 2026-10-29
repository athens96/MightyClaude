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
            var pane = new QueueRunNow.Pane(Flag("busy"), Flag("heldForUpdate"), second, menuOnly, Flag("waitsOnBackgroundOnly"), p.GetProperty("held").GetInt32());
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
}
