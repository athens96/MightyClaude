using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // One catalogue per provider and workspace, shared by every pane and kept
    // for 30 s, exactly as AppStore+SlashCommands.swift does. Scanning reads the
    // disk, so it never happens on the UI thread and never starts a CLI.
    private readonly SlashCatalogCache slashCatalogs = new();
    private readonly HashSet<string> slashScansInFlight = [];
    // Smoke mode hands the palette a fixture list instead of scanning the disk.
    private SlashCommand[]? smokeSlashCommands;

    private sealed partial class PaneView
    {
        // The completion list above the composer (macOS SlashCommandPalette.swift).
        // This layer draws the state SlashPalette produced and forwards keys; the
        // open/closed decision, the filtering and the highlight all live in Core.
        private const double SlashRowHeight = 40, SlashVisibleRows = 8;
        private readonly StackPanel slashRows = new();
        private ScrollViewer slashScroll = null!;
        private readonly TextBlock slashCount = new() { FontSize = 10, Opacity = .5, VerticalAlignment = VerticalAlignment.Center };
        private Border slashPaletteHost = null!;
        private SlashPaletteState paletteState = SlashPaletteState.Closed;
        private string? slashDismissedFor;

        /// <summary>Builds the slot the composer reserves above the input.</summary>
        private void InitSlashPalette()
        {
            slashScroll = new ScrollViewer
            {
                Content = slashRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
            };
            static TextBlock Hint(string text) => new() { Text = text, FontSize = 10, Opacity = .6, VerticalAlignment = VerticalAlignment.Center };
            var footer = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 5, 12, 5) };
            footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            hints.Children.Add(Hint(SlashCommandStrings.PaletteMove));
            hints.Children.Add(Hint(SlashCommandStrings.PaletteSelect));
            hints.Children.Add(Hint(SlashCommandStrings.PaletteDismiss));
            footer.Children.Add(hints);
            Grid.SetColumn(slashCount, 2); footer.Children.Add(slashCount);
            var body = new StackPanel();
            body.Children.Add(slashScroll);
            body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 135, 135, 135)) });
            body.Children.Add(footer);
            var host = new Border
            {
                Child = body, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(75, 135, 135, 135)),
                Background = new SolidColorBrush(Color.FromArgb(240, 32, 32, 32)),
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6),
            };
            AutomationProperties.SetAutomationId(host, "slash-palette-" + id);
            slashPaletteHost = host;
            // The Mighty view needs the pane's own grid and header, which exist
            // only once the pane is in the visual tree. This composer slot is
            // built here and loads with the pane, so it is the anchor the view
            // waits on (MainWindow.MightyGraph.cs).
            AttachMightyView(host);
        }

        /// <summary>The catalogue key this pane scans under, mirroring slashCatalogKey.</summary>
        private string? SlashWorkspacePath => Workspace.Remote is null ? Workspace.Path : null;

        /// <summary>
        /// Recomputes the palette for the current draft and draws it. Called on
        /// every keystroke; when the draft is a query the catalogue is refreshed
        /// off the UI thread first, the way macOS refreshes on paletteDraft.
        /// </summary>
        internal void RefreshPalette(string draft)
        {
            var pane = Session;
            SlashCommand[] scanned = owner.smokeSlashCommands ?? owner.slashCatalogs.Get(pane.Provider, SlashWorkspacePath) ?? [];
            var next = SlashPalette.State(pane.Provider, pane.Kind, draft, slashDismissedFor, scanned, ArgumentChoices);
            // A new row set restarts the highlight at the top, as macOS does.
            if (!next.Commands.Select(c => c.Id).SequenceEqual(paletteState.Commands.Select(c => c.Id)))
                paletteState = next;
            else paletteState = next with { HighlightedIndex = paletteState.SafeIndex };
            RenderSlashPalette();
            // The Mighty canvas carries a draft block, so it follows the composer.
            RefreshDraftBlock();
            if (SlashPalette.Draft(pane.Kind, draft, slashDismissedFor) is not null) ScanSlashCommands();
        }

        // The choices a built-in's argument completes into, from the same
        // runtime catalogue and permission modes the composer's buttons use.
        private SlashCommand[] ArgumentChoices(SlashArgument argument, string command)
        {
            var pane = Session;
            if (argument == SlashArgument.Model)
            {
                var catalog = owner.Runtime(pane.Provider, pane.WorkspaceId)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
                var options = catalog.Models.Select(m => (m.Value, m.DisplayName)).ToList();
                if (!options.Any(o => o.Value == pane.Model)) options.Add((pane.Model, pane.Model));
                return SlashPalette.ModelChoices(command, options, pane.Model);
            }
            var modes = (Capabilities.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains);
            return SlashPalette.PermissionChoices(command, modes, pane.Settings.PermissionMode, mode => PermissionLabel(pane.Provider, mode));
        }

        /// <summary>Draws one row per command, at most eight of them visible.</summary>
        private void RenderSlashPalette()
        {
            if (!paletteState.IsOpen) { slashPaletteHost.Visibility = Visibility.Collapsed; slashRows.Children.Clear(); return; }
            slashRows.Children.Clear();
            var accent = new SolidColorBrush(Colors.CornflowerBlue);
            for (var index = 0; index < paletteState.Commands.Length; index++)
            {
                var command = paletteState.Commands[index];
                var highlighted = index == paletteState.SafeIndex;
                slashRows.Children.Add(SlashRow(command, highlighted, index, accent));
            }
            slashScroll.MaxHeight = Math.Min(paletteState.Commands.Length, SlashVisibleRows) * SlashRowHeight;
            slashCount.Text = SlashPalette.CountLabel(paletteState.Commands.Length);
            slashPaletteHost.Visibility = Visibility.Visible;
            // Keep the highlighted row on screen the way the macOS list does.
            if (slashRows.Children.ElementAtOrDefault(paletteState.SafeIndex) is FrameworkElement row) row.StartBringIntoView();
        }

        private Border SlashRow(SlashCommand command, bool highlighted, int index, Brush accent)
        {
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 6, 12, 6) };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var invocation = new TextBlock
            {
                Text = "/" + command.Invocation, FontSize = 12, FontFamily = new FontFamily("Consolas"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
            };
            grid.Children.Add(invocation);
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = SlashPalette.Description(command), FontSize = 11, Opacity = highlighted ? .9 : .7, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = command.Source, FontSize = 9, Opacity = highlighted ? .75 : .55, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            // ↵ runs in the app, › continues with the argument choices.
            var mark = command.Action is not null ? "↵" : command.Argument is not null ? "›" : null;
            if (mark is not null)
            {
                var glyph = new TextBlock { Text = mark, FontSize = 11, Opacity = highlighted ? .75 : .55, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(glyph, command.Action is not null ? SlashCommandStrings.PaletteActionTooltip : SlashCommandStrings.PaletteArgumentTooltip);
                Grid.SetColumn(glyph, 2); grid.Children.Add(glyph);
            }
            var row = new Border { Child = grid, Background = highlighted ? accent : new SolidColorBrush(Colors.Transparent), CornerRadius = new CornerRadius(6) };
            AutomationProperties.SetAutomationId(row, "slash-command-" + command.Invocation);
            AutomationProperties.SetName(row, "/" + command.Invocation + " " + SlashPalette.Description(command) + " " + command.Source);
            row.PointerEntered += (_, _) => HighlightSlashRow(index);
            row.Tapped += async (_, args) => { args.Handled = true; await ChooseSlashCommand(command); };
            return row;
        }

        private void HighlightSlashRow(int index)
        {
            if (!paletteState.IsOpen || index == paletteState.SafeIndex) return;
            paletteState = paletteState with { HighlightedIndex = index };
            RenderSlashPalette();
        }

        /// <summary>
        /// Up, Down, Enter, Tab and Esc belong to an open palette: they move the
        /// highlight or choose, and never submit the prompt or move the caret.
        /// Returns false when the key should reach the composer unchanged.
        /// </summary>
        private bool HandlePaletteKey(Windows.System.VirtualKey key)
        {
            if (!paletteState.IsOpen || composingInput) return false;
            switch (key)
            {
                case Windows.System.VirtualKey.Up: paletteState = paletteState.MoveUp(); break;
                case Windows.System.VirtualKey.Down: paletteState = paletteState.MoveDown(); break;
                case Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Tab:
                    // Shift+Enter is still a newline and Shift+Tab still leaves the field.
                    if (IsInputKeyDown(Windows.System.VirtualKey.Shift)) return false;
                    if (paletteState.Highlighted is { } choice) _ = ChooseSlashCommand(choice);
                    return true;
                case Windows.System.VirtualKey.Escape:
                    // Esc closes the list for this draft and leaves the draft alone.
                    slashDismissedFor = input.Text; paletteState = SlashPaletteState.Closed; break;
                default: return false;
            }
            RenderSlashPalette();
            return true;
        }

        /// <summary>
        /// A plain command is inserted as "/name "; a built-in runs in the app and
        /// clears the draft; a built-in that takes an argument leaves "/name " and
        /// keeps the list open on its choices.
        /// </summary>
        private Task ChooseSlashCommand(SlashCommand command) => owner.Act(async () =>
        {
            var choice = SlashPaletteState.Choose(command);
            if (choice.Effect == SlashChoiceEffect.AppAction)
            {
                if (!await PerformSlashAction(choice.Action!.Value, choice.ActionArg)) return;
                slashDismissedFor = null;
            }
            else slashDismissedFor = choice.Effect == SlashChoiceEffect.ArgumentCompletion ? null : choice.Draft;
            // Refresh before the async draft save so the palette closes (or
            // reopens for argument completion) synchronously on this dispatcher
            // frame, before WaitUI in the smoke check can observe the draft.
            RefreshPalette(choice.Draft);
            if (!owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
            await SetDraft(choice.Draft);
            input.Focus(FocusState.Programmatic);
        });

        private async Task SetDraft(string text)
        {
            updating = true; input.Text = text; input.SelectionStart = text.Length; updating = false;
            RefreshComposerState();
            await Change(p => p with { Draft = text });
        }

        /// <summary>
        /// Runs a built-in for this pane. Anything that cannot happen right now is
        /// explained in the pane's log rather than silently ignored, as on macOS.
        /// </summary>
        private async Task<bool> PerformSlashAction(SlashCommandAction action, string? argument)
        {
            var pane = Session;
            if (pane.Kind == "shell") return false;
            var running = pane.Status == "running" || starting;
            switch (action)
            {
                case SlashCommandAction.NewConversation:
                    if (running) await SlashNote(SlashCommandStrings.NoteNewConversationRunning);
                    else if (pane.ResumeId is null) await SlashNote(SlashCommandStrings.NoteNewConversationNothingToResume);
                    else { await Change(p => p with { ResumeId = null }); Refresh(); }
                    break;
                case SlashCommandAction.OpenPlugins: await owner.OpenPluginBrowser(pane.Provider); break;
                case SlashCommandAction.ShowUsage: await ShowContext(); break;
                case SlashCommandAction.OpenSettings: await owner.OpenSettings(); break;
                case SlashCommandAction.Rename: await owner.RenameSession(id); break;
                case SlashCommandAction.Help: await SlashNote(SlashCommandCatalog.HelpText(pane.Provider)); break;
                case SlashCommandAction.SetModel:
                {
                    if (argument is not { } model) return false;
                    if (running) { await SlashNote(SlashCommandStrings.NoteModelRunning); break; }
                    var catalog = owner.Runtime(pane.Provider, pane.WorkspaceId)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
                    var name = catalog.Models.FirstOrDefault(m => m.Value == model)?.DisplayName ?? model;
                    if (pane.Model == model) { await SlashNote(SlashCommandStrings.NoteModelAlreadyTemplate.Replace("{name}", name)); break; }
                    await ChangeModel(model);
                    await SlashNote(SlashCommandStrings.NoteModelChangedTemplate.Replace("{name}", name).Replace("{particle}", KoreanParticle.Ro(name)));
                    break;
                }
                case SlashCommandAction.SetPermission:
                {
                    if (argument is not { } mode) return false;
                    var label = PermissionLabel(pane.Provider, mode);
                    if (running) { await SlashNote(SlashCommandStrings.NotePermissionRunning); break; }
                    if (pane.Settings.PermissionMode == mode) { await SlashNote(SlashCommandStrings.NotePermissionAlreadyTemplate.Replace("{label}", label)); break; }
                    await ChangeSettings(s => s with { PermissionMode = mode, NetworkAccess = pane.Provider == "codex" && mode == "acceptEdits" && s.NetworkAccess });
                    if (Session.Settings.PermissionMode == mode)
                        await SlashNote(SlashCommandStrings.NotePermissionChangedTemplate.Replace("{label}", label).Replace("{particle}", KoreanParticle.Ro(label)));
                    break;
                }
                // Actions without a Windows screen never reach here: SlashPalette
                // leaves their built-ins out (docs/windows-slash-commands.md).
                default: return false;
            }
            return true;
        }

        private Task SlashNote(string text) =>
            Change(p => p with { Logs = [.. p.Logs, new LogEntry(Wire.Id(), "system", text, Wire.Now(), p.Provider)] });

        /// <summary>
        /// Rescans this pane's commands when the cache is missing or older than
        /// 30 s. The scan reads the disk, so it runs on the thread pool.
        /// </summary>
        private void ScanSlashCommands()
        {
            var pane = Session;
            if (pane.Kind == "shell" || owner.smokeSlashCommands is not null) return;
            var provider = pane.Provider; var path = SlashWorkspacePath;
            if (!owner.slashCatalogs.IsStale(provider, path)) return;
            var key = provider + "|" + (path ?? "");
            if (!owner.slashScansInFlight.Add(key)) return;
            _ = ScanAsync(provider, path, key);
        }

        private async Task ScanAsync(string provider, string? path, string key)
        {
            try
            {
                var commands = await Task.Run(() => SlashCommandCatalog.Commands(provider, path));
                if (!owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
                owner.slashCatalogs.Set(provider, path, commands);
                RefreshPalette(input.Text);
            }
            catch (Exception) { /* A pane with no catalogue still shows its built-ins. */ }
            finally { owner.slashScansInFlight.Remove(key); }
        }
    }
}
