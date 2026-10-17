using MightyClaude.Core;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MightyClaude.WinUI;

/// <summary>
/// The text setting's controls (M/TextSettings.swift, M/SettingsViews.swift textGroup): Settings › General › 글자 and
/// Ctrl+= / Ctrl+- / Ctrl+0. <see cref="AppText"/> carries the setting to every word; this saves it and keeps the
/// pieces that lay themselves out (the panes' RTF, the terminal, the sidebar) in step.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The window's shortcuts and its answer to a new setting; called once the shell is built.</summary>
    private void InitTextAppearance()
    {
        AddTextSizeShortcuts(root);
        AppText.Changed += TextAppearanceChanged;
    }

    /// <summary>A new text setting: saved per user (never by a smoke run) and drawn in every window at once.</summary>
    internal void SetTextAppearance(TextAppearance next)
    {
        if (next == AppText.Current) return;
        if (!options.SmokeTest) TextAppearanceStore.Save(next);
        AppText.Set(next);
    }

    /// <summary>What the remembered words cannot do themselves: each pane's documents and terminal, then the window's own layout.</summary>
    private void TextAppearanceChanged()
    {
        if (closing) return;
        foreach (var view in views.Values) view.FollowTextAppearance();
        Render();
    }

    /// <summary>
    /// Ctrl+= (and Ctrl++ on the number pad) a size larger, Ctrl+- a size smaller, Ctrl+0 back to 100% with the fonts kept
    /// (M/MightyClaudeApp.swift View menu). A focused web view keeps the keys: in a terminal Ctrl+- is the shell's undo
    /// (^_), and in the browser pane they zoom its page.
    /// </summary>
    internal void AddTextSizeShortcuts(UIElement target)
    {
        void Bind(VirtualKey key, Func<TextAppearance, TextAppearance> next)
        {
            var shortcut = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            shortcut.Invoked += (_, args) =>
            {
                if (target.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is WebView2) return;
                args.Handled = true; SetTextAppearance(next(AppText.Current));
            };
            target.KeyboardAccelerators.Add(shortcut);
        }
        Bind(EqualsKey, text => text.Stepped(1)); Bind(VirtualKey.Add, text => text.Stepped(1));
        Bind(MinusKey, text => text.Stepped(-1)); Bind(VirtualKey.Subtract, text => text.Stepped(-1));
        Bind(VirtualKey.Number0, text => text.WithScale(1)); Bind(VirtualKey.NumberPad0, text => text.WithScale(1));
    }

    /// <summary>The = / + key and the - key of the main keyboard (VK_OEM_PLUS, VK_OEM_MINUS), on every layout the app is used with.</summary>
    private const VirtualKey EqualsKey = (VirtualKey)187, MinusKey = (VirtualKey)189;

    internal bool HasTextSizeShortcuts => new[] { EqualsKey, MinusKey, VirtualKey.Number0 }.All(key => root.KeyboardAccelerators.Any(a => a.Key == key && a.Modifiers == VirtualKeyModifiers.Control));

    private static (IReadOnlyList<string> All, IReadOnlyList<string> Code)? installedFamilies;

    /// <summary>
    /// The installed families for the pickers, by their English names (what the registry keeps and RTF can name), and of those
    /// the monospaced ones and the known code faces (<see cref="TextAppearance.CodeFamilies"/>). Symbol fonts, whose letters
    /// would turn every label into pictures, and vertical (@) faces are left out, as on the Mac. Read once per run.
    /// </summary>
    private static (IReadOnlyList<string> All, IReadOnlyList<string> Code) InstalledFamilies()
    {
        if (installedFamilies is { } known) return known;
        var fonts = new List<(string Family, bool FixedPitch)>();
        try
        {
            using var set = CanvasFontSet.GetSystemFontSet();
            foreach (var face in set.Fonts)
            {
                using (face)
                {
                    if (face.IsSymbolFont) continue;
                    var names = face.FamilyNames;
                    var name = names.FirstOrDefault(pair => pair.Key.Equals("en-us", StringComparison.OrdinalIgnoreCase)).Value ?? names.Values.FirstOrDefault();
                    if (name is { Length: > 0 }) fonts.Add((name, face.IsMonospaced));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without the font set the plain family list still names what can be chosen; code keeps to the known faces.
            try { fonts = CanvasTextFormat.GetSystemFontFamilies().Select(name => (name, false)).ToList(); } catch (Exception inner) when (inner is not OutOfMemoryException) { fonts = []; }
        }
        // Each face is listed once per family, and a family is fixed-pitch when its faces are.
        var families = fonts.GroupBy(font => font.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Family: group.Key, FixedPitch: group.All(font => font.FixedPitch)))
            .Where(font => font.Family[0] is not '@' and not '.')
            .OrderBy(font => font.Family, StringComparer.OrdinalIgnoreCase).ToList();
        var result = ((IReadOnlyList<string>)families.Select(font => font.Family).ToList(), TextAppearance.CodeFamilies(families));
        installedFamilies = result;
        return result;
    }

    /// <summary>
    /// 글자 (M/SettingsViews.swift textGroup): the size from 85% to 150%, the interface, conversation and code fonts each over a
    /// line of words in that font, the note about the shortcuts, and the button back to the standard setting. Every choice
    /// applies at once; a change made elsewhere (a shortcut, the button) shows in the pickers.
    /// </summary>
    private StackPanel BuildTextSection()
    {
        var rows = new StackPanel(); var b = brushes;
        var (all, code) = InstalledFamilies();
        var syncing = false;

        ComboBox Picker(string label, string id)
        {
            var picker = SettingsPopup(new ComboBox()); picker.HorizontalAlignment = HorizontalAlignment.Right; picker.MaxWidth = 240;
            AutomationProperties.SetAutomationId(picker, id); AutomationProperties.SetName(picker, label);
            return picker;
        }
        // The pop-up with the Mac's chevron chip, which ends at the row's edge as the pet picker's does.
        Grid Framed(ComboBox picker)
        {
            var frame = SettingsPopupFrame(picker); frame.Margin = new Thickness(0, 0, -SettingsPopupChipInset, 0);
            return frame;
        }
        static void Fill(ComboBox picker, string none, IReadOnlyList<string> families, string? saved)
        {
            picker.Items.Add(new ComboBoxItem { Content = none, Tag = "" });
            // A saved family that is no longer installed stays listed; it draws in the stand-in.
            foreach (var family in saved is not null && !families.Contains(saved, StringComparer.OrdinalIgnoreCase) ? families.Append(saved) : families)
                picker.Items.Add(new ComboBoxItem { Content = family, Tag = family });
        }
        static void Show(ComboBox picker, string value) =>
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals((string)item.Tag, value, StringComparison.OrdinalIgnoreCase)) ?? picker.Items[0];
        Border Preview(TextArea area, double size, string id)
        {
            var words = new TextBlock { Text = Locale.Get("settings.text.preview"), TextSize = size, TextFont = area, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(words, id);
            return new Border { Child = words, Padding = new Thickness(DesignMetrics.Spacing.Sm), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), Background = b.Subtle };
        }
        void Choose(ComboBox picker, Func<TextAppearance, string?, TextAppearance> next) =>
            picker.SelectionChanged += (_, _) =>
            {
                if (syncing || picker.SelectedItem is not ComboBoxItem { Tag: string value }) return;
                SetTextAppearance(next(AppText.Current, value.Length == 0 ? null : value));
            };

        var sizeLabel = Locale.Get("settings.text.sizeLabel");
        var size = Picker(sizeLabel, "settings-text-size");
        foreach (var scale in TextAppearance.Scales)
        {
            var percent = new TextAppearance(scale).Percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            size.Items.Add(new ComboBoxItem { Content = scale == 1 ? Locale.Get("settings.text.sizeStandard", new Dictionary<string, string> { ["percent"] = percent }) : percent + "%", Tag = scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
        }
        size.SelectionChanged += (_, _) =>
        {
            if (syncing || size.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            SetTextAppearance(AppText.Current.WithScale(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
        };
        SettingsRow(rows, SettingsLabeled(SettingsText(sizeLabel), Framed(size)));

        var current = AppText.Current;
        var uiLabel = Locale.Get("settings.text.uiFontLabel");
        var ui = Picker(uiLabel, "settings-text-font"); Fill(ui, Locale.Get("settings.text.fontSystem"), all, current.Family); Choose(ui, (text, family) => text.WithFamily(family));
        var uiBlock = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        uiBlock.Children.Add(SettingsLabeled(SettingsText(uiLabel), Framed(ui))); uiBlock.Children.Add(Preview(TextArea.Interface, DesignMetrics.Type.Body, "settings-text-preview"));
        SettingsRow(rows, uiBlock);

        var transcriptLabel = Locale.Get("settings.text.transcriptFontLabel");
        var transcript = Picker(transcriptLabel, "settings-text-transcript-font"); Fill(transcript, Locale.Get("settings.text.sameAsInterface"), all, current.TranscriptFamily); Choose(transcript, (text, family) => text.WithTranscriptFamily(family));
        var transcriptBlock = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        transcriptBlock.Children.Add(SettingsLabeled(SettingsText(transcriptLabel), Framed(transcript))); transcriptBlock.Children.Add(Preview(TextArea.Transcript, DesignMetrics.Type.Body, "settings-text-transcript-preview"));
        SettingsRow(rows, transcriptBlock);

        var codeLabel = Locale.Get("settings.text.codeFontLabel");
        var codePicker = Picker(codeLabel, "settings-text-code-font"); Fill(codePicker, Locale.Get("settings.text.monospaceSystem"), code, current.CodeFamily); Choose(codePicker, (text, family) => text.WithCodeFamily(family));
        var codeBlock = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        codeBlock.Children.Add(SettingsLabeled(SettingsText(codeLabel), Framed(codePicker))); codeBlock.Children.Add(Preview(TextArea.Code, TextAppearance.TerminalBaseSize, "settings-text-code-preview"));
        SettingsRow(rows, codeBlock);

        var reset = SettingsPush(Button(Locale.Get("settings.text.reset"), () => { SetTextAppearance(TextAppearance.Standard); return Task.CompletedTask; }));
        AutomationProperties.SetAutomationId(reset, "settings-text-reset");
        SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.text.noteWindows"), 11, DesignToken.Ink2), reset));

        // The pickers show the setting however it was made; the pop-ups keep their own handlers out of it.
        void Sync()
        {
            syncing = true;
            var text = AppText.Current;
            Show(size, text.Scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Show(ui, text.Family ?? ""); Show(transcript, text.TranscriptFamily ?? ""); Show(codePicker, text.CodeFamily ?? "");
            reset.IsEnabled = !text.IsStandard;
            syncing = false;
        }
        Sync(); AppText.Follow(rows, Sync);
        return rows;
    }

    private sealed partial class PaneView
    {
        /// <summary>A new text setting reaches what this pane draws as a document: its Markdown previews, plan and answers, and the terminal.</summary>
        internal void FollowTextAppearance()
        {
            RefreshTerminalTheme();
            RethemeFilesMarkdown(always: true); RethemePlanViews(); RethemeStructureAnswers(always: true);
            // The pill words are measured on a block kept out of the window.
            AppText.Refresh(pillMeasure);
            ArrangeComposer();
        }

        /// <summary>The editor's one line and its six at the most grow with the text setting (exactly 20 and 106.5 at 100%).</summary>
        private void FitComposerToText()
        {
            var scale = AppText.Current.Scale;
            input.MinHeight = ComposerLine * scale; input.MaxHeight = (ComposerLine + 5 * ComposerLineStep) * scale;
        }

        /// <summary>The composer's editor and the conversation's box, for the text setting's smoke.</summary>
        internal (TextBox Input, RichEditBox Transcript) TextSamples => (input, output.View);
    }
}
