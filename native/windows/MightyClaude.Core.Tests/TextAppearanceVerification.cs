using System.Text.Json;
using MightyClaude.Core;

/// <summary>The text setting's model, held to the Mac's (M/MightyCore/TextAppearance.swift, TextAppearanceTests).</summary>
internal static class TextAppearanceVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task StandardDrawsEveryBaseSizeExactly()
    {
        var standard = TextAppearance.Standard;
        Check(standard.IsStandard && standard.Scale == 1 && standard.Family is null && standard.TranscriptFamily is null && standard.CodeFamily is null, "the standard setting is scale 1 with no families");
        foreach (var size in new[] { 9, 10, 10.5, 10.8, 11, 11.3, 11.5, 12, 12.5, 13, 14, 15, 16, 17, 22, 27, 29, 34 })
            Check(standard.Size(size) == size, $"at scale 1 a {size}pt base must stay {size}, got {standard.Size(size)}");
        Check(standard.TerminalSize == 12 && standard.Percent == 100, "the terminal is 12pt and the label 100% at the standard setting");
        Check(new TextAppearance().Equals(TextAppearance.Standard) && new TextAppearance(1, " ", "", null).IsStandard, "blank families are no families");
        return Task.CompletedTask;
    }

    internal static Task SizesRoundToTheHalfPointAndScalesSnapToSteps()
    {
        Check(TextAppearance.Scales.SequenceEqual([0.85, 0.9, 1, 1.1, 1.2, 1.3, 1.4, 1.5]), "the steps are 85% to 150%");
        Check(new TextAppearance(1.2).Size(13) == 15.5, "13 at 120% is 15.6, drawn at 15.5");
        Check(new TextAppearance(1.1).Size(13) == 14.5, "13 at 110% is 14.3, drawn at 14.5");
        Check(new TextAppearance(0.85).Size(12.5) == 10.5, "12.5 at 85% is 10.625, drawn at 10.5");
        Check(new TextAppearance(1.5).Size(11) == 16.5 && new TextAppearance(1.5).TerminalSize == 18, "150% of 11 and of the terminal's 12");
        Check(new TextAppearance(0.85).Size(10) == 8.5, "10 at 85% is exactly 8.5");
        Check(new TextAppearance(1.17).Scale == 1.2 && new TextAppearance(3).Scale == 1.5 && new TextAppearance(0.1).Scale == 0.85, "a stray scale snaps to the nearest step");
        Check(new TextAppearance(double.NaN).Scale == 1 && new TextAppearance(-2).Scale == 1 && new TextAppearance(0).Scale == 1 && new TextAppearance(double.PositiveInfinity).Scale == 1, "an unreadable scale is 1");
        Check(new TextAppearance(1.3).Percent == 130 && new TextAppearance(0.85).Percent == 85, "the percent label");
        return Task.CompletedTask;
    }

    internal static Task SteppingStopsAtBothEndsAndKeepsTheFamilies()
    {
        var text = new TextAppearance(1, "Malgun Gothic", "Georgia", "D2Coding");
        Check(text.Stepped(1).Scale == 1.1 && text.Stepped(-1).Scale == 0.9 && text.Stepped(3).Scale == 1.3, "a step is the next size");
        Check(text.Stepped(1) is { Family: "Malgun Gothic", TranscriptFamily: "Georgia", CodeFamily: "D2Coding" }, "a step keeps every family");
        var largest = new TextAppearance(1.5); var smallest = new TextAppearance(0.85);
        Check(!largest.CanGrow && largest.CanShrink && largest.Stepped(1).Scale == 1.5 && largest.Stepped(20).Scale == 1.5, "the largest size stays the largest");
        Check(smallest.CanGrow && !smallest.CanShrink && smallest.Stepped(-1).Scale == 0.85 && smallest.Stepped(-20).Scale == 0.85, "the smallest size stays the smallest");
        Check(TextAppearance.Standard.CanGrow && TextAppearance.Standard.CanShrink, "the standard size can go both ways");
        Check(text.WithScale(1.4).Scale == 1.4 && text.WithFamily(null).Family is null && text.WithTranscriptFamily(" ").TranscriptFamily is null && text.WithCodeFamily("Consolas").CodeFamily == "Consolas", "each part changes alone");
        Check(text.WithFamily("  Segoe Print ").Family == "Segoe Print", "a family name is trimmed");
        return Task.CompletedTask;
    }

    internal static Task TheConversationFollowsTheInterfaceUntilItHasItsOwn()
    {
        Check(new TextAppearance(1, "Malgun Gothic").ResolvedTranscriptFamily == "Malgun Gothic", "no conversation font is the interface's");
        Check(new TextAppearance(1, "Malgun Gothic", "Georgia").ResolvedTranscriptFamily == "Georgia", "its own wins");
        Check(TextAppearance.Standard.ResolvedTranscriptFamily is null, "standard is the app's own font");
        var families = TextAppearance.CodeFamilies([("Arial", false), ("Consolas", true), ("D2Coding", false), ("@MS Gothic", true), (".Hidden", true), ("Consolas", true), ("Lucida Console", true), ("Segoe UI", false)]);
        Check(families.SequenceEqual(["Consolas", "D2Coding", "Lucida Console"]), "code offers fixed-pitch and known monospaced families once, without hidden ones: " + string.Join(", ", families));
        return Task.CompletedTask;
    }

    internal static Task TheRegistryValuesRoundTripAndTheStandardStoresNothing()
    {
        Check(TextAppearance.Standard.ToValues().All(value => value.Value is null), "the standard setting writes nothing");
        Check(TextAppearance.Standard.ToValues().Select(value => value.Name).SequenceEqual(["textScale", "textFontFamily", "textTranscriptFontFamily", "textCodeFontFamily"]), "the value names are the Mac's defaults keys");
        foreach (var text in new[] { new TextAppearance(1.1, "Malgun Gothic", null, "D2Coding"), new TextAppearance(0.85), new TextAppearance(1, null, "Georgia"), new TextAppearance(1.5, "A", "B", "C") })
        {
            var values = text.ToValues().ToDictionary(value => value.Name, value => (object?)value.Value);
            var back = TextAppearance.FromValues(values["textScale"], values["textFontFamily"], values["textTranscriptFontFamily"], values["textCodeFontFamily"]);
            Check(back == text, $"{text} must read back as itself, got {back}");
        }
        Check(TextAppearance.FromValues(null, null, null, null).IsStandard, "nothing saved is the standard setting");
        Check(TextAppearance.FromValues("big", 12, new object(), "") .IsStandard, "broken values read as the standard setting");
        Check(TextAppearance.FromValues(" 1.2 ", null, null, null).Scale == 1.2 && TextAppearance.FromValues(130, null, null, null).Scale == 1.3 && TextAppearance.FromValues(90L, null, null, null).Scale == 0.9 && TextAppearance.FromValues("110", null, null, null).Scale == 1.1, "a string scale or a whole percent, as a number or a string");
        // The settings window's smoke reads the model through JSON: only the four stored parts, no computed ones.
        var json = JsonSerializer.Serialize(new TextAppearance(1.2, "A"), Wire.Json);
        Check(!json.Contains("percent", StringComparison.OrdinalIgnoreCase) && !json.Contains("isStandard", StringComparison.OrdinalIgnoreCase) && !json.Contains("canGrow", StringComparison.OrdinalIgnoreCase), "computed properties stay out of JSON: " + json);
        return Task.CompletedTask;
    }
}
