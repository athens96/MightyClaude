using System.Globalization;
using System.Security;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MightyClaude.Core;

/// <summary>
/// The text setting (Settings › General › 글자, and Ctrl+= / Ctrl+- / Ctrl+0), the Mac's TextAppearance
/// (M/MightyCore/TextAppearance.swift): one size scale for every word the app draws, and three optional font
/// families: the interface's (the app's chrome), the conversation's (replies, the user's turns, the composer and
/// Mighty's prose) and the code's (code, tool output and the terminals). At the standard values the app draws
/// exactly as it did before the setting existed. Saved per user in the registry (<see cref="TextAppearanceStore"/>),
/// not in the workspace state, so the phone never sees it.
/// </summary>
public sealed record TextAppearance
{
    /// <summary>The sizes the shortcuts and Settings step through; 1 is the size the app was drawn at.</summary>
    public static IReadOnlyList<double> Scales => steps;
    private static readonly double[] steps = [0.85, 0.9, 1, 1.1, 1.2, 1.3, 1.4, 1.5];
    public static TextAppearance Standard { get; } = new();
    public const string ScaleKey = "textScale", FamilyKey = "textFontFamily", TranscriptFamilyKey = "textTranscriptFontFamily", CodeFamilyKey = "textCodeFontFamily";
    /// <summary>The terminal's size at scale 1 (Assets/Terminal/terminal.js).</summary>
    public const double TerminalBaseSize = 12;
    /// <summary>
    /// Monospaced families offered for code even when their fonts do not say they are fixed-pitch (D2Coding's wide
    /// Hangul clears the flag, for one): the Mac's list (M/MightyCore/TextAppearance.swift knownCodeFamilies).
    /// </summary>
    public static IReadOnlySet<string> KnownCodeFamilies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Menlo", "Monaco", "SF Mono", "JetBrains Mono", "D2Coding", "D2Coding ligature", "Courier", "Courier New",
        "Andale Mono", "PT Mono", "Fira Code", "Fira Mono", "Source Code Pro", "Cascadia Code", "Cascadia Mono",
        "IBM Plex Mono", "Hack", "Inconsolata", "Roboto Mono", "Ubuntu Mono", "Consolas",
    };

    public TextAppearance(double scale = 1, string? family = null, string? transcriptFamily = null, string? codeFamily = null)
    {
        Scale = Normalized(scale);
        Family = NormalizedFamily(family);
        TranscriptFamily = NormalizedFamily(transcriptFamily);
        CodeFamily = NormalizedFamily(codeFamily);
    }

    /// <summary>One of <see cref="Scales"/>.</summary>
    public double Scale { get; }
    /// <summary>The interface's installed font family; null is the app's own (Segoe UI Variable).</summary>
    public string? Family { get; }
    /// <summary>The conversation's family; null is the interface's.</summary>
    public string? TranscriptFamily { get; }
    /// <summary>Code's and the terminals' family; null is the app's monospaced one (Cascadia Mono, Consolas).</summary>
    public string? CodeFamily { get; }

    /// <summary>A family name without its surrounding blanks; empty is null.</summary>
    public static string? NormalizedFamily(string? family) => string.IsNullOrWhiteSpace(family) ? null : family.Trim();

    /// <summary>The nearest step in <see cref="Scales"/>; anything unreadable is 1.</summary>
    public static double Normalized(double value) =>
        !double.IsFinite(value) || value <= 0 ? 1 : Scales.MinBy(step => Math.Abs(step - value));

    /// <summary><paramref name="size"/> points at this scale, on the half point; at scale 1 exactly <paramref name="size"/>.</summary>
    public double Size(double size) => Scale == 1 ? size : Math.Round(size * Scale * 2, MidpointRounding.AwayFromZero) / 2;

    [JsonIgnore] public double TerminalSize => Size(TerminalBaseSize);
    /// <summary>The family the conversation draws in: its own, else the interface's; null is the app's own.</summary>
    [JsonIgnore] public string? ResolvedTranscriptFamily => TranscriptFamily ?? Family;
    [JsonIgnore] public int Percent => (int)Math.Round(Scale * 100, MidpointRounding.AwayFromZero);
    [JsonIgnore] public bool IsStandard => this == Standard;

    private int Index => Array.IndexOf(steps, Scale) is var at and >= 0 ? at : Array.IndexOf(steps, 1.0);
    [JsonIgnore] public bool CanGrow => Index < Scales.Count - 1;
    [JsonIgnore] public bool CanShrink => Index > 0;

    /// <summary><paramref name="steps"/> sizes larger (or smaller when negative), held to the ends of <see cref="Scales"/>.</summary>
    public TextAppearance Stepped(int steps) => WithScale(Scales[Math.Clamp(Index + steps, 0, Scales.Count - 1)]);

    public TextAppearance WithScale(double scale) => new(scale, Family, TranscriptFamily, CodeFamily);
    public TextAppearance WithFamily(string? family) => new(Scale, family, TranscriptFamily, CodeFamily);
    public TextAppearance WithTranscriptFamily(string? family) => new(Scale, Family, family, CodeFamily);
    public TextAppearance WithCodeFamily(string? family) => new(Scale, Family, TranscriptFamily, family);

    /// <summary>
    /// The families the code picker offers, in the order given: those whose fonts are fixed-pitch and the well-known
    /// monospaced ones, without hidden ("@" vertical, "." system) or repeated names.
    /// </summary>
    public static IReadOnlyList<string> CodeFamilies(IEnumerable<(string Family, bool FixedPitch)> fonts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return fonts.Where(font => font.Family.Length > 0 && font.Family[0] is not '.' and not '@' && (font.FixedPitch || KnownCodeFamilies.Contains(font.Family)) && seen.Add(font.Family))
            .Select(font => font.Family).ToList();
    }

    /// <summary>
    /// The saved setting from its four values as the registry returns them (null when missing); a missing or broken
    /// value reads as the standard one. The scale is a string in the invariant culture ("1.1"); a number above 10, as a
    /// string or a DWORD, is taken as a percent (110), as a person editing the value by hand would write it.
    /// </summary>
    public static TextAppearance FromValues(object? scale, object? family, object? transcriptFamily, object? codeFamily)
    {
        var value = scale switch
        {
            string text when double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number > 10 ? number / 100 : number,
            int percent => percent / 100.0,
            long percent => percent / 100.0,
            _ => 1,
        };
        return new(value, family as string, transcriptFamily as string, codeFamily as string);
    }

    /// <summary>The values to write, by registry name; null removes the value, so the standard setting stores nothing.</summary>
    public IReadOnlyList<(string Name, string? Value)> ToValues() =>
    [
        (ScaleKey, Scale == 1 ? null : Scale.ToString("R", CultureInfo.InvariantCulture)),
        (FamilyKey, Family),
        (TranscriptFamilyKey, TranscriptFamily),
        (CodeFamilyKey, CodeFamily),
    ];

    public bool Equals(TextAppearance? other) =>
        other is not null && Scale == other.Scale && Family == other.Family && TranscriptFamily == other.TranscriptFamily && CodeFamily == other.CodeFamily;
    public override int GetHashCode() => HashCode.Combine(Scale, Family, TranscriptFamily, CodeFamily);
}

/// <summary>
/// Where Windows keeps <see cref="TextAppearance"/>: string values under <c>HKEY_CURRENT_USER\Software\MightyClaudeNative</c>
/// beside <c>delegation.enabled</c> (<see cref="DelegationSwitch"/>), named as the Mac's defaults keys.
/// </summary>
public static class TextAppearanceStore
{
    /// <summary>The saved setting; the standard one off Windows, or when the registry cannot be read.</summary>
    public static TextAppearance Load()
    {
        if (!OperatingSystem.IsWindows()) return TextAppearance.Standard;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DelegationSwitch.RegistryKey);
            return key is null ? TextAppearance.Standard : TextAppearance.FromValues(key.GetValue(TextAppearance.ScaleKey), key.GetValue(TextAppearance.FamilyKey), key.GetValue(TextAppearance.TranscriptFamilyKey), key.GetValue(TextAppearance.CodeFamilyKey));
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return TextAppearance.Standard; }
    }

    /// <summary>Writes the setting; standard values are removed rather than stored. False when the registry refused it.</summary>
    public static bool Save(TextAppearance appearance)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(DelegationSwitch.RegistryKey);
            foreach (var (name, value) in appearance.ToValues())
                if (value is null) key.DeleteValue(name, throwOnMissingValue: false); else key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }
}
