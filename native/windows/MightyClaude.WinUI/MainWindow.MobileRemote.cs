using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private MobileRelayHost? mobileHost;
    private MobileDesktopRouter? mobileRouter;
    private readonly DispatcherTimer mobileNotify = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long mobileNotified;
    private bool mobileNotifying;
    private async Task InitializeMobileRemote()
    {
        if (options.SmokeTest || mobileHost is not null) return;
        var directory = Path.Combine(StateDirectory, "mobile-remote");
        mobileRouter = new(service, directory, Environment.MachineName, MobileAction, MobileExtras) { AppVersion = AppVersionText, ImagePreview = MakeMobileImagePreview, QueueCount = id => views.TryGetValue(id, out var pane) ? pane.MobileQueuedCount : 0 };
        mobileHost = new(directory, Environment.MachineName, MobileDesktopRouter.Capabilities, MobileRoute) { AppVersion = AppVersionText };
        mobileRouter.HostId = mobileHost.HostId;
        await InitializeScreenShare(directory);
        mobileNotify.Tick += async (_, _) =>
        {
            if (closing || mobileNotifying || mobileRouter is null || mobileHost is null || mobileRouter.Revision == mobileNotified) return;
            mobileNotifying = true;
            try { mobileNotified = mobileRouter.Revision; await mobileHost.NotifyAsync("state", mobileNotified); foreach (var pane in service.Snapshot.Sessions.Where(s => s.Kind == "claude")) await mobileHost.NotifyAsync("session:" + pane.Id, mobileNotified); }
            finally { mobileNotifying = false; }
        };
        mobileNotify.Start(); await mobileHost.ApplyAsync(service.Snapshot.MobileRemote);
    }
    private Task<MobileReply> MobileRoute(string method, string path, JsonElement? body, string device, CancellationToken token)
    {
        var completion = new TaskCompletionSource<MobileReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => completion.TrySetCanceled(token));
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { token.ThrowIfCancellationRequested(); if (closing || mobileRouter is null) throw new OperationCanceledException(); completion.TrySetResult(await mobileRouter.RouteAsync(method, path, body, device, token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch { completion.TrySetResult(new(500, new { protocol = 1, error = "Desktop request failed." })); }
            finally { registration.Dispose(); }
        })) { registration.Dispose(); completion.TrySetCanceled(); }
        return completion.Task;
    }
    private async Task ShutdownMobileRemote()
    {
        mobileNotify.Stop(); await ShutdownScreenShare(); if (mobileHost is not null) await mobileHost.DisposeAsync(); mobileHost = null; mobileRouter?.Dispose(); mobileRouter = null;
    }
    private StackPanel BuildMobileRemoteSection()
    {
        var panel = new StackPanel { Spacing = 10 }; var enabled = new ToggleSwitch { Header = Locale.Get("windows.mobile.allow"), IsOn = service.Snapshot.MobileRemote.Enabled };
        var relay = new TextBox { Header = Locale.Get("settings.mobileRemote.relayLabel"), Text = service.Snapshot.MobileRemote.RelayURL, PlaceholderText = MobileRelayHost.DefaultRelay, MaxLength = 256 };
        var legacy = new ToggleSwitch { Header = Locale.Get("settings.mobileRemote.legacyAppsToggle"), IsOn = service.Snapshot.MobileRemote.AllowLegacyPhones };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; var identity = new StackPanel { Spacing = 8 }; var devices = new StackPanel { Spacing = 6 };
        var qr = new Image { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Left }; AutomationProperties.SetName(qr, Locale.Get("settings.mobileRemote.qrAccessibility"));
        panel.Children.Add(new TextBlock { Text = Locale.Get("windows.mobile.relayDescription"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(enabled); panel.Children.Add(relay); panel.Children.Add(legacy); panel.Children.Add(status);
        identity.Children.Add(new TextBlock { Text = Locale.Get("settings.mobileRemote.scanInstruction"), TextWrapping = TextWrapping.Wrap }); identity.Children.Add(qr);
        identity.Children.Add(Button(Locale.Get("settings.mobileRemote.copyLinkButton"), () => { if (mobileHost is not null && service.Snapshot.MobileRemote.Enabled) { var package = new DataPackage(); package.SetText(mobileHost.PairingUrl); Clipboard.SetContent(package); } return Task.CompletedTask; }));
        identity.Children.Add(Button(Locale.Get("settings.mobileRemote.regenerateKeyButton"), () => Revoke(null)));
        identity.Children.Add(new TextBlock { Text = Locale.Get("windows.mobile.rotationDescription"), TextWrapping = TextWrapping.Wrap });
        identity.Children.Add(new TextBlock { Text = Locale.Get("settings.mobileRemote.connectedDevicesTitle") }); identity.Children.Add(devices); panel.Children.Add(identity);
        var apply = Button(Locale.Get("settings.mobileRemote.applyButton"), () => Act(async () =>
        {
            if (options.SmokeTest) return;
            if (relay.Text.Trim().Length > 0 && MobileRelayHost.NormalizeRelay(relay.Text.Trim()) is null) throw new ArgumentException(Locale.Get("windows.mobile.invalidRelay"));
            await InitializeMobileRemote(); var settings = new MobileRemoteSettings(enabled.IsOn, relay.Text.Trim(), legacy.IsOn);
            await service.UpdateAsync(s => s with { MobileRemote = settings }); if (!settings.Enabled && screenHub is not null) await screenHub.KillAllAsync("revoked"); await mobileHost!.ApplyAsync(settings); await Refresh();
        })); panel.Children.Insert(4, apply);
        async Task Revoke(string? id)
        {
            if (mobileHost is null || options.SmokeTest) return;
            var dialog = new ContentDialog { XamlRoot = SettingsXamlRoot, Title = Locale.Get(id is null ? "settings.mobileRemote.regenerateKeyButton" : "settings.mobileRemote.revokeDialogTitle"), Content = Locale.Get("windows.mobile.rotationConfirm"), PrimaryButtonText = Locale.Get("settings.mobileRemote.revokeConfirmButton"), CloseButtonText = Locale.Get("settings.mobileRemote.cancelButton"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (screenHub is not null) await screenHub.ResetAsync("rekey-pairing"); await mobileHost.RotateKeyAsync(); mobileRouter?.ResetUploads(); await Refresh();
        }
        async Task Refresh()
        {
            identity.Visibility = service.Snapshot.MobileRemote.Enabled && mobileHost is not null ? Visibility.Visible : Visibility.Collapsed;
            var connected = mobileHost?.Status; status.Text = !service.Snapshot.MobileRemote.Enabled ? Locale.Get("settings.mobileRemote.statusOff") : connected?.Connected != true ? Locale.Get("settings.mobileRemote.statusConnecting") : Locale.Get("settings.mobileRemote.statusConnectedTemplate", new Dictionary<string, string> { ["count"] = connected.Connections.ToString() });
            if (identity.Visibility != Visibility.Visible) { qr.Source = null; return; }
            var link = mobileHost!.PairingUrl; var png = PngByteQRCodeHelper.GetQRCode(link, QRCodeGenerator.ECCLevel.M, 6);
            using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); } stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); if (mobileHost?.PairingUrl != link) return; qr.Source = bitmap;
            devices.Children.Clear(); foreach (var device in mobileHost.Devices)
            {
                var row = new StackPanel { Spacing = 4 }; row.Children.Add(new TextBlock { Text = device.Name + (device.Legacy ? " · Legacy" : ""), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = Locale.Get("settings.mobileRemote.seenTemplate", new Dictionary<string, string> { ["first"] = device.FirstSeen, ["last"] = device.LastSeen }), FontSize = 11, Opacity = .65 });
                row.Children.Add(Button(Locale.Get("settings.mobileRemote.revokeRowButton"), () => Revoke(device.Id))); devices.Children.Add(row);
            }
        }
        void Updated(MobileRelayStatus _) => DispatcherQueue.TryEnqueue(async () => { if (!closing && panel.IsLoaded) await Act(Refresh); });
        panel.Loaded += async (_, _) => { if (options.SmokeTest) { enabled.IsEnabled = relay.IsEnabled = legacy.IsEnabled = apply.IsEnabled = false; identity.Visibility = Visibility.Collapsed; return; } await Act(async () => { await InitializeMobileRemote(); mobileHost!.StatusChanged += Updated; await Refresh(); }); };
        panel.Unloaded += (_, _) => { if (mobileHost is not null) mobileHost.StatusChanged -= Updated; qr.Source = null; };
        panel.Children.Add(BuildScreenShareSection());
        return panel;
    }
    private PaneView MobilePane(string id)
    {
        if (!service.Snapshot.Sessions.Any(p => p.Id == id && p.Kind == "claude")) throw new MobileRequestException(404, "Session not found.");
        if (!views.TryGetValue(id, out var pane)) { pane = new(this, id); views[id] = pane; pane.InitRefresher(); }
        return pane;
    }
    private MobilePaneExtras MobileExtras(RunSession pane)
        => pane.Kind == "claude" ? MobilePane(pane.Id).MobileSnapshot() : new([]);
    private async Task<object?> MobileAction(MobileDesktopAction request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (closing) throw new OperationCanceledException();
        if (request.Action == "create")
        {
            var created = SessionTemplate.Inherit(new RunSession { WorkspaceId = request.SessionId, Provider = request.Body.Text("provider") ?? "claude", Kind = "claude", Title = ProviderCatalog.Name(request.Body.Text("provider") ?? "claude") }, service.Snapshot.Sessions);
            await service.UpdateAsync(s => s.Workspaces.Any(w => w.Id == request.SessionId) ? s with { Sessions = s.Sessions.Append(created).ToList() } : throw new MobileRequestException(404, "Workspace not found.")); Render(); return created.Id;
        }
        var pane = MobilePane(request.SessionId); var body = request.Body;
        switch (request.Action)
        {
            case "submit": return await pane.MobileSubmit(body.Text("text") ?? "", body.Text("mode"), request.Attachments ?? [], token);
            case "stop": var running = service.IsSessionRunning(request.SessionId); pane.MobileCancelQueue(); await service.StopAsync(request.SessionId); return running;
            case "queue-remove": pane.MobileQueueRemove(body.Text("id") ?? ""); return null;
            case "queue-next": return await pane.MobileRunNext(token);
            case "rename": if (body.Text("titleMode") == "auto") await service.SetSessionAutoTitleAsync(request.SessionId); else await service.RenameSessionAsync(request.SessionId, body.Text("title")!.Trim()); Render(); return null;
            case "close": pane.MobileCancelQueue(); await service.StopAsync(request.SessionId); token.ThrowIfCancellationRequested(); await service.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != request.SessionId).ToList() }); Render(); return null;
            case "settings": await pane.MobileSettings(body, token); return null;
            case "guided": return await pane.MobileGuided(body, token);
            case "command":
                if (body.Text("action") == "help") return SlashCommandCatalog.HelpText(pane.MobileSession.Provider);
                if (body.Text("action") == "usage") return pane.MobileUsage();
                if (!await pane.ResetConversationAsync(token)) throw new MobileRequestException(409, "Stop the session before clearing it.");
                return null;
            default: throw new MobileRequestException(404, "Unknown desktop action.");
        }
    }
    private sealed partial class PaneView
    {
        internal string MobileUsage()
        {
            var pane = Session; var usage = pane.SessionUsage;
            var lines = new List<string> { Locale.Get("composer.label.model") + " · " + (usage?.Model ?? pane.Model) };
            if (usage?.ContextUsedTokens is { } context) lines.Add(Locale.Get("composer.sessionInfo.context") + $" · {context:N0} / {usage.ContextWindowTokens:N0}");
            if (usage?.TotalTokens is { } tokens) lines.Add("Tokens · " + tokens.ToString("N0"));
            if (usage?.CostUSD is { } cost) lines.Add(Locale.Get("composer.sessionInfo.cost") + $" · ${cost:0.####}");
            if (pane.RunTiming is { } timing) lines.Add(Locale.Get("composer.sessionInfo.elapsed") + " · " + timing.Label());
            return string.Join("\n", lines);
        }
        internal int MobileQueuedCount => queuedInputs.Items.Count;
        internal RunSession MobileSession => Session;
        internal void MobileQueueRemove(string itemId) { if (!queuedInputs.Remove(itemId)) throw new MobileRequestException(404, "Queued request not found."); RenderQueuedInputs(); RefreshComposerState(); }
        private bool MobileBusy => starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id);
        internal void MobileCancelQueue() { composerSubmissionVersion++; queueDrainTimer.Stop(); queuedInputs.Clear(); RenderQueuedInputs(); RefreshComposerState(); }
        internal async Task<string> MobileSubmit(string text, string? mode, IReadOnlyList<RunAttachment> files, CancellationToken token, bool prepared = false, string? queueId = null)
        {
            token.ThrowIfCancellationRequested(); var pane = Session;
            var request = new StartRunRequest(id, pane.WorkspaceId, pane.Kind, text, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files).Validate();
            if (MobileBusy)
            {
                if (mode != "queue" && files.Count == 0 && pane.Provider == "claude" && await owner.service.TrySteerAsync(id, text)) return "steered";
                token.ThrowIfCancellationRequested(); queuedInputs.Add(text, files); RenderQueuedInputs(); RefreshComposerState(); if (!MobileBusy) queueDrainTimer.Start(); return "queued";
            }
            var submission = ++composerSubmissionVersion; starting = true; RefreshComposerState();
            try
            {
                var submitted = prepared ? text : await PrepareStyleSubmission(text, files.Count > 0); request = await PrepareStyleRunRequest(request with { Input = submitted });
                token.ThrowIfCancellationRequested(); if (!QueuePaneAlive || submission != composerSubmissionVersion || queueId is not null && !queuedInputs.Items.Any(q => q.Id == queueId)) throw new OperationCanceledException(); await owner.StartFromComposer(request); return "started";
            }
            finally { starting = false; if (QueuePaneAlive) Refresh(); }
        }
        internal async Task<object?> MobileRunNext(CancellationToken token)
        {
            if (MobileBusy) throw new MobileRequestException(409, "Session is still running.");
            var next = queuedInputs.Items.FirstOrDefault() ?? throw new MobileRequestException(409, "The queue is empty.");
            await MobileSubmit(next.Text, "queue", next.Attachments, token, queueId: next.Id); queuedInputs.Remove(next.Id); RenderQueuedInputs(); RefreshComposerState(); return null;
        }
        internal MobilePaneExtras MobileSnapshot()
        {
            var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var registered = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot);
            var registry = styleRegistry ??= StyleRegistry.Load(owner.StateDirectory, Workspace.Path); var styles = (registry?.Styles ?? []).Where(s => s.Runnable).Select(s => new { id = s.Id, label = s.Manifest.Name, source = s.Source }).ToArray();
            var settings = new { editable = !MobileBusy, model = pane.Model, permissionMode = pane.Settings.PermissionMode, effort = pane.Settings.Effort, agentViewMode = pane.AgentViewMode == "mighty" ? "mighty" : "plain", mightyStyle = pane.MightyStyle is "ouroboros" or "paperthin" ? pane.MightyStyle : "cli", styleId = pane.MightyStyle ?? "cli", options = new { models = ModelLabel.PickerOptions(pane, catalog).Select(m => new { id = m.Value, label = m.DisplayName }), permissionModes = (Capabilities.PermissionModes ?? []).Select(m => new { id = m, label = PermissionLabel(pane.Provider, m) }), efforts = new[] { "default" }.Concat(ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registered)).Select(m => new { id = m, label = m }), mightyStyles = new[] { new { id = "cli", label = "CLI" } }.Concat(styles.Where(s => s.id is "ouroboros" or "paperthin").Select(s => new { s.id, s.label })), styles } };
            var mighty = new { style = pane.MightyStyle is "ouroboros" or "paperthin" ? pane.MightyStyle : "cli", styleId = pane.MightyStyle ?? "cli", runs = (pane.GraphRuns ?? []).TakeLast(8).Select(run => new { run.Id, run.Input, run.Status, result = run.FinalOutput, blocks = new[] { new { id = run.Id + ":main", kind = "main", title = "Main", status = run.Status, summary = run.Input, output = string.Join("\n", run.RootEntries.Where(e => e.Kind is "assistant" or "output").Select(e => e.Text)), nodeModelLabel = run.NodeModelLabel } }.Concat(run.Agents.Select(a => new { id = a.Id, kind = a.Kind ?? "agent", title = a.Title, status = a.Status, summary = a.Input, output = string.Join("\n", a.Entries.Where(e => e.Kind is "assistant" or "output").Select(e => e.Text)), nodeModelLabel = (string?)null })) }), panel = MobileStylePanel() };
            var statusLine = Refresher?.Result is { } statusResult ? new { lines = statusResult.Lines.Select(line => line.Select(s => new { s.Text, fg = Terminal(s.Foreground) is { } color ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : null, bold = s.Bold })) } : null;
            return new(queuedInputs.Items.Select(q => new MobileQueuedItem(q.Id, q.Text)).ToArray(), settings, mighty, statusLine);
        }
        private object? MobileStylePanel()
        {
            if (activeStyle is not { } style) return null; var phase = GuidedPhase(style); var job = style.Evaluator.JobOpen(Session);
            var group = style.Manifest.Groups.FirstOrDefault(g=>g.Id==styleGroup) ?? style.Evaluator.InitialGroup(styleCapabilities); var next = style.Evaluator.VisibleActions(phase, group, MobileBusy, job).Select(a => a.Id).ToArray();
            var phases = style.Manifest.Phases.OrderBy(p => p.Order).ToArray();
            return new {
                style = new { id = style.Id, name = style.Manifest.Name, source = style.Source, icon = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"icon"), tint = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"tint") },
                phase = phase is null ? null : new { phase.Id, phase.Title, index = Array.FindIndex(phases, p => p.Id == phase.Id), count = phases.Length },
                groups = style.Manifest.Groups.Select(g => new { g.Id, g.Title, g.Axis, g.Question, selected = g.Id == group?.Id, g.Actions }),
                actions = style.Manifest.Actions.Select(a => new { a.Id, a.Title, a.Help, a.Icon, a.Glyph, a.Scope, a.TakesText, a.RequiresText, flags = a.Flags ?? [], prominent = next.FirstOrDefault() == a.Id }),
                next, recommended = style.Evaluator.RecommendedAction(styleCapabilities), attachments = styleCapabilityFiles.Select(file => new { id = StyleText.Safe(Path.GetFileName(file.Path),120), title = file.Title, detail = file.Detail, readOnly = file.ReadOnly }),
                setup = new { ready = stylePrerequisites?.Ready ?? false, missing = stylePrerequisites?.Missing ?? [], hint = stylePrerequisites?.Hint, installCommand = stylePrerequisites?.InstallCommand },
                guidance = style.Evaluator.Guidance(phase, MobileBusy, job), presentation = new { headerTitle = style.Manifest.Name + (phase is null ? "" : " · " + phase.Title), source = style.Source, icon = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"icon"), tint = StyleManifest.Text(style.Manifest.Root.GetProperty("presentation"),"tint") }, widgets = styleReading?.Widgets
            };
        }
        internal async Task MobileSettings(JsonElement value, CancellationToken token)
        {
            if (MobileBusy) throw new MobileRequestException(409, "Stop the session before changing settings.");
            if (!value.EnumerateObject().Any() || value.EnumerateObject().Any(p => p.Name is not ("model" or "permissionMode" or "effort" or "agentViewMode" or "mightyStyle" or "styleId") || p.Value.ValueKind != JsonValueKind.String)) throw new MobileRequestException(400, "Unknown setting.");
            var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider); var model = value.Text("model") ?? pane.Model; var mode = value.Text("permissionMode") ?? pane.Settings.PermissionMode; var effort = value.Text("effort") ?? pane.Settings.Effort;
            if (!ModelLabel.PickerOptions(pane, catalog).Any(m => m.Value == model) || !(Capabilities.PermissionModes ?? []).Contains(mode) || effort != "default" && !ProviderCatalog.Efforts(pane.Provider, model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot)).Contains(effort)) throw new MobileRequestException(400, "Unsupported model, permission, or effort.");
            var view = value.Text("agentViewMode") ?? (pane.AgentViewMode == "mighty" ? "mighty" : "plain"); if (view is not ("plain" or "mighty")) throw new MobileRequestException(400, "Unsupported view.");
            var styleId = value.Text("styleId") ?? value.Text("mightyStyle") ?? pane.MightyStyle ?? "cli"; RegisteredStyle? selected = null;
            if (styleId != "cli")
            {
                if (pane.Provider != "claude") throw new MobileRequestException(400, "Styles require Claude.");
                var registry = await Task.Run(() => StyleRegistry.Load(owner.StateDirectory, Workspace.Path), token); selected = registry.Styles.FirstOrDefault(s => s.Id == styleId && s.Runnable) ?? throw new MobileRequestException(400, "Unknown or unapproved style.");
                if (!StyleRegistry.Unchanged(selected)) throw new MobileRequestException(409, "Style changed.");
            }
            token.ThrowIfCancellationRequested(); if (MobileBusy || Session.Provider != pane.Provider || Session.Model != pane.Model || Session.MightyStyle != pane.MightyStyle || Session.Settings != pane.Settings) throw new MobileRequestException(409, "The pane changed.");
            await Change(p => p with { Model = model, Settings = p.Settings with { PermissionMode = mode, Effort = effort, NetworkAccess = mode is ("acceptEdits" or "onRequest") && p.Settings.NetworkAccess }, AgentViewMode = view == "mighty" ? "mighty" : "default", MightyStyle = selected?.Id, MightyStyleHash = selected?.Hash, MightyStyleSince = p.MightyStyle == selected?.Id ? p.MightyStyleSince : null }); loadedStyleKey = null; await LoadStyles(); Refresh();
        }
        internal async Task<string> MobileGuided(JsonElement body, CancellationToken token)
        {
            var pane = Session; var chosen = body.Text("styleId") ?? body.Text("style"); var actionId = body.Text("actionId") ?? body.Text("skill"); var text = body.Text("text") ?? "";
            if (MobileBusy || pane.Provider != "claude" || pane.AgentViewMode != "mighty" || chosen != pane.MightyStyle || text.Length > 32768) throw new MobileRequestException(409, "The guided pane is unavailable.");
            var registry = await Task.Run(() => StyleRegistry.Load(owner.StateDirectory, Workspace.Path), token); var style = registry.Runnable(chosen, pane.MightyStyleHash) ?? throw new MobileRequestException(400, "Unknown or unapproved style.");
            var action = style.Manifest.Actions.FirstOrDefault(a => a.Id == actionId) ?? throw new MobileRequestException(400, "Unknown guided action.");
            if (action.RequiresText && string.IsNullOrWhiteSpace(text)) throw new MobileRequestException(400, "This action needs text.");
            if (!StylePrerequisites.Read(style.Manifest, Workspace.Path).Ready) throw new MobileRequestException(409, "Install the style prerequisites on the desktop.");
            token.ThrowIfCancellationRequested(); if (MobileBusy || Session.MightyStyle != chosen || !StyleRegistry.Unchanged(style)) throw new MobileRequestException(409, "Style changed.");
            return await MobileSubmit(action.Prompt(text), "queue", [], token, true);
        }
    }
}
