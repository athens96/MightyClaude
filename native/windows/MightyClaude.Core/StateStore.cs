using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed class StateStore(string directory, string? legacyDirectory = null)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Workspace> approved = [];
    private bool loaded;
    public string DirectoryPath { get; } = directory;
    public AppSnapshot Snapshot { get; private set; } = new();
    private string StatePath => Path.Combine(DirectoryPath, "workspace-state.json");

    /// <summary>
    /// The saved theme setting, read before the state loads so the launch splash paints in the
    /// user's theme rather than a default one: the top-level <c>theme</c> of the saved state, or
    /// "dark" (the <see cref="AppSnapshot"/> default) when there is no readable file. Never throws.
    /// </summary>
    public static string SavedTheme(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "workspace-state.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return "dark";
            var reader = new Utf8JsonReader(File.ReadAllBytes(path));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return "dark";
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var theme = reader.ValueTextEquals("theme");
                if (!reader.Read()) break;
                if (theme) return reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "dark" : "dark";
                reader.Skip();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return "dark";
    }
    public async Task<AppSnapshot> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(StatePath) && legacyDirectory is not null)
            {
                var original = Path.Combine(legacyDirectory, "workspace-state.json");
                if (File.Exists(original)) { if (new FileInfo(original).Length > 8 * 1024 * 1024) throw new InvalidDataException(Locale.Get("store.error.legacyFileTooLarge")); File.Copy(original, StatePath, false); }
            }
            if (File.Exists(StatePath))
            {
                if (new FileInfo(StatePath).Length > 8 * 1024 * 1024) throw new InvalidDataException(Locale.Get("store.error.fileTooLarge"));
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath));
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1 || !json.RootElement.TryGetProperty("workspaces", out var workspaces) || workspaces.ValueKind != JsonValueKind.Array || !json.RootElement.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array) throw new InvalidDataException(Locale.Get("store.error.invalidFormat"));
                // A workspace whose "remote" value is not today's (version 2) names a folder on another computer in a shape
                // this app does not read; it is left out before decoding, and Normalize then drops everything keyed by its id.
                var restored = JsonSerializer.Deserialize<AppSnapshot>(KeptWorkspaces(json.RootElement), Wire.Json) ?? throw new InvalidDataException(Locale.Get("store.error.noSnapshot"));
                // Once per profile, on load: `gemini` panes now run Antigravity (AntigravityMigration).
                if (restored.AntigravityMigrated != true) restored = restored with { Sessions = (restored.Sessions ?? []).Select(s => s is null ? s! : AntigravityMigration.Migrated(s)).ToList(), AntigravityMigrated = true };
                Snapshot = Normalize(restored, true);
            }
            // A new profile never held Gemini CLI panes.
            else Snapshot = Snapshot with { AntigravityMigrated = true };
            approved.Clear();
            foreach (var workspace in Snapshot.Workspaces) approved[workspace.Id] = workspace;
            loaded = true;
            return Wire.Clone(Snapshot);
        }
        finally { gate.Release(); }
    }
    public Workspace ApproveLocal(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(Locale.Get("store.error.projectFolderNotFound"));
        lock (approved)
        {
            var existing = approved.Values.FirstOrDefault(w => w.Remote is null && w.Path == path);
            if (existing is not null) return existing;
            var workspace = new Workspace { Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), Path = path };
            approved[workspace.Id] = workspace;
            return workspace;
        }
    }
    /// <summary>
    /// A workspace another computer shares (RemoteClientService): its path is the host's and is never checked or used
    /// here. Opening the same host workspace through the same connection again returns the one already listed.
    /// </summary>
    public Workspace ApproveRemote(string connectionId, string hostWorkspaceId, string name, string path, string hostName)
    {
        var host = Wire.Clean(hostName, 120).Trim();
        if (!Wire.Identifier(connectionId) || !Wire.Identifier(hostWorkspaceId) || !RemotePath(path) || host.Length == 0) throw new ArgumentException(Locale.Get("remote.workspace.error.badWorkspace"));
        lock (approved)
        {
            var existing = approved.Values.FirstOrDefault(w => w.Remote?.ConnectionId == connectionId && w.Remote.WorkspaceId == hostWorkspaceId);
            var item = new Workspace { Id = existing?.Id ?? Wire.Id(), Name = Wire.Clean(name, 120), Path = path, CreatedAt = existing?.CreatedAt ?? Wire.Now(), Remote = new(connectionId, hostWorkspaceId, host) };
            approved[item.Id] = item;
            return item;
        }
    }
    public Workspace GetWorkspace(string id)
    {
        lock (approved) return approved.TryGetValue(id, out var workspace) ? workspace : throw new ArgumentException(Locale.Get("store.error.notApproved"));
    }
    public Task<Workspace> ResolveLocalAsync(string id)
    {
        var workspace = GetWorkspace(id);
        if (workspace.Remote is not null) throw new InvalidOperationException(RemoteWindowsStrings.NotLocal);
        if (!Directory.Exists(workspace.Path)) throw new DirectoryNotFoundException(Locale.Get("store.error.workspaceFolderNotFound"));
        return Task.FromResult(workspace);
    }
    public async Task SaveAsync(AppSnapshot snapshot)
    {
        await gate.WaitAsync();
        try
        {
            if (!loaded) throw new InvalidOperationException(Locale.Get("store.error.notLoaded"));
            snapshot = Normalize(snapshot, false);
            lock (approved)
                foreach (var workspace in snapshot.Workspaces)
                    if (!approved.TryGetValue(workspace.Id, out var original) || workspace.Path != original.Path || workspace.Remote != original.Remote) throw new InvalidOperationException(Locale.Get("store.error.pathNotApproved"));
            // Files panes are never written: they do not come back after a restart.
            var encoded = JsonSerializer.SerializeToUtf8Bytes(FilePaneKind.Stored(snapshot), Wire.Json);
            if (encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException(Locale.Get("store.error.stateTooLarge"));
            await AtomicWriteAsync(StatePath, encoded);
            // Normalize made it, and its records are never edited: it is kept as it is, not copied again.
            Snapshot = snapshot;
        }
        finally { gate.Release(); }
    }
    public static bool ValidWorkspace(Workspace w) => w is not null && Wire.Identifier(w.Id) && w.Path is { Length: > 0 and <= 4096 } && !w.Path.Contains('\0')
        && (w.Remote is not { } remote ? Path.IsPathFullyQualified(w.Path)
            : remote.Version == RemoteWorkspaceReference.CurrentVersion && Wire.Identifier(remote.ConnectionId) && Wire.Identifier(remote.WorkspaceId) && !string.IsNullOrWhiteSpace(Wire.Clean(remote.HostName, 120)) && RemotePath(w.Path));

    /// <summary>A host's folder: absolute on the host's own OS (a Mac's "/…", a Windows drive or UNC path), whatever this OS calls absolute.</summary>
    public static bool RemotePath(string? path) => path is { Length: > 0 and <= 4096 } && !path.Contains('\0')
        && (path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal) || Regex.IsMatch(path, @"^[A-Za-z]:[\\/]"));

    /// <summary>
    /// Whether a saved workspace object is kept on load (M/StateRepository.keepsWorkspace): a local one (no "remote", or an
    /// explicit null), or a remote one whose reference carries version 2 as a JSON number (2.0 included, as the Mac reads it).
    /// </summary>
    public static bool KeepsWorkspace(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (!value.TryGetProperty("remote", out var remote) || remote.ValueKind == JsonValueKind.Null) return true;
        return remote.ValueKind == JsonValueKind.Object && remote.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
            && version.TryGetDouble(out var number) && number == RemoteWorkspaceReference.CurrentVersion;
    }

    /// <summary>
    /// The saved state with only the workspaces <see cref="KeepsWorkspace"/> keeps. The reader matches names without regard to
    /// case, so only the exact "workspaces" and "remote" keys are read, as on the Mac; any other spelling of them is left out.
    /// A kept remote workspace is written with the integer version (a saved 2.0 reads into the int), and one whose reference
    /// does not decode is left out on its own, as the Mac decodes each workspace apart.
    /// </summary>
    private static byte[] KeptWorkspaces(JsonElement root)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.Equals("workspaces", StringComparison.OrdinalIgnoreCase)) { property.WriteTo(writer); continue; }
                if (!property.NameEquals("workspaces") || property.Value.ValueKind != JsonValueKind.Array) continue;
                writer.WritePropertyName(property.Name); writer.WriteStartArray();
                foreach (var workspace in property.Value.EnumerateArray().Where(KeepsWorkspace))
                {
                    // Every "remote" object the workspace holds must itself carry the number 2 (a repeated key is checked too),
                    // so no reference without it is ever read with the record's default version.
                    var remote = false; var current = true;
                    var item = new ArrayBufferWriter<byte>();
                    using (var one = new Utf8JsonWriter(item))
                    {
                        one.WriteStartObject();
                        foreach (var field in workspace.EnumerateObject())
                        {
                            if (!field.Name.Equals("remote", StringComparison.OrdinalIgnoreCase)) field.WriteTo(one);
                            else if (!field.NameEquals("remote")) continue;
                            else if (field.Value.ValueKind != JsonValueKind.Object) { remote |= field.Value.ValueKind != JsonValueKind.Null; field.WriteTo(one); }
                            else
                            {
                                remote = true; var versioned = false;
                                one.WritePropertyName(field.Name); one.WriteStartObject();
                                foreach (var part in field.Value.EnumerateObject())
                                    if (!part.Name.Equals("version", StringComparison.OrdinalIgnoreCase)) part.WriteTo(one);
                                    else if (part.NameEquals("version") && part.Value.ValueKind == JsonValueKind.Number && part.Value.TryGetDouble(out var number) && number == RemoteWorkspaceReference.CurrentVersion) { one.WriteNumber(part.Name, RemoteWorkspaceReference.CurrentVersion); versioned = true; }
                                    else current = false;
                                one.WriteEndObject();
                                current &= versioned;
                            }
                        }
                        one.WriteEndObject();
                    }
                    if (remote)
                        try { if (!current || JsonSerializer.Deserialize<Workspace>(item.WrittenSpan, Wire.Json) is null) continue; }
                        catch (JsonException) { continue; }
                    writer.WriteRawValue(item.WrittenSpan, true);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
    /// <summary>A pane as a normalize left it: the text budget it started with and how much of it its text took.</summary>
    private sealed record NormalizedSession(int BudgetBefore, int Cost);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RunSession, NormalizedSession> NormalizedSessions = new();

    public static AppSnapshot Normalize(AppSnapshot value, bool restoring)
    {
        if (value.Version != 1) return new();
        value = value with { PhaseModels = PhaseModelPreferences.Normalize(value.PhaseModels) };
        var workspaces = (value.Workspaces ?? []).Where(ValidWorkspace).DistinctBy(w => w.Id).Take(64).Select(w => w with { Name = Wire.Clean(w.Name, 120), Remote = w.Remote is { } r ? r with { HostName = Wire.Clean(r.HostName, 120) } : null }).ToList();
        var ids = workspaces.Select(w => w.Id).ToHashSet();
        // A remote workspace's panes run on another computer, whose CLIs this PC cannot see: they never ask another CLI.
        var remoteIds = workspaces.Where(w => w.Remote is not null).Select(w => w.Id).ToHashSet();
        var textBudget = 2 * 1024 * 1024;
        string Bounded(string? text, int limit) { var result = Wire.Clean(text, Math.Min(limit, Math.Max(0, textBudget / 6))); textBudget -= JsonSerializer.SerializeToUtf8Bytes(result, Wire.Json).Length; return result; }
        LogEntry NormalizeLog(LogEntry log)
        {
            var activity = ActivitySupport.Normalize(log.Activity, restoring);
            if (activity is not null) activity = activity with { Summary = Bounded(activity.Summary, 1000), Output = activity.Output is not null ? Bounded(activity.Output, 8192) : null };
            return log with { Text = Bounded(log.Text, log.Kind == "assistant" ? ActivitySupport.MaximumMessageBytes : 32768), Provider = Wire.Providers.Contains(log.Provider) ? log.Provider : null, Activity = activity, Images = log.Kind == "image" ? AgentImageSupport.Normalized(log.Images) : null };
        }
        RunSession NormalizeSession(RunSession s)
        {
            // A pane this normalize made before and nothing replaced since is normalized already: it is taken as it is, its text
            // counted against the budget as before, when at least as much budget is left as it was made with (its limits were no
            // larger) and it is not running (a running pane's clock moves on each normalize). Normalizing every pane's whole
            // history on each change took tens of milliseconds and ~100 MB for long transcripts, on the UI thread.
            if (!restoring && s.Status != "running" && NormalizedSessions.TryGetValue(s, out var made) && textBudget >= made.BudgetBefore)
            {
                textBudget -= made.Cost;
                return s;
            }
            var before = textBudget;
            var result = NormalizeSessionFully(s);
            if (!restoring) NormalizedSessions.AddOrUpdate(result, new NormalizedSession(before, before - textBudget));
            return result;
        }
        RunSession NormalizeSessionFully(RunSession s)
        {
            // A files pane runs nothing and keeps nothing but its title (docs/file-pane.md).
            if (FilePaneKind.IsFilePane(s.Kind))
                return new RunSession { Id = s.Id, WorkspaceId = s.WorkspaceId, Kind = FilePaneKind.Kind, Title = RenameSupport.ClampTitle(s.Title) };
            if (s.Kind is "browser" or AgentIOPaneKind.Browser)
                return s with { Title = RenameSupport.ClampTitle(s.Title), Status = "idle", Logs = [], Draft = "", RunTiming = null, SessionUsage = null, CurrentActivity = null, AgentViewMode = null, GraphRuns = null, GraphResultSize = null, GraphPlanSize = null, GraphBlockSizes = null, GraphViewMode = null, PlanHistory = null, TodoProgress = null, BackgroundWork = null, ResumeId = null, WorkspaceProfileKey = Wire.Clean(s.WorkspaceProfileKey, 128) is { Length: > 0 } wpk ? wpk : null, OwnerSessionId = Wire.Identifier(s.OwnerSessionId) ? s.OwnerSessionId : null, ParentSessionId = null, WorkingFolder = null, AskTarget = null };
            var provider = Wire.Providers.Contains(s.Provider) ? s.Provider : "claude";
            var logs = (s.Logs ?? []).Where(l => l is not null && Wire.Identifier(l.Id) && LogEntry.Stored.Contains(l.Kind)).TakeLast(TranscriptRetention.MaximumEntries).Select(NormalizeLog).Where(l => l.Text.Length > 0).ToList();
            var timing = s.Kind == "shell" ? null : s.RunTiming is { IsValid: true } ? s.RunTiming : AgentRunTiming.Infer(logs);
            if (timing is not null) timing = restoring ? timing.Interrupt() : s.Status == "running" ? timing.Observe() : timing;
            var usage = s.Kind == "claude" && s.SessionUsage?.Provider == provider ? SessionUsageSupport.Normalize(s.SessionUsage) : null;
            var viewMode = s.AgentViewMode is "default" or "mighty" ? s.AgentViewMode : null;
            var graphBudget = MightyGraphSupport.LiveHistoryLimit;
            var graphRuns = s.GraphRuns is { Count: > 0 } ? MightyGraphSupport.Normalized(s.GraphRuns, restoring, budget: ref graphBudget) : null;
            if (graphRuns is { Count: 0 }) graphRuns = null;
            // Sizes saved before the 실행 split move once; the pane is in the current layout from then on (macOS StateRepository.normalize).
            var blockSizes = s.GraphSizesLayout is null ? GraphBlockPreferences.SplittingExecution(s.GraphBlockSizes) : s.GraphBlockSizes;
            var planMode = s.Kind == "claude" && provider == "claude";
            var normalized = s with { Title = RenameSupport.ClampTitle(s.Title), Draft = Bounded(s.Draft, 100000), Provider = provider, Model = Wire.Model(s.Model) ? s.Model : "default", Settings = ProviderCatalog.NormalizeSettings(provider, s.Settings), ResumeId = Wire.Identifier(s.ResumeId) ? s.ResumeId : null, Status = restoring && s.Status == "running" ? s.Kind == "shell" ? "idle" : "stopped" : s.Status is "idle" or "running" or "completed" or "error" or "stopped" ? s.Status : "idle", Logs = logs, RunTiming = timing, SessionUsage = usage, CurrentActivity = restoring ? null : ActivitySupport.Normalize(s.CurrentActivity), AgentViewMode = viewMode, GraphViewMode = s.Kind == "shell" ? null : MightyTimeline.NormalizeMode(s.GraphViewMode), GraphRuns = graphRuns, GraphResultSize = s.Kind == "shell" ? null : s.GraphResultSize?.Normalized, GraphPlanSize = s.Kind == "shell" ? null : s.GraphPlanSize?.Normalized, GraphSizesLayout = GraphBlockPreferences.SplitLayout, GraphBlockSizes = s.Kind == "shell" ? null : GraphBlockPreferences.Normalize(blockSizes, graphRuns ?? []), TitleMode = PaneTitle.NormalizedMode(s.TitleMode),
                PlanHistory = planMode ? ClaudePlanMode.NormalizedHistory(s.PlanHistory) : null, TodoProgress = planMode ? TodoProgress.Normalized(s.TodoProgress) : null,
                BackgroundWork = planMode ? BackgroundWork.Normalized(s.BackgroundWork, restoring || s.Status != "running") : null,
                // A child's links stay even when its parent pane is gone: a closed parent keeps its children. Only agent panes can be children; a malformed value is dropped.
                ParentSessionId = s.Kind == "claude" && Wire.Identifier(s.ParentSessionId) && s.ParentSessionId != s.Id ? s.ParentSessionId : null,
                WorkingFolder = s.Kind == "claude" && DelegationPanes.IsWorkingFolder(s.WorkingFolder) ? s.WorkingFolder : null,
                // Only a Claude agent pane asks other CLIs; a saved id that is no longer offered is kept and reads as Claude (macOS StateRepository.normalize).
                AskTarget = planMode && !remoteIds.Contains(s.WorkspaceId) ? s.AskTarget : null };
            return restoring ? PaneTitle.Restored(normalized) : normalized;
        }
        var sessions = (value.Sessions ?? []).Where(s => s is not null && Wire.Identifier(s.Id) && ids.Contains(s.WorkspaceId) && (s.Kind is "claude" or "shell" or "browser" || !restoring && FilePaneKind.IsFilePane(s.Kind) && s.Id == FilePaneKind.PaneId(s.WorkspaceId) || !restoring && AgentIOPaneKind.IsAgentIO(s.Kind) && s.OwnerSessionId is { } owner && s.Id == AgentIOPaneKind.PaneId(owner, s.Kind) && (value.Sessions ?? []).Any(parent => parent.Id == owner && parent.Kind == "claude" && parent.WorkspaceId == s.WorkspaceId))).DistinctBy(s => s.Id).Take(128).Select(NormalizeSession).ToList();
        var workspaceId = ids.Contains(value.ActiveWorkspaceId ?? "") ? value.ActiveWorkspaceId : workspaces.FirstOrDefault()?.Id;
        value = value with { ExpandedWorkspaceIds = value.ExpandedWorkspaceIds?.Where(ids.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
        var activeSessionId = sessions.FirstOrDefault(s => s.WorkspaceId == workspaceId && s.Id == value.ActiveSessionId)?.Id ?? sessions.FirstOrDefault(s => s.WorkspaceId == workspaceId)?.Id;
        Dictionary<string, PaneLayoutNode>? layouts = null;
        if (value.PaneLayouts is not null)
        {
            layouts = [];
            foreach (var pair in value.PaneLayouts.Where(pair => ids.Contains(pair.Key)))
                if (PaneLayout.Normalize(pair.Value, sessions.Where(s => s.WorkspaceId == pair.Key).Select(s => s.Id), pair.Key == workspaceId ? activeSessionId : value.PaneLayoutActiveSessionIds?.GetValueOrDefault(pair.Key)) is { } node) layouts[pair.Key] = node;
        }
        var modes = new Dictionary<string, string>(); var selections = new Dictionary<string, string>();
        string? Selected(PaneLayoutNode? node) => node is null ? null : node.Kind == "tabs" ? node.SelectedSessionId ?? node.SessionIds.FirstOrDefault() : node.Children.Select(Selected).FirstOrDefault(v => v is not null);
        foreach (var workspace in workspaces)
        {
            var root = layouts?.GetValueOrDefault(workspace.Id); var savedMode = value.PaneLayoutModes?.GetValueOrDefault(workspace.Id);
            modes[workspace.Id] = savedMode is "grid" or "columns" or "focus" or "tabs" or "custom" ? savedMode : value.PaneLayoutModes is null && value.Layout == "focus" && workspace.Id == workspaceId ? "focus" : root is not null ? root.Kind == "tabs" ? "tabs" : "custom" : value.Layout is "grid" or "columns" or "tabs" ? value.Layout : "grid";
            var paneIds = sessions.Where(s => s.WorkspaceId == workspace.Id).Select(s => s.Id).ToHashSet();
            var preferred = workspace.Id == workspaceId ? activeSessionId : value.PaneLayoutActiveSessionIds?.GetValueOrDefault(workspace.Id);
            var selected = preferred is not null && paneIds.Contains(preferred) ? preferred : Selected(root) ?? paneIds.FirstOrDefault();
            if (selected is not null) selections[workspace.Id] = selected;
        }
        var trustedStatusLines = value.TrustedStatusLines?.Where(p => Wire.Identifier(p.Key) && p.Value is { Length: > 0 and <= 64 }).ToDictionary(p => p.Key, p => p.Value);
        return value with { Workspaces = workspaces, Sessions = sessions, ActiveWorkspaceId = workspaceId, ActiveSessionId = activeSessionId, PaneLayouts = layouts, PaneLayoutModes = modes, PaneLayoutActiveSessionIds = selections, Layout = value.Layout is "grid" or "columns" or "focus" or "tabs" or "custom" ? value.Layout : "grid", Theme = value.Theme == "light" ? "light" : "dark", SidebarWidth = double.IsFinite(value.SidebarWidth) ? Math.Clamp(value.SidebarWidth, DesignMetrics.Layout.SidebarMin, DesignMetrics.Layout.SidebarMax) : DesignMetrics.Layout.SidebarDefault, ReferenceBubbleWidth = ReferencePreview.BubbleSize(value.ReferenceBubbleWidth, value.ReferenceBubbleHeight).Width, ReferenceBubbleHeight = ReferencePreview.BubbleSize(value.ReferenceBubbleWidth, value.ReferenceBubbleHeight).Height, TrustedStatusLines = trustedStatusLines?.Count > 0 ? trustedStatusLines : null, LanguagePreference = Locale.Languages.Contains(value.LanguagePreference) ? value.LanguagePreference : "system", AgentWebOpenChoices = value.AgentWebOpenChoices?.Where(p => ids.Contains(p.Key) && p.Value is "inApp" or "external").ToDictionary(p => p.Key, p => p.Value), MobileRemote = (value.MobileRemote ?? new()).Normalized, SettingsPane = value.SettingsPane is "general" or "models" or "styles" or "tools" or "cli" or "mobile" or "remote" or "companion" or "about" ? value.SettingsPane : "general" };
    }
    public static async Task AtomicWriteAsync(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Wire.Id() + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // Another reader can hold the file for a moment (on Windows a plain read shares no delete, so the replace is
            // refused with "Access to the path is denied"). A refused replace is tried again for about 2 s before the save
            // fails, so a brief read never drops the newest state.
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 40) { await Task.Delay(50); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>
/// When a save of the workspace state starts (M/StateRepository.swift SaveSchedule): a short debounce after the change that
/// asked for it, and at least <c>minimumGap</c> seconds after the last save began, so a streaming run no longer writes the whole
/// state back to back.
/// </summary>
public static class SaveSchedule
{
    public static double Delay(double? sinceLastStart, double debounce = 0.22, double minimumGap = 1.5) =>
        sinceLastStart is not { } since || !double.IsFinite(since) ? debounce : Math.Max(debounce, minimumGap - Math.Max(0, since));

    /// <summary>
    /// Whether a save that is already waiting stays as it is when another change comes, instead of being scheduled again.
    /// Without a cap, changes arriving faster than the debounce would push the save back for ever; so once
    /// <paramref name="minimumGap"/> has passed since the last save began, or since the waiting save was first asked for, it is
    /// left to fire (it writes the state as it is then, the new change too).
    /// </summary>
    public static bool KeepsPending(double? sinceLastStart, double sinceFirstRequest, double minimumGap = 1.5) =>
        sinceLastStart is { } since && double.IsFinite(since) && since >= minimumGap || double.IsFinite(sinceFirstRequest) && sinceFirstRequest >= minimumGap;
}
