using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>
        /// The background work line's switches (MainWindow.BackgroundWork.cs), through the handlers its fold, its eye button
        /// and the pane's … menu use: the fold's open state is saved and the rows follow it, the eye hides the line in the
        /// default and the Mighty view and saves that, the menu item reads the saved value and brings the line back, and a
        /// hidden line comes back as it was left. The pane and both switches are put back after.
        /// </summary>
        internal async Task<Dictionary<string, object?>> RunBackgroundWorkToggleSmoke()
        {
            var original = Session; var state = owner.service.Snapshot;
            var work = new BackgroundWork([
                new BackgroundTask("smoke-line-1", "agent", "background line agent", Wire.Now()),
            ], TurnEnded: true);
            ToggleButton? Fold() => FindById<ToggleButton>(backgroundHost, "background-work-" + id);
            Button? Eye() => FindById<Button>(backgroundHost, "background-work-hide-" + id);
            bool Rows() => FindById<StackPanel>(backgroundHost, "background-tasks-" + id) is { IsLoaded: true };
            bool Shown() => backgroundHost is { Visibility: Visibility.Visible } && Fold() is { IsLoaded: true } && Eye() is { IsLoaded: true };
            bool Hidden() => backgroundHost is { Visibility: Visibility.Collapsed } && backgroundHost.Children.Count == 0;
            async Task<AppSnapshot> Saved(Func<AppSnapshot, bool> done)
            {
                var stored = await new StateStore(owner.StateDirectory).LoadAsync();
                for (var tries = 0; !done(stored) && tries < 100; tries++) { await Task.Delay(40); stored = await new StateStore(owner.StateDirectory).LoadAsync(); }
                return stored;
            }
            async Task View(string mode) { await Change(p => p with { AgentViewMode = mode }); Refresh(); RefreshMightyView(Session); }
            try
            {
                await owner.service.UpdateAsync(s => s with { ShowsBackgroundWork = null, BackgroundWorkOpen = null });
                await Change(p => p with { Provider = "claude", AgentViewMode = "default", MightyStyle = null, Status = "running", BackgroundWork = work });
                activeStyle = null; loadedStyleKey = null; Refresh(); RefreshMightyView(Session);
                await WaitUI(() => Shown() && !Rows(), () => $"older state must show the line folded; host {backgroundHost?.Visibility}, fold {Fold()?.IsLoaded}, eye {Eye()?.IsLoaded}, rows {Rows()}");
                var eye = Eye()!;
                Require(AutomationProperties.GetName(eye) == Locale.Get("plan.background.hideStrip") && Equals(ToolTipService.GetToolTip(eye), Locale.Get("plan.background.hideStripHelp")),
                    $"the eye button must be named '{Locale.Get("plan.background.hideStrip")}' and say where the line comes back; got '{AutomationProperties.GetName(eye)}', '{ToolTipService.GetToolTip(eye)}'");

                // The fold opens the line on its rows, and that is saved.
                await ToggleBackgroundWorkOpen();
                await WaitUI(() => Shown() && Rows() && Fold()?.IsChecked == true, () => $"the fold must open the line on its rows; rows {Rows()}, checked {Fold()?.IsChecked}");
                var stored = await Saved(s => s.BackgroundWorkOpen == true);
                Require(owner.service.Snapshot.BackgroundWorkOpen == true && stored.BackgroundWorkOpen == true, $"the open line must be saved; got {owner.service.Snapshot.BackgroundWorkOpen}, stored {stored.BackgroundWorkOpen}");
                var checks = new Dictionary<string, object?> { ["openSaved"] = true };

                // The eye hides it, in the default and the Mighty view, and that is saved; the pane keeps running.
                await HideBackgroundWork();
                await WaitUI(Hidden, () => $"the eye must hide the line; host {backgroundHost?.Visibility}, children {backgroundHost?.Children.Count}");
                await View("mighty");
                await WaitUI(Hidden, () => $"a hidden line must stay hidden in the Mighty view; host {backgroundHost?.Visibility}");
                stored = await Saved(s => s.ShowsBackgroundWork == false);
                Require(stored.ShowsBackgroundWork == false && stored.BackgroundWorkOpen == true, $"hiding must be saved and keep the open state; got shows={stored.ShowsBackgroundWork} open={stored.BackgroundWorkOpen}");
                Require(Session.Status == "running" && Session.BackgroundWork?.Running.Count == 1, $"hiding the line must leave the work and the pane alone; got {Session.Status}, {Session.BackgroundWork?.Running.Count} running");
                checks["hiddenBothViews"] = true; checks["hideSaved"] = true;

                // The … menu holds the switch, reads the saved value, and brings the line back open as it was left.
                var item = ((MenuFlyout)paneMenuButton!.Flyout).Items.OfType<ToggleMenuFlyoutItem>().SingleOrDefault(i => AutomationProperties.GetAutomationId(i) == "pane-menu-background-work-" + id);
                Require(item is not null && item.Text == Locale.Get("pane.menu.backgroundWork"), $"the pane menu must hold '{Locale.Get("pane.menu.backgroundWork")}'; got '{item?.Text}'");
                await ToggleBackgroundWorkShown();
                await WaitUI(() => Shown() && Rows(), () => $"the menu must bring the line back open; host {backgroundHost?.Visibility}, rows {Rows()}");
                stored = await Saved(s => s.ShowsBackgroundWork == true);
                Require(stored.ShowsBackgroundWork == true && owner.ShowsBackgroundWork, $"showing again must be saved; got stored {stored.ShowsBackgroundWork}");
                await View("default");
                await WaitUI(() => Shown() && Rows(), () => $"the line must show open in the default view again; host {backgroundHost?.Visibility}, rows {Rows()}");
                checks["menuShows"] = true; checks["showSaved"] = true; checks["openRemembered"] = true;

                // Settings → General → Display uses the same setter.
                await owner.Act(() => owner.SetShowsBackgroundWork(false));
                await WaitUI(Hidden, () => $"the settings switch's setter must hide the line; host {backgroundHost?.Visibility}");
                await owner.Act(() => owner.SetShowsBackgroundWork(true));
                await WaitUI(Shown, () => $"the settings switch's setter must show the line again; host {backgroundHost?.Visibility}");
                checks["settingsSetter"] = true;
                return checks;
            }
            finally
            {
                await owner.service.UpdateAsync(s => s with { ShowsBackgroundWork = state.ShowsBackgroundWork, BackgroundWorkOpen = state.BackgroundWorkOpen });
                await Change(p => p with { Provider = original.Provider, AgentViewMode = original.AgentViewMode, MightyStyle = original.MightyStyle, Status = original.Status, BackgroundWork = original.BackgroundWork });
                activeStyle = null; loadedStyleKey = null; Refresh(); RefreshMightyView(Session);
                await WaitUI(() => !styleLoading);
            }
        }
    }
}
