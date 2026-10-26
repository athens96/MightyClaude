namespace MightyClaude.Core;

/// <summary>
/// A message a sub-agent sends the session that started it, as Claude Code hands it over:
/// <c>&lt;agent-message from="&lt;agent ID or name&gt;"&gt;</c> … <c>&lt;/agent-message&gt;</c>, alone or inside a
/// <c>&lt;system-reminder&gt;</c> with other text. Its final report (its hand-back) is framed: a preamble opening
/// <c>[Subagent hand-back]</c> and ending <c>The report follows:</c> (or, when auto mode held the report back, a
/// <c>SECURITY WARNING … The report follows; …</c> line), then the report with every line indented by two spaces. A message
/// it sends while still running (SendMessage) has the same wrapper and no frame (macOS AgentHandback in CLIStreamParser.swift).
/// </summary>
public static class AgentHandback
{
    public const string Opening = "<agent-message";
    public const string Closing = "</agent-message>";
    private const string Frame = "[Subagent hand-back]";
    private const string Marker = "The report follows";
    private const int MaximumMessages = 64;

    /// <summary>
    /// Every message in <paramref name="text"/>, in order: who sent it, its body, and whether it is a framed final report. A
    /// report's body is the indented text after the marker with its indentation removed; a framed message without the marker,
    /// or an unframed one, is the whole text inside the tag.
    /// </summary>
    public static IReadOnlyList<(string From, string Body, bool IsHandback)> Parse(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var found = new List<(string From, string Body, bool IsHandback)>();
        var position = 0;
        while (found.Count < MaximumMessages && text.IndexOf(Opening, position, StringComparison.Ordinal) is var start and >= 0)
        {
            var rest = start + Opening.Length;
            if (rest >= text.Length) break;
            // `<agent-messages` and the like are not this tag.
            if (text[rest] != '>' && !char.IsWhiteSpace(text[rest])) { position = rest; continue; }
            var tagEnd = text.IndexOf('>', rest);
            if (tagEnd < 0) break;
            var content = tagEnd + 1;
            // The frame closes at the start of a line, before any next message; report lines are indented.
            var next = text.IndexOf("\n" + Opening, content, StringComparison.Ordinal);
            if (next < 0) next = text.Length;
            int end, after;
            if (text.IndexOf("\n" + Closing, content, next - content, StringComparison.Ordinal) is var lineEnd and >= 0) { end = lineEnd; after = lineEnd + 1 + Closing.Length; }
            else if (text.IndexOf(Closing, content, StringComparison.Ordinal) is var anywhere and >= 0) { end = anywhere; after = anywhere + Closing.Length; }
            else break;
            var inner = text[content..end];
            var framed = inner.Contains(Frame, StringComparison.Ordinal) || inner.Contains(Marker, StringComparison.Ordinal);
            found.Add((Sender(text[rest..tagEnd]), framed ? Body(inner) : inner.Trim(), framed));
            position = after;
        }
        return found;
    }

    private static string Sender(string attributes)
    {
        const string key = "from=\"";
        var start = attributes.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return "";
        var end = attributes.IndexOf('"', start + key.Length);
        return end < 0 ? "" : attributes[(start + key.Length)..end];
    }

    private static string Body(string inner)
    {
        var found = inner.IndexOf(Marker + ":", StringComparison.Ordinal);
        var length = Marker.Length + 1;
        if (found < 0) { found = inner.IndexOf(Marker, StringComparison.Ordinal); length = Marker.Length; }
        if (found < 0) return inner.Trim();
        var report = inner[(found + length)..];
        // The marker ends the preamble's line: what follows it there (the warning's advice) is not the report.
        var lineEnd = report.IndexOf('\n');
        report = lineEnd < 0 ? "" : report[(lineEnd + 1)..];
        return string.Join("\n", report.Split('\n').Select(line => line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] : line)).Trim();
    }
}
