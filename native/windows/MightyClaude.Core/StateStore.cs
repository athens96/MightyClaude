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
                Snapshot = Normalize(json.RootElement.Deserialize<AppSnapshot>(Wire.Json) ?? throw new InvalidDataException(Locale.Get("store.error.noSnapshot")), true);
            }
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
    public Workspace ApproveRemote(string connectionId, Workspace peer, string hostName)
    {
        if (!Wire.Identifier(connectionId) || !ValidWorkspace(peer) || peer.Remote is not null) throw new ArgumentException(Locale.Get("store.error.invalidRemoteWorkspace"));
        lock (approved)
        {
            var existing = approved.Values.FirstOrDefault(w => w.Remote?.ConnectionId == connectionId && w.Remote.WorkspaceId == peer.Id);
            if (existing is not null) return existing;
            var workspace = peer with { Id = Wire.Id(), Remote = new(connectionId, peer.Id, Wire.Clean(hostName, 120)) };
            approved[workspace.Id] = workspace;
            return workspace;
        }
    }
    public Workspace GetWorkspace(string id)
    {
        lock (approved) return approved.TryGetValue(id, out var workspace) ? workspace : throw new ArgumentException(Locale.Get("store.error.notApproved"));
    }
    public Task<Workspace> ResolveLocalAsync(string id)
    {
        var workspace = GetWorkspace(id);
        if (workspace.Remote is not null) throw new ArgumentException(Locale.Get("store.error.remoteNotLocal"));
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
            var encoded = JsonSerializer.SerializeToUtf8Bytes(snapshot, Wire.Json);
            if (encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException(Locale.Get("store.error.stateTooLarge"));
            await AtomicWriteAsync(StatePath, encoded);
            Snapshot = Wire.Clone(snapshot);
        }
        finally { gate.Release(); }
    }
    public static bool ValidWorkspace(Workspace w) => w is not null && Wire.Identifier(w.Id) && w.Path is { Length: > 0 and <= 4096 } && !w.Path.Contains('\0') && (Path.IsPathFullyQualified(w.Path) || Regex.IsMatch(w.Path, @"^[a-zA-Z]:[\\/]")) && (w.Remote is null || Wire.Identifier(w.Remote.ConnectionId) && Wire.Identifier(w.Remote.WorkspaceId));
    public static AppSnapshot Normalize(AppSnapshot value, bool restoring)
    {
        if (value.Version != 1) return new();
        var workspaces = (value.Workspaces ?? []).Where(ValidWorkspace).DistinctBy(w => w.Id).Take(64).Select(w => w with { Name = Wire.Clean(w.Name, 120) }).ToList();
        var ids = workspaces.Select(w => w.Id).ToHashSet();
        var textBudget = 2 * 1024 * 1024;
        string Bounded(string? text, int limit) { var result = Wire.Clean(text, Math.Min(limit, Math.Max(0, textBudget / 6))); textBudget -= JsonSerializer.SerializeToUtf8Bytes(result, Wire.Json).Length; return result; }
        LogEntry NormalizeLog(LogEntry log)
        {
            var activity = ActivitySupport.Normalize(log.Activity, restoring);
            if (activity is not null) activity = activity with { Summary = Bounded(activity.Summary, 1000), Output = activity.Output is not null ? Bounded(activity.Output, 8192) : null };
            return log with { Text = Bounded(log.Text, log.Kind == "assistant" ? ActivitySupport.MaximumMessageBytes : 32768), Provider = Wire.Providers.Contains(log.Provider) ? log.Provider : null, Activity = activity };
        }
        RunSession NormalizeSession(RunSession s)
        {
            if (s.Kind == "browser")
                return s with { Title = RenameSupport.ClampTitle(s.Title), Status = "idle", Logs = [], Draft = "", RunTiming = null, SessionUsage = null, CurrentActivity = null, AgentViewMode = null, GraphRuns = null, ResumeId = null, WorkspaceProfileKey = Wire.Clean(s.WorkspaceProfileKey, 128) is { Length: > 0 } wpk ? wpk : null, OwnerSessionId = Wire.Identifier(s.OwnerSessionId) ? s.OwnerSessionId : null };
            var provider = Wire.Providers.Contains(s.Provider) ? s.Provider : "claude";
            var logs = (s.Logs ?? []).Where(l => l is not null && Wire.Identifier(l.Id) && l.Kind is "user" or "assistant" or "system" or "output" or "error").TakeLast(300).Select(NormalizeLog).Where(l => l.Text.Length > 0).ToList();
            var timing = s.Kind == "shell" ? null : s.RunTiming is { IsValid: true } ? s.RunTiming : AgentRunTiming.Infer(logs);
            if (timing is not null) timing = restoring ? timing.Interrupt() : s.Status == "running" ? timing.Observe() : timing;
            var usage = s.Kind == "claude" && s.SessionUsage?.Provider == provider ? SessionUsageSupport.Normalize(s.SessionUsage) : null;
            var viewMode = s.AgentViewMode is "default" or "mighty" ? s.AgentViewMode : null;
            var graphBudget = MightyGraphSupport.LiveHistoryLimit;
            var graphRuns = s.GraphRuns is { Count: > 0 } ? MightyGraphSupport.Normalized(s.GraphRuns, restoring: true, budget: ref graphBudget) : null;
            if (graphRuns is { Count: 0 }) graphRuns = null;
            return s with { Title = RenameSupport.ClampTitle(s.Title), Draft = Bounded(s.Draft, 100000), Provider = provider, Model = Wire.Model(s.Model) ? s.Model : "default", Settings = ProviderCatalog.NormalizeSettings(provider, s.Settings), ResumeId = Wire.Identifier(s.ResumeId) ? s.ResumeId : null, Status = restoring && s.Status == "running" ? "stopped" : s.Status is "idle" or "running" or "completed" or "error" or "stopped" ? s.Status : "idle", Logs = logs, RunTiming = timing, SessionUsage = usage, CurrentActivity = restoring ? null : ActivitySupport.Normalize(s.CurrentActivity), AgentViewMode = viewMode, GraphRuns = graphRuns };
        }
        var sessions = (value.Sessions ?? []).Where(s => s is not null && Wire.Identifier(s.Id) && ids.Contains(s.WorkspaceId) && s.Kind is "claude" or "shell" or "browser").DistinctBy(s => s.Id).Take(128).Select(NormalizeSession).ToList();
        var workspaceId = ids.Contains(value.ActiveWorkspaceId ?? "") ? value.ActiveWorkspaceId : workspaces.FirstOrDefault()?.Id;
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
        return value with { Workspaces = workspaces, Sessions = sessions, ActiveWorkspaceId = workspaceId, ActiveSessionId = activeSessionId, PaneLayouts = layouts, PaneLayoutModes = modes, PaneLayoutActiveSessionIds = selections, Layout = value.Layout is "grid" or "columns" or "focus" or "tabs" or "custom" ? value.Layout : "grid", Theme = value.Theme == "light" ? "light" : "dark", SidebarWidth = double.IsFinite(value.SidebarWidth) ? Math.Clamp(value.SidebarWidth, 200, 400) : 252, TrustedStatusLines = trustedStatusLines?.Count > 0 ? trustedStatusLines : null, LanguagePreference = value.LanguagePreference is "ko" or "en" ? value.LanguagePreference : "system" };
    }
    public static async Task AtomicWriteAsync(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Wire.Id() + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, data); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
