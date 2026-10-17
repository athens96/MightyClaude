using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>What <c>show rule</c> says: <see cref="Present"/> means only that a rule by <see cref="WindowsFirewallRule.RuleName"/>
/// exists, not that its ports or program are the ones the app would write now. The last applied values come back in
/// <see cref="FirewallChangeResult.Applied"/>.</summary>
public enum FirewallRuleState { Unknown, Present, Absent }
public enum FirewallChange { Done, Cancelled, Refused, Failed }
/// <summary>The ports (<c>localport=</c> spelling) and the exe a <see cref="FirewallChange.Done"/> add or update wrote.</summary>
public sealed record FirewallRuleSpec(string Ports, string Program);
/// <param name="Detail">Refused: <c>noPorts</c>, <c>badPorts</c>, <c>badProgram</c> or <c>noSystemDirectory</c>. Failed: netsh's
/// exit code, <c>timeout</c>, <c>queryFailed</c> (the rule could not be read before an add) or the launch error.</param>
/// <param name="Applied">After a done add or update, the ports and program now in the rule; null otherwise.</param>
public sealed record FirewallChangeResult(FirewallChange Kind, string? Detail = null, FirewallRuleSpec? Applied = null);

/// <summary>
/// The one Windows Firewall rule the app keeps (spec 2026-10-17-windows-catch-up-design.md, "Windows Firewall"): inbound TCP on the
/// app's own ports, only from and to Tailscale's ranges, only for the app's own executable. This is the one place the app asks Windows
/// to elevate a command, and the command is fixed: <c>%SystemRoot%\System32\netsh.exe advfirewall firewall add|set|delete rule …</c>,
/// built from these constants, the validated exe path and the validated ports:
/// <c>name="Mighty Claude (Tailscale)" dir=in action=allow protocol=TCP localport=&lt;ports&gt; localip=&lt;ranges&gt;
/// remoteip=&lt;ranges&gt; program="&lt;exe&gt;" profile=any</c> (<c>set rule … new … enable=yes</c> when the rule is there).
/// Reading whether the rule exists needs no elevation.
/// </summary>
public static partial class WindowsFirewallRule
{
    public const string RuleName = "Mighty Claude (Tailscale)";
    public const string RemoteRanges = "100.64.0.0/10,fd7a:115c:a1e0::/48";
    /// <summary>The rule's <c>localip=</c>: the same ranges, a backstop for <c>profile=any</c> so no other adapter's address is opened.</summary>
    public const string LocalRanges = "100.64.0.0/10,fd7a:115c:a1e0::/48";
    /// <summary>The only exe the rule may name, so a development run under dotnet.exe never opens ports for every .NET program.</summary>
    public const string ProgramFileName = "MightyClaude.exe";
    public const int MaxPorts = 16;
    public const string ElevationVerb = "runas";
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    public const int ElevationCancelled = 1223;

    /// <summary>Windows' system folder as Windows reports it; null off Windows or when it reads as no absolute drive path.</summary>
    public static string? SystemDirectory => OperatingSystem.IsWindows() && ValidSystemDirectory(Environment.SystemDirectory) ? Environment.SystemDirectory : null;

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;
    public static bool ValidSystemDirectory(string? path) => path is { Length: > 3 and <= 260 } && PlainCharacters(path) && SystemDirectoryPattern().IsMatch(path);

    /// <summary><paramref name="path"/> when it is a drive-letter absolute path to <see cref="ProgramFileName"/> with no quote, %,
    /// control or format character, whitespace other than a space, '/', second ':' or '.'/'..' segment; else null.</summary>
    public static string? ValidProgram(string? path) =>
        path is { Length: > 7 and <= 1024 } && PlainCharacters(path) && ProgramPattern().IsMatch(path)
        && !path.Split('\\').Skip(1).Any(segment => segment is "." or "..")
        && string.Equals(path[(path.LastIndexOf('\\') + 1)..], ProgramFileName, StringComparison.OrdinalIgnoreCase) ? path : null;

    /// <summary>No control character (C0, DEL, C1), no format character (bidi overrides, BOM, zero-width), no whitespace but ' '.</summary>
    private static bool PlainCharacters(string text) => text.All(c =>
        !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format && (c == ' ' || !char.IsWhiteSpace(c)));

    /// <summary>The ports for <c>localport=</c>: each 1024-65535, sorted, each once, at least one, at most <see cref="MaxPorts"/>; else null.</summary>
    public static string? PortList(IEnumerable<int> ports)
    {
        var list = ports.Distinct().Order().ToArray();
        return list.Length is > 0 and <= MaxPorts && list.All(ValidPort) ? string.Join(",", list.Select(port => port.ToString(CultureInfo.InvariantCulture))) : null;
    }

    private static string[] RuleBody(string ports, string program) =>
        ["dir=in", "action=allow", "protocol=TCP", "localport=" + ports, "localip=" + LocalRanges, "remoteip=" + RemoteRanges, "program=" + program, "profile=any"];

    /// <summary>A new rule: <c>add rule name=… dir=in action=allow protocol=TCP localport=… localip=… remoteip=… program=… profile=any</c>.</summary>
    public static IReadOnlyList<string>? AddArguments(string program, IEnumerable<int> ports) =>
        ValidProgram(program) is { } valid && PortList(ports) is { } list ? ["advfirewall", "firewall", "add", "rule", "name=" + RuleName, .. RuleBody(list, valid)] : null;

    /// <summary>The rule already there rewritten in place (no second rule by the name): <c>set rule name=… new &lt;the add rule's body&gt; enable=yes</c>.</summary>
    public static IReadOnlyList<string>? SetArguments(string program, IEnumerable<int> ports) =>
        ValidProgram(program) is { } valid && PortList(ports) is { } list ? ["advfirewall", "firewall", "set", "rule", "name=" + RuleName, "new", .. RuleBody(list, valid), "enable=yes"] : null;

    public static IReadOnlyList<string> DeleteArguments() => ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName];
    public static IReadOnlyList<string> ShowArguments() => ["advfirewall", "firewall", "show", "rule", "name=" + RuleName];

    /// <summary>The rule for exactly <paramref name="ports"/>: the add argv, or the delete argv when the set is empty; null when the path or a port is refused.
    /// The runner's <see cref="WindowsFirewallRuleRunner.ApplyAsync"/> uses <see cref="SetArguments"/> instead when the rule is already there.</summary>
    public static IReadOnlyList<string>? Arguments(IReadOnlyCollection<int> ports, string program) => ports.Count == 0 ? DeleteArguments() : AddArguments(program, ports);

    private static readonly string[] AlwaysQuoted = ["name=", "program="];

    /// <summary>netsh's own spelling: <c>key="value"</c> for the rule name and the program path every time and for any value with a
    /// space, the token as it is otherwise. Values never hold a quote (validated).</summary>
    public static string CommandLine(IReadOnlyList<string> arguments) => string.Join(" ", arguments.Select(argument =>
    {
        var equals = argument.IndexOf('=');
        if (!argument.Contains(' ') && !AlwaysQuoted.Any(key => argument.StartsWith(key, StringComparison.Ordinal))) return argument;
        return equals > 0 ? argument[..(equals + 1)] + "\"" + argument[(equals + 1)..] + "\"" : "\"" + argument + "\"";
    }));

    private static string Netsh(string systemDirectory) => systemDirectory.TrimEnd('\\') + "\\netsh.exe";

    /// <summary>Whether a rule by the name exists: <c>show rule</c>, unelevated and hidden; exit 0 means it is there, 1 that it is not.</summary>
    public static ProcessStartInfo QueryStartInfo(string systemDirectory) => new(Netsh(systemDirectory), CommandLine(ShowArguments()))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = systemDirectory,
    };

    /// <summary>A change: netsh from the system folder, elevated through Windows' own UAC prompt, its window hidden.</summary>
    public static ProcessStartInfo ElevatedStartInfo(string systemDirectory, IReadOnlyList<string> arguments) => new(Netsh(systemDirectory), CommandLine(arguments))
    {
        UseShellExecute = true, Verb = ElevationVerb, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = systemDirectory,
    };

    [GeneratedRegex(@"\A[A-Za-z]:\\(?:[^\\/:*?""<>|%\x00-\x1f\x7f]+\\)*[^\\/:*?""<>|%\x00-\x1f\x7f]+\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramPattern();
    [GeneratedRegex(@"\A[A-Za-z]:\\[^""%\x00-\x1f\x7f]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SystemDirectoryPattern();
}

/// <summary>
/// Runs the rule's commands. The app builds it with <see cref="ForThisApp"/>; the checks and the GUI smoke give it a recorder that
/// starts nothing, so no development run ever touches this PC's firewall. A cancelled caller token is rethrown, never turned into a result.
/// </summary>
public sealed class WindowsFirewallRuleRunner(Func<ProcessStartInfo, CancellationToken, Task<int>> run, string? systemDirectory, string? program)
{
    /// <summary>How long a started netsh may take before the runner stops waiting (the unelevated query is killed).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static WindowsFirewallRuleRunner ForThisApp() => new(RunAsync, WindowsFirewallRule.SystemDirectory, Environment.ProcessPath);

    /// <summary>Whether a rule by <see cref="WindowsFirewallRule.RuleName"/> exists (not whether its ports are current).</summary>
    public async Task<FirewallRuleState> QueryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (systemDirectory is null) return FirewallRuleState.Unknown;
        try
        {
            return await run(WindowsFirewallRule.QueryStartInfo(systemDirectory), token) switch { 0 => FirewallRuleState.Present, 1 => FirewallRuleState.Absent, _ => FirewallRuleState.Unknown };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException or TimeoutException) { return FirewallRuleState.Unknown; }
    }

    /// <summary>The rule for exactly <paramref name="ports"/>, as one elevated netsh: <c>set rule</c> when a rule by the name is there
    /// (so rules never pile up and a dropped port closes), <c>add rule</c> when none is, nothing when it cannot be read.</summary>
    public async Task<FirewallChangeResult> AddAsync(IEnumerable<int> ports, CancellationToken token)
    {
        var list = ports.ToArray();
        if (list.Length == 0) return new(FirewallChange.Refused, "noPorts");
        if (WindowsFirewallRule.PortList(list) is not { } portList) return new(FirewallChange.Refused, "badPorts");
        if (program is null || WindowsFirewallRule.ValidProgram(program) is null) return new(FirewallChange.Refused, "badProgram");
        if (SystemDirectoryRefused()) return new(FirewallChange.Refused, "noSystemDirectory");
        var arguments = await QueryAsync(token) switch
        {
            FirewallRuleState.Present => WindowsFirewallRule.SetArguments(program, list),
            FirewallRuleState.Absent => WindowsFirewallRule.AddArguments(program, list),
            _ => null,
        };
        if (arguments is null) return new(FirewallChange.Failed, "queryFailed");
        var result = await Elevated(arguments, token);
        return result.Kind == FirewallChange.Done ? result with { Applied = new(portList, program) } : result;
    }

    public Task<FirewallChangeResult> RemoveAsync(CancellationToken token) => Elevated(WindowsFirewallRule.DeleteArguments(), token);

    /// <summary>The rule for exactly <paramref name="ports"/>: added or updated in place for a set, removed for none (part 3 passes the union of its ports and the phone's).</summary>
    public Task<FirewallChangeResult> ApplyAsync(IReadOnlyCollection<int> ports, CancellationToken token) => ports.Count == 0 ? RemoveAsync(token) : AddAsync(ports, token);

    /// <summary>No folder, or on Windows a folder other than the one Windows reports: netsh is never started from anywhere else.</summary>
    private bool SystemDirectoryRefused() => systemDirectory is null
        || OperatingSystem.IsWindows() && !string.Equals(systemDirectory.TrimEnd('\\'), Environment.SystemDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private async Task<FirewallChangeResult> Elevated(IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (SystemDirectoryRefused()) return new(FirewallChange.Refused, "noSystemDirectory");
        try
        {
            var code = await run(WindowsFirewallRule.ElevatedStartInfo(systemDirectory!, arguments), token);
            return code == 0 ? new(FirewallChange.Done) : new(FirewallChange.Failed, code.ToString(CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == WindowsFirewallRule.ElevationCancelled) { return new(FirewallChange.Cancelled); }
        catch (TimeoutException) { return new(FirewallChange.Failed, "timeout"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException) { return new(FirewallChange.Failed, ex.Message); }
    }

    /// <summary>Starts the process off the caller's thread (an elevated start waits on the UAC prompt) and waits up to
    /// <see cref="Limit"/> for its exit code. Past that it throws <see cref="TimeoutException"/>; the caller's own cancel throws as it
    /// is. Either way an unelevated query is killed; an elevated netsh cannot be killed from here and is left to finish.</summary>
    private static async Task<int> RunAsync(ProcessStartInfo info, CancellationToken token)
    {
        using var process = await Task.Run(() => Process.Start(info), token) ?? throw new InvalidOperationException("netsh did not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(Limit);
        if (info.RedirectStandardOutput) { _ = process.StandardOutput.ReadToEndAsync(limit.Token); _ = process.StandardError.ReadToEndAsync(limit.Token); }
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            if (!info.UseShellExecute) { try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { } }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("netsh did not finish in " + Limit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        }
        return process.ExitCode;
    }
}
