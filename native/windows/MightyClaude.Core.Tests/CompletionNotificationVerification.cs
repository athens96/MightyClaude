using System.Text.Json;
using MightyClaude.Core;

internal static class CompletionNotificationVerification
{
    private static (AppSnapshot snapshot, string sessionId) MakeSnapshot(string workspaceName, string sessionTitle)
    {
        var workspace = new Workspace { Name = workspaceName };
        var session = new RunSession { WorkspaceId = workspace.Id, Title = sessionTitle };
        return (new AppSnapshot { Workspaces = [workspace], Sessions = [session] }, session.Id);
    }

    internal static Task FiresOnceAfterRunning()
    {
        var d = new CompletionNotificationDecision();
        var (snap, id) = MakeSnapshot("Workspace", "My Run");
        if (d.Process(RunEvent.State(id, "running"), snap, true) is not null)
            throw new InvalidOperationException("running must not fire");
        var title = d.Process(RunEvent.State(id, "completed"), snap, true);
        if (title is null) throw new InvalidOperationException("completed after running must fire");
        return Task.CompletedTask;
    }

    internal static Task TitleIncludesWorkspaceName()
    {
        var d = new CompletionNotificationDecision();
        var (snap, id) = MakeSnapshot("My Project", "My Session");
        d.Process(RunEvent.State(id, "running"), snap, true);
        var title = d.Process(RunEvent.State(id, "completed"), snap, true);
        if (title != "My Project · My Session")
            throw new InvalidOperationException("unexpected title: " + title);
        return Task.CompletedTask;
    }

    internal static Task TitleIsSessionAloneWithoutWorkspace()
    {
        var d = new CompletionNotificationDecision();
        var session = new RunSession { Title = "Solo Run" };
        var snap = new AppSnapshot { Sessions = [session] };
        d.Process(RunEvent.State(session.Id, "running"), snap, true);
        var title = d.Process(RunEvent.State(session.Id, "completed"), snap, true);
        if (title != "Solo Run")
            throw new InvalidOperationException("title should be session title alone: " + title);
        return Task.CompletedTask;
    }

    internal static Task DoesNotFireForErrorOrStopped()
    {
        foreach (var status in new[] { "error", "stopped" })
        {
            var d = new CompletionNotificationDecision();
            var (snap, id) = MakeSnapshot("W", "S");
            d.Process(RunEvent.State(id, "running"), snap, true);
            if (d.Process(RunEvent.State(id, status), snap, true) is not null)
                throw new InvalidOperationException(status + " must not fire");
        }
        return Task.CompletedTask;
    }

    internal static Task DoesNotFireForCompletedWithoutRunning()
    {
        var d = new CompletionNotificationDecision();
        var (snap, id) = MakeSnapshot("W", "S");
        if (d.Process(RunEvent.State(id, "completed"), snap, true) is not null)
            throw new InvalidOperationException("completed without running must not fire");
        return Task.CompletedTask;
    }

    internal static Task DoesNotFireTwiceForSameRun()
    {
        var d = new CompletionNotificationDecision();
        var (snap, id) = MakeSnapshot("W", "S");
        d.Process(RunEvent.State(id, "running"), snap, true);
        d.Process(RunEvent.State(id, "completed"), snap, true);
        if (d.Process(RunEvent.State(id, "completed"), snap, true) is not null)
            throw new InvalidOperationException("second completed must not fire");
        return Task.CompletedTask;
    }

    internal static Task DoesNotFireWhenPreferenceOff()
    {
        var d = new CompletionNotificationDecision();
        var (snap, id) = MakeSnapshot("W", "S");
        d.Process(RunEvent.State(id, "running"), snap, false);
        if (d.Process(RunEvent.State(id, "completed"), snap, false) is not null)
            throw new InvalidOperationException("preference off must not fire");
        return Task.CompletedTask;
    }

    // The GUI smoke run makes exactly one real notifier call with the fixture
    // title and records "sent"; the recorded value carries no prompt, tool
    // argument, output or path.
    internal static async Task SmokeSendsOneFixtureCallAndRecordsSent()
    {
        var sent = new List<(string Title, string SessionId)>();
        var registrations = 0;
        var outcome = await CompletionNotificationSmoke.RunAsync(
            true,
            () => { registrations++; return Task.FromResult<string?>(null); },
            (title, sessionId) => { sent.Add((title, sessionId)); return Task.CompletedTask; });
        if (registrations != 1 || sent.Count != 1)
            throw new InvalidOperationException($"expected one registration and one call, got {registrations}/{sent.Count}");
        if (sent[0].Title != CompletionNotificationSmokeOutcome.FixtureTitle || sent[0].SessionId != CompletionNotificationSmokeOutcome.FixtureSessionId)
            throw new InvalidOperationException("the smoke call must carry the fixture title and session id only");
        if (outcome.Status != CompletionNotificationSmokeOutcome.SentStatus || outcome.Reason is not null)
            throw new InvalidOperationException("a successful call must be recorded as sent without a reason");
        // The smoke result file is written with plain options; the recorded shape
        // must be the same there as on the wire.
        foreach (var options in new[] { Wire.Json, new JsonSerializerOptions() })
        {
            var json = JsonSerializer.Serialize(outcome, options);
            if (json != """{"status":"sent"}""")
                throw new InvalidOperationException("unexpected recorded value: " + json);
        }
    }

    // Unsupported or unregistered notifications are recorded as skipped with a
    // reason and never send anything - skipped is not a failure.
    internal static async Task SmokeRecordsSkippedWithAReason()
    {
        var sent = 0;
        Task Send(string _, string __) { sent++; return Task.CompletedTask; }
        var unsupported = await CompletionNotificationSmoke.RunAsync(
            false,
            () => throw new InvalidOperationException("registration must not be attempted when support is missing"),
            Send);
        if (unsupported.Status != CompletionNotificationSmokeOutcome.SkippedStatus || unsupported.Reason != CompletionNotificationSmokeOutcome.UnsupportedReason)
            throw new InvalidOperationException("unsupported must be skipped with the IsSupported reason");
        var unregistered = await CompletionNotificationSmoke.RunAsync(true, () => Task.FromResult<string?>("registration unavailable"), Send);
        if (unregistered.Status != CompletionNotificationSmokeOutcome.SkippedStatus || unregistered.Reason != "registration unavailable")
            throw new InvalidOperationException("an unavailable registration must be skipped with its reason");
        var blank = await CompletionNotificationSmoke.RunAsync(true, () => Task.FromResult<string?>("  "), Send);
        if (blank.Reason != CompletionNotificationSmokeOutcome.UnknownReason)
            throw new InvalidOperationException("a skipped outcome must always carry a reason");
        if (sent != 0) throw new InvalidOperationException("a skipped smoke run must not send a notification");
        foreach (var options in new[] { Wire.Json, new JsonSerializerOptions() })
        {
            var json = JsonSerializer.Serialize(unsupported, options);
            if (json != """{"status":"skipped","reason":"IsSupported false"}""")
                throw new InvalidOperationException("unexpected recorded value: " + json);
        }
    }

    // An exception after support was confirmed is a real failure, not a skip.
    internal static async Task SmokeFailureAfterSupportIsAFailure()
    {
        try
        {
            await CompletionNotificationSmoke.RunAsync(
                true,
                () => Task.FromResult<string?>(null),
                (_, _) => throw new TimeoutException("toast call timed out"));
        }
        catch (TimeoutException)
        {
            return;
        }
        throw new InvalidOperationException("a failure after IsSupported was true must not be recorded as skipped");
    }

    // The smoke run leaves the saved state as it found it: the preference is a
    // new field with the default on and the saved-state version stays 1.
    internal static async Task SmokeKeepsTheSavedStateVersion()
    {
        if (new AppSnapshot() is not { Version: 1, CompletionNotificationsEnabled: true })
            throw new InvalidOperationException("the preference must default to on with version 1");
        var directory = Path.Combine(Path.GetTempPath(), "mighty-notification-" + Wire.Id());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new StateStore(directory);
            await store.LoadAsync();
            var workspace = store.ApproveLocal(directory);
            var before = new AppSnapshot { Workspaces = [workspace], CompletionNotificationsEnabled = false };
            await store.SaveAsync(before);
            var sent = 0;
            await CompletionNotificationSmoke.RunAsync(false, () => Task.FromResult<string?>(null), (_, _) => { sent++; return Task.CompletedTask; });
            var restored = await new StateStore(directory).LoadAsync();
            if (restored.Version != 1)
                throw new InvalidOperationException("the saved-state version changed: " + restored.Version);
            if (restored.CompletionNotificationsEnabled || restored.Workspaces.Count != 1 || sent != 0)
                throw new InvalidOperationException("the smoke run changed the saved state");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json")));
            if (json.RootElement.GetProperty("version").GetInt32() != 1)
                throw new InvalidOperationException("the saved file version is no longer 1");
        }
        finally { Directory.Delete(directory, true); }
    }
}
