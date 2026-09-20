using System.Text.Json;

namespace MightyClaude.Core;

public sealed record ToolPermissionField(string Label, string Value, bool Code);

public sealed class ToolPermissionPresentation
{
    public const int MaximumFields = 10;

    public string Title { get; }
    public string? Headline { get; }
    public IReadOnlyList<ToolPermissionField> Fields { get; }

    private ToolPermissionPresentation(string title, string? headline, IReadOnlyList<ToolPermissionField> fields)
    { Title = title; Headline = headline; Fields = fields; }

    public static ToolPermissionPresentation Make(string toolName, string inputJson)
    {
        JsonElement? root = null;
        try { using var doc = JsonDocument.Parse(inputJson); root = doc.RootElement.Clone(); } catch { }
        if (root is not { ValueKind: JsonValueKind.Object }) return Fallback(toolName);

        var input = root.Value;
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = toolName.Split("__", 3);
            var server = parts.Length >= 2 ? parts[1] : toolName;
            var mcpTitle = ToolPermissionStrings.TitleMcpTemplate.Replace("{server}", server);
            return new(mcpTitle, null, GenericFields(input));
        }

        return toolName switch
        {
            "Bash" => BashPresentation(input),
            "Read" => new(ToolPermissionStrings.TitleRead, null, ReadFields(input)),
            "Edit" or "MultiEdit" => new(ToolPermissionStrings.TitleEdit, null, EditFields(input)),
            "Write" => new(ToolPermissionStrings.TitleWrite, null, WriteFields(input)),
            "NotebookEdit" => new(ToolPermissionStrings.TitleNotebookEdit, null, NotebookEditFields(input)),
            "Glob" => new(ToolPermissionStrings.TitleGlob, null, GlobFields(input)),
            "Grep" => new(ToolPermissionStrings.TitleGrep, null, GrepFields(input)),
            "WebFetch" => new(ToolPermissionStrings.TitleWebFetch, null, WebFetchFields(input)),
            "WebSearch" => new(ToolPermissionStrings.TitleWebSearch, null, WebSearchFields(input)),
            "Agent" or "Task" => new(ToolPermissionStrings.TitleAgent, null, AgentFields(input)),
            _ => new(ToolPermissionStrings.TitleTool, null, GenericFields(input)),
        };
    }

    private static ToolPermissionPresentation Fallback(string toolName)
        => new(ToolPermissionStrings.TitleTool, null, []);

    private static ToolPermissionPresentation BashPresentation(JsonElement input)
    {
        var headline = input.Text("description");
        var fields = new List<ToolPermissionField>();
        if (input.Text("command") is { } cmd) fields.Add(new(ToolPermissionStrings.FieldCommand, cmd, true));
        if (input.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.Number)
            fields.Add(new(ToolPermissionStrings.FieldTimeoutMs, timeout.GetRawText(), false));
        if (input.TryGetProperty("background", out var bg) && bg.ValueKind != JsonValueKind.Null)
            fields.Add(new(ToolPermissionStrings.FieldBackground, bg.GetBoolean() ? ToolPermissionStrings.BooleanYes : ToolPermissionStrings.BooleanNo, false));
        return new(ToolPermissionStrings.TitleBash, string.IsNullOrEmpty(headline) ? null : headline, fields);
    }

    private static IReadOnlyList<ToolPermissionField> ReadFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("file_path") is { } path) fields.Add(new(ToolPermissionStrings.FieldFile, path, false));
        if (input.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Number)
            fields.Add(new(ToolPermissionStrings.FieldOffset, offset.GetRawText(), false));
        if (input.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
            fields.Add(new(ToolPermissionStrings.FieldLimit, limit.GetRawText(), false));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> EditFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("file_path") is { } path) fields.Add(new(ToolPermissionStrings.FieldFile, path, false));
        if (input.Text("old_string") is { } old) fields.Add(new(ToolPermissionStrings.FieldOldString, old, true));
        if (input.Text("new_string") is { } @new) fields.Add(new(ToolPermissionStrings.FieldNewString, @new, true));
        if (input.TryGetProperty("replace_all", out var all) && all.ValueKind != JsonValueKind.Null)
            fields.Add(new(ToolPermissionStrings.FieldReplaceAll, all.GetBoolean() ? ToolPermissionStrings.BooleanYes : ToolPermissionStrings.BooleanNo, false));
        if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            fields.Add(new(ToolPermissionStrings.FieldEdits, edits.GetArrayLength().ToString(), false));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> WriteFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("file_path") is { } path) fields.Add(new(ToolPermissionStrings.FieldFile, path, false));
        if (input.Text("content") is { } content) fields.Add(new(ToolPermissionStrings.FieldContent, content, true));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> NotebookEditFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("notebook_path") is { } nb) fields.Add(new(ToolPermissionStrings.FieldNotebook, nb, false));
        if (input.TryGetProperty("cell_number", out var cell) && cell.ValueKind == JsonValueKind.Number)
            fields.Add(new(ToolPermissionStrings.FieldCell, cell.GetRawText(), false));
        if (input.Text("edit_mode") is { } mode) fields.Add(new(ToolPermissionStrings.FieldEditMode, mode, false));
        if (input.Text("new_source") is { } src) fields.Add(new(ToolPermissionStrings.FieldContent, src, true));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> GlobFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("pattern") is { } p) fields.Add(new(ToolPermissionStrings.FieldPattern, p, true));
        if (input.Text("path") is { } path) fields.Add(new(ToolPermissionStrings.FieldPath, path, false));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> GrepFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("pattern") is { } p) fields.Add(new(ToolPermissionStrings.FieldPattern, p, true));
        if (input.Text("path") is { } path) fields.Add(new(ToolPermissionStrings.FieldPath, path, false));
        if (input.Text("glob") is { } g) fields.Add(new(ToolPermissionStrings.FieldGlob, g, false));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> WebFetchFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("url") is { } url) fields.Add(new(ToolPermissionStrings.FieldUrl, url, false));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> WebSearchFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("query") is { } q) fields.Add(new(ToolPermissionStrings.FieldQuery, q, false));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> AgentFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        if (input.Text("subagent_type") is { } t) fields.Add(new(ToolPermissionStrings.FieldSubagentType, t, false));
        if (input.Text("model") is { } model) fields.Add(new(ToolPermissionStrings.FieldModel, model, false));
        if (input.Text("prompt") is { } prompt) fields.Add(new(ToolPermissionStrings.FieldInstruction, prompt, true));
        return fields;
    }

    private static IReadOnlyList<ToolPermissionField> GenericFields(JsonElement input)
    {
        var fields = new List<ToolPermissionField>();
        foreach (var prop in input.EnumerateObject().Take(MaximumFields))
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => ToolPermissionStrings.BooleanYes,
                JsonValueKind.False => ToolPermissionStrings.BooleanNo,
                JsonValueKind.Null => "",
                _ => prop.Value.GetRawText()
            };
            if (value.Length > 0) fields.Add(new(prop.Name, value, false));
            if (fields.Count >= MaximumFields) break;
        }
        return fields;
    }
}
