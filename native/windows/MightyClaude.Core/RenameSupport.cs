using System.Globalization;

namespace MightyClaude.Core;

public static class RenameSupport
{
    public static string? DisplayName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return null;
        if (TextElements(name) > 120) return null;
        // Unicode Cc ranges: U+0000-U+001F, U+007F, U+0080-U+009F.
        // Explicit numeric check avoids .NET classification edge cases with U+0000.
        if (name.Any(c => c < ' ' || c == '\x7F' || (c >= '\x80' && c <= '\x9F'))) return null;
        return name;
    }

    private static int TextElements(string s)
    {
        var count = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) count++;
        return count;
    }

    public static AppSnapshot RenameWorkspace(AppSnapshot snapshot, string workspaceId, string name)
    {
        if (DisplayName(name) is not { } displayName) throw new ArgumentException("이름이 올바르지 않습니다.");
        var index = snapshot.Workspaces.FindIndex(w => w.Id == workspaceId);
        if (index < 0) throw new ArgumentException(RenameStrings.ErrorNotFound);
        var workspaces = snapshot.Workspaces.ToList();
        workspaces[index] = workspaces[index] with { Name = displayName };
        return snapshot with { Workspaces = workspaces };
    }

    public static AppSnapshot RenameSession(AppSnapshot snapshot, string sessionId, string name)
    {
        if (DisplayName(name) is not { } displayName) throw new ArgumentException("이름이 올바르지 않습니다.");
        if (!snapshot.Sessions.Any(s => s.Id == sessionId)) throw new ArgumentException(RenameStrings.ErrorNotFound);
        return snapshot with { Sessions = snapshot.Sessions.Select(s => s.Id == sessionId ? s with { Title = displayName } : s).ToList() };
    }
}
