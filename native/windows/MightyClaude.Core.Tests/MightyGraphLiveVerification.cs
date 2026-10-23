using MightyClaude.Core;

// The 실행 block and the timeline show the main agent's work while a request runs, as on the Mac (recordGraph "log").
internal static class MightyGraphLiveVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static LogEntry Log(string id, string kind, string text, AgentActivity? activity = null) => new(id, kind, text, "2026-10-23T00:00:00Z", "gemini", activity);

    internal static Task TheRunningRequestShowsTheMainAgentsWork()
    {
        var pane = new RunSession { Id = "pane", Kind = "claude", Provider = "gemini", Status = "running", Logs = [Log("old", "assistant", "earlier answer"), Log("ask", "user", "why")] };
        // Before the pipeline's first graph: the transcript already has a tool row for this request.
        pane = pane.Apply(new RunEvent("pane", "log", Log("tool-1", "system", "README.md", new AgentActivity("tool-1", "gemini", "read", "running", "README.md"))));
        pane = pane.Apply(new RunEvent("pane", "graph_run", GraphRun: new MightyGraphRun { Id = "run", Input = "why", Status = "running", Provider = "gemini" }));
        var run = pane.GraphRuns![^1];
        Check(run.RootEntries.Select(e => e.Id).SequenceEqual(["tool-1"]), "a request seen first starts with the transcript since it was asked, not before");

        pane = pane.Apply(new RunEvent("pane", "log", Log("tool-1", "system", "README.md", new AgentActivity("tool-1", "gemini", "read", "completed", "README.md"))));
        pane = pane.Apply(new RunEvent("pane", "log", Log("answer", "assistant", "because")));
        run = pane.GraphRuns![^1];
        Check(run.RootEntries.Count == 2 && run.RootEntries[0].Activity!.State == "completed" && run.RootEntries[1].Text == "because", "later rows are added and an updated row replaces its earlier state");

        // The pipeline's next graph has no transcript; what the request showed stays, and the result is not said twice.
        var final = new MightyGraphRun { Id = "run", Input = "why", Status = "completed", Provider = "gemini", FinalOutput = "because" };
        MightyGraphSupport.RefreshResult(final);
        pane = pane.Apply(new RunEvent("pane", "graph_run", GraphRun: final));
        run = pane.GraphRuns![^1];
        Check(run.RootEntries.Select(e => e.Id).SequenceEqual(["tool-1", "answer"]), "a later graph keeps the shown work and drops the duplicate result entry");
        Check(run.ResultEntries.Count == 1 && run.ResultEntries[0].Text == "because", "the result block still has the answer");

        // A finished request takes no more rows.
        pane = pane.Apply(new RunEvent("pane", "log", Log("late", "system", "after")));
        Check(pane.GraphRuns![^1].RootEntries.All(e => e.Id != "late"), "a finished request takes no more rows");
        Check(MightyGraphSupport.MergeRootEntries([], Enumerable.Range(0, 150).Select(i => Log("e" + i, "system", "x"))).Count == MightyGraphSupport.MaximumRootEntries, "the newest 100 entries are kept");
        return Task.CompletedTask;
    }
}
