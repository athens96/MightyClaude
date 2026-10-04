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
            Text = Locale.Get("badge.beta"), FontSize = DesignMetrics.Type.Badge, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = brushes.Brush(DesignToken.StopText)
        };
        var badge = new Border
        {
            Child = text, CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center,
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
        var line = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.Children.Add(title);
        var badge = BetaBadgeView.Create(brushes); Grid.SetColumn(badge, 1); line.Children.Add(badge);
        (tab ? tabBetas : sidebarBetas).Add(session.Id);
        return line;
    }

    /// <summary>
    /// Smoke key <c>betaBadge</c>: every Codex and Gemini agent row in the sidebar and every such
    /// tab carries the 베타 capsule after its title, Claude and non-agent rows carry none, and the
    /// title text itself never gains the badge.
    /// </summary>
    private Dictionary<string, object?> RunBetaBadgeSmoke()
    {
        var checks = new Dictionary<string, object?>();
        RenderSidebar();
        var state = service.Snapshot;
        var rows = state.Sessions.Where(s => s.WorkspaceId == state.ActiveWorkspaceId).ToList();
        var betas = rows.Where(ProviderCatalog.ShowsBetaBadge).ToList();
        Require(betas.Select(s => s.Provider).Distinct().Count() == 2, "beta badge smoke needs a Codex and a Gemini row");
        foreach (var session in rows)
            Require(sidebarBetas.Contains(session.Id) == ProviderCatalog.ShowsBetaBadge(session), $"sidebar row {session.Id} ({session.Provider}) badge is {sidebarBetas.Contains(session.Id)}");
        Require(tabBetas.All(id => rows.Any(s => s.Id == id && ProviderCatalog.ShowsBetaBadge(s))), "a tab that is not a beta agent's carries the badge");
        Require(rows.All(s => !s.Title.Contains(Locale.Get("badge.beta"), StringComparison.Ordinal)), "a title carries the badge text");
        checks["providers"] = betas.Select(s => s.Provider).Distinct().OrderBy(p => p).ToList();
        checks["betaRowsCarryBadge"] = true;
        checks["otherRowsCarryNone"] = rows.Count - betas.Count;
        checks["badgeText"] = Locale.Get("badge.beta");
        return checks;
    }
}
