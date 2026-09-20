using System.Text.Json;
using MightyClaude.Core;

internal static class SettingsPreferencesVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string InsertField(string baseJson, string name, string jsonValue)
    {
        var last = baseJson.LastIndexOf('}');
        return baseJson[..last] + $",\"{name}\":{jsonValue}" + baseJson[last..];
    }

    // A file written before AutoUpdateCLIs was introduced has no such key.
    // The field must default to null (off) and the snapshot version must stay 1.
    internal static Task MissingKeyKeepsDefaultOff()
    {
        var legacy = """{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":252}""";
        var decoded = JsonSerializer.Deserialize<AppSnapshot>(legacy, Wire.Json)!;
        Check(new AppSnapshot().AutoUpdateCLIs is null, "Default must be null (off)");
        Check(decoded.AutoUpdateCLIs is null, "Missing key must deserialize to null");
        Check(decoded.AutoUpdateCLIs != true, "Missing key must not equal true");
        var normalized = StateStore.Normalize(decoded, true);
        Check(normalized.Version == 1, "Version must stay 1");
        Check(normalized.AutoUpdateCLIs is null, "Normalize must preserve null");
        var serialized = JsonSerializer.Serialize(decoded, Wire.Json);
        Check(!serialized.Contains("autoUpdateCLIs"), "Null field must not appear in serialized JSON");
        return Task.CompletedTask;
    }

    // Explicit true and false must survive a save/load round-trip through StateStore.
    internal static async Task ExplicitOnAndOffPersistAcrossReloads()
    {
        var directory = Verification.Temp();
        try
        {
            foreach (var enabled in new bool[] { true, false })
            {
                var store = new StateStore(directory);
                await store.LoadAsync();
                await store.SaveAsync(new AppSnapshot { AutoUpdateCLIs = enabled });
                var path = Path.Combine(directory, "workspace-state.json");
                var fromFile = JsonSerializer.Deserialize<AppSnapshot>(await File.ReadAllTextAsync(path), Wire.Json)!;
                Check(fromFile.AutoUpdateCLIs == enabled, $"Saved file must have AutoUpdateCLIs={enabled}");
                Check(fromFile.Version == 1, "Saved file must keep Version=1");
                var restored = await new StateStore(directory).LoadAsync();
                Check(restored.AutoUpdateCLIs == enabled, $"Reloaded state must have AutoUpdateCLIs={enabled}");
                Check(restored.Version == 1, "Reloaded state must keep Version=1");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // Non-boolean JSON values must fall back to null; sessions and workspaces must survive.
    internal static Task OnlyJsonBooleansEnableSettingAndMalformedValuesKeepSessions()
    {
        var workspaceId = "pref-ws";
        var sessionId = "pref-sess";
        var logId = "pref-log";
        var workspace = new Workspace { Id = workspaceId, Name = "Prefs", Path = "C:\\fixture" };
        var log = new LogEntry(logId, "assistant", "Preserved", "2026-01-01T00:00:00.000Z");
        var session = new RunSession { Id = sessionId, WorkspaceId = workspaceId, Title = "Pref session", Logs = [log] };
        var baseJson = JsonSerializer.Serialize(new AppSnapshot { Workspaces = [workspace], Sessions = [session] }, Wire.Json);

        foreach (var (jsonValue, label) in new[] { ("0", "number 0"), ("1", "number 1"), ("\"true\"", "string true"), ("\"false\"", "string false"), ("[]", "array"), ("{\"enabled\":true}", "object"), ("null", "null") })
        {
            var json = InsertField(baseJson, "autoUpdateCLIs", jsonValue);
            var restored = JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)!;
            Check(restored.AutoUpdateCLIs is null, $"{label}: must fall back to null");
            Check(restored.AutoUpdateCLIs != true, $"{label}: must not equal true");
            Check(restored.Workspaces.Count == 1, $"{label}: workspaces must survive");
            Check(restored.Sessions.FirstOrDefault()?.Logs.FirstOrDefault()?.Text == "Preserved",
                $"{label}: session log must survive");
        }

        foreach (var (jsonValue, expected) in new[] { ("true", true), ("false", false) })
        {
            var json = InsertField(baseJson, "autoUpdateCLIs", jsonValue);
            var restored = JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)!;
            Check(restored.AutoUpdateCLIs == expected, $"JSON {jsonValue} must deserialize as {expected}");
        }

        return Task.CompletedTask;
    }
}
