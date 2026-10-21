using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// The one shape a style's panel and the phone payload are both drawn from (M/Styles/StylePanelProjection.swift
/// StylePanel, §5.6, §7.3). Written with <see cref="Wire.Json"/> it is the phone's <c>panel</c>: camel-case keys, and a
/// part that is not there is left out, as the Mac's encoder leaves it out.
/// </summary>
public sealed record StylePanel(StylePanel.StyleInfo Style, StylePanel.PhaseInfo? Phase, IReadOnlyList<StylePanel.GroupInfo> Groups,
    IReadOnlyList<StylePanel.ActionInfo> Actions, IReadOnlyList<string> Next, string? Recommended, IReadOnlyList<StylePanel.AttachmentInfo> Attachments,
    StylePanel.SetupInfo Setup, string? Guidance, StylePanel.PresentationInfo Presentation,
    [property: JsonConverter(typeof(StylePanel.WidgetsJson))] IReadOnlyList<StyleStateWidget>? Widgets)
{
    public sealed record StyleInfo(string Id, string Name, string Source, string? Icon, string? Tint);
    public sealed record PhaseInfo(string Id, string Title, int Index, int Count);
    public sealed record GroupInfo(string Id, string Title, string? Axis, string? Question, bool Selected, IReadOnlyList<string> Actions);
    public sealed record ActionInfo(string Id, string Title, string? Icon, string? Glyph, string Help, string? Scope, bool TakesText, bool RequiresText, IReadOnlyList<string> Flags, bool Prominent);
    /// <summary>Read-only chips a built-in feature offered. Always empty since v7 (the vocabulary is empty, §1.8), but still sent: a phone that predates v7 reads the field.</summary>
    public sealed record AttachmentInfo(string Id, string Title, string? Detail, bool ReadOnly);
    public sealed record SetupInfo(bool Ready, IReadOnlyList<string> Missing, string? Hint, string? InstallCommand);
    public sealed record PresentationInfo(string HeaderTitle, string Source, string? Icon, string? Tint);

    /// <summary>§1.16: each computed widget with only the fields its kind has (<see cref="StylePresentation.Payload"/>).</summary>
    public sealed class WidgetsJson : JsonConverter<IReadOnlyList<StyleStateWidget>>
    {
        public override IReadOnlyList<StyleStateWidget> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IReadOnlyList<StyleStateWidget> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Select(StylePresentation.Payload).ToArray(), options);
    }
}

public static class StylePanelProjection
{
    /// <summary>The flags an action may carry, in the order the panel lists them (M/Styles/StyleManifest.swift StyleActionFlag).</summary>
    private static readonly string[] Flags = ["userInvoked", "readOnly"];

    /// <summary>
    /// A pane's panel (M/Styles/StylePanelProjection.swift make): its phase from the pane's own requests and the
    /// <paramref name="state"/> its sources read, the chips it may draw now, and what its features and requirements found.
    /// </summary>
    public static StylePanel Make(RegisteredStyle style, RunSession session, string? selectedGroupId, IReadOnlyDictionary<string, string> capabilityStates,
        StylePrerequisiteResult prerequisites, bool running = false, StyleStateReading? state = null) =>
        Make(style, session.GraphRuns is { Count: > 0 } runs ? runs.Select(run => run.Input) : session.Logs.Where(log => log.Kind == "user").Select(log => log.Text),
            selectedGroupId, capabilityStates, prerequisites, running, session, state);

    public static StylePanel Make(RegisteredStyle style, IEnumerable<string> prompts, string? selectedGroupId, IReadOnlyDictionary<string, string> capabilityStates,
        StylePrerequisiteResult prerequisites, bool running = false, RunSession? session = null, StyleStateReading? state = null)
    {
        var manifest = style.Manifest; var evaluator = style.Evaluator;
        // §1.16: the same state the pane's phase bar reads.
        var phase = evaluator.CurrentPhase(prompts, state?.Files, state?.PlanStage);
        var jobOpen = session is not null && evaluator.JobOpen(session);
        var ordered = manifest.Phases.OrderBy(p => p.Order).ToArray();
        var selected = manifest.Groups.FirstOrDefault(g => g.Id == selectedGroupId) ?? evaluator.InitialGroup(capabilityStates);
        var next = evaluator.VisibleActions(phase, selected, running, jobOpen).Select(a => a.Id).ToArray();
        var presentation = manifest.Root.GetProperty("presentation");
        string? icon = StyleManifest.Text(presentation, "icon"), tint = StyleManifest.Text(presentation, "tint");
        var index = phase is null ? -1 : Array.FindIndex(ordered, p => p.Id == phase.Id);
        return new(
            new(manifest.Id, manifest.Name, style.Source, icon, tint),
            index < 0 ? null : new(phase!.Id, phase.Title, index, ordered.Length),
            manifest.Groups.Select(g => new StylePanel.GroupInfo(g.Id, g.Title, g.Axis, g.Question, g.Id == selected?.Id, g.Actions)).ToArray(),
            // The catalogue travels whole; what is drawn is decided by groups and next.
            manifest.Actions.Select(a => new StylePanel.ActionInfo(a.Id, a.Title, a.Icon, a.Glyph, a.Help, a.Scope, a.TakesText, a.RequiresText,
                Flags.Where(flag => a.Flags?.Contains(flag) == true).ToArray(), a.Id == next.FirstOrDefault())).ToArray(),
            next, evaluator.RecommendedAction(capabilityStates),
            [],
            new(prerequisites.Ready, prerequisites.Missing, prerequisites.Hint, manifest.Root.TryGetProperty("install", out var install) ? StyleManifest.Text(install, "command") : null),
            evaluator.Guidance(phase, running, jobOpen),
            new(phase is null ? manifest.Name : manifest.Name + " · " + phase.Title, style.Source, icon, tint),
            state is { Widgets.Count: > 0 } ? state.Widgets : null);
    }

    /// <summary>Frozen with the tag so a golden stays byte-stable (M/Styles/StylePanelProjection.swift serialise, §8.4).</summary>
    public static byte[] Serialise(StylePanel panel) => Canonical(JsonSerializer.SerializeToNode(panel, Wire.Json)!);

    /// <summary>
    /// The golden's bytes as the Mac's encoder writes them: UTF-8, keys sorted, two-space indent with <c>" : "</c> after a
    /// key, an empty array or object opened and closed around one blank line, no <c>\/</c>, one trailing newline.
    /// </summary>
    public static byte[] Canonical(JsonNode node)
    {
        var text = new StringBuilder();
        Write(text, node, 0);
        return Encoding.UTF8.GetBytes(text.Append('\n').ToString());
    }

    private static void Write(StringBuilder text, JsonNode? node, int depth)
    {
        void Line(int level) => text.Append('\n').Append(' ', level * 2);
        switch (node)
        {
            case JsonObject value:
                text.Append('{'); var first = true;
                // Case-blind first and by code unit between equals, as the Mac's sorted keys are.
                foreach (var pair in value.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) text.Append(','); first = false;
                    Line(depth + 1); Quote(text, pair.Key); text.Append(" : "); Write(text, pair.Value, depth + 1);
                }
                if (first) text.Append('\n');
                Line(depth); text.Append('}'); break;
            case JsonArray value:
                text.Append('[');
                for (var index = 0; index < value.Count; index++) { if (index > 0) text.Append(','); Line(depth + 1); Write(text, value[index], depth + 1); }
                if (value.Count == 0) text.Append('\n');
                Line(depth); text.Append(']'); break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String: Quote(text, value.GetValue<string>()); break;
            case null: text.Append("null"); break;
            default: text.Append(node.ToJsonString()); break;
        }
    }

    private static void Quote(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
            text.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f", < ' ' => "\\u" + ((int)c).ToString("x4"), _ => c.ToString() });
        text.Append('"');
    }
}
