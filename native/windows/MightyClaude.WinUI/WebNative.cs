using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.CompilerServices;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// Reads a WinUI form the window built (a settings section, the setup list, a dialog's content) as the web page's node tree
/// (native/windows/web/src/native), and plays the page's input back on the same controls: a click is the button's own
/// Invoke, a switch its Toggle, a pick the ComboBox's SelectedIndex, typed words the TextBox's Text. So every rule, every
/// handler and every redraw stays the section's C# code; the page draws what the controls say. The form stays in the
/// window's tree, behind the page and invisible, so its Loaded handlers, its timers and its templates run as when shown.
/// </summary>
internal sealed class WebNativeReader(Func<Windows.UI.Color, string?> token)
{
    private static readonly ConditionalWeakTable<object, StrongBox<int>> ids = new();
    private static readonly ConditionalWeakTable<ImageSource, string> images = new();
    private static int nextId;
    private readonly Dictionary<int, object> elements = [];

    /// <summary>A picture a section drew from bytes (the pairing QR): the page gets it as this data URL.</summary>
    internal static void RememberImage(ImageSource source, string mediaType, byte[] bytes) => images.AddOrUpdate(source, "data:" + mediaType + ";base64," + Convert.ToBase64String(bytes));

    private int Id(object element)
    {
        var id = ids.GetValue(element, _ => new StrongBox<int>(Interlocked.Increment(ref nextId))).Value;
        elements[id] = element;
        return id;
    }

    /// <summary>The element the page named in the tree it was sent last.</summary>
    internal object? Element(int id) => elements.GetValueOrDefault(id);

    /// <summary>Starts a new read: only what it reaches can be acted on.</summary>
    internal void Begin() => elements.Clear();

    private string? Tone(Brush? brush) => brush is SolidColorBrush { Opacity: > 0 } solid && solid.Color.A > 0 ? token(solid.Color) : null;

    internal static string Words(object? content) => content switch
    {
        null => "",
        string text => text,
        TextBlock block => BlockText(block),
        ContentControl control => Words(control.Content),
        Panel panel => string.Join(" ", panel.Children.Select(Words).Where(w => w.Length > 0)),
        Border border => Words(border.Child),
        Viewbox box => Words(box.Child),
        UIElement => "",
        _ => content.ToString() ?? "",
    };

    private static string BlockText(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text) || block.Inlines.Count == 0) return block.Text ?? "";
        return string.Concat(block.Inlines.Select(InlineText));
    }

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(InlineText)),
        _ => "",
    };

    /// <summary>A size as the form wrote it, before the text setting scaled it: the page scales its own sizes.</summary>
    private static double Unscaled(double size) => Math.Round(size / AppText.Current.Scale * 2, MidpointRounding.AwayFromZero) / 2;

    private static object Edges(Thickness t) => new[] { t.Left, t.Top, t.Right, t.Bottom };
    private static bool Zero(Thickness t) => t.Left == 0 && t.Top == 0 && t.Right == 0 && t.Bottom == 0;

    private static string? Align(HorizontalAlignment alignment) => alignment switch
    {
        HorizontalAlignment.Left => "start", HorizontalAlignment.Center => "center", HorizontalAlignment.Right => "end", _ => null,
    };

    private static string? VAlign(VerticalAlignment alignment) => alignment switch
    {
        VerticalAlignment.Top => "start", VerticalAlignment.Center => "center", VerticalAlignment.Bottom => "end", _ => null,
    };

    private static string Track(GridLength length) => length.GridUnitType switch
    {
        GridUnitType.Auto => "auto",
        GridUnitType.Star => "minmax(0," + length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "fr)",
        _ => length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px",
    };

    /// <summary>The tree under <paramref name="element"/>, or null when nothing of it shows.</summary>
    internal Dictionary<string, object?>? Node(object? element, bool enabled = true)
    {
        if (element is null) return null;
        if (element is string text) return text.Length == 0 ? null : new() { ["t"] = "text", ["text"] = text };
        if (element is not FrameworkElement e || e.Visibility != Visibility.Visible || e.Opacity == 0) return null;
        enabled = enabled && (e is not Control c || c.IsEnabled);
        var node = Read(e, enabled);
        if (node is null) return null;
        if (AutomationProperties.GetAutomationId(e) is { Length: > 0 } aid) node["aid"] = aid;
        if (ToolTipService.GetToolTip(e) is string help && help.Length > 0) node["help"] = help;
        if (!Zero(e.Margin)) node["margin"] = Edges(e.Margin);
        if (Align(e.HorizontalAlignment) is { } h) node["ha"] = h;
        if (VAlign(e.VerticalAlignment) is { } v) node["va"] = v;
        if (!double.IsNaN(e.Width) && e is not TextBlock) node["w"] = e.Width;
        if (!double.IsNaN(e.Height) && e is Image or Border or Panel) node["h"] = e.Height;
        if (e.MaxWidth is > 0 and < double.PositiveInfinity) node["maxW"] = e.MaxWidth;
        if (e.MaxHeight is > 0 and < double.PositiveInfinity) node["maxH"] = e.MaxHeight;
        if (e.MinWidth > 0 && e is not Control) node["minW"] = e.MinWidth;
        if (e.Opacity < 0.75) node["dim"] = true;
        if (e is Control && !enabled) node["disabled"] = true;
        return node;
    }

    private List<Dictionary<string, object?>> Nodes(IEnumerable<object?> children, bool enabled) => children.Select(child => Node(child, enabled)).OfType<Dictionary<string, object?>>().ToList();

    private Dictionary<string, object?>? Read(FrameworkElement e, bool enabled)
    {
        switch (e)
        {
            case TextBlock block:
            {
                var words = BlockText(block);
                if (words.Length == 0) return null;
                var node = new Dictionary<string, object?> { ["t"] = "text", ["text"] = words, ["size"] = Unscaled(block.FontSize) };
                if (block.FontWeight.Weight >= 600) node["weight"] = "bold"; else if (block.FontWeight.Weight >= 500) node["weight"] = "medium";
                if (block.FontFamily?.Source is { } face && (face.Contains("Mono", StringComparison.OrdinalIgnoreCase) || face.Contains("Consol", StringComparison.OrdinalIgnoreCase) || face.Contains("Code", StringComparison.OrdinalIgnoreCase))) node["mono"] = true;
                if (Tone(block.Foreground) is { } ink) node["ink"] = ink;
                if (block.TextWrapping == TextWrapping.NoWrap) node["nowrap"] = true;
                if (block.IsTextSelectionEnabled) node["selectable"] = true;
                return node;
            }
            case RichTextBlock rich:
            {
                var words = string.Join("\n", rich.Blocks.OfType<Paragraph>().Select(p => string.Concat(p.Inlines.Select(InlineText))));
                return words.Length == 0 ? null : new() { ["t"] = "text", ["text"] = words, ["size"] = Unscaled(rich.FontSize), ["selectable"] = true };
            }
            case RichEditBox editor:
            {
                // A document (a plan, a sample): its words, without the RTF's formatting.
                editor.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var words);
                words = words.TrimEnd('\r', '\n').Replace("\r", "\n", StringComparison.Ordinal);
                return words.Length == 0 ? null : new() { ["t"] = "text", ["text"] = words, ["size"] = Unscaled(editor.FontSize), ["selectable"] = true };
            }
            case FontIcon icon:
                return new() { ["t"] = "glyph", ["glyph"] = icon.Glyph, ["size"] = Unscaled(icon.FontSize), ["ink"] = Tone(icon.Foreground) };
            case SymbolIcon:
                return null;
            case Image image:
            {
                string? src = image.Source is { } source && images.TryGetValue(source, out var url) ? url : null;
                if (src is null && image.Source is BitmapImage { UriSource: { } uri } && uri.Scheme is "http" or "https") src = uri.AbsoluteUri;
                return src is null ? null : new() { ["t"] = "image", ["src"] = src };
            }
            case Ellipse dot when Tone(dot.Fill) is { } fill:
                return new() { ["t"] = "dot", ["fill"] = fill, ["size"] = double.IsNaN(dot.Width) ? 8 : dot.Width };
            case Shape:
                return null;
            case ProgressRing ring:
                return ring.IsActive ? new() { ["t"] = "spinner" } : null;
            case ProgressBar bar:
                return new() { ["t"] = "progress", ["value"] = bar.Value, ["max"] = bar.Maximum, ["indeterminate"] = bar.IsIndeterminate };
            case HyperlinkButton link:
                return new() { ["t"] = "link", ["id"] = Id(link), ["label"] = Words(link.Content) is { Length: > 0 } linkWords ? linkWords : AutomationProperties.GetName(link) };
            case CheckBox check:
                return new() { ["t"] = "check", ["id"] = Id(check), ["on"] = check.IsChecked == true, ["label"] = Words(check.Content) };
            case RadioButton radio:
                return new() { ["t"] = "radio", ["id"] = Id(radio), ["on"] = radio.IsChecked == true, ["label"] = Words(radio.Content) };
            case ToggleSwitch toggle:
                return new() { ["t"] = "switch", ["id"] = Id(toggle), ["on"] = toggle.IsOn, ["name"] = Words(toggle.Header) is { Length: > 0 } h ? h : AutomationProperties.GetName(toggle) };
            case ToggleButton toggle:
                return new() { ["t"] = "switch", ["id"] = Id(toggle), ["on"] = toggle.IsChecked == true, ["name"] = AutomationProperties.GetName(toggle) };
            case ComboBox combo:
                return new()
                {
                    ["t"] = "select", ["id"] = Id(combo), ["index"] = combo.SelectedIndex, ["name"] = AutomationProperties.GetName(combo),
                    ["items"] = combo.Items.Select(item => Words(item is ComboBoxItem box ? box.Content : item)).ToList(),
                };
            case TextBox field:
                return new()
                {
                    ["t"] = "field", ["id"] = Id(field), ["text"] = field.Text, ["placeholder"] = field.PlaceholderText, ["multiline"] = field.AcceptsReturn,
                    ["readonly"] = field.IsReadOnly, ["name"] = AutomationProperties.GetName(field) is { Length: > 0 } n ? n : Words(field.Header), ["mono"] = field.FontFamily?.Source?.Contains("Mono", StringComparison.OrdinalIgnoreCase) == true,
                };
            case PasswordBox secret:
                return new() { ["t"] = "field", ["id"] = Id(secret), ["text"] = secret.Password, ["password"] = true, ["placeholder"] = secret.PlaceholderText, ["name"] = AutomationProperties.GetName(secret) };
            case Slider slider:
                return new() { ["t"] = "slider", ["id"] = Id(slider), ["value"] = slider.Value, ["min"] = slider.Minimum, ["max"] = slider.Maximum, ["step"] = slider.StepFrequency, ["name"] = AutomationProperties.GetName(slider) };
            case Button button:
            {
                var name = AutomationProperties.GetName(button);
                var node = new Dictionary<string, object?> { ["t"] = "button", ["id"] = Id(button), ["name"] = name };
                if (button.Content is string or TextBlock) node["label"] = Words(button.Content);
                else if (Node(button.Content, enabled) is { } inner) node["c"] = new[] { inner };
                if (Tone(button.Background) is "accent") node["accent"] = true;
                if (Tone(button.Foreground) is "err-text") node["destructive"] = true;
                if (button.Flyout is MenuFlyout menu) { node["t"] = "menu"; node["items"] = MenuItems(menu.Items); }
                return node;
            }
            case ScrollViewer scroll:
                return Node(scroll.Content, enabled);
            case Expander expander:
                return new() { ["t"] = "expander", ["id"] = Id(expander), ["open"] = expander.IsExpanded, ["header"] = Node(expander.Header, enabled), ["c"] = expander.IsExpanded ? Nodes([expander.Content], enabled) : null };
            case InfoBar info:
                return info.IsOpen ? new() { ["t"] = "info", ["title"] = info.Title, ["message"] = info.Message, ["severity"] = info.Severity.ToString().ToLowerInvariant() } : null;
            case ListView list:
                return new()
                {
                    ["t"] = "list", ["id"] = Id(list), ["index"] = list.SelectedIndex,
                    ["items"] = list.Items.Select(item => Node(item is ListViewItem row ? row.Content : item, enabled) ?? new() { ["t"] = "text", ["text"] = Words(item) }).ToList(),
                };
            case ItemsControl items:
                return new() { ["t"] = "stack", ["dir"] = "v", ["c"] = Nodes(items.Items, enabled) };
            case ContentControl control:
                return Node(control.Content, enabled);
            case Viewbox box:
                return Node(box.Child, enabled);
            case Border border:
            {
                var fill = Tone(border.Background); var line = border.BorderThickness; var lined = !Zero(line) && Tone(border.BorderBrush) is not null;
                if (border.Child is null)
                    return fill is not null && (border.Height <= 2 || border.Width <= 2) ? new() { ["t"] = "rule", ["fill"] = fill, ["vertical"] = border.Width <= 2 } : null;
                var child = Node(border.Child, enabled);
                if (child is null) return null;
                if (fill is null && !lined && Zero(border.Padding) && border.CornerRadius.TopLeft == 0) return child;
                var node = new Dictionary<string, object?> { ["t"] = "box", ["c"] = new[] { child } };
                if (fill is not null) node["fill"] = fill;
                if (lined) { node["line"] = Edges(line); node["lineInk"] = Tone(border.BorderBrush); }
                if (!Zero(border.Padding)) node["pad"] = Edges(border.Padding);
                if (border.CornerRadius.TopLeft > 0) node["radius"] = border.CornerRadius.TopLeft;
                return node;
            }
            case Grid grid when grid.Tag is Action<string>:
            {
                // The form's segmented picker (SettingsSegmented): its segments are the toggles in it.
                var segments = grid.Children.OfType<ToggleButton>().Select(segment => new Dictionary<string, object?> { ["id"] = Id(segment), ["label"] = Words(segment.Content), ["on"] = segment.IsChecked == true }).ToList();
                return new() { ["t"] = "segmented", ["name"] = AutomationProperties.GetName(grid), ["options"] = segments, ["disabled"] = grid.Opacity < 0.75 || segments.Count == 0 };
            }
            case Grid grid:
            {
                var cells = grid.Children.OfType<FrameworkElement>().Select(child => (child, node: Node(child, enabled))).Where(x => x.node is not null).Select(x =>
                {
                    var node = x.node!;
                    node["row"] = Grid.GetRow(x.child); node["col"] = Grid.GetColumn(x.child);
                    if (Grid.GetRowSpan(x.child) > 1) node["rowSpan"] = Grid.GetRowSpan(x.child);
                    if (Grid.GetColumnSpan(x.child) > 1) node["colSpan"] = Grid.GetColumnSpan(x.child);
                    return node;
                }).ToList();
                if (cells.Count == 0) return null;
                var node = new Dictionary<string, object?>
                {
                    ["t"] = "grid", ["c"] = cells,
                    ["cols"] = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Select(d => Track(d.Width)).ToList() : null,
                    ["rows"] = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Select(d => d.Height.GridUnitType == GridUnitType.Star ? "auto" : Track(d.Height)).ToList() : null,
                    ["gap"] = new[] { grid.RowSpacing, grid.ColumnSpacing },
                };
                if (!Zero(grid.Padding)) node["pad"] = Edges(grid.Padding);
                if (Tone(grid.Background) is { } fill) node["fill"] = fill;
                return node;
            }
            case StackPanel stack:
            {
                var children = Nodes(stack.Children, enabled);
                if (children.Count == 0) return null;
                var node = new Dictionary<string, object?> { ["t"] = "stack", ["dir"] = stack.Orientation == Orientation.Horizontal ? "h" : "v", ["gap"] = stack.Spacing, ["c"] = children };
                if (!Zero(stack.Padding)) node["pad"] = Edges(stack.Padding);
                if (Tone(stack.Background) is { } fill) node["fill"] = fill;
                return node;
            }
            case Panel panel:
            {
                var children = Nodes(panel.Children, enabled);
                return children.Count == 0 ? null : new() { ["t"] = "stack", ["dir"] = "v", ["c"] = children };
            }
            default:
                return null;
        }
    }

    private List<Dictionary<string, object?>> MenuItems(IEnumerable<MenuFlyoutItemBase> items) => items.Select(item => item switch
    {
        MenuFlyoutSeparator => new Dictionary<string, object?> { ["sep"] = true },
        MenuFlyoutSubItem sub => new() { ["label"] = sub.Text, ["items"] = MenuItems(sub.Items), ["disabled"] = !sub.IsEnabled },
        ToggleMenuFlyoutItem toggle => new() { ["id"] = Id(toggle), ["label"] = toggle.Text, ["checked"] = toggle.IsChecked, ["disabled"] = !toggle.IsEnabled },
        MenuFlyoutItem entry => new() { ["id"] = Id(entry), ["label"] = entry.Text, ["disabled"] = !entry.IsEnabled },
        _ => null,
    }).Where(item => item is not null && (item.ContainsKey("sep") || item["label"] is string { Length: > 0 })).Select(item => item!).ToList();

    /// <summary>
    /// The page's input on element <paramref name="id"/>: <c>invoke</c> (a button, a link, a menu row), <c>toggle</c> (a switch,
    /// a check box, a segment, a radio button, an expander), <c>select</c> (a pop-up's or a list's row by index), <c>text</c>
    /// (a field's words) or <c>value</c> (a slider). False when the element is gone or does not take that input.
    /// </summary>
    internal bool Act(int id, string op, System.Text.Json.JsonElement value)
    {
        if (Element(id) is not { } element) return false;
        switch (op, element)
        {
            case ("invoke", HyperlinkButton { NavigateUri: { } uri }) when uri.Scheme is "http" or "https":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                return true;
            case ("invoke", Button { IsEnabled: true } button):
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                return true;
            case ("invoke", ToggleMenuFlyoutItem { IsEnabled: true } toggle):
                ((IToggleProvider)new ToggleMenuFlyoutItemAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                return true;
            case ("invoke", MenuFlyoutItem { IsEnabled: true } item):
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                return true;
            case ("toggle", RadioButton { IsEnabled: true } radio):
                radio.IsChecked = true;
                return true;
            case ("toggle", ToggleButton { IsEnabled: true } toggle):
                ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                return true;
            case ("toggle", ToggleSwitch { IsEnabled: true } toggle):
                toggle.IsOn = !toggle.IsOn;
                return true;
            case ("toggle", Expander expander):
                expander.IsExpanded = !expander.IsExpanded;
                return true;
            case ("select", ComboBox { IsEnabled: true } combo) when value.TryGetInt32(out var index) && index >= 0 && index < combo.Items.Count:
                combo.SelectedIndex = index;
                return true;
            case ("select", ListView list) when value.TryGetInt32(out var row) && row >= -1 && row < list.Items.Count:
                list.SelectedIndex = row;
                return true;
            case ("text", TextBox { IsEnabled: true, IsReadOnly: false } field) when value.ValueKind == System.Text.Json.JsonValueKind.String:
                field.Text = value.GetString() ?? ""; field.SelectionStart = field.Text.Length;
                return true;
            case ("text", PasswordBox { IsEnabled: true } secret) when value.ValueKind == System.Text.Json.JsonValueKind.String:
                secret.Password = value.GetString() ?? "";
                return true;
            case ("value", Slider { IsEnabled: true } slider) when value.ValueKind == System.Text.Json.JsonValueKind.Number:
                slider.Value = Math.Clamp(value.GetDouble(), slider.Minimum, slider.Maximum);
                return true;
            default:
                return false;
        }
    }
}
