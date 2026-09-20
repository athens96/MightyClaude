using System.Globalization;

namespace MightyClaude.Core;

public static class RenameSupport
{
    public const int MaximumTextElements = 120;

    public static string? DisplayName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return null;
        if (TextElements(name) > MaximumTextElements) return null;
        if (HasControlCharacter(name)) return null;
        return name;
    }

    /// <summary>True when 저장 may be pressed, mirroring RenameViews.swift's validName.</summary>
    public static bool IsValid(string value) => DisplayName(value) is not null;

    /// <summary>
    /// The red captions RenameViews.swift shows under the field, in macOS order.
    /// An empty name shows no caption on macOS — it only disables 저장.
    /// </summary>
    public static IReadOnlyList<string> Messages(string value)
    {
        var name = value.Trim();
        var messages = new List<string>();
        if (TextElements(name) > MaximumTextElements) messages.Add(RenameStrings.ErrorTooLong);
        if (HasControlCharacter(name)) messages.Add(RenameStrings.ErrorControlCharacter);
        return messages;
    }

    /// <summary>
    /// Normalizes a stored title: strips control characters and clamps to 120 text elements.
    /// Counting text elements rather than UTF-16 units keeps a name of 120 emoji whole across a
    /// restart; a plain Length cut would halve it and could split a surrogate pair.
    /// </summary>
    public static string ClampTitle(string? value)
    {
        var clean = Wire.Clean(value);
        if (clean.Length <= MaximumTextElements) return clean;
        var enumerator = StringInfo.GetTextElementEnumerator(clean);
        var count = 0; var end = clean.Length;
        while (enumerator.MoveNext())
        {
            if (count == MaximumTextElements) { end = enumerator.ElementIndex; break; }
            count++;
        }
        return count <= MaximumTextElements && end == clean.Length ? clean : clean[..end];
    }

    private static bool HasControlCharacter(string s) =>
        // Unicode Cc ranges: U+0000-U+001F, U+007F, U+0080-U+009F.
        // Explicit numeric check avoids .NET classification edge cases with U+0000.
        s.Any(c => c < ' ' || c == '\x7F' || (c >= '\x80' && c <= '\x9F'));

    private static int TextElements(string s)
    {
        var count = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) count++;
        return count;
    }

    public static AppSnapshot RenameWorkspace(AppSnapshot snapshot, string workspaceId, string name)
    {
        if (DisplayName(name) is not { } displayName) throw new ArgumentException(RenameStrings.ErrorTooLong);
        var index = snapshot.Workspaces.FindIndex(w => w.Id == workspaceId);
        if (index < 0) throw new ArgumentException(RenameStrings.ErrorNotFound);
        var workspaces = snapshot.Workspaces.ToList();
        workspaces[index] = workspaces[index] with { Name = displayName };
        return snapshot with { Workspaces = workspaces };
    }

    public static AppSnapshot RenameSession(AppSnapshot snapshot, string sessionId, string name)
    {
        if (DisplayName(name) is not { } displayName) throw new ArgumentException(RenameStrings.ErrorTooLong);
        if (!snapshot.Sessions.Any(s => s.Id == sessionId)) throw new ArgumentException(RenameStrings.ErrorNotFound);
        return snapshot with { Sessions = snapshot.Sessions.Select(s => s.Id == sessionId ? s with { Title = displayName } : s).ToList() };
    }
}
