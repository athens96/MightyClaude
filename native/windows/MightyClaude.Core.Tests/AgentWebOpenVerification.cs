using MightyClaude.Core;

internal static class AgentWebOpenVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static AgentIOBinding Bind(AgentIOBindings registry, string pane) => registry.Bind(pane,
        new Workspace { Id = "workspace", Path = Path.GetTempPath() }, "codex", "fixture", Path.Combine(Path.GetTempPath(), "app.exe"));
    internal static async Task IndependentChoicesAndFallback()
    {
        using var prompts = new AgentWebOpenPrompts();
        var registry = new AgentIOBindings(); var first = Bind(registry, "first"); var second = Bind(registry, "second");
        var one = prompts.RequestAsync(first, new Uri("http://localhost:3000/first"), CancellationToken.None);
        var two = prompts.RequestAsync(second, new Uri("https://example.com/second"), CancellationToken.None);
        var extra = prompts.RequestAsync(first, new Uri("https://example.com/third"), CancellationToken.None);
        Check(prompts.ForPane("first").Count == 2 && prompts.ForPane("second").Count == 1, "multiple requests appear independently in their owning pane");
        var secondId = prompts.ForPane("second").Single().Id;
        Check(prompts.Choose(secondId, "external", true), "second pane can answer before first");
        Check(await two == new AgentWebOpenAnswer("external", true) && !one.IsCompleted && !extra.IsCompleted, "answer only completes its own request");
        Check(!prompts.Choose(secondId, "inApp", false), "a duplicate answer is ignored");
        var firstId = prompts.ForPane("first")[0].Id;
        Check(!prompts.Choose(firstId, "javascript", false), "unknown destination cannot resolve a request");
        prompts.Choose(firstId, "inApp", false); await one;
        prompts.Choose(prompts.ForPane("first").Single().Id, "external", false); await extra;
        Check(AgentWebOpenPrompts.DefaultTimeout == TimeSpan.FromSeconds(30), "production fallback matches Mac's thirty seconds");
        var fallback = prompts.RequestAsync(first, new Uri("https://example.com/timeout"), CancellationToken.None, TimeSpan.FromMilliseconds(30));
        var expiredId = prompts.ForPane("first").Single().Id;
        Check(await fallback == new AgentWebOpenAnswer("inApp", false), "no response falls back in-app without remembering");
        Check(prompts.ForPane("first").Count == 0 && !prompts.Choose(expiredId, "external", true), "timeout removes its card and ignores late remembered choices");
    }
    internal static async Task RevocationCancellationAndShutdown()
    {
        using var prompts = new AgentWebOpenPrompts();
        var registry = new AgentIOBindings(); var first = Bind(registry, "first"); var second = Bind(registry, "second");
        var revoked = prompts.RequestAsync(first, new Uri("https://example.com/first"), CancellationToken.None);
        var stale = prompts.ForPane("first").Single().Id;
        var replacement = Bind(registry, "first");
        await Cancelled(revoked);
        Check(prompts.ForPane("first").Count == 0 && !prompts.Choose(stale, "external", true), "replaced binding immediately removes card and cannot open or remember");
        using var cancellation = new CancellationTokenSource();
        var cancelled = prompts.RequestAsync(replacement, new Uri("https://example.com/cancel"), cancellation.Token);
        var independent = prompts.RequestAsync(second, new Uri("https://example.com/second"), CancellationToken.None);
        cancellation.Cancel(); await Cancelled(cancelled);
        Check(prompts.ForPane("first").Count == 0 && !independent.IsCompleted, "request cancellation leaves other panes active");
        prompts.Dispose(); await Cancelled(independent);
        Check(prompts.ForPane("second").Count == 0, "shutdown cancels and removes every pending card");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Revoked URL request must cancel instead of opening a browser.");
    }
}
