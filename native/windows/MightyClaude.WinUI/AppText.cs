using System.Runtime.CompilerServices;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>Which of the text setting's fonts a piece of text draws in (M/MightyCore/TextAppearance.swift).</summary>
internal enum TextArea
{
    /// <summary>Text that names no family: it keeps its own (the stock or inherited one) until an interface font is chosen.</summary>
    Default,
    /// <summary>The chrome where the app names its body family: the interface font, else <see cref="DesignMetrics.Font.Body"/>.</summary>
    Interface,
    /// <summary>The bold headings: the interface font, else <see cref="DesignMetrics.Font.Heading"/>.</summary>
    Heading,
    /// <summary>Replies, the user's turns, the composer and Mighty's prose: the conversation font, else the interface font, else the element's own.</summary>
    Transcript,
    /// <summary>Code, paths, diffs, tool output and the terminals: the code font, else <see cref="DesignMetrics.Font.Mono"/>.</summary>
    Code,
}

/// <summary>
/// The one place the WinUI app sizes and faces its words (Settings › General › 글자, Ctrl+= / Ctrl+- / Ctrl+0). Every
/// element states its size at scale 1 as <c>TextSize</c>, its line pitch as <c>TextLineHeight</c> and its font as
/// <c>TextFont</c>, instead of setting FontSize, LineHeight and FontFamily itself (scripts/check-design-tokens.js holds
/// that). Each such element is remembered weakly with those values, so a new setting reaches every open window, sheet,
/// menu and dialog at once without rebuilding it, and whatever must redraw itself (a RichEditBox's RTF, the terminal)
/// asks to be told through <see cref="Follow"/>. At the standard setting every size and family is exactly what the
/// element was given, so the app draws as it did before the setting existed.
/// </summary>
internal static class AppText
{
    /// <summary>The setting the app draws with now.</summary>
    internal static TextAppearance Current { get; private set; } = TextAppearance.Standard;
    /// <summary>Raised after a new setting has reached every remembered element.</summary>
    internal static event Action? Changed;

    /// <summary><paramref name="size"/> points at the current scale (<see cref="TextAppearance.Size"/>).</summary>
    internal static double Size(double size) => Current.Size(size);

    /// <summary>The family an area draws in now; null leaves the element's own.</summary>
    internal static string? FamilyName(TextArea area) => area switch
    {
        TextArea.Interface => Current.Family ?? DesignMetrics.Font.Body,
        TextArea.Heading => Current.Family ?? DesignMetrics.Font.Heading,
        TextArea.Transcript => Current.ResolvedTranscriptFamily,
        TextArea.Code => Current.CodeFamily ?? DesignMetrics.Font.Mono,
        _ => Current.Family,
    };

    /// <summary>A shared <see cref="FontFamily"/> for an area, as <see cref="FamilyName"/>; null leaves the element's own.</summary>
    internal static FontFamily? Family(TextArea area) => FamilyName(area) is { } name ? Face(name) : null;

    private static readonly Dictionary<string, FontFamily> faces = new(StringComparer.Ordinal);
    private static FontFamily Face(string name)
    {
        if (!faces.TryGetValue(name, out var face)) faces[name] = face = new FontFamily(name);
        return face;
    }

    /// <summary>What one element asked for, in scale-1 terms.</summary>
    private sealed class Entry
    {
        internal double? Size, LineHeight, Height;
        internal TextArea? Area;
        /// <summary>The family name this class last wrote, so a family the code set itself is never overwritten.</summary>
        internal string? Written;
        internal Action? Follow;
    }

    private static readonly ConditionalWeakTable<DependencyObject, Entry> entries = [];
    private static readonly List<WeakReference<DependencyObject>> remembered = [];
    private static int pruneAt = 1024;

    private static Entry Remember(DependencyObject element)
    {
        if (entries.TryGetValue(element, out var entry)) return entry;
        entry = new Entry(); entries.Add(element, entry);
        if (remembered.Count >= pruneAt) { remembered.RemoveAll(reference => !reference.TryGetTarget(out _)); pruneAt = Math.Max(1024, remembered.Count * 2); }
        remembered.Add(new(element));
        return entry;
    }

    /// <summary>The element's font size, font family and line height properties; null for one that has none.</summary>
    private static (DependencyProperty Size, DependencyProperty? Family, DependencyProperty? Line)? Properties(DependencyObject element) => element switch
    {
        TextBlock => (TextBlock.FontSizeProperty, TextBlock.FontFamilyProperty, TextBlock.LineHeightProperty),
        RichTextBlock => (RichTextBlock.FontSizeProperty, RichTextBlock.FontFamilyProperty, RichTextBlock.LineHeightProperty),
        Control => (Control.FontSizeProperty, Control.FontFamilyProperty, null),
        Block => (TextElement.FontSizeProperty, TextElement.FontFamilyProperty, Block.LineHeightProperty),
        TextElement => (TextElement.FontSizeProperty, TextElement.FontFamilyProperty, null),
        ContentPresenter => (ContentPresenter.FontSizeProperty, ContentPresenter.FontFamilyProperty, ContentPresenter.LineHeightProperty),
        // An icon keeps its symbol font whatever the setting: only its size follows.
        FontIcon => (FontIcon.FontSizeProperty, null, null),
        _ => null,
    };

    private static void Apply(DependencyObject element, Entry entry)
    {
        if (entry.Height is { } height && element is FrameworkElement framed) framed.Height = Size(height);
        if (Properties(element) is not { } properties) return;
        if (entry.Size is { } size) element.SetValue(properties.Size, Size(size));
        if (entry.LineHeight is { } line && properties.Line is { } lineProperty) element.SetValue(lineProperty, Size(line));
        if (properties.Family is not { } familyProperty) return;
        // Text that names no family takes the interface font only while one is chosen, and only while its family is
        // still its own or the one written here: a symbol face or an inherited one set by the code is left alone.
        // An element only followed, or only sized round its words (TextHeight), keeps its font as it is.
        var area = entry.Area ?? TextArea.Default;
        if (entry.Area is null && (entry.Size is null || element is not (TextBlock or RichTextBlock or Control or TextElement))) return;
        var local = element.ReadLocalValue(familyProperty);
        var unset = local == DependencyProperty.UnsetValue;
        if (!unset && entry.Area is null && (entry.Written is null || local is not FontFamily mine || mine.Source != entry.Written)) return;
        if (FamilyName(area) is { } name)
        {
            if (entry.Written != name || unset) element.SetValue(familyProperty, Face(name));
            entry.Written = name;
        }
        else if (entry.Written is not null)
        {
            element.ClearValue(familyProperty);
            entry.Written = null;
        }
    }

    /// <summary>
    /// Calls <paramref name="refresh"/> after each new setting while <paramref name="anchor"/> lives: for what draws its
    /// words itself (a RichEditBox's RTF, which the box repaints over when its size changes; the terminal; a measured layout).
    /// </summary>
    internal static void Follow(DependencyObject anchor, Action refresh)
    {
        var entry = Remember(anchor);
        entry.Follow += refresh;
    }

    /// <summary>Draws every remembered element with <paramref name="next"/>, then tells the followers and <see cref="Changed"/>.</summary>
    internal static void Set(TextAppearance next)
    {
        if (next == Current) return;
        Current = next;
        var alive = new List<(DependencyObject Element, Entry Entry)>();
        remembered.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var element) || !entries.TryGetValue(element, out var entry)) return true;
            alive.Add((element, entry));
            return false;
        });
        pruneAt = Math.Max(1024, remembered.Count * 2);
        foreach (var (element, entry) in alive)
        {
            // Every window of this app runs on its UI thread; an element of another thread is drawn on its own.
            if (element.DispatcherQueue is { HasThreadAccess: false } queue) { queue.TryEnqueue(() => { Apply(element, entry); entry.Follow?.Invoke(); }); continue; }
            Apply(element, entry);
        }
        foreach (var (element, entry) in alive) if (element.DispatcherQueue is null or { HasThreadAccess: true }) entry.Follow?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>
    /// The setting the app starts with, read as the main window starts (never the registry in a smoke run); the words its
    /// fields built before then take it as well.
    /// </summary>
    internal static void Load(bool smokeTest) => Set(smokeTest ? TextAppearance.Standard : TextAppearanceStore.Load());

    /// <summary>The size an element was given at scale 1, NaN when it was given none here.</summary>
    private static double BaseSize(DependencyObject element) => entries.TryGetValue(element, out var entry) && entry.Size is { } size ? size : double.NaN;

    extension(DependencyObject element)
    {
        /// <summary>The font size at scale 1; the element draws it at <see cref="Current"/>'s scale and follows each new setting.</summary>
        public double TextSize
        {
            get => BaseSize(element);
            set { var entry = Remember(element); entry.Size = value; Apply(element, entry); }
        }

        /// <summary>The line pitch at scale 1 (TextBlock, RichTextBlock, Paragraph); drawn and followed as <c>TextSize</c>.</summary>
        public double TextLineHeight
        {
            get => entries.TryGetValue(element, out var entry) && entry.LineHeight is { } line ? line : double.NaN;
            set { var entry = Remember(element); entry.LineHeight = value; Apply(element, entry); }
        }

        /// <summary>
        /// The height at scale 1 of a control drawn round one line of words (a settings pop-up or button): it grows with
        /// the words so they are never cut, and is exactly this at 100%.
        /// </summary>
        public double TextHeight
        {
            get => entries.TryGetValue(element, out var entry) && entry.Height is { } height ? height : double.NaN;
            set { var entry = Remember(element); entry.Height = value; Apply(element, entry); }
        }

        /// <summary>Which of the setting's fonts the element draws in.</summary>
        public TextArea TextFont
        {
            get => entries.TryGetValue(element, out var entry) ? entry.Area ?? TextArea.Default : TextArea.Default;
            set { var entry = Remember(element); entry.Area = value; Apply(element, entry); }
        }
    }
}
