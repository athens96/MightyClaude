using System.Globalization;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The web shell's frame (native/windows/web/src/shell, layout, dashboard, dialogs; spec .omc/plans/shell-spec.md): the
/// sidebar, the workspace header and banners, the layout of tab groups and splits, the dashboard and the status bar. What
/// they show is read here from the same Core rules and window state the WinUI frame draws from, as one model the page gets
/// whole (<c>{kind:"shell"}</c>, pushed when it changed, looked at four times a second); every action is a method that runs
/// the window's own action (SelectLayoutSession, DockSession, CloseSession, the rename and remove flows, ...). Dialogs the
/// window's flows open are drawn by the page: a flow awaits the page's answer (<see cref="OpenWebDialog"/>).
/// </summary>
public sealed partial class MainWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? webShellClock;
    private string? webShellSent;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> webDialogs = [];
    private int webDialogNext;
    /// <summary>The resume list the page shows: its agent, its workspace, the listing read for it and whether that listing holds the automated sessions.</summary>
    private (string Provider, Workspace Workspace, ResumableSessionListing? Listing, bool All)? webResume;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string ToneName(DesignTone tone) => tone.ToString().ToLowerInvariant();
    private static double? ArgNumber(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static bool ArgFlag(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void RegisterShellMethods(WebBridge bridge)
    {
        bridge.Register("shellState", _ => Task.FromResult<object?>(WebShellModel()));
        bridge.Register("selectWorkspace", async args => { if (args.Text("id") is { } id) await SelectWorkspace(id); return null; });
        bridge.Register("activatePane", args => { if (args.Text("id") is { } id) ActivatePane(id); return Task.FromResult<object?>(null); });
        bridge.Register("showDashboard", _ => { showsDashboard = true; RenderSidebar(); RenderDashboard(); return Task.FromResult<object?>(null); });
        bridge.Register("hideDashboard", _ => { HideDashboard(); RenderSidebar(); return Task.FromResult<object?>(null); });
        bridge.Register("toggleDisclosure", async args => { if (args.Text("id") is { } id) await ToggleWorkspaceDisclosure(id, false); return null; });
        bridge.Register("setSidebarWidth", async args =>
        {
            if (ArgNumber(args, "width") is not { } asked || double.IsNaN(asked)) return null;
            var width = Math.Clamp(asked, DesignMetrics.Layout.SidebarMin, DesignMetrics.Layout.SidebarMax);
            await Act(() => service.UpdateAsync(s => s with { SidebarWidth = width })); root.ColumnDefinitions[0].Width = new(width);
            return null;
        });
        bridge.Register("setSidebarCollapsed", async args => { await Act(() => SetSidebarCollapsed(ArgFlag(args, "collapsed"))); return null; });
        bridge.Register("applyLayoutPreset", async args => { if (args.Text("preset") is { } preset && preset is "grid" or "columns" or "focus" or "tabs" or "custom") await ApplyLayoutPreset(preset); return null; });
        bridge.Register("resizeSplit", async args =>
        {
            if (args.Text("workspaceId") is { } workspace && args.Text("splitId") is { } split && ArgNumber(args, "ratio") is { } ratio && !double.IsNaN(ratio)) await ResizeSplit(workspace, split, PaneLayout.ClampRatio(ratio));
            return null;
        });
        bridge.Register("resetSplit", async args => { if (args.Text("workspaceId") is { } workspace && args.Text("splitId") is { } split) await ResetSplit(workspace, split); return null; });
        bridge.Register("dockSession", async args =>
        {
            if (args.Text("sessionId") is not { } id || args.Text("workspaceId") is not { } workspace || args.Text("groupId") is not { } group) return null;
            var edge = args.Text("edge") is { } side && side is "left" or "right" or "top" or "bottom" ? side : "center";
            int? index = ArgNumber(args, "index") is { } at && at >= 0 ? (int)at : null;
            await DockSession(id, workspace, group, edge, index);
            return null;
        });
        bridge.Register("closeSession", async args => { if (args.Text("id") is { } id) await CloseSession(id); return null; });
        bridge.Register("renameSession", async args => { if (args.Text("id") is { } id) await RenameSession(id); return null; });
        bridge.Register("renameWorkspace", async args => { if (args.Text("id") is { } id) await RenameWorkspace(id); return null; });
        bridge.Register("renameCheck", args =>
        {
            var text = args.Text("text") ?? "";
            return Task.FromResult<object?>(new { valid = RenameSupport.IsValid(text), messages = RenameSupport.Messages(text) });
        });
        RegisterWorkspaceCreateMethods(bridge);
        bridge.Register("removeWorkspace", async args => { if (args.Text("id") is { } id) await ConfirmRemoveWorkspace(id); return null; });
        bridge.Register("showInExplorer", async args => { if (args.Text("id") is { } id) await ShowWorkspaceInExplorer(id); return null; });
        bridge.Register("openFiles", async args => { if (!dialogOpen) { HideDashboard(); await OpenFilePane(args.Text("workspaceId")); } return null; });
        bridge.Register("focusPane", async args =>
        {
            if (args.Text("id") is not { } id) return null;
            // menu.focusPane always focuses; session.menu.focus goes back to custom when the layout is focused already (TogglePaneFocus).
            if (ArgFlag(args, "toggle")) await TogglePaneFocus(id);
            else await Act(async () => { await SelectLayoutSession(id); await ApplyLayoutPreset("focus"); });
            return null;
        });
        // The pane header's plugin button (M/SessionPaneView.swift headerControls): the provider's plugin browser, as the WinUI button opens it.
        bridge.Register("openPlugins", async args =>
        {
            if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == args.Text("id")) is { Provider: "claude" or "codex" } pane && !dialogOpen) await OpenPluginBrowser(pane.Provider);
            return null;
        });
        bridge.Register("childAction", async args =>
        {
            if (args.Text("id") is not { } id || !Enum.TryParse<DelegationCardAction>(args.Text("action"), true, out var action)) return null;
            if (!service.IsCardBusy(id) && !dialogOpen) await PerformCardAction(action, id);
            return null;
        });
        bridge.Register("addMenu", args => Task.FromResult<object?>(WebAddMenu(args.Text("workspaceId"))));
        bridge.Register("addPaneEntry", async args =>
        {
            if (args.Text("entry") is not { } entry) return null;
            var group = args.Text("groupId"); var workspace = args.Text("workspaceId");
            if (entry == "connect") await OpenRemoteSettings();
            else if (entry == AddPaneMenu.OpenProject) { if (!dialogOpen) await PickFolder(); }
            else if (entry is AddPaneMenu.NewFolder or AddPaneMenu.FromGit) await ShowWorkspaceCreateAsync(entry);
            else if (workspace is not null && workspace != service.Snapshot.ActiveWorkspaceId) await DashboardAddPane(workspace, entry);
            else if (entry == AddPaneMenu.Shell) await AddPane("shell", groupId: group);
            else if (entry == AddPaneMenu.Browser) await AddBrowserPane(group);
            else if (entry == AddPaneMenu.Files) { HideDashboard(); await OpenFilePane(workspace); }
            else if (AddPaneMenu.AgentProvider(entry) is { } provider) await AddAgentPane(provider, group);
            return null;
        });
        bridge.Register("newPane", async args =>
        {
            if (dialogOpen) return null;
            if (args.Text("kind") == "shell") { if (service.Snapshot.ActiveWorkspaceId is not null) await AddPane("shell"); }
            else await AddPaneFromShortcut();
            return null;
        });
        bridge.Register("pickFolder", async _ => { if (!dialogOpen) await PickFolder(); return null; });
        bridge.Register("connectRemote", async _ => { await OpenRemoteSettings(); return null; });
        bridge.Register("openSettings", async _ => { if (!dialogOpen) await OpenSettings(); return null; });
        bridge.Register("openSetup", _ => { OpenSetup(); return Task.FromResult<object?>(null); });
        bridge.Register("openHelp", async _ => { await OpenHelp(); return null; });
        bridge.Register("textSize", args =>
        {
            var step = ArgNumber(args, "step") ?? 0;
            SetTextAppearance(step == 0 ? AppText.Current.WithScale(1) : AppText.Current.Stepped(step > 0 ? 1 : -1));
            return Task.FromResult<object?>(null);
        });
        bridge.Register("dismissError", _ => { error.Text = ""; return Task.FromResult<object?>(null); });
        bridge.Register("dismissResource", _ => { resourceBanner.Visibility = Visibility.Collapsed; return Task.FromResult<object?>(null); });
        bridge.Register("restartForUpdate", _ => { RestartForAppUpdateNow(); return Task.FromResult<object?>(null); });
        bridge.Register("postponeUpdate", _ => { PostponeAppUpdateRestart(); return Task.FromResult<object?>(null); });
        bridge.Register("usageRefresh", args => { QueueAccountUsageRefresh(ArgFlag(args, "force")); return Task.FromResult<object?>(null); });
        bridge.Register("usageDirect", async args => { await SetUsageDirectLookup(ArgFlag(args, "on")); return null; });
        bridge.Register("dialogAnswer", args =>
        {
            if (ArgNumber(args, "id") is { } id && webDialogs.Remove((int)id, out var waiting))
                waiting.TrySetResult(args.TryGetProperty("answer", out var answer) ? answer.Clone() : default);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("resumeRows", async args => await WebResumeRows(args.Text("search") ?? "", ArgFlag(args, "all")));
        webShellClock = DispatcherQueue.CreateTimer(); webShellClock.Interval = TimeSpan.FromMilliseconds(250); webShellClock.IsRepeating = true;
        webShellClock.Tick += (_, _) => PushWebShellModel(); webShellClock.Start();
    }

    private bool webShellQueued;

    /// <summary>
    /// The frame's model at the next turn of the UI thread, joined with any other change in the same turn: a pane added or
    /// chosen shows in the page at once, not on the 250 ms clock's next tick (that clock still catches changes made elsewhere).
    /// </summary>
    private void QueueWebShellModel()
    {
        if (webShell is null || webShellQueued) return;
        webShellQueued = true;
        if (!DispatcherQueue.TryEnqueue(() => { webShellQueued = false; PushWebShellModel(); })) webShellQueued = false;
    }

    /// <summary>Pushes the frame's model when it changed since the last one sent; the page asks for it whole on mount (<c>shellState</c>).</summary>
    private void PushWebShellModel()
    {
        if (closing || webShell is not { } shell || !shell.Bridge.Ready) { webShellSent = null; return; }
        var model = WebShellModel();
        var json = JsonSerializer.Serialize(model, Wire.Json);
        if (json == webShellSent) return;
        webShellSent = json;
        shell.Bridge.Push(new { kind = "shell", shell = model });
    }

    // ── Dialogs drawn by the page ──

    /// <summary>Pushes a dialog to the page; the task completes with its <c>answer</c> (undefined when it was dismissed or closed by the host).</summary>
    private (int Id, Task<JsonElement> Answer) OpenWebDialog(object dialog)
    {
        var id = ++webDialogNext; var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        webDialogs[id] = waiting;
        webShell?.Bridge.Push(new { kind = "dialog", id, dialog });
        return (id, waiting.Task);
    }

    private void CloseWebDialog(int id)
    {
        if (webDialogs.Remove(id, out var waiting)) waiting.TrySetResult(default);
        webShell?.Bridge.Push(new { kind = "dialogClose", id });
    }

    /// <summary>A confirmation the window's flows build as a ContentDialog (remove workspace, delegation), asked in the page: Primary when its primary button was chosen.</summary>
    private async Task<ContentDialogResult> WebDialog(ContentDialog dialog)
    {
        var message = dialog.Content switch { TextBlock text => text.Text, string words => words, _ => "" };
        var (_, answer) = OpenWebDialog(new
        {
            type = "confirm", title = dialog.Title as string ?? "", message,
            primary = string.IsNullOrEmpty(dialog.PrimaryButtonText) ? null : dialog.PrimaryButtonText, close = dialog.CloseButtonText,
            defaultPrimary = dialog.DefaultButton == ContentDialogButton.Primary,
        });
        return (await answer).ValueKind == JsonValueKind.True ? ContentDialogResult.Primary : ContentDialogResult.None;
    }

    /// <summary>The rename sheet (RenameDialog) in the page: it checks the name through <c>renameCheck</c> (RenameSupport); the name saved is RenameSupport's.</summary>
    private async Task<(bool Automatic, string? Name)> WebRenameDialog(string heading, string hint, string current, bool offerAutomatic)
    {
        dialogOpen = true;
        try
        {
            var (_, answer) = OpenWebDialog(new
            {
                type = "rename", title = heading, hint, current, field = RenameStrings.FieldLabel, save = RenameStrings.ButtonSave, cancel = RenameStrings.ButtonCancel,
                automatic = offerAutomatic ? Locale.Get("pane.rename.automatic") : null,
            });
            var result = await answer;
            if (result.ValueKind != JsonValueKind.Object) return (false, null);
            if (offerAutomatic && result.Text("choice") == "automatic") return (true, null);
            return result.Text("choice") == "save" && result.Text("name") is { } name && RenameSupport.IsValid(name) ? (false, RenameSupport.DisplayName(name)) : (false, null);
        }
        finally { dialogOpen = false; }
    }

    /// <summary>
    /// AddAgentPane with the page's sheets: the same look-up (the head of one record, off the UI thread, the choice opening in its
    /// checking state after <see cref="AddPaneMenu.QuietLookUp"/>), the same rules (AddPaneMenu.AsksResume) and the same pane.
    /// </summary>
    private Task WebAddAgentPane(string provider, string? groupId) => Act(async () =>
    {
        if (!AddPaneMenu.MayAskResume(provider, fromMenu: true) || service.Snapshot.ActiveWorkspaceId is not { } workspaceId
            || service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is not { } workspace || LocalPath(workspace) is null)
        {
            await AddPane("claude", provider, groupId);
            return;
        }
        if (resumeLookup || dialogOpen) return;
        resumeLookup = true;
        string? picked;
        try
        {
            var query = ResumeQuery(workspace) with { HeadOnly = true, MaximumSessions = 1 };
            var reading = Task.Run(() => { try { return ResumableSessions.Listing(query, provider); } catch (Exception) { return ResumableSessionListing.Empty; } });
            var quiet = options.SmokeTest || await Task.WhenAny(reading, Task.Delay(AddPaneMenu.QuietLookUp)) == reading;
            if (service.Snapshot.ActiveWorkspaceId != workspaceId) return;
            bool Asks(ResumableSessionListing found) => AddPaneMenu.AsksResume(provider, found.Items, ResumableSessions.InUse(service.Snapshot.Sessions));
            if (quiet && !Asks(await reading)) { await AddPane("claude", provider, groupId); return; }
            var agent = ProviderMark.Label(provider);
            dialogOpen = true;
            try
            {
                var (id, answer) = OpenWebDialog(new
                {
                    type = "resumeChoice", provider, checking = !quiet,
                    title = Locale.Get("resume.choice.title", new Dictionary<string, string> { ["provider"] = agent }),
                    message = Locale.Get("resume.choice.message", new Dictionary<string, string> { ["provider"] = agent }),
                });
                picked = null;
                if (!quiet && await Task.WhenAny(answer, reading) != answer)
                {
                    // Nothing to continue after all: the pane is added as Start new adds it.
                    if (!Asks(await reading)) { CloseWebDialog(id); picked = "new"; }
                    else webShell?.Bridge.Push(new { kind = "dialogUpdate", id, checking = false });
                }
                if (picked is null) { var result = await answer; picked = result.ValueKind == JsonValueKind.String ? result.GetString() : null; }
            }
            finally { dialogOpen = false; }
        }
        finally { resumeLookup = false; }
        if (picked == "new") await AddPane("claude", provider, groupId);
        else if (picked == "resume") await WebPickResumedSession(provider, workspace, groupId);
    });

    /// <summary>PickResumedSession with the page's list: the rows come from <c>resumeRows</c>; the pick is checked and applied as there.</summary>
    private async Task WebPickResumedSession(string provider, Workspace workspace, string? groupId)
    {
        webResume = (provider, workspace, null, false);
        JsonElement result;
        dialogOpen = true;
        try
        {
            var (_, answer) = OpenWebDialog(new { type = "resumeList", provider, title = Locale.Get("resume.title"), path = workspace.Path });
            result = await answer;
        }
        finally { dialogOpen = false; }
        var listing = webResume?.Listing; webResume = null;
        if (result.ValueKind != JsonValueKind.String || listing?.Items.FirstOrDefault(i => i.SessionID == result.GetString()) is not { } session) return;
        // Taken by another pane while the list was open: never two panes on one session.
        if (ResumableSessions.InUse(service.Snapshot.Sessions).Contains(session.SessionID.ToLowerInvariant())) { error.Text = Locale.Get("resume.error.inUse"); return; }
        service.RememberSession(session.SessionID);
        await AddPane("claude", session.Provider, groupId, pane => ResumableSessions.Apply(session, pane));
    }

    /// <summary>The resume list's rows for the page's search, read again when Show all sessions changed (PickResumedSession's Load and Fill).</summary>
    private async Task<object?> WebResumeRows(string search, bool all)
    {
        if (webResume is not { } open) return null;
        var listing = open.Listing;
        if (listing is null || open.All != all)
        {
            var query = ResumeQuery(open.Workspace) with { IncludeAutomated = all };
            try { listing = await Task.Run(() => ResumableSessions.Listing(query, open.Provider)); }
            catch (Exception) { listing = ResumableSessionListing.Empty; }
            if (webResume is not { } still || !ReferenceEquals(still.Workspace, open.Workspace)) return null;
            webResume = (open.Provider, open.Workspace, listing, all);
        }
        var now = DateTimeOffset.UtcNow; var excluding = ResumableSessions.InUse(service.Snapshot.Sessions);
        var items = ResumableSessions.Filter(listing.Items.Where(i => !excluding.Contains(i.SessionID.ToLowerInvariant())), search);
        var warning = Locale.Get("resume.recentlyModified");
        var rows = items.Select(item =>
        {
            var spoken = ResumableSessions.Details(item, now); var busy = ResumableSessions.MayBeRunning(item, now);
            return new
            {
                id = item.SessionID, provider = item.Provider, title = ResumableSessions.RowTitle(item), untitled = item.Title is null, beta = ProviderCatalog.IsBeta(item.Provider),
                details = busy && spoken.EndsWith(" · " + warning, StringComparison.Ordinal) ? spoken[..^(warning.Length + 3)] : spoken, busy, help = item.Title ?? item.SessionID,
            };
        }).ToList();
        string? empty = null, reason = null;
        if (rows.Count == 0 && string.IsNullOrWhiteSpace(search)) { empty = Locale.Get("resume.empty"); reason = Locale.Get("resume.emptyReason", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(open.Provider) }); }
        else if (rows.Count == 0) empty = Locale.Get("resume.noMatch");
        var hidden = listing.Hidden > 0 && !all ? Locale.Get("resume.hiddenCount", new Dictionary<string, string> { ["count"] = Number(listing.Hidden) }) : null;
        return new { rows, hidden, empty, reason };
    }

    // ── The model ──

    /// <summary>The add-pane menu's entries for a workspace (the active one when none is named): NewSessionMenu's order and words (the three ways to add a workspace last), then Connect.</summary>
    private object WebAddMenu(string? workspaceId)
    {
        var state = service.Snapshot;
        var workspace = state.Workspaces.FirstOrDefault(w => w.Id == (workspaceId ?? state.ActiveWorkspaceId));
        var remote = workspace?.Remote is not null;
        var entries = RemoteWorkspaceFeatures.AddPaneEntries(workspace).Select(entry => entry == AddPaneMenu.Separator
            ? new { entry, label = "", provider = (string?)null, shortcut = (string?)null }
            : new
            {
                entry,
                label = AddPaneTitle(entry, remote) ?? entry switch
                {
                    AddPaneMenu.Files => Locale.Get("menu.showFiles"),
                    AddPaneMenu.OpenProject => Locale.Get(AddPaneMenu.OpenProjectKey),
                    AddPaneMenu.NewFolder => Locale.Get(AddPaneMenu.NewFolderKey),
                    AddPaneMenu.FromGit => Locale.Get(AddPaneMenu.FromGitKey),
                    _ => entry,
                },
                provider = AddPaneMenu.AgentProvider(entry),
                shortcut = entry == AddPaneMenu.OpenProject ? AddPaneMenu.OpenFolderShortcut : entry == AddPaneMenu.Files ? FilePaneKind.Shortcut : null,
            }).ToList();
        entries.Add(new { entry = "connect", label = Locale.Get("remote.menu.connect"), provider = (string?)null, shortcut = (string?)null });
        return new { workspaceId = workspace?.Id, entries, disabled = dialogOpen };
    }

    /// <summary>What a pane shows wherever it is listed: the sidebar's row, its tab, its dashboard row.</summary>
    private object WebPaneInfo(RunSession session)
    {
        var pending = PendingRequests(session.Id); var shown = StatusGlyph.DisplayStatus(session.Status, pending); var tone = StatusGlyph.Tone(shown);
        var attention = DashboardAttention(session.Id);
        var provider = ProviderMark.SidebarProvider(session);
        var card = WorkDashboard.MakeCard(session, attention);
        return new
        {
            id = session.Id, workspaceId = session.WorkspaceId, title = session.Title, help = PaneTitle.Help(session), kind = session.Kind, provider = session.Provider,
            beta = ProviderCatalog.ShowsBetaBadge(session), tone = ToneName(tone), word = StateLabel(shown), settled = tone is DesignTone.Done or DesignTone.Stop or DesignTone.Idle,
            agent = provider, agentLabel = provider is null ? null : ProviderMark.Label(provider), meta = provider is null ? SidebarKindWords(session.Kind) : SidebarMeta(session),
            wait = SidebarWaitText(attention), tabMark = WorkDashboard.IsCounted(session.Kind) && tone != DesignTone.Idle, waiting = attention.Total,
            dashMeta = DashboardMeta(card), activity = session.Status == "running" ? card.LastActivity : null, activityError = card.ActivityIsError,
            dashWait = attention.Total > 0 ? DashboardAttentionWord(attention) : null,
        };
    }

    private static object WebCounts(WorkDashboard.Badges b) => new { questions = b.Questions, permissions = b.Permissions, errors = b.Errors, running = b.Running, done = b.Done, stopped = b.Stopped, idle = b.Idle };

    private static object? WebGit(WorkspaceGitInfo? info) => info is null ? null : new
    {
        label = info.Label, dirty = info.IsDirty, ahead = info.Ahead ?? 0, behind = info.Behind ?? 0,
        help = "Git · " + info.Label + " · " + Locale.Get(info.IsDirty ? "git.dirty" : "git.clean") + "\n" + Locale.Get("git.localUpstream"),
    };

    /// <summary>A delegated child's card line (SidebarChildCard): its actions, the busy line, held notices, its last answer and the kept-link note.</summary>
    private object? WebChildCard(DelegationChildRow child, int heldNotices)
    {
        var busy = service.IsCardBusy(child.Id);
        var note = service.CardNote(child.Id) is { } answer && !busy ? DelegationText.Note(answer, child) : null;
        var kept = DelegationText.CloseNote(service.ChildClose(child.Id) is { IsCompletedSuccessfully: true } close ? close.Result : null);
        if (child.CardActions.Count == 0 && !busy && heldNotices == 0 && note is null && kept is null) return null;
        return new
        {
            busy, actions = busy ? [] : child.CardActions.Select(a => new { action = a.ToString().ToLowerInvariant(), label = DelegationText.Action(a), help = DelegationText.ActionHelp(a, child) }).ToList(),
            held = heldNotices > 0 ? Locale.Get("delegation.card.heldNotices", new Dictionary<string, string> { ["count"] = Number(heldNotices) }) : null,
            note = note?.Text, noteTone = note is { } line ? ToneName(line.Tone) : null, kept,
        };
    }

    private object WebChildItem(DelegationChildRow child, AppSnapshot state, bool parentClosed)
    {
        var open = state.Sessions.Any(s => s.Id == child.Id);
        var shownState = DelegationSidebar.ShownState(child.State, open && DashboardAttention(child.Id).Total > 0);
        return new
        {
            type = open ? "pane" : "closedChild", id = child.Id, depth = 1,
            title = child.Task ?? Locale.Get("delegation.child.untitled"), tone = ToneName(DelegationSidebar.Tone(child.State)),
            stateWord = DelegationText.State(shownState), stateTone = ToneName(DelegationSidebar.Tone(shownState)),
            card = WebChildCard(child, parentClosed ? child.HeldNotices : 0), menu = child.CardActions.Select(a => new { action = a.ToString().ToLowerInvariant(), label = DelegationText.Action(a) }).ToList(),
        };
    }

    /// <summary>One expanded workspace's list (RenderWorkspaceSidebar): each pane, its delegated children under it, then the closed parents' nodes.</summary>
    private List<object> WebSidebarItems(AppSnapshot state, string workspaceId)
    {
        var tree = service.SidebarTree(state, workspaceId); var items = new List<object>();
        foreach (var session in tree.Top)
        {
            items.Add(new { type = "pane", id = session.Id, depth = 0 });
            foreach (var child in tree.Children.GetValueOrDefault(session.Id) ?? []) items.Add(WebChildItem(child, state, false));
        }
        foreach (var node in tree.ParentClosed)
        {
            items.Add(new
            {
                type = "parentClosed", id = node.Id, depth = 0, branch = node.ParentBranch, count = node.Children.Count, help = Locale.Get("delegation.tree.parentClosedHelp"),
                label = Locale.Get("delegation.tree.parentClosed"), name = Locale.Get("delegation.tree.parentClosedAccessibility", new Dictionary<string, string> { ["count"] = Number(node.Children.Count) }),
            });
            foreach (var child in node.Children) items.Add(WebChildItem(child, state, true));
        }
        return items;
    }

    /// <summary>The usage popover's rows (RenderAccountUsageDetails), all of Core's AccountUsageStatus.</summary>
    private object? WebUsageDetails()
    {
        if (usage is null) return null;
        return new
        {
            title = AccountUsageStrings.Title, refreshing = usage.Refreshing || usageRefreshQueued, refreshLabel = AccountUsageStrings.RefreshAccessibilityLabel, refreshTooltip = AccountUsageStrings.RefreshTooltip,
            cards = usage.Cards().Select(card => new
            {
                provider = card.Provider, name = ProviderMark.Label(card.Provider), title = card.Title, beta = ProviderCatalog.IsBeta(card.Provider), account = card.Account,
                windows = card.Windows.Select(w => new { label = w.Label, used = w.Used, fraction = w.Fraction, reset = w.Reset, warning = w.Warning }).ToList(),
                detail = card.Detail, note = card.Note, checkedAt = card.CheckedAt,
                reset = card.Provider == "claude" && usage.ResetRows() is { Count: > 0 } rows
                    ? new { title = ClaudeResetEntitlements.Title, rows = rows.Select(r => new { program = r.Program, label = r.Label, text = r.Text }).ToList(), link = ClaudeResetEntitlements.LinkLabel, url = ClaudeResetEntitlements.LinkTarget }
                    : null,
            }).ToList(),
            direct = usage.Providers.Contains("claude") ? new { label = AccountUsageStrings.ToggleLabel, description = AccountUsageStrings.ToggleDescription, on = usage.DirectClaudeLookupEnabled } : null,
            shared = AccountUsageStrings.SharedLimitsNote,
        };
    }

    /// <summary>The dashboard (RenderDashboard): its counts, the usage bars, each workspace's head and rows in WorkDashboard's order.</summary>
    private object WebDashboard(AppSnapshot state)
    {
        var stats = WorkDashboard.Count(state.Sessions, DashboardAttention);
        var bars = usage?.LeadingBars() ?? [];
        return new
        {
            subtitle = Locale.Get("dashboard.subtitle", new Dictionary<string, string> { ["workspaces"] = Number(state.Workspaces.Count), ["panes"] = Number(state.Sessions.Count) }),
            running = stats.Running, waiting = stats.Waiting, done = stats.Done,
            usage = bars.Count == 0 ? null : bars.Select(card => new { provider = card.Provider, name = ProviderMark.Label(card.Provider), bars = card.Bars.Select(b => new { label = b.Label, percent = b.Percent, fraction = b.Fraction, warning = b.Warning }).ToList() }).ToList(),
            workspaces = state.Workspaces.Select(workspace => new
            {
                id = workspace.Id, name = workspace.Name, path = workspace.Path, local = workspace.Remote is null,
                git = WebGit(dashboardGit.TryGetValue(workspace.Id, out var cached) && cached.Path == LocalPath(workspace) ? cached.Info : null),
                rows = WorkDashboard.Ordered(state.Sessions.Where(s => s.WorkspaceId == workspace.Id).Select(s => WorkDashboard.MakeCard(s, DashboardAttention(s.Id)))).Select(card => card.Session.Id).ToList(),
            }).ToList(),
        };
    }

    /// <summary>The whole frame's model: what the WinUI frame draws, read from the same rules and the same window state.</summary>
    private object WebShellModel()
    {
        using var perf = PerfLog.Measure("web shell model");
        var state = service.Snapshot;
        var expanded = WorkspaceDisclosure.Expanded(state);
        var active = state.Workspaces.FirstOrDefault(w => w.Id == state.ActiveWorkspaceId);
        var activeGit = active is not null && gitWorkspaceKey == active.Id + "|" + LocalPath(active) ? gitInfo : null;
        object? layout = null;
        if (active is not null && EffectiveLayout(state, active.Id) is { } tree)
        {
            var mode = LayoutMode(state, active.Id);
            var shown = mode == "focus" ? PaneLayout.Groups(tree).FirstOrDefault(g => g.SessionIds.Contains(state.ActiveSessionId ?? "")) ?? PaneLayout.Groups(tree).First() : tree;
            layout = new { workspaceId = active.Id, mode, root = shown };
        }
        var connection = active is null ? null : RemoteConnection(active);
        var version = appUpdate?.State.Availability?.Manifest.Version;
        return new
        {
            version = AppVersionText, dialogOpen,
            dashboard = showsDashboard ? WebDashboard(state) : null,
            panes = state.Sessions.Select(WebPaneInfo).ToList(),
            entry = WebCounts(WorkDashboard.WorkspaceBadges(state.Sessions, DashboardAttention)),
            workspaces = state.Workspaces.Select(w => new
            {
                id = w.Id, name = w.Name, path = w.Path, remoteHost = w.Remote?.HostName, local = w.Remote is null,
                counts = WebCounts(WorkDashboard.WorkspaceBadges(state.Sessions.Where(s => s.WorkspaceId == w.Id), DashboardAttention)),
                expanded = expanded.Contains(w.Id), items = expanded.Contains(w.Id) ? WebSidebarItems(state, w.Id) : null,
            }).ToList(),
            header = active is null ? null : new
            {
                id = active.Id, name = active.Name, path = active.Path, git = WebGit(activeGit), remoteHost = active.Remote is null ? null : RemoteHostLabel(active),
                filesEnabled = RemoteWorkspaceFeatures.AllowsLocalOnly(active), filesHelp = active.Remote is null ? Locale.Get("menu.showFiles") + " (" + FilePaneKind.Shortcut + ")" : RemoteWindowsStrings.PaneUnavailable,
            },
            layout,
            error = string.IsNullOrEmpty(error.Text) ? null : error.Text,
            resource = resourceBanner.Visibility == Visibility.Visible && resourceWarning.Text.Length > 0 ? resourceWarning.Text : null,
            countdown = appUpdateCountdown?.Visibility == Visibility.Visible && appUpdateCountdownMessage is { } said ? said.Text : null,
            status = new
            {
                machine = status.Text, remote = active?.Remote is null ? (bool?)null : connection?.Status == RemoteConnectionStatus.Connected,
                panes = statusPanes.Text, running = statusRunning.Text,
                update = updateBadge?.Visibility == Visibility.Visible ? updateBadgeText.Text : null, updateHelp = Locale.Get("window.status.updateBadgeHelp"),
                staged = updateStaged.Visibility == Visibility.Visible ? updateStagedText.Text : null, stagedVersion = version,
                setup = setupBadge?.Visibility == Visibility.Visible,
                chips = usage is { Providers.Count: > 0 } ? usage.Chips().Select(chip => new { provider = chip.Provider, name = ProviderCatalog.Name(chip.Provider) + " " + chip.Text, words = UsageChipWords(chip).Select(w => new { text = w.Text, warning = w.Warning }).ToList() }).ToList() : null,
                chipsTitle = AccountUsageStrings.Title, chipsTooltip = AccountUsageStrings.ChipsTooltip,
            },
            usage = WebUsageDetails(),
            setupNeeded = SetupNeedsAttention,
            companion = WebCompanionControls(),
        };
    }

    // ── Smoke ──

    /// <summary>
    /// The frame's part of the web smoke: the sidebar lists the workspace and its panes; a click on a pane row and one on a tab
    /// make that pane the active one in the host's state and in the page (the tab switch timed in the page, from the click to the
    /// frame after the pane shows, once onto a pane not drawn before and once back onto one kept mounted); the add-pane menu adds
    /// a terminal tab; the columns preset draws two groups. Nothing is clicked that opens a dialog, the clipboard or another window.
    /// </summary>
    private async Task<bool> RunWebShellFrameSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, IReadOnlyList<string> ids, Dictionary<string, object?> result)
    {
        async Task<JsonElement> Eval(string script) => JsonDocument.Parse(await core.ExecuteScriptAsync(script)).RootElement.Clone();
        async Task<JsonElement> Probe(string script) => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(" + script + ")")) ?? "null").RootElement.Clone();
        async Task<bool> Until(Func<Task<bool>> done, double seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await done()) return true; await Task.Delay(50); }
            return false;
        }
        const string Frame = "({ workspaces: document.querySelectorAll('[data-testid=sidebar-workspace]').length, rows: [...document.querySelectorAll('[data-testid=sidebar-pane]')].map(e => e.dataset.sessionId), "
            + "tabs: [...document.querySelectorAll('[data-testid=tab]')].map(e => e.dataset.tabId), groups: document.querySelectorAll('[data-testid=tab-group]').length, splits: document.querySelectorAll('[data-testid=split]').length, "
            + "active: document.querySelector('.pane-slot.active:not(.hidden)')?.dataset.paneId ?? null, statusBar: !!document.querySelector('[data-testid=status-bar]'), header: !!document.querySelector('[data-testid=workspace-header]') })";
        static List<string> Ids(JsonElement page, string name) => page.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
        static int Count(JsonElement page, string name) => page.TryGetProperty(name, out var n) && n.TryGetInt32(out var value) ? value : 0;
        var checks = new Dictionary<string, object?>(); result["frame"] = checks;
        // The sidebar: the workspace and every pane; the header and the status bar.
        var listed = await Until(async () => { var page = await Probe(Frame); return Count(page, "workspaces") >= 1 && ids.All(Ids(page, "rows").Contains) && page.GetProperty("statusBar").GetBoolean(); });
        checks["sidebar"] = (await Probe(Frame)).Clone();
        // A pane row: the shell pane becomes the active one.
        await Eval($"document.querySelector('.sidebar [data-session-id=\"{ids[2]}\"]')?.click()");
        var rowPicked = await Until(async () => service.Snapshot.ActiveSessionId == ids[2] && (await Probe(Frame)).Text("active") == ids[2]);
        checks["rowClick"] = rowPicked;
        // A tab, timed: the click to the frame after the chosen pane shows (window.__tabSwitch, in milliseconds).
        string TimeTab(string id) => "(() => { window.__tabSwitch = null; const tab = document.querySelector('[data-tab-id=\"" + id + "\"] .tab-handle'); if (!tab) return false; const t0 = performance.now(); tab.click(); "
            + "const shown = () => { const slot = document.querySelector('.pane-slot[data-pane-id=\"" + id + "\"]'); return !!slot && !slot.classList.contains('hidden') && !!slot.querySelector('.pane'); }; "
            + "const sync = shown() ? performance.now() - t0 : null; const check = () => { if (shown()) requestAnimationFrame(() => { window.__tabSwitch = { sync, frame: performance.now() - t0 }; }); else requestAnimationFrame(check); }; check(); return true; })()";
        async Task<JsonElement?> Timed(string id)
        {
            if ((await Eval(TimeTab(id))).ValueKind != JsonValueKind.True) return null;
            JsonElement timing = default;
            await Until(async () => (timing = await Probe("window.__tabSwitch")).ValueKind == JsonValueKind.Object, 5);
            return timing.ValueKind == JsonValueKind.Object ? timing : null;
        }
        var first = await Timed(ids[1]);
        var tabPicked = await Until(async () => service.Snapshot.ActiveSessionId == ids[1] && (await Probe(Frame)).Text("active") == ids[1]);
        var back = await Timed(ids[2]);
        var backPicked = await Until(async () => service.Snapshot.ActiveSessionId == ids[2] && (await Probe(Frame)).Text("active") == ids[2]);
        checks["tabClick"] = tabPicked && backPicked; checks["tabSwitchFirstMs"] = first; checks["tabSwitchMountedMs"] = back;
        result["tabSwitchMs"] = back is { } mounted && mounted.TryGetProperty("frame", out var frame) ? Math.Round(frame.GetDouble(), 1) : null;
        // The add-pane menu: its terminal entry adds a tab to the active group.
        var before = service.Snapshot.Sessions.Count; var tabsBefore = Ids(await Probe(Frame), "tabs").Count;
        await Eval("document.querySelector('[data-testid=sidebar-add]')?.click()");
        var menuShown = await Until(async () => (await Eval("!!document.querySelector('[data-testid=\"add-entry-shell\"]')")).ValueKind == JsonValueKind.True, 10);
        await Eval("document.querySelector('[data-testid=\"add-entry-shell\"]')?.click()");
        var added = menuShown && await Until(async () => service.Snapshot.Sessions.Count == before + 1 && Ids(await Probe(Frame), "tabs").Count == tabsBefore + 1);
        checks["addMenu"] = menuShown; checks["addedTab"] = added;
        // The columns preset: two groups side by side.
        // The header's layout button opens the presets' menu (the Mac's View menu).
        await Eval("document.querySelector('[data-testid=layout-select]')?.click()");
        await Until(async () => (await Eval("!!document.querySelector('[data-testid=layout-preset-columns]')")).ValueKind == JsonValueKind.True, 10);
        await Eval("document.querySelector('[data-testid=layout-preset-columns]')?.click()");
        var split = await Until(async () => { var page = await Probe(Frame); return Count(page, "groups") >= 2 && Count(page, "splits") >= 1; });
        checks["split"] = split; checks["afterSplit"] = (await Probe(Frame)).Clone();
        var passed = listed && rowPicked && tabPicked && backPicked && added && split;
        checks["passed"] = passed;
        return passed;
    }
}
