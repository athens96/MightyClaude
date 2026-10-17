using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public enum FirewallRuleState { Unknown, Present, Absent }
public enum FirewallChange { Done, Cancelled, Refused, Failed }
/// <param name="Detail">Refused: <c>noPorts</c>, <c>badProgram</c> or <c>noSystemDirectory</c>. Failed: netsh's exit code or the launch error.</param>
public sealed record FirewallChangeResult(FirewallChange Kind, string? Detail = null);

/// <summary>
/// The one Windows Firewall rule the app keeps (spec 2026-10-17-windows-catch-up-design.md, "Windows Firewall"): inbound TCP on the
/// app's own ports, only from Tailscale's ranges, only for the app's own executable. This is the one place the app asks Windows to
/// elevate a command, and the command is fixed: <c>%SystemRoot%\System32\netsh.exe advfirewall firewall add|delete rule …</c>, built
/// from these constants, the validated exe path and the validated ports. Reading whether the rule exists needs no elevation.
/// </summary>
public static partial class WindowsFirewallRule
{
    public const string RuleName = "Mighty Claude (Tailscale)";
    public const string RemoteRanges = "100.64.0.0/10,fd7a:115c:a1e0::/48";
    public const string ElevationVerb = "runas";
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    public const int ElevationCancelled = 1223;

    /// <summary>Windows' system folder as Windows reports it; null off Windows or when it reads as no absolute drive path.</summary>
    public static string? SystemDirectory => OperatingSystem.IsWindows() && ValidSystemDirectory(Environment.SystemDirectory) ? Environment.SystemDirectory : null;

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;
    public static bool ValidSystemDirectory(string? path) => path is { Length: > 3 and <= 260 } && SystemDirectoryPattern().IsMatch(path);

    /// <summary><paramref name="path"/> when it is a drive-letter absolute path to an .exe with no quote, %, control character, '/', second ':' or '.'/'..' segment; else null.</summary>
    public static string? ValidProgram(string? path) =>
        path is { Length: > 7 and <= 1024 } && ProgramPattern().IsMatch(path) && !path.Split('\\').Skip(1).Any(segment => segment is "." or "..") ? path : null;

    /// <summary>The ports for <c>localport=</c>: each 1024-65535, sorted, each once, at least one, at most 16; else null.</summary>
    public static string? PortList(IEnumerable<int> ports)
    {
        var list = ports.Distinct().Order().ToArray();
        return list.Length is > 0 and <= 16 && list.All(ValidPort) ? string.Join(",", list.Select(port => port.ToString(CultureInfo.InvariantCulture))) : null;
    }

    public static IReadOnlyList<string>? AddArguments(string program, IEnumerable<int> ports) =>
        ValidProgram(program) is { } valid && PortList(ports) is { } list
            ? ["advfirewall", "firewall", "add", "rule", "name=" + RuleName, "dir=in", "action=allow", "protocol=TCP", "localport=" + list, "remoteip=" + RemoteRanges, "program=" + valid, "profile=any"]
            : null;

    public static IReadOnlyList<string> DeleteArguments() => ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName];
    public static IReadOnlyList<string> ShowArguments() => ["advfirewall", "firewall", "show", "rule", "name=" + RuleName];

    /// <summary>The rule for exactly <paramref name="ports"/>: the add argv, or the delete argv when the set is empty; null when the path or a port is refused.</summary>
    public static IReadOnlyList<string>? Arguments(IReadOnlyCollection<int> ports, string program) => ports.Count == 0 ? DeleteArguments() : AddArguments(program, ports);

    /// <summary>netsh's own spelling: <c>key="value"</c> when the value holds a space, the token as it is otherwise. Values never hold a quote (validated).</summary>
    public static string CommandLine(IReadOnlyList<string> arguments) => string.Join(" ", arguments.Select(argument =>
    {
        if (!argument.Contains(' ')) return argument;
        var equals = argument.IndexOf('=');
        return equals > 0 ? argument[..(equals + 1)] + "\"" + argument[(equals + 1)..] + "\"" : "\"" + argument + "\"";
    }));

    private static string Netsh(string systemDirectory) => systemDirectory.TrimEnd('\\') + "\\netsh.exe";

    /// <summary>Whether the rule exists: <c>show rule</c>, unelevated and hidden; exit 0 means it is there, 1 that it is not.</summary>
    public static ProcessStartInfo QueryStartInfo(string systemDirectory) => new(Netsh(systemDirectory), CommandLine(ShowArguments()))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = systemDirectory,
    };

    /// <summary>A change: netsh from the system folder, elevated through Windows' own UAC prompt, its window hidden.</summary>
    public static ProcessStartInfo ElevatedStartInfo(string systemDirectory, IReadOnlyList<string> arguments) => new(Netsh(systemDirectory), CommandLine(arguments))
    {
        UseShellExecute = true, Verb = ElevationVerb, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = systemDirectory,
    };

    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^\\/:*?""<>|%\x00-\x1f]+\\)*[^\\/:*?""<>|%\x00-\x1f]+\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramPattern();
    [GeneratedRegex(@"^[A-Za-z]:\\[^""%\x00-\x1f]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SystemDirectoryPattern();
}

/// <summary>
/// Runs the rule's commands. The app builds it with <see cref="ForThisApp"/>; the checks and the GUI smoke give it a recorder that
/// starts nothing, so no development run ever touches this PC's firewall.
/// </summary>
public sealed class WindowsFirewallRuleRunner(Func<ProcessStartInfo, CancellationToken, Task<int>> run, string? systemDirectory, string? program)
{
    public static WindowsFirewallRuleRunner ForThisApp() => new(RunAsync, WindowsFirewallRule.SystemDirectory, Environment.ProcessPath);

    public async Task<FirewallRuleState> QueryAsync(CancellationToken token)
    {
        if (systemDirectory is null) return FirewallRuleState.Unknown;
        try
        {
            return await run(WindowsFirewallRule.QueryStartInfo(systemDirectory), token) switch { 0 => FirewallRuleState.Present, 1 => FirewallRuleState.Absent, _ => FirewallRuleState.Unknown };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException) { return FirewallRuleState.Unknown; }
    }

    public Task<FirewallChangeResult> AddAsync(IEnumerable<int> ports, CancellationToken token)
    {
        var list = ports.ToArray();
        if (WindowsFirewallRule.PortList(list) is null) return Task.FromResult(new FirewallChangeResult(FirewallChange.Refused, "noPorts"));
        if (program is null || WindowsFirewallRule.ValidProgram(program) is null) return Task.FromResult(new FirewallChangeResult(FirewallChange.Refused, "badProgram"));
        return Elevated(WindowsFirewallRule.AddArguments(program, list)!, token);
    }

    public Task<FirewallChangeResult> RemoveAsync(CancellationToken token) => Elevated(WindowsFirewallRule.DeleteArguments(), token);

    /// <summary>The rule for exactly <paramref name="ports"/>: added for a set, removed for none (part 3 passes the union of its ports and the phone's).</summary>
    public Task<FirewallChangeResult> ApplyAsync(IReadOnlyCollection<int> ports, CancellationToken token) => ports.Count == 0 ? RemoveAsync(token) : AddAsync(ports, token);

    private async Task<FirewallChangeResult> Elevated(IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (systemDirectory is null) return new(FirewallChange.Refused, "noSystemDirectory");
        try
        {
            var code = await run(WindowsFirewallRule.ElevatedStartInfo(systemDirectory, arguments), token);
            return code == 0 ? new(FirewallChange.Done) : new(FirewallChange.Failed, code.ToString(CultureInfo.InvariantCulture));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == WindowsFirewallRule.ElevationCancelled) { return new(FirewallChange.Cancelled); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException) { return new(FirewallChange.Failed, ex.Message); }
    }

    /// <summary>Starts the process off the caller's thread (an elevated start waits on the UAC prompt) and waits up to 30 s for its exit code.</summary>
    private static async Task<int> RunAsync(ProcessStartInfo info, CancellationToken token)
    {
        using var process = await Task.Run(() => Process.Start(info), token) ?? throw new InvalidOperationException("netsh did not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(30));
        if (info.RedirectStandardOutput) { _ = process.StandardOutput.ReadToEndAsync(limit.Token); _ = process.StandardError.ReadToEndAsync(limit.Token); }
        await process.WaitForExitAsync(limit.Token);
        return process.ExitCode;
    }
}
