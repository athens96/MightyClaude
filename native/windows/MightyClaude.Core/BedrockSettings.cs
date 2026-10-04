using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public static class BedrockSettings
{
    public const int MaximumSettingsBytes = 1_048_576;
    public static string ConfigurationOnly => Locale.Get("settings.cliAccounts.bedrockConfigurationOnly");
    public static string ExternalConfigurationOnly => Locale.Get("settings.cliAccounts.externalConfigurationOnly");
    public static string ConflictMessage => Locale.Get("settings.cliAccounts.bedrockConflict");
    public static string SettingsPath(string home, IReadOnlyDictionary<string, string> environment) => Path.Combine(environment.TryGetValue("CLAUDE_CONFIG_DIR", out var directory) && !string.IsNullOrWhiteSpace(directory) ? Path.GetFullPath(directory, home) : Path.Combine(home, ".claude"), "settings.json");
    public static string? ConflictDetail(IReadOnlyDictionary<string, string> environment, string? json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumSettingsBytes || !environment.TryGetValue("AWS_BEARER_TOKEN_BEDROCK", out var token) || string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("env", out var env) && env.Text("AWS_BEARER_TOKEN_BEDROCK") is { } other && !string.IsNullOrWhiteSpace(other) && token.Trim() != other.Trim()) return ConflictMessage;
        }
        catch (JsonException) { }
        return null;
    }
    private static readonly HashSet<string> BedrockKeys = new(StringComparer.Ordinal) { "AWS_BEARER_TOKEN_BEDROCK", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "ANTHROPIC_BEDROCK_BASE_URL" };
    private static readonly HashSet<string> ModelKeys = new(StringComparer.Ordinal) { "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_CUSTOM_MODEL_OPTION", "CLAUDE_CODE_SUBAGENT_MODEL" };
    private static bool BedrockModel(string? value) => value is not null && Regex.IsMatch(value, @"^(?:(?:us|eu|apac|global)\.)?anthropic\.|^arn:aws[^:]*:bedrock:");
    public static string ResetUserSettingsJson(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumSettingsBytes) throw new InvalidDataException("Claude settings exceed the size limit.");
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Claude settings must be a JSON object.");
        if (root["env"] is not null && root["env"] is not JsonObject) throw new InvalidDataException("Claude settings env must be an object.");
        if (root["env"] is JsonObject env)
        {
            var enabled = env["CLAUDE_CODE_USE_BEDROCK"]?.ToString() is "1" or "true";
            foreach (var key in env.Select(p => p.Key).ToArray())
                if (BedrockKeys.Contains(key) || enabled && key is "AWS_REGION" or "AWS_DEFAULT_REGION" || ModelKeys.Contains(key) && BedrockModel(env[key]?.ToString())) env.Remove(key);
        }
        if (BedrockModel(root["model"]?.ToString())) root.Remove("model");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    /// Explicit user action only. Keeps unrelated AWS credentials/settings and
    /// never touches project/managed settings or the OS credential store.
    public static string? ResetUserSettings(string home, IReadOnlyDictionary<string, string> environment)
    {
        var path = SettingsPath(home, environment);
        if (!File.Exists(path)) return null;
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing a linked Claude settings directory.");
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumSettingsBytes) throw new IOException("Claude settings are not a bounded regular file.");
        var original = File.ReadAllText(path);
        var updated = ResetUserSettingsJson(original);
        if (JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(updated))) return null;
        var backup = path + ".bedrock-backup-" + Guid.NewGuid().ToString("N");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(path, backup, false);
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            if (File.ReadAllText(path) != original) throw new IOException("Claude settings changed during reset; retry after saving settings.");
            File.Move(temporary, path, true);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
