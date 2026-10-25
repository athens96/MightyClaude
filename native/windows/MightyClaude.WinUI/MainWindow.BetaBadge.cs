using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>
/// The small capsule drawn after a beta provider's name (macOS BetaBadge.swift): 9pt medium
/// <c>stopText</c> on a <c>stopSoft</c> capsule, padding h5 v1. It sits beside the title and is
/// never part of it. The colours are the window's shared token brushes, so a theme toggle
/// recolours a badge already on screen.
/// </summary>
internal static class BetaBadgeView
{
    internal static Border Create(DesignBrushes brushes)
    {
        var text = new TextBlock
        {
            Text = Locale.Get("badge.beta"), TextSize = DesignMetrics.Type.Badge, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = brushes.Brush(DesignToken.StopText)
        };
        var badge = new Border
        {
            Child = text, CornerRadius = new CornerRadius(7), Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), VerticalAlignment = VerticalAlignment.Center,
            Background = brushes.Brush(DesignToken.StopSoft)
        };
        AutomationProperties.SetName(badge, Locale.Get("badge.betaAccessibility"));
        return badge;
    }
}

public sealed partial class MainWindow
{
    /// <summary>Sidebar rows and tabs that drew the capsule, by session id.</summary>
    private readonly HashSet<string> sidebarBetas = [], tabBetas = [];

    /// <summary>
    /// The title of a sidebar row or tab: the plain text, or for a Codex or Gemini agent pane the
    /// text followed by the 베타 capsule. The title still trims; the capsule stays right after it.
    /// </summary>
    private FrameworkElement SessionTitle(RunSession session, TextBlock title, bool tab)
    {
        if (!ProviderCatalog.ShowsBetaBadge(session)) return title;
        var line = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.Children.Add(title);
        var badge = BetaBadgeView.Create(brushes); Grid.SetColumn(badge, 1); line.Children.Add(badge);
        (tab ? tabBetas : sidebarBetas).Add(session.Id);
        return line;
    }

}
