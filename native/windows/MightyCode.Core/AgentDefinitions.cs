using System.Text;
using System.Text.Json;

namespace MightyCode.Core;

/// <summary>A custom sub-agent as its definition file's frontmatter says it: only what the flow diagram draws of it (macOS AgentDefinition).</summary>
/// <param name="Name">The name an Agent call's <c>subagent_type</c> uses: the frontmatter's <c>name</c>, else the file's own.</param>
/// <param name="Color">One of Claude Code's agent colours (<see cref="FlowRoles.AgentColors"/>); null when the file names none, or one Claude Code lacks.</param>
/// <param name="Model">The model the definition asks for (an alias, a model id or <c>inherit</c>); null when it names none.</param>
public sealed record AgentDefinition(string Name, string? Color = null, string? Model = null);

/// <summary>
/// Reads custom sub-agents' colour and model from their definition files, for the flow diagram's headers (<see cref="FlowRoles.Subagent"/>): the
/// project's <c>&lt;workspace&gt;\.claude\agents\*.md</c>, then the user's <c>&lt;Claude config&gt;\agents\*.md</c> (a project agent wins over a user agent of
/// the same name), and a plugin's agent (<c>plugin:agent</c>) from that plugin's own <c>agents</c> folder. Nothing is written; a file's head is read
/// up to <see cref="MaximumBytes"/>, a folder up to <see cref="MaximumFiles"/> files, and a file that does not open or has no frontmatter is left out
/// (macOS AgentDefinitions).
/// </summary>
public static class AgentDefinitions
{
    public const int MaximumBytes = 16 * 1024;
    public const int MaximumFiles = 256;
    internal const int MaximumLines = 80;
    private const int MaximumPluginIndexBytes = 1024 * 1024;
    private static readonly char[] LineBreaks = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    /// <summary>The definition a file's text holds, or null when it does not open with a frontmatter block. The frontmatter is read line by line, not as
    /// YAML: a top-level <c>key: value</c>, with or without quotes and with a trailing <c># comment</c> cut; lists, blocks and anything indented are passed
    /// over, so a file YAML would reject still gives what it can.</summary>
    public static AgentDefinition? Parse(string text, string fileName)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0) return null;
        var first = lines[0].StartsWith('﻿') ? lines[0][1..] : lines[0];
        if (first.Trim(' ', '\t') != "---") return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var closed = false;
        var count = 0;
        foreach (var line in lines.Skip(1))
        {
            if (count >= MaximumLines) break;
            count += 1;
            var trimmed = line.Trim(' ', '\t');
            if (trimmed is "---" or "...") { closed = true; break; }
            if (line.Length == 0 || !(char.IsLetter(line[0]) || line[0] == '_') || line.IndexOf(':') is not (var colon and >= 0)) continue;
            var key = line[..colon].Trim(' ', '\t').ToLowerInvariant();
            if (key is not ("name" or "color" or "model") || fields.ContainsKey(key)) continue;
            if (Scalar(line[(colon + 1)..]) is { } value) fields[key] = value;
        }
        if (!closed) return null;
        var fallback = Path.GetFileNameWithoutExtension(fileName);
        var name = ActivitySupport.Clean(fields.GetValueOrDefault("name") ?? fallback, 160, true);
        if (name.Length == 0) return null;
        var color = fields.GetValueOrDefault("color")?.ToLowerInvariant();
        var model = fields.GetValueOrDefault("model");
        return new AgentDefinition(name, color is not null && FlowRoles.AgentColors.Contains(color) ? color : null, Wire.Model(model) ? model : null);
    }

    /// <summary>Swift's <c>split(omittingEmptySubsequences: false, whereSeparator: \.isNewline)</c>, where "\r\n" is one break.</summary>
    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (Array.IndexOf(LineBreaks, text[index]) < 0) continue;
            result.Add(text[start..index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
        result.Add(text[start..]);
        return result;
    }

    /// <summary>A frontmatter value on its key's line: quotes taken off, a trailing comment cut; null for an empty value and for the start of a list, a
    /// mapping or a block.</summary>
    internal static string? Scalar(string raw)
    {
        var value = raw.Trim(' ', '\t');
        if (value.Length == 0 || "[{|>&*!".Contains(value[0])) return null;
        var first = value[0];
        if (first is '"' or '\'')
        {
            var end = value.IndexOf(first, 1);
            if (end < 0) return null;
            value = value[1..end];
        }
        else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and >= 0) value = value[..comment].Trim(' ', '\t');
        return value.Length == 0 ? null : value;
    }

    /// <summary>The definitions of the agents <paramref name="types"/> name, by type: project and user agents by their name, a plugin's agent by
    /// <c>plugin:agent</c>. <paramref name="workspace"/> is the pane's local folder (null reads no project agents). Folders are read again only when a
    /// file in them came, went or changed (<see cref="Cache"/>).</summary>
    public static Dictionary<string, AgentDefinition> Load(IReadOnlySet<string> types, string? workspace, string configDirectory, Cache? cache = null)
    {
        var held = cache ?? Cache.Shared;
        var found = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        if (types.Count == 0) return found;
        // The user's first, so the project's replace them.
        var folders = new List<string> { Path.Combine(configDirectory, "agents") };
        if (workspace is not null) folders.Add(Path.Combine(workspace, ".claude", "agents"));
        foreach (var folder in folders)
            foreach (var definition in held.Definitions(folder))
                if (types.Contains(definition.Name)) found[definition.Name] = definition;
        var scoped = types.Where(type => type.Contains(':') && !found.ContainsKey(type)).ToList();
        if (scoped.Count == 0) return found;
        var installs = held.PluginFolders(configDirectory);
        foreach (var type in scoped)
        {
            var parts = type.Split(':');
            if (parts.Length < 2 || parts[^1] is not { Length: > 0 } agent || agent.Contains('/') || agent.Contains('\\') || agent == ".."
                || !installs.TryGetValue(parts[0], out var folder)) continue;
            // The file is named after the agent; its frontmatter's name is the same one without the plugin.
            if (held.Definitions(Path.Combine(folder, "agents")).FirstOrDefault(d => d.Name == agent) is { } definition) found[type] = definition with { Name = type };
        }
        return found;
    }

    /// <summary>What was read of each folder, kept until the folder's files change.</summary>
    public sealed class Cache
    {
        public static readonly Cache Shared = new();
        private readonly object gate = new();
        private readonly Dictionary<string, (string Stamp, List<AgentDefinition> Definitions)> folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Stamp, Dictionary<string, string> Folders)> plugins = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every definition in <paramref name="folder"/>'s <c>*.md</c> files (not its sub-folders), in file-name order.</summary>
        public List<AgentDefinition> Definitions(string folder)
        {
            string[] names;
            try { names = Directory.Exists(folder) ? Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().ToArray() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { names = []; }
            if (names.Length == 0 && !Directory.Exists(folder)) { lock (gate) folders.Remove(folder); return []; }
            var files = names.Where(name => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.')).Order(StringComparer.Ordinal).Take(MaximumFiles);
            var stamp = new StringBuilder();
            var readable = new List<(string Name, string Path)>();
            foreach (var name in files)
            {
                var path = Path.Combine(folder, name);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) continue;
                    stamp.Append(name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                    readable.Add((name, path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var key = stamp.ToString();
            lock (gate) if (folders.TryGetValue(folder, out var held) && held.Stamp == key) return held.Definitions;
            var definitions = readable.Select(file => Head(file.Path) is { } text ? Parse(text, file.Name) : null).OfType<AgentDefinition>().ToList();
            lock (gate) folders[folder] = (key, definitions);
            return definitions;
        }

        /// <summary>Each installed plugin's folder by the plugin's name, from Claude Code's own list (<c>plugins\installed_plugins.json</c>:
        /// <c>"&lt;plugin&gt;@&lt;marketplace&gt;": [{ "installPath": … }]</c>).</summary>
        public Dictionary<string, string> PluginFolders(string configDirectory)
        {
            var index = Path.Combine(configDirectory, "plugins", "installed_plugins.json");
            FileInfo info;
            try { info = new FileInfo(index); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return []; }
            if (!info.Exists || info.Length > MaximumPluginIndexBytes) return [];
            var stamp = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            lock (gate) if (plugins.TryGetValue(index, out var held) && held.Stamp == stamp) return held.Folders;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(index));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("plugins", out var installed) && installed.ValueKind == JsonValueKind.Object)
                    foreach (var property in installed.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        var plugin = property.Name.Split('@')[0];
                        var entry = property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().FirstOrDefault() : property.Value;
                        if (plugin.Length == 0 || result.ContainsKey(plugin) || entry.ValueKind != JsonValueKind.Object || entry.Text("installPath") is not { Length: > 0 } path) continue;
                        result[plugin] = path;
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            lock (gate) plugins[index] = (stamp, result);
            return result;
        }

        /// <summary>The first <see cref="MaximumBytes"/> of a file, which hold any frontmatter worth reading.</summary>
        private static string? Head(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[MaximumBytes];
                var read = 0;
                while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var count and > 0) read += count;
                return read == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
