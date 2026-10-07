using System.Text;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>The approval sheet's size (M/StyleApprovalSheet.swift:54).</summary>
    internal const double StyleApprovalWidth = 640, StyleApprovalHeight = 560;
    /// <summary>The sheet's inset: 16 at the sides of its heading, its sections and its buttons, 12 over and under the heading and the buttons, 14 between sections (M/StyleApprovalSheet.swift:44-48, 65, 133).</summary>
    private const double StyleApprovalInset = 16, StyleApprovalEdge = 12, StyleApprovalGap = 14;
    /// <summary>The most the auto-allow list takes of the sheet's fixed top before it scrolls by itself.</summary>
    private const double StyleApprovalAutoAllowHeight = 120;

    // M/StyleApprovalSheet.swift:39-134: the heading — the style's name (15 semibold), its source badge and
    // the expand-all switch — a rule, the sections 14 apart (a 12pt semibold title over 11pt ink2 lines,
    // mono where the section says so) with the raw JSON last in 10pt mono, a rule, and the buttons at the
    // trailing edge beside what the sheet has to say (the second confirmation, or the locked trust file).
    // On Windows the origin and the auto-allow list stay in the fixed top of the sheet, so the tool names
    // cannot scroll away behind the details, and the allow button stays off until that list has been drawn.
    private async Task<bool> ShowStyleApproval(RegisteredStyle style, bool readOnly = false, Func<Task>? onApprove = null)
    {
        var trust = new StyleTrustStore(Path.Combine(StateDirectory, "style-trust"));
        Task<bool> IsTrustLocked() => Task.Run(() =>
        {
            try { trust.Load(); return false; }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return true; }
        });
        var locked = await IsTrustLocked();
        var automatic = StyleManifest.Items(style.Manifest.Root,"autoAllow").Length;
        var sections = StylePresentation.Approval(style); var decision = new StyleApprovalDecision(automatic);
        var collapsible = new List<FrameworkElement>(); var visible = false; var approved = false;
        var dialog = new ContentDialog { XamlRoot = SettingsXamlRoot };
        AutomationProperties.SetAutomationId(dialog, "style-approval-sheet");
        Border Rule() => new() { Height = DesignMetrics.Stroke.Line, Background = brushes.Brush(DesignToken.Line) };
        TextBlock Title(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap };

        // The heading.
        var heading = new Grid { Padding = new Thickness(StyleApprovalInset, StyleApprovalEdge, StyleApprovalInset, StyleApprovalEdge), ColumnSpacing = DesignMetrics.Spacing.Sm };
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var named = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
        named.Children.Add(new TextBlock { Text = style.Manifest.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), VerticalAlignment = VerticalAlignment.Center });
        // The badge that follows a name that is not the app's own (M/GuidedActionChip.swift:83-90): 9pt ink2 on the subtle capsule.
        if(style.Source != "bundled") named.Children.Add(new Border
        {
            Child = new TextBlock { Text = StylePresentation.Source(style.Source), FontSize = 9, Foreground = brushes.Brush(DesignToken.Ink2) },
            Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), CornerRadius = new CornerRadius(8), Background = brushes.Subtle, VerticalAlignment = VerticalAlignment.Center,
        });
        heading.Children.Add(named);
        var expandLabel = Locale.Get("styles.approval.expandAll");
        var disclosure = SettingsSwitch(expandLabel, true, "style-approval-expand");
        var expand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
        expand.Children.Add(SettingsText(expandLabel, 9)); expand.Children.Add(disclosure);
        Grid.SetColumn(expand, 1); heading.Children.Add(expand);

        // The sections.
        FrameworkElement Section(StyleApprovalSection section)
        {
            var panel = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
            AutomationProperties.SetAutomationId(panel, "style-approval-" + section.Id);
            panel.Children.Add(Title(section.Title));
            var lines = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
            foreach(var text in section.Lines) lines.Children.Add(SettingsText(text, 11, DesignToken.Ink2, mono: section.Monospaced, selectable: true));
            panel.Children.Add(lines); if(section.Foldable)collapsible.Add(lines);
            return panel;
        }
        var top = new StackPanel { Spacing = StyleApprovalGap, Padding = new Thickness(StyleApprovalInset, StyleApprovalInset, StyleApprovalInset, 0) };
        var rest = new StackPanel { Spacing = StyleApprovalGap, Padding = new Thickness(StyleApprovalInset, StyleApprovalGap, StyleApprovalInset, StyleApprovalInset) };
        Button? allow = null;
        var fixedTop = true;
        foreach(var section in sections)
        {
            if(section.Id == "autoAllow")
            {
                // Auto-allow stays in the fixed top portion, outside the expandable
                // details. Its exact wire names cannot disappear behind raw JSON.
                var auto = Section(section);
                auto.Loaded += (_,_) => { visible = true; if(allow is not null) allow.IsEnabled = !locked; };
                top.Children.Add(new ScrollViewer { Content = auto, MaxHeight = StyleApprovalAutoAllowHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                fixedTop = false;
            }
            else (fixedTop ? top : rest).Children.Add(Section(section));
        }
        var raw = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        AutomationProperties.SetAutomationId(raw, "style-approval-raw");
        raw.Children.Add(Title(Locale.Get("styles.approval.raw")));
        var contents = SettingsText(Encoding.UTF8.GetString(style.Bytes.Span), 10, DesignToken.Ink2, mono: true, selectable: true);
        raw.Children.Add(contents); collapsible.Add(contents); rest.Children.Add(raw);
        void Expanded() { foreach(var item in collapsible)item.Visibility = disclosure.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        disclosure.Checked += (_,_) => Expanded(); disclosure.Unchecked += (_,_) => Expanded();

        // The buttons, beside what the sheet has to say.
        var message = SettingsText("", 11, DesignToken.Ink2, selectable: true);
        void Say()
        {
            // A lock found between the two presses is what the sheet has to say then, not the request to confirm again.
            if(locked) { message.Text = Locale.Get("settings.styles.lockBanner") + "\n" + Path.Combine(trust.DirectoryPath, "approvals.json"); message.Foreground = brushes.Brush(DesignToken.WaitText); }
            else if(decision.Confirming) { message.Text = Locale.Get("styles.approval.secondConfirmation",new Dictionary<string,string>{{"count",automatic.ToString()}}); message.Foreground = brushes.Brush(DesignToken.Ink2); }
            else message.Text = "";
        }
        var footer = new Grid { Padding = new Thickness(StyleApprovalInset, StyleApprovalEdge, StyleApprovalInset, StyleApprovalEdge), ColumnSpacing = DesignMetrics.Spacing.Sm };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(message);
        var dismiss = SettingsPush(Button(readOnly ? Locale.Get("settings.closeButton") : Locale.Get("guidedPanel.cancelButton"), () => { dialog.Hide(); return Task.CompletedTask; }));
        AutomationProperties.SetAutomationId(dismiss, "style-approval-dismiss");
        Grid.SetColumn(dismiss, 1); footer.Children.Add(dismiss);
        if(!readOnly)
        {
            var allowLabel = Locale.Get("settings.styles.allowButton");
            // The auto-allow block has to have been on screen, and a non-empty list is confirmed a second time.
            allow = SettingsPush(Button(allowLabel, () => Act(async () =>
            {
                locked = await IsTrustLocked();
                if(locked) { allow!.IsEnabled = false; Say(); return; }
                if(!decision.Press(visible))
                {
                    if(decision.Confirming)
                    {
                        var again = Locale.Get("styles.approval.confirmAgain");
                        allow!.Content = again; AutomationProperties.SetName(allow, again); Say();
                    }
                    return;
                }
                approved = true; dialog.Hide();
            })), prominent: true);
            allow.IsEnabled = false;
            AutomationProperties.SetAutomationId(allow, "style-approval-allow");
            Grid.SetColumn(allow, 2); footer.Children.Add(allow);
        }
        Say();

        // The sheet's own padding is taken back, so its two rules run from edge to edge.
        var sheet = new Grid { Margin = new Thickness(-SheetPadding) };
        foreach(var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) sheet.RowDefinitions.Add(new() { Height = height });
        var parts = new FrameworkElement[] { heading, Rule(), top, new ScrollViewer { Content = rest, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Rule(), footer };
        for(var row = 0; row < parts.Length; row++) { Grid.SetRow(parts[row], row); sheet.Children.Add(parts[row]); }
        dialog.Content = sheet;
        // Styled once the body is complete, so every part of it takes the sheet's inks.
        StyledDialog(dialog, StyleApprovalWidth, StyleApprovalHeight);
        // Enter and Space dismiss unless another control was chosen; Esc always does.
        dialog.Opened += (_,_) => dismiss.Focus(FocusState.Programmatic);
        await dialog.ShowAsync();
        if(!approved||readOnly)return false;
        if(onApprove is not null)await onApprove(); else await ApproveStyle(style);
        return true;
    }
}
