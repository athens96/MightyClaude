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
            // A 22×24 header button, like the Mac's (M/SessionPaneView.swift:261, 271, 298).
            Width = 22, Height = 24, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(3), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment),
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

        private void InitializeStatusLineToggle(StackPanel header)
        {
            InitializeStatusLineGlyph();
            AutomationProperties.SetName(statusLineToggle, Locale.Get("settings.display.statusLineToggle"));
            ToolTipService.SetToolTip(statusLineToggle, Locale.Get("settings.display.statusLineToggle"));
            statusLineToggle.Click += async (_, _) => await owner.Act(() => owner.SetStatusLineEnabled(statusLineToggle.IsChecked == true));
            header.Children.Add(statusLineToggle);
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
            statusLineUntrusted = untrusted;
            statusLineHost.Children.Clear();
            statusLineHost.Margin = new Thickness(padding * 6, 2, 0, 0);
            if (untrusted is not null)
            {
                var prompt = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
                prompt.Children.Add(new TextBlock { Text = StatusLineStrings.TrustPromptTemplate.Replace("{source}", untrusted.Source), FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap });
                prompt.Children.Add(new Border
                {
                    Child = new TextBlock { Text = untrusted.Command, FontSize = 11, FontFamily = new FontFamily(DesignMetrics.Font.Mono), MaxLines = 3, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(6),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 135, 135, 135)),
                });
                var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var allow = Button(StatusLineStrings.TrustAllow, () => owner.Act(() => TrustStatusLine(untrusted)));
                allow.Height = 28; allow.MinHeight = 0; allow.Padding = new Thickness(10, 0, 10, 0); allow.FontSize = 11;
                AutomationProperties.SetAutomationId(allow, "status-line-trust-" + id);
                var deny = Button(StatusLineStrings.TrustDeny, () => { DismissStatusLine(); RenderStatusLine(config, null, result, padding); return Task.CompletedTask; });
                deny.Height = 28; deny.MinHeight = 0; deny.Padding = new Thickness(10, 0, 10, 0); deny.FontSize = 11;
                answers.Children.Add(allow); answers.Children.Add(deny);
                answers.Children.Add(new TextBlock { Text = StatusLineStrings.TrustNote, FontSize = 10, Opacity = .55, VerticalAlignment = VerticalAlignment.Center });
                prompt.Children.Add(answers);
                AutomationProperties.SetAutomationId(prompt, "status-line-untrusted-" + id);
                statusLineHost.Children.Add(prompt);
            }
            if (result is not null && config is not null)
            {
                foreach (var line in result.Lines.Take(StatusLineSupport.MaximumLines)) statusLineHost.Children.Add(Row(line));
                if (result.ErrorText is { Length: > 0 } error && result.Lines.Count == 0)
                    statusLineHost.Children.Add(new TextBlock { Text = "⚠ " + error, FontSize = 11, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis });
            }
            AutomationProperties.SetName(statusLineHost, StatusLineStrings.AccessibilityLabel);
            AutomationProperties.SetHelpText(statusLineHost, result is null ? "" : string.Join("\n", result.Lines.Select(line => string.Concat(line.Select(segment => segment.Text)))));
            ToolTipService.SetToolTip(statusLineHost, config is null ? "statusLine" : "statusLine · " + config.Source + " · " + config.Command);
            statusLineHost.Visibility = statusLineHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// --smoke-test: the trust question, 이 워크스페이스에서 허용, the re-ask after an edited
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
            Require(texts.Contains(StatusLineStrings.TrustPromptTemplate.Replace("{source}", untrusted.Source)) && texts.Contains(StatusLineStrings.TrustNote), "the statusLine question text differs from macOS");
            var answers = Descendants(question).OfType<Button>().Select(b => b.Content as string).ToList();
            Require(answers.Contains(StatusLineStrings.TrustAllow) && answers.Contains(StatusLineStrings.TrustDeny), "the allow / not now buttons are missing");
            Require(!StatusLineTrust.IsTrusted(owner.service.Snapshot, untrusted, workspaceId), "the workspace command was trusted before it was allowed");

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

        /// <summary>이 워크스페이스에서 허용: Core records the fingerprint, so a changed command asks again.</summary>
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

        private static TextBlock Row(IReadOnlyList<AnsiSegment> segments)
        {
            var row = new TextBlock { FontSize = 11, FontFamily = new FontFamily(DesignMetrics.Font.Mono), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
            foreach (var segment in segments)
            {
                var piece = new Microsoft.UI.Xaml.Documents.Run { Text = segment.Text };
                var color = Terminal(segment.Foreground);
                if (color is { } value) piece.Foreground = new SolidColorBrush(segment.Dim ? Windows.UI.Color.FromArgb(150, value.R, value.G, value.B) : value);
                else if (segment.Dim) piece.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 135, 135, 135));
                if (segment.Bold) piece.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                if (segment.Italic) piece.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (segment.Underline) piece.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                row.Inlines.Add(piece);
            }
            return row;
        }

        /// <summary>Terminal colours chosen to stay legible on both themes (macOS StatusLineView.color).</summary>
        private static Windows.UI.Color? Terminal(AnsiColor value)
        {
            static Windows.UI.Color Rgb(double red, double green, double blue) => Windows.UI.Color.FromArgb(255, (byte)Math.Round(red * 255), (byte)Math.Round(green * 255), (byte)Math.Round(blue * 255));
            switch (value.Kind)
            {
                case AnsiColorKind.Rgb: return Windows.UI.Color.FromArgb(255, (byte)value.A, (byte)value.B, (byte)value.C);
                case AnsiColorKind.Palette:
                    var (r, g, b) = AnsiPalette.ToRgb(value.A);
                    return Windows.UI.Color.FromArgb(255, r, g, b);
                case AnsiColorKind.Standard:
                    return (value.A % 8) switch
                    {
                        1 => Rgb(.86, .30, .30),
                        2 => Rgb(.30, .66, .40),
                        3 => Rgb(.80, .62, .20),
                        4 => Rgb(.36, .55, .90),
                        5 => Rgb(.70, .45, .85),
                        6 => Rgb(.25, .65, .70),
                        _ => value.A == 8 ? Windows.UI.Color.FromArgb(170, 135, 135, 135) : null,
                    };
                default: return null;
            }
        }
    }
}
