using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The text setting (Settings › General › 글자, AppText): at 100% with no fonts chosen every size and family is exactly
    /// the one the code gives; the section's size pop-up redraws sample words, the window's own words, the composer and the
    /// conversation at once; the interface, conversation and code fonts each reach their words and no others; Ctrl+=, Ctrl+-
    /// and Ctrl+0 are bound; the reset button brings every one of them back exactly. The setting is standard again after.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunTextAppearanceSmoke(PaneView pane)
    {
        var (input, transcript) = pane.TextSamples;
        // Samples made the way every screen makes its words.
        var body = new TextBlock { Text = "Sample", TextSize = DesignMetrics.Type.Body };
        var code = new TextBlock { Text = "Sample", TextSize = DesignMetrics.Type.Mono, TextFont = TextArea.Code };
        var lined = new TextBlock { Text = "Sample", TextSize = 12, TextLineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        var icon = new FontIcon { Glyph = "", TextSize = 9 };
        var heading = new TextBlock { Text = "Sample", TextSize = DesignMetrics.Type.Header, TextFont = TextArea.Heading };
        var section = BuildTextSection();
        T Part<T>(string id) where T : FrameworkElement => VisualChildren(section).OfType<T>().First(element => AutomationProperties.GetAutomationId(element) == id);
        static string? Family(DependencyObject element, DependencyProperty property) => (element.ReadLocalValue(property) as FontFamily)?.Source;
        static bool Unset(DependencyObject element, DependencyProperty property) => element.ReadLocalValue(property) == DependencyProperty.UnsetValue;
        static void Pick(ComboBox picker, Func<string, bool> wanted) =>
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => wanted((string)item.Tag)) ?? picker.Items.OfType<ComboBoxItem>().Skip(1).First();
        void Standard(string when)
        {
            Require(body.FontSize == DesignMetrics.Type.Body && code.FontSize == DesignMetrics.Type.Mono && lined.FontSize == 12 && lined.LineHeight == 15 && icon.FontSize == 9 && heading.FontSize == DesignMetrics.Type.Header,
                $"{when}: every size must be the one given at 100%; got body {body.FontSize}, code {code.FontSize}, line {lined.FontSize}/{lined.LineHeight}, icon {icon.FontSize}, heading {heading.FontSize}");
            Require(Unset(body, TextBlock.FontFamilyProperty) && Family(code, TextBlock.FontFamilyProperty) == DesignMetrics.Font.Mono && Family(heading, TextBlock.FontFamilyProperty) == DesignMetrics.Font.Heading,
                $"{when}: words that name no font keep their own, code is '{DesignMetrics.Font.Mono}' and headings '{DesignMetrics.Font.Heading}'; got '{Family(body, TextBlock.FontFamilyProperty)}', '{Family(code, TextBlock.FontFamilyProperty)}', '{Family(heading, TextBlock.FontFamilyProperty)}'");
            Require(input.FontSize == DesignMetrics.Type.Body && input.MinHeight == PaneView.ComposerLine && input.MaxHeight == PaneView.ComposerLine + 5 * PaneView.ComposerLineStep && Unset(input, Control.FontFamilyProperty) && transcript.FontSize == DesignMetrics.Type.Body && Unset(transcript, Control.FontFamilyProperty),
                $"{when}: the composer and the conversation must be {DesignMetrics.Type.Body}pt in their own font, the editor {PaneView.ComposerLine} to {PaneView.ComposerLine + 5 * PaneView.ComposerLineStep} high; got {input.FontSize}pt min {input.MinHeight} max {input.MaxHeight}, {transcript.FontSize}pt");
            Require(status.FontSize == DesignMetrics.Type.Small, $"{when}: the status bar's words must be {DesignMetrics.Type.Small}pt; got {status.FontSize}");
        }
        root.Children.Add(section); Grid.SetColumn(section, 1); Grid.SetRow(section, 1);
        try
        {
            Require(HasTextSizeShortcuts, "Ctrl+=, Ctrl+- and Ctrl+0 must be bound on the window root; no such accelerators were found");
            Require(AppText.Current.IsStandard, "a smoke run must start at the standard text setting, never the registry's: got " + AppText.Current);
            Standard("at 100%");
            section.UpdateLayout();

            // 120%: 13 → 15.5, 11 → 13, 12 on 15 → 14.5 on 18, 9 → 11, 17 → 20.5, 10 → 12.
            var size = Part<ComboBox>("settings-text-size");
            size.SelectedItem = size.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "1.2");
            await WaitUI(() => AppText.Current.Scale == 1.2, () => "choosing 120% must set the scale; got " + AppText.Current.Scale);
            Require(body.FontSize == 15.5 && code.FontSize == 13 && lined.FontSize == 14.5 && lined.LineHeight == 18 && icon.FontSize == 11 && heading.FontSize == 20.5 && status.FontSize == 12,
                $"at 120% each size must be its 100% size scaled to the half point; got body {body.FontSize}, code {code.FontSize}, line {lined.FontSize}/{lined.LineHeight}, icon {icon.FontSize}, heading {heading.FontSize}, status {status.FontSize}");
            Require(input.FontSize == 15.5 && Math.Abs(input.MinHeight - PaneView.ComposerLine * 1.2) < 0.01 && transcript.FontSize == 15.5,
                $"at 120% the composer and the conversation must be 15.5pt and the editor {PaneView.ComposerLine * 1.2} high; got {input.FontSize}, {input.MinHeight}, {transcript.FontSize}");
            var preview = Part<TextBlock>("settings-text-preview");
            Require(preview.FontSize == 15.5, "the section's preview must follow the size; got " + preview.FontSize);

            // The interface font reaches the words that name none and the headings, and the conversation, not code.
            var ui = Part<ComboBox>("settings-text-font");
            Pick(ui, family => family == "Arial");
            var chosen = AppText.Current.Family;
            Require(chosen is not null && Family(body, TextBlock.FontFamilyProperty) == chosen && Family(heading, TextBlock.FontFamilyProperty) == chosen && Family(input, Control.FontFamilyProperty) == chosen && Family(code, TextBlock.FontFamilyProperty) == DesignMetrics.Font.Mono,
                $"an interface font '{chosen}' must reach plain words, headings and the composer and leave code alone; got '{Family(body, TextBlock.FontFamilyProperty)}', '{Family(heading, TextBlock.FontFamilyProperty)}', '{Family(input, Control.FontFamilyProperty)}', '{Family(code, TextBlock.FontFamilyProperty)}'");
            Require(Family(icon, FontIcon.FontFamilyProperty) is null, "an icon must keep its symbol font; got " + Family(icon, FontIcon.FontFamilyProperty));

            // The conversation font: the composer and the conversation only.
            var conversation = Part<ComboBox>("settings-text-transcript-font");
            Pick(conversation, family => family == "Georgia");
            var spoken = AppText.Current.TranscriptFamily;
            Require(spoken is not null && Family(input, Control.FontFamilyProperty) == spoken && Family(transcript, Control.FontFamilyProperty) == spoken && Family(body, TextBlock.FontFamilyProperty) == chosen,
                $"a conversation font '{spoken}' must reach the composer and the conversation and not the chrome; got '{Family(input, Control.FontFamilyProperty)}', '{Family(transcript, Control.FontFamilyProperty)}', '{Family(body, TextBlock.FontFamilyProperty)}'");

            // The code font: code only.
            var codes = Part<ComboBox>("settings-text-code-font");
            Require(codes.Items.Count > 1, "the code font list must offer the installed monospaced families");
            Pick(codes, family => family == "Courier New");
            var typed = AppText.Current.CodeFamily;
            Require(typed is not null && Family(code, TextBlock.FontFamilyProperty) == typed && Family(body, TextBlock.FontFamilyProperty) == chosen,
                $"a code font '{typed}' must reach code and nothing else; got '{Family(code, TextBlock.FontFamilyProperty)}', '{Family(body, TextBlock.FontFamilyProperty)}'");

            // The shortcuts' steps: one larger, then back to 100% with the fonts kept.
            SetTextAppearance(AppText.Current.Stepped(1));
            Require(AppText.Current.Scale == 1.3 && body.FontSize == 17 && ReferenceEquals(size.SelectedItem, size.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "1.3")),
                $"a step larger must be 130% and show in the pop-up; got {AppText.Current.Scale}, body {body.FontSize}");
            SetTextAppearance(AppText.Current.WithScale(1));
            Require(AppText.Current is { Scale: 1, Family: not null, CodeFamily: not null } && body.FontSize == DesignMetrics.Type.Body,
                "Ctrl+0's step must bring the size back to 100% and keep the fonts; got " + AppText.Current);

            // The reset button: everything exactly as at the start.
            var reset = Part<Button>("settings-text-reset");
            Require(reset.IsEnabled, "the reset button must be enabled while the setting is not the standard one");
            ((IInvokeProvider)new ButtonAutomationPeer(reset).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUI(() => AppText.Current.IsStandard, () => "the reset button must bring back the standard setting; got " + AppText.Current);
            Standard("after the reset");
            Require(!reset.IsEnabled && size.SelectedIndex == TextAppearance.Scales.ToList().IndexOf(1) && ui.SelectedIndex == 0 && conversation.SelectedIndex == 0 && codes.SelectedIndex == 0,
                "after the reset the pop-ups must show 100% and the stand-in fonts, and the button must be off");
            return new() { ["shortcutsBound"] = true, ["standardExact"] = true, ["sizeFollows"] = true, ["interfaceFontFollows"] = true, ["conversationFontFollows"] = true, ["codeFontFollows"] = true, ["resetRestoresExact"] = true };
        }
        finally
        {
            SetTextAppearance(TextAppearance.Standard);
            root.Children.Remove(section);
        }
    }
}
