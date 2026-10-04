using System.Reflection;
using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/design-tokens.json is the concept D palette the Mac
// (DesignTokenParityTests) is held to; these checks hold the Windows port to the same
// bytes: every palette field, the derived tones and glyph colours, the metrics and
// opacities (Windows-only), the dash conversion WinUI needs, and the macOS contrast rules
// (DesignTokenContrastTests).
internal static class DesignTokenVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal(string actual, string expected, string what) => Check(actual == expected, $"{what}: expected {expected}, got {actual}");
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.DesignTokens.json";
        using var stream = typeof(DesignTokenVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    private static readonly (string Theme, DesignPalette Palette)[] Themes = [("light", DesignTokens.Light), ("dark", DesignTokens.Dark)];

    /// <summary>The palette's own fields, in declaration order: the record's primary constructor.</summary>
    private static IReadOnlyList<string> Fields() =>
        typeof(DesignPalette).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First().GetParameters().Select(p => p.Name!).ToList();

    private static DesignColor Field(DesignPalette palette, string name) =>
        (DesignColor)(typeof(DesignPalette).GetProperty(name)?.GetValue(palette) ?? throw new InvalidOperationException("DesignPalette has no " + name));

    internal static Task EveryFixtureHexEqualsThePaletteField()
    {
        var fields = Fields();
        Check(fields.Count == 41, "the palette must carry 41 tokens; got " + fields.Count);
        Check(Enum.GetNames<DesignToken>().SequenceEqual(fields), "DesignToken must name every palette field in order: " + string.Join(", ", Enum.GetNames<DesignToken>().Except(fields).Concat(fields.Except(Enum.GetNames<DesignToken>()))));
        foreach (var (theme, palette) in Themes)
        {
            var table = Fixture.Value.GetProperty(theme);
            var keys = table.EnumerateObject().Select(p => p.Name).ToHashSet();
            var names = fields.Select(Camel).ToHashSet();
            Check(keys.SetEquals(names), $"{theme}: the fixture and the palette name different tokens: fixture-only [{string.Join(", ", keys.Except(names))}], palette-only [{string.Join(", ", names.Except(keys))}]");
            foreach (var field in fields)
            {
                Equal(Field(palette, field).Hex, table.GetProperty(Camel(field)).GetString()!, $"{theme}.{Camel(field)}");
                Check(palette[Enum.Parse<DesignToken>(field)] == Field(palette, field), $"{theme}: palette[DesignToken.{field}] reads another field");
            }
        }
        return Task.CompletedTask;
    }

    internal static Task DerivedTonesAndGlyphsMatchTheFixture()
    {
        foreach (var (theme, palette) in Themes)
        {
            var derived = Fixture.Value.GetProperty("derived").GetProperty(theme);
            Check(palette.IsDark == derived.GetProperty("isDark").GetBoolean(), $"{theme}: isDark");
            Equal(palette.SegmentTrack.Hex, derived.GetProperty("segmentTrack").GetString()!, $"{theme}.segmentTrack");
            Equal(palette.SegmentOn.Hex, derived.GetProperty("segmentOn").GetString()!, $"{theme}.segmentOn");
            foreach (var tone in Enum.GetValues<DesignTone>())
            {
                var row = derived.GetProperty("tones").GetProperty(Camel(tone.ToString()));
                Equal(palette.Fill(tone).Hex, row.GetProperty("fill").GetString()!, $"{theme}.fill({tone})");
                Equal(palette.Text(tone).Hex, row.GetProperty("text").GetString()!, $"{theme}.text({tone})");
                Equal(palette.Soft(tone).Hex, row.GetProperty("soft").GetString()!, $"{theme}.soft({tone})");
                Equal(palette.Mark(tone).Hex, row.GetProperty("mark").GetString()!, $"{theme}.mark({tone})");
                Equal(palette.Glyph(tone).Hex, row.GetProperty("glyph").GetString()!, $"{theme}.glyph({tone})");
                // The glyph drawn by WinUI reads the same palette.
                Equal(StatusGlyph.GlyphHex(tone, palette.IsDark), row.GetProperty("glyph").GetString()!, $"{theme}: StatusGlyph.GlyphHex({tone})");
            }
            foreach (var tone in new[] { DesignTone.Wait, DesignTone.Err })
            {
                var disc = derived.GetProperty("disc").GetProperty(Camel(tone.ToString()));
                Equal(StatusGlyph.DiscFill(tone), disc.GetProperty("fill").GetString()!, $"{theme}: disc fill of {tone}");
                Equal(StatusGlyph.DiscInk(tone), disc.GetProperty("ink").GetString()!, $"{theme}: disc ink of {tone}");
                Equal(palette.DiscFill(tone).Hex, disc.GetProperty("fill").GetString()!, $"{theme}.discFill({tone})");
                Equal(palette.DiscInk(tone).Hex, disc.GetProperty("ink").GetString()!, $"{theme}.discInk({tone})");
            }
            // The diagram's running outline: run blue, wait amber, a runSoft halo per mode.
            Equal(MightyGraphActivity.StrokeHex(MightyGraphActivity.Marching), palette.Run.Hex, $"{theme}: running outline");
            Equal(MightyGraphActivity.StrokeHex(MightyGraphActivity.Waiting), palette.Wait.Hex, $"{theme}: waiting outline");
            Equal(MightyGraphActivity.HaloHex(MightyGraphActivity.Marching, palette.IsDark) ?? "none", Fixture.Value.GetProperty(theme).GetProperty("runSoft").GetString()!, $"{theme}: running halo");
        }
        return Task.CompletedTask;
    }

    internal static Task WindowsOnlyColoursMatchTheFixture()
    {
        var windowsOnly = Fixture.Value.GetProperty("windowsOnly");
        foreach (var provider in new[] { "claude", "codex", "gemini" })
        {
            var expected = windowsOnly.GetProperty("provider").GetProperty(provider).EnumerateArray().Select(v => v.GetString()!).ToList();
            var actual = ProviderMark.Colors(provider).Select(hex => new DesignColor(hex).Hex).ToList();
            Check(actual.SequenceEqual(expected), $"provider {provider}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
        }
        foreach (var (theme, palette) in Themes)
        {
            var subtle = windowsOnly.GetProperty("subtle").GetProperty(theme);
            Equal(DesignTokens.Subtle(palette).Hex, subtle.GetProperty("color").GetString()!, $"{theme}: subtle colour");
            Check(Near(subtle.GetProperty("opacity").GetDouble(), DesignMetrics.Opacity.Subtle), $"{theme}: subtle opacity");
            var syntax = windowsOnly.GetProperty("syntax").GetProperty(theme);
            Check(syntax.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["keyword", "string", "number"]), $"{theme}: syntax kinds");
            foreach (var kind in syntax.EnumerateObject())
                Equal(DesignTokens.Syntax(kind.Name, palette)?.Hex ?? "none", kind.Value.GetString()!, $"{theme}: syntax {kind.Name}");
            Check(DesignTokens.Syntax("comment", palette) == palette.Ink2 && DesignTokens.Syntax("plain", palette) is null, $"{theme}: comments are ink2, plain text has none");
        }
        return Task.CompletedTask;
    }

    internal static Task DashesConvertToStrokeUnits()
    {
        var running = DesignMetrics.Dash.InStrokeUnits(DesignMetrics.Dash.Running, DesignMetrics.Dash.RunningWidth);
        Check(running.SequenceEqual([4.5, 3.5]), "[9,7] at 2pt must be [4.5, 3.5] stroke units; got [" + string.Join(", ", running) + "]");
        var draft = DesignMetrics.Dash.InStrokeUnits(DesignMetrics.Dash.Draft, DesignMetrics.Dash.DraftWidth);
        Check(draft.Length == 2 && Near(draft[0], 10.0 / 3) && Near(draft[1], 8.0 / 3), "[5,4] at 1.5pt must be [3.333…, 2.666…]; got [" + string.Join(", ", draft) + "]");
        Check(DesignMetrics.Dash.InStrokeUnits([9, 7], 1).SequenceEqual([9.0, 7.0]), "a 1pt stroke keeps pt");
        // The diagram outline draws the same pattern at the same width.
        Check(MightyGraphActivity.Dash.SequenceEqual(DesignMetrics.Dash.Running) && Near(MightyGraphActivity.LineWidth, DesignMetrics.Dash.RunningWidth), "the running outline is [9,7] at 2pt");
        var dash = Fixture.Value.GetProperty("metrics").GetProperty("dash");
        Check(dash.GetProperty("running").GetProperty("pattern").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(DesignMetrics.Dash.Running), "fixture running dash");
        Check(Near(dash.GetProperty("running").GetProperty("width").GetDouble(), DesignMetrics.Dash.RunningWidth), "fixture running width");
        Check(dash.GetProperty("running").GetProperty("inStrokeUnits").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(running), "fixture running stroke units");
        Check(dash.GetProperty("draft").GetProperty("pattern").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(DesignMetrics.Dash.Draft), "fixture draft dash");
        Check(Near(dash.GetProperty("draft").GetProperty("width").GetDouble(), DesignMetrics.Dash.DraftWidth), "fixture draft width");
        try { DesignMetrics.Dash.InStrokeUnits([9, 7], 0); Check(false, "a zero stroke width must be refused"); }
        catch (ArgumentOutOfRangeException) { }
        return Task.CompletedTask;
    }

    internal static Task MetricsAndOpacitiesMatchTheFixture()
    {
        var metrics = Fixture.Value.GetProperty("metrics");
        foreach (var (section, type) in new[] { ("radius", typeof(DesignMetrics.Radius)), ("stroke", typeof(DesignMetrics.Stroke)), ("type", typeof(DesignMetrics.Type)), ("layout", typeof(DesignMetrics.Layout)) })
        {
            var constants = type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToDictionary(f => Camel(f.Name), f => (double)f.GetRawConstantValue()!);
            var table = metrics.GetProperty(section).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
            Check(constants.Keys.ToHashSet().SetEquals(table.Keys), $"metrics.{section}: C# [{string.Join(", ", constants.Keys.Order())}] vs fixture [{string.Join(", ", table.Keys.Order())}]");
            foreach (var (name, value) in table) Check(Near(constants[name], value), $"metrics.{section}.{name}: expected {value}, got {constants[name]}");
        }
        var fonts = metrics.GetProperty("font");
        Equal(DesignMetrics.Font.Mono, fonts.GetProperty("mono").GetString()!, "font.mono");
        Equal(DesignMetrics.Font.Body, fonts.GetProperty("body").GetString()!, "font.body");
        Equal(DesignMetrics.Font.Heading, fonts.GetProperty("heading").GetString()!, "font.heading");

        var opacities = typeof(DesignMetrics.Opacity).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToDictionary(f => Camel(f.Name), f => (double)f.GetRawConstantValue()!);
        var rows = Fixture.Value.GetProperty("opacities").EnumerateArray().ToList();
        var tokens = Fields().Select(Camel).Concat(["black", "tint"]).ToHashSet();
        foreach (var row in rows)
        {
            var name = row.GetProperty("name").GetString()!;
            Check(opacities.TryGetValue(name, out var value) && Near(value, row.GetProperty("opacity").GetDouble()), $"opacity {name}: expected {row.GetProperty("opacity").GetDouble()}, got {(opacities.TryGetValue(name, out var v) ? v : double.NaN)}");
            Check(tokens.Contains(row.GetProperty("token").GetString()!), $"opacity {name}: unknown token {row.GetProperty("token").GetString()}");
            Check(row.TryGetProperty("source", out _), $"opacity {name}: no Mac source");
        }
        // Every C# opacity is in the table; `subtle` is the Windows-only wash.
        Check(opacities.Keys.ToHashSet().SetEquals(rows.Select(r => r.GetProperty("name").GetString()!).Append("subtle")), "DesignMetrics.Opacity and the fixture name different opacities");
        return Task.CompletedTask;
    }

    internal static Task ThemeSettingPicksThePalette()
    {
        Check(DesignTokens.Palette("light") == DesignTokens.Light, "light");
        Check(DesignTokens.Palette("dark") == DesignTokens.Dark, "dark");
        Check(DesignTokens.Palette("") == DesignTokens.Dark && DesignTokens.Palette("system") == DesignTokens.Dark, "only \"light\" is light");
        Check(DesignTokens.Palette(true) == DesignTokens.Dark && DesignTokens.Palette(false) == DesignTokens.Light, "the dark flag");
        Check(!DesignTokens.Light.IsDark && DesignTokens.Dark.IsDark, "isDark");
        Check(Near(new DesignColor(0xFFFFFF).Contrast(new DesignColor(0x000000)), 21) && Near(new DesignColor(0x777777).Contrast(new DesignColor(0x777777)), 1), "WCAG luminance");
        Equal(new DesignColor(0x0E1320).Hex, "#0E1320", "hex");
        return Task.CompletedTask;
    }

    internal static Task TheSplashReadsTheSavedThemeBeforeTheStateLoads()
    {
        var directory = Verification.Temp();
        Check(StateStore.SavedTheme(directory) == "dark", "no saved state is the dark default");
        var path = Path.Combine(directory, "workspace-state.json");
        var saved = new AppSnapshot { Theme = "light", Sessions = [] };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(saved, Wire.Json));
        Check(StateStore.SavedTheme(directory) == "light", "the saved light theme");
        // A "theme" key deeper in the file is not the setting.
        File.WriteAllText(path, """{"version":1,"workspaces":[{"theme":"light"}],"sessions":[],"theme":"dark"}""");
        Check(StateStore.SavedTheme(directory) == "dark", "only the top-level theme counts");
        File.WriteAllText(path, """{"version":1,"workspaces":[{"theme":"light"}]""");
        Check(StateStore.SavedTheme(directory) == "dark", "a damaged file falls back to dark");
        File.WriteAllText(path, "not json");
        Check(StateStore.SavedTheme(directory) == "dark", "a file that is not JSON falls back to dark");
        Directory.Delete(directory, true);
        return Task.CompletedTask;
    }

    // The macOS DesignTokenContrastTests, ported: every word 4.5:1 on what it sits on, every
    // status fill that carries text 4.5:1 against that text, every mark 3:1 (WCAG 1.4.3, 1.4.11).
    private const double AA = 4.5, MarkRatio = 3.0;

    private static IEnumerable<(string Name, Func<DesignPalette, DesignColor> Foreground, Func<DesignPalette, DesignColor> Background)> TextPairs()
    {
        var grounds = new (string, Func<DesignPalette, DesignColor>)[] { ("page", p => p.Page), ("card", p => p.Card), ("cardRaised", p => p.CardRaised) };
        var inks = new (string, Func<DesignPalette, DesignColor>)[]
        {
            ("ink", p => p.Ink), ("ink2", p => p.Ink2), ("ink3", p => p.Ink3), ("accent", p => p.Accent),
            ("waitText", p => p.WaitText), ("doneText", p => p.DoneText), ("errText", p => p.ErrText), ("stopText", p => p.StopText),
            ("agentText", p => p.AgentText), ("taskText", p => p.TaskText), ("steerText", p => p.SteerText),
            ("compactText", p => p.CompactText), ("questionText", p => p.QuestionText),
        };
        foreach (var (inkName, ink) in inks) foreach (var (groundName, ground) in grounds) yield return ($"{inkName} on {groundName}", ink, ground);
        var sidebarInks = new (string, Func<DesignPalette, DesignColor>)[]
        {
            ("ink", p => p.Ink), ("sidebarInk2", p => p.SidebarInk2), ("sidebarAccent", p => p.SidebarAccent),
            ("waitText", p => p.WaitText), ("doneText", p => p.DoneText), ("errText", p => p.ErrText), ("stopText", p => p.StopText),
        };
        foreach (var (inkName, ink) in sidebarInks) yield return ($"{inkName} on sidebar", ink, p => p.Sidebar);
        yield return ("accent on runSoft", p => p.Accent, p => p.RunSoft);
        yield return ("accent on accentSoft", p => p.Accent, p => p.AccentSoft);
        yield return ("ink on accentSoft", p => p.Ink, p => p.AccentSoft);
        yield return ("waitText on waitSoft", p => p.WaitText, p => p.WaitSoft);
        yield return ("doneText on doneSoft", p => p.DoneText, p => p.DoneSoft);
        yield return ("errText on errSoft", p => p.ErrText, p => p.ErrSoft);
        yield return ("stopText on stopSoft", p => p.StopText, p => p.StopSoft);
        yield return ("ink2 on stopSoft", p => p.Ink2, p => p.StopSoft);
        yield return ("codeText on codeSurface", p => p.CodeText, p => p.CodeSurface);
        yield return ("ink2 on segmentTrack", p => p.Ink2, p => p.SegmentTrack);
        yield return ("ink on segmentOn", p => p.Ink, p => p.SegmentOn);
        yield return ("bubble text (card) on ink", p => p.Card, p => p.Ink);
    }

    private static IEnumerable<(string Name, Func<DesignPalette, DesignColor> Foreground, Func<DesignPalette, DesignColor> Background)> FilledTextPairs() =>
    [
        ("onStatus on run", p => p.OnStatus, p => p.Run), ("onStatus on done", p => p.OnStatus, p => p.Done),
        ("onStatus on err", p => p.OnStatus, p => p.Err), ("onStatus on stop", p => p.OnStatus, p => p.Stop),
        ("onStatus on idle", p => p.OnStatus, p => p.Idle), ("onWait on wait", p => p.OnWait, p => p.Wait),
        ("onAccent on accent", p => p.OnAccent, p => p.Accent), ("onTask on task", p => p.OnTask, p => p.Task),
    ];

    private static IEnumerable<(string Name, Func<DesignPalette, DesignColor> Foreground, Func<DesignPalette, DesignColor> Background)> MarkPairs()
    {
        var grounds = new (string, Func<DesignPalette, DesignColor>)[] { ("page", p => p.Page), ("card", p => p.Card), ("sidebar", p => p.Sidebar) };
        foreach (var tone in Enum.GetValues<DesignTone>())
            foreach (var (groundName, ground) in grounds)
                yield return ($"mark({tone}) on {groundName}", p => p.Mark(tone), ground);
        yield return ("agent on page", p => p.Agent, p => p.Page);
        yield return ("agent on card", p => p.Agent, p => p.Card);
        yield return ("onStatus glyph on agent", p => p.OnStatus, p => p.Agent);
        yield return ("accent mark on accentSoft", p => p.Accent, p => p.AccentSoft);
    }

    internal static Task InksFillsAndMarksClearTheirContrast()
    {
        foreach (var (theme, palette) in Themes)
        {
            foreach (var (name, foreground, background) in TextPairs())
            {
                var ratio = foreground(palette).Contrast(background(palette));
                Check(ratio >= AA, $"{theme}: {name} is {ratio:0.00}");
            }
            foreach (var (name, foreground, background) in FilledTextPairs())
            {
                var ratio = foreground(palette).Contrast(background(palette));
                Check(ratio >= AA, $"{theme}: {name} is {ratio:0.00}");
            }
            foreach (var (name, foreground, background) in MarkPairs())
            {
                var ratio = foreground(palette).Contrast(background(palette));
                Check(ratio >= MarkRatio, $"{theme}: {name} is {ratio:0.00}");
            }
            // Words for a tone go through Text(tone); each lands on an ink checked above.
            var checkedInks = TextPairs().Select(pair => pair.Foreground(palette)).ToHashSet();
            foreach (var tone in Enum.GetValues<DesignTone>()) Check(checkedInks.Contains(palette.Text(tone)), $"{theme}: text({tone}) is not a checked ink");
            var lift = palette.Card.Contrast(palette.Page);
            Check(lift > 1.05 && lift < 1.5, $"{theme}: card lifts off the page by {lift:0.000}");
            Check(palette.Line.Contrast(palette.Page) > 1.05 && palette.Sidebar != palette.Page, $"{theme}: line and sidebar stand off the page");
        }
        var page = DesignTokens.Dark.Page;
        Check(page.RelativeLuminance > 0.004 && page.B > page.R, "the dark page is ink navy rather than black");
        foreach (var tone in Enum.GetValues<DesignTone>().Where(t => t != DesignTone.Idle))
            Check(DesignTokens.Light.Fill(tone) == DesignTokens.Dark.Fill(tone), $"the {tone} fill is one colour across modes");
        Check(DesignTokens.Light.Agent == DesignTokens.Dark.Agent && DesignTokens.Light.Task == DesignTokens.Dark.Task, "the block fills are one colour across modes");
        return Task.CompletedTask;
    }
}
