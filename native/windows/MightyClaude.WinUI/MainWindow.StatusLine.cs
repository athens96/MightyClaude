using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly HashSet<Task> statusLineClosures = [];
    private void CloseStatusLine(PaneView pane)
    {
        var task = pane.CloseStatusLine(); statusLineClosures.Add(task);
        _ = task.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => statusLineClosures.Remove(task)), TaskScheduler.Default);
    }
    private async Task ShutdownStatusLines()
    {
        foreach (var pane in views.Values) CloseStatusLine(pane);
        await Task.WhenAll(statusLineClosures.ToArray()); statusLineClosures.Clear();
    }
    private async Task SetStatusLineEnabled(bool enabled)
    {
        await service.UpdateAsync(snapshot => snapshot with { StatusLineEnabled = enabled });
        foreach (var pane in views.Values) pane.RequestStatusLineRefresh(force: enabled);
    }

    private sealed partial class PaneView
    {
        // Claude's statusLine under the composer (macOS StatusLineView.swift).
        // This layer only draws the state Core produced and forwards the answer
        // to the trust question; discovery, trust and execution live in Core.
        private readonly StackPanel statusLineHost = new() { Spacing = 2, Visibility = Visibility.Collapsed };
        private StatusLineConfig? statusLineUntrusted;
        private StatusLineRefresher? _refresher;
        private Task statusLineStopping = Task.CompletedTask;
        private bool statusLineRestartPending;
        private readonly Microsoft.UI.Xaml.Controls.Primitives.ToggleButton statusLineToggle = new()
        {
            // The toggle sits in the composer's right cluster in a 16pt-wide, toolbar-high frame (M/SessionPaneView.swift:123-133, 736).
            Width = 16, Height = DesignMetrics.Layout.Toolbar, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow),
        };

        internal StatusLineRefresher? Refresher => _refresher;
        internal Task CloseStatusLine()
        {
            if (_refresher is { } previous) { previous.Close(); statusLineStopping = Task.WhenAll(statusLineStopping, previous.WhenStopped); _refresher = null; }
            return statusLineStopping;
        }

        internal void InitRefresher()
        {
            if (_refresher is not null || !StatusLineEligible || !statusLineStopping.IsCompleted) return;
            var workspaceId = Session.WorkspaceId;
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var refresher = new StatusLineRefresher(
                () => owner.smokeStatusLineDiscovery?.Invoke()
                    ?? StatusLineSupport.Discover(Workspace.Path, homeDir),
                () => owner.service.Snapshot,
                workspaceId,
                (cfg, ctx, ct) => owner.smokeStatusLineRunner?.Invoke(cfg, ctx, ct)
                    ?? StatusLineSupport.RunAsync(cfg, ctx, cancellation: ct));
            _refresher = refresher;
            refresher.StateChanged += () => owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (!owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
                if (!ReferenceEquals(_refresher, refresher) || !StatusLineEligible) return;
                RenderStatusLine(refresher.Config, refresher.Untrusted, refresher.Result, refresher.Config?.Padding ?? 0);
            });
        }

        internal void RequestStatusLineRefresh(bool force = false)
        {
            statusLineToggle.IsChecked = owner.service.Snapshot.StatusLineEnabled;
            statusLineToggle.Visibility = Session.Kind == "claude" && Session.Provider == "claude" ? Visibility.Visible : Visibility.Collapsed;
            if (!StatusLineEligible)
            {
                if (_refresher is { } previous) { previous.Close(); statusLineStopping = Task.WhenAll(statusLineStopping, previous.WhenStopped); _refresher = null; }
                RenderStatusLine(null, null, null);
                return;
            }
            if (!statusLineStopping.IsCompleted)
            {
                if (!statusLineRestartPending) { statusLineRestartPending = true; _ = RestartStatusLineAfterStop(); }
                return;
            }
            InitRefresher();
            _refresher?.RequestRefresh(BuildStatusLineContext(), force);
        }

        private async Task RestartStatusLineAfterStop()
        {
            await statusLineStopping;
            owner.DispatcherQueue.TryEnqueue(() =>
            {
                statusLineRestartPending = false;
                if (!owner.closing && owner.service.Snapshot.Sessions.Any(s => s.Id == id) && StatusLineEligible) RequestStatusLineRefresh(force: true);
            });
        }

        private bool StatusLineEligible => owner.service.Snapshot.StatusLineEnabled && Session.Kind == "claude" && Session.Provider == "claude";

        /// <summary>Adds the status-line toggle to the composer's right cluster, between the context ring and stop / send.</summary>
        private void InitializeStatusLineToggle(StackPanel cluster)
        {
            InitializeStatusLineGlyph();
            AutomationProperties.SetName(statusLineToggle, Locale.Get("settings.display.statusLineToggle"));
            statusLineToggle.Click += async (_, _) => await owner.Act(() => owner.SetStatusLineEnabled(statusLineToggle.IsChecked == true));
            cluster.Children.Add(statusLineToggle);
        }

        private StatusLineContext BuildStatusLineContext()
        {
            var pane = Session;
            var workspace = Workspace;
            var runtime = owner.Runtime(pane.Provider);
            var catalog = runtime?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var usage = pane.SessionUsage;
            var (modelId, modelName) = ModelLabel.StatusLine(pane, catalog);
            var elapsedMs = pane.RunTiming is { } timing ? (long)Math.Max(0, timing.Elapsed() * 1000) : (long?)null;
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configDir = StatusLineSupport.ConfigDir(homeDir) ?? Path.Combine(homeDir, ".claude");
            var sessionId = pane.ResumeId ?? pane.Id;
            return new StatusLineContext(
                SessionId: sessionId,
                Cwd: workspace.Path,
                ProjectDir: workspace.Path,
                ModelId: modelId,
                ModelName: modelName,
                Version: runtime?.Version ?? "",
                CostUSD: usage?.CostUSD,
                DurationMs: elapsedMs,
                ApiDurationMs: 0,
                InputTokens: usage?.InputTokens,
                OutputTokens: usage?.OutputTokens,
                CacheReadTokens: usage?.CacheReadTokens,
                CacheWriteTokens: usage?.CacheWriteTokens,
                ContextUsedTokens: usage?.ContextUsedTokens,
                ContextWindowTokens: usage?.ContextWindowTokens,
                Effort: pane.Settings.Effort == "default" ? null : pane.Settings.Effort,
                FastMode: pane.Settings.FastMode,
                RateLimits: usage?.RateLimits,
                OutputStyle: null,
                ThinkingEnabled: null,
                TranscriptPath: StatusLineSupport.TranscriptPath(configDir, workspace.Path, sessionId));
        }

        /// <summary>The slot the composer reserves under the input for the status line.</summary>
        internal StackPanel StatusLineHost => statusLineHost;
        /// <summary>The workspace command still waiting for an answer, if any.</summary>
        internal StatusLineConfig? StatusLineUntrusted => statusLineUntrusted;

        /// <summary>
        /// Draws one row per output line — at most <see cref="StatusLineSupport.MaximumLines"/>,
        /// with the terminal's colour and weight — and, above it, the question a
        /// workspace-supplied command must answer before it is ever run.
        /// </summary>
        internal void RenderStatusLine(StatusLineConfig? config, StatusLineConfig? untrusted, StatusLineResult? result, int padding = 0)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var mono = new FontFamily(DesignMetrics.Font.Mono);
            statusLineUntrusted = untrusted;
            statusLineHost.Children.Clear();
            // The Mac's own padding: 12 at the sides (plus the command's own, 6 a step), 2 over and 8 under (M/StatusLineView.swift:42-43).
            statusLineHost.Margin = new Thickness(12 + padding * 6, 2, 12, 8);
            if (untrusted is not null)
            {
                // The question a workspace's command must answer first (M/StatusLineView.swift:15-29): the terminal mark and
                // the 11pt ink2 question, the command on the subtle wash at radius 6, two small buttons and a quiet note.
                var prompt = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
                var question = new Grid { ColumnSpacing = 5 }; question.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); question.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                question.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
                var asked = new TextBlock { Text = StatusLineStrings.TrustPromptTemplate.Replace("{source}", untrusted.SourceLabel), FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(asked, 1); question.Children.Add(asked); prompt.Children.Add(question);
                prompt.Children.Add(new Border
                {
                    Child = new TextBlock { Text = untrusted.Command, FontSize = 11, FontFamily = mono, Foreground = b.Brush(DesignToken.Ink), MaxLines = 3, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), Padding = new Thickness(6),
                    Background = b.Subtle,
                });
                var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var allow = SmallButton(StatusLineStrings.TrustAllow, () => owner.Act(() => TrustStatusLine(untrusted)));
                AutomationProperties.SetAutomationId(allow, "status-line-trust-" + id);
                var deny = SmallButton(StatusLineStrings.TrustDeny, () => { DismissStatusLine(); RenderStatusLine(config, null, result, padding); return Task.CompletedTask; });
                answers.Children.Add(allow); answers.Children.Add(deny);
                // The note beside the answers: 10pt in the tertiary ink (M/StatusLineView.swift:24).
                answers.Children.Add(new TextBlock { Text = StatusLineStrings.TrustNote, FontSize = 10, Foreground = b.Tertiary, VerticalAlignment = VerticalAlignment.Center });
                prompt.Children.Add(answers);
                AutomationProperties.SetAutomationId(prompt, "status-line-untrusted-" + id);
                statusLineHost.Children.Add(prompt);
            }
            if (result is not null && config is not null)
            {
                foreach (var line in result.Lines.Take(StatusLineSupport.MaximumLines)) statusLineHost.Children.Add(Row(line));
                if (result.ErrorText is { Length: > 0 } error && result.Lines.Count == 0)
                {
                    // A failed command says so in one quiet line behind the warning mark (M/StatusLineView.swift:35-37).
                    var failed = new Grid { ColumnSpacing = 5 }; failed.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); failed.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                    failed.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center });
                    var said = new TextBlock { Text = error, FontSize = 11, FontFamily = mono, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                    Grid.SetColumn(said, 1); failed.Children.Add(said); statusLineHost.Children.Add(failed);
                }
            }
            AutomationProperties.SetName(statusLineHost, StatusLineStrings.AccessibilityLabel);
            AutomationProperties.SetHelpText(statusLineHost, result is null ? "" : string.Join("\n", result.Lines.Select(line => string.Concat(line.Select(segment => segment.Text)))));
            ToolTipService.SetToolTip(statusLineHost, config is null ? "statusLine" : "statusLine · " + config.SourceLabel + " · " + config.Command);
            statusLineHost.Visibility = statusLineHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // The toolbar sits 10 over the card's edge, or 4 over a status line (M/SessionPaneView.swift:645).
            if (toolbar is not null) toolbar.Margin = new Thickness(10, 0, 10, statusLineHost.Visibility == Visibility.Visible ? 4 : 10);
        }

        /// <summary>
        /// A small push button, the Mac's <c>.controlSize(.small)</c>: 11pt, about 20 high, radius 5; <c>card</c> with a
        /// 1pt <c>line</c> and <c>ink</c> words, or, when prominent, <c>accent</c> behind <c>onAccent</c> words.
        /// </summary>
        private Button SmallButton(string title, Func<Task> action, bool prominent = false)
        {
            var b = owner.brushes; var button = Button(title, action);
            button.FontSize = 11; button.MinWidth = 0; button.MinHeight = 0; button.Padding = new Thickness(8, 1, 8, 2); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow); button.VerticalAlignment = VerticalAlignment.Center;
            if (prominent)
            {
                button.BorderThickness = new Thickness(0); button.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
                owner.PaintPlainButton(button, b.Brush(DesignToken.Accent), b.Brush(DesignToken.Accent, 0.9), ink: b.Brush(DesignToken.OnAccent), disabledInk: b.Brush(DesignToken.OnAccent));
                button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : DisabledDim;
            }
            else
            {
                button.BorderThickness = new Thickness(DesignMetrics.Stroke.Line);
                owner.PaintPlainButton(button, b.Brush(DesignToken.Card), b.Subtle, b.Brush(DesignToken.Line), b.Brush(DesignToken.Ink), b.Brush(DesignToken.Ink3));
            }
            return button;
        }

        /// <summary>
        /// --smoke-test: the trust question, "allow in this workspace", the re-ask after an edited
        /// command, and a six-line coloured render — all from fixture state, never a real CLI.
        /// </summary>
        internal async Task<bool> RunStatusLineSmoke()
        {
            var workspaceId = Session.WorkspaceId;
            var untrusted = new StatusLineConfig("echo repo", 0, StatusLineStrings.SourceWorkspace, FromWorkspace: true);
            RenderStatusLine(null, untrusted, null);
            Require(statusLineHost.Visibility == Visibility.Visible && StatusLineUntrusted == untrusted, "the workspace statusLine question is not shown");
            var question = statusLineHost.Children.OfType<StackPanel>().Single();
            var texts = Descendants(question).OfType<TextBlock>().Select(t => t.Text).ToList();
            Require(texts.Contains(StatusLineStrings.TrustPromptTemplate.Replace("{source}", untrusted.SourceLabel)) && texts.Contains(StatusLineStrings.TrustNote), "the statusLine question text differs from macOS");
            var answers = Descendants(question).OfType<Button>().Select(b => b.Content as string).ToList();
            Require(answers.Contains(StatusLineStrings.TrustAllow) && answers.Contains(StatusLineStrings.TrustDeny), "the allow / not now buttons are missing");
            Require(!StatusLineTrust.IsTrusted(owner.service.Snapshot, untrusted, workspaceId), "the workspace command was trusted before it was allowed");
            await SettleDesktopCapture(owner.root); await CaptureElement(Container, Path.Combine(owner.options.ProfileDirectory!, "smoke-composer-status-line-question.png"));

            await TrustStatusLine(untrusted);
            Require(StatusLineTrust.IsTrusted(owner.service.Snapshot, untrusted, workspaceId) && StatusLineUntrusted is null, "the question stayed after allowing");
            Require(!StatusLineTrust.IsTrusted(owner.service.Snapshot, untrusted with { Command = "echo repo --changed" }, workspaceId), "a changed command was not asked about again");

            var rendered = "\u001B[36mMighty\u001B[0m \u001B[1mbuild\u001B[0m\n2\n3\n4\n5\n6\n7\n8".Split('\n').Take(StatusLineSupport.MaximumLines).Select(AnsiText.Parse).ToList();
            var result = new StatusLineResult(rendered, null, 0, false);
            RenderStatusLine(untrusted, null, result);
            var rows = statusLineHost.Children.OfType<TextBlock>().ToList();
            Require(rows.Count == StatusLineSupport.MaximumLines, "the status line was not limited to 6 lines");
            var segments = rows[0].Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().ToList();
            Require(segments[0].Foreground is SolidColorBrush, "the terminal colour was not applied to the first status line segment");
            Require(segments.Any(r => r.FontWeight.Weight >= Microsoft.UI.Text.FontWeights.SemiBold.Weight), "bold text was not applied to the status line");
            // The toolbar sits 4 over a status line, 10 over the card's edge without one (M/SessionPaneView.swift:645).
            Require(toolbar.Margin.Bottom == 4, $"the toolbar must sit 4 over the status line; got {toolbar.Margin.Bottom}");
            await SettleDesktopCapture(owner.root); await CaptureElement(Container, Path.Combine(owner.options.ProfileDirectory!, "smoke-composer-status-line.png"));
            return true;
        }

        private static IEnumerable<DependencyObject> Descendants(Panel root)
        {
            foreach (var child in root.Children)
            {
                yield return child;
                if (child is Panel panel) foreach (var nested in Descendants(panel)) yield return nested;
                else if (child is Border { Child: { } inner }) yield return inner;
            }
        }

        /// <summary>"Allow in this workspace": Core records the fingerprint, so a changed command asks again.</summary>
        private async Task TrustStatusLine(StatusLineConfig config)
        {
            var workspaceId = Session.WorkspaceId;
            await owner.service.UpdateAsync(s => StatusLineTrust.Trust(s, config, workspaceId));
            // Render immediately so the trust prompt disappears; the refresher
            // will follow up with actual command output once it runs.
            RenderStatusLine(config, null, null);
            _refresher?.RequestRefresh(BuildStatusLineContext(), force: true);
        }

        internal void DismissStatusLine() => _refresher?.Dismiss();

        /// <summary>
        /// One status-line row in 11pt mono with the terminal's colours (M/StatusLineView.swift:50-79):
        /// the ANSI colours are data (<see cref="DesignTokens.AnsiStandard"/>, the 256-colour table and
        /// 24-bit values), so each coloured run gets its own brush; text with no colour takes the
        /// pane's ink, bright black the secondary ink, both as shared token brushes, and a dim
        /// segment keeps its colour at <see cref="DesignTokens.AnsiDimOpacity"/>.
        /// </summary>
        private TextBlock Row(IReadOnlyList<AnsiSegment> segments)
        {
            var row = new TextBlock { FontSize = 11, FontFamily = new FontFamily(DesignMetrics.Font.Mono), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
            foreach (var segment in segments)
            {
                var piece = new Microsoft.UI.Xaml.Documents.Run { Text = segment.Text };
                var opacity = segment.Dim ? DesignTokens.AnsiDimOpacity : 1;
                if (Terminal(segment.Foreground) is { } color) piece.Foreground = new SolidColorBrush(DesignBrushes.ToColor(color, opacity));
                else if (segment.Foreground is { Kind: AnsiColorKind.Standard, A: 8 }) piece.Foreground = owner.brushes.Brush(DesignToken.Ink2, opacity);
                else if (segment.Dim) piece.Foreground = owner.brushes.Brush(DesignToken.Ink, opacity);
                if (segment.Bold) piece.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                if (segment.Italic) piece.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (segment.Underline) piece.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                row.Inlines.Add(piece);
            }
            return row;
        }

        /// <summary>A segment's terminal colour, or null when it takes an ink (no colour, 0, 7, 8 and 15).</summary>
        private static DesignColor? Terminal(AnsiColor value)
        {
            switch (value.Kind)
            {
                case AnsiColorKind.Rgb: return new DesignColor((byte)value.A, (byte)value.B, (byte)value.C);
                case AnsiColorKind.Palette:
                    var (r, g, b) = AnsiPalette.ToRgb(value.A);
                    return new DesignColor(r, g, b);
                case AnsiColorKind.Standard: return DesignTokens.AnsiStandard(value.A);
                default: return null;
            }
        }
    }
}
