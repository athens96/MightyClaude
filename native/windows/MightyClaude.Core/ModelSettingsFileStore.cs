using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// Reads and writes owned model knobs in the external tool config files.
///
/// omc:       agents.&lt;camelCaseKey&gt;.model  in  %USERPROFILE%\.config\claude-omc\config.jsonc
/// Ouroboros: dotted *_model scalar keys   in  %USERPROFILE%\.ouroboros\config.yaml
///
/// Re-reads immediately before writing, backs up next to the original, writes
/// atomically via a temp-file rename. Throws (file left byte-identical) when a
/// file cannot be parsed.  Pass homeDirectory for tests so no real user file is
/// ever touched.
public sealed class ModelSettingsFileStore
{
    public string HomeDirectory { get; }

    public ModelSettingsFileStore(string? homeDirectory = null)
    {
        HomeDirectory = homeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string OmcConfigPath =>
        Path.Combine(HomeDirectory, ".config", "claude-omc", "config.jsonc");

    public string OuroborosConfigPath =>
        Path.Combine(HomeDirectory, ".ouroboros", "config.yaml");

    // MARK: - omc (config.jsonc)

    /// Returns [camelCaseKey: model] for every agent that has a model key,
    /// or null when the file does not exist.  Throws when file exists but
    /// cannot be parsed.
    public Dictionary<string, string>? LoadOmcAgents()
    {
        var path = OmcConfigPath;
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        var root = ParseJSONC(data);
        if (!root.TryGetValue("agents", out var agentsRaw) ||
            agentsRaw is not Dictionary<string, object?> agentsDict)
            return [];
        var result = new Dictionary<string, string>();
        foreach (var (key, agentRaw) in agentsDict)
        {
            if (agentRaw is Dictionary<string, object?> agentDict &&
                agentDict.TryGetValue("model", out var modelRaw) &&
                modelRaw is string model)
                result[key] = model;
        }
        return result;
    }

    /// Merges agents into config.jsonc, writing only agents.&lt;key&gt;.model for the
    /// provided keys.  Re-reads immediately before writing; backs up; atomic write.
    /// Throws (file byte-identical) when the current file is unparseable.
    public void SaveOmcAgents(Dictionary<string, string> agents)
    {
        var path = OmcConfigPath;
        var root = new Dictionary<string, object?>();
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            root = ParseJSONC(existing);   // throws on parse error; file unchanged
            WriteBackup(existing, path);
        }

        var agentsSection = root.TryGetValue("agents", out var ag) &&
                            ag is Dictionary<string, object?> d ? d : [];
        foreach (var (key, model) in agents)
        {
            if (model == "default")
            {
                if (agentsSection.TryGetValue(key, out var entryRaw) &&
                    entryRaw is Dictionary<string, object?> entry)
                {
                    entry.Remove("model");
                    if (entry.Count == 0) agentsSection.Remove(key);
                    else agentsSection[key] = entry;
                }
            }
            else
            {
                var entry = agentsSection.TryGetValue(key, out var er) &&
                            er is Dictionary<string, object?> e ? e : [];
                entry["model"] = model;
                agentsSection[key] = entry;
            }
        }
        root["agents"] = agentsSection;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = SerializeToJson(root);
        AtomicWrite(Encoding.UTF8.GetBytes(json), path);
    }

    // MARK: - Ouroboros (config.yaml)

    /// Returns [dottedKey: value] for scalar values whose key ends in _model,
    /// or null when the file does not exist.
    public Dictionary<string, string>? LoadOuroborosKeys()
    {
        var path = OuroborosConfigPath;
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (Encoding.UTF8.GetString(bytes) is not { } content)
            throw new InvalidDataException(Locale.Get("settings.phaseModels.error.yamlNotUtf8"));
        var all = ParseYAMLScalars(content);
        return all.Where(kv => kv.Key.EndsWith("_model", StringComparison.Ordinal))
                  .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// Rewrites only the lines in config.yaml whose dotted key is in owned.
    /// All other lines — including orchestrator.cli_path — are preserved verbatim.
    public void SaveOuroborosKeys(Dictionary<string, string> owned)
    {
        var path = OuroborosConfigPath;
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(bytes);
        WriteBackup(bytes, path);
        var updated = RewriteYAMLKeys(content, owned);
        AtomicWrite(Encoding.UTF8.GetBytes(updated), path);
    }

    // MARK: - JSONC helpers

    internal static Dictionary<string, object?> ParseJSONC(byte[] data)
    {
        string source;
        try { source = Encoding.UTF8.GetString(data); }
        catch { throw new InvalidDataException(Locale.Get("settings.phaseModels.error.jsoncNotUtf8")); }
        var stripped = StripJSONCComments(source);
        try
        {
            using var doc = JsonDocument.Parse(stripped);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(Locale.Get("settings.phaseModels.error.jsoncNotObject"));
            return ElementToDict(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Locale.Get("settings.phaseModels.error.jsoncUnparseable"), ex);
        }
    }

    private static Dictionary<string, object?> ElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = ElementToValue(prop.Value);
        return dict;
    }

    private static object? ElementToValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => ElementToDict(el),
        JsonValueKind.Array => el.EnumerateArray().Select(ElementToValue).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? (object?)i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static string SerializeToJson(object? value)
    {
        // Minimal pretty-print so the output resembles what macOS writes.
        return JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        });
    }

    internal static string StripJSONCComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;
        var inString = false;
        var inLine = false;
        var inBlock = false;

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n') { inLine = false; result.Append(c); }
            }
            else if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
            }
            else if (inString)
            {
                result.Append(c);
                if (c == '\\')
                {
                    i++;
                    if (i < source.Length) result.Append(source[i]);
                }
                else if (c == '"') inString = false;
            }
            else
            {
                if (c == '"') { inString = true; result.Append(c); }
                else if (c == '/' && next == '/') { inLine = true; i++; }
                else if (c == '/' && next == '*') { inBlock = true; i++; }
                else result.Append(c);
            }
            i++;
        }
        return result.ToString();
    }

    // MARK: - YAML helpers

    internal static Dictionary<string, string> ParseYAMLScalars(string content)
    {
        var result = new Dictionary<string, string>();
        string? section = null;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t')
            {
                var t = line.TrimEnd();
                if (t.EndsWith(':') && !t.Contains(' '))
                    section = t[..^1];
                else
                    section = null;
            }
            else if (section is not null)
            {
                if (trimmed.StartsWith('-')) continue;
                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                var key = trimmed[..colon].Trim();
                var afterColon = trimmed[(colon + 1)..].Trim();
                if (afterColon.Length == 0 || afterColon[0] == '{' ||
                    afterColon[0] == '[' || afterColon[0] == '|' || afterColon[0] == '>')
                    continue;
                result[$"{section}.{key}"] = afterColon;
            }
        }
        return result;
    }

    internal static string RewriteYAMLKeys(string content, Dictionary<string, string> updates)
    {
        var lines = new List<string>();
        string? section = null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t')
            {
                var t = line.TrimEnd();
                if (t.EndsWith(':') && !t.Contains(' ') && !t.StartsWith('#'))
                    section = t[..^1];
                else
                    section = null;
                lines.Add(line);
                continue;
            }
            if (section is null || trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('-'))
            {
                lines.Add(line);
                continue;
            }
            var colon = trimmed.IndexOf(':');
            if (colon < 0) { lines.Add(line); continue; }
            var key = trimmed[..colon].Trim();
            var dottedKey = $"{section}.{key}";
            if (updates.TryGetValue(dottedKey, out var newValue))
            {
                var leadingWS = line.Length - line.TrimStart().Length;
                lines.Add(line[..leadingWS] + key + ": " + newValue);
            }
            else lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    // MARK: - File I/O helpers

    private static void WriteBackup(byte[] data, string path)
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var uid = Guid.NewGuid().ToString("N")[..8];
        var ext = Path.GetExtension(path);
        var dir = Path.GetDirectoryName(path)!;
        var backupName = $"config.mighty-backup-{ts}-{uid}{ext}";
        File.WriteAllBytes(Path.Combine(dir, backupName), data);
    }

    private static void AtomicWrite(byte[] data, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(tmp, data);
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }
}
