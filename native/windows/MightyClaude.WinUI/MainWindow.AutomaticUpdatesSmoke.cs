using MightyClaude.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, object?>> RunAutomaticUpdatesSmoke(Workspace workspace)
    {
        var saved = service.Snapshot;
        try
        {
            await service.UpdateAsync(s => s with { AutoUpdatePlugins = null });
            var section = BuildCliUpdateSection([]);
            // The switch sits at the trailing edge of its own row now; it is still found by its id.
            var toggle = SettingsElements(section).OfType<ToggleButton>().Single(t => AutomationProperties.GetAutomationId(t) == "plugin-auto-update");
            Require(toggle.IsChecked == true, "Plugin automatic updates must default on independently of CLI updates.");
            toggle.IsChecked = false; await WaitUI(() => service.Snapshot.AutoUpdatePlugins == false);
            var id = saved.Sessions.First(p => p.Provider == "claude" && p.Kind == "claude" && p.WorkspaceId == workspace.Id).Id;
            await SelectLayoutSession(id); Render(); var pane = views[id];
            await WaitUI(() => pane.Container.IsLoaded);
            var checks = await pane.SmokeAutomaticUpdateHolds();
            await pane.SmokeManualUpdateAdmission();
            checks["defaultAndPersistedPreference"] = true;
            checks["manualUpdateRefusesWithoutConsumingDrafts"] = true;
            checks["manualPluginMutationBlocksBothProviders"] = true;
            return checks;
        }
        finally { await service.UpdateAsync(_ => saved); Render(); }
    }

    private sealed partial class PaneView
    {
        internal async Task SmokeManualUpdateAdmission()
        {
            var original = Session; var draft = input.Text; var attachments = pendingAttachments.ToArray();
            var oldUpdater = owner.smokeCliUpdater; var oldStart = owner.smokeStart; var oldResults = owner.lastCliUpdateResults;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            try
            {
                pendingAttachments.Clear(); RefreshAttachments();
                await Change(p => p with { Status = "completed", MightyStyle = "cli" });
                owner.smokeStart = _ => { started++; return Task.CompletedTask; };
                owner.smokeCliUpdater = async (provider, token) => { if (provider == "claude") await release.Task.WaitAsync(token); return new(provider, "updated"); };
                Require(owner.coordinator.Start(), "Isolated manual updater must start.");
                await WaitUI(() => owner.manuallyUpdatingProvider == "claude");
                Require(owner.ManualMutationBlockReason(Session) is not null && owner.ManualMutationBlockReason(Session with { Provider = "codex" }) is null,
                    "Manual CLI update must block only its active provider.");
                updating = true; input.Text = "keep blocked draft"; updating = false; RefreshComposerState(); await Send();
                Require(input.Text == "keep blocked draft" && !send.IsEnabled && started == 0 && queuedInputs.Items.Count == 0, "Blocked local send must keep its draft and avoid queueing or launching.");
                var refused = false;
                try { await MobileSubmit("keep blocked phone request", "queue", [], CancellationToken.None); } catch (MobileRequestException) { refused = true; }
                Require(refused && queuedInputs.Items.Count == 0 && started == 0, "Blocked mobile request must be refused before queue admission.");
                queuedInputs.Add("keep blocked queued request", []); await StartNextQueuedInput();
                Require(queuedInputs.Items.Count == 1 && started == 0, "Blocked queue start must preserve the queued item.");
                queuedInputs.Clear(); queueDrainTimer.Stop(); release.TrySetResult(); await WaitUI(() => !owner.coordinator.IsUpdating);

                var browser = new ClaudePluginBrowser("claude", Workspace); browser.Apply(PluginSmokeSnapshot); browser.Scope = "user";
                var reader = new FakeMarketplaceReader(PluginSmokeSnapshot, holdUntilCancelled: true);
                using var cancellation = new CancellationTokenSource();
                var mutation = owner.pluginOperations.InstallAsync(browser, reader, "docs@sample", cancellation.Token);
                try
                {
                    Require(owner.pluginOperations.IsRunning && owner.ManualMutationBlockReason(Session) is not null
                        && owner.ManualMutationBlockReason(Session with { Provider = "codex" }) is not null
                        && owner.ManualMutationBlockReason(Session with { Provider = "gemini" }) is null,
                        "A manual plugin mutation must block Claude and Codex only.");
                    await Send(); Require(input.Text == "keep blocked draft" && started == 0, "Plugin mutation must preserve the pending local draft.");
                    refused = false;
                    try { await MobileSubmit("keep blocked phone request", "queue", [], CancellationToken.None); } catch (MobileRequestException) { refused = true; }
                    Require(refused && queuedInputs.Items.Count == 0, "Plugin mutation must refuse phone input without consuming it.");
                }
                finally { cancellation.Cancel(); await mutation; reader.Shutdown(); }
            }
            finally
            {
                release.TrySetResult(); await owner.coordinator.CancelAsync(); queueDrainTimer.Stop(); queuedInputs.Clear();
                owner.smokeCliUpdater = oldUpdater; owner.smokeStart = oldStart; owner.lastCliUpdateResults = oldResults;
                await owner.service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? original : p).ToList() });
                pendingAttachments.Clear(); pendingAttachments.AddRange(attachments); RefreshAttachments();
                updating = true; input.Text = draft; updating = false; RenderQueuedInputs(); Refresh();
            }
        }

        internal async Task<Dictionary<string, object?>> SmokeAutomaticUpdateHolds()
        {
            var original = Session; var draft = input.Text; var attachments = pendingAttachments.ToArray(); var start = owner.smokeStart;
            var previousRunning = owner.automaticUpdateRunning; var previousProvider = owner.automaticallyUpdatingProvider;
            var calls = new List<string>();
            Require(queuedInputs.Items.Count == 0, "Automatic-update fixture requires an empty isolated queue.");
            try
            {
                pendingAttachments.Clear(); RefreshAttachments();
                await Change(p => p with { Status = "completed", MightyStyle = "cli", ResumeId = null });
                owner.smokeStart = request => { calls.Add(request.Input); return Task.CompletedTask; };
                owner.automaticUpdateRunning = true; owner.automaticallyUpdatingProvider = "claude";
                owner.NotifyAutomaticUpdates();
                Require(owner.BackgroundUpdateHolds(Session) && !owner.BackgroundUpdateHolds(Session with { Provider = "codex" }), "An update must hold only its own provider.");
                updating = true; input.Text = "local held instruction"; updating = false;
                RefreshComposerState(); await Send();
                Require(queuedInputs.Items.Count == 1 && input.Text.Length == 0 && calls.Count == 0, "Local send must preserve input in the queue while plugins update.");
                Require(await MobileSubmit("mobile held instruction", "queue", [], CancellationToken.None) == "queued" && queuedInputs.Items.Count == 2,
                    "Mobile send must use the same provider hold and queue.");
                await StartNextQueuedInput();
                Require(calls.Count == 0 && !owner.ReadyForAutomaticUpdate("claude"), "Queued requests must neither start under a hold nor admit another idle update.");
                queueDrainTimer.Stop(); owner.automaticUpdateRunning = false; owner.automaticallyUpdatingProvider = null;
                owner.NotifyAutomaticUpdates();
                Require(queueDrainTimer.IsEnabled, "Releasing an update must schedule queued input draining.");
                queueDrainTimer.Stop(); await StartNextQueuedInput(); await StartNextQueuedInput();
                Require(calls.SequenceEqual(["local held instruction", "mobile held instruction"]) && queuedInputs.Items.Count == 0, "Released local and mobile inputs must retain FIFO order.");
                owner.automaticUpdateRunning = true; owner.automaticallyUpdatingProvider = "claude";
                await MobileSubmit("cancel held instruction", "queue", [], CancellationToken.None);
                await StopActiveRun();
                Require(queuedInputs.Items.Count == 0 && !queueDrainTimer.IsEnabled && calls.Count == 2, "Stop must discard held requests without starting a CLI.");
                return new() { ["sameProviderLocalAndMobileHeld"] = true, ["releasedQueuePreservesOrder"] = true, ["stopCancelsHeldRequests"] = true };
            }
            finally
            {
                queueDrainTimer.Stop(); queuedInputs.Clear(); owner.smokeStart = start;
                owner.automaticUpdateRunning = previousRunning; owner.automaticallyUpdatingProvider = previousProvider;
                await owner.service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? original : p).ToList() });
                pendingAttachments.Clear(); pendingAttachments.AddRange(attachments); RefreshAttachments();
                updating = true; input.Text = draft; updating = false; RenderQueuedInputs(); Refresh();
            }
        }
    }
}
