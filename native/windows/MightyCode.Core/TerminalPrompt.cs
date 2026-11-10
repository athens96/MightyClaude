using System.Text;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

/// <summary>
/// A command in an agent pane's terminal that waits for the user to type something (docs/terminal-prompts.md): the pane and the
/// phone show it as a card, and the answer is typed into the command's terminal. <see cref="Kind"/> is one of
/// <see cref="TerminalPromptKinds.All"/>. <see cref="Id"/> is <c>&lt;handle&gt;:&lt;cleaned output length&gt;</c>: a new wait after
/// more output is a new prompt. <see cref="DefaultYes"/> is for yesNo: true when Enter alone answers yes, false for no, null for neither.
/// </summary>
public sealed record TerminalPrompt(string Id, string Handle, string Text, IReadOnlyList<string> Options, string Kind, bool? DefaultYes = null)
{
    public const int MaximumTextLength = 300;
    public const int MaximumOptions = 12;
}

public static class TerminalPromptKinds
{
    public const string YesNo = "yesNo", Secret = "secret", Text = "text", Choice = "choice";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { YesNo, Secret, Text, Choice };
}

/// <summary>What the user answered a <see cref="TerminalPrompt"/> with; <see cref="Input"/> is what that types into the terminal.</summary>
public readonly record struct TerminalPromptAnswer(string Name, string? Line = null)
{
    public const int MaximumPhoneText = 4_096;
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal) { "up", "down", "enter", "space" };
    public static TerminalPromptAnswer Yes => new("yes");
    public static TerminalPromptAnswer No => new("no");
    public static TerminalPromptAnswer Typed(string line) => new("text", line);
    public static TerminalPromptAnswer Key(string key) => new(key);

    /// <summary>The bytes the terminal would get had the user typed the answer there: Enter is a CR; a line holding a CR or LF is cut at it.</summary>
    public string Input => Name switch
    {
        "yes" => "y\r",
        "no" => "n\r",
        "text" => (Line ?? "").Split('\r', '\n')[0] + "\r",
        "up" => "\u001b[A",
        "down" => "\u001b[B",
        "enter" => "\r",
        "space" => " ",
        _ => "",
    };

    /// <summary>The phone's <c>{promptId, answer, text?}</c>, read strictly: null for an unknown answer, a missing, empty or over-long text, or a malformed id.</summary>
    public static TerminalPromptAnswer? FromPhone(string? promptId, string? answer, string? text)
    {
        if (string.IsNullOrEmpty(promptId) || promptId.Length > 200 || promptId.Any(char.IsControl)) return null;
        switch (answer)
        {
            case "yes": return Yes;
            case "no": return No;
            case "text": return string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaximumPhoneText ? null : Typed(text);
            default: return answer is not null && Keys.Contains(answer) ? Key(answer) : null;
        }
    }
}

/// <summary>Tells from the end of a command's cleaned output whether it waits for input; asked only once the output has been quiet (<see cref="TerminalPromptMonitor"/>).</summary>
public static class TerminalPromptDetector
{
    public readonly record struct Detected(string Text, IReadOnlyList<string> Options, string Kind, bool? DefaultYes);

    private static readonly Regex YesNoPattern = new(@"[\(\[]\s*(y(es)?)\s*/\s*(n(o)?)\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Korean, Japanese and Chinese programs ask for a password in these words too (M/TerminalPrompt.swift); matched against what a command
    // prints, never shown (allowed in scripts/korean-literal-budget.json).
    private static readonly Regex SecretPattern = new(@"(password|passphrase|passcode|\bpin\b|token|secret|비밀번호|암호|パスワード|密码)[^\n]*[:?：]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Markers = ["❯", "›", ">", "●", "◉", "◆", "→"];

    /// <summary>The prompt the output ends with, null when it does not look like one. The last line is the one after the last newline, where the cursor waits.</summary>
    public static Detected? Detect(string tail)
    {
        var lines = tail.Split('\n');
        var last = TrimEnd(lines[^1]);
        if (Choice(lines) is { } list) return list;
        if (last.Length == 0 || last.Length > 400 || last.EndsWith("...", StringComparison.Ordinal) || last.EndsWith('…')) return null;
        var text = Shown(last);
        var match = YesNoPattern.Match(last);
        if (match.Success)
        {
            var letters = new string(match.Value.Where(char.IsLetter).ToArray());
            var yes = new string(letters.TakeWhile(c => "yYeEsS".Contains(c)).ToArray());
            var no = letters[yes.Length..];
            bool? defaultYes = yes[0] == 'Y' && no[0] == 'n' ? true : yes[0] == 'y' && no[0] == 'N' ? false : null;
            return new(text, [], TerminalPromptKinds.YesNo, defaultYes);
        }
        if (SecretPattern.IsMatch(last)) return new(text, [], TerminalPromptKinds.Secret, null);
        if ("?:›>：？".Contains(last[^1])) return new(text, [], TerminalPromptKinds.Text, null);
        return null;
    }

    /// <summary>A list the arrow keys move through: a line marked with a pointer among the last lines, below the question (a line opening with <c>?</c>).</summary>
    private static Detected? Choice(string[] lines)
    {
        var recent = lines.Skip(Math.Max(0, lines.Length - (TerminalPrompt.MaximumOptions + 2))).Select(TrimEnd).ToArray();
        var question = Array.FindLastIndex(recent, line => line.Trim().StartsWith('?'));
        if (question < 0) return null;
        var options = recent.Skip(question + 1).Where(line => line.Trim().Length > 0).ToArray();
        if (options.Length < 2 || !options.Any(IsMarked)) return null;
        return new(Shown(recent[question]), options.Take(TerminalPrompt.MaximumOptions).Select(line => line.Trim()).ToArray(), TerminalPromptKinds.Choice, null);
    }

    public static bool IsMarked(string line) { var start = line.Trim(); return Markers.Any(m => start.StartsWith(m, StringComparison.Ordinal)); }
    private static string Shown(string line) { var text = line.Trim(); return text.Length > TerminalPrompt.MaximumTextLength ? text[..TerminalPrompt.MaximumTextLength] : text; }
    private static string TrimEnd(string line) => line.TrimEnd();
}

/// <summary>One page of a command's output read from a cursor.</summary>
public readonly record struct PromptRead(string Output, int NextOffset, bool MoreRemains);

/// <summary>What <see cref="TerminalPromptMonitor"/> needs of an agent pane's terminal (<see cref="AgentTerminal"/>).</summary>
public interface IPromptTerminal
{
    IReadOnlyList<string> RunningHandles();
    PromptRead ReadOutput(string handle, int offset, int maxChars);
}

/// <summary>
/// Follows the commands running in one agent pane's terminal and says which of them waits for input. It reads each command's output
/// with its own cursor (the agent's reads are not touched), cleans it, keeps its last <see cref="TailCharacters"/>, and asks
/// <see cref="TerminalPromptDetector"/> once the output has not grown for the quiet time. An answered prompt stays hidden until the output grows.
/// </summary>
public sealed class TerminalPromptMonitor(double quietSeconds = TerminalPromptMonitor.QuietSeconds, Func<DateTime>? now = null)
{
    public const double QuietSeconds = 0.8;
    public const int TailCharacters = 4_096;
    private const int ReadChunk = 21_000;

    private sealed class Follow(DateTime grewAt)
    {
        internal int Offset, Length;
        internal readonly TerminalTextCleaner Cleaner = new();
        internal string Tail = "";
        internal DateTime GrewAt = grewAt;
        internal int? AnsweredAtLength;
    }

    private readonly Func<DateTime> now = now ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, Follow> follows = [];
    private readonly object gate = new();

    /// <summary>Reads what the running commands printed since the last poll; the prompt the newest waiting one shows, null when none waits.</summary>
    public TerminalPrompt? Poll(IPromptTerminal terminal)
    {
        lock (gate)
        {
            var running = terminal.RunningHandles();
            foreach (var gone in follows.Keys.Where(h => !running.Contains(h)).ToArray()) follows.Remove(gone);
            TerminalPrompt? found = null;
            foreach (var handle in running)
            {
                if (!follows.TryGetValue(handle, out var follow)) follows[handle] = follow = new(now());
                bool more;
                do
                {
                    var read = terminal.ReadOutput(handle, follow.Offset, ReadChunk);
                    more = read.MoreRemains && read.NextOffset > follow.Offset;
                    follow.Offset = read.NextOffset;
                    var cleaned = follow.Cleaner.Clean(read.Output);
                    if (cleaned.Length == 0) continue;
                    var tail = follow.Tail + cleaned;
                    follow.Tail = tail.Length > TailCharacters ? tail[^TailCharacters..] : tail;
                    follow.Length += cleaned.Length;
                    follow.GrewAt = now();
                } while (more);
                if ((now() - follow.GrewAt).TotalSeconds < quietSeconds || follow.AnsweredAtLength is { } at && follow.Length <= at) continue;
                if (TerminalPromptDetector.Detect(follow.Tail) is not { } detected) continue;
                found = new($"{handle}:{follow.Length}", handle, detected.Text, detected.Options, detected.Kind, detected.DefaultYes);
            }
            return found;
        }
    }

    /// <summary>The user answered <paramref name="prompt"/>: it is not shown again until its command prints more.</summary>
    public void Answered(TerminalPrompt prompt)
    {
        lock (gate) if (follows.TryGetValue(prompt.Handle, out var follow)) follow.AnsweredAtLength = follow.Length;
    }
}
