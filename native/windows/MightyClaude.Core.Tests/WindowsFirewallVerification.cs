using System.ComponentModel;
using System.Diagnostics;
using MightyClaude.Core;

// The one command the app ever asks Windows to elevate (spec docs/superpowers/specs/2026-10-17-windows-catch-up-design.md,
// "Windows Firewall"): built from constants, the app's own path and validated ports. Nothing here runs netsh: the runner is given a
// recorder in place of Process.Start.
internal static class WindowsFirewallVerification
{
    private const string Program = @"C:\Program Files\Mighty Claude\MightyClaude.exe";
    private const string System32 = @"C:\Windows\System32";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task TheRuleIsBuiltFromConstants()
    {
        var add = WindowsFirewallRule.AddArguments(Program, [43139]);
        Check(add is not null && add.SequenceEqual(["advfirewall", "firewall", "add", "rule", "name=Mighty Claude (Tailscale)", "dir=in", "action=allow", "protocol=TCP", "localport=43139", "remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48", "program=" + Program, "profile=any"]), "the add rule's argv");
        Check(WindowsFirewallRule.CommandLine(add!) == "advfirewall firewall add rule name=\"Mighty Claude (Tailscale)\" dir=in action=allow protocol=TCP localport=43139 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 program=\"C:\\Program Files\\Mighty Claude\\MightyClaude.exe\" profile=any", "netsh's own spelling: key=\"value\" around spaces");
        Check(WindowsFirewallRule.AddArguments(Program, [43139, 43137, 43139])![8] == "localport=43137,43139", "a port set (part 3 adds 43137): sorted, each once");
        Check(WindowsFirewallRule.DeleteArguments().SequenceEqual(["advfirewall", "firewall", "delete", "rule", "name=Mighty Claude (Tailscale)"]), "the delete rule's argv");
        Check(WindowsFirewallRule.ShowArguments().SequenceEqual(["advfirewall", "firewall", "show", "rule", "name=Mighty Claude (Tailscale)"]), "the query's argv");
        Check(WindowsFirewallRule.Arguments([43139], Program)!.SequenceEqual(add!) && WindowsFirewallRule.Arguments([], Program)!.SequenceEqual(WindowsFirewallRule.DeleteArguments()), "a port set adds; none deletes");
        foreach (var bad in new[] { "", "MightyClaude.exe", "C:\\Mighty\\Mighty\"Claude.exe", "C:\\Mighty\\Mighty\nClaude.exe", @"C:\Mighty\%TEMP%\MightyClaude.exe", @"C:\Mighty\MightyClaude.bat", @"\\server\share\MightyClaude.exe", @"C:\Mighty\..\MightyClaude.exe", @"C:\Mighty\x.exe:stream.exe", "C:/Mighty/MightyClaude.exe", @"C:\Mighty\\MightyClaude.exe" })
            Check(WindowsFirewallRule.AddArguments(bad, [43139]) is null, "a refused program path: " + bad.Replace("\n", "\\n", StringComparison.Ordinal));
        foreach (var ports in new int[][] { [], [1023], [65536], [0], [43139, -1] })
            Check(WindowsFirewallRule.AddArguments(Program, ports) is null, "refused ports: " + string.Join(",", ports));
        Check(WindowsFirewallRule.AddArguments(Program, [1024, 65535]) is not null, "1024 and 65535 are allowed");
        Check(WindowsFirewallRule.ValidSystemDirectory(System32) && !WindowsFirewallRule.ValidSystemDirectory("") && !WindowsFirewallRule.ValidSystemDirectory(null) && !WindowsFirewallRule.ValidSystemDirectory("System32") && !WindowsFirewallRule.ValidSystemDirectory("C:\\Win\"dows"), "the system folder is an absolute drive path");
        var query = WindowsFirewallRule.QueryStartInfo(System32);
        Check(query.FileName == System32 + @"\netsh.exe" && !query.UseShellExecute && query.CreateNoWindow && query.Verb.Length == 0 && query.RedirectStandardOutput
            && query.Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.ShowArguments()) && query.WorkingDirectory == System32, "the query runs netsh from the system folder, hidden and unelevated");
        var elevated = WindowsFirewallRule.ElevatedStartInfo(System32, add!);
        Check(elevated.FileName == System32 + @"\netsh.exe" && elevated.UseShellExecute && elevated.Verb == WindowsFirewallRule.ElevationVerb && elevated.WindowStyle == ProcessWindowStyle.Hidden
            && elevated.Arguments == WindowsFirewallRule.CommandLine(add!) && elevated.WorkingDirectory == System32, "a change asks Windows to elevate netsh from the system folder");
        return Task.CompletedTask;
    }

    internal static async Task TheRunnerElevatesOnlyThatCommand()
    {
        var launched = new List<ProcessStartInfo>(); var answer = 0; Exception? failure = null;
        var runner = new WindowsFirewallRuleRunner((info, _) => { launched.Add(info); if (failure is not null) throw failure; return Task.FromResult(answer); }, System32, Program);
        answer = 1; Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Absent, "exit 1: no such rule");
        answer = 0; Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Present, "exit 0: the rule is there");
        answer = 2; Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "any other exit: unknown");
        Check(launched.All(info => !info.UseShellExecute && info.Verb.Length == 0), "reading the rule never elevates");
        launched.Clear(); answer = 0;
        Check(await runner.AddAsync([43139], CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Verb == WindowsFirewallRule.ElevationVerb
            && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.AddArguments(Program, [43139])!), "add elevates netsh once with the rule");
        launched.Clear();
        Check(await runner.RemoveAsync(CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()), "remove elevates the delete");
        launched.Clear();
        Check(await runner.ApplyAsync([], CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()), "an empty port set removes the rule");
        failure = new Win32Exception(WindowsFirewallRule.ElevationCancelled);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Cancelled, "a declined UAC prompt is a cancel, not an error");
        failure = new Win32Exception(5);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Failed, "any other launch error is a failure");
        Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "a query that cannot run is unknown");
        failure = null; answer = 1;
        Check(await runner.AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Failed, Detail: "1" }, "netsh's exit code is kept");
        launched.Clear();
        Check(await runner.AddAsync([], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "noPorts" } && launched.Count == 0, "no port, no command");
        Check(await new WindowsFirewallRuleRunner((info, _) => { launched.Add(info); return Task.FromResult(0); }, System32, "MightyClaude.exe").AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badProgram" } && launched.Count == 0, "a relative program path, no command");
        Check(await new WindowsFirewallRuleRunner((info, _) => { launched.Add(info); return Task.FromResult(0); }, null, Program).QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown && launched.Count == 0, "no system folder, nothing runs");
    }
}
