using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyCode.Core;

/// <summary>
/// A question an agent other than Claude asks, in the shape the question card already reads (Claude's AskUserQuestion input)
/// (M/MightyCore/AgentQuestions.swift): opencode's and Hermes's ACP <c>elicitation/create</c> form, Hermes's clarify approval (one
/// <c>allow_once</c> option per choice) and a pi extension's dialog (<c>select</c>, <c>confirm</c>, <c>input</c>). The card, the companion
/// and the phone read the request as they read Claude's; only how the answer travels back is each channel's own.
/// </summary>
public static class AgentQuestions
{
    public const string ToolName = "AskUserQuestion";

    public sealed class Item(string header, string question)
    {
        public string Header { get; set; } = header;
        public string Question { get; set; } = question;
        public bool MultiSelect { get; set; }
        public List<(string Label, string Description)> Options { get; } = [];
    }

    /// <summary>Text as the card shows it: no control characters, bounded, never blank.</summary>
    public static string? Text(string? value, int limit, bool singleLine = false)
    {
        var cleaned = ActivitySupport.Clean(value ?? "", limit, singleLine);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// The card's request, or null when the card cannot show these questions whole (too many questions or options, a repeated question or
    /// label): the caller keeps its old answer.
    /// </summary>
    public static ToolPermissionRequest? Request(string id, string runId, string toolUseId, IReadOnlyList<Item> items)
    {
        if (items.Count == 0) return null;
        var questions = new JsonArray();
        foreach (var item in items)
        {
            var options = new JsonArray();
            foreach (var (label, description) in item.Options) options.Add(new JsonObject { ["description"] = description, ["label"] = label });
            questions.Add(new JsonObject { ["header"] = item.Header, ["multiSelect"] = item.MultiSelect, ["options"] = options, ["question"] = item.Question });
        }
        var json = new JsonObject { ["questions"] = questions }.ToJsonString(Indented);
        if (UserQuestionnaire.Parse(json) is null) return null;
        return new ToolPermissionRequest(id, runId, toolUseId, ToolName, json, ActivitySupport.Clean(items[0].Question, 1_000, singleLine: true), CanAllow: false, CanAnswerQuestions: true);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The answers checked against the card's own questions: what was picked, in the offered order, and what was typed (trimmed, empty when
    /// nothing was). Throws the card's own words for an answer it does not take.
    /// </summary>
    public static Dictionary<string, (List<string> Picked, string Typed)> Answers(ToolPermissionRequest request, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        if (!request.CanAnswerQuestions || UserQuestionnaire.Parse(request.InputJson) is not { } questionnaire) throw new InvalidOperationException(Locale.Get("permission.error.noAnswers"));
        questionnaire.ValidateAnswers(answers);
        var result = new Dictionary<string, (List<string>, string)>(StringComparer.Ordinal);
        foreach (var question in questionnaire.Questions)
        {
            var answer = answers.TryGetValue(question.Question, out var given) && given is not null ? given : new UserQuestionAnswer([]);
            var selected = answer.SelectedOptions.ToHashSet(StringComparer.Ordinal);
            result[question.Question] = (question.Options.Select(o => o.Label).Where(selected.Contains).ToList(), answer.CustomText?.Trim() ?? "");
        }
        return result;
    }
}

/// <summary>
/// An ACP <c>elicitation/create</c> form (https://agentclientprotocol.com, the MCP elicitation schema) as questions
/// (M/MightyCore/AgentQuestions.swift ACPElicitationForm). Each property is one question: a string with <c>oneOf</c> / <c>enum</c> is a single
/// choice, an array of those a multiple choice, a plain string a typed answer, a boolean a yes or no. opencode adds <c>&lt;key&gt;_custom</c>
/// (a plain string) beside a choice whose answer may be typed: that is the card's own "write your answer" row, not a question. Numbers and
/// nested objects are not questions the card asks; such a form is declined.
/// </summary>
public sealed class AcpElicitationForm
{
    public enum Kind { Single, Multiple, Text, Boolean }
    /// <param name="Values">The value each shown label stands for.</param>
    /// <param name="CustomKey">Where a typed answer goes: its own property, or (when the agent takes any string for a choice) the field itself.</param>
    public sealed record Field(string Key, string Question, Kind Kind, IReadOnlyDictionary<string, JsonNode?> Values, string? CustomKey);
    public IReadOnlyList<Field> Fields { get; }
    public IReadOnlyList<AgentQuestions.Item> Items { get; }

    private AcpElicitationForm(List<Field> fields, List<AgentQuestions.Item> items) { Fields = fields; Items = items; }

    /// <summary>
    /// The form, or null when the card cannot ask it. <paramref name="freeChoices"/>: the agent reads a typed answer in place of a choice
    /// (Hermes's clarify does; opencode does only where it sent the <c>_custom</c> property).
    /// </summary>
    public static AcpElicitationForm? Read(JsonObject parameters, bool freeChoices)
    {
        if ((Text(parameters["mode"]) ?? "form") != "form" || parameters["requestedSchema"] is not JsonObject schema || (Text(schema["type"]) ?? "object") != "object"
            || schema["properties"] is not JsonObject properties || properties.Count == 0) return null;
        var message = AgentQuestions.Text(Text(parameters["message"]), 8_192);
        // A JSON object has no order here; `q0, q1, … q10` sort as numbers do.
        var keys = properties.Select(p => p.Key).OrderBy(k => k, NumericOrder.Instance).ToList();
        static bool PlainString(JsonNode? property) => property is JsonObject value && Text(value["type"]) == "string" && value["enum"] is null && value["oneOf"] is null && value["anyOf"] is null;
        var customKeys = keys.Where(key => key.EndsWith("_custom", StringComparison.Ordinal) && PlainString(properties[key]) && properties[key[..^7]] is JsonObject choice
            && (Choices(choice) is not null || Choices(choice["items"] as JsonObject ?? []) is not null)).ToHashSet(StringComparer.Ordinal);
        var fields = new List<Field>(); var items = new List<AgentQuestions.Item>();
        foreach (var key in keys.Where(key => !customKeys.Contains(key)))
        {
            if (System.Text.Encoding.UTF8.GetByteCount(key) > 256 || properties[key] is not JsonObject property) return null;
            var title = AgentQuestions.Text(Text(property["title"]), 256, singleLine: true);
            var description = AgentQuestions.Text(Text(property["description"]), 8_192);
            // opencode: the title is the question's short header and the description its text. Hermes: the property says nothing and the
            // form's message is the question.
            var question = description ?? (properties.Count - customKeys.Count == 1 ? message : null) ?? title;
            if (question is null) return null;
            var header = (description is not null ? title : null) ?? Locale.Get("questionnaire.header.agent");
            var kind = Kind.Text; List<(string Label, string Description, JsonNode? Value)> options = [];
            switch (Text(property["type"]))
            {
                case "string":
                    if (Choices(property) is { } single) { kind = Kind.Single; options = single; }
                    break;
                case "array":
                    if (Choices(property["items"] as JsonObject ?? []) is not { } multiple) return null;
                    kind = Kind.Multiple; options = multiple;
                    break;
                case "boolean":
                    kind = Kind.Boolean;
                    options = [(Locale.Get("questionnaire.option.yes"), "", JsonValue.Create(true)), (Locale.Get("questionnaire.option.no"), "", JsonValue.Create(false))];
                    break;
                default: return null;
            }
            if (options.Select(o => o.Label).Distinct(StringComparer.Ordinal).Count() != options.Count) return null;
            var custom = customKeys.Contains(key + "_custom") ? key + "_custom" : kind == Kind.Text || freeChoices && kind != Kind.Boolean ? key : null;
            fields.Add(new(key, question, kind, options.ToDictionary(o => o.Label, o => o.Value, StringComparer.Ordinal), custom));
            var item = new AgentQuestions.Item(header, question) { MultiSelect = kind == Kind.Multiple };
            item.Options.AddRange(options.Select(o => (o.Label, o.Description)));
            items.Add(item);
        }
        return fields.Count == 0 ? null : new AcpElicitationForm(fields, items);
    }

    /// <summary>The choices of a <c>oneOf</c> / <c>anyOf</c> list of <c>{const, title, description}</c> or of an <c>enum</c> (with <c>enumNames</c>); null when the schema offers none.</summary>
    private static List<(string Label, string Description, JsonNode? Value)>? Choices(JsonObject schema)
    {
        static bool Usable(JsonNode? value) => value is JsonValue v && (v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number);
        static string Shown(JsonNode value) => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        if ((schema["oneOf"] ?? schema["anyOf"]) is JsonArray list && list.Count > 0)
        {
            var result = new List<(string, string, JsonNode?)>();
            foreach (var entry in list)
            {
                if (entry is not JsonObject choice || !Usable(choice["const"]) || AgentQuestions.Text(Text(choice["title"]) ?? Shown(choice["const"]!), 1_024, singleLine: true) is not { } label) return null;
                result.Add((label, AgentQuestions.Text(Text(choice["description"]), 8_192) ?? "", choice["const"]!.DeepClone()));
            }
            return result;
        }
        if (schema["enum"] is JsonArray values && values.Count > 0)
        {
            var names = schema["enumNames"] as JsonArray;
            var result = new List<(string, string, JsonNode?)>();
            for (var index = 0; index < values.Count; index++)
            {
                if (!Usable(values[index])) return null;
                var name = names is not null && index < names.Count ? Text(names[index]) : null;
                if (AgentQuestions.Text(name ?? Shown(values[index]!), 1_024, singleLine: true) is not { } label) return null;
                result.Add((label, "", values[index]!.DeepClone()));
            }
            return result;
        }
        return null;
    }

    /// <summary>The <c>content</c> of an <c>accept</c> answer. A typed answer where the agent takes only its choices is refused, and the card stays answerable.</summary>
    public JsonObject Content(IReadOnlyDictionary<string, (List<string> Picked, string Typed)> answers)
    {
        var content = new JsonObject();
        foreach (var field in Fields)
        {
            if (!answers.TryGetValue(field.Question, out var answer)) throw new ArgumentException(Locale.Get("questionnaire.error.missingAnswer"));
            var picked = answer.Picked.Where(field.Values.ContainsKey).Select(label => field.Values[label]?.DeepClone()).ToList();
            if (answer.Typed.Length > 0)
            {
                if (field.CustomKey is not { } customKey) throw new ArgumentException(Locale.Get("questionnaire.error.choicesOnly"));
                if (customKey != field.Key)
                {
                    content[customKey] = answer.Typed;
                    if (field.Kind == Kind.Multiple && picked.Count > 0) content[field.Key] = new JsonArray([.. picked]);
                    continue;
                }
                content[field.Key] = field.Kind == Kind.Multiple ? new JsonArray([.. picked, JsonValue.Create(answer.Typed)]) : JsonValue.Create(answer.Typed);
                continue;
            }
            if (picked.Count == 0) throw new ArgumentException(Locale.Get("questionnaire.error.missingAnswer"));
            content[field.Key] = field.Kind == Kind.Multiple ? new JsonArray([.. picked]) : picked[0];
        }
        return content;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Keys as numbers sort (<c>q2</c> before <c>q10</c>), the Mac's <c>compare(_:options: .numeric)</c>.</summary>
    private sealed class NumericOrder : IComparer<string>
    {
        internal static readonly NumericOrder Instance = new();
        public int Compare(string? x, string? y)
        {
            string a = x ?? "", b = y ?? ""; int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    var left = a[si..i].TrimStart('0'); var right = b[sj..j].TrimStart('0');
                    if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
                    var digits = string.CompareOrdinal(left, right);
                    if (digits != 0) return digits;
                    continue;
                }
                if (a[i] != b[j]) return a[i].CompareTo(b[j]);
                i++; j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
