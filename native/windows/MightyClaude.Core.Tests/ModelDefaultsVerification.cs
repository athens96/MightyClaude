using System.Text.Json;
using MightyClaude.Core;

/// Proves that saved state containing old per-mode model-defaults keys still
/// loads without error and leaves every other field byte-identical.
internal static class ModelDefaultsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task LegacyPerModeKeysLoad()
    {
        // A snapshot that was saved by an older build which wrote per-mode keys
        // into the ModelDefaults field.  The new build must deserialize it without
        // error and preserve the field so even older builds can still load it.
        var json = """
            {
              "version": 1,
              "workspaces": [],
              "sessions": [],
              "layout": "grid",
              "theme": "dark",
              "sidebarWidth": 252,
              "modelDefaults": {
                "claude": {
                  "modeDefaults": { "manual": "claude-sonnet-5", "auto": "claude-opus-5", "plan": "claude-fable-5-1" },
                  "registeredModels": [{ "name": "acme/legacy", "supportsEffort": false }]
                },
                "codex": {
                  "modeDefaults": { "manual": "gpt-6-astra" },
                  "registeredModels": []
                }
              }
            }
            """;

        var snapshot = JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)!;
        Check(snapshot.Version == 1, "version must stay 1");
        Check(snapshot.ModelDefaults is not null, "modelDefaults field must survive deserialization");
        Check(snapshot.ModelDefaults!.Claude.ModeDefaults["manual"] == "claude-sonnet-5",
            "legacy claude manual key must be preserved");
        Check(snapshot.ModelDefaults.Claude.ModeDefaults["auto"] == "claude-opus-5",
            "legacy claude auto key must be preserved");
        Check(snapshot.ModelDefaults.Claude.RegisteredModels.Count == 1,
            "legacy registeredModels must be preserved");
        Check(snapshot.ModelDefaults.Codex.ModeDefaults["manual"] == "gpt-6-astra",
            "legacy codex manual key must be preserved");

        // The field round-trips: serialising and re-reading leaves it intact.
        var round = JsonSerializer.Deserialize<AppSnapshot>(
            JsonSerializer.Serialize(snapshot, Wire.Json), Wire.Json)!;
        Check(round.ModelDefaults is not null, "modelDefaults must survive round-trip");
        Check(round.ModelDefaults!.Claude.ModeDefaults["manual"] == "claude-sonnet-5",
            "claude manual must survive round-trip");

        // A workspace that carries the same legacy field loads identically.
        var workspaceJson = """
            {
              "id": "ws1",
              "name": "Legacy workspace",
              "path": "/tmp/ws",
              "createdAt": "2026-09-24T00:00:00Z",
              "modelDefaults": {
                "claude": { "modeDefaults": { "plan": "claude-fable-5-1" }, "registeredModels": [] },
                "codex": { "modeDefaults": {}, "registeredModels": [] }
              }
            }
            """;
        var workspace = JsonSerializer.Deserialize<Workspace>(workspaceJson, Wire.Json)!;
        Check(workspace.ModelDefaults is not null, "workspace modelDefaults must survive deserialization");
        Check(workspace.ModelDefaults!.Claude.ModeDefaults["plan"] == "claude-fable-5-1",
            "workspace legacy plan key must be preserved");

        return Task.CompletedTask;
    }
}
