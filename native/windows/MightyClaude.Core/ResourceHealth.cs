using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// The files the Windows app reads beside its exe at run time, checked once as it starts (M/MightyCore/ResourceHealthChecker.swift,
/// M/AppStore.swift:420-431): the default pet, the terminal page and its scripts, and the bundled Claude plugin. A copy of the app
/// that lost any of them (an incomplete unzip, a file a scanner took away) says which in the warning band. The locale catalogues,
/// which the Mac also checks, are built into MightyClaude.Core here and cannot go missing on their own.
/// </summary>
public static class ResourceHealth
{
    /// <summary>The terminal page's own files under Assets/Terminal.</summary>
    public static readonly string[] TerminalFiles = ["index.html", "terminal.js", "terminal.css", "xterm.js", "xterm.css", "addon-fit.js"];

    /// <summary>The names of the resources under <paramref name="baseDirectory"/> that are missing or unreadable, in a fixed order.</summary>
    public static IReadOnlyList<string> Missing(string baseDirectory)
    {
        var missing = new List<string>();
        if (!PetValid(Path.Combine(baseDirectory, "Assets", "pets", "mighty-raccoon"))) missing.Add("pets/mighty-raccoon");
        missing.AddRange(TerminalFiles.Where(name => !File.Exists(Path.Combine(baseDirectory, "Assets", "Terminal", name))).Select(name => "Assets/Terminal/" + name));
        if (!File.Exists(Path.Combine(baseDirectory, "claude-mods", ".claude-plugin", "plugin.json"))) missing.Add("claude-mods/.claude-plugin/plugin.json");
        return missing;
    }

    /// <summary>The band's sentence for <paramref name="missing"/>, or null when nothing is.</summary>
    public static string? Warning(IReadOnlyList<string> missing) =>
        missing.Count == 0 ? null : Locale.Get("store.error.resourcesMissing", new Dictionary<string, string> { ["names"] = string.Join(", ", missing) });

    /// <summary>A pet folder whose pet.json names a spritesheet, the Mac's test (ResourceHealthChecker.petDirectoryValid).</summary>
    public static bool PetValid(string directory)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "pet.json")));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("spritesheetPath", out var sheet) && sheet.ValueKind == JsonValueKind.String && sheet.GetString() is { Length: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
}
