namespace MightyClaude.Core;

public sealed partial class RemoteClientService
{
    // The runs half (Task 10) fills these in: a connection's runs end when it is replaced, disconnected or lost.
    private Task DropRunsAsync(string connectionId, string? message) => Task.CompletedTask;

    public ValueTask DisposeAsync() { lock (gate) closing = true; return ValueTask.CompletedTask; }
}
