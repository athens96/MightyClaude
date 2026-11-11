using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// Whether Claude's own settings turn the advisor on. The advisor is Claude Code's experimental server-side advisor tool: <c>claude --advisor
/// &lt;model&gt;</c>, the <c>advisorModel</c> setting, or <c>/advisor [model|off]</c>, and the <c>CLAUDE_CODE_DISABLE_ADVISOR_TOOL</c> variable turns it
/// off. A pane left on "follow Claude's settings" passes nothing, so what the run will do is in Claude's settings files; they are read here, never
/// written (macOS ClaudeAdvisorSettings).
///
/// The files, in the order Claude reads them, a later one winning: the user's <c>settings.json</c> in Claude's config folder
/// (<c>%USERPROFILE%\.claude</c>, or <c>CLAUDE_CONFIG_DIR</c>), then the working folder's <c>.claude\settings.json</c> and
/// <c>.claude\settings.local.json</c>. A file is read only up to <see cref="MaximumBytes"/>; one that is missing, larger, or not a JSON object says
/// nothing. A file is read again only when its size or modification time changed.
/// </summary>
public static class ClaudeAdvisorSettings
{
    public const int MaximumBytes = 1024 * 1024;
    /// <summary>The variable that turns the advisor off, in the run's environment or in a settings file's <c>env</c>.</summary>
    public const string DisableVariable = "CLAUDE_CODE_DISABLE_ADVISOR_TOOL";

    /// <summary>What one settings file says about the advisor. <c>Model</c>: <c>advisorModel</c>, a model's name, or <see cref="Off"/> when the key says
    /// off (<c>"off"</c>, empty, false or null); null when the file has no such key. <c>Disabled</c>: the file's <c>env</c> sets the disable variable,
    /// true when it turns the advisor off, false when it sets it to something that does not, null when it does not name it.</summary>
    public sealed record Entry(Choice? Model = null, bool? Disabled = null);
    /// <summary>What a file's <c>advisorModel</c> chose: a model's name, or off (<see cref="Off"/>, no name).</summary>
    public sealed record Choice(string? Name)
    {
        public static readonly Choice Off = new((string?)null);
        public static Choice Of(string name) => new(name);
        public bool IsOff => Name is null;
    }

    /// <summary>The settings files in the order Claude reads them. <paramref name="workspace"/> is the run's working folder (null reads the user's
    /// file alone: another computer's folder is not on this one).</summary>
    public static List<string> Files(string? workspace, string configDirectory)
    {
        var files = new List<string> { Path.Combine(configDirectory, "settings.json") };
        if (workspace is not null)
        {
            files.Add(Path.Combine(workspace, ".claude", "settings.json"));
            files.Add(Path.Combine(workspace, ".claude", "settings.local.json"));
        }
        return files;
    }

    /// <summary>Claude Code's own folder: <c>CLAUDE_CONFIG_DIR</c>, else <c>&lt;home&gt;\.claude</c> (macOS AgentDefinitions.configDirectory).</summary>
    public static string ConfigDirectory(string home, IReadOnlyDictionary<string, string>? environment = null)
    {
        string? configured = null;
        if (environment is not null) environment.TryGetValue("CLAUDE_CONFIG_DIR", out configured);
        else configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured, home) : Path.Combine(home, ".claude");
    }

    /// <summary>The advisor's model Claude's settings name for a run in <paramref name="workspace"/>, null when none does or when they turn it off.</summary>
    public static string? Model(string? workspace, string configDirectory, Cache? cache = null)
    {
        var held = cache ?? Cache.Shared;
        return Resolve(Files(workspace, configDirectory).Select(held.Entry).ToList());
    }

    /// <summary>The files' entries, in order, as one answer: the last file that names <c>advisorModel</c> decides the model, and the last that sets the
    /// disable variable decides whether it is off.</summary>
    public static string? Resolve(IReadOnlyList<Entry> entries)
    {
        Choice? choice = null; var disabled = false;
        foreach (var entry in entries)
        {
            if (entry.Model is { } model) choice = model;
            if (entry.Disabled is { } value) disabled = value;
        }
        return disabled ? null : choice?.Name;
    }

    /// <summary>What a settings file's bytes say. Anything that is not a JSON object says nothing.</summary>
    public static Entry Read(ReadOnlySpan<byte> data)
    {
        JsonElement root;
        try
        {
            var reader = new Utf8JsonReader(data);
            using var document = JsonDocument.ParseValue(ref reader);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return new(); }
        if (root.ValueKind != JsonValueKind.Object) return new();
        var entry = new Entry();
        if (root.TryGetProperty("advisorModel", out var value))
            entry = entry with { Model = value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } name && !name.Equals("off", StringComparison.OrdinalIgnoreCase) && Wire.Model(name) ? Choice.Of(name) : Choice.Off };
        if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object && env.TryGetProperty(DisableVariable, out var switched))
            entry = entry with { Disabled = Truthy(switched) };
        return entry;
    }

    /// <summary>A variable's value as Claude Code reads a switch: 1, true, yes or on.</summary>
    public static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
        JsonValueKind.String => Truthy(value.GetString()),
        _ => false,
    };
    public static bool Truthy(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>The advisor's model a request starts with, when one is known to be on: the pane's own choice (<see cref="RunSettings.Advisor"/>: a model,
    /// or <see cref="RunSettings.AdvisorOff"/>), else what Claude's settings name. Null when the pane turned it off, when the run's environment does
    /// (<paramref name="environmentDisables"/>, which no model choice overrides), or when nothing names one.</summary>
    public static string? Configured(string? pane, bool environmentDisables, Func<string?> settings)
    {
        if (pane == RunSettings.AdvisorOff || environmentDisables) return null;
        if (pane is not null) return Wire.Model(pane) ? pane : null;
        return settings();
    }

    /// <summary>What was read of each file, kept until its size or modification time changes.</summary>
    public sealed class Cache
    {
        public static readonly Cache Shared = new();
        private readonly object gate = new();
        private readonly Dictionary<string, (string Stamp, Entry Entry)> files = new(StringComparer.OrdinalIgnoreCase);

        public Entry Entry(string file)
        {
            FileInfo info;
            try { info = new FileInfo(file); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException) { return new(); }
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumBytes || (info.Attributes & FileAttributes.Directory) != 0)
            {
                lock (gate) files.Remove(file);
                return new();
            }
            var stamp = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            lock (gate) if (files.TryGetValue(file, out var held) && held.Stamp == stamp) return held.Entry;
            var entry = new Entry();
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[MaximumBytes + 1];
                var read = 0;
                while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var count and > 0) read += count;
                if (read <= MaximumBytes) entry = Read(buffer.AsSpan(0, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
            lock (gate)
            {
                if (files.Count >= 256) files.Clear();
                files[file] = (stamp, entry);
            }
            return entry;
        }
    }
}
