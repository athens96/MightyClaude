using System.Collections.Concurrent;
using System.Text.Json;
using MightyClaude.Core;

internal static class QueuedComposerVerification
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the queued input to be refused");
    }

    internal static Task QueuePreservesOrderBoundsAndSettlementPolicy()
    {
        var queue = new QueuedInputBuffer();
        var first = queue.Add("첫 번째", []); var second = queue.Add("second", []);
        Reject(() => queue.Add(" ", []));
        Check(queue.Items.Select(item => item.Id).SequenceEqual([first.Id, second.Id]), "invalid drafts do not consume or reorder queued work");
        Check(!queue.Settle("running") && !queue.Settle("error") && queue.Items.Count == 2, "errors keep work paused for explicit retry");
        Check(queue.Settle("completed") && queue.Settle("idle"), "a successful turn may drain the queue");
        Check(queue.Remove(first.Id) && !queue.Remove(first.Id) && queue.Items[0].Id == second.Id, "removal is scoped and preserves FIFO order");
        while (queue.Items.Count < QueuedInputBuffer.MaximumItems) queue.Add("queued", []);
        Reject(() => queue.Add("overflow", []));
        Check(queue.Items.Count == QueuedInputBuffer.MaximumItems, "capacity is bounded without evicting prior instructions");
        Check(!queue.Settle("stopped") && queue.Items.Count == 0, "an explicit stop cancels all queued instructions");
        return Task.CompletedTask;
    }

    internal static Task SteerRequiresLiveInitializedChannelAndSuccessfulWrite()
    {
        var writes = new List<string>(); var failWrite = false;
        var channel = new ClaudePermissionChannel("run", "initial", value => { if (failWrite) throw new IOException("closed stdin"); writes.Add(value); }, _ => { }, (_, _) => { }, _ => { }, _ => { });
        Check(!channel.TrySteer("before initialization") && writes.Count == 0, "uninitialized streams cannot accept a follow-up");
        channel.Receive(JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = channel.InitializationId } }));
        Check(writes.SequenceEqual(["initial"]), "initial prompt is sent only after the handshake");
        Check(channel.TrySteer("다음 지시"), "a live Claude channel accepts Korean text");
        Check(writes[^1] == ProviderInput.PromptFrame("다음 지시"), "steering uses the same supported user-message frame as the initial prompt");
        Check(!channel.TrySteer("") && !channel.TrySteer("bad\0input") && !channel.TrySteer(new string('x', 100_001)), "invalid instructions are refused before a write");
        failWrite = true; Check(!channel.TrySteer("not delivered"), "failed writes fall back to the queue instead of losing input");
        failWrite = false; channel.CancelAll(); Check(!channel.TrySteer("after stop"), "a stopped channel never restarts through steering");
        return Task.CompletedTask;
    }

    internal static async Task PermissionResponsesAndCancellationSettleOnlyOnce()
    {
        var writes = new ConcurrentQueue<string>(); var settled = new ConcurrentQueue<ToolPermissionRequest>();
        var channel = new ClaudePermissionChannel("run", "initial", writes.Enqueue, settled.Enqueue, (_, _) => { }, _ => { }, _ => { });
        channel.Receive("""{"type":"control_request","request_id":"request","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool","input":{"file_path":"README.md"}}}""");
        await Task.WhenAll(Task.Run(() => { try { channel.Respond("request", true); } catch (InvalidOperationException) { } }), Task.Run(channel.CancelAll));
        Check(channel.Waiting.Count == 0 && settled.Count(value => value.State != "pending") == 1 && writes.Count <= 1, "concurrent parser stop and UI consent settle a request once");
    }
}
