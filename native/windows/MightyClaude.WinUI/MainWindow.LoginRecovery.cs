using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
    private sealed class LoginJob
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal CliBackgroundLogin? Process;
        internal string Phase = "starting";
        internal Task Task = Task.CompletedTask;
    }

    private void InitializeLoginRecovery()
    {
        service.RequestStarting += request => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            loginRetries.Sent(request); loginNotes.Remove(request.SessionId);
            CancelUnusedLogin(request.Provider); RefreshLoginCards();
        });
    }
    private void ReceiveLoginSignal(RunEvent value)
    {
        if (value.Type != "status" || value.Status is not ("completed" or "stopped" or "error")) return;
        var request = loginRetries.Settled(value.SessionId);
        if (value.Status == "error" && value.Reason == "authentication" && request is not null)
            TrackLoginTask(ConfirmLoginFailure(request));
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
        loginRetries.Drop(session); loginNotes.Remove(session);
        if (provider is not null) CancelUnusedLogin(provider);
        RefreshLoginCards();
    }
    private void CancelUnusedLogin(string provider)
    {
        if (!loginRetries.Requests.Values.Any(retry => retry.Request.Provider == provider)) CancelBackgroundLogin(provider);
    }
    private void CancelBackgroundLogin(string provider)
    {
        if (loginJobs.Remove(provider, out var job)) job.Cancellation.Cancel();
        loginFailures.Remove(provider);
    }
    private Task StartBackgroundLogin(string provider)
    {
        if (options.SmokeTest || closing || loginJobs.ContainsKey(provider) || provider is not ("claude" or "codex")) return Task.CompletedTask;
        if (loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider) || coordinator.IsUpdating || service.HasActiveProvider(provider))
        {
            loginFailures[provider] = Locale.Get(coordinator.IsUpdating ? "loginRecovery.updating" : "loginRecovery.busy"); RefreshLoginCards(); return Task.CompletedTask;
        }
        var job = new LoginJob(); loginJobs[provider] = job; loginBusy[provider] = job; loginFailures.Remove(provider);
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
                loginJobs.Remove(provider); loginFailures.Remove(provider);
                if (loginBusy.GetValueOrDefault(provider) == job) loginBusy.TryRemove(provider, out _);
                await RefreshCliAccounts();
                if (closing) return;
                foreach (var id in loginRetries.Requests.Values.Where(value => value.Request.Provider == provider).Select(value => value.Request.SessionId).ToArray())
                    await ResendLoginRequest(id);
            }
            else if (loginJobs.GetValueOrDefault(provider) == job)
            {
                loginJobs.Remove(provider);
                loginFailures[provider] = Locale.Get(outcome.Outcome == CliLoginOutcome.TimedOut ? "loginRecovery.timedOut" : "loginRecovery.exited");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!closing && loginJobs.GetValueOrDefault(provider) == job) { loginJobs.Remove(provider); loginFailures[provider] = Locale.Get("loginRecovery.startFailed"); }
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
        if (coordinator.IsUpdating || loginBusy.ContainsKey(pane.Provider) || accountChanges.ContainsKey(pane.Provider))
        {
            loginNotes[id] = Locale.Get(coordinator.IsUpdating ? "loginRecovery.updating" : "loginRecovery.busy"); RefreshLoginCards(); return;
        }
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
        await Task.WhenAll(loginTasks.ToArray()); loginTasks.Clear(); loginRetries.Clear(); loginNotes.Clear();
    }
    private sealed partial class PaneView
    {
        private readonly StackPanel loginRecoveryHost = new() { Spacing = 5, Visibility = Visibility.Collapsed };
        private string? loginCardFingerprint;
        private void InitializeLoginRecoveryCard(StackPanel composer)
        {
            composer.Children.Insert(0, loginRecoveryHost);
            AutomationProperties.SetAutomationId(loginRecoveryHost, "login-required-" + id);
        }
        internal void RenderLoginRecovery()
        {
            if (!owner.loginRetries.Requests.TryGetValue(id, out var retry)) { loginRecoveryHost.Children.Clear(); loginRecoveryHost.Visibility = Visibility.Collapsed; loginCardFingerprint = null; return; }
            var provider = retry.Request.Provider;
            var job = owner.loginJobs.GetValueOrDefault(provider); var progress = job?.Process?.Output;
            var failure = owner.loginFailures.GetValueOrDefault(provider); var note = owner.loginNotes.GetValueOrDefault(id);
            var fingerprint = $"{owner.service.Snapshot.LanguagePreference}:{retry.Generation}:{job?.Phase}:{progress?.Url}:{progress?.AsksForCode}:{failure}:{note}";
            if (loginCardFingerprint == fingerprint) return;
            loginCardFingerprint = fingerprint; loginRecoveryHost.Children.Clear(); loginRecoveryHost.Visibility = Visibility.Visible;
            loginRecoveryHost.Children.Add(new TextBlock { Text = Locale.Get("loginRecovery.title", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) }), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            loginRecoveryHost.Children.Add(new TextBlock { Text = note is not null ? Locale.Get("loginRecovery.resendBlockedTemplate", new Dictionary<string, string> { ["reason"] = note }) : failure ?? Locale.Get(job?.Phase == "starting" ? "loginRecovery.starting" : job is not null ? "loginRecovery.waiting" : "loginRecovery.body"), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            if (progress?.Url is { } url)
                loginRecoveryHost.Children.Add(owner.SafeButton(Locale.Get("loginRecovery.openLink"), () => { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }));
            if (progress?.AsksForCode == true)
            {
                var field = new PasswordBox { PlaceholderText = Locale.Get("loginRecovery.codePlaceholder"), MaxLength = 4096, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(field, Locale.Get("loginRecovery.codePlaceholder"));
                Task SubmitCode() { if (job?.Process?.SendCode(field.Password) == true) field.Password = ""; return Task.CompletedTask; }
                field.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await SubmitCode(); } };
                loginRecoveryHost.Children.Add(new TextBlock { Text = Locale.Get("loginRecovery.codePrompt"), FontSize = 11 });
                loginRecoveryHost.Children.Add(field); loginRecoveryHost.Children.Add(owner.SafeButton(Locale.Get("loginRecovery.codeSubmit"), SubmitCode));
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (job is not null) buttons.Children.Add(owner.SafeButton(Locale.Get("loginRecovery.cancel"), () => { owner.CancelBackgroundLogin(provider); owner.RefreshLoginCards(); return Task.CompletedTask; }));
            else
            {
                buttons.Children.Add(owner.SafeButton(Locale.Get("loginRecovery.loginButton"), () => owner.StartBackgroundLogin(provider)));
                if (failure is not null) buttons.Children.Add(owner.SafeButton(Locale.Get("loginRecovery.terminalButton"), () => owner.TerminalLoginFallback(provider)));
                buttons.Children.Add(owner.SafeButton(Locale.Get("loginRecovery.resendButton"), () => owner.ResendLoginRequest(id)));
            }
            buttons.Children.Add(owner.SafeButton("×", () => { owner.DismissLoginRecovery(id); return Task.CompletedTask; }));
            AutomationProperties.SetName(buttons.Children[^1], Locale.Get("loginRecovery.dismiss"));
            loginRecoveryHost.Children.Add(buttons);
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
                await owner.StartFromComposer(request); return true;
            }
            finally { starting = false; if (QueuePaneAlive) Refresh(); }
        }
    }
}
