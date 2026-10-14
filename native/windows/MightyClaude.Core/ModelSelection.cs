using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record ModelSelectionResolution(string Model, string Effort);

/// <summary>Keeps a pane's model and effort right against a newly read model list (M/MightyCore/ModelSelectionSupport.swift).</summary>
public static class ModelSelectionSupport
{
    private static readonly Regex OfficialClaude = new(@"^(?:(?:opus|sonnet|fable)\[1m\]|claude-(?:(?:opus|sonnet|haiku|fable)-[0-9]+(?:[-.][0-9]+)*|[0-9]+(?:-[0-9]+)*-(?:opus|sonnet|haiku|fable)(?:-[0-9]+)*)(?:\[1m\])?)\z", RegexOptions.CultureInvariant);
    /// <summary>A model name Claude Code itself knows: a row of the fallback list, an alias with <c>[1m]</c>, or a <c>claude-…</c> id (M/MightyCore/Models.swift:694-696).</summary>
    public static bool IsOfficialClaudeModel(string value) => ProviderCatalog.Fallback("claude").Models.Any(m => m.Value == value) || OfficialClaude.IsMatch(value);

    /// <summary>
    /// The model and effort a pane keeps under a newly read list. Only a list the CLI itself gave can retire a model it offered
    /// before; a custom id survives a missing or inconclusive reading.
    /// </summary>
    public static ModelSelectionResolution Reconcile(string model, string effort, ModelCatalog? previous, ModelCatalog refreshed, string? provider = null)
    {
        var original = new ModelSelectionResolution(model, effort);
        if (refreshed.Source != "cli") return original;
        var prior = previous?.Models.FirstOrDefault(m => m.Value == model || m.ResolvedModel == model);
        var direct = refreshed.Models.FirstOrDefault(m => m.Value == model) ?? refreshed.Models.FirstOrDefault(m => m.Value != "default" && m.ResolvedModel == model);
        // Bedrock's list can hold concrete regional ids while the CLI still takes the stable aliases: a missing row must not
        // change their model family, or freeze an alias to what it last stood for.
        var stableAlias = provider == "claude" && !model.StartsWith("claude-", StringComparison.Ordinal) && IsOfficialClaudeModel(model);
        if (stableAlias && direct is null) return original;
        var matched = direct ?? (prior?.ResolvedModel is { } resolved ? refreshed.Models.FirstOrDefault(m => m.Value != "default" && (m.Value == resolved || m.ResolvedModel == resolved)) : null);
        if (matched is null)
        {
            var retiredOfficial = provider == "claude" && model.StartsWith("claude-", StringComparison.Ordinal) && IsOfficialClaudeModel(model);
            return prior is null && !retiredOfficial ? original : new("default", "default");
        }
        var unsupported = matched.SupportsEffort == false || matched.SupportedEffortLevels is { } levels && !levels.Contains(effort);
        return new(matched.Value, effort != "default" && unsupported ? "default" : effort);
    }

    /// <summary>
    /// The pane with its model and effort put right against a newly read list, and the transcript line that says what changed
    /// (M/AppStore+ModelCatalogs.swift:47-63); null when the list leaves the choice as it is. A pane that continues a conversation
    /// and falls back to the default names the model the default stands for: with none named, the CLI would restore the
    /// conversation's retired model.
    /// </summary>
    public static RunSession? Adjusted(RunSession pane, ModelCatalog? previous, ModelCatalog refreshed)
    {
        var resolution = Reconcile(pane.Model, pane.Settings.Effort, previous, refreshed, pane.Provider);
        var model = resolution.Model;
        if (pane.Model != "default" && model == "default" && pane.ResumeId is not null
            && refreshed.Models.FirstOrDefault(m => m.Value == "default")?.ResolvedModel is { Length: > 0 } stands && stands != "default") model = stands;
        if (model == pane.Model && resolution.Effort == pane.Settings.Effort) return null;
        var line = Locale.Get("composer.model.catalogAdjusted", new Dictionary<string, string> { ["fromModel"] = pane.Model, ["toModel"] = model, ["fromEffort"] = pane.Settings.Effort, ["toEffort"] = resolution.Effort });
        return pane with { Model = model, Settings = pane.Settings with { Effort = resolution.Effort }, Logs = TranscriptRetention.Trimmed([.. pane.Logs, new(Wire.Id(), "system", line, Wire.Now(), pane.Provider)]) };
    }
}

/// <summary>One provider's model list in one workspace folder.</summary>
public sealed record ModelContext(string WorkspaceId, string Path, string Provider);

/// <summary>
/// Reads each context's model list one probe at a time (M/LocalModelCatalogRefresh.swift). Requests made while a probe runs share
/// it; an invalidating request during a probe makes it read again before it answers, so it never answers with the list from
/// before; and a list the CLI gave is used again for 2 s by a forced request (a pane selected) and for 60 s by any other (a run
/// about to start). A reading that failed, or gave only the fallback list (offline, signed out), is not asked again by a run
/// about to start for <see cref="FailedLifetime"/>, so each send does not wait for the CLI to fail anew; a selection asks again.
/// <c>changed</c> hears every new list with the one before it.
/// </summary>
public sealed class ModelCatalogRefresh(Func<ModelContext, Task<ProviderRuntime>> loader, Action<ModelContext, ProviderRuntime?, ProviderRuntime> changed, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan ForcedLifetime = TimeSpan.FromSeconds(2), Lifetime = TimeSpan.FromSeconds(60), FailedLifetime = TimeSpan.FromSeconds(15);
    /// <summary>The longest a run waits for its list before it starts with the one in hand (a read's own limit is 7 s).</summary>
    public static readonly TimeSpan PreRunWait = TimeSpan.FromSeconds(3);
    /// <summary>One gesture can ask twice (a selection and the focus it moves): a probe waits this long so they share it.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);
    private sealed class Entry { internal int Revision; internal int? LoadedRevision; internal ProviderRuntime? Value; internal DateTimeOffset LoadedAt = DateTimeOffset.MinValue, FailedAt = DateTimeOffset.MinValue; internal Task<ProviderRuntime?>? Probe; }
    private readonly Dictionary<ModelContext, Entry> entries = [];
    private readonly object sync = new();
    private bool stopped;
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public ProviderRuntime? Value(ModelContext key) { lock (sync) return entries.GetValueOrDefault(key)?.Value; }
    /// <summary>The list in hand is the newest asked for: no probe runs and nothing invalidated it since it was read.</summary>
    public bool IsCurrent(ModelContext key) { lock (sync) return entries.GetValueOrDefault(key) is { Probe: null } entry && entry.LoadedRevision == entry.Revision; }
    /// <summary>
    /// The context's list: the one in hand while it is fresh, else the running probe's, else a new probe's. With
    /// <paramref name="invalidate"/> the list in hand is known to be another agent's or account's: it is not used, and a
    /// running probe reads again. Null once stopped, or when the reading failed.
    /// </summary>
    public Task<ProviderRuntime?> Request(ModelContext key, bool force = false, bool invalidate = false)
    {
        lock (sync)
        {
            if (stopped) return Task.FromResult<ProviderRuntime?>(null);
            if (!entries.TryGetValue(key, out var entry)) entries[key] = entry = new();
            if (invalidate) { entry.Revision++; entry.LoadedAt = DateTimeOffset.MinValue; }
            if (entry.Probe is { } running) return running;
            if (entry.Value is { } value && Now - entry.LoadedAt < (value.ModelCatalog.Source == "cli" ? force ? ForcedLifetime : Lifetime : force ? TimeSpan.Zero : FailedLifetime)) return Task.FromResult<ProviderRuntime?>(value);
            if (!force && !invalidate && Now - entry.FailedAt < FailedLifetime) return Task.FromResult<ProviderRuntime?>(null);
            return entry.Probe = ProbeAsync(key, entry);
        }
    }
    /// <summary>
    /// Waits for the list a run should start with (M/AppStore+ModelCatalogs.swift:66-76), but no longer than <paramref name="limit"/>
    /// (<see cref="PreRunWait"/>): true when the newest list is in hand, false when it failed, was stopped or took too long, and the run
    /// goes on with what is in hand.
    /// </summary>
    public async Task<bool> WaitCurrent(ModelContext key, TimeSpan? limit = null)
    {
        var deadline = Task.Delay(limit ?? PreRunWait);
        while (true)
        {
            var request = Request(key);
            if (await Task.WhenAny(request, deadline) == deadline || await request is null) return false;
            if (IsCurrent(key)) return true;
        }
    }
    /// <summary>Whether two readings say the same: compared by what they hold, since every reading is a new object.</summary>
    public static bool Same(ProviderRuntime? a, ProviderRuntime? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null && JsonSerializer.Serialize(a, Wire.Json) == JsonSerializer.Serialize(b, Wire.Json);
    /// <summary>A list read another way (the whole runtime at once) is the context's newest: it is kept and reported like a probe's.</summary>
    public void Store(ModelContext key, ProviderRuntime value)
    {
        ProviderRuntime? previous;
        lock (sync)
        {
            if (stopped) return;
            if (!entries.TryGetValue(key, out var entry)) entries[key] = entry = new();
            previous = entry.Value; entry.Value = value; entry.LoadedAt = Now;
            if (entry.Probe is null) entry.LoadedRevision = entry.Revision;
        }
        changed(key, previous, value);
    }
    public void Shutdown() { lock (sync) { stopped = true; entries.Clear(); } }

    private async Task<ProviderRuntime?> ProbeAsync(ModelContext key, Entry entry)
    {
        // Never finished by the time Request has kept it: the wait below always yields.
        await Task.Delay(Settle);
        // This probe, as Request kept it: a probe that `changed` starts must not be forgotten by this one's finally.
        Task<ProviderRuntime?>? self; lock (sync) self = entry.Probe;
        try
        {
            while (true)
            {
                int revision;
                lock (sync) { if (stopped) return null; revision = entry.Revision; }
                var value = await loader(key);
                ProviderRuntime? previous;
                lock (sync)
                {
                    if (stopped) return null;
                    if (revision != entry.Revision) continue;
                    previous = entry.Value; entry.Value = value; entry.LoadedAt = Now; entry.LoadedRevision = revision; entry.Probe = null;
                }
                changed(key, previous, value);
                return value;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { lock (sync) entry.FailedAt = Now; return null; }
        finally { lock (sync) if (ReferenceEquals(entry.Probe, self)) entry.Probe = null; }
    }
}
