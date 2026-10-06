using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        /// <summary>A row's quieter inks (M/SlashCommandPalette.swift:53-64): the description and the source on the highlighted row, the source on the others.</summary>
        private const double SlashSaid = 0.9, SlashQuietOn = 0.75, SlashQuiet = 0.7;
        private readonly StackPanel slashRows = new();
        private ScrollViewer slashScroll = null!;
        private readonly TextBlock slashCount = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
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
            // The palette is a card with a line border, r10; the hints ink2, the count in the tertiary ink (M/SlashCommandPalette.swift:34-43).
            var ink2 = owner.brushes.Brush(DesignToken.Ink2); slashCount.Foreground = owner.brushes.Tertiary;
            TextBlock Hint(string text) => new() { Text = text, FontSize = 10, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
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
            body.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = owner.brushes.Brush(DesignToken.Line) });
            body.Children.Add(footer);
            var host = new Border
            {
                Child = body, CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
                BorderBrush = owner.brushes.Brush(DesignToken.Line),
                Background = owner.brushes.Brush(DesignToken.Card),
                // Padding h10 t10 in the composer's stack (M/SessionPaneView.swift:562).
                Visibility = Visibility.Collapsed, Margin = new Thickness(10, 10, 10, 0),
            };
            AutomationProperties.SetAutomationId(host, "slash-palette-" + id);
            slashPaletteHost = host;
            // The Mighty view needs the pane's own grid and header, which exist
            // only once the pane is in the visual tree. This composer slot is
            // built here and loads with the pane, so it is the anchor the view
            // waits on (MainWindow.MightyGraph.cs).
            AttachMightyView(host);
            // The browser view needs the same anchor: a browser pane replaces the pane's
            // transcript and composer once the pane is in the visual tree
            // (MainWindow.Browser.cs).
            AttachBrowserView(host);
        }

        /// <summary>The catalogue key this pane scans under, mirroring slashCatalogKey.</summary>
        private string? SlashWorkspacePath => Workspace.Path;

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
                var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
                return SlashPalette.ModelChoices(command, ModelLabel.PickerOptions(pane, catalog).Select(m => (m.Value, m.DisplayName)), pane.Model);
            }
            var modes = (Capabilities.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains);
            return SlashPalette.PermissionChoices(command, modes, pane.Settings.PermissionMode, mode => PermissionLabel(pane.Provider, mode));
        }

        /// <summary>Draws one row per command, at most eight of them visible.</summary>
        private void RenderSlashPalette()
        {
            if (!paletteState.IsOpen) { slashPaletteHost.Visibility = Visibility.Collapsed; slashRows.Children.Clear(); return; }
            slashRows.Children.Clear();
            var accent = owner.brushes.Brush(DesignToken.Accent);
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
                Text = "/" + command.Invocation, FontSize = 12, FontFamily = new FontFamily(DesignMetrics.Font.Mono),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
            };
            grid.Children.Add(invocation);
            // The highlighted row is accent behind onAccent words (the description × 0.9, the source and the mark × 0.75);
            // the others ink, ink2 and ink2 × 0.7 (macOS SlashCommandPalette.swift:49-69).
            var b = owner.brushes;
            invocation.Foreground = b.Brush(highlighted ? DesignToken.OnAccent : DesignToken.Ink);
            var said = highlighted ? b.Brush(DesignToken.OnAccent, SlashSaid) : b.Brush(DesignToken.Ink2);
            var quiet = highlighted ? b.Brush(DesignToken.OnAccent, SlashQuietOn) : b.Brush(DesignToken.Ink2, SlashQuiet);
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = SlashPalette.Description(command), FontSize = 11, Foreground = said, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = command.Source, FontSize = 9, Foreground = quiet, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            // The return mark runs in the app, the chevron continues with the argument choices: 9pt semibold symbols at the row's end.
            if (command.Action is not null || command.Argument is not null)
            {
                var mark = command.Action is not null
                    ? ComposerGlyph.Icon("", 9, 12, 12, Microsoft.UI.Text.FontWeights.SemiBold).Ink(quiet).View
                    : ComposerGlyph.ChevronRight(9, 1.3).Ink(quiet).View;
                // The symbol alone would be a tiny tooltip target: its transparent box takes the pointer.
                var holder = new Border { Child = mark, Background = b.Transparent, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(holder, command.Action is not null ? SlashCommandStrings.PaletteActionTooltip : SlashCommandStrings.PaletteArgumentTooltip);
                Grid.SetColumn(holder, 2); grid.Children.Add(holder);
            }
            var row = new Border { Child = grid, Background = highlighted ? accent : b.Transparent };
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
                    else await ResetConversation();
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
                    var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
                    var name = ModelLabel.Selection(pane with { Model = model }, catalog);
                    if (pane.Model == model) { await SlashNote(SlashCommandStrings.NoteModelAlreadyTemplate.Replace("{name}", name)); break; }
                    await ChangeModel(model);
                    await SlashNote(Locale.Get("slash.note.modelChanged", new Dictionary<string, string> { ["name"] = name }));
                    break;
                }
                case SlashCommandAction.SetPermission:
                {
                    if (argument is not { } mode) return false;
                    var label = PermissionLabel(pane.Provider, mode);
                    if (running) { await SlashNote(SlashCommandStrings.NotePermissionRunning); break; }
                    if (pane.Settings.PermissionMode == mode) { await SlashNote(SlashCommandStrings.NotePermissionAlreadyTemplate.Replace("{label}", label)); break; }
                    await ChangeSettings(s => s with { PermissionMode = mode, NetworkAccess = pane.Provider == "codex" && mode is ("acceptEdits" or "onRequest") && s.NetworkAccess });
                    if (Session.Settings.PermissionMode == mode)
                        await SlashNote(Locale.Get("slash.note.permissionChanged", new Dictionary<string, string> { ["label"] = label }));
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
