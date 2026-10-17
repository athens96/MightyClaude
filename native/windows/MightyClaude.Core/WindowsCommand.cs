namespace MightyClaude.Core;

/// <summary>
/// How a program named by its bare name (<c>claude</c>, <c>npm</c>, <c>git</c>, <c>winget</c>) is started on Windows.
/// CreateProcessW is handed the program's full path as its application name, and with one it neither looks along the PATH
/// nor runs a <c>.cmd</c> shim, as npm's <c>npm.cmd</c> and <c>claude.cmd</c> are: the name is found along the PATH with
/// each PATHEXT extension first, and a batch file is run by <c>cmd.exe /d /s /c</c> with every argument quoted.
/// </summary>
public static class WindowsCommand
{
    /// <summary>What PATHEXT says when it says nothing (Windows' own default, the script hosts left out).</summary>
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// The full path <paramref name="name"/> runs as: a rooted path as it is, when it is there; a bare name along
    /// <paramref name="path"/> (64 folders at most, rooted ones only) with each extension of <paramref name="pathExt"/>,
    /// the name as written first when it already ends in one of them. Null when nothing is found.
    /// </summary>
    public static string? Find(string name, string? path, string? pathExt, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (name.Length == 0 || name.Contains('\0')) return null;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => extension.StartsWith('.') && extension.Length > 1).ToArray();
        var named = extensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> Candidates(string stem) => named ? [stem, .. extensions.Select(extension => stem + extension)] : extensions.Select(extension => stem + extension);
        if (Path.IsPathRooted(name)) return exists(name) ? name : Candidates(name).FirstOrDefault(exists);
        if (name.IndexOfAny(['/', '\\', ':']) >= 0) return null;
        foreach (var folder in (path ?? "").Split(Path.PathSeparator).Take(64))
        {
            var directory = folder.Trim().Trim('"');
            if (directory.Length == 0 || directory.Contains('\0') || !Path.IsPathRooted(directory)) continue;
            if (Candidates(Path.Combine(directory, name)).FirstOrDefault(exists) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Every copy of <paramref name="name"/> in <paramref name="folders"/>, in order and once each (the first PATHEXT extension
    /// found in a folder): the setup screen asks each in turn for its version, as the Mac does along its PATH.
    /// </summary>
    public static IReadOnlyList<string> Copies(string name, IEnumerable<string> folders, string? pathExt, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => extension.StartsWith('.') && extension.Length > 1).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var copies = new List<string>();
        foreach (var folder in folders.Take(80))
        {
            var directory = folder.Trim().Trim('"');
            if (directory.Length == 0 || directory.Contains('\0') || !Path.IsPathRooted(directory)) continue;
            if (extensions.Select(extension => Path.Combine(directory, name + extension)).FirstOrDefault(exists) is { } found && seen.Add(found)) copies.Add(found);
        }
        return copies;
    }

    /// <summary>An environment read as Windows reads it, names in any case (<c>Path</c> is <c>PATH</c>).</summary>
    public static Dictionary<string, string> Variables(IEnumerable<KeyValuePair<string, string>> environment)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment) variables[key] = value;
        return variables;
    }

    /// <summary>A batch file, which only cmd.exe can run.</summary>
    public static bool IsBatch(string path) => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// cmd.exe by the system's own path, never one found along the PATH: on Windows the system folder Windows reports, which no
    /// environment variable of the user's can move; elsewhere (the checks) <c>SystemRoot</c> as given.
    /// </summary>
    public static string CommandInterpreter(IReadOnlyDictionary<string, string>? environment = null)
    {
        if (OperatingSystem.IsWindows() && Environment.GetFolderPath(Environment.SpecialFolder.System) is { Length: > 0 } system) return Path.Combine(system, "cmd.exe");
        var root = environment?.GetValueOrDefault("SystemRoot") is { Length: > 0 } given ? given : @"C:\Windows";
        return Path.Combine(root, "System32", "cmd.exe");
    }

    /// <summary>
    /// The text <c>cmd.exe /d /s /c "…"</c> runs for <paramref name="batch"/> with <paramref name="arguments"/>. /s takes the
    /// outer quotes away and runs the rest as written, so the batch file and every argument stand in double quotes of
    /// their own, where cmd reads &amp; | &lt; &gt; ^ ( ) as plain characters; a backslash run before a closing quote is
    /// doubled, as the program the batch hands its arguments to splits them. What cmd would still expand or end a
    /// quoted argument with (<c>"</c>, <c>%</c>, <c>!</c>, a line break) is refused: such an argument cannot be passed through a batch file safely.
    /// </summary>
    public static string BatchCommandLine(string batch, IReadOnlyList<string> arguments)
    {
        static string Quote(string value)
        {
            if (value.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) >= 0) throw new ArgumentException(Locale.Get("windows.process.unsafeBatchArgument"), nameof(arguments));
            var trailing = value.Length - value.TrimEnd('\\').Length;
            return "\"" + value + new string('\\', trailing) + "\"";
        }
        return string.Join(" ", new[] { batch }.Concat(arguments).Select(Quote));
    }
}
