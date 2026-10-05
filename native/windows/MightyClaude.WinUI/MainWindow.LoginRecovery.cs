using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly CliLoginRetryBook loginRetries = new();
    private readonly Dictionary<string, LoginJob> loginJobs = [];
    // Cancellation removes the visible job immediately, but its provider stays
    // reserved until native process teardown completes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LoginJob> loginBusy = new();
    private readonly Dictionary<string, string> loginFailures = [], loginNotes = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> accountChanges = new();
    private readonly HashSet<Task> loginTasks = [];
    private readonly HashSet<string> loginResendsAwaitingUpdate = [];
    // When a lost sign-in may start its provider's sign-in by itself (Settings → CLI accounts switch).
    private CliAutoLoginGate autoLogin = new();
    // An automatic start held back only because another run of the provider was still going, per provider;
    // it is tried again once the provider is idle (ResumeDeferredAutoLogin).
    private readonly Dictionary<string, (CliLoginRetry Retry, CliAccountStatus Status)> deferredAutoLogins = [];
    // The smoke never runs a CLI: when set, this stands in for the sign-in process (MainWindow.SmallParitySmoke.cs).
    private Func<string, Task>? smokeLoginStarter;
    private sealed class LoginJob
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal CliBackgroundLogin? Process;
        internal string Phase = "starting";
        internal bool Automatic;
        internal Task Task = Task.CompletedTask;
    }

    private void InitializeLoginRecovery()
    {
        service.RequestStarting += request => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            loginRetries.Sent(request); loginNotes.Remove(request.SessionId); loginResendsAwaitingUpdate.Remove(request.SessionId);
            CancelUnusedLogin(request.Provider); RefreshLoginCards();
        });
    }
    private void ReceiveLoginSignal(RunEvent value)
    {
        if (value.Type != "status" || value.Status is not ("completed" or "stopped" or "error")) return;
        var request = loginRetries.Settled(value.SessionId);
        if (value.Status == "error" && value.Reason == "authentication" && request is not null)
            TrackLoginTask(ConfirmLoginFailure(request));
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == value.SessionId)?.Provider is { } provider && deferredAutoLogins.ContainsKey(provider))
            TrackLoginTask(ResumeDeferredAutoLogin(provider));
    }
    /// <summary>A run of the provider ended: an automatic start held back for it is tried again once nothing of the provider runs.</summary>
    private async Task ResumeDeferredAutoLogin(string provider)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!closing && service.HasActiveProvider(provider) && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        if (closing || service.HasActiveProvider(provider) || !deferredAutoLogins.Remove(provider, out var deferred)) return;
        // Only while that pane still waits with the same request.
        if (loginRetries.Requests.GetValueOrDefault(deferred.Retry.Request.SessionId) == deferred.Retry) StartAutomaticLoginIfAllowed(deferred.Retry, deferred.Status);
    }
    private void TrackLoginTask(Task task)
    {
        loginTasks.Add(task);
        _ = task.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => loginTasks.Remove(task)), TaskScheduler.Default);
    }
    private async Task ConfirmLoginFailure(CliLoginRetry retry)
    {
        try
        {
            var status = await accountsCoordinator.StatusAsync(retry.Request.Provider);
            var idleDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!closing && loginRetries.IsCurrent(retry) && service.IsSessionRunning(retry.Request.SessionId) && DateTimeOffset.UtcNow < idleDeadline)
                await Task.Delay(50);
            if (closing || !loginRetries.IsCurrent(retry) || !CliAuthFailure.SignInCanFix(status)) return;
            var pane = service.Snapshot.Sessions.FirstOrDefault(p => p.Id == retry.Request.SessionId);
            if (pane is null || pane.Provider != retry.Request.Provider || pane.Status == "running" || service.IsSessionRunning(pane.Id)) return;
            loginRetries.Remember(retry); RefreshLoginCards();
            StartAutomaticLoginIfAllowed(retry, status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* A status read cannot turn a failed run into a new failure. */ }
    }
    private void RefreshLoginCards()
    {
        var live = service.Snapshot.Sessions.ToDictionary(p => p.Id);
        foreach (var pair in loginRetries.Requests.ToArray())
            if (!live.TryGetValue(pair.Key, out var pane) || pane.Provider != pair.Value.Request.Provider) loginRetries.Drop(pair.Key);
        foreach (var provider in loginJobs.Keys.ToArray()) CancelUnusedLogin(provider);
        foreach (var pane in views.Values) pane.RenderLoginRecovery();
    }
    private void DismissLoginRecovery(string session)
    {
        var provider = loginRetries.Requests.GetValueOrDefault(session)?.Request.Provider;
        loginRetries.Drop(session); loginNotes.Remove(session); loginResendsAwaitingUpdate.Remove(session);
        if (provider is not null) CancelUnusedLogin(provider);
        RefreshLoginCards();
    }
    private void CancelUnusedLogin(string provider)
    {
        if (!loginRetries.Requests.Values.Any(retry => retry.Request.Provider == provider)) CancelBackgroundLogin(provider);
    }
    private void CancelBackgroundLogin(string provider)
    {
        if (loginJobs.Remove(provider, out var job)) { job.Cancellation.Cancel(); autoLogin.Stopped(provider, DateTimeOffset.UtcNow); }
        loginFailures.Remove(provider);
    }
    /// <summary>
    /// The automatic start (M/AppStore+CLILoginRecovery.swift startAutomaticLoginIfAllowed): one sign-in per
    /// provider however many panes lost it together, and none for a while after one failed or was cancelled.
    /// Anything that would refuse the sign-in now leaves the card as it was, with its button.
    /// </summary>
    private void StartAutomaticLoginIfAllowed(CliLoginRetry retry, CliAccountStatus status)
    {
        var provider = retry.Request.Provider;
        var changing = loginJobs.ContainsKey(provider) || loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider) || AnyCliUpdateRunning;
        bool Allowed(bool active) => autoLogin.ShouldStart(provider, service.Snapshot.AutoLoginCLIs != false, status, active, DateTimeOffset.UtcNow, retry.Resent, retry.SentAt);
        if (closing) return;
        if (!changing && service.HasActiveProvider(provider)) { if (Allowed(false)) deferredAutoLogins[provider] = (retry, status); return; }
        if (!Allowed(changing)) return;
        deferredAutoLogins.Remove(provider);
        _ = StartBackgroundLogin(provider, automatic: true);
    }
    /// <summary>The provider's automatic sign-in, running or still shutting down; a send cancels it (M sends never wait on one).</summary>
    private LoginJob? AutomaticLoginOf(string provider) =>
        loginJobs.GetValueOrDefault(provider) is { Automatic: true } job ? job : loginBusy.GetValueOrDefault(provider) is { Automatic: true } ending ? ending : null;
    private Task StartBackgroundLogin(string provider, bool automatic = false)
    {
        if (closing || loginJobs.ContainsKey(provider) || provider is not ("claude" or "codex")) return Task.CompletedTask;
        if (options.SmokeTest)
        {
            if (smokeLoginStarter is null) return Task.CompletedTask;
            loginJobs[provider] = new LoginJob { Phase = "waiting", Automatic = automatic }; loginFailures.Remove(provider); RefreshLoginCards();
            return smokeLoginStarter(provider);
        }
        if (loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider) || AnyCliUpdateRunning || service.HasActiveProvider(provider))
        {
            loginFailures[provider] = Locale.Get(AnyCliUpdateRunning ? "loginRecovery.updating" : "loginRecovery.busy"); RefreshLoginCards(); return Task.CompletedTask;
        }
        var job = new LoginJob { Automatic = automatic }; loginJobs[provider] = job; loginBusy[provider] = job; loginFailures.Remove(provider);
        job.Task = RunBackgroundLogin(provider, job); TrackLoginTask(job.Task); RefreshLoginCards();
        return Task.CompletedTask;
    }
    private async Task RunBackgroundLogin(string provider, LoginJob job)
    {
        var cancellation = job.Cancellation.Token;
        try
        {
            // Re-read account method at the actual button click. A meanwhile-selected
            // Bedrock/API-key configuration must never start a browser account login.
            if (!CliAuthFailure.SignInCanFix(await accountsCoordinator.StatusAsync(provider, cancellation))) throw new InvalidOperationException();
            var command = await service.Providers.FindAsync(provider, cancellation) ?? throw new InvalidOperationException();
            var argv = CliAccountSupport.LoginArguments(provider) ?? throw new InvalidOperationException();
            cancellation.ThrowIfCancellationRequested();
            job.Process = await Task.Run(() => new CliBackgroundLogin(command, argv.Skip(1).ToArray(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), cancellation);
            if (cancellation.IsCancellationRequested) return;
            job.Process.Changed += _ => DispatcherQueue.TryEnqueue(() => { if (!closing && loginJobs.GetValueOrDefault(provider) == job) RefreshLoginCards(); });
            job.Phase = "waiting"; RefreshLoginCards();
            var outcome = await CliLoginWait.RunAsync(() => job.Process.IsRunning, () => job.Process.ExitCode, token => accountsCoordinator.StatusAsync(provider, token), cancellation);
            job.Process.FinishOutput();
            if (outcome.Outcome == CliLoginOutcome.LoggedIn)
            {
                // Let the CLI finish saving its credential files before ending a lingering job.
                try { await job.Process.Completion.WaitAsync(TimeSpan.FromSeconds(15), cancellation); } catch (TimeoutException) { }
                await job.Process.DisposeAsync();
                cancellation.ThrowIfCancellationRequested();
                if (loginJobs.GetValueOrDefault(provider) != job) return;
                autoLogin.Succeeded(provider, DateTimeOffset.UtcNow); loginJobs.Remove(provider); loginFailures.Remove(provider);
                if (loginBusy.GetValueOrDefault(provider) == job) loginBusy.TryRemove(provider, out _);
                await RefreshCliAccounts();
                if (closing) return;
                foreach (var id in loginRetries.Requests.Values.Where(value => value.Request.Provider == provider).Select(value => value.Request.SessionId).ToArray())
                    await ResendLoginRequest(id);
            }
            else if (loginJobs.GetValueOrDefault(provider) == job)
            {
                loginJobs.Remove(provider); autoLogin.Stopped(provider, DateTimeOffset.UtcNow);
                loginFailures[provider] = Locale.Get(outcome.Outcome == CliLoginOutcome.TimedOut ? "loginRecovery.timedOut" : "loginRecovery.exited");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!closing && loginJobs.GetValueOrDefault(provider) == job) { loginJobs.Remove(provider); autoLogin.Stopped(provider, DateTimeOffset.UtcNow); loginFailures[provider] = Locale.Get("loginRecovery.startFailed"); }
        }
        finally
        {
            if (job.Process is not null) await job.Process.DisposeAsync();
            if (loginBusy.GetValueOrDefault(provider) == job) loginBusy.TryRemove(provider, out _);
            if (loginJobs.GetValueOrDefault(provider) == job) loginJobs.Remove(provider);
            job.Cancellation.Dispose();
            if (!closing) RefreshLoginCards();
        }
    }
    private async Task ResendLoginRequest(string id)
    {
        if (closing || !loginRetries.Requests.TryGetValue(id, out var retry)) return;
        var pane = service.Snapshot.Sessions.FirstOrDefault(value => value.Id == id);
        if (pane is null || pane.Provider != retry.Request.Provider || pane.Status == "running" || service.IsSessionRunning(id)) { DismissLoginRecovery(id); return; }
        if (AnyCliUpdateRunning || ManualMutationBlockReason(pane) is not null || loginBusy.ContainsKey(pane.Provider) || accountChanges.ContainsKey(pane.Provider))
        {
            if (AnyCliUpdateRunning || ManualMutationBlockReason(pane) is not null) loginResendsAwaitingUpdate.Add(id);
            loginNotes[id] = ManualMutationBlockReason(pane) ?? Locale.Get(AnyCliUpdateRunning ? "loginRecovery.updating" : "loginRecovery.busy"); RefreshLoginCards(); return;
        }
        loginResendsAwaitingUpdate.Remove(id);
        try
        {
            if (await ReloadProviderModels()) RefreshEnvironment();
            if (closing || !loginRetries.IsCurrent(retry)) return;
            if (!views.TryGetValue(id, out var view)) { await SelectLayoutSession(id); view = views.GetValueOrDefault(id); }
            if (view is null || !await view.ResendLoginRequest(retry)) throw new InvalidOperationException(Locale.Get("loginRecovery.resendFailed"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (loginRetries.Remember(retry)) loginNotes[id] = ex.Message;
        }
        RefreshLoginCards();
    }
    private async Task ResendLoginRequestsAfterUpdate()
    {
        if (closing || AnyCliUpdateRunning || pluginOperations.IsRunning) return;
        foreach (var id in loginResendsAwaitingUpdate.Order().ToArray())
        {
            loginResendsAwaitingUpdate.Remove(id);
            await ResendLoginRequest(id);
        }
    }
    private async Task TerminalLoginFallback(string provider)
    {
        var previous = loginBusy.GetValueOrDefault(provider);
        CancelBackgroundLogin(provider);
        if (previous is not null) await previous.Task;
        if (closing) return;
        await StartCliSignIn(provider, CliLoginOption.Account);
        RefreshLoginCards();
    }
    private async Task ShutdownLoginRecovery()
    {
        foreach (var provider in loginJobs.Keys.ToArray()) CancelBackgroundLogin(provider);
        await Task.WhenAll(loginTasks.ToArray()); loginTasks.Clear(); loginRetries.Clear(); loginNotes.Clear(); loginResendsAwaitingUpdate.Clear(); deferredAutoLogins.Clear();
    }
    private sealed partial class PaneView
    {
        /// <summary>The sign-in card, between the editor and the toolbar (M/SessionPaneView.swift:619-621, M/CLILoginRecoveryCard.swift:67).</summary>
        private readonly StackPanel loginRecoveryHost = new() { Margin = new Thickness(12, 0, 12, 0), Visibility = Visibility.Collapsed };
        private string? loginCardFingerprint;
        private void InitializeLoginRecoveryCard() => AutomationProperties.SetAutomationId(loginRecoveryHost, "login-required-" + id);
        internal void RenderLoginRecovery()
        {
            if (!owner.loginRetries.Requests.TryGetValue(id, out var retry)) { loginRecoveryHost.Children.Clear(); loginRecoveryHost.Visibility = Visibility.Collapsed; loginCardFingerprint = null; return; }
            var provider = retry.Request.Provider;
            var job = owner.loginJobs.GetValueOrDefault(provider); var progress = job?.Process?.Output;
            var failure = owner.loginFailures.GetValueOrDefault(provider); var note = owner.loginNotes.GetValueOrDefault(id);
            var fingerprint = $"{owner.service.Snapshot.LanguagePreference}:{retry.Generation}:{job?.Phase}:{job?.Automatic}:{progress?.Url}:{progress?.AsksForCode}:{failure}:{note}";
            if (loginCardFingerprint == fingerprint) return;
            loginCardFingerprint = fingerprint; loginRecoveryHost.Children.Clear(); loginRecoveryHost.Visibility = Visibility.Visible;
            // The Mac's card (M/CLILoginRecoveryCard.swift:15-69), one 11pt row: the amber account mark; the title in medium
            // ink over what is happening (the explanation in ink2, a note or failure in the amber waitText, a spinner while
            // the CLI signs in); then the small buttons and the dismiss cross.
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2); var wait = b.Brush(DesignToken.WaitText);
            TextBlock Line(string text, Brush brush) => new() { Text = text, FontSize = 11, Foreground = brush, TextWrapping = TextWrapping.Wrap };
            Button Small(string title, Func<Task> action) => SmallButton(title, () => owner.Act(action));
            var card = new Grid { ColumnSpacing = 7 };
            card.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); card.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); card.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            card.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = wait, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
            var words = new StackPanel { Spacing = 4 };
            var title = Line(Locale.Get("loginRecovery.title", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) }), ink); title.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
            words.Children.Add(title);
            if (note is not null) words.Children.Add(Line(Locale.Get("loginRecovery.resendBlockedTemplate", new Dictionary<string, string> { ["reason"] = note }), wait));
            if (job is not null)
            {
                if (job.Automatic) words.Children.Add(Line(Locale.Get("loginRecovery.autoStarted"), ink2));
                var busy = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                busy.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = ink2 });
                busy.Children.Add(Line(Locale.Get(job.Phase == "starting" ? "loginRecovery.starting" : "loginRecovery.waiting"), ink2)); words.Children.Add(busy);
            }
            else if (failure is not null) words.Children.Add(Line(failure, wait));
            else words.Children.Add(Line(Locale.Get("loginRecovery.body"), ink2));
            if (progress?.Url is { } url)
            {
                var link = Small(Locale.Get("loginRecovery.openLink"), () => { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; });
                link.HorizontalAlignment = HorizontalAlignment.Left; ToolTipService.SetToolTip(link, url.AbsoluteUri); AutomationProperties.SetAutomationId(link, "login-link-" + id); words.Children.Add(link);
            }
            if (progress?.AsksForCode == true)
            {
                var field = new PasswordBox { PlaceholderText = Locale.Get("loginRecovery.codePlaceholder"), MaxLength = 4096, Width = 240, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left };
                // AppKit's placeholder is the tertiary ink (M/CLILoginRecoveryCard.swift:41). A password box has no placeholder property: its template reads these resources.
                owner.SetResourcesOnce(field, new[] { "", "PointerOver", "Focused" }.Select(state => ("TextControlPlaceholderForeground" + state, (object)b.Tertiary)).ToList());
                AutomationProperties.SetName(field, Locale.Get("loginRecovery.codePlaceholder"));
                Task SubmitCode() { if (job?.Process?.SendCode(field.Password) == true) field.Password = ""; return Task.CompletedTask; }
                field.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await SubmitCode(); } };
                words.Children.Add(Line(Locale.Get("loginRecovery.codePrompt"), ink2));
                var code = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; code.Children.Add(field); code.Children.Add(Small(Locale.Get("loginRecovery.codeSubmit"), SubmitCode)); words.Children.Add(code);
            }
            Grid.SetColumn(words, 1); card.Children.Add(words);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 0, 0, 0) };
            if (job is not null)
            {
                // Sends of the provider wait while its sign-in runs, so resending is offered again once it ends.
                buttons.Children.Add(Small(Locale.Get("loginRecovery.cancel"), () => { owner.CancelBackgroundLogin(provider); owner.RefreshLoginCards(); return Task.CompletedTask; }));
                buttons.Children.Add(Small(Locale.Get("loginRecovery.terminalButton"), () => owner.TerminalLoginFallback(provider)));
            }
            else
            {
                buttons.Children.Add(Small(Locale.Get("loginRecovery.loginButton"), () => owner.StartBackgroundLogin(provider)));
                if (failure is not null) buttons.Children.Add(Small(Locale.Get("loginRecovery.terminalButton"), () => owner.TerminalLoginFallback(provider)));
                var resend = Small(Locale.Get("loginRecovery.resendButton"), () => owner.ResendLoginRequest(id)); ToolTipService.SetToolTip(resend, Locale.Get("loginRecovery.resendHelp")); buttons.Children.Add(resend);
            }
            var dismiss = owner.SafeButton("×", () => { owner.DismissLoginRecovery(id); return Task.CompletedTask; });
            dismiss.Content = new FontIcon { Glyph = "", FontSize = 9 }; dismiss.Width = 18; dismiss.Height = 16; dismiss.MinWidth = 0; dismiss.MinHeight = 0; dismiss.Padding = new Thickness(0); dismiss.BorderThickness = new Thickness(0); dismiss.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow); dismiss.VerticalAlignment = VerticalAlignment.Center;
            owner.PaintPlainButton(dismiss, b.Transparent, b.Subtle, ink: ink2);
            AutomationProperties.SetName(dismiss, Locale.Get("loginRecovery.dismiss")); buttons.Children.Add(dismiss);
            Grid.SetColumn(buttons, 2); card.Children.Add(buttons);
            loginRecoveryHost.Children.Add(card);
        }
        internal async Task<bool> ResendLoginRequest(CliLoginRetry retry)
        {
            if (!QueuePaneAlive || starting || queueStarting || Session.Status == "running" || !owner.loginRetries.IsCurrent(retry)) return false;
            starting = true; var submission = ++composerSubmissionVersion; RefreshComposerState();
            try
            {
                var pane = Session;
                if (owner.Runtime(pane.Provider)?.Available != true) throw new InvalidOperationException(Locale.Get("composer.hint.refreshRuntime"));
                var request = await PrepareStyleRunRequest(new StartRunRequest(id, pane.WorkspaceId, pane.Kind, retry.Request.Input,
                    RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot), pane.Model, pane.Provider, pane.Settings, pane.ResumeId, retry.Request.Attachments));
                if (!QueuePaneAlive || submission != composerSubmissionVersion || Session.Provider != retry.Request.Provider || !owner.loginRetries.IsCurrent(retry)) return false;
                // Its own sign-in failure, if any, only raises the card again (never an automatic start).
                owner.loginRetries.ExpectResend(id);
                try { await owner.StartFromComposer(request); }
                catch { owner.loginRetries.CancelResend(id); throw; }
                return true;
            }
            finally { starting = false; if (QueuePaneAlive) Refresh(); }
        }
    }
}
