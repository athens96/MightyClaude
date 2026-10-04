namespace MightyClaude.Core;

public sealed record AgentWebOpenAnswer(string Destination, bool Remember);
public sealed record AgentWebOpenPrompt(string Id, string PaneId, string WorkspaceId, Uri Url);

/// <summary>Independent, ephemeral requests. A timeout chooses in-app without saving a preference.</summary>
public sealed class AgentWebOpenPrompts : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Dictionary<string, Waiting> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public event Action? Changed;
    private sealed record Waiting(AgentWebOpenPrompt Prompt, CancellationToken Cancellation)
    {
        internal TaskCompletionSource<AgentWebOpenAnswer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public IReadOnlyList<AgentWebOpenPrompt> ForPane(string paneId)
    {
        lock (gate) return pending.Values.Where(p => p.Prompt.PaneId == paneId && !p.Cancellation.IsCancellationRequested).Select(p => p.Prompt).ToArray();
    }

    public async Task<AgentWebOpenAnswer> RequestAsync(AgentIOBinding binding, Uri url, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        // Reuse the transport validator so direct callers cannot expose unsafe links.
        AgentIOTools.Validate("open_url", System.Text.Json.JsonSerializer.SerializeToElement(new { url = url.OriginalString }));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, binding.Revoked, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        var request = new Waiting(new(Guid.NewGuid().ToString("N"), binding.PaneId, binding.WorkspaceId, url), linked.Token);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked.Token.ThrowIfCancellationRequested();
            if (pending.Count >= 16) throw new InvalidOperationException("Too many pending URL requests.");
            pending.Add(request.Prompt.Id, request);
        }
        Changed?.Invoke();
        // Cancellation closes the card synchronously in the model, before any
        // queued UI click can resolve it or persist its remember checkbox.
        using var cancelled = linked.Token.Register(() => Cancel(request.Prompt.Id, linked.Token));
        try
        {
            try { return await request.Completion.Task.WaitAsync(timeout ?? DefaultTimeout, linked.Token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Choose(request.Prompt.Id, "inApp", false);
                linked.Token.ThrowIfCancellationRequested();
                return await request.Completion.Task.ConfigureAwait(false);
            }
        }
        finally { Remove(request.Prompt.Id); }
    }

    public bool Choose(string requestId, string destination, bool remember)
    {
        if (destination is not ("inApp" or "external")) return false;
        lock (gate)
        {
            if (!pending.TryGetValue(requestId, out var item) || item.Cancellation.IsCancellationRequested) return false;
            pending.Remove(requestId);
            item.Completion.TrySetResult(new(destination, remember));
        }
        Changed?.Invoke(); return true;
    }
    private void Cancel(string id, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (!pending.Remove(id, out var item)) return;
            item.Completion.TrySetCanceled(cancellation);
        }
        Changed?.Invoke();
    }
    private void Remove(string id)
    {
        bool removed; lock (gate) removed = pending.Remove(id);
        if (removed) Changed?.Invoke();
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        lifetime.Cancel();
        // Requests may still unwind; retaining this tiny CTS avoids racing a
        // concurrent RequestAsync reading its token during shutdown.
    }
}
