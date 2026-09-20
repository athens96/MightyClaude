using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        // Claude's extra tool-permission requests, answered inside the run pane
        // (macOS ToolPermissionBar.swift). This layer only draws the request
        // Core produced and forwards one answer; the protocol, the caps and
        // every fail-closed rule live in ClaudePermissionChannel.
        //
        // A request is ephemeral: it is held here only while it is waiting, it
        // is never written to the snapshot, never logged with its input and
        // never forwarded to a remote host.
        private readonly StackPanel toolPermissionHost = new() { Spacing = 6, Visibility = Visibility.Collapsed };
        private readonly List<ToolPermissionRequest> toolPermissions = [];
        private bool toolPermissionAnswering, toolPermissionRawOpen;

        /// <summary>The slot the composer reserves above the input for the approval bar.</summary>
        internal StackPanel ToolPermissionHost => toolPermissionHost;
        /// <summary>The requests still waiting for an answer, oldest first.</summary>
        internal IReadOnlyList<ToolPermissionRequest> ToolPermissionsWaiting => toolPermissions;

        /// <summary>
        /// Keeps the waiting list in the order Core produced it. Anything that
        /// is no longer pending — allowed, denied or cancelled with the run —
        /// leaves the list at once, so the bar can never answer it twice.
        /// </summary>
        internal void ReceiveToolPermission(ToolPermissionRequest value)
        {
            var at = toolPermissions.FindIndex(r => r.Id == value.Id);
            if (value.State == "pending")
            {
                if (at < 0) toolPermissions.Add(value); else toolPermissions[at] = value;
            }
            else if (at >= 0) { toolPermissions.RemoveAt(at); if (toolPermissions.Count == 0) toolPermissionRawOpen = false; }
            RenderToolPermission();
        }

        /// <summary>Drops every waiting request without answering it (the run ended, or the pane is gone).</summary>
        internal void ClearToolPermissions()
        {
            toolPermissions.Clear(); toolPermissionRawOpen = false; RenderToolPermission();
        }

        /// <summary>
        /// One bar for the oldest waiting request: what the tool does, what it
        /// wants to do, why, the path, how many are waiting, the note that the
        /// choice applies to this request only, and the two buttons. A request
        /// that cannot be allowed here shows the macOS sentence and 거부 alone.
        /// </summary>
        internal void RenderToolPermission()
        {
            toolPermissionHost.Children.Clear();
            if (toolPermissions.Count == 0) { toolPermissionHost.Visibility = Visibility.Collapsed; return; }

            var request = toolPermissions[0];
            var presentation = ToolPermissionPresentation.Make(request.ToolName, request.InputJson);
            var body = new StackPanel { Spacing = 6 };

            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new TextBlock
            {
                Text = ToolPermissionStrings.BarTitleTemplate.Replace("{title}", presentation.Title),
                FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });
            if (toolPermissions.Count > 1)
                heading.Children.Add(new TextBlock
                {
                    Text = ToolPermissionStrings.BarWaitingCountTemplate.Replace("{count}", toolPermissions.Count.ToString()),
                    FontSize = 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center,
                });
            body.Children.Add(heading);

            if (presentation.Headline is { Length: > 0 } headline)
                body.Children.Add(new TextBlock { Text = headline, FontSize = 11, Opacity = .8, TextWrapping = TextWrapping.Wrap });
            if (request.Summary is { Length: > 0 } summary)
                body.Children.Add(Mono(summary));
            foreach (var field in presentation.Fields)
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = field.Label, FontSize = 10, Opacity = .55 });
                row.Children.Add(field.Code ? Mono(field.Value) : new TextBlock { Text = field.Value, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 6, IsTextSelectionEnabled = true });
                body.Children.Add(row);
            }
            if (request.Reason is { Length: > 0 } reason)
                body.Children.Add(new TextBlock { Text = reason, FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (request.BlockedPath is { Length: > 0 } path)
                body.Children.Add(new TextBlock
                {
                    Text = ToolPermissionStrings.BarPathTemplate.Replace("{path}", path),
                    FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                });
            if (!request.CanAllow)
                body.Children.Add(new TextBlock { Text = ToolPermissionStrings.BarCannotAllowHere, FontSize = 11, Opacity = .85, TextWrapping = TextWrapping.Wrap });

            // 원본 JSON: the complete input, exactly as it was checked against
            // the display limit. A request whose input cannot be shown whole is
            // denied by Core before it ever reaches this bar.
            var raw = Button(ToolPermissionStrings.BarRawJson, () => { toolPermissionRawOpen = !toolPermissionRawOpen; RenderToolPermission(); return Task.CompletedTask; });
            raw.Height = 26; raw.MinHeight = 0; raw.Padding = new Thickness(8, 0, 8, 0); raw.FontSize = 11;
            raw.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); raw.BorderThickness = new Thickness(0);
            raw.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetAutomationId(raw, "tool-permission-raw-" + id);
            body.Children.Add(raw);
            if (toolPermissionRawOpen)
                body.Children.Add(new Border
                {
                    Child = new TextBlock { Text = request.InputJson, FontSize = 11, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, MaxLines = 24, IsTextSelectionEnabled = true },
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(6),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 135, 135, 135)),
                });

            var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var deny = Button(ToolPermissionStrings.ButtonDeny, () => Answer(request, false));
            deny.Height = 28; deny.MinHeight = 0; deny.Padding = new Thickness(10, 0, 10, 0); deny.FontSize = 11;
            deny.IsEnabled = !toolPermissionAnswering;
            AutomationProperties.SetAutomationId(deny, "tool-permission-deny-" + id);
            answers.Children.Add(deny);
            if (request.CanAllow)
            {
                var allow = Button(ToolPermissionStrings.ButtonAllowOnce, () => Answer(request, true));
                allow.Height = 28; allow.MinHeight = 0; allow.Padding = new Thickness(10, 0, 10, 0); allow.FontSize = 11;
                allow.IsEnabled = !toolPermissionAnswering;
                AutomationProperties.SetAutomationId(allow, "tool-permission-allow-" + id);
                answers.Children.Add(allow);
            }
            answers.Children.Add(new TextBlock { Text = ToolPermissionStrings.BarOnceOnlyNote, FontSize = 10, Opacity = .55, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(answers);

            var card = new Border
            {
                Child = body, CornerRadius = new CornerRadius(10), Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 226, 160, 60)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 226, 160, 60)),
            };
            AutomationProperties.SetAutomationId(card, "tool-permission-bar-" + id);
            AutomationProperties.SetName(card, ToolPermissionStrings.BarTitleTemplate.Replace("{title}", presentation.Title));
            AutomationProperties.SetLiveSetting(card, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
            ToolTipService.SetToolTip(card, request.ToolName);
            toolPermissionHost.Children.Add(card);
            toolPermissionHost.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 이번만 허용 / 거부 for this request only. Core refuses an answer for a
        /// request that is already settled, so a double click can never allow.
        /// </summary>
        private Task Answer(ToolPermissionRequest request, bool allow) => owner.Act(() =>
        {
            if (toolPermissionAnswering || !toolPermissions.Any(r => r.Id == request.Id)) return Task.CompletedTask;
            if (allow && !request.CanAllow) throw new InvalidOperationException(ToolPermissionStrings.CannotAllow);
            toolPermissionAnswering = true; RenderToolPermission();
            try { owner.service.RespondToToolPermission(id, request.Id, allow); }
            finally { toolPermissionAnswering = false; }
            // Core answers with the settled request, which removes it here.
            RenderToolPermission();
            return Task.CompletedTask;
        });

        private static TextBlock Mono(string text) => new()
        {
            Text = text, FontSize = 11, FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap, MaxLines = 6, IsTextSelectionEnabled = true,
        };
    }
}
