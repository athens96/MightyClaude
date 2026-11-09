using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// The page's runnable shell blocks, read the same way here (native/windows/web/src/conversation/shellBlocks.ts over markdown.ts
/// parseBlocks/parseList): the fenced code blocks of a reply in reading order, into quotes and list items, with paragraphs, headings,
/// rules and tables only stepped over. The host runs a block only when the page's command is the one this scan finds at the key's
/// place (<see cref="ShellBlockRunSupport.Shown"/>), so a difference between the two can only refuse a run, never widen one.
/// </summary>
public static partial class ShellBlockScan
{
    private const int MaximumDepth = 8, MaximumRenderBytes = 131_072;
    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "shell", "console", "ps1", "powershell", "pwsh" };
    private static readonly string[] Prompts = ["$ ", "PS> ", "> "];

    [GeneratedRegex(@"^(\s{0,3})(`{3,}|~{3,})\s*([^`]*)$")] private static partial Regex Fence();
    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s{0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$")] private static partial Regex ThematicBreak();
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])(?:\s+(.*))?$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$")] private static partial Regex TableDelimiter();
    [GeneratedRegex(@"(?<!\\)\|")] private static partial Regex CellSplit();
    /// <summary>Text that could show one thing and run another: bidi controls, zero-width and format characters, C0 controls but tab and newline, DEL.</summary>
    [GeneratedRegex("[‪-‮⁦-⁩​-‏﻿⁠\u0000-\u0008\u000B-\u001F\u007F]")] private static partial Regex Deceptive();

    /// <summary>Whether <paramref name="text"/> holds a character that hides or reorders what is shown (shellBlocks.ts unsafeCharacters).</summary>
    public static bool IsDeceptive(string text) => Deceptive().IsMatch(text);

    /// <summary>shellBlocks.ts shellCommand on Windows: the command a block runs, or null when it gets no button.</summary>
    public static string? Command(string? language, string text)
    {
        var name = language?.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (name is null || !Shells.Contains(name)) return null;
        // Checked on the block as written, before a lone CR becomes a line break.
        if (IsDeceptive(text.Replace("\r\n", "\n"))) return null;
        var body = Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n+$", "");
        string command;
        if (name != "console") command = body;
        else command = string.Join('\n', body.Split('\n').Select(line => Prompts.FirstOrDefault(mark => line.StartsWith(mark, StringComparison.Ordinal)) is { } prompt ? line[prompt.Length..] : null).Where(line => line is not null));
        return command.Trim().Length > 0 ? command : null;
    }

    /// <summary>shellBlocks.ts runnableBlocks: the commands of the reply's closed runnable blocks, by their place.</summary>
    public static List<string> Runnable(string text)
    {
        var found = new List<string>();
        if (Encoding.UTF8.GetByteCount(text) > MaximumRenderBytes) return found;
        Blocks(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), 0, found);
        return found;
    }

    private static bool Blank(string line) => line.Trim().Length == 0;

    private static int IndentOf(string line)
    {
        var width = 0;
        foreach (var c in line) { if (c == ' ') width++; else if (c == '\t') width += 4; else break; }
        return width;
    }

    private static string Unindent(string line, int width)
    {
        var at = 0;
        while (at < line.Length && width > 0 && line[at] is ' ' or '\t') { width -= line[at] == '\t' ? 4 : 1; at++; }
        return line[at..];
    }

    private static bool Interrupts(string line)
    {
        var item = ListItem().Match(line);
        return Fence().IsMatch(line) || Heading().IsMatch(line) || ThematicBreak().IsMatch(line) || line.TrimStart().StartsWith('>') || item.Success && item.Groups[3].Success;
    }

    private static int CellCount(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        return CellSplit().Split(text).Length;
    }

    private static bool TableStarts(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count && lines[i].Contains('|') && lines[i + 1].Contains('-') && TableDelimiter().IsMatch(lines[i + 1]) && CellCount(lines[i]) == CellCount(lines[i + 1]);

    private static void Blocks(IReadOnlyList<string> lines, int depth, List<string> found)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (Blank(line)) { i++; continue; }
            var opened = Fence().Match(line);
            if (opened.Success)
            {
                var mark = opened.Groups[2].Value; var code = new List<string>(); i++;
                while (i < lines.Count && !(lines[i].TrimStart().StartsWith(mark, StringComparison.Ordinal) && lines[i].Trim().Replace(mark[0].ToString(), "").Length == 0)) code.Add(Unindent(lines[i++], opened.Groups[1].Length));
                // A block still streaming (no closing fence) has no button.
                if (i >= lines.Count) continue;
                i++;
                var info = opened.Groups[3].Value.Trim();
                if (Command(info.Length == 0 ? null : info, string.Join('\n', code)) is { } command) found.Add(command);
                continue;
            }
            if (Heading().IsMatch(line) || ThematicBreak().IsMatch(line)) { i++; continue; }
            if (depth < MaximumDepth && line.TrimStart().StartsWith('>'))
            {
                var inner = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++) { var rest = lines[i].TrimStart()[1..]; inner.Add(rest.StartsWith(' ') ? rest[1..] : rest); }
                Blocks(inner, depth + 1, found);
                continue;
            }
            if (depth < MaximumDepth && ListItem().IsMatch(line)) { i = List(lines, i, depth, found); continue; }
            if (TableStarts(lines, i))
            {
                i += 2;
                while (i < lines.Count && !Blank(lines[i]) && lines[i].Contains('|')) i++;
                continue;
            }
            // A paragraph: up to a blank line, or a line that starts another block (markdown.ts: a line of backslashes alone adds no words).
            var words = false;
            for (; i < lines.Count && !Blank(lines[i]) && (!words || !Interrupts(lines[i]) && !TableStarts(lines, i)); i++) words |= lines[i].Trim().TrimEnd('\\').Length > 0;
        }
    }

    private static int List(IReadOnlyList<string> lines, int start, int depth, List<string> found)
    {
        var i = start;
        var opening = ListItem().Match(lines[i]);
        var margin = IndentOf(lines[i]);
        var ordered = char.IsAsciiDigit(opening.Groups[2].Value[0]);
        bool Sibling(string line)
        {
            var item = ListItem().Match(line); var indent = IndentOf(line);
            return item.Success && indent >= margin && indent <= margin + 1 && char.IsAsciiDigit(item.Groups[2].Value[0]) == ordered;
        }
        while (i < lines.Count && Sibling(lines[i]))
        {
            var item = ListItem().Match(lines[i]);
            var inner = margin + item.Groups[2].Length + 1;
            var content = new List<string> { item.Groups[3].Success ? item.Groups[3].Value : "" };
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (Blank(line))
                {
                    var next = i + 1;
                    while (next < lines.Count && Blank(lines[next])) next++;
                    if (next >= lines.Count || IndentOf(lines[next]) < inner) break;
                    for (; i < next; i++) content.Add("");
                    continue;
                }
                if (IndentOf(line) >= inner) { content.Add(Unindent(line, inner)); i++; continue; }
                if (ListItem().IsMatch(line) || Interrupts(line)) break;
                content.Add(line.Trim()); i++;
            }
            Blocks(content, depth + 1, found);
            var after = i;
            while (after < lines.Count && Blank(lines[after])) after++;
            if (after < lines.Count && Sibling(lines[after])) i = after;
        }
        return i;
    }
}
