namespace MightyClaude.Core;

public sealed record QueuedComposerInput(string Id, string Text, IReadOnlyList<RunAttachment> Attachments);

/// <summary>Ephemeral requests behind a running turn. Errors pause; an explicit stop cancels.</summary>
public sealed class QueuedInputBuffer
{
    public const int MaximumItems = 16;
    private readonly List<QueuedComposerInput> items = [];
    public IReadOnlyList<QueuedComposerInput> Items => items;
    public QueuedComposerInput Add(string text, IEnumerable<RunAttachment> attachments)
    {
        if (items.Count >= MaximumItems) throw new InvalidOperationException(Locale.Get("queue.full"));
        var files = attachments.ToArray();
        if (string.IsNullOrWhiteSpace(text) && files.Length == 0 || text.Length > 100_000 || text.Contains('\0')) throw new ArgumentException(Locale.Get("wire.startRun.emptyInput"));
        if (files.Length > 0) AttachmentSupport.Validate(files);
        var item = new QueuedComposerInput(Wire.Id(), text, files); items.Add(item); return item;
    }
    public bool Remove(string id) => items.RemoveAll(item => item.Id == id) > 0;
    public void Clear() => items.Clear();
    public bool Settle(string status)
    {
        if (status == "stopped") Clear();
        return items.Count > 0 && status is "completed" or "idle";
    }
}
