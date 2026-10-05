using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record UserQuestionOption(string Label, string Description);
public sealed record UserQuestion(string Header, string Question, bool MultiSelect, IReadOnlyList<UserQuestionOption> Options);
public sealed record UserQuestionAnswer(IReadOnlyList<string> SelectedOptions, string? CustomText = null);

/// <summary>The complete bounded AskUserQuestion input. Question text is a protocol key, never shortened.</summary>
public sealed record UserQuestionnaire(IReadOnlyList<UserQuestion> Questions)
{
    public static UserQuestionnaire? Parse(string input)
    {
        // The input is a JSON object or it is nothing: a transcript asks this of every reply and every code
        // block it draws, and nearly all of them are prose or code that should not cost a thrown parse error.
        if (input.AsSpan().TrimStart() is not ['{', ..] || Encoding.UTF8.GetByteCount(input) > 65_536) return null;
        try
        {
            using var document = JsonDocument.Parse(input);
            if (!document.RootElement.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() is < 1 or > 4) return null;
            var result = new List<UserQuestion>();
            foreach (var q in questions.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object || !q.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.String || !Valid(header.GetString()!, 256)
                    || !q.TryGetProperty("question", out var question) || question.ValueKind != JsonValueKind.String || !Valid(question.GetString()!, 8192)
                    || !q.TryGetProperty("multiSelect", out var multi) || multi.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !q.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() is < 2 or > 4) return null;
                var choices = new List<UserQuestionOption>();
                foreach (var option in options.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String || !Valid(label.GetString()!, 1024)
                        || !option.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String || !Valid(description.GetString()!, 8192, true)) return null;
                    choices.Add(new(label.GetString()!, description.GetString()!));
                }
                if (choices.Select(o => o.Label).Distinct(StringComparer.Ordinal).Count() != choices.Count) return null;
                result.Add(new(header.GetString()!, question.GetString()!, multi.GetBoolean(), choices));
            }
            return result.Select(q => q.Question).Distinct(StringComparer.Ordinal).Count() != result.Count ? null : new(result);
        }
        // ArgumentException: text that is no valid UTF-16 (a surrogate without its pair) cannot be read as JSON at all.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }

    public IReadOnlyDictionary<string, string> ValidateAnswers(IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        if (answers is null || !Questions.Select(q => q.Question).ToHashSet(StringComparer.Ordinal).SetEquals(answers.Keys)) throw new ArgumentException(Locale.Get("questionnaire.error.incomplete"));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var question in Questions)
        {
            var answer = answers[question.Question];
            if (answer?.SelectedOptions is null) throw new ArgumentException(Locale.Get("questionnaire.error.selection"));
            var selected = answer.SelectedOptions.ToHashSet(StringComparer.Ordinal); var custom = answer.CustomText?.Trim() ?? "";
            if (selected.Count != answer.SelectedOptions.Count || !selected.IsSubsetOf(question.Options.Select(o => o.Label))
                || !Valid(custom, 8192, true) || selected.Count == 0 && custom.Length == 0 || !question.MultiSelect && selected.Count + (custom.Length > 0 ? 1 : 0) != 1)
                throw new ArgumentException(Locale.Get("questionnaire.error.selection"));
            var values = question.Options.Where(o => selected.Contains(o.Label)).Select(o => o.Label).ToList();
            if (custom.Length > 0) values.Add(custom);
            result[question.Question] = string.Join(", ", values);
        }
        return result;
    }

    private static bool Valid(string value, int maximum, bool empty = false) => Encoding.UTF8.GetByteCount(value) <= maximum
        && (empty || !string.IsNullOrWhiteSpace(value)) && !value.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Control && r.Value is not (9 or 10 or 13));
}
