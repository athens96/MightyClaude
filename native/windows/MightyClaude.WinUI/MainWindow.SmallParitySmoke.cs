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
            var checks = await pane.SmokeSmallParityAsync();
            checks["automaticLogin"] = await SmokeAutomaticLogin(pane, id, workspace) + "; " + await SmokeAutomaticGeminiLogin(pane, id, workspace);
            return checks;
        }
        finally { await service.UpdateAsync(_ => saved); Render(); }
    }
    // A lost sign-in starts one sign-in per provider by itself, with no CLI: the injected starter stands in for
    // the sign-in process and the account status is a fixture, so this never asks a real CLI anything.
    private async Task<string> SmokeAutomaticLogin(PaneView pane, string id, Workspace workspace)
    {
        var starts = 0; smokeLoginStarter = _ => { starts++; return Task.CompletedTask; };
        var sends = new List<StartRunRequest>(); var previousStart = smokeStart;
        var status = new CliAccountStatus { Provider = "claude", LoggedIn = true, Method = "claude.ai", MethodId = CliAccountMethod.ClaudeSubscription };
        try
        {
            Require(!service.HasActiveProvider("claude") && !AnyCliUpdateRunning && !loginJobs.ContainsKey("claude") && !loginBusy.ContainsKey("claude") && !accountChanges.ContainsKey("claude"),
                $"automatic sign-in: the smoke needs Claude idle with no update or sign-in under way; got running={service.HasActiveProvider("claude")}, update={AnyCliUpdateRunning}, sign-in={loginJobs.ContainsKey("claude") || loginBusy.ContainsKey("claude")}, account change={accountChanges.ContainsKey("claude")}");
            autoLogin = new();
            loginRetries.Sent(new StartRunRequest(id, workspace.Id, "claude", "smoke", []));
            Require(loginRetries.Settled(id) is { } settled && loginRetries.Remember(settled), "automatic sign-in: the fixture retry must be kept");
            var retry = loginRetries.Requests[id];
            await service.UpdateAsync(s => s with { AutoLoginCLIs = false });
            StartAutomaticLoginIfAllowed(retry, status);
            Require(starts == 0 && !loginJobs.ContainsKey("claude"), "automatic sign-in: with the switch off a lost sign-in must only show the card");
            await service.UpdateAsync(s => s with { AutoLoginCLIs = null });
            // A request login recovery resent only raises the card.
            StartAutomaticLoginIfAllowed(retry with { Resent = true }, status);
            Require(starts == 0, "automatic sign-in: a resent request's failure must not start a sign-in");
            // Two panes losing the sign-in together.
            StartAutomaticLoginIfAllowed(retry, status); StartAutomaticLoginIfAllowed(retry, status);
            Require(starts == 1 && loginJobs.TryGetValue("claude", out var job) && job.Automatic,
                $"automatic sign-in: one lost sign-in must start exactly one sign-in; got {starts} starts, job {loginJobs.ContainsKey("claude")}");
            RefreshLoginCards();
            Require(pane.SmokeLoginCardSays(Locale.Get("loginRecovery.autoStarted")) && pane.SmokeLoginCardSays(Locale.Get("loginRecovery.waiting")) && pane.SmokeLoginCardSays(Locale.Get("loginRecovery.cancel")),
                "automatic sign-in: the card must say the sign-in started by itself, show its progress and keep Cancel");
            CancelBackgroundLogin("claude"); RefreshLoginCards();
            StartAutomaticLoginIfAllowed(retry, status);
            Require(starts == 1 && !loginJobs.ContainsKey("claude") && pane.SmokeLoginCardSays(Locale.Get("loginRecovery.loginButton")),
                $"automatic sign-in: a cancelled sign-in must not start again within the cooldown, and the card must offer its button; got {starts} starts");
            // A send from a composer while an automatic sign-in runs cancels it and goes ahead.
            autoLogin = new();
            StartAutomaticLoginIfAllowed(retry, status);
            Require(starts == 2 && loginJobs.ContainsKey("claude"), $"automatic sign-in: a fresh gate must start again; got {starts} starts");
            smokeStart = request => { sends.Add(request); return Task.CompletedTask; };
            await StartFromComposer(new StartRunRequest(id, workspace.Id, "claude", "smoke send", []));
            StartAutomaticLoginIfAllowed(retry with { SentAt = DateTimeOffset.UtcNow }, status);
            Require(sends.Count == 1 && !loginJobs.ContainsKey("claude") && starts == 2,
                $"automatic sign-in: a composer send must cancel the automatic sign-in, go ahead and hold the next start; got {sends.Count} sends, sign-in {loginJobs.ContainsKey("claude")}, {starts} starts");
            return "one start, switch and resend respected, cooldown after cancel and after a send";
        }
        finally { smokeLoginStarter = null; smokeStart = previousStart; CancelBackgroundLogin("claude"); autoLogin = new(); DismissLoginRecovery(id); }
    }
    // Gemini goes through the same gate but signs in in a terminal: the injected starter stands in for the terminal and
    // the status is a fixture, so no CLI or window is started. The pane is Gemini for this check only (the caller restores the state).
    private async Task<string> SmokeAutomaticGeminiLogin(PaneView pane, string id, Workspace workspace)
    {
        var starts = new List<string>(); smokeLoginStarter = provider => { starts.Add(provider); return Task.CompletedTask; };
        var google = new CliAccountStatus { Provider = "gemini", LoggedIn = true, Method = CliAccountSupport.GeminiGoogleMethod, MethodId = CliAccountMethod.GeminiGoogle };
        try
        {
            Require(!service.HasActiveProvider("gemini") && !AnyCliUpdateRunning && !loginJobs.ContainsKey("gemini") && !loginBusy.ContainsKey("gemini") && !accountChanges.ContainsKey("gemini"),
                $"automatic Gemini sign-in: the smoke needs Gemini idle with no update or sign-in under way; got running={service.HasActiveProvider("gemini")}, update={AnyCliUpdateRunning}, sign-in={loginJobs.ContainsKey("gemini") || loginBusy.ContainsKey("gemini")}, account change={accountChanges.ContainsKey("gemini")}");
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Provider = "gemini" } : p).ToList() });
            autoLogin = new();
            loginRetries.Sent(new StartRunRequest(id, workspace.Id, "claude", "smoke", [], Provider: "gemini"));
            Require(loginRetries.Settled(id) is { } settled && loginRetries.Remember(settled), "automatic Gemini sign-in: the fixture retry must be kept");
            var retry = loginRetries.Requests[id];
            // A key or Vertex AI is not renewed by signing in: only the card.
            StartAutomaticLoginIfAllowed(retry, google with { Method = Locale.Get("windows.cli.method.geminiApiKey"), MethodId = CliAccountMethod.GeminiApiKey });
            StartAutomaticLoginIfAllowed(retry, google with { Method = "Vertex AI", MethodId = CliAccountMethod.GeminiVertex });
            Require(starts.Count == 0 && !loginJobs.ContainsKey("gemini"), "automatic Gemini sign-in: an API key or Vertex AI must never open the sign-in terminal");
            StartAutomaticLoginIfAllowed(retry, google); StartAutomaticLoginIfAllowed(retry, google);
            Require(starts.SequenceEqual(new[] { "gemini" }) && loginJobs.TryGetValue("gemini", out var job) && job.Automatic && job.Terminal,
                $"automatic Gemini sign-in: one lost sign-in must open exactly one sign-in terminal; got {string.Join(",", starts)}");
            RefreshLoginCards();
            Require(pane.SmokeLoginCardSays(Locale.Get("loginRecovery.geminiTerminal")) && pane.SmokeLoginCardSays(Locale.Get("loginRecovery.cancel")) && !pane.SmokeLoginCardSays(Locale.Get("loginRecovery.terminalButton")),
                "automatic Gemini sign-in: the card must say a sign-in terminal opened, keep Cancel and offer no second terminal");
            // Nothing runs in the background, so a send neither waits on the terminal sign-in nor is refused by it.
            var sends = new List<StartRunRequest>(); var previousStart = smokeStart; smokeStart = request => { sends.Add(request); return Task.CompletedTask; };
            try { await StartFromComposer(new StartRunRequest(id, workspace.Id, "claude", "smoke send", [], Provider: "gemini")); }
            finally { smokeStart = previousStart; }
            Require(sends.Count == 1 && loginJobs.ContainsKey("gemini"), $"automatic Gemini sign-in: a send must go ahead beside the terminal sign-in; got {sends.Count} sends, sign-in {loginJobs.ContainsKey("gemini")}");
            CancelBackgroundLogin("gemini"); RefreshLoginCards();
            StartAutomaticLoginIfAllowed(retry, google);
            Require(starts.Count == 1 && pane.SmokeLoginCardSays(Locale.Get("loginRecovery.loginButton")),
                "automatic Gemini sign-in: a cancelled sign-in must not open the terminal again within the cooldown, and the card must offer its button");
            return "Gemini: one terminal start, key and Vertex excluded, sends go ahead, cooldown after cancel";
        }
        finally { smokeLoginStarter = null; CancelBackgroundLogin("gemini"); autoLogin = new(); DismissLoginRecovery(id); }
    }
    private sealed partial class PaneView
    {
        /// <summary>Whether the pane's sign-in card shows these words, as a line or on a button.</summary>
        internal bool SmokeLoginCardSays(string text)
        {
            static bool Has(UIElement element, string text) => element switch
            {
                TextBlock words => words.Text == text,
                Button { Content: string title } => title == text,
                Button { Content: UIElement content } => Has(content, text),
                Panel panel => panel.Children.Any(child => Has(child, text)),
                _ => false,
            };
            return loginRecoveryHost.Visibility == Visibility.Visible && Has(loginRecoveryHost, text);
        }
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
                // Stop, steer (a Claude turn and no files), then queue where send sits, each round like send; at 315 the row
                // still folds into the options menu and every control stays on the one toolbar-high line, inside the pane.
                var queueable = Session.Kind == "claude";
                var steerShown = queueable && Session.Provider == "claude" && pendingAttachments.Count == 0;
                var cluster = toolbarActions.Children.ToList();
                Require(queueStopButton?.Visibility == (queueable ? Visibility.Visible : Visibility.Collapsed) && steerHost.Visibility == (steerShown ? Visibility.Visible : Visibility.Collapsed)
                    && cluster.IndexOf(queueStopButton!) == cluster.Count - 3 && cluster.IndexOf(steerHost) == cluster.Count - 2 && cluster.IndexOf(sendHost) == cluster.Count - 1
                    && (!queueable || sendSymbol == "queue") && steer.IsEnabled == (steerShown && send.IsEnabled),
                    $"composer capture ({theme}): a busy pane with a draft must show stop, steer (Claude only) and queue in that order; got stop {queueStopButton?.Visibility}, steer {steerHost.Visibility} ({steer.IsEnabled}), send {sendSymbol} ({send.IsEnabled})");
                if (steerShown)
                {
                    o.RequireBrush(steerDisc, e => ((Border)e).Background, steer.IsEnabled ? DesignToken.Run : DesignToken.Track, $"the steer circle (steer {(steer.IsEnabled ? "enabled" : "disabled")})", key: "composer");
                    o.RequireBrush(steerDisc, _ => steerBolt.Colour, steer.IsEnabled ? DesignToken.OnStatus : DesignToken.Ink2, "the steer bolt", key: "composer");
                    Require(steerDisc.CornerRadius == sendDisc.CornerRadius && steerDisc.Width == sendDisc.Width && steerHost.Opacity == sendHost.Opacity,
                        $"composer capture ({theme}): steer must wear the queue button's circle; got r{steerDisc.CornerRadius} {steerDisc.Width} at {steerHost.Opacity}, queue r{sendDisc.CornerRadius} {sendDisc.Width} at {sendHost.Opacity}");
                }
                Container.Width = 315; Container.UpdateLayout(); await WaitUI(() => Math.Abs(Container.ActualWidth - 315) < 1 && toolbarStyle == ToolbarStyle.Overflow);
                var row = selectors.Children.OfType<FrameworkElement>().Concat(toolbarActions.Children.OfType<FrameworkElement>()).Where(c => c.Visibility == Visibility.Visible).ToArray();
                var middles = row.Select(c => c.TransformToVisual(Container).TransformPoint(new(0, 0)).Y + c.ActualHeight / 2).ToArray();
                var pillsEnd = selectors.TransformToVisual(toolbar).TransformPoint(new(selectors.ActualWidth, 0)).X; var clusterStart = toolbarActions.TransformToVisual(toolbar).TransformPoint(new(0, 0)).X;
                Require(row.All(c => Math.Abs(c.ActualHeight - (ReferenceEquals(c, queueStopButton) ? QueueStopSize : DesignMetrics.Layout.Toolbar)) < 1 && c.TransformToVisual(Container).TransformPoint(new(c.ActualWidth, 0)).X <= Container.ActualWidth + 1)
                    && middles.Max() - middles.Min() < 1 && pillsEnd <= clusterStart + .5,
                    $"composer capture ({theme}): at 315 the busy toolbar with stop, steer and queue must stay one line inside the pane; pills end {pillsEnd:F1}, cluster starts {clusterStart:F1}");
                Container.Width = double.NaN; Container.UpdateLayout();
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
