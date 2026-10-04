using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>Mac AppStore.expandedWorkspaceSet: disclosure is independent of selection.</summary>
public static class WorkspaceDisclosure
{
    public static HashSet<string> Expanded(AppSnapshot snapshot) =>
        (snapshot.ExpandedWorkspaceIds ?? (snapshot.ActiveWorkspaceId is { } active ? [active] : []))
            .Where(id => snapshot.Workspaces.Any(workspace => workspace.Id == id)).ToHashSet(StringComparer.Ordinal);

    public static AppSnapshot Open(AppSnapshot snapshot, string id)
    {
        if (!snapshot.Workspaces.Any(workspace => workspace.Id == id)) return snapshot;
        var ids = Expanded(snapshot); ids.Add(id);
        return snapshot with { ExpandedWorkspaceIds = ids.Order(StringComparer.Ordinal).ToList() };
    }

    public static AppSnapshot Toggle(AppSnapshot snapshot, string id)
    {
        if (!snapshot.Workspaces.Any(workspace => workspace.Id == id)) return snapshot;
        var ids = Expanded(snapshot); if (!ids.Add(id)) ids.Remove(id);
        return snapshot with { ExpandedWorkspaceIds = ids.Order(StringComparer.Ordinal).ToList() };
    }
}

/// <summary>Like the Mac cast to [String], malformed optional state falls back to legacy defaults.</summary>
internal sealed class WorkspaceDisclosureConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        if (value.RootElement.ValueKind != JsonValueKind.Array || value.RootElement.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String)) return null;
        return value.RootElement.EnumerateArray().Select(id => id.GetString()!).ToList();
    }
    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray(); foreach (var id in value) writer.WriteStringValue(id); writer.WriteEndArray();
    }
}
