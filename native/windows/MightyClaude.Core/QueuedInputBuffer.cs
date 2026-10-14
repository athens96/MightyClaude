namespace MightyClaude.Core;

/// <param name="PermissionModeOverride">Decided when queued: "plan" when a plan-mode style asked for it (§1.17.4).</param>
/// <param name="Prepared">The text is already a style's prompt (a style button pressed on the phone): it is sent as it is, never rewritten again.</param>
public sealed record QueuedComposerInput(string Id, string Text, IReadOnlyList<RunAttachment> Attachments, string? PermissionModeOverride = null, bool Prepared = false);

/// <summary>Ephemeral requests behind a running turn. Errors pause; an explicit stop cancels.</summary>
public sealed class QueuedInputBuffer
{
    public const int MaximumItems = 16;
    private readonly List<QueuedComposerInput> items = [];
    public IReadOnlyList<QueuedComposerInput> Items => items;
    public QueuedComposerInput Add(string text, IEnumerable<RunAttachment> attachments, string? permissionModeOverride = null, bool prepared = false)
    {
        if (items.Count >= MaximumItems) throw new InvalidOperationException(Locale.Get("queue.full"));
        var files = attachments.ToArray();
        if (string.IsNullOrWhiteSpace(text) && files.Length == 0 || text.Length > 100_000 || text.Contains('\0')) throw new ArgumentException(Locale.Get("wire.startRun.emptyInput"));
        if (files.Length > 0) AttachmentSupport.Validate(files);
        var item = new QueuedComposerInput(Wire.Id(), text, files, permissionModeOverride, prepared); items.Add(item); return item;
    }
    public bool Remove(string id) => items.RemoveAll(item => item.Id == id) > 0;
    public void Clear() => items.Clear();
    /// <param name="keepOnStop">The stopped run's turn was already over (it only waited on background work): the queue survives and runs next.</param>
    public bool Settle(string status, bool keepOnStop = false)
    {
        if (status == "stopped" && !keepOnStop) Clear();
        return items.Count > 0 && (status is "completed" or "idle" || status == "stopped" && keepOnStop);
    }
    private static Dictionary<string, string> Count(int count) => new() { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    /// <summary>
    /// The transcript line a run's end leaves about what waited behind it (M/AppStore.swift:922-929): a stop cancelled it, an
    /// error holds it. Null where nothing waited, or the run ended some other way.
    /// </summary>
    public static string? SettleNotice(string status, int count, bool keepOnStop = false) => count <= 0 ? null
        : status == "stopped" && !keepOnStop ? Locale.Get("queue.log.cancelledOnStop", Count(count))
        : status == "error" ? Locale.Get("queue.log.heldAfterError", Count(count)) : null;
    /// <summary>The pane cannot run at all (<see cref="RunBlock"/>): what waited is cancelled, and the line says why (M/AppStore.swift:904-908).</summary>
    public static string UnrunnableNotice(int count, string reason) { var values = Count(count); values["reason"] = reason; return Locale.Get("queue.log.cancelledUnrunnable", values); }
    /// <summary>A start refused before its run began keeps its request in the queue, so nothing typed is lost (M/AppStore.swift:917-921).</summary>
    public static string KeptNotice(string error) => Locale.Get("queue.log.keptAfterFailure", new Dictionary<string, string> { ["error"] = error });
}

/// <summary>
/// Why a pane cannot run for good (M/AppStore.swift:488-506): its CLI is missing or too old, or its permission mode is one this
/// CLI does not take. Null when it may run, and while the CLI has not been read yet. A request waiting in such a pane's queue is
/// cancelled once a fresh reading says the same; a passing block (an update or a plugin change, by hand or in the background)
/// only holds it, and is not a reason here.
/// </summary>
public static class RunBlock
{
    public static string? Reason(RunSession pane, ProviderRuntime? runtime)
    {
        if (pane.Kind == "shell") return null;
        if (runtime is null) return null;
        if (!runtime.Available) return runtime.Detail.Length > 0 ? runtime.Detail : Locale.Get("run.blocked.installCLI", new Dictionary<string, string> { ["name"] = runtime.Name });
        var modes = runtime.Capabilities.PermissionModes ?? [];
        if (pane.Settings.PermissionMode == "auto" && !modes.Contains("auto")) return Locale.Get("composer.hint.autoModeUnverified");
        if (pane.Settings.PermissionMode == "onRequest" && !modes.Contains("onRequest")) return Locale.Get("run.blocked.onRequestNeedsUpdate");
        return null;
    }
}
