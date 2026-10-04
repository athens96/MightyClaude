using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // Smoke mode intercept: set before smoke, cleared in finally.
    private Dictionary<string, bool>? smokePermissionResponses;

    private sealed partial class PaneView
    {
        // The bar sits above the input inside the composer card,
        // matching macOS ToolPermissionBar.swift placement.
        private readonly StackPanel toolPermissionHost = new() { Spacing = 6, Visibility = Visibility.Collapsed };
        private readonly List<ToolPermissionRequest> toolPermissions = [];
        // Stored for smoke assertions — updated by RenderToolPermissions.
        private TextBlock permTitleBlock = null!;
        private TextBlock permPathBlock = null!;
        private TextBlock permCountBlock = null!;
        private TextBlock permReasonBlock = null!;
        private TextBlock permCannotAllowBlock = null!;
        private Button permDenyButton = null!;
        private Button permAllowButton = null!;
        private TextBlock permInputBlock = null!;

        // Builds the card once; RenderToolPermissions updates its text later.
        private void InitPermissionBar()
        {
            permTitleBlock = new TextBlock
            {
                FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(permTitleBlock, "permission-title-" + id);
            permCountBlock = new TextBlock { FontSize = 11, Opacity = .65 };
            AutomationProperties.SetAutomationId(permCountBlock, "permission-count-" + id);

            var titleRow = new Grid { ColumnSpacing = 8 };
            titleRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            titleRow.Children.Add(permTitleBlock);
            Grid.SetColumn(permCountBlock, 1); titleRow.Children.Add(permCountBlock);

            permPathBlock = new TextBlock { FontSize = 11, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetAutomationId(permPathBlock, "permission-path-" + id);
            permReasonBlock = new TextBlock { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 };
            permCannotAllowBlock = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            var noteBlock = new TextBlock { Text = ToolPermissionStrings.BarOnceOnlyNote, FontSize = 10, Opacity = .55 };

            permDenyButton = new Button { Content = ToolPermissionStrings.ButtonDeny, Height = 28, MinHeight = 0, Padding = new Thickness(10, 0, 10, 0) };
            AutomationProperties.SetName(permDenyButton, ToolPermissionStrings.ButtonDeny);
            AutomationProperties.SetAutomationId(permDenyButton, "permission-deny-" + id);
            permDenyButton.Click += (_, _) => OnPermissionDeny();

            permAllowButton = new Button
            {
                Content = ToolPermissionStrings.ButtonAllowOnce, Height = 28, MinHeight = 0,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(200, 99, 179, 237)),
            };
            AutomationProperties.SetName(permAllowButton, ToolPermissionStrings.ButtonAllowOnce);
            AutomationProperties.SetAutomationId(permAllowButton, "permission-allow-" + id);
            permAllowButton.Click += (_, _) => OnPermissionAllow();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(permDenyButton); buttons.Children.Add(permAllowButton);

            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(titleRow); body.Children.Add(permPathBlock);
            body.Children.Add(permReasonBlock); body.Children.Add(permCannotAllowBlock);
            permInputBlock = new TextBlock { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            AutomationProperties.SetAutomationId(permInputBlock, "permission-input-" + id);
            body.Children.Add(new ScrollViewer { Content = permInputBlock, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(noteBlock); body.Children.Add(buttons);

            var card = new Border
            {
                Child = body, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(75, 135, 135, 135)),
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 180, 50)),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4),
            };
            AutomationProperties.SetAutomationId(card, "permission-bar-" + id);
            toolPermissionHost.Children.Add(card);
        }

        internal void ReceiveToolPermission(ToolPermissionRequest value)
        {
            var idx = toolPermissions.FindIndex(p => p.Id == value.Id);
            if (value.State == "pending") { if (idx < 0) toolPermissions.Add(value); else toolPermissions[idx] = value; }
            else { if (idx >= 0) toolPermissions.RemoveAt(idx); }
            RenderToolPermissions(preserveQuestionnaire: true);
        }

        internal void ClearToolPermissions()
        {
            toolPermissions.Clear(); RenderToolPermissions();
        }

        private void RenderToolPermissions(bool preserveQuestionnaire = false)
        {
            var pending = toolPermissions.Where(p => p.State == "pending").ToList();
            RenderGuidedStyle();
            if (pending.Count == 0) { HideQuestionnaire(); toolPermissionHost.Visibility = Visibility.Collapsed; return; }
            var current = pending[0];
            if (TryRenderQuestionnaire(current, pending.Count, preserveQuestionnaire)) return;
            var pres = ToolPermissionPresentation.Make(current.ToolName, current.InputJson);
            permInputBlock.Text = current.InputJson;
            permTitleBlock.Text = ToolPermissionStrings.BarTitleTemplate.Replace("{title}", pres.Title);
            if (pending.Count > 1)
            {
                permCountBlock.Text = ToolPermissionStrings.BarWaitingCountTemplate.Replace("{count}", pending.Count.ToString());
                permCountBlock.Visibility = Visibility.Visible;
            }
            else { permCountBlock.Text = ""; permCountBlock.Visibility = Visibility.Collapsed; }
            permPathBlock.Text = current.BlockedPath is { } path ? ToolPermissionStrings.BarPathTemplate.Replace("{path}", path) : "";
            permPathBlock.Visibility = current.BlockedPath is not null ? Visibility.Visible : Visibility.Collapsed;
            permReasonBlock.Text = current.Reason ?? "";
            permReasonBlock.Visibility = current.Reason is not null ? Visibility.Visible : Visibility.Collapsed;
            var cannotAllow = !current.CanAllow;
            permCannotAllowBlock.Text = cannotAllow ? ToolPermissionStrings.BarCannotAllowHere : "";
            permCannotAllowBlock.Visibility = cannotAllow ? Visibility.Visible : Visibility.Collapsed;
            permAllowButton.Visibility = cannotAllow ? Visibility.Collapsed : Visibility.Visible;
            toolPermissionHost.Visibility = Visibility.Visible;
        }

        private void OnPermissionAllow()
        {
            var current = toolPermissions.FirstOrDefault(p => p.State == "pending");
            if (current is null) return;
            if (owner.smokePermissionResponses is { } dict) { dict[current.Id] = true; ReceiveToolPermission(current with { State = "allowed" }); return; }
            try { owner.service.RespondToToolPermission(id, current.Id, true); } catch (Exception ex) { owner.error.Text = ex.Message; }
        }

        private void OnPermissionDeny()
        {
            var current = toolPermissions.FirstOrDefault(p => p.State == "pending");
            if (current is null) return;
            if (owner.smokePermissionResponses is { } dict) { dict[current.Id] = false; ReceiveToolPermission(current with { State = "denied" }); return; }
            try { owner.service.RespondToToolPermission(id, current.Id, false); } catch (Exception ex) { owner.error.Text = ex.Message; }
        }

        internal async Task<Dictionary<string, object?>> RunToolPermissionSmoke()
        {
            var checks = new Dictionary<string, object?>();
            var savedDraft = input.Text;
            var savedFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(toolPermissionHost.XamlRoot);
            try
            {
                owner.smokePermissionResponses = [];

                var req1 = new ToolPermissionRequest("smoke-perm-1", "smoke-run", "smoke-tuid-1", "Read",
                    "{\"file_path\":\"/tmp/test.txt\"}", "Read file", BlockedPath: "/tmp/test.txt");
                var req2 = new ToolPermissionRequest("smoke-perm-2", "smoke-run", "smoke-tuid-2", "Bash",
                    "{\"command\":\"ls /tmp\"}", "Run command");

                ReceiveToolPermission(req1); ReceiveToolPermission(req2);
                await WaitUI(() => toolPermissionHost.Visibility == Visibility.Visible);

                var pres1 = ToolPermissionPresentation.Make(req1.ToolName, req1.InputJson);
                var expectedTitle = ToolPermissionStrings.BarTitleTemplate.Replace("{title}", pres1.Title);
                Require(permTitleBlock.Text == expectedTitle, "Unexpected permission bar title: " + permTitleBlock.Text);
                Require(permPathBlock.Visibility == Visibility.Visible && permPathBlock.Text.Contains("/tmp/test.txt"),
                    "Blocked path was not displayed.");
                Require(permCountBlock.Visibility == Visibility.Visible && permCountBlock.Text.Contains("2"),
                    "Pending count was not displayed.");
                checks["barShownWithTitlePathCount"] = true;

                OnPermissionAllow();
                Require(owner.smokePermissionResponses.TryGetValue("smoke-perm-1", out var r1) && r1,
                    "One-time permission was not recorded.");
                checks["allowRecorded"] = true;

                Require(toolPermissions.Any(p => p.Id == "smoke-perm-2" && p.State == "pending"),
                    "Second request was not displayed.");
                OnPermissionDeny();
                Require(owner.smokePermissionResponses.TryGetValue("smoke-perm-2", out var r2) && !r2,
                    "Deny response was not recorded.");
                checks["denyRecorded"] = true;

                await WaitUI(() => toolPermissionHost.Visibility == Visibility.Collapsed);
                checks["barHiddenAfterAllAnswered"] = true;
            }
            finally
            {
                owner.smokePermissionResponses = null;
                toolPermissions.Clear(); RenderToolPermissions();
                updating = true; try { input.Text = savedDraft; } finally { updating = false; }
                ((FrameworkElement?)savedFocus)?.Focus(FocusState.Programmatic);
            }
            return checks;
        }
    }
}
