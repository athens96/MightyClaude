namespace MightyClaude.Core;

/// <summary>The host's runs (Task 14 fills this in): until then no run route exists and nothing a runner sends is kept.</summary>
public sealed partial class RemoteHostService
{
    private sealed class Job { public bool Done { get; set; } }
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private Task<RemoteHttpResponse> ServeRunsAsync(RemoteHttpRequest request, string[] route, string query, Guid revision) => throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
    private void Receive(RunEvent value, Guid revision) { }
    private void ReceivePermission(ToolPermissionRequest value, Guid revision) { }
    private void ReceiveGraph(string sessionId, ExecutionGraphNode node, Guid revision) { }
    private Task LeaseLoopAsync(Guid revision, CancellationToken token) => Task.CompletedTask;
}
