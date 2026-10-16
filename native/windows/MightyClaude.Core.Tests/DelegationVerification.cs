using System.Text;
using System.Text.Json;
using MightyClaude.Core;
using static DelegationSupport;

/// <summary>What the delegation checks share: assertions that say what they saw, and the fixed values the Mac tests use.</summary>
internal static class DelegationSupport
{
    internal static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    internal static void Same<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{why}: expected <{expected}> but was <{actual}>"); }
    internal static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why)
    {
        var (was, wanted) = (actual.ToList(), expected.ToList());
        if (!was.SequenceEqual(wanted)) throw new InvalidOperationException($"{why}: expected [{string.Join(", ", wanted)}] but was [{string.Join(", ", was)}]");
    }
    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    internal static readonly string Commit = new('a', 40);
    internal static readonly DelegationCaller Parent = new("parent", Delegation: true);
    internal static DelegationCaller Caller(string pane, string kind = "claude", string provider = "claude", bool delegation = true) => new(pane, kind, provider, delegation);
    internal static DelegationRequest Request(string tool, params (string Name, string Value)[] arguments) => new(tool, arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
    internal static DelegationCopy Copy(string child, DelegationCopyKind kind, string text, long? revision = null) => DelegationCopy.Of(child, kind, revision ?? (kind == DelegationCopyKind.Report ? 1 : 0), Utf8(text));
    internal static async Task<bool> Eventually(Func<Task<bool>> done, int milliseconds = 60_000)
    {
        var end = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
        while (DateTimeOffset.UtcNow < end) { if (await done()) return true; await Task.Delay(10); }
        return await done();
    }
    internal static Task<bool> Eventually(Func<bool> done, int milliseconds = 60_000) => Eventually(() => Task.FromResult(done()), milliseconds);
    /// <summary>Removes a fixture folder; git's object files are read-only, which Windows does not delete.</summary>
    internal static void RemoveFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The delegation model, file, pruning, switch, sidebar and pane rules, ported from the Mac's DelegationModelTests,
/// DelegationFileStoreTests, DelegationPruningTests, DelegationSidebarTests, DelegationAppHostTests, DelegationSnapshotTests
/// and the parts of DelegationSwitchTests and DelegationCoordinatorTests that need no pane.
/// </summary>
internal static class DelegationVerification
{
    private static ChildRecord Record(string id = "child-1", string parent = "parent-1", ChildState state = ChildState.Creating, string? checkout = null) =>
        ChildRecord.Make(id, parent, Path.Combine(Path.GetTempPath(), "worktrees", id), "main", "abc123", "acceptEdits", "key-" + id, state: state, parentCheckout: checkout);

    internal static Task StatesAndMoves()
    {
        var states = Enum.GetValues<ChildState>(); var events = Enum.GetValues<ChildStateEvent>();
        SameList(states.Select(DelegationNames.Of), ["creating", "running", "waiting", "reported", "ended", "interrupted", "merged", "failed", "closed", "discarded"], "the states carry the ontology names");
        // The ontology's moves, written out by hand. Every (state, event) pair not listed here must be refused.
        var after = new Dictionary<ChildState, Dictionary<ChildStateEvent, ChildState>>
        {
            [ChildState.Creating] = new() { [ChildStateEvent.StartRun] = ChildState.Running, [ChildStateEvent.FailStart] = ChildState.Failed, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Running] = new() { [ChildStateEvent.AskPermission] = ChildState.Waiting, [ChildStateEvent.EndWithReport] = ChildState.Reported, [ChildStateEvent.EndWithoutReport] = ChildState.Ended, [ChildStateEvent.QuitApp] = ChildState.Interrupted, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Waiting] = new() { [ChildStateEvent.AnswerPermission] = ChildState.Running, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Reported] = new() { [ChildStateEvent.StartRun] = ChildState.Running, [ChildStateEvent.Merge] = ChildState.Merged, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Ended] = new() { [ChildStateEvent.StartRun] = ChildState.Running, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Interrupted] = new() { [ChildStateEvent.StartRun] = ChildState.Running, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Merged] = new() { [ChildStateEvent.StartRun] = ChildState.Running, [ChildStateEvent.UndoMerge] = ChildState.Reported, [ChildStateEvent.ClosePane] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Failed] = new() { [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Closed] = new() { [ChildStateEvent.UndoMerge] = ChildState.Closed, [ChildStateEvent.Discard] = ChildState.Discarded },
            [ChildState.Discarded] = [],
        };
        int allowed = 0, refused = 0, moves = 0;
        foreach (var state in states)
        {
            foreach (var change in events)
            {
                ChildState? expected = after[state].TryGetValue(change, out var next) ? next : null;
                Same(state.After(change), expected, $"{state} + {change}");
                if (expected is null) refused++; else allowed++;
            }
            foreach (var to in states)
            {
                var expected = to != state && after[state].ContainsValue(to);
                Same(state.CanMove(to), expected, $"{state} -> {to}");
                if (expected) moves++;
            }
        }
        Check(allowed == 30 && refused == 80 && moves == 29, "exactly the ontology's 30 allowed pairs, 80 refusals and 29 moves");
        // Undo of a merge: back to reported while the pane is open; a closed child stays closed. A failed start is only discarded. Discarded is final.
        Check(!ChildState.Closed.CanMove(ChildState.Reported) && !ChildState.Closed.CanMove(ChildState.Merged) && !ChildState.Closed.CanMove(ChildState.Running), "a closed child is never reopened");
        Check(ChildState.Failed.After(ChildStateEvent.ClosePane) is null && ChildState.Failed.After(ChildStateEvent.StartRun) is null && events.All(change => ChildState.Discarded.After(change) is null), "failed is only discarded, and discarded is final");
        SameList(states.Where(state => state.After(ChildStateEvent.Merge) is not null), [ChildState.Reported], "only a reported child merges");
        SameList(states.Where(state => state.After(ChildStateEvent.UndoMerge) is not null), [ChildState.Merged, ChildState.Closed], "undo is only for a merged or closed child");
        SameList(states.Where(state => state.IsOpen()), [ChildState.Creating, ChildState.Running, ChildState.Waiting, ChildState.Reported, ChildState.Ended, ChildState.Interrupted, ChildState.Merged], "open is every state except failed, closed and discarded");
        SameList(states.Where(state => state.After(ChildStateEvent.ClosePane) == ChildState.Closed), states.Where(state => state.IsOpen()), "exactly the open states can have their pane closed");

        // A record moves only by allowed events, and a refused event changes nothing.
        var child = Record();
        Check(child.Branch == "mighty/child-1" && child.State == ChildState.Creating && child.ReportRevision == 0 && child.ReportHead is null, "a new record is creating on its flat branch");
        void Step(ChildStateEvent change, bool moved, ChildState state)
        {
            var next = child.Applying(change);
            Check(next is not null == moved && (next ?? child).State == state && child.Applied(change) == (next ?? child), $"{change} from {child.State}");
            child = next ?? child;
        }
        bool Report(string head, bool moved)
        {
            var next = child.RecordingReport(head); var matched = next is not null == moved; child = next ?? child; return matched;
        }
        Step(ChildStateEvent.Merge, false, ChildState.Creating); Check(Report("def456", false), "no report before a run");
        Step(ChildStateEvent.StartRun, true, ChildState.Running); Check(Report("def456", true), "a running child reports");
        Check(child is { State: ChildState.Reported, ReportRevision: 1, ReportHead: "def456" }, "the first report is revision 1 at its head");
        // A new run (a follow-up or the human's send) clears reported; the next report is revision 2.
        Step(ChildStateEvent.StartRun, true, ChildState.Running); Check(Report("fed789", true) && child is { ReportRevision: 2, ReportHead: "fed789" }, "the next report is revision 2");
        Step(ChildStateEvent.Merge, true, ChildState.Merged); Step(ChildStateEvent.UndoMerge, true, ChildState.Reported);
        Step(ChildStateEvent.Merge, true, ChildState.Merged); Step(ChildStateEvent.ClosePane, true, ChildState.Closed); Step(ChildStateEvent.UndoMerge, true, ChildState.Closed);
        Step(ChildStateEvent.StartRun, false, ChildState.Closed); Check(Report("000", false) && child.ReportRevision == 2, "a closed child takes no report");
        Step(ChildStateEvent.Discard, true, ChildState.Discarded); Step(ChildStateEvent.Discard, false, ChildState.Discarded);
        return Task.CompletedTask;
    }

    internal static Task NamesRecordsAndRequestKey()
    {
        SameList(Enum.GetValues<DelegationReasonCode>().Select(DelegationNames.Of), [
            "claude_only", "child_cannot_delegate", "width_cap", "low_disk", "store_full", "not_git", "unborn_branch", "detached_head",
            "wider_mode", "discard_human_only", "not_reported", "tracked_changes", "head_moved", "diverged", "branch_not_checked_out",
            "child_closed", "parent_closed", "follow_up_limit", "worktree_missing", "merge_conflict", "undo_parent_moved", "parent_busy",
            "workspace_has_children", "held_not_removable", "human_started", "merge_human_only"], "the reason codes are the ontology list in order");
        SameList(Enum.GetValues<NoticeKind>().Select(DelegationNames.Of), ["reported", "ended_without_report", "failed_to_start"], "notice kinds");
        SameList(Enum.GetValues<DeliveryLane>().Select(DelegationNames.Of), ["pending", "held", "delivered"], "lanes");
        SameList(Enum.GetValues<DeliveryRoute>().Select(DelegationNames.Of), ["steer", "queue"], "routes");
        SameList(Enum.GetValues<MergeKind>().Select(DelegationNames.Of), ["tool_fast_forward", "card_fast_forward", "card_merge_commit"], "merge kinds");
        SameList(Enum.GetValues<ChildCleanupHold>().Select(DelegationNames.Of), ["not_merged", "child_busy", "worktree_missing", "head_moved", "tracked_changes", "untracked_files", "nested_worktree", "branch_not_checked_out", "not_in_parent", "link"], "the Mac's nine cleanup holds, and Windows' link");
        Check(DelegationNames.Parse<DelegationReasonCode>("width_cap") == DelegationReasonCode.WidthCap && DelegationNames.Parse<DelegationReasonCode>("WidthCap") is null, "a name parses back, and only the ontology's spelling");

        var receipt = new DeliveryReceipt("2026-10-09T00:00:00Z", DeliveryRoute.Steer, "run-2");
        var notice = new Notice("n1", "child-1", 1, NoticeKind.EndedWithoutReport, DeliveryLane.Delivered, receipt);
        var followUp = new FollowUp("f1", "child-1", "Also update the tests.", DeliveryLane.Held);
        var merge = new MergeRecord("child-1", MergeKind.CardMergeCommit, "main", "abc123", "fed789", "def456");
        var child = Record(state: ChildState.Merged) with { FollowUpCount = 2, ReportRevision = 3, ReportHead = "def456" };
        var noticeJson = Encoding.UTF8.GetString(DelegationFileStore.Encode(notice));
        Same(noticeJson, """{"childId":"child-1","id":"n1","kind":"ended_without_report","lane":"delivered","receipt":{"route":"steer","runId":"run-2","time":"2026-10-09T00:00:00Z"},"reportRevision":1}""", "a notice is written with the Mac's field names, sorted");
        Check(Encoding.UTF8.GetString(DelegationFileStore.Encode(merge)).Contains("\"card_merge_commit\"", StringComparison.Ordinal), "a merge kind is written by name");
        Check(JsonSerializer.Deserialize<Notice>(DelegationFileStore.Encode(notice), Wire.Json) == notice && JsonSerializer.Deserialize<FollowUp>(DelegationFileStore.Encode(followUp), Wire.Json) == followUp
            && JsonSerializer.Deserialize<MergeRecord>(DelegationFileStore.Encode(merge), Wire.Json) == merge && JsonSerializer.Deserialize<ChildRecord>(DelegationFileStore.Encode(child), Wire.Json) == child, "records round-trip");
        // The app's wire options write the same names, so a response crosses the pane pipe as the Mac's does.
        var response = JsonSerializer.Serialize(DelegationResponse.Refusal(DelegationReasonCode.WidthCap), Wire.Json);
        Same(response, """{"refused":"width_cap"}""", "a refusal on the wire");
        Check(JsonSerializer.Deserialize<DelegationResponse>(JsonSerializer.Serialize(new DelegationResponse(Merged: merge, Child: DelegationChildInfo.Of(child)), Wire.Json), Wire.Json) is { Merged: var back, Child.State: ChildState.Merged } && back == merge, "a response round-trips on the wire");
        Check(JsonSerializer.Deserialize<DelegationRequest>("""{"tool":"merge","arguments":{"child":"c1","expected_head":"abc"}}""", Wire.Json) is { Tool: "merge" } request && request.Arguments["expected_head"] == "abc", "a request reads from the wire");

        var key = DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits");
        // Golden values pin the encoding: each part as an 8-byte big-endian UTF-8 byte count and its bytes, then SHA-256.
        Same(key, "0767d17eeeaaa20c3f5c0e3a11dc260bd005cc116a76ea02adb2c78fb3a48ef7", "the request key is the Mac's");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Tidy the caf\u00e9 menu \u2192 done", "plan"), "7f4b512df24f7e3bc398f2a2ca4f9ac5ce328ce54ec824429a2c55bc3d49e976", "the request key hashes UTF-8");
        var variants = new[] { key, DelegationRequestKey.Make("parent-2", "run-1", "Fix the login bug", "acceptEdits"), DelegationRequestKey.Make("parent-1", "run-2", "Fix the login bug", "acceptEdits"),
            DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug.", "acceptEdits"), DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "plan") };
        Check(variants.Distinct().Count() == 5 && DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits") == key, "each part changes the key, and a repeat keeps it");
        Check(DelegationRequestKey.Make("ab", "c", "t", "plan") != DelegationRequestKey.Make("a", "bc", "t", "plan") && DelegationRequestKey.Make("p", "r", "do it plan", "") != DelegationRequestKey.Make("p", "r", "do it", " plan"), "moving text between parts never collides");
        return Task.CompletedTask;
    }

    private static DelegationFile Sample()
    {
        var reported = ChildRecord.Make("c1", "p1", @"C:\Temp\worktrees\c1", "main", "a1", "acceptEdits", "k1", followUpCount: 1, state: ChildState.Running).RecordingReport("b2")!;
        var closed = ChildRecord.Make("c2", "p1", "/tmp/worktrees/c2", "main", "a1", "plan", "k2", state: ChildState.Closed);
        return DelegationFile.Of([reported, closed],
            [new("n1", "c1", 1, NoticeKind.Reported, DeliveryLane.Delivered, new("2026-10-09T01:02:03Z", DeliveryRoute.Steer, "run-7")), new("n2", "c2", 0, NoticeKind.EndedWithoutReport, DeliveryLane.Held)],
            [new("f1", "c1", "\uD14C\uC2A4\uD2B8\uB3C4 \uCD94\uAC00\uD574 \uC918", DeliveryLane.Delivered, new("2026-10-09T01:05:00Z", DeliveryRoute.Queue, "run-8"))],
            [new("c1", MergeKind.ToolFastForward, "main", "a1", "b2", "b2")])
            .SettingCopy(Copy("c1", DelegationCopyKind.Task, "# \uD560 \uC77C\n\uBC84\uADF8\uB97C \uACE0\uCCD0\uB77C")).SettingCopy(Copy("c1", DelegationCopyKind.Report, "# \uBCF4\uACE0\n\uACE0\uCCE4\uB2E4"));
    }

    internal static async Task FileStoreSavesRestoresAndRefuses()
    {
        var folder = Verification.Temp();
        try
        {
            var missing = new DelegationFileStore(Path.Combine(folder, "profile"));
            Check(missing.Load() == DelegationFile.Empty && !File.Exists(missing.FilePath) && !Directory.Exists(missing.Folder), "a missing file loads as empty and creates nothing");

            // Beside the workspace state, with the same file name as the Mac.
            var state = new StateStore(folder); await state.SaveAsync(await state.LoadAsync());
            var store = new DelegationFileStore(folder); var file = Sample();
            store.Save(file);
            Check(store.FilePath == Path.Combine(folder, "delegation-state.json"), "the file is delegation-state.json in the profile folder");
            SameList(Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(), ["delegation-state.json", "workspace-state.json"], "a save leaves no temporary file beside the state");
            var restored = new DelegationFileStore(folder).Load();
            Check(restored == file && restored.Children[0] is { ReportRevision: 1, State: ChildState.Reported } && restored.Notices[0].Receipt?.RunId == "run-7" && restored.FollowUps[0].Receipt?.Route == DeliveryRoute.Queue && restored.Merges[0].MergedCommit == "b2", "a saved file restores exactly");
            Check(restored.Copy("c1", DelegationCopyKind.Task)?.Text == "# \uD560 \uC77C\n\uBC84\uADF8\uB97C \uACE0\uCCD0\uB77C" && restored.Copy("c1", DelegationCopyKind.Report)?.Truncated == false, "copies keep their text");
            var bytes = File.ReadAllBytes(store.FilePath);
            store.Save(restored);
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "saving the restored file writes the same bytes");
            // The Mac's encoder: version 1, sorted keys, escaped slashes and backslashes, text outside ASCII as it is, absent values left out.
            var text = Encoding.UTF8.GetString(bytes);
            Check(text.StartsWith("""{"children":[{"baseCommit":"a1","branch":"mighty\/c1","followUpCount":1,"id":"c1","parentBranch":"main","parentSessionId":"p1","reportHead":"b2","reportRevision":1,"requestKey":"k1","startingMode":"acceptEdits","state":"reported","worktreePath":"C:\\Temp\\worktrees\\c1"}""", StringComparison.Ordinal), "a child is written as the Mac writes it: " + text[..Math.Min(text.Length, 300)]);
            Check(text.EndsWith(""","version":1}""", StringComparison.Ordinal) && text.Contains("\uD14C\uC2A4\uD2B8\uB3C4", StringComparison.Ordinal) && !text.Contains("null", StringComparison.Ordinal) && !text.Contains("\\u", StringComparison.Ordinal), "the file is version 1 with plain text and no empty values");

            var replaced = file.SettingCopy(Copy("c1", DelegationCopyKind.Report, "# 2", 2));
            SameList(replaced.Copies.Where(copy => copy.ChildId == "c1" && copy.Kind == DelegationCopyKind.Report).Select(copy => copy.Revision), [2L], "a new copy replaces the child's earlier copy of the same kind");
            Check(replaced.Copy("c1", DelegationCopyKind.Task)?.Revision == 0, "the other kind's copy stays");

            // A save over 4 MiB is refused and the earlier file stays.
            Check(DelegationFileStore.MaximumFileBytes == 4 * 1024 * 1024 && DelegationFileStore.MaximumCopyBytes == 64 * 1024, "the caps are the Mac's");
            var before = File.ReadAllBytes(store.FilePath); var full = new string('r', DelegationFileStore.MaximumCopyBytes * 2);
            var fits = DelegationFile.Empty;
            for (var index = 0; index < 63; index++) fits = fits.SettingCopy(Copy("c" + index, DelegationCopyKind.Report, full));
            Check(DelegationFileStore.Encode(fits).Length <= DelegationFileStore.MaximumFileBytes, "63 full copies fit");
            var over = fits.SettingCopy(Copy("c63", DelegationCopyKind.Report, full)).SettingCopy(Copy("c64", DelegationCopyKind.Report, full));
            Check(DelegationFileStore.Encode(over).Length > DelegationFileStore.MaximumFileBytes, "65 do not");
            try { store.Save(over); throw new InvalidOperationException("a file over the cap was saved"); } catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { }
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(before) && store.Load() == Sample(), "a refused save leaves the earlier file");

            // An oversized or damaged file, one of another version or one missing a field is refused on load and left alone.
            foreach (var damaged in new[] { new byte[DelegationFileStore.MaximumFileBytes + 1], Utf8("{"), Utf8("""{"version":2,"children":[],"notices":[],"followUps":[],"merges":[],"copies":[]}"""),
                Utf8("""{"version":1,"children":[],"notices":[],"followUps":[],"merges":[]}"""), Utf8("""{"version":1,"children":[null],"notices":[],"followUps":[],"merges":[],"copies":[]}"""),
                Utf8("""{"version":1,"children":[],"notices":[{"id":"n","childId":"c","reportRevision":0,"kind":"shouted","lane":"held"}],"followUps":[],"merges":[],"copies":[]}"""),
                Utf8("""{"version":1,"children":[],"notices":[{"id":"n","childId":"c","reportRevision":0,"kind":"reported"}],"followUps":[],"merges":[],"copies":[]}""") })
            {
                File.WriteAllBytes(store.FilePath, damaged);
                try { store.Load(); throw new InvalidOperationException("a damaged file was loaded: " + Encoding.UTF8.GetString(damaged, 0, Math.Min(damaged.Length, 80))); } catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Unreadable) { }
                Check(File.ReadAllBytes(store.FilePath).SequenceEqual(damaged), "a refused load leaves the file as it was");
            }
            File.WriteAllBytes(store.FilePath, Utf8("""{"version":1,"children":[],"notices":[],"followUps":[],"merges":[],"copies":[],"later":"ignored"}"""));
            Check(store.Load() == DelegationFile.Empty, "a key this version does not know is passed over");
            File.Delete(store.FilePath); Directory.CreateDirectory(store.FilePath);
            try { store.Load(); throw new InvalidOperationException("a folder was loaded"); } catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Unreadable) { }

            // Windows: another program holding the file open for a moment only delays the replace; one that keeps it fails the save honestly.
            Directory.Delete(store.FilePath); store.Save(Sample());
            var release = Task.Run(async () => { using var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read); await Task.Delay(400); });
            await Task.Delay(100);
            store.Save(Sample().SettingCopy(Copy("c2", DelegationCopyKind.Report, "after the wait")));
            await release;
            Check(store.Load().Copy("c2", DelegationCopyKind.Report)?.Text == "after the wait", "a save that met a brief reader is written once the reader lets go");
            using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var kept = File.ReadAllBytes(store.FilePath);
                try { new DelegationFileStore(folder, 3).Save(Sample()); throw new InvalidOperationException("a save replaced a file another program holds open"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Check(DelegationFileStore.IsSaveFailure(ex), "a refused replace is a save failure"); }
                Check(File.ReadAllBytes(store.FilePath).SequenceEqual(kept), "a failed replace leaves the earlier file");
            }
            SameList(Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(), ["delegation-state.json", "workspace-state.json"], "a failed save leaves no temporary file");
        }
        finally { RemoveFolder(folder); }
    }

    internal static Task CopiesAreCutAt64KiB()
    {
        const int cap = DelegationFileStore.MaximumCopyBytes; const string marker = DelegationCopy.TruncationMarker;
        Same(marker, "\n\n[truncated at 64 KiB: the full file stays in the child's worktree until cleanup]\n", "the marker is the Mac's");
        var exact = Copy("c1", DelegationCopyKind.Report, new string('a', cap));
        Check(!exact.Truncated && Encoding.UTF8.GetByteCount(exact.Text) == cap && !exact.Text.EndsWith(marker, StringComparison.Ordinal), "a file of exactly 64 KiB is kept whole");
        var text = new string('a', cap + 1); var cut = Copy("c1", DelegationCopyKind.Report, text);
        Check(cut.Truncated && cut.OriginalBytes == cap + 1 && cut.Text.EndsWith(marker, StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(cut.Text) == cap && text.StartsWith(cut.Text[..^marker.Length], StringComparison.Ordinal), "one byte more is cut behind the marker to 64 KiB");

        // Three-byte Korean characters are never split, and are stored as they are, not as escapes.
        var korean = string.Concat(Enumerable.Repeat("\uBCF4\uACE0\uC11C", 30_000)); var cutKorean = Copy("c1", DelegationCopyKind.Report, korean); var kept = cutKorean.Text[..^marker.Length];
        Check(cutKorean.Truncated && Encoding.UTF8.GetByteCount(cutKorean.Text) <= cap && cap - Encoding.UTF8.GetByteCount(cutKorean.Text) < 3 && korean.StartsWith(kept, StringComparison.Ordinal) && kept.All(character => "\uBCF4\uACE0\uC11C".Contains(character)), "Korean text is cut at a character");
        // Inside the file a copy never takes more than the cap, its escapes included; only the marker's own escapes come on top.
        Check(DelegationCopy.StoredBytes("a\"b\\c/d\ne\u001bf\uD55C") == 1 + 2 + 1 + 2 + 1 + 2 + 1 + 2 + 1 + 6 + 1 + 3, "quotes, backslashes, slashes and newlines take two bytes, a colour code six, Korean three");
        foreach (var body in new[] { new string('"', cap), string.Concat(Enumerable.Repeat("line\n", cap / 5 + 1)), string.Concat(Enumerable.Repeat("\u001b[31mred\u001b[0m ", 5_000)), string.Concat(Enumerable.Repeat("\uD83D\uDC69\u200D\uD83D\uDCBB ", 8_000)) })
        {
            var copy = Copy("c1", DelegationCopyKind.Report, body);
            Check(copy.Truncated && DelegationCopy.StoredBytes(copy.Text[..^marker.Length]) <= cap - Encoding.UTF8.GetByteCount(marker) && body.StartsWith(copy.Text[..^marker.Length], StringComparison.Ordinal) && DelegationFile.ElementBytes(copy) <= DelegationFile.ReportCopyRoom("c1"), "a copy with escapes stays within its room in the file");
            // Cut by the room it takes in the file: one more character would not fit.
            var length = copy.Text.Length - marker.Length; var next = System.Globalization.StringInfo.ParseCombiningCharacters(body).FirstOrDefault(start => start > length, body.Length);
            Check(!char.IsHighSurrogate(copy.Text[^(marker.Length + 1)]) && DelegationCopy.StoredBytes(body[..next]) > cap - Encoding.UTF8.GetByteCount(marker), "a character is never split, and one more would not fit");
        }
        Check(DelegationCopy.Start("\uD83D\uDC69\u200D\uD83D\uDCBB\uD83D\uDC69\u200D\uD83D\uDCBB", 12) == "\uD83D\uDC69\u200D\uD83D\uDCBB" && DelegationCopy.Start("abc", 0).Length == 0, "the start is whole characters within the budget");

        // Reading a huge report stores only the cut copy, and never loads the file whole.
        var folder = Verification.Temp();
        try
        {
            var report = Path.Combine(folder, "REPORT.md"); var body = Utf8(string.Concat(Enumerable.Repeat("line of the report\n", 60_000)));
            File.WriteAllBytes(report, body);
            var read = DelegationCopy.Read("c1", DelegationCopyKind.Report, 3, report);
            Check(read == DelegationCopy.Of("c1", DelegationCopyKind.Report, 3, body) && read.Truncated && read.OriginalBytes == body.Length && Encoding.UTF8.GetByteCount(read.Text) <= cap, "a long report reads as its cut copy");
            File.WriteAllBytes(report, Utf8("# \uC9E7\uC740 \uBCF4\uACE0"));
            Check(DelegationCopy.Read("c1", DelegationCopyKind.Report, 4, report) is { Truncated: false, Text: "# \uC9E7\uC740 \uBCF4\uACE0" } && DelegationCopy.TryRead("c1", DelegationCopyKind.Report, 4, Path.Combine(folder, "missing.md")) is null, "a short report reads whole, and a missing one as nothing");
        }
        finally { RemoveFolder(folder); }
        return Task.CompletedTask;
    }

    // Pruning: parent p1 is open, p2 is closed; no merge can be undone.
    private static readonly DelegationPruneContext Open = new(new HashSet<string> { "p1" }, _ => false);
    private const int Cap = DelegationFileStore.MaximumFileBytes, FullCopy = DelegationFileStore.MaximumCopyBytes;
    private static ChildRecord Pruned(string id, ChildState state, string parent = "p1") => ChildRecord.Make(id, parent, "/tmp/worktrees/" + id, "main", "a1", "acceptEdits", "k-" + id, state: state);
    private static DelegationCopy Filler(string id, DelegationCopyKind kind = DelegationCopyKind.Report, int length = 1_000) => Copy(id, kind, new string('r', length));
    private static Notice Sent(string id, DeliveryLane lane, NoticeKind kind = NoticeKind.Reported) => new("n-" + id, id, 1, kind, lane);
    private static MergeRecord Merged(string id) => new(id, MergeKind.ToolFastForward, "main", "a1", "m-" + id, "m-" + id);
    private static int Size(DelegationFile file) => DelegationFileStore.Encode(file).Length;
    private static DelegationFile? Freeing(int bytes, DelegationFile file, DelegationPruneContext context) => file.Pruned(context, Cap - Size(file) + bytes);
    private static DelegationFile WithoutReports(DelegationFile file, params string[] ids) => file with { Copies = file.Copies.RemoveAll(copy => ids.Contains(copy.ChildId) && copy.Kind == DelegationCopyKind.Report) };
    private static DelegationFile WithoutRecords(DelegationFile file, params string[] ids) => file with
    {
        Children = file.Children.RemoveAll(child => ids.Contains(child.Id)), Notices = file.Notices.RemoveAll(notice => ids.Contains(notice.ChildId)), FollowUps = file.FollowUps.RemoveAll(followUp => ids.Contains(followUp.ChildId)),
        Merges = file.Merges.RemoveAll(merge => ids.Contains(merge.ChildId)), Copies = file.Copies.RemoveAll(copy => ids.Contains(copy.ChildId)),
    };

    /// <summary><paramref name="start"/> plus open filler children whose report copies leave exactly <paramref name="room"/> bytes under the cap (a negative room passes it).</summary>
    private static DelegationFile Filled(DelegationFile start, int room)
    {
        var file = start; var index = 0;
        void Add(int length) { var id = $"fill{index++:000}"; file = (file with { Children = file.Children.Add(Pruned(id, ChildState.Running)) }).SettingCopy(Filler(id, length: length)); }
        // Whole 64 KiB fillers, measured once, until about three are left to fit.
        var whole = (Cap - room - Size(file)) / (FullCopy + 1_000) - 3;
        for (var count = 0; count < whole; count++) Add(FullCopy);
        while (true)
        {
            Add(10_000);
            var length = 10_000 + Cap - room - Size(file);
            Check(length >= 10_000, "the filler has room to grow");
            if (length <= FullCopy) return file.SettingCopy(Filler(file.Children[^1].Id, length: length));
            file = file.SettingCopy(Filler(file.Children[^1].Id, length: Math.Min(FullCopy, length - 30_000)));
        }
    }

    internal static Task PruningGoesOldestFirstAndKeepsWhatItMust()
    {
        var small = DelegationFile.Of([Pruned("c1", ChildState.Closed)], [Sent("c1", DeliveryLane.Delivered)], copies: [Filler("c1")]);
        Check(small.Pruned(Open) == small && Freeing(0, small, Open) == small, "a file that fits comes back unchanged");

        var file = DelegationFile.Of([Pruned("c1", ChildState.Closed), Pruned("c2", ChildState.Running), Pruned("c3", ChildState.Discarded), Pruned("c4", ChildState.Closed)],
            [Sent("c1", DeliveryLane.Delivered), Sent("c3", DeliveryLane.Delivered), Sent("c4", DeliveryLane.Delivered)],
            [new("f1", "c1", "\uD14C\uC2A4\uD2B8\uB3C4 \uCD94\uAC00\uD574 \uC918", DeliveryLane.Delivered)], [Merged("c1")],
            [Filler("c1", DelegationCopyKind.Task), Filler("c1"), Filler("c2"), Filler("c3"), Filler("c4")]);
        var reportBytes = new[] { "c1", "c3", "c4" }.Select(id => DelegationFile.ElementBytes(Filler(id))).ToArray();
        // The oldest closed record's report copy goes first; its TASK copy and record stay. Then the next closed record's (a discarded child is closed too); the open child's copy is kept.
        Check(Freeing(1, file, Open) == WithoutReports(file, "c1") && Freeing(reportBytes[0] + 1, file, Open) == WithoutReports(file, "c1", "c3"), "report copies of the oldest closed records go first");
        // Only when every closed report copy is gone does the oldest closed, fully delivered record go, with its notice, follow-up, merge and TASK copy.
        var oldestGone = Freeing(reportBytes.Sum() + 1, file, Open);
        Check(oldestGone == WithoutRecords(WithoutReports(file, "c1", "c3", "c4"), "c1") && oldestGone?.Copy("c2", DelegationCopyKind.Report) == Filler("c2"), "then the oldest delivered record goes whole");
        SameList(oldestGone!.Children.Select(child => child.Id), ["c2", "c3", "c4"], "the others stay");
        var firstRecordBytes = DelegationFile.ElementBytes(file.Children[0]) + DelegationFile.ElementBytes(file.Notices[0]) + DelegationFile.ElementBytes(file.FollowUps[0]) + DelegationFile.ElementBytes(file.Merges[0]) + DelegationFile.ElementBytes(file.Copies[0]);
        SameList(Freeing(reportBytes.Sum() + firstRecordBytes + 1, file, Open)!.Children.Select(child => child.Id), ["c2", "c4"], "the next oldest record goes next");

        // Open children, failed children, held items of open panes and undoable merges are never pruned.
        var guarded = DelegationFile.Of(
            [Pruned("open", ChildState.Running), Pruned("waiting", ChildState.Waiting), Pruned("failed", ChildState.Failed), Pruned("heldOpenParent", ChildState.Closed), Pruned("heldClosedParent", ChildState.Closed, "p2"),
             Pruned("pendingNotice", ChildState.Closed), Pruned("heldFollowUp", ChildState.Closed), Pruned("undoable", ChildState.Closed), Pruned("merged", ChildState.Closed)],
            [Sent("open", DeliveryLane.Held), Sent("failed", DeliveryLane.Delivered, NoticeKind.FailedToStart), Sent("heldOpenParent", DeliveryLane.Held), Sent("heldClosedParent", DeliveryLane.Held), Sent("pendingNotice", DeliveryLane.Pending),
             Sent("undoable", DeliveryLane.Delivered), Sent("merged", DeliveryLane.Delivered)],
            [new("f-open", "open", "one more", DeliveryLane.Held), new("f-held", "heldFollowUp", "finish it", DeliveryLane.Held)], [Merged("undoable"), Merged("merged")],
            [Filler("open", DelegationCopyKind.Task), Filler("open"), Filler("waiting"), Filler("failed", DelegationCopyKind.Task), Filler("heldOpenParent"), Filler("heldClosedParent"), Filler("pendingNotice"), Filler("heldFollowUp"), Filler("undoable"), Filler("merged")]);
        var canUndo = new DelegationPruneContext(new HashSet<string> { "p1", "open", "waiting" }, merge => merge.MergedCommit == "m-undoable");
        var expected = WithoutRecords(WithoutReports(guarded, "heldOpenParent", "heldClosedParent", "pendingNotice", "heldFollowUp", "undoable", "merged"), "heldClosedParent", "merged");
        Check(guarded.Pruned(canUndo, Cap - Size(expected)) == expected, "everything allowed is pruned, and nothing else");
        SameList(expected.Children.Select(child => child.Id), ["open", "waiting", "failed", "heldOpenParent", "pendingNotice", "heldFollowUp", "undoable"], "the protected records stay");
        Check(expected.Merges.SequenceEqual([Merged("undoable")]) && expected.FollowUps.Select(followUp => followUp.Id).SequenceEqual(["f-open", "f-held"]) && expected.Copy("open", DelegationCopyKind.Report) is not null
            && expected.Copy("waiting", DelegationCopyKind.Report) is not null && expected.Copy("failed", DelegationCopyKind.Task) is not null, "an undoable merge, held follow-ups and open children's copies stay");
        Check(guarded.Pruned(canUndo, Cap - Size(expected) + 1) is null, "needing one byte more than that is not possible");
        // With p1 closed too, the notice held for its closed child counts as delivered.
        var parentClosed = new DelegationPruneContext(new HashSet<string> { "open", "waiting" }, merge => merge.MergedCommit == "m-undoable");
        var more = WithoutRecords(expected, "heldOpenParent");
        Check(guarded.Pruned(parentClosed, Cap - Size(more)) == more, "a notice held for a closed parent's child counts as delivered");
        return Task.CompletedTask;
    }

    internal static Task ANewChildNeedsRoomAndSavingNearTheCapPrunes()
    {
        var newChild = Pruned("new", ChildState.Creating); var task = Filler("new", DelegationCopyKind.Task, 200);
        // Only open children: nothing can be pruned, so there is no room for a record and a full report copy.
        var openOnly = Filled(DelegationFile.Empty, 100);
        Check(Size(openOnly) == Cap - 100 && openOnly.Admitting(newChild, Open, task) == new DelegationAdmission.Refused(DelegationReasonCode.StoreFull), "a full file of open children refuses a new child with store_full");
        // A closed record whose notice is still held for its open parent is protected too: dropping its report copy alone is not room enough.
        var oldCopies = new[] { Filler("old", DelegationCopyKind.Task, FullCopy), Filler("old", length: FullCopy) };
        var held = Filled(DelegationFile.Of([Pruned("old", ChildState.Closed)], [Sent("old", DeliveryLane.Held)], copies: oldCopies), 100);
        Check(held.Admitting(newChild, Open, task) == new DelegationAdmission.Refused(DelegationReasonCode.StoreFull), "a held notice of an open parent protects its record");
        // Pruning makes room for a new child when it can.
        var roomy = Filled(DelegationFile.Of([Pruned("old", ChildState.Closed)], [Sent("old", DeliveryLane.Delivered)], copies: oldCopies), 100);
        Check(roomy.Admitting(newChild, Open, task) is DelegationAdmission.Admitted { File: var admitted } && admitted.Children[^1] == newChild && admitted.Copy("new", DelegationCopyKind.Task) == task
            && admitted.Children.All(child => child.Id != "old") && admitted.Copies.All(copy => copy.ChildId != "old") && admitted.Children.Count == roomy.Children.Count
            && Size(admitted) + DelegationFile.ReportCopyRoom("new") <= Cap, "the closed, delivered record makes room");

        var folder = Verification.Temp();
        try
        {
            var store = new DelegationFileStore(Path.Combine(folder, "profile"));
            // Less than one more child's room left: the closed record's report copy goes.
            var near = Filled(DelegationFile.Of([Pruned("old", ChildState.Closed)], [Sent("old", DeliveryLane.Delivered)], copies: [Filler("old", DelegationCopyKind.Task), Filler("old", length: FullCopy)]), 10_000);
            var saved = store.Save(near, Open);
            Check(saved == WithoutReports(near, "old") && store.Load() == saved && Size(saved) + DelegationFile.NewChildRoom() <= Cap, "a save near the cap prunes to one more child's room");
            // A file that fits but has nothing to prune is saved as it is; one over the cap with nothing to prune is refused and nothing is written.
            var openFile = Filled(DelegationFile.Empty, 10);
            Check(store.Save(openFile, Open) == openFile, "a file with nothing to prune is saved whole");
            var before = File.ReadAllBytes(store.FilePath);
            try { store.Save(Filled(DelegationFile.Empty, -10), Open); throw new InvalidOperationException("a file over the cap was saved"); } catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { }
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(before), "a refused save writes nothing");
        }
        finally { RemoveFolder(folder); }
        return Task.CompletedTask;
    }

    internal static async Task SwitchGatesAndTheSixTools()
    {
        // The switch, Mighty Orchestration: on unless a REG_DWORD of zero turns it off, read at each call. The checks never write the registry.
        Check(DelegationSwitch.RegistryKey == @"Software\MightyClaudeNative" && DelegationSwitch.ValueName == "delegation.enabled", "the switch is delegation.enabled under the app's key");
        Check(DelegationSwitch.IsOn(null) && !DelegationSwitch.IsOn(0) && DelegationSwitch.IsOn(1) && DelegationSwitch.IsOn(-1) && DelegationSwitch.IsOn("0") && DelegationSwitch.IsOn(0L) && DelegationSwitch.IsOn(false), "only a DWORD of zero turns it off; a missing value is on");
        _ = DelegationSwitch.IsOn();
        Check(DelegationSwitch.IsClaudePane("claude", "claude") && !DelegationSwitch.IsClaudePane("claude", "codex") && !DelegationSwitch.IsClaudePane("claude", "gemini") && !DelegationSwitch.IsClaudePane("shell", "claude") && !DelegationSwitch.IsClaudePane("browser", "claude"), "only an agent pane running Claude is a Claude pane");

        // With no coordinator behind it: off reaches nothing, a pane whose agent has no delegation tools is refused, a run without the server stays detached.
        var delegate_ = Request("delegate", ("task", "Write the release notes"), ("mode", "plan"));
        var on = new DelegationIOHandler(() => true); var off = new DelegationIOHandler(() => false);
        Check((await on.Handle(delegate_, Caller("codex", provider: "codex"))) is { Refused: null, Error: "Unknown delegation tool delegate." } && await on.Handle(delegate_, Caller("codex", provider: "codex", delegation: false)) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage),
            "a Codex pane with the server attached reaches the tools; one without it stays detached");
        foreach (var caller in new[] { Caller("gemini", provider: "gemini"), Caller("shell", kind: "shell") })
            foreach (var tool in new[] { "delegate", "list_children", "child_status", "merge", "follow_up", "discard", "no_such_tool" })
                Check(await on.Handle(tool == "delegate" ? delegate_ : Request(tool, ("child", "child-1")), caller) == DelegationResponse.Refusal(DelegationReasonCode.ClaudeOnly), $"{caller.PaneId} {tool} is claude_only");
        Check((await on.Handle(delegate_, Caller("claude"))) is { Refused: null, Error: "Unknown delegation tool delegate." }, "a Claude pane with the server attached is not refused as a non-Claude pane");
        foreach (var caller in new[] { Caller("codex", provider: "codex"), Caller("claude") })
            Check(await off.Handle(delegate_, caller) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage), "switched off, every call reaches nothing");
        Check(await on.Handle(delegate_, Caller("claude", delegation: false)) == DelegationResponse.Failure(DelegationIOHandler.DetachedMessage), "a run started while the switch was off stays detached");
        Same(DelegationIOHandler.DetachedMessage, "The delegation tools are not attached to this pane's run.", "the detached answer is the Mac's");
        Check(DelegationRequest.IsToolName("child_status") && DelegationRequest.IsToolName("a1") && !DelegationRequest.IsToolName("Delegate") && !DelegationRequest.IsToolName("1a") && !DelegationRequest.IsToolName("") && !DelegationRequest.IsToolName("a-b") && !DelegationRequest.IsToolName(new string('a', 65)) && DelegationRequest.IsToolName(new string('a', 64)), "tool names are lowercase words of at most 64");

        // The six tools, their arguments and their words.
        string[] six = ["delegate", "list_children", "child_status", "merge", "follow_up", "discard"];
        SameList(DelegationToolManifest.All.Select(tool => tool.Name), six, "exactly the six tools in order");
        Check(!six.Intersect(AgentIOTools.All.Select(tool => tool.Name)).Any(), "none of them is a mighty-terminal tool");
        var arguments = new Dictionary<string, string[]> { ["delegate"] = ["task", "mode"], ["list_children"] = [], ["child_status"] = ["child"], ["merge"] = ["child", "expected_head"], ["follow_up"] = ["child", "text"], ["discard"] = ["child"] };
        foreach (var tool in DelegationToolManifest.All) SameList(tool.Arguments.Select(argument => argument.Name), arguments[tool.Name], tool.Name + " takes its arguments");
        Check(DelegationToolManifest.Delegate.Description.StartsWith("Start a child pane running your own agent CLI that works on a task in its own git worktree, on the flat branch mighty/<child id> made from your current branch head.", StringComparison.Ordinal)
            && DelegationToolManifest.Delegate.Description.EndsWith("At most 3 open children per pane, and a child cannot delegate. The same call twice in one of your runs returns the same child.", StringComparison.Ordinal)
            && DelegationToolManifest.Discard.Description == "Discarding a child is for the human only, from the child's card in the app. This tool always refuses with discard_human_only and changes nothing."
            && DelegationToolManifest.Merge.Arguments[1].Description == "The child's reported head commit, as child_status gave it.", "the agent reads the Mac's words");
        Check(DelegationCoordinator.MaximumOpenChildren == 3 && DelegationCoordinator.MaximumDepth == 1 && DelegationCoordinator.MaximumFollowUps == 2 && DelegationCoordinator.MaximumFollowUpBytes == 48 * 1024
            && ChildWorktreeMaker.MinimumFreeBytes == 10_000_000_000 && DelegationCoordinator.AnswerSeconds == 45 && DelegationCoordinator.DeliveryOffers == 2, "the limits are the Mac's");
        Check(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["child"] = "c" }, DelegationToolManifest.Merge) == "merge takes exactly these string arguments: child, expected_head."
            && DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["child"] = "c" }, DelegationToolManifest.ListChildren) == "list_children takes no arguments."
            && DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["text"] = "t", ["child"] = "c" }, DelegationToolManifest.FollowUp) is null, "a call needs exactly its tool's arguments");

        // A tool still working at the limit is answered once, and its work goes on.
        var gate = new TaskCompletionSource(); var finished = new TaskCompletionSource(); var late = DelegationResponse.Failure("late");
        var answer = await DelegationCoordinator.Answer(0.2, late, async () => { await gate.Task; finished.SetResult(); return DelegationResponse.Refusal(DelegationReasonCode.StoreFull); });
        Check(answer == late, "the late answer comes at the limit");
        gate.SetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Check(await DelegationCoordinator.Answer(30, late, () => Task.FromResult(DelegationResponse.Refusal(DelegationReasonCode.NotGit))) == DelegationResponse.Refusal(DelegationReasonCode.NotGit), "a quick tool answers itself");
        Same(DelegationCoordinator.LateMessage("merge", DelegationCoordinator.AnswerSeconds), "merge did not finish within 45 seconds. Its work goes on in Mighty Claude; call list_children to see where your children stand.", "the late answer's words");
    }

    private static RunSession Pane(string id, string workspace, string? parent = null) => new() { Id = id, WorkspaceId = workspace, Title = id, ParentSessionId = parent };
    private static DelegationChildRow Row(string id, string parent, ChildState state = ChildState.Running, string? checkout = null) => new(id, parent, state, ParentBranch: "main", ParentCheckout: checkout);

    internal static Task SidebarTreeRowsAndCards()
    {
        Workspace[] workspaces = [new() { Id = "w1", Name = "repo", Path = @"C:\repo" }, new() { Id = "w2", Name = "sub", Path = @"C:\repo\sub" }, new() { Id = "w3", Name = "app", Path = @"C:\other\app" }];
        // A parent's close counts its open children and a child pane whose row has not arrived.
        RunSession[] family = [Pane("p1", "w1"), Pane("c1", "w1", "p1"), Pane("c3", "w1", "p1"), Pane("q1", "w1", "q")];
        DelegationChildRow[] known = [Row("c1", "p1"), Row("c2", "p1", ChildState.Failed), Row("x", "q", ChildState.Reported)];
        Check(DelegationSidebar.OpenChildCount("p1", known, family) == 2 && DelegationSidebar.OpenChildCount("p1", [], family) == 2 && DelegationSidebar.OpenChildCount("p1", known.Where(row => row.Id != "c1"), [Pane("p1", "w1")]) == 0, "a parent's close counts its open children");

        // Children are listed under their open parent in the order they were made; a pane with no record stays at the top.
        RunSession[] sessions = [Pane("p1", "w1"), Pane("c1", "w1", "p1"), Pane("x", "w1"), Pane("stray", "w1", "p1"), Pane("q", "w2")];
        DelegationChildRow[] children = [Row("c2", "p1", ChildState.Failed), Row("c1", "p1", ChildState.Reported)];
        var tree = DelegationSidebar.Tree("w1", workspaces, sessions, children);
        SameList(tree.Top.Select(pane => pane.Id), ["p1", "x", "stray"], "a child pane is listed once, under its parent");
        Check(tree.Children["p1"].SequenceEqual(children) && tree.ParentClosed.Count == 0, "children keep the order they were made in");
        var other = DelegationSidebar.Tree("w2", workspaces, sessions, children);
        Check(other.Top.Select(pane => pane.Id).SequenceEqual(["q"]) && other.Children.Count == 0 && other.ParentClosed.Count == 0, "another workspace lists none of them");

        // Children of a closed parent are listed under a 'parent closed' node. Folders compare without regard to case or separator.
        RunSession[] open = [Pane("p1", "w1"), Pane("c3", "w2", "gone")];
        DelegationChildRow[] orphans =
        [
            Row("c3", "gone", checkout: @"C:\repo"),                         // its own pane is open in w2
            Row("c4", "gone", ChildState.Closed, @"C:\repo"),                // no pane: the workspace at the checkout itself
            Row("c5", "gone2", ChildState.Failed, "c:/REPO/"),               // the same folder written another way
            Row("c6", "gone3", ChildState.Ended, @"C:\other"),               // the first workspace inside the checkout
            Row("c7", "gone4", ChildState.Merged),                           // no pane and no checkout: not placed
            Row("c8", "gone", ChildState.Interrupted, @"C:\repo"),
            Row("c9", "gone5", ChildState.Closed, @"C:\rep"),                // a folder whose name only starts the same holds no workspace
        ];
        var w1 = DelegationSidebar.Tree("w1", workspaces, open, orphans); var w2 = DelegationSidebar.Tree("w2", workspaces, open, orphans); var w3 = DelegationSidebar.Tree("w3", workspaces, open, orphans);
        Check(w1.Top.Select(pane => pane.Id).SequenceEqual(["p1"]) && w1.Children.Count == 0 && w1.ParentClosed.Select(node => node.Id).SequenceEqual(["gone", "gone2"])
            && w1.ParentClosed[0].Children.Select(row => row.Id).SequenceEqual(["c4", "c8"]) && w1.ParentClosed[0].ParentBranch == "main", "a closed parent's children are listed at its checkout's workspace");
        Check(w2.Top.Count == 0 && w2.ParentClosed.Select(node => node.Id).SequenceEqual(["gone"]) && w2.ParentClosed[0].Children.Select(row => row.Id).SequenceEqual(["c3"]) && w3.ParentClosed.Select(node => node.Id).SequenceEqual(["gone3"]), "a child with its own pane is listed in that pane's workspace");
        SameList(new[] { w1, w2, w3 }.SelectMany(listed => listed.ParentClosed.SelectMany(node => node.Children)).Select(row => row.Id).Order(), ["c3", "c4", "c5", "c6", "c8"], "each placed child is listed once");

        // A row shows its state, with waiting while a running child asks a human.
        Check(DelegationSidebar.ShownState(ChildState.Running, true) == ChildState.Waiting && DelegationSidebar.ShownState(ChildState.Running, false) == ChildState.Running
            && DelegationSidebar.ShownState(ChildState.Waiting, false) == ChildState.Waiting && DelegationSidebar.ShownState(ChildState.Reported, true) == ChildState.Reported, "the shown state");
        foreach (var (state, tone) in new[] { (ChildState.Creating, DesignTone.Run), (ChildState.Running, DesignTone.Run), (ChildState.Waiting, DesignTone.Wait), (ChildState.Reported, DesignTone.Done), (ChildState.Merged, DesignTone.Done),
            (ChildState.Ended, DesignTone.Idle), (ChildState.Interrupted, DesignTone.Stop), (ChildState.Failed, DesignTone.Err), (ChildState.Closed, DesignTone.Idle) })
            Same(DelegationSidebar.Tone(state), tone, state + " tone");
        foreach (var state in Enum.GetValues<ChildState>()) Check(Locale.Catalogue("ko").ContainsKey("delegation.child.state." + DelegationNames.Of(state)) && Locale.Catalogue("en").ContainsKey("delegation.child.state." + DelegationNames.Of(state)), state + " has its word in the catalogues");

        // Rows leave out discarded children and carry the task in one line; a row counts only its child's held notices.
        ChildRecord Listed(string id, ChildState state) => ChildRecord.Make(id, "p", @"C:\worktrees\" + id, "main", "base", "auto", "key-" + id, state: state, parentCheckout: @"C:\repo");
        var file = DelegationFile.Of([Listed("a", ChildState.Reported), Listed("b", ChildState.Discarded), Listed("c", ChildState.Failed)], copies: [Copy("a", DelegationCopyKind.Task, "Fix the login\n  bug"), Copy("a", DelegationCopyKind.Report, "done")]);
        var rows = DelegationSidebar.Rows(file);
        Check(rows.Select(row => row.Id).SequenceEqual(["a", "c"]) && rows[0] == new DelegationChildRow("a", "p", ChildState.Reported, "Fix the login bug", "main", @"C:\repo") && rows[1].Task is null, "rows leave out discarded children and shorten the task");
        var receipt = new DeliveryReceipt("2026-10-10T01:00:00Z", DeliveryRoute.Queue, "run-1");
        var counted = DelegationFile.Of([Listed("a", ChildState.Ended), Listed("b", ChildState.Reported)], [new("n1", "a", 0, NoticeKind.EndedWithoutReport, DeliveryLane.Held), new("n2", "a", 1, NoticeKind.Reported, DeliveryLane.Delivered, receipt),
            new("n3", "a", 1, NoticeKind.Reported, DeliveryLane.Pending), new("n4", "a", 2, NoticeKind.Reported, DeliveryLane.Held), new("n5", "b", 1, NoticeKind.Reported, DeliveryLane.Delivered, receipt)]);
        SameList(DelegationSidebar.Rows(counted).Select(row => row.HeldNotices), [2, 0], "a row counts only its child's held notices");

        // The card's actions: merge while reported, undo while the last merge can be undone, discard until discarded.
        SameList(Row("a", "p", ChildState.Reported).CardActions, [DelegationCardAction.Merge, DelegationCardAction.Discard], "a reported child's card");
        SameList((Row("a", "p", ChildState.Merged) with { CanUndo = true }).CardActions, [DelegationCardAction.Undo, DelegationCardAction.Discard], "a merged child's card");
        SameList((Row("a", "p", ChildState.Closed) with { CanUndo = true }).CardActions, [DelegationCardAction.Undo, DelegationCardAction.Discard], "a closed child's card keeps undo");
        SameList(Row("a", "p", ChildState.Closed).CardActions, [DelegationCardAction.Discard], "a closed child's card has only discard");
        SameList(Row("a", "p", ChildState.Failed).CardActions, [DelegationCardAction.Discard], "a failed child's card");
        Check(Row("a", "p", ChildState.Discarded).CardActions.Count == 0, "a discarded child has no actions");
        // The merge a card can undo: the child's last one while merged, or closed at that merge's head.
        var first = new MergeRecord("a", MergeKind.CardFastForward, "main", Commit, new string('b', 40), new string('b', 40)); var last = first with { PreMergeCommit = new string('b', 40), MergedCommit = new string('c', 40), ChildHead = new string('C', 40) };
        DelegationFile With(ChildState state, string? closedHead = null) => DelegationFile.Of([Listed("a", state) with { ClosedHead = closedHead }], merges: [first, last]);
        Check(With(ChildState.Merged).UndoableMerge("a") == last && With(ChildState.Closed, new string('c', 40)).UndoableMerge("a") == last && With(ChildState.Closed, new string('b', 40)).UndoableMerge("a") is null
            && With(ChildState.Closed).UndoableMerge("a") is null && With(ChildState.Reported).UndoableMerge("a") is null && With(ChildState.Merged).UndoableMerge("z") is null, "only the last merge comes back to undo");
        Check(DelegationSidebar.Rows(With(ChildState.Merged))[0].CanUndo && !DelegationSidebar.Rows(With(ChildState.Reported))[0].CanUndo, "the row says whether its card can undo");
        return Task.CompletedTask;
    }

    internal static async Task PanesLedgerAndSnapshotKeys()
    {
        // A child pane is a Claude pane in the requested mode, never the last-used pane's.
        var lastUsed = new RunSession { Id = "last", WorkspaceId = "w", Model = "claude-opus-5", Settings = new(Effort: "high", PermissionMode: "fullAccess"), Logs = [new("l1", "user", "hi", "2026-10-09T10:00:00Z")] };
        var worktree = Path.Combine(Path.GetTempPath(), "worktrees", "child");
        var child = DelegationPanes.ChildSession(new("child", "parent", "plan", worktree), "w", "Child pane");
        Check(child is { Id: "child", WorkspaceId: "w", Title: "Child pane", Kind: "claude", Provider: "claude", Model: "default", ParentSessionId: "parent" } && child.WorkingFolder == worktree && child.Settings == new RunSettings(PermissionMode: "plan") && lastUsed.Settings.PermissionMode == "fullAccess", "exactly the mode asked for");
        // A delegated child is never the template a new pane copies, and what is copied never carries its links.
        var parent = new RunSession { Id = "parent", WorkspaceId = "w", CreatedAt = "2026-10-01T00:00:00Z" };
        var recent = child with { Model = "claude-opus-5", Settings = new(Effort: "high", PermissionMode: "acceptEdits"), Logs = [new("c1", "user", "hi", "2026-10-09T10:00:00Z")] };
        Check(SessionTemplate.Find(new RunSession { Id = "fresh", WorkspaceId = "w" }, [parent, recent])?.Id == "parent", "the child is the most recently used Claude pane, but its parent is the template");
        var fresh = SessionTemplate.Inherit(new RunSession { Id = "fresh", WorkspaceId = "w" }, [recent, parent]);
        Check(fresh.ParentSessionId is null && fresh.WorkingFolder is null && !JsonSerializer.Serialize(fresh, Wire.Json).Contains("parentSessionId", StringComparison.Ordinal) && !JsonSerializer.Serialize(fresh, Wire.Json).Contains("workingFolder", StringComparison.Ordinal), "a new pane never inherits the links");

        // A child runs in its worktree, and refuses with worktree_missing without it; it never falls back to the parent's checkout.
        var workspace = new Workspace { Id = "w", Name = "W", Path = Path.GetTempPath() };
        Check(DelegationPanes.RunWorkspace(child, workspace) is { Id: "w" } runs && runs.Path == worktree && DelegationPanes.RunWorkspace(parent, workspace) == workspace, "a child's run works in its worktree folder");
        Check(DelegationPanes.RunRefusal(parent) is null && DelegationPanes.RunRefusal(child, _ => true) is null && DelegationPanes.RunRefusal(child, _ => false) == DelegationReasonCode.WorktreeMissing
            && DelegationPanes.RunRefusal(child with { WorkingFolder = null }, _ => true) == DelegationReasonCode.WorktreeMissing && DelegationPanes.RunRefusal(child) == DelegationReasonCode.WorktreeMissing, "a child without its worktree may not run");
        // A pane idle after a normal finish reads as idle while the app would refuse to start a run in it.
        var state = DelegationPanes.PaneState(child, workspace, "run-1", DelegationPaneActivity.Finished);
        Check(state == new DelegationPaneState("child", "plan", worktree, ParentSessionId: "parent", RunId: "run-1", Activity: DelegationPaneActivity.Finished), "the pane as the coordinator reads it");
        Check(DelegationPanes.PaneState(child, workspace, "run-1", DelegationPaneActivity.Finished, true).Activity == DelegationPaneActivity.Idle && DelegationPanes.PaneState(child, workspace, "run-1", DelegationPaneActivity.Running, true).Activity == DelegationPaneActivity.Running
            && DelegationPanes.PaneState(child, workspace, null, DelegationPaneActivity.Idle, true).Activity == DelegationPaneActivity.Idle, "only a finished pane the app would refuse reads as idle");
        Check(DelegationCoordinator.Automatic(DelegationPanes.PaneState(child, workspace, "run-1", DelegationPaneActivity.Finished, true)) is DelegationCoordinator.AutomaticDelivery.Hold && DelegationCoordinator.Automatic(state) == new DelegationCoordinator.AutomaticDelivery.Route(DeliveryRoute.Queue), "delivery holds what comes for a pane that cannot start a run");

        // Run ids and normal finishes since launch are tracked per pane.
        var ledger = new DelegationRunLedger();
        Check(ledger.RunId("a") is null && ledger.Activity("a", false) == DelegationPaneActivity.Idle && ledger.End("a", "completed", false) is null, "a pane with no run since launch is idle");
        var run = ledger.Begin("a");
        Check(run.StartsWith("run-", StringComparison.Ordinal) && ledger.RunId("a") == run && ledger.Activity("a", true) == DelegationPaneActivity.Running, "a run gets an id");
        Check(ledger.End("a", "completed", false) == (run, ChildRunEnd.Finished) && ledger.End("a", "completed", false) is null && ledger.Activity("a", false) == DelegationPaneActivity.Finished && ledger.RunId("a") == run, "a second end of the same run is no end");
        var second = ledger.Begin("a");
        Check(second != run && ledger.End("a", "stopped", false) == (second, ChildRunEnd.Stopped) && ledger.Activity("a", false) == DelegationPaneActivity.Idle, "a stopped run leaves the pane idle");
        Check(ledger.End("b", "error", false) is null && ledger.Begin("b") is { } failing && ledger.End("b", "error", false) == (failing, ChildRunEnd.Errored) && ledger.Begin("b") is { } killed && ledger.End("b", "completed", true) == (killed, ChildRunEnd.Quit)
            && ledger.Activity("b", false) == DelegationPaneActivity.Idle, "errors and a quit are told apart");
        ledger.Begin("a"); ledger.End("a", "completed", false); ledger.Forget("a");
        Check(ledger.RunId("a") is null && ledger.Activity("a", false) == DelegationPaneActivity.Idle, "a closed pane is forgotten");

        // Run events reach their handler one at a time, in the order sent.
        var seen = new List<string>(); var busy = 0; var overlapped = false;
        var pump = new DelegationRunEventPump(async change =>
        {
            if (Interlocked.Increment(ref busy) > 1) overlapped = true;
            await Task.Delay(5);
            lock (seen) seen.Add(change switch { DelegationRunEvent.Started started => "start " + started.RunId, DelegationRunEvent.Ended ended => $"end {ended.RunId} {ended.End}", _ => "?" });
            Interlocked.Decrement(ref busy);
        });
        for (var index = 0; index < 20; index++) { pump.Send(new DelegationRunEvent.Started("c", "r" + index)); pump.Send(new DelegationRunEvent.Ended("c", "r" + index, ChildRunEnd.Finished)); }
        await pump.Finish();
        pump.Send(new DelegationRunEvent.Started("c", "late"));
        SameList(seen, Enumerable.Range(0, 20).SelectMany(index => new[] { "start r" + index, $"end r{index} Finished" }), "events are handed on in order, and none after the finish");
        Check(!overlapped, "one at a time");

        // The pane's two optional snapshot keys: decoded, a damaged value keeps the conversation, and a state file written before them loads unchanged.
        var folder = Path.Combine(Path.GetTempPath(), "w").Replace("\\", "\\\\"); var childFolder = Path.Combine(Path.GetTempPath(), "worktrees", "child");
        var json = $$"""
        {"version":1,"workspaces":[{"id":"w","name":"W","path":"{{folder}}","createdAt":"2026-10-01T00:00:00Z"}],"sessions":[
          {"id":"parent","workspaceId":"w","title":"Claude","createdAt":"2026-10-09T00:00:00Z"},
          {"id":"child","workspaceId":"w","title":"Claude","createdAt":"2026-10-09T00:00:00Z","parentSessionId":"parent","workingFolder":"{{childFolder.Replace("\\", "\\\\")}}"},
          {"id":"damaged","workspaceId":"w","title":"Claude","createdAt":"2026-10-09T00:00:00Z","parentSessionId":7,"workingFolder":["x"],
           "logs":[{"id":"l1","kind":"user","text":"keep me","timestamp":"2026-10-09T00:00:01Z"}]}
        ],"activeWorkspaceId":"w"}
        """;
        var snapshot = StateStore.Normalize(JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)!, true);
        SameList(snapshot.Sessions.Select(session => session.Id), ["parent", "child", "damaged"], "every pane loads");
        Check(snapshot.Sessions[1] is { ParentSessionId: "parent" } loaded && loaded.WorkingFolder == childFolder && snapshot.Sessions[2] is { ParentSessionId: null, WorkingFolder: null } damaged && damaged.Logs.Select(log => log.Text).SequenceEqual(["keep me"]), "both keys load, and a damaged value keeps the conversation");
        var again = StateStore.Normalize(JsonSerializer.Deserialize<AppSnapshot>(JsonSerializer.Serialize(snapshot, Wire.Json), Wire.Json)!, true);
        Check(again.Sessions[1].ParentSessionId == "parent" && again.Sessions[1].WorkingFolder == childFolder && JsonSerializer.Serialize(again.Sessions[0], Wire.Json) is var plain && !plain.Contains("parentSessionId", StringComparison.Ordinal) && !plain.Contains("workingFolder", StringComparison.Ordinal), "saving writes both keys back for a child and neither for any other pane");

        // Normalize keeps well-formed links, even for a closed parent, and drops the rest.
        RunSession Linked(string id, string? parentId, string? folderPath, string kind = "claude", string provider = "claude") => new() { Id = id, WorkspaceId = "w", Kind = kind, Provider = provider, CreatedAt = "2026-10-09T00:00:00Z", ParentSessionId = parentId, WorkingFolder = folderPath };
        var shell = Path.Combine(childFolder, "shell");
        List<RunSession> linked =
        [
            Linked("child", "closed-parent", childFolder), Linked("codex", "closed-parent", childFolder, provider: "codex"), Linked("self", "self", "relative/folder"), Linked("bad", "not an id", childFolder + "\0b"),
            Linked("long", "p", @"C:\" + new string('a', 4096)), Linked("rooted", "p", @"\no\drive"), Linked("shell", "child", shell, "shell"), Linked("browser", "child", shell, "browser"), new() { Id = "plain", WorkspaceId = "w" },
        ];
        foreach (var restoring in new[] { true, false })
        {
            var output = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = linked, ActiveWorkspaceId = "w" }, restoring);
            (string?, string?) Links(string id) => output.Sessions.Single(session => session.Id == id) is var session ? (session.ParentSessionId, session.WorkingFolder) : default;
            SameList(output.Sessions.Select(session => session.Id), linked.Select(session => session.Id), "no pane is dropped");
            Check(Links("child") == ("closed-parent", childFolder) && Links("codex") == ("closed-parent", childFolder) && Links("self") == (null, null) && Links("bad") == (null, null) && Links("long") == ("p", null) && Links("rooted") == ("p", null)
                && Links("shell") == (null, null) && Links("browser") == (null, null) && Links("plain") == (null, null), "well-formed links stay and malformed ones go, restoring " + restoring);
        }
        Check(DelegationPanes.IsWorkingFolder(childFolder) && !DelegationPanes.IsWorkingFolder(null) && !DelegationPanes.IsWorkingFolder("") && !DelegationPanes.IsWorkingFolder("C:relative"), "a working folder is a full path");
    }
}
