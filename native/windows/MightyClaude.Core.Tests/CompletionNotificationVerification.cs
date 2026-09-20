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
}
