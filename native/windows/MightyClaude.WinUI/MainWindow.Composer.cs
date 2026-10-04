using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private void ArrangeComposer()
        {
            // WinUI can finish a layout pass after its pane was removed from
            // the snapshot. A detached editor must not resolve a live session.
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null) return;
            var width = selectors.ActualWidth;
            var capabilities = owner.Runtime(pane.Provider)?.Capabilities ?? ProviderCatalog.Capabilities(pane.Provider);
            composerMode = width >= 660 ? 2 : width >= 430 ? 1 : 0;
            if (pane.Kind == "shell") { provider.Visibility = model.Visibility = effort.Visibility = permission.Visibility = fast.Visibility = more.Visibility = Visibility.Collapsed; return; }
            provider.Width = composerMode == 2 ? 90 : 72; provider.Visibility = width >= 250 ? Visibility.Visible : Visibility.Collapsed;
            effort.Width = composerMode == 2 ? 80 : 66; permission.Width = composerMode == 2 ? 110 : 90; fast.Width = 58; more.Width = 32;
            effort.Visibility = composerMode > 0 && (capabilities.Effort || pane.Settings.Effort != "default") ? Visibility.Visible : Visibility.Collapsed;
            permission.Visibility = composerMode > 0 ? Visibility.Visible : Visibility.Collapsed;
            fast.Visibility = composerMode == 2 && pane.Provider == "codex" && (capabilities.FastMode || pane.Settings.FastMode) ? Visibility.Visible : Visibility.Collapsed;
            model.Visibility = more.Visibility = Visibility.Visible;
        }
        private Task ChangeProvider(string value) => owner.Act(async () =>
        {
            if (Session.Status == "running" || starting || Session.Provider == value) return;
            await Change(p => p with { Provider = value, Title = p.Title == ProviderCatalog.Name(p.Provider) ? ProviderCatalog.Name(value) : p.Title, Model = "default", Settings = new(), ResumeId = null }); Refresh(); input.Focus(FocusState.Programmatic);
        });
        private void AddOverflowSettings(MenuFlyout menu, RunSession pane, ProviderCapabilities caps)
        {
            if (pane.Kind != "claude") return;
            var providers = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.runner") };
            foreach (var value in Wire.Providers) providers.Items.Add(Item(ProviderCatalog.BetaLabel(value, ProviderCatalog.Name(value)), () => ChangeProvider(value), pane.Provider == value)); menu.Items.Add(providers);
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var models = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.model") };
            models.Items.Add(Item(Locale.Get("composer.model.refresh"), RefreshPaneModels));
            foreach (var value in ModelLabel.PickerOptions(pane, catalog)) models.Items.Add(Item(value.DisplayName, () => ChangeModel(value.Value), value.Value == pane.Model));
            if (pane.Provider != "gemini") models.Items.Add(Item(Locale.Get("composer.model.enterIdMenu"), CustomModel)); menu.Items.Add(models);
            if (caps.Effort || pane.Settings.Effort != "default")
            {
                var strengths = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.effort") };
                var registeredModels = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot);
                foreach (var value in new[] { "default" }.Concat(ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels))) strengths.Items.Add(Item(value == "default" ? Locale.Get("composer.effort.auto") : value, () => ChangeSettings(s => s with { Effort = value }), value == pane.Settings.Effort));
                menu.Items.Add(strengths);
            }
            var permissions = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.permission") };
            foreach (var value in (caps.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains)) permissions.Items.Add(Item(PermissionLabel(pane.Provider, value), () => ChangeSettings(s => s with { PermissionMode = value, NetworkAccess = pane.Provider == "codex" && value is ("acceptEdits" or "onRequest") && s.NetworkAccess }), pane.Settings.PermissionMode == value, PermissionHelp(pane.Provider, value)));
            menu.Items.Add(permissions);
            if (pane.Provider == "codex" && (caps.FastMode || pane.Settings.FastMode)) menu.Items.Add(Item("Fast", () => ChangeSettings(s => s with { FastMode = !s.FastMode && caps.FastMode }), pane.Settings.FastMode));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        /// <summary>
        /// A ComposerPill's look (M/ComposerControls.swift:21-26): <c>card</c> with a 1pt <c>line</c> and
        /// <c>ink</c>, or, while its setting is on, <c>accentSoft</c> with accent × 0.35 and <c>accent</c>;
        /// no change under the pointer, <c>ink3</c> while disabled. It is drawn on the pill's face, so it
        /// may change at any time; the button's own resources are written once (<see cref="InitializePills"/>).
        /// </summary>
        private void PaintPill(ContentControl pill, bool active)
        {
            var b = owner.brushes; var face = (Border)pill.Content;
            if (active) activePills.Add(pill); else activePills.Remove(pill);
            face.Background = b.Brush(active ? DesignToken.AccentSoft : DesignToken.Card);
            face.BorderBrush = active ? b.Brush(DesignToken.Accent, DesignMetrics.Opacity.PillActiveBorder) : b.Brush(DesignToken.Line);
            var ink = b.Brush(!pill.IsEnabled ? DesignToken.Ink3 : active ? DesignToken.Accent : DesignToken.Ink);
            if (face.Child is TextBlock words) words.Foreground = ink;
            else if (face.Child is IconElement icon) icon.Foreground = ink;
        }

        /// <summary>
        /// Gives every pill its fixed resources once, before it enters the tree: no fill or edge of the
        /// button's own in any state and the subtle wash under the pointer (hidden by the opaque face,
        /// so the pill does not change there, as on the Mac). Their faces follow their state:
        /// enablement for all, the setting for the permission pill, the check for Fast (M/SessionPaneView.swift:408).
        /// </summary>
        private void InitializePills()
        {
            var b = owner.brushes;
            foreach (var pill in new[] { attach, provider, model, effort, permission, more })
            {
                owner.PaintPlainButton(pill, b.Transparent, b.Subtle);
                pill.IsEnabledChanged += (_, _) => PaintPill(pill, activePills.Contains(pill));
                PaintPill(pill, false);
            }
            var values = new List<(string, object)>();
            foreach (var state in new[] { "", "Disabled", "Checked", "CheckedDisabled" }) { values.Add(("ToggleButtonBackground" + state, b.Transparent)); values.Add(("ToggleButtonBorderBrush" + state, b.Transparent)); }
            foreach (var state in new[] { "PointerOver", "Pressed", "CheckedPointerOver", "CheckedPressed" }) { values.Add(("ToggleButtonBackground" + state, b.Subtle)); values.Add(("ToggleButtonBorderBrush" + state, b.Transparent)); }
            owner.SetResourcesOnce(fast, values);
            fast.Background = b.Transparent; fast.BorderBrush = b.Transparent;
            void PaintFast() => PaintPill(fast, fast.IsChecked == true);
            fast.Checked += (_, _) => PaintFast(); fast.Unchecked += (_, _) => PaintFast(); fast.IsEnabledChanged += (_, _) => PaintFast();
            PaintFast();
        }

        /// <summary>
        /// The shape under the send button (M/SessionPaneView.swift:737-760): the 32pt circle in
        /// <c>run</c> while there is something to send and <c>track</c> while not; the 32pt <c>err</c>
        /// square (r8) while it stops the run. The symbol is <c>onStatus</c>; a disabled send arrow is
        /// <c>ink2</c>, while the stop square keeps <c>onStatus</c> as it stops. Both live on the shape
        /// and the symbol, never in the button's resources.
        /// </summary>
        private void PaintSend()
        {
            var b = owner.brushes;
            sendDisc.CornerRadius = new CornerRadius(sendIsStop ? DesignMetrics.Radius.Row : 16);
            sendDisc.Background = b.Brush(sendIsStop ? DesignToken.Err : send.IsEnabled ? DesignToken.Run : DesignToken.Track);
            sendGlyph.Foreground = b.Brush(sendIsStop || send.IsEnabled ? DesignToken.OnStatus : DesignToken.Ink2);
        }

        /// <summary>
        /// The composer card's edge (M/SessionPaneView.swift:655): 1pt <c>line</c>, or accent × 0.8 at 1.5pt
        /// while the editor has focus or files are dragged over it. The padding gives back the extra half
        /// point so the contents never move.
        /// </summary>
        private void PaintComposerRing()
        {
            var ring = composerFocused || composerDropTargeted;
            var stroke = ring ? DesignMetrics.Stroke.Focus : DesignMetrics.Stroke.Line; var give = stroke - DesignMetrics.Stroke.Line;
            composerCard.BorderBrush = ring ? owner.brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.ComposerFocus) : owner.brushes.Brush(DesignToken.Line);
            composerCard.BorderThickness = new Thickness(stroke);
            composerCard.Padding = new Thickness(10 - give, 2 - give, 10 - give, 10 - give);
        }

        /// <summary>The editor draws no box of its own in any state: the card is its frame. The placeholder is <c>ink2</c>.</summary>
        private void StyleComposerInput()
        {
            foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
            {
                input.Resources["TextControlBackground" + state] = owner.brushes.Transparent;
                input.Resources["TextControlBorderBrush" + state] = owner.brushes.Transparent;
                input.Resources["TextControlPlaceholderForeground" + state] = owner.brushes.Brush(DesignToken.Ink2);
            }
            input.Background = owner.brushes.Transparent;
        }

        /// <summary>The run clock for the pane's session as already read (the 1-second tick reads the snapshot once).</summary>
        internal void RefreshElapsed(RunSession pane)
        {
            elapsed.Text = pane.Kind == "shell" ? "" : pane.RunTiming?.Label() ?? "";
            RefreshSessionInfo();
        }

    }
}
