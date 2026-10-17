using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MightyClaude.Core;

public enum SetupProbeState { Installed, Missing, Outdated, Unverified, NeedsAttention }

/// <summary>What a probe found (M/MightyCore/SetupProbe.swift SetupProbeResult). The probe, never an exit code, decides whether an item is there.</summary>
public sealed record SetupProbeResult(SetupProbeState State, string? Version = null, string? Minimum = null)
{
    public static readonly SetupProbeResult Missing = new(SetupProbeState.Missing);
    public static SetupProbeResult Installed(string? version = null) => new(SetupProbeState.Installed, version);
    /// <summary>Counts as there: installed, or there with its version unread.</summary>
    public bool IsInstalled => State is SetupProbeState.Installed or SetupProbeState.Unverified;
    /// <summary>The text <c>--version</c> printed, when the item is there at all.</summary>
    public string? ShownVersion => State is SetupProbeState.Installed or SetupProbeState.Outdated ? Version : null;
}

/// <summary>
/// Reads whether each catalog item is on this PC, fresh on every call (M/MightyCore/SetupProbe.swift SetupProber): the PATH it is
/// given plus the folders the official installers use, with PATHEXT; <c>--version</c> with a deadline; the Claude plugin registry;
/// the skills folder. Nothing is installed or changed. The environment and the version reader are injected, so tests use fixtures.
/// </summary>
public sealed class SetupProber(IReadOnlyDictionary<string, string> environment, Func<string, CancellationToken, Task<string?>>? versionReader = null)
{
    /// <summary>Written in a skill's folder by the Mac's setup screen: a folder it placed counts only once its setup finished.</summary>
    public const string SkillInstallingMarker = ".mighty-setup", SkillDoneMarker = ".mighty-setup-ok";
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, string> variables = WindowsCommand.Variables(environment);
    private string Home => variables.GetValueOrDefault("USERPROFILE") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task<SetupProbeResult> ProbeAsync(SetupProbe probe, CancellationToken cancellation = default)
    {
        switch (probe)
        {
            case SetupProbe.Executable executable:
            {
                // Like ProviderCatalog: every copy in order, the first that says its version counts.
                var copies = Executables(executable.Name, variables);
                if (copies.Count == 0) return SetupProbeResult.Missing;
                string? version = null;
                foreach (var copy in copies)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (await (versionReader ?? ReadVersionAsync)(copy, cancellation) is { } text) { version = text; break; }
                }
                if (executable.MinimumVersion is not { } minimum) return SetupProbeResult.Installed(version);
                if (version is null) return new(SetupProbeState.Unverified, null, minimum);
                return Meets(version, minimum) ? SetupProbeResult.Installed(version) : new(SetupProbeState.Outdated, version, minimum);
            }
            case SetupProbe.ClaudePlugin plugin:
                return ToolkitProbe.UserScopePluginInstalled(Home, id => id.StartsWith(plugin.Prefix, StringComparison.Ordinal)) ? SetupProbeResult.Installed() : SetupProbeResult.Missing;
            case SetupProbe.Skill skill:
                return SkillInstalled(Path.Combine(Home, ".claude", "skills", skill.Name)) ? SetupProbeResult.Installed() : SetupProbeResult.Missing;
            default:
                // The command line tools and app bundles are the Mac's; the Windows catalog never asks for them.
                return SetupProbeResult.Missing;
        }
    }

    /// <summary>Every item that has a Windows entry (only those in <paramref name="only"/>, when given), probed one after another.</summary>
    public async Task<Dictionary<string, SetupProbeResult>> ProbeAllAsync(SetupCatalog catalog, IReadOnlySet<string>? only = null, CancellationToken cancellation = default)
    {
        var results = new Dictionary<string, SetupProbeResult>(StringComparer.Ordinal);
        foreach (var item in catalog.Items)
        {
            if (only is not null && !only.Contains(item.Id) || item.Entry(SetupPlatform.Windows) is not { } entry) continue;
            cancellation.ThrowIfCancellationRequested();
            results[item.Id] = await ProbeAsync(entry.Probe, cancellation);
        }
        return results;
    }

    /// <summary>Each copy of <paramref name="name"/> along the PATH and then in the installers' folders (<see cref="SetupScript.WellKnownFolders"/>), in order, once.</summary>
    public static IReadOnlyList<string> Executables(string name, IReadOnlyDictionary<string, string> environment)
    {
        var variables = WindowsCommand.Variables(environment);
        var folders = (variables.GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator).Take(64).Concat(SetupScript.WellKnownFolders(variables));
        return WindowsCommand.Copies(name, folders, variables.GetValueOrDefault("PATHEXT"));
    }

    /// <summary>A skill counts when its SKILL.md is there; a folder the setup screen placed counts only once its setup finished.</summary>
    public static bool SkillInstalled(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "SKILL.md"))) return false;
        if (!File.Exists(Path.Combine(folder, SkillInstallingMarker))) return true;
        return File.Exists(Path.Combine(folder, SkillDoneMarker));
    }

    /// <summary><c>--version</c>'s output, trimmed and at most 160 characters, or null when it did not answer in time or failed.</summary>
    private async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellation)
    {
        var quiet = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase) { ["NO_COLOR"] = "1", ["DISABLE_AUTOUPDATER"] = "1", ["NO_UPDATE_NOTIFIER"] = "1" };
        try
        {
            // The system folder: no repository and no user-writable folder a shim's bare node could be planted in.
            var neutral = OperatingSystem.IsWindows() && Environment.SystemDirectory is { Length: > 0 } system ? system : Path.GetTempPath();
            var result = await new CliRunner(outputCapBytes: 16_384).RunAsync(executable, ["--version"], VersionTimeout, cancellation, quiet, neutral);
            var text = result.Output.Trim();
            return result.ExitCode == 0 && !result.TimedOut && text.Length > 0 ? text[..Math.Min(text.Length, 160)] : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>The first <c>1.2.3</c> (or <c>1.2</c>, <c>1</c>) in a version line.</summary>
    public static int[]? VersionNumbers(string text)
    {
        var match = Regex.Match(text, @"[0-9]+(\.[0-9]+){0,2}");
        if (!match.Success) return null;
        var numbers = match.Value.Split('.').Select(part => int.TryParse(part, out var value) ? value : -1).Where(value => value >= 0).ToArray();
        return numbers.Length == 0 ? null : numbers;
    }

    public static bool IsAtLeast(IReadOnlyList<int> found, IReadOnlyList<int> required)
    {
        var width = Math.Max(found.Count, required.Count);
        for (var index = 0; index < width; index++)
        {
            var a = index < found.Count ? found[index] : 0; var b = index < required.Count ? required[index] : 0;
            if (a != b) return a > b;
        }
        return true;
    }

    /// <summary>Whether <paramref name="version"/> reaches <paramref name="minimum"/>. A prerelease (<c>2.1.300-beta</c>) never does, as ProviderCatalog.SupportsMods reads it.</summary>
    public static bool Meets(string version, string minimum)
    {
        if (Regex.Match(version, @"[0-9]+\.[0-9]+\.[0-9]+") is { Success: true } triple && triple.Index + triple.Length < version.Length && version[triple.Index + triple.Length] == '-') return false;
        return VersionNumbers(version) is { } found && VersionNumbers(minimum) is { } required && IsAtLeast(found, required);
    }
}

/// <summary>What a row shows (M/MightyCore/SetupProbe.swift SetupRowState).</summary>
public enum SetupRowState { Checking, Installed, Missing, Outdated, Attention, Installing, Failed }

public static class SetupRows
{
    /// <summary>
    /// <paramref name="inRun"/>: the item is part of the install that is running, or was part of the last one. <paramref name="runFinished"/>:
    /// that install's script has ended. An item the probe still does not see once the script ended failed.
    /// </summary>
    public static SetupRowState Derive(SetupProbeResult? result, bool inRun, bool runFinished) => result?.State switch
    {
        null => SetupRowState.Checking,
        SetupProbeState.Installed or SetupProbeState.Unverified => SetupRowState.Installed,
        SetupProbeState.NeedsAttention => SetupRowState.Attention,
        SetupProbeState.Outdated => inRun && !runFinished ? SetupRowState.Installing : SetupRowState.Outdated,
        _ => !inRun ? SetupRowState.Missing : runFinished ? SetupRowState.Failed : SetupRowState.Installing,
    };

    /// <summary>Whether the row's box may be ticked: what is missing or failed, and an out-of-date tool that is not an agent CLI (those get the CLI update instead).</summary>
    public static bool IsSelectable(SetupRowState state, SetupItem item) =>
        item.Entry(SetupPlatform.Windows)?.Install.RunsInTerminal == true && state switch
        {
            SetupRowState.Missing or SetupRowState.Failed => true,
            SetupRowState.Outdated => item.Provider is null,
            _ => false,
        };

    /// <summary>The state word's locale key.</summary>
    public static string StatusKey(SetupRowState state) => state switch
    {
        SetupRowState.Checking => "setup.status.checking", SetupRowState.Installed => "setup.status.installed", SetupRowState.Missing => "setup.status.missing",
        SetupRowState.Outdated => "setup.status.outdated", SetupRowState.Attention => "setup.status.attention", SetupRowState.Installing => "setup.status.installing",
        _ => "setup.status.failed",
    };
}

/// <summary>Whether the screen opens by itself at launch, and what it ticks the first time (M/MightyCore/SetupProbe.swift SetupFirstRun).</summary>
public static class SetupFirstRun
{
    /// <summary>
    /// True when no agent CLI can run, or a required item (Git) is not ready, unless the user chose never to see it again.
    /// Results still missing count as not known yet.
    /// </summary>
    public static bool ShouldPresent(SetupCatalog catalog, IReadOnlyDictionary<string, SetupProbeResult> results, bool neverShow)
    {
        if (neverShow) return false;
        var items = catalog.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null).ToList();
        if (!items.All(item => results.ContainsKey(item.Id))) return false;
        var agents = items.Where(item => item.Group == SetupGroup.Agent).ToList();
        if (agents.Count > 0 && !agents.Any(item => results[item.Id].IsInstalled)) return true;
        return items.Any(item => item.Required && !results[item.Id].IsInstalled);
    }

    /// <summary>The welcome screen's button shows while this holds (the same rule, without "never show").</summary>
    public static bool NeedsAttention(SetupCatalog catalog, IReadOnlyDictionary<string, SetupProbeResult> results) => ShouldPresent(catalog, results, false);

    /// <summary>Every required item that is missing, and Claude Code when no agent CLI is there at all.</summary>
    public static IReadOnlySet<string> Preselection(SetupCatalog catalog, IReadOnlyDictionary<string, SetupProbeResult> results)
    {
        var picked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in catalog.Items)
            if (item.Required && results.GetValueOrDefault(item.Id)?.State == SetupProbeState.Missing && item.Entry(SetupPlatform.Windows)?.Install.RunsInTerminal == true) picked.Add(item.Id);
        var agents = catalog.Items.Where(item => item.Group == SetupGroup.Agent && item.Entry(SetupPlatform.Windows) is not null).ToList();
        if (!agents.Any(item => results.GetValueOrDefault(item.Id)?.IsInstalled == true) && agents.FirstOrDefault() is { } first && results.GetValueOrDefault(first.Id)?.State == SetupProbeState.Missing)
            picked.Add(first.Id);
        return picked;
    }

    public const string RegistryKey = @"Software\MightyClaudeNative", NeverShowValue = "setup.neverShow";

    /// <summary>"다시 보지 않기", kept as a REG_DWORD under <c>HKCU\Software\MightyClaudeNative</c> (the Mac's defaults key <c>setup.neverShow</c>).</summary>
    public static bool NeverShow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { using var key = Registry.CurrentUser.OpenSubKey(RegistryKey); return key?.GetValue(NeverShowValue) is int value && value != 0; }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static void SetNeverShow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { using var key = Registry.CurrentUser.CreateSubKey(RegistryKey); key.SetValue(NeverShowValue, 1, RegistryValueKind.DWord); }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Whether the app's runtime disagrees with what the probe just saw, so the runtime (and every pane blocked for a missing CLI) must be read again.</summary>
public static class SetupRuntimeSync
{
    public static bool IsStale(IEnumerable<ProviderRuntime> runtime, SetupCatalog catalog, IReadOnlyDictionary<string, SetupProbeResult> results)
    {
        var providers = runtime.ToList();
        foreach (var item in catalog.Items)
        {
            if (item.Provider is not { } provider || !results.TryGetValue(item.Id, out var result)) continue;
            if (result.ShownVersion != providers.FirstOrDefault(p => p.Id == provider)?.Version) return true;
        }
        return false;
    }
}
