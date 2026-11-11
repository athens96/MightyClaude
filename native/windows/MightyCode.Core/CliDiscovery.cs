namespace MightyCode.Core;

/// <summary>
/// Where Windows finds an agent CLI whose installer leaves more than one <c>&lt;name&gt;.exe</c> along the PATH, and what of its
/// <c>--version</c> is its version: ProviderCatalog's discovery, the setup probe (<see cref="SetupProber"/>) and the CLI update's
/// reading (<see cref="CliUpdateService"/>) agree on both, so a setup row and the runtime show the same version.
/// </summary>
public static class CliDiscovery
{
    /// <summary>
    /// Where Hermes Agent's own installer (install.ps1) puts <c>hermes.exe</c>, or the <c>hermes.cmd</c> it writes when it cannot make
    /// the .exe: <c>$HERMES_HOME\bin</c> when HERMES_HOME is a whole path, else <c>%LOCALAPPDATA%\hermes\bin</c>. It adds that folder to
    /// the user PATH for new sessions only, so the folder is looked in as well, as Antigravity's is (ProviderCatalog.AntigravityInstallFolder).
    /// Null when the environment names neither folder.
    /// </summary>
    public static string? HermesInstallFolder(IReadOnlyDictionary<string, string> environment)
    {
        var variables = WindowsCommand.Variables(environment);
        if (Whole(variables.GetValueOrDefault("HERMES_HOME")) is { } home) return Path.Combine(home, "bin");
        return Whole(variables.GetValueOrDefault("LOCALAPPDATA")) is { } local ? Path.Combine(local, "hermes", "bin") : null;
    }

    // A drive path (C:\…) counts as whole on any system, as SetupScript.WellKnownFolders reads it, so the folders read the same where the checks run.
    private static string? Whole(string? path) => path is { Length: > 0 } && !path.Contains('\0') && (Path.IsPathFullyQualified(path) || path is [_, ':', '\\', ..]) ? path : null;

    /// <summary>
    /// The files tried for <paramref name="provider"/>, in order, along <paramref name="directories"/> (the PATH, then the fixed folders).
    /// Off Windows each folder's bare name. On Windows, folder by folder as the PATH orders them, as a terminal would find the command:
    /// opencode's <c>opencode.exe</c>, then npm's copy of OpenCode 2's own binary (<c>node_modules\@opencode\cli\bin\opencode.exe</c>, which the
    /// npm shim only forwards to, so no cmd.exe stands between), then the shim <c>opencode.cmd</c> itself (an npm OpenCode 1, <c>opencode-ai</c>,
    /// has a shim and no such binary); Hermes's <c>hermes.exe</c>, then <c>hermes.cmd</c>, its installer's folder last
    /// (<see cref="HermesInstallFolder"/>); pi's npm shim <c>pi.cmd</c>; any other CLI its <c>&lt;name&gt;.exe</c>.
    /// </summary>
    public static IReadOnlyList<string> Files(string provider, IEnumerable<string> directories, bool windows, IReadOnlyDictionary<string, string> environment)
    {
        var name = ProviderCatalog.Executable(provider);
        var folders = directories.Where(folder => folder.Length > 0 && !folder.Contains('\0')).ToList();
        if (windows && provider == "hermes" && HermesInstallFolder(environment) is { } hermes) folders.Add(hermes);
        string[] Names(string folder) => !windows ? [Path.Combine(folder, name)] : provider switch
        {
            "opencode" => [Path.Combine(folder, "opencode.exe"), Path.Combine(folder, "node_modules", "@opencode", "cli", "bin", "opencode.exe"), Path.Combine(folder, "opencode.cmd")],
            "hermes" => [Path.Combine(folder, "hermes.exe"), Path.Combine(folder, "hermes.cmd")],
            "pi" => [Path.Combine(folder, "pi.cmd")],
            _ => [Path.Combine(folder, name + ".exe")],
        };
        return folders.SelectMany(Names).Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The version a CLI's <c>--version</c> printed, trimmed: <c>hermes --version</c> prints its version line first ("Hermes Agent v0.23.1
    /// (2026.9.24) · stable · …"), then its install folder, Python and update status, so for Hermes only that first line is the version
    /// (M/MightyCore/HermesSupport.swift HermesAgent.versionLine). <paramref name="program"/> is the program's name (<c>hermes</c>).
    /// </summary>
    public static string VersionText(string program, string? output)
    {
        var text = (output ?? "").Trim();
        return program == "hermes" ? text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "" : text;
    }
}
