using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, object?>> RunSmallParitySmoke(Workspace workspace)
    {
        var saved = service.Snapshot;
        var id = saved.Sessions.First(p => p.Kind == "claude" && p.WorkspaceId == workspace.Id).Id;
        try
        {
            await SelectLayoutSession(id); Render();
            var pane = views[id]; await WaitUI(() => pane.Container.IsLoaded);
            return await pane.SmokeSmallParityAsync();
        }
        finally { await service.UpdateAsync(_ => saved); Render(); }
    }
    private sealed partial class PaneView
    {
        internal async Task<Dictionary<string, object?>> SmokeSmallParityAsync()
        {
            var checks = new Dictionary<string, object?>();
            var usage = new SessionUsage { Provider = Session.Provider, Source = "synthetic-smoke", UpdatedAt = Wire.Now(), TokenScope = "session", TotalTokens = 12345, ProviderSessionId = "smoke-cli-session", ContextUsedTokens = 123, ContextWindowTokens = 1000 };
            await Change(p => p with { SessionUsage = usage }); Refresh();
            await ShowContext();
            try
            {
                Require(sessionInfoOpen && sessionInfoRows["total"].Value.Text == 12345.ToString("N0") && sessionInfoRows["cli-identity"].Value.Text == "smoke-cli-session", "Session info omitted total tokens or CLI identity.");
                Require(sessionInfoRows["path"].Value.Text == Workspace.Path && context.Content is Grid, "Session info path or circular context indicator is missing.");
                var row = sessionInfoRows["total"].Value;
                await Change(p => p with { SessionUsage = usage with { TotalTokens = 23456, ContextWindowTokens = null } }); RefreshSessionInfo();
                Require(ReferenceEquals(row, sessionInfoRows["total"].Value) && row.Text == 23456.ToString("N0") && sessionInfoRows["context"].Value.Text.Contains("123", StringComparison.Ordinal), "Live session info must update in place and keep partial context counts.");
                checks["liveSessionPopover"] = true; checks["contextRingAndIdentifiers"] = true;
            }
            finally { sessionInfoFlyout?.Hide(); }
            Require(attach.ContextFlyout is MenuFlyout { Items.Count: 4 }, "Attachment menu must include file picker, paste and remove-all.");
            checks["attachmentMenu"] = true;
            var pixels = await SmokePng(10, 8); var originalPath = Path.Combine(Workspace.Path, "smoke-image-external-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await File.WriteAllBytesAsync(originalPath, pixels);
                var action = new TranscriptAction("image", originalPath);
                var revealed = ImageExternalPath(action, Workspace.Path, pixels, "image/png", revealOriginal: true);
                var opened = ImageExternalPath(action, Workspace.Path, pixels, "image/png", revealOriginal: false);
                Require(WorkspaceFiles.RealPath(originalPath) == revealed && WorkspaceFiles.Contains(opened, WorkspaceFiles.RealPath(owner.service.Images.Directory)!) && AgentImageSupport.Sha256(await File.ReadAllBytesAsync(opened)) == AgentImageSupport.Sha256(pixels),
                    "Image reveal must use the bounded original while external viewing uses the verified cache snapshot.");
                checks["imageExternalVerifiedSnapshot"] = true;
            }
            finally { File.Delete(originalPath); }
            var registry = new AgentIOBindings();
            var binding = registry.Bind(id, Workspace, "codex", "smoke", Path.Combine(owner.StateDirectory, "fixture.exe"));
            var one = owner.agentUrlPrompts.RequestAsync(binding, new Uri("https://example.com/first"), CancellationToken.None);
            var two = owner.agentUrlPrompts.RequestAsync(binding, new Uri("https://example.com/second"), CancellationToken.None);
            try
            {
                RenderAgentWebPrompts();
                Require(agentWebPromptCards.Count == 2 && agentWebPromptScroll.MaxHeight == 320 && agentWebPromptScroll.Visibility == Visibility.Visible && !owner.dialogOpen,
                    "Agent URL choices must be independent bounded cards without a modal dialog.");
                await SettleDesktopCapture(owner.root); await CaptureElement(Container, Path.Combine(owner.options.ProfileDirectory!, "smoke-composer-web-open.png"));
                var requests = owner.agentUrlPrompts.ForPane(id);
                owner.agentUrlPrompts.Choose(requests[1].Id, "external", true);
                Require(await two == new AgentWebOpenAnswer("external", true) && !one.IsCompleted, "Second card must answer independently.");
                registry.Revoke(id);
                try { await one; throw new InvalidOperationException("Revoked URL card completed instead of cancelling."); } catch (OperationCanceledException) { }
                RenderAgentWebPrompts(); Require(agentWebPromptCards.Count == 0 && agentWebPromptScroll.Visibility == Visibility.Collapsed, "Revocation must remove pending URL cards.");
                checks["agentUrlCardsNonmodalAndBounded"] = true;
            }
            finally { registry.Clear(); RenderAgentWebPrompts(); }
            await SmokeConversationReset(); checks["conversationResetPreservesDraftAndHistory"] = true;
            return checks;
        }
        /// <summary>
        /// Pictures of the composer's states in the theme just rendered, written beside the smoke's other screenshots as
        /// <c>smoke-composer-{state}-{theme}.png</c> for the comparison with the Mac's (docs/design-system). Everything it
        /// sets up — the draft, the attachments, the run state, the queue, the cards and the width — is put back.
        /// </summary>
        internal async Task CaptureComposerSmoke()
        {
            var o = owner; var theme = o.SmokeTheme; var directory = o.options.ProfileDirectory!;
            var original = Session; var draft = input.Text; var attachments = pendingAttachments.ToArray();
            async Task Shot(FrameworkElement element, string name)
            {
                o.root.UpdateLayout(); await Task.Delay(150); o.root.UpdateLayout();
                await CaptureElement(element, Path.Combine(directory, $"smoke-composer-{name}-{theme}.png"));
            }
            IEnumerable<FrameworkElement> Open<T>() where T : FrameworkElement => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(o.root.XamlRoot).Select(p => p.Child).OfType<T>();
            // A popover is drawn in its own layer: its presenter is the picture. A popup that closes another still settles
            // for a moment, so each one waits for the layer to be empty before it opens and after it closes.
            async Task Popover<T>(Func<Task> open, Action close, string name) where T : FrameworkElement
            {
                await WaitUI(() => !Open<FrameworkElement>().Any(), () => $"composer capture ({theme}): a popup was still open before the {name}");
                await Task.Delay(200);
                await open();
                await WaitUI(() => Open<T>().Any(p => p.IsLoaded && p.ActualHeight > 0), () => $"composer capture ({theme}): the {name} never opened");
                o.root.UpdateLayout(); await Task.Delay(250);
                var shown = Open<T>().FirstOrDefault(p => p.IsLoaded && p.ActualHeight > 0) ?? throw new InvalidOperationException($"composer capture ({theme}): the {name} closed by itself before its picture");
                // A FlyoutPresenter itself renders as nothing to a bitmap; the root of its template, which is all of it, does.
                var surface = shown is FlyoutPresenter && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(shown) > 0 && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(shown, 0) is FrameworkElement frame ? frame : shown;
                await CaptureElement(surface, Path.Combine(directory, $"smoke-composer-{name}-{theme}.png"));
                close(); await WaitUI(() => !Open<FrameworkElement>().Any(), () => $"composer capture ({theme}): the {name} never closed");
            }
            // A menu is the system's own: its acrylic backdrop does not render to a bitmap, so only the light pass, where the
            // words stay readable without it, takes its picture.
            Task Menu(Button pill, string name) => theme == "light" && pill.Flyout is MenuFlyout menu && pill.Visibility == Visibility.Visible
                ? Popover<MenuFlyoutPresenter>(() => { menu.ShowAt(pill); return Task.CompletedTask; }, menu.Hide, name) : Task.CompletedTask;
            async Task Draft(string text) { updating = true; input.Text = text; updating = false; await Change(p => p with { Draft = text }); }
            var sample = Locale.Get("composer.placeholder.idle").TrimEnd('…');
            try
            {
                // Idle: a draft and an attachment, every pill with its words, the run circle.
                await Draft(sample); await Change(p => p with { Status = "completed", ResumeId = "smoke-composer-resume" }); Refresh();
                await Shot(Container, "idle");
                // Nothing to send: the placeholder and the track circle.
                pendingAttachments.Clear(); RefreshAttachments(); await Draft(""); await Change(p => p with { ResumeId = null }); Refresh();
                await Shot(Container, "empty");
                // The sign-in card, between the editor and the toolbar.
                o.loginRetries.Sent(new StartRunRequest(id, Session.WorkspaceId, "claude", sample, []));
                if (o.loginRetries.Settled(id) is { } retry && o.loginRetries.Remember(retry)) { o.RefreshLoginCards(); await Shot(Container, "login"); }
                o.DismissLoginRecovery(id);
                await Menu(model, "model-menu"); await Menu(effort, "effort-menu"); await Menu(permission, "permission-menu");
                await Popover<FlyoutPresenter>(() => ShowRunSettings(more), () => { RequireRunSettings(); HideRunSettings(); }, "run-settings");
                await Popover<FlyoutPresenter>(ShowContext, () => sessionInfoFlyout?.Hide(), "session-info");
                // A permission request, then a question, over the composer.
                o.smokePermissionResponses = [];
                var command = System.Text.Json.JsonSerializer.Serialize(new { command = "git status --short && git diff --stat", description = sample, timeout = 120000 });
                ReceiveToolPermission(new ToolPermissionRequest("smoke-composer-permission", "smoke-run", "smoke-composer-tool", "Bash", command, "Run command", Reason: Locale.Get("settings.run.appliesNextRequest"), BlockedPath: Workspace.Path));
                await WaitUI(() => toolPermissionHost.Visibility == Visibility.Visible && permissionCard.ActualHeight > 0);
                await Shot(Container, "permission");
                ClearToolPermissions();
                var asked = System.Text.Json.JsonSerializer.Serialize(new
                {
                    questions = new[]
                    {
                        new { header = Locale.Get("composer.label.permission"), question = sample, multiSelect = false, options = new[] { new { label = "Auto mode", description = Locale.Get("settings.run.appliesNextRequest") }, new { label = "Plan mode", description = Locale.Get("permission.other.plan") }, new { label = "Bypass", description = "" } } },
                        new { header = Locale.Get("composer.effort.label"), question = Locale.Get("composer.shell.title"), multiSelect = true, options = new[] { new { label = "High", description = "" }, new { label = "Max", description = "" } } },
                    },
                });
                var question = new ToolPermissionRequest("smoke-composer-question", "smoke-run", "smoke-composer-ask", "AskUserQuestion", asked, "Ask", CanAnswerQuestions: true);
                ReceiveToolPermission(question);
                await WaitUI(() => questionnaireCard is { ActualHeight: > 0 });
                questionDrafts[question.Id].Picks[0] = ["Auto mode"]; RenderToolPermissions();
                await WaitUI(() => questionnaireCard is { ActualHeight: > 0 });
                await Shot(Container, "question");
                ClearToolPermissions(); o.smokePermissionResponses = null;
                // Running: the pills dimmed and the stop square; then, with a draft and two requests waiting, the queue button and the small stop.
                await Change(p => p with { Status = "running" }); Refresh();
                await Shot(Container, "running");
                queuedInputs.Add(sample, []); queuedInputs.Add(Locale.Get("composer.shell.title"), attachments);
                await Draft(sample); Refresh();
                await Shot(Container, "queue");
                queuedInputs.Clear(); await Draft(""); await Change(p => p with { Status = "completed" }); Refresh();
                // A narrow pane: attach, the model and one options menu.
                Container.Width = 315; Container.UpdateLayout(); await WaitUI(() => Math.Abs(Container.ActualWidth - 315) < 1 && toolbarStyle == ToolbarStyle.Overflow);
                await Shot(Container, "narrow");
                await Menu(options, "options-menu");
            }
            finally
            {
                o.smokePermissionResponses = null; toolPermissions.Clear(); RenderToolPermissions(); queuedInputs.Clear();
                HideRunSettings(); sessionInfoFlyout?.Hide();
                Container.Width = double.NaN;
                await o.service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? original : p).ToList() });
                pendingAttachments.Clear(); pendingAttachments.AddRange(attachments); RefreshAttachments();
                updating = true; input.Text = draft; updating = false; Refresh(); Container.UpdateLayout();
            }
        }

        /// <summary>
        /// The open run-settings popover as the Mac draws it (M/SettingsViews.swift:33-125): each limit the 20-high rounded field in the
        /// body font with the tertiary placeholder, Cancel and Apply the 16-high small buttons, no scroll bar of its own, and no field
        /// in focus as it opens.
        /// </summary>
        private void RequireRunSettings()
        {
            var o = owner; var theme = o.SmokeTheme;
            var body = RunSettingsBody ?? throw new InvalidOperationException($"run settings ({theme}): the popover is not open");
            var fields = VisualChildren(body).OfType<TextBox>().ToList();
            Require(fields.Count > 0 && fields.All(field => field.Height == RunSettingsField && field.FontSize == 12 && field.FontFamily?.Source == DesignMetrics.Font.Body && ReferenceEquals(field.PlaceholderForeground, o.brushes.Tertiary)),
                $"run settings ({theme}): each limit must be a {RunSettingsField}-high 12pt field in the body font with the tertiary placeholder; got {string.Join(", ", fields.Select(field => $"{field.Height} high, {field.FontSize}pt '{field.FontFamily?.Source}', placeholder {Describe(field.PlaceholderForeground)}"))}");
            foreach (var field in fields) o.RequireBrush(field, e => ((TextBox)e).PlaceholderForeground, DesignToken.Ink, "a run-settings limit's placeholder", DesignBrushes.TertiaryOpacity, "run settings");
            var buttons = new[] { "run-settings-cancel-" + id, "run-settings-apply-" + id }.Select(wanted => VisualChildren(body).OfType<Button>().FirstOrDefault(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == wanted)).ToList();
            Require(buttons.All(button => button is { Height: SettingsSmallHeight, FontSize: 11 }), $"run settings ({theme}): Cancel and Apply must be the {SettingsSmallHeight}-high small buttons with 11pt words; got {string.Join(", ", buttons.Select(button => button is null ? "none" : $"{button.Height} high, {button.FontSize}pt"))}");
            var scroll = VisualChildren(body).OfType<ScrollViewer>().FirstOrDefault(viewer => viewer.Content is ContentControl);
            Require(scroll is { VerticalScrollBarVisibility: ScrollBarVisibility.Hidden, VerticalScrollMode: not ScrollMode.Disabled }, $"run settings ({theme}): the settings must scroll with no bar of their own; got {scroll?.VerticalScrollBarVisibility}, {scroll?.VerticalScrollMode}");
            // Programmatic focus can be refused (a window that is not in front); wherever it went, it is on none of the fields.
            Require(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(body.XamlRoot) is not TextBox focused || !fields.Contains(focused), $"run settings ({theme}): the popover must open with no field in focus");
            // While its pane is busy the popover's settings and Apply wait, and take up again when the pane is free (M/SettingsViews.swift:114, 122).
            var apply = buttons[1]!; var held = (ContentControl)scroll!.Content;
            try
            {
                HoldRunSettings(true);
                Require(!apply.IsEnabled && !held.IsEnabled && fields.All(field => !field.IsEnabled), $"run settings ({theme}): a busy pane must hold the popover's settings and Apply; got Apply {apply.IsEnabled}, settings {held.IsEnabled}, fields {string.Join(", ", fields.Select(field => field.IsEnabled))}");
            }
            finally { HoldRunSettings(false); }
            Require(apply.IsEnabled && held.IsEnabled && fields.All(field => field.IsEnabled), $"run settings ({theme}): the popover must take its settings again once its pane is free; got Apply {apply.IsEnabled}, settings {held.IsEnabled}");
        }

        private async Task SmokeConversationReset()
        {
            var original = Session; var originalDraft = input.Text;
            try
            {
                updating = true; input.Text = "unsent draft \uD55C\uAE00"; updating = false;
                await Change(p => p with { Draft = input.Text, Status = "completed", ResumeId = "smoke-resume", SessionUsage = new SessionUsage { Provider = p.Provider, Source = "smoke-reset", TotalTokens = 234, UpdatedAt = Wire.Now() } });
                var logs = Session.Logs.ToArray();
                Require(await ResetConversationAsync(CancellationToken.None), "Idle new-conversation action should succeed.");
                Require(Session.ResumeId is null && Session.SessionUsage is null && Session.Logs.Take(logs.Length).SequenceEqual(logs) && Session.Draft == "unsent draft \uD55C\uAE00" && input.Text == Session.Draft,
                    "New conversation must clear CLI identity/usage while keeping the pending draft and previous logs.");
                await Change(p => p with { Status = "running", ResumeId = "still-running" });
                Require(!await ResetConversationAsync(CancellationToken.None) && Session.ResumeId == "still-running", "New conversation must refuse a running pane.");
                // Mirror the view-ownership removal without deleting the main
                // fixture used by later smokes. This await completes synchronously.
                owner.views.Remove(id);
                try { Require(!await ResetConversationAsync(CancellationToken.None), "A removed pane must be ignored without resolving stale Session state."); }
                finally { owner.views[id] = this; }
            }
            finally
            {
                await owner.service.UpdateAsync(s => s with { Sessions = s.Sessions.Any(p => p.Id == id) ? s.Sessions.Select(p => p.Id == id ? original : p).ToList() : [original, .. s.Sessions] });
                updating = true; input.Text = originalDraft; updating = false; Refresh();
            }
        }
    }
}
