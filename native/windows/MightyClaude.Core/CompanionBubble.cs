using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>One button of the pet's request bubble.</summary>
/// <param name="Icon">Drawn as its symbol alone; <paramref name="Label"/> is what it is called.</param>
/// <param name="Leading">Stands at the bubble's left edge; the others stand at the right.</param>
/// <param name="OwnRow">Fills a row of its own under the others, as the plan's two approvals do.</param>
/// <param name="Disabled">Drawn faded and not clickable.</param>
public sealed record CompanionBubbleButton(string Id, string Label, bool Prominent = false, bool Icon = false, bool Leading = false, bool OwnRow = false, bool Disabled = false);

/// <summary>A run of a plan block's text drawn in another face: <c>bold</c>, <c>italic</c> or <c>code</c>.</summary>
public sealed record CompanionPlanSpan(int Start, int Length, string Style);

/// <summary>
/// One block of a plan as the bubble draws it: a <c>heading</c> (<paramref name="Level"/> 1 to 6), a <c>paragraph</c>, a list
/// <c>item</c> (<paramref name="Level"/> deep, behind its <paramref name="Marker"/>), a <c>quote</c>, a <c>code</c> block
/// (its lines as written) or a <c>rule</c>.
/// </summary>
public sealed record CompanionPlanBlock(string Kind, string Text, int Level = 0, string? Marker = null, IReadOnlyList<CompanionPlanSpan>? Spans = null);

/// <summary>
/// What the pet's bubble offers for the request it shows (M/AgentCompanionViews.swift:299-452, M/AgentCompanion.swift:295-332):
/// which buttons, how a question is answered there, and the plan as it is drawn.
/// </summary>
public static class CompanionBubble
{
    public const string Open = "open", Review = "review", Deny = "deny", Allow = "allow";
    public const string Custom = "questions", Back = "question-back", Next = "question-commit", Choice = "choice-";
    public const string PlanReview = "plan-review", PlanCancel = "plan-cancel", PlanRevise = "plan-revise", PlanApproveConfirm = "plan-approve-confirm", PlanApprove = "plan-approve";
    /// <summary>Longer sources are shown as one literal paragraph, not parsed (M/AgentMarkdownView.swift maximumRenderBytes).</summary>
    public const int MaximumPlanBytes = 131_072;

    /// <summary>
    /// A tool approval (M/AgentCompanionViews.swift:341-344, 357-361): open at the left, Deny and Allow once at the right,
    /// Allow once off for a request that cannot be allowed here. Windows keeps its window with the whole request, behind
    /// a symbol beside open.
    /// </summary>
    public static IReadOnlyList<CompanionBubbleButton> Approval(bool canAllow)
    {
        var buttons = new List<CompanionBubbleButton> { new(Open, Locale.Get("companion.button.open"), Leading: true) };
        if (canAllow) buttons.Add(new(Review, Locale.Get("companion.button.review"), Icon: true, Leading: true));
        buttons.Add(new(Deny, ToolPermissionStrings.ButtonDeny));
        buttons.Add(new(Allow, ToolPermissionStrings.ButtonAllowOnce, Prominent: true, Disabled: !canAllow));
        return buttons;
    }

    /// <summary>
    /// A question (M/AgentCompanionViews.swift:341-356): open as its symbol, then Back from the second question on, Next or
    /// Send where a tap alone does not answer, and Cancel. The bubble takes no typing, so Windows keeps its window for an
    /// answer written out, behind a symbol beside open.
    /// </summary>
    public static IReadOnlyList<CompanionBubbleButton> Question(UserQuestionnaire questionnaire, QuestionnaireProgress progress)
    {
        var buttons = new List<CompanionBubbleButton>
        {
            new(Open, Locale.Get("companion.button.open"), Icon: true, Leading: true),
            new(Custom, Locale.Get("companion.button.answer"), Icon: true, Leading: true),
        };
        if (progress.Index > 0) buttons.Add(new(Back, Locale.Get("companion.question.back")));
        if (NeedsCommit(questionnaire, progress))
            buttons.Add(new(Next, Locale.Get(progress.Index + 1 < questionnaire.Questions.Count ? "companion.question.next" : "composer.send.name"), Prominent: true, Disabled: !CanCommit(questionnaire, progress)));
        buttons.Add(new(Deny, Locale.Get("phone.questionnaire.cancel")));
        return buttons;
    }

    /// <summary>
    /// A finished plan (M/AgentCompanionViews.swift:421-436): open and the plan's own window as symbols, Cancel and Request
    /// changes, then the two approvals each on a row of its own, the one that edits by itself last and prominent. All are
    /// off while an answer is on its way.
    /// </summary>
    public static IReadOnlyList<CompanionBubbleButton> Plan(bool sending) =>
    [
        new(Open, Locale.Get("companion.button.open"), Icon: true, Leading: true, Disabled: sending),
        new(PlanReview, Locale.Get("companion.plan.review"), Icon: true, Leading: true, Disabled: sending),
        new(PlanCancel, Locale.Get("plan.card.cancel"), Disabled: sending),
        new(PlanRevise, Locale.Get("plan.card.revise"), Disabled: sending),
        new(PlanApproveConfirm, Locale.Get("plan.card.approveConfirm"), OwnRow: true, Disabled: sending),
        new(PlanApprove, Locale.Get("plan.card.approveAuto"), Prominent: true, OwnRow: true, Disabled: sending),
    ];

    /// <summary>"선택 요청 2/3" while stepping through several questions (M/AgentCompanionViews.swift:379-383).</summary>
    public static string QuestionTitle(UserQuestionnaire questionnaire, QuestionnaireProgress progress)
    {
        var total = questionnaire.Questions.Count;
        return total > 1
            ? Locale.Get("companion.question.titleProgress", new Dictionary<string, string> { ["current"] = (progress.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ["total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            : Locale.Get("phone.questionnaire.title");
    }

    /// <summary>"Next" / "Send": needed under a multi-choice question and on the last of several questions.</summary>
    public static bool NeedsCommit(UserQuestionnaire questionnaire, QuestionnaireProgress progress) =>
        progress.Current(questionnaire) is { } question && (question.MultiSelect || questionnaire.Questions.Count > 1 && progress.Index + 1 == questionnaire.Questions.Count);

    public static bool CanCommit(UserQuestionnaire questionnaire, QuestionnaireProgress progress) =>
        progress.Current(questionnaire) is { } question && (question.MultiSelect ? progress.Selected.Count > 0 : progress.Answers.ContainsKey(question.Question));

    /// <summary>Toggled picks for a multi-choice question; the recorded answer when the user came back to one.</summary>
    public static bool Picked(string label, UserQuestionnaire questionnaire, QuestionnaireProgress progress) =>
        progress.Current(questionnaire) is { } question && (question.MultiSelect ? progress.Selected.Contains(label)
            : progress.Answers.TryGetValue(question.Question, out var answer) && answer.SelectedOptions.Contains(label));

    /// <summary>
    /// A tap on a choice: a single-choice tap records the answer and moves on, a multi-choice tap toggles. The answers to
    /// send come back only for a lone single-choice question; with several questions the last one waits for Send.
    /// </summary>
    public static IReadOnlyDictionary<string, UserQuestionAnswer>? Choose(string label, UserQuestionnaire questionnaire, QuestionnaireProgress progress)
    {
        var step = progress.Choose(label, questionnaire);
        return questionnaire.Questions.Count == 1 ? step?.Answers : null;
    }

    /// <summary>Next or Send: a multi-choice question's picks are recorded; the answers to send come back once every question has one.</summary>
    public static IReadOnlyDictionary<string, UserQuestionAnswer>? Commit(UserQuestionnaire questionnaire, QuestionnaireProgress progress)
    {
        if (progress.Current(questionnaire) is not { } question || !CanCommit(questionnaire, progress)) return null;
        if (question.MultiSelect) return progress.Commit("", questionnaire)?.Answers;
        try { questionnaire.ValidateAnswers(progress.Answers); }
        catch (ArgumentException) { return null; }
        return new Dictionary<string, UserQuestionAnswer>(progress.Answers, StringComparer.Ordinal);
    }

    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Rule = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Item = new(@"^([ \t]*)(?:([-*+])|(\d{1,9})[.)])[ \t]+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Quote = new(@"^ {0,3}>[ \t]?(.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The plan's Markdown as the blocks the bubble draws, in small type (M/AgentMarkdownView.swift compact): headings,
    /// paragraphs, list items with their bullets, numbers or check boxes, quotes, code and rules, with bold, italic and
    /// code runs inside a line. A table keeps its lines as written. Nothing is fetched or opened: a link is its words.
    /// </summary>
    public static IReadOnlyList<CompanionPlanBlock> PlanBlocks(string plan)
    {
        var blocks = new List<CompanionPlanBlock>();
        if (Encoding.UTF8.GetByteCount(plan) > MaximumPlanBytes) { blocks.Add(new("paragraph", plan)); return blocks; }
        var paragraph = new StringBuilder(); var kind = "paragraph"; var level = 0; string? marker = null;
        void Flush()
        {
            if (paragraph.Length == 0) return;
            var (text, spans) = Inline(paragraph.ToString());
            if (text.Length > 0) blocks.Add(new(kind, text, level, marker, spans));
            paragraph.Clear(); kind = "paragraph"; level = 0; marker = null;
        }
        var lines = plan.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index]; var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                // A fence runs to the next line that opens with the same three marks, or to the end.
                Flush(); var fence = trimmed[..3]; var code = new List<string>();
                while (++index < lines.Length && !lines[index].Trim().StartsWith(fence, StringComparison.Ordinal)) code.Add(lines[index]);
                blocks.Add(new("code", string.Join('\n', code)));
            }
            else if (trimmed.Length == 0) Flush();
            else if (Heading.Match(line) is { Success: true } heading)
            {
                Flush(); var (text, spans) = Inline(heading.Groups[2].Value);
                if (text.Length > 0) blocks.Add(new("heading", text, heading.Groups[1].Length, null, spans));
            }
            else if (Rule.IsMatch(line)) { Flush(); blocks.Add(new("rule", "")); }
            else if (trimmed[0] == '|')
            {
                Flush(); var rows = new List<string> { trimmed };
                while (index + 1 < lines.Length && lines[index + 1].TrimStart().StartsWith('|')) rows.Add(lines[++index].Trim());
                blocks.Add(new("code", string.Join('\n', rows)));
            }
            else if (Item.Match(line) is { Success: true } item)
            {
                Flush(); kind = "item"; var words = item.Groups[4].Value;
                level = Math.Min(3, item.Groups[1].Value.Replace("\t", "    ", StringComparison.Ordinal).Length / 2);
                marker = item.Groups[3].Success ? item.Groups[3].Value + "." : "•";
                if (words.StartsWith("[ ] ", StringComparison.Ordinal)) { marker = "☐"; words = words[4..]; }
                else if (words.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) { marker = "☑"; words = words[4..]; }
                paragraph.Append(words);
            }
            else if (Quote.Match(line) is { Success: true } quote)
            {
                if (kind != "quote") { Flush(); kind = "quote"; }
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(quote.Groups[1].Value.Trim());
            }
            else
            {
                // A line under a paragraph, a list item or a quote goes on with it.
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(trimmed);
            }
        }
        Flush();
        return blocks;
    }

    /// <summary>How far ahead a closing mark is looked for, so a plan full of stray marks is still read in one pass.</summary>
    private const int MaximumRun = 2_000;

    /// <summary>A line's words without their marks, and where the bold, italic and code runs lie in them.</summary>
    private static (string Text, IReadOnlyList<CompanionPlanSpan>? Spans) Inline(string source)
    {
        var text = new StringBuilder(source.Length); var spans = new List<CompanionPlanSpan>();
        Read(source, 0, source.Length, text, spans, 0);
        // Trimmed at the end only: every run was placed before these last spaces.
        var words = text.ToString().TrimEnd();
        return (words, spans.Count == 0 ? null : spans.Where(span => span.Start < words.Length).Select(span => span with { Length = Math.Min(span.Length, words.Length - span.Start) }).ToArray());
    }

    private static void Read(string source, int from, int to, StringBuilder text, List<CompanionPlanSpan> spans, int depth)
    {
        static bool Word(char c) => char.IsLetterOrDigit(c);
        for (var at = from; at < to; at++)
        {
            var c = source[at];
            if (c == '\\' && at + 1 < to && char.IsPunctuation(source[at + 1]) | char.IsSymbol(source[at + 1])) { text.Append(source[++at]); continue; }
            if (c == '`')
            {
                var marks = 1; while (at + marks < to && source[at + marks] == '`') marks++;
                var close = source.IndexOf(new string('`', marks), at + marks, Math.Min(to - at - marks, MaximumRun), StringComparison.Ordinal);
                if (close > 0)
                {
                    var code = source[(at + marks)..close].Trim();
                    spans.Add(new(text.Length, code.Length, "code")); text.Append(code); at = close + marks - 1; continue;
                }
            }
            if (c is '*' or '_' or '~' && depth < 4)
            {
                var twice = at + 1 < to && source[at + 1] == c; var marks = twice ? 2 : 1;
                // An underscore inside a word is part of the word (snake_case); a lone tilde is a tilde.
                var opens = at + marks < to && !char.IsWhiteSpace(source[at + marks]) && (c != '_' || at == from || !Word(source[at - 1])) && (c != '~' || twice);
                if (opens)
                {
                    var close = Closing(source, at + marks, Math.Min(to, at + marks + MaximumRun), c, marks);
                    if (close > 0)
                    {
                        var start = text.Length; Read(source, at + marks, close, text, spans, depth + 1);
                        if (c != '~') spans.Add(new(start, text.Length - start, twice ? "bold" : "italic"));
                        at = close + marks - 1; continue;
                    }
                }
            }
            if (c is '[' or '!' && depth < 4)
            {
                // A link or a picture is its words: nothing here opens or loads its target.
                var open = c == '!' ? at + 1 : at;
                if (open < to && source[open] == '[' && source.IndexOf("](", open, Math.Min(to - open, MaximumRun), StringComparison.Ordinal) is > 0 and var middle
                    && source.IndexOf(')', middle, Math.Min(to - middle, MaximumRun)) is > 0 and var end)
                { Read(source, open + 1, middle, text, spans, depth + 1); at = end; continue; }
            }
            text.Append(c);
        }
    }

    /// <summary>Where the run opened by <paramref name="marks"/> of <paramref name="mark"/> closes: the same marks after a character that is not a space, and for an underscore not inside a word.</summary>
    private static int Closing(string source, int from, int to, char mark, int marks)
    {
        for (var at = from; at + marks <= to; at++)
        {
            if (source[at] == '`') { var end = source.IndexOf('`', at + 1, to - at - 1); if (end > 0) { at = end; continue; } }
            if (source[at] != mark || at == from || char.IsWhiteSpace(source[at - 1])) continue;
            var run = 1; while (at + run < to && source[at + run] == mark) run++;
            // The marks nearest the end of a run close this one; those before them close what was opened inside it.
            // A pair of marks is never a single mark's close.
            if (run >= marks && (marks == 2 || run % 2 == 1))
            {
                var close = at + run - marks;
                if (mark != '_' || close + marks >= source.Length || !char.IsLetterOrDigit(source[close + marks])) return close;
            }
            at += run - 1;
        }
        return -1;
    }
}
