using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Paints a button the Mac's <c>.plain</c> way through lightweight styling: <paramref name="normal"/>
    /// at rest and while disabled, <paramref name="hover"/> under the pointer and while pressed, the
    /// <paramref name="border"/> (none when null) in every state, the <paramref name="ink"/> (the
    /// template's when null) in every enabled state and <paramref name="disabledInk"/> (the template's
    /// when null) while disabled. The brushes are the window's shared ones, so a theme toggle recolours
    /// them in place; they go into the button's Light and Dark theme dictionaries (<see cref="SetThemeResources"/>).
    /// </summary>
    internal void PaintPlainButton(Button button, Brush normal, Brush hover, Brush? border = null, Brush? ink = null, Brush? disabledInk = null)
    {
        var edge = border ?? brushes.Transparent;
        button.Background = normal; button.BorderBrush = edge;
        if (ink is not null) button.Foreground = ink;
        var values = new List<(string, object)> { ("ButtonBackground", normal), ("ButtonBackgroundPointerOver", hover), ("ButtonBackgroundPressed", hover), ("ButtonBackgroundDisabled", normal) };
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" }) values.Add(("ButtonBorderBrush" + state, edge));
        if (ink is not null) foreach (var state in new[] { "", "PointerOver", "Pressed" }) values.Add(("ButtonForeground" + state, ink));
        if (disabledInk is not null) values.Add(("ButtonForegroundDisabled", disabledInk));
        SetThemeResources(button, values);
    }

    /// <summary>
    /// Writes lightweight-styling values into the element's own "Light" and "Dark" theme dictionaries,
    /// with an empty "HighContrast" one so the system's contrast resources still resolve there (as the
    /// status-line toggle does). A template reads its states' ThemeResources when it is applied and
    /// again on a theme change, so when a value changes on an element whose template is already
    /// applied, its theme is flipped and set back to make the states read the new brushes.
    /// <para>
    /// A changed theme is never edited in place: it is rebuilt as a fresh dictionary (the keys not
    /// being written carried over) and swapped in whole. WinUI's indexer replaces a key by removing
    /// its value <em>object</em>, and the core removes that object's first entry in the dictionary;
    /// the shared brushes sit under several keys (ButtonForeground and ButtonForegroundDisabled are
    /// both onStatus on the stop button), so it dropped another key's entry, the key being written
    /// stayed, and the add threw E_DO_RESOURCE_KEYCONFLICT (0x800F0902). Each theme dictionary is a
    /// distinct instance, so swapping one in ThemeDictionaries removes the right entry.
    /// </para>
    /// A write that still fails throws with its context in the smoke run and is traced otherwise,
    /// leaving the element's previous look, so it can neither pass a smoke run nor crash the app.
    /// </summary>
    internal void SetThemeResources(FrameworkElement element, IReadOnlyList<(string Key, object Value)> values)
    {
        try
        {
            var themes = element.Resources.ThemeDictionaries; var changed = false;
            if (!themes.ContainsKey("HighContrast")) themes["HighContrast"] = new ResourceDictionary();
            foreach (var name in new[] { "Light", "Dark" })
            {
                var current = themes.TryGetValue(name, out var found) ? found as ResourceDictionary : null;
                if (current is not null && values.All(v => current.TryGetValue(v.Key, out var old) && ReferenceEquals(old, v.Value))) continue;
                var merged = new Dictionary<object, object>();
                if (current is not null) foreach (var pair in current) merged[pair.Key] = pair.Value;
                foreach (var (key, value) in values) merged[key] = value;
                var fresh = new ResourceDictionary();
                foreach (var pair in merged) fresh.Add(pair.Key, pair.Value);
                themes[name] = fresh; changed = true;
            }
            if (!changed || VisualTreeHelper.GetChildrenCount(element) == 0) return;
            var requested = element.RequestedTheme;
            element.RequestedTheme = element.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            element.RequestedTheme = requested;
        }
        catch (Exception error)
        {
            var context = $"Theme resources of {element.GetType().Name} '{AutomationProperties.GetAutomationId(element)}' ({string.Join(", ", values.Select(v => v.Key))}) could not be written: {error.Message}";
            if (options.SmokeTest) throw new InvalidOperationException(context, error);
            System.Diagnostics.Trace.TraceError(context);
        }
    }

    /// <summary>A lightweight-styling value as the element's current theme dictionary holds it (null when absent).</summary>
    internal static object? ThemeResource(FrameworkElement element, string key) =>
        element.Resources.ThemeDictionaries.TryGetValue(element.ActualTheme == ElementTheme.Light ? "Light" : "Dark", out var found)
        && found is ResourceDictionary theme && theme.TryGetValue(key, out var value) ? value : null;

    private sealed partial class PaneView
    {
        /// <summary>Below this width the header keeps its one line by showing the Default | Mighty switch as icons only.</summary>
        private const double NarrowHeader = 420;
        private Grid? paneHeader;
        /// <summary>The header's trailing controls: the Default | Mighty switch, the status-line toggle and the … menu, 10 apart (M/SessionPaneView.swift:235-238).</summary>
        private readonly StackPanel paneHeaderControls = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        /// <summary>The pane title: 13 bold, tracking −0.1, in <c>ink</c> (M/SessionPaneView.swift:217).</summary>
        private readonly TextBlock headerTitle = new() { FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -8, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        private Button? paneMenuButton;
        private bool paneHeaderLayoutQueued;

        /// <summary>
        /// The agent pane's one 34pt line (M/SessionPaneView.swift:208-251): padding l14 r10 on <c>card</c>
        /// with a 1pt <c>line</c> under it; the glyph, the title, the state word, the figures (which give
        /// way first), then the controls. It runs edge to edge over the pane grid's 12pt padding, its top
        /// corners following the card's inner curve.
        /// </summary>
        private Grid BuildPaneHeader()
        {
            const double inset = 12;
            var inner = DesignMetrics.Radius.Pane - DesignMetrics.Stroke.Line;
            var header = new Grid
            {
                Height = DesignMetrics.Layout.PaneHeader, Margin = new Thickness(-inset, -inset, -inset, 0), Padding = new Thickness(14, 0, 10, 0), ColumnSpacing = 8,
                Background = owner.brushes.Brush(DesignToken.Card), BorderBrush = owner.brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(inner, inner, 0, 0),
            };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
            headerTitle.Foreground = owner.brushes.Brush(DesignToken.Ink); elapsed.Foreground = owner.brushes.Brush(DesignToken.Ink2);
            var parts = new[] { headerMark.View, headerTitle, label, elapsed, paneHeaderControls };
            for (var column = 0; column < parts.Length; column++) { Grid.SetColumn(parts[column], column); header.Children.Add(parts[column]); }
            AutomationProperties.SetAutomationId(header, "pane-header-" + id); AutomationProperties.SetAutomationId(label, "pane-status-" + id);
            return paneHeader = header;
        }

        /// <summary>
        /// The header's … menu (M/SessionPaneView.swift:280-302): the pane's own menu with Copy placed
        /// before Close behind a separator (decision Q4), disabled while the pane has no logs, as on the
        /// Mac. Returns the Copy item and its separator, which a terminal pane hides together.
        /// </summary>
        private UIElement[] AddPaneMenu()
        {
            var copy = new MenuFlyoutItem { Text = Locale.Get("pane.menu.copyLog") };
            copy.Click += (_, _) => Copy(output.Text);
            AutomationProperties.SetAutomationId(copy, "pane-menu-copy-" + id);
            var separator = new MenuFlyoutSeparator();
            var menu = owner.SessionMenu(id, out var close);
            var at = menu.Items.IndexOf(close);
            menu.Items.Insert(at, separator); menu.Items.Insert(at, copy);
            menu.Opening += (_, _) => copy.IsEnabled = owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id)?.Logs.Count > 0;
            var button = paneMenuButton = new Button
            {
                Width = 22, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "\uE712", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold }, Flyout = menu, VerticalAlignment = VerticalAlignment.Center,
            };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: owner.brushes.Brush(DesignToken.Ink2));
            var name = Locale.Get("pane.menu.accessibility");
            AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); AutomationProperties.SetAutomationId(button, "pane-menu-" + id);
            paneHeaderControls.Children.Add(button);
            return [copy, separator];
        }

        private void InitializeResponsiveHeader(Grid header)
        {
            header.Loaded += (_, _) => QueuePaneHeaderLayout();
            header.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            label.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            paneHeaderControls.SizeChanged += (_, _) => QueuePaneHeaderLayout();
        }

        /// <summary>
        /// Keeps the header one line at any width: a narrow pane shows the switch as icons, and the
        /// title trims to what the glyph, the word and the controls leave (the figures take the rest
        /// and trim first, as the Mac's fade). No control ever leaves the pane.
        /// </summary>
        private void QueuePaneHeaderLayout()
        {
            if (paneHeaderLayoutQueued || !QueuePaneAlive) return;
            paneHeaderLayoutQueued = true;
            if (!Container.DispatcherQueue.TryEnqueue(() =>
            {
                paneHeaderLayoutQueued = false;
                if (!QueuePaneAlive || paneHeader is not { IsLoaded: true, ActualWidth: > 0 } header) return;
                ShowModeWords(header.ActualWidth >= NarrowHeader);
                var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
                paneHeaderControls.Measure(unbounded); label.Measure(unbounded);
                var taken = header.Padding.Left + header.Padding.Right + header.ColumnSpacing * (header.ColumnDefinitions.Count - 1)
                    + headerMark.View.Width + label.DesiredSize.Width + paneHeaderControls.DesiredSize.Width;
                var maxTitle = Math.Max(0, header.ActualWidth - taken);
                if (Math.Abs(headerTitle.MaxWidth - maxTitle) > .5 || double.IsPositiveInfinity(headerTitle.MaxWidth)) headerTitle.MaxWidth = maxTitle;
            })) paneHeaderLayoutQueued = false;
        }

        /// <summary>
        /// The header is still one 34pt row, every visible control (the … menu, both sides of the
        /// switch, the status-line toggle) lies inside it, and the switch shows its words only when
        /// the pane is not <paramref name="narrow"/>.
        /// </summary>
        private bool HeaderFitsSmoke(bool narrow)
        {
            if (paneHeader is not { ActualWidth: > 0 } header || modeSwitch is null || Math.Abs(header.ActualHeight - DesignMetrics.Layout.PaneHeader) > .5) return false;
            if (ModeWordsShown != !narrow) return false;
            return new FrameworkElement[] { paneMenuButton!, modeDefaultButton!, modeMightyButton!, statusLineToggle }.Where(control => control.Visibility == Visibility.Visible).All(control =>
            {
                if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
                var start = control.TransformToVisual(header).TransformPoint(new Point());
                var end = control.TransformToVisual(header).TransformPoint(new Point(control.ActualWidth, control.ActualHeight));
                return start.X >= -1 && end.X <= header.ActualWidth + 1 && start.Y >= -1 && end.Y <= header.ActualHeight + 1;
            });
        }
    }
}
