using System.Text.Json;
using MightyClaude.Core;

internal static class AgentIORevocationVerification
{
    internal static async Task RevocationCancelsAuthenticatedQueuedRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0;
        await using var host = new AgentIOPipe(async (binding, _, _, cancellation) =>
        {
            entered.TrySetResult(); await release.Task;
            cancellation.ThrowIfCancellationRequested(); binding.Revoked.ThrowIfCancellationRequested();
            Interlocked.Increment(ref effects); return new { done = true };
        });
        var workspace = new Workspace { Id = "workspace", Path = Path.GetTempPath() };
        var previous = host.Bindings.Bind("pane", workspace, "codex", host.Name, Path.Combine(Path.GetTempPath(), "app.exe"));
        var call = AgentIOPipe.Call(host.Name, previous.Environment[AgentIOBinding.TokenKey], "open_url", JsonSerializer.SerializeToElement(new { url = "https://example.com" }), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = host.Bindings.Bind("pane", workspace, "codex", host.Name, Path.Combine(Path.GetTempPath(), "app.exe"));
        release.TrySetResult();
        var response = await call;
        if (response.GetProperty("ok").GetBoolean() || effects != 0 || host.Bindings.IsCurrent(previous) || !host.Bindings.IsCurrent(next))
            throw new InvalidOperationException("An authenticated request survived its binding's revocation while queued.");
    }
}
