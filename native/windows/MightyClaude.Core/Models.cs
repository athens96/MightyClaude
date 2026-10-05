using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };
    public static string Id() => Guid.NewGuid().ToString();
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public static bool Identifier(string? value) => value is not null && Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}\z");
    public static bool Model(string? value) => value is { Length: > 0 and <= 200 } && Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9._:/@\[\]-]*\z");
    public static string Clean(string? value, int limit = 32768) { var clean = Regex.Replace(value ?? "", @"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", ""); return clean[..Math.Min(clean.Length, limit)]; }
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    public static readonly string[] Providers = ["claude", "codex", "gemini"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
}

public sealed record Workspace
{
    public string Id { get; init; } = Wire.Id();
    public string Name { get; init; } = "Workspace";
    public string Path { get; init; } = "";
    public string CreatedAt { get; init; } = Wire.Now();
    // Workspace-level model defaults override; null means no workspace-level override.
    public ModelDefaultsConfig? ModelDefaults { get; init; }
}
[JsonConverter(typeof(RunSettingsJsonConverter))]
public sealed record RunSettings(string Effort = "default", string PermissionMode = "manual", int? MaxTurns = null, double? MaxBudgetUsd = null, bool FastMode = false, string WebSearch = "default", bool NetworkAccess = false);

// Legacy v1 hosts reject unknown settings. Keep their four-field payload when
// extensions are at their defaults, while retaining explicit nullable limits.
public sealed class RunSettingsJsonConverter : JsonConverter<RunSettings>
{
    public override RunSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException(Locale.Get("wire.runSettings.notObject"));
        try
        {
            return new(
                value.TryGetProperty("effort", out var effort) ? effort.GetString() ?? throw new JsonException(Locale.Get("wire.runSettings.invalidEffort")) : "default",
                value.TryGetProperty("permissionMode", out var mode) ? mode.GetString() ?? throw new JsonException(Locale.Get("wire.runSettings.invalidPermission")) : "manual",
                value.TryGetProperty("maxTurns", out var turns) && turns.ValueKind != JsonValueKind.Null ? turns.GetInt32() : null,
                value.TryGetProperty("maxBudgetUsd", out var budget) && budget.ValueKind != JsonValueKind.Null ? budget.GetDouble() : null,
                value.TryGetProperty("fastMode", out var fast) && fast.GetBoolean(),
                value.TryGetProperty("webSearch", out var web) ? web.GetString() ?? throw new JsonException(Locale.Get("wire.runSettings.invalidWebSearch")) : "default",
                value.TryGetProperty("networkAccess", out var network) && network.GetBoolean());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { throw new JsonException(Locale.Get("wire.runSettings.invalidValueType"), ex); }
    }
    public override void Write(Utf8JsonWriter writer, RunSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("effort", value.Effort); writer.WriteString("permissionMode", value.PermissionMode);
        if (value.MaxTurns is int turns) writer.WriteNumber("maxTurns", turns); else writer.WriteNull("maxTurns");
        if (value.MaxBudgetUsd is double budget) writer.WriteNumber("maxBudgetUsd", budget); else writer.WriteNull("maxBudgetUsd");
        if (value.FastMode) writer.WriteBoolean("fastMode", true);
        if (value.WebSearch != "default") writer.WriteString("webSearch", value.WebSearch);
        if (value.NetworkAccess) writer.WriteBoolean("networkAccess", true);
        writer.WriteEndObject();
    }
}
/// <param name="Images">Pictures an <c>image</c> entry shows, by reference (macOS LogEntry.images); <c>Text</c> stands in for them where pictures are not drawn.</param>
public sealed record LogEntry(string Id, string Kind, string Text, string Timestamp, string? Provider = null, AgentActivity? Activity = null, List<AgentImageRef>? Images = null)
{
    /// <summary>The entry kinds a transcript keeps (macOS LogEntry.stored).</summary>
    public static readonly IReadOnlySet<string> Stored = new HashSet<string>(StringComparer.Ordinal) { "user", "assistant", "system", "output", "error", "image" };
}
public sealed record RunSession
{
    public string Id { get; init; } = Wire.Id();
    public string WorkspaceId { get; init; } = "";
    public string Title { get; init; } = "Claude";
    public string Kind { get; init; } = "claude";
    public string Status { get; init; } = "idle";
    public string Model { get; init; } = "default";
    public string Provider { get; init; } = "claude";
    public RunSettings Settings { get; init; } = new();
    public List<LogEntry> Logs { get; init; } = [];
    public string? ResumeId { get; init; }
    public string CreatedAt { get; init; } = Wire.Now();
    public string Draft { get; init; } = "";
    public AgentRunTiming? RunTiming { get; init; }
    public SessionUsage? SessionUsage { get; init; }
    [JsonIgnore] public AgentActivity? CurrentActivity { get; init; }
    // "default" or "mighty"; anything else — another string, a number, a bool —
    // loads as no choice at all (macOS RunSession.agentViewMode).
    [JsonPropertyName("agentViewMode")]
    [JsonConverter(typeof(AgentViewModeConverter))]
    public string? AgentViewMode { get; init; }
    public string? MightyStyle { get; init; }
    public string? MightyStyleHash { get; init; }
    public string? MightyStyleSince { get; init; }
    [JsonPropertyName("graphRuns")] public List<MightyGraphRun>? GraphRuns { get; init; }
    // The newest result card's remembered maximum, set by dragging it
    // (macOS RunSession.graphResultSize); null means the window fit.
    [JsonPropertyName("graphResultSize")] public GraphBlockSize? GraphResultSize { get; init; }
    [JsonPropertyName("graphBlockSizes")] public Dictionary<string, GraphBlockSize>? GraphBlockSizes { get; init; }
    // The Mighty presentation, independent of the outer default/Mighty choice.
    [JsonPropertyName("graphViewMode"), JsonConverter(typeof(MightyGraphViewModeConverter))]
    public string? GraphViewMode { get; init; }
    // Browser pane fields — serialized with the same keys as macOS RunSession.
    [JsonPropertyName("workspaceProfileKey")] public string? WorkspaceProfileKey { get; init; }
    [JsonPropertyName("ownerSessionId")] public string? OwnerSessionId { get; init; }
    // "auto" (or absent) while an agent pane's title follows its latest request,
    // "fixed" once renamed (macOS RunSession.titleMode, docs/windows-parity.md).
    [JsonPropertyName("titleMode")] public string? TitleMode { get; init; }

    internal RunSession Apply(RunEvent ev)
    {
        var value = this; var timing = RunTiming;
        if (Kind != "shell")
        {
            if (ev.Type == "status" && ev.Status == "running") timing = timing is null || timing.FinishedAt is not null ? AgentRunTiming.Begin() : timing.Observe();
            else if (ev.Type == "status" && ev.Status is "completed" or "error" or "stopped") timing = timing?.Finish();
            else if (Status == "running") timing = timing?.Observe();
        }
        value = value with { RunTiming = timing };
        if (ev.Type == "status")
        {
            var terminal = ev.Status is "completed" or "error" or "stopped";
            return value with { Status = ev.Status!, CurrentActivity = terminal ? null : value.CurrentActivity, Logs = terminal ? value.Logs.Select(l => l.Activity is { State: "running" or "waiting" } a ? l with { Activity = a with { State = ev.Status == "stopped" ? "stopped" : "error" } } : l).ToList() : value.Logs };
        }
        if (ev.Type == "resume") return value with { ResumeId = ev.ResumeId };
        if (ev.Type == "usage" && Kind == "claude" && ev.Usage?.Provider == Provider && SessionUsageSupport.Normalize(ev.Usage) is { } usage) return value with { SessionUsage = usage with { SelectedModel = Wire.Model(Model) ? Model : null } };
        if (ev.Type == "activity" && ev.Activity?.Provider == Provider) return value with { CurrentActivity = ActivitySupport.Normalize(ev.Activity) };
        if (ev.Type == "log" && ev.Entry is { } entry)
        {
            var logs = value.Logs.ToList(); var index = logs.FindIndex(l => l.Id == entry.Id);
            if (index >= 0) logs[index] = entry; else logs.Add(entry);
            return value with { Logs = logs.TakeLast(300).ToList() };
        }
        // The run pipeline hands the finished execution graph of one request to
        // its own session; Gemini and shell panes never produce one.
        if (ev.Type == "graph_run" && ev.GraphRun is { } newRun && Kind == "claude" && MightyGraphSupport.Providers.Contains(Provider))
        {
            var runs = (GraphRuns ?? []).Select(r => r.Copy()).ToList();
            var at = runs.FindIndex(r => r.Id == newRun.Id);
            if (at >= 0) runs[at] = newRun; else runs.Add(newRun);
            var bounded = MightyGraphSupport.BoundedLiveHistory([.. runs.TakeLast(128)]);
            return value with { GraphRuns = bounded.Count > 0 ? bounded : null };
        }
        return value;
    }
}
// Reads "default"/"mighty" only; any other JSON token loads as null, so a saved
// file written by a newer or malformed writer still loads cleanly.
internal sealed class AgentViewModeConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (value is "default" or "mighty") return value;
            return null;
        }
        reader.Skip();
        return null;
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is "default" or "mighty") writer.WriteStringValue(value); else writer.WriteNullValue();
    }
}
// Reads true/false only; any other JSON token (number, string, array, object, null) returns null.
// This lets old saved files load cleanly even if the field was never written.
internal sealed class LenientNullableBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True) return true;
        if (reader.TokenType == JsonTokenType.False) return false;
        reader.Skip();
        return null;
    }
    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is bool b) writer.WriteBooleanValue(b);
        else writer.WriteNullValue();
    }
}
public sealed record RegisteredModelEntry(string Name, bool SupportsEffort = false, string[]? SupportedEffortLevels = null);

public sealed record ProviderModeDefaults
{
    public Dictionary<string, string> ModeDefaults { get; init; } = [];
    public List<RegisteredModelEntry> RegisteredModels { get; init; } = [];
}

public sealed record ModelDefaultsConfig
{
    public ProviderModeDefaults Claude { get; init; } = new();
    public ProviderModeDefaults Codex { get; init; } = new();
}


public sealed record PhaseModelsSnapshot
{
    [JsonPropertyName("claudeMain")] public string ClaudeMain { get; init; } = "default";
    [JsonPropertyName("claudeOpusAlias")] public string ClaudeOpusAlias { get; init; } = "default";
    [JsonPropertyName("claudeSonnetAlias")] public string ClaudeSonnetAlias { get; init; } = "default";
    [JsonPropertyName("claudeHaikuAlias")] public string ClaudeHaikuAlias { get; init; } = "default";
    [JsonPropertyName("claudeSubagentDefault")] public string ClaudeSubagentDefault { get; init; } = "default";
    [JsonPropertyName("codexReviewModel")] public string CodexReviewModel { get; init; } = "default";
    [JsonPropertyName("codexSubagentDefault")] public string CodexSubagentDefault { get; init; } = "default";
    [JsonPropertyName("codexPlanModeReasoningEffort")] public string CodexPlanModeReasoningEffort { get; init; } = "default";
    public string? ClaudeMainEffort { get; init; }
    public string? CodexMainEffort { get; init; }
    public string? CodexSubagentEffort { get; init; }
}

public sealed record AppSnapshot
{
    public int Version { get; init; } = 1;
    public List<Workspace> Workspaces { get; init; } = [];
    public List<RunSession> Sessions { get; init; } = [];
    public string? ActiveWorkspaceId { get; init; }
    public string? ActiveSessionId { get; init; }
    // Null migrates old snapshots by opening the active workspace; [] means all closed.
    [JsonConverter(typeof(WorkspaceDisclosureConverter))]
    public List<string>? ExpandedWorkspaceIds { get; init; }
    public string Layout { get; init; } = "grid";
    public Dictionary<string, PaneLayoutNode>? PaneLayouts { get; init; }
    public Dictionary<string, string>? PaneLayoutModes { get; init; }
    public Dictionary<string, string>? PaneLayoutActiveSessionIds { get; init; }
    public string Theme { get; init; } = "dark";
    public double SidebarWidth { get; init; } = 252;
    // The sidebar folded away (Ctrl+B or the header's sidebar button). Additive with a default
    // (false = open) so Version stays 1; SidebarWidth is kept while folded, so unfolding restores it.
    public bool SidebarCollapsed { get; init; }
    public Dictionary<string, string>? TrustedStatusLines { get; init; }
    public bool StatusLineEnabled { get; init; } = true;
    public bool CompletionNotificationsEnabled { get; init; } = true;
    [JsonConverter(typeof(LenientNullableBoolConverter))]
    public bool? AutoUpdateCLIs { get; init; }
    public bool? AutoUpdatePlugins { get; init; }
    // Additive with a default, so Version stays 1: StateStore resets every
    // field when Version is not 1. Off out of the box — nothing is looked up
    // directly until the user switches it on in the usage popover.
    public bool ClaudeDirectUsageLookupEnabled { get; init; }
    // Additive with a default (true = macOS default). Automatic once-a-day check.
    public bool AppUpdateAutoCheck { get; init; } = true;
    // When set, the last automatic check timestamp (ISO 8601).
    public string? AppUpdateLastCheckedAt { get; init; }
    // User-entered manifest URL override (ignored when the build has a built-in URL).
    public string? AppUpdateManifestUrlOverride { get; init; }
    // "system", "ko", or "en". Applied at next app start via Locale.LanguagePreference.
    public string LanguagePreference { get; init; } = "system";
    public string SettingsPane { get; init; } = "general";
    public Dictionary<string, string>? AgentWebOpenChoices { get; init; }
    public MobileRemoteSettings MobileRemote { get; init; } = new();
    // App-level per-provider per-mode model defaults; null means all modes use "default".
    // Additive with a default (null) so Version stays 1.
    public ModelDefaultsConfig? ModelDefaults { get; init; }
    // Per-phase model knobs (Claude + Codex). omc/Ouroboros knobs live in their own files.
    // Additive with a default (null = all "default") so Version stays 1.
    [JsonPropertyName("phaseModels")]
    public PhaseModelsSnapshot? PhaseModels { get; init; }
    // Opt-in browser pane engine. Read once at launch; a change applies after restart.
    // Additive with a default (false) so Version stays 1.
    [JsonPropertyName("browserEngineEnabled")]
    public bool BrowserEngineEnabled { get; init; }
    public AppSnapshot Apply(RunEvent ev) => !ev.Valid() ? this : this with { Sessions = Sessions.Select(s => s.Id == ev.SessionId ? s.Apply(ev) : s).ToList() };
}
public sealed record StartRunRequest(string SessionId, string WorkspaceId, string Kind, string Input, IReadOnlyList<RegisteredModelEntry> RegisteredModels, string Model = "default", string Provider = "claude", RunSettings? Settings = null, string? ResumeId = null, IReadOnlyList<RunAttachment>? Attachments = null)
{
    // Local style selection is the only source of these grants. Never accepted
    // from remote JSON, persisted to history, or inferred from a style id alone.
    [JsonIgnore] public IReadOnlyList<string>? StyleAutoAllow { get; init; }
    // Captured from local application settings at acceptance, never supplied by remote JSON.
    [JsonIgnore] public PhaseModelsSnapshot? PhaseModels { get; init; }
    private readonly IReadOnlyList<RunAttachment>? attachments = Attachments;
    public IReadOnlyList<RunAttachment>? Attachments { get => attachments is { Count: > 0 } ? attachments : null; init => attachments = value; }
    public IReadOnlyList<RegisteredModelEntry> RegisteredModels { get; init; } = RegisteredModels ?? [];
    public StartRunRequest Validate()
    {
        var settings = Settings ?? new();
        var files = AttachmentSupport.Validate(Attachments);
        if (!Wire.Identifier(SessionId) || !Wire.Identifier(WorkspaceId) || Kind is not ("claude" or "shell") || !Wire.Model(Model) || !Wire.Providers.Contains(Provider) || ResumeId is not null && !Wire.Identifier(ResumeId)) throw new ArgumentException(Locale.Get("wire.startRun.invalidFormat"));
        if (Input is null || string.IsNullOrWhiteSpace(Input) && files is null || Input.Length > 100000 || Input.Contains('\0')) throw new ArgumentException(Locale.Get("wire.startRun.emptyInput"));
        if (Kind == "shell" && files is not null) throw new ArgumentException(Locale.Get("wire.startRun.attachmentAiOnly"));
        if (settings.Effort != "default" && !Wire.Efforts.Contains(settings.Effort) || settings.PermissionMode is not ("manual" or "plan" or "onRequest" or "acceptEdits" or "auto" or "fullAccess") || settings.MaxTurns is < 1 or > 1000 || settings.MaxBudgetUsd is double budget && (!double.IsFinite(budget) || budget <= 0 || budget > 10000) || settings.WebSearch is not ("default" or "disabled" or "cached" or "live")) throw new ArgumentException(Locale.Get("wire.startRun.invalidSettings"));
        if (settings.PermissionMode == "onRequest" && (Provider != "codex" || Kind != "claude")) throw new ArgumentException(Locale.Get("wire.startRun.codexOnlyFeatures"));
        if (settings.PermissionMode == "auto" && (Kind != "claude" || Provider != "claude")) throw new ArgumentException(Locale.Get("wire.startRun.autoModeClaudeOnly"));
        if ((Provider != "codex" || Kind != "claude") && (settings.FastMode || settings.WebSearch != "default" || settings.NetworkAccess) || settings.NetworkAccess && settings.PermissionMode is not ("acceptEdits" or "onRequest")) throw new ArgumentException(Locale.Get("wire.startRun.codexOnlyFeatures"));
        if (Kind == "claude" && (Provider != "claude" && (settings.MaxTurns is not null || settings.MaxBudgetUsd is not null) || Provider == "codex" && settings.PermissionMode == "plan" || Provider == "gemini" && settings.Effort != "default" || Provider == "claude" && Model.Contains("haiku", StringComparison.OrdinalIgnoreCase) && settings.Effort != "default")) throw new ArgumentException(Locale.Get("wire.startRun.unsupportedSettings"));
        if (StyleAutoAllow is { Count: > 0 } grants && (Provider != "claude" || Kind != "claude" || grants.Count > 32 || grants.Any(g => !StyleRunPermissions.ValidWireName(g))))
            throw new ArgumentException("Invalid local style tool permissions.");
        return this with { Settings = settings, Attachments = files, StyleAutoAllow = StyleAutoAllow is { Count: > 0 } validGrants ? Array.AsReadOnly(validGrants.ToArray()) : null };
    }
}
public sealed record RunEvent(string SessionId, string Type, LogEntry? Entry = null, string? Status = null, string? ResumeId = null, AgentActivity? Activity = null, SessionUsage? Usage = null, ToolPermissionRequest? Permission = null, MightyGraphRun? GraphRun = null, string? Reason = null)
{
    public static RunEvent Log(string id, string kind, string text, string? provider = null) => new(id, "log", new(Wire.Id(), kind, ActivitySupport.Clean(text, kind == "assistant" ? ActivitySupport.MaximumMessageBytes : 32768), Wire.Now(), provider));
    public static RunEvent State(string id, string state, string? reason = null) => new(id, "status", Status: state, Reason: reason);
    public bool Valid() => Wire.Identifier(SessionId) && (Type == "status" && Status is "idle" or "running" or "completed" or "error" or "stopped" || Type == "resume" && Wire.Identifier(ResumeId) || Type == "log" && Entry is not null && Wire.Identifier(Entry.Id) && LogEntry.Stored.Contains(Entry.Kind) && (Entry.Kind != "image" || AgentImageSupport.Normalized(Entry.Images) is not null) && Entry.Text is not null && System.Text.Encoding.UTF8.GetByteCount(Entry.Text) <= (Entry.Kind == "assistant" ? ActivitySupport.MaximumMessageBytes : 32768) || Type == "activity" && ActivitySupport.Normalize(Activity) is not null || Type == "usage" || Type == "graph_run" && GraphRun is not null && Wire.Identifier(GraphRun.Id));
}
public sealed record ModelOption(string Value, string DisplayName, string Description, string? ResolvedModel = null, bool? SupportsEffort = null, string[]? SupportedEffortLevels = null);
public sealed record ModelCatalog(string Source, List<ModelOption> Models, string Detail);
public sealed record ProviderCapabilities(bool Effort, string[] PermissionModes, bool MaxTurns, bool MaxBudgetUsd, bool Resume, bool FastMode = false, bool WebSearch = false, bool NetworkAccess = false, bool Attachments = false);
public sealed record ProviderRuntime(string Id, string Name, bool Available, string? Version, string Detail, ModelCatalog ModelCatalog, ProviderCapabilities Capabilities);
public sealed record ModsInfo(string Status, string MinimumVersion, string Detail);
public sealed record RuntimeInfo(string Platform, string AppVersion, bool ClaudeAvailable, string? ClaudeVersion, ModelCatalog? ModelCatalog, List<ProviderRuntime> Providers, ModsInfo? Mods);
