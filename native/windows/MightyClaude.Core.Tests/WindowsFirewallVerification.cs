using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MightyClaude.Core;

// The one command the app ever asks Windows to elevate (spec docs/superpowers/specs/2026-10-17-windows-catch-up-design.md,
// "Windows Firewall"): built from constants, the app's own path and validated ports. Nothing here runs netsh: the runner is given a
// recorder in place of Process.Start.
internal static class WindowsFirewallVerification
{
    private const string Program = @"C:\Program Files\Mighty Claude\MightyClaude.exe";
    private const string System32 = @"C:\Windows\System32";
    private const string Ranges = "100.64.0.0/10,fd7a:115c:a1e0::/48";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task TheRuleIsBuiltFromConstants()
    {
        var add = WindowsFirewallRule.AddArguments(Program, [43139]);
        Check(add is not null && add.SequenceEqual(["advfirewall", "firewall", "add", "rule", "name=Mighty Claude (Tailscale)", "dir=in", "action=allow", "protocol=TCP", "localport=43139", "localip=" + Ranges, "remoteip=" + Ranges, "program=" + Program, "profile=any"]), "the add rule's argv");
        Check(WindowsFirewallRule.CommandLine(add!) == "advfirewall firewall add rule name=\"Mighty Claude (Tailscale)\" dir=in action=allow protocol=TCP localport=43139 localip=100.64.0.0/10,fd7a:115c:a1e0::/48 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 program=\"C:\\Program Files\\Mighty Claude\\MightyClaude.exe\" profile=any", "netsh's own spelling: key=\"value\" around the name and the path");
        Check(WindowsFirewallRule.CommandLine(WindowsFirewallRule.AddArguments(@"C:\Mighty\MightyClaude.exe", [43139])!).Contains(" program=\"C:\\Mighty\\MightyClaude.exe\" ", StringComparison.Ordinal), "the path is quoted even without a space");
        Check(WindowsFirewallRule.AddArguments(Program, [43139, 43137, 43139])![8] == "localport=43137,43139", "a port set (part 3 adds 43137): sorted, each once");
        var set = WindowsFirewallRule.SetArguments(Program, [43139, 43137]);
        Check(set is not null && set.SequenceEqual(["advfirewall", "firewall", "set", "rule", "name=Mighty Claude (Tailscale)", "new", "dir=in", "action=allow", "protocol=TCP", "localport=43137,43139", "localip=" + Ranges, "remoteip=" + Ranges, "program=" + Program, "profile=any", "enable=yes"]), "the update-in-place argv");
        Check(WindowsFirewallRule.SetArguments("MightyClaude.exe", [43139]) is null && WindowsFirewallRule.SetArguments(Program, []) is null, "the update is refused like the add");
        Check(WindowsFirewallRule.DeleteArguments().SequenceEqual(["advfirewall", "firewall", "delete", "rule", "name=Mighty Claude (Tailscale)"]), "the delete rule's argv");
        Check(WindowsFirewallRule.ShowArguments().SequenceEqual(["advfirewall", "firewall", "show", "rule", "name=Mighty Claude (Tailscale)"]), "the query's argv");
        Check(WindowsFirewallRule.Arguments([43139], Program)!.SequenceEqual(add!) && WindowsFirewallRule.Arguments([], Program)!.SequenceEqual(WindowsFirewallRule.DeleteArguments()), "a port set adds; none deletes");
        foreach (var bad in new[] { "", "MightyClaude.exe", "C:\\Mighty\\Mighty\"Claude.exe", "C:\\Mighty\\Mighty\nClaude.exe", @"C:\Mighty\%TEMP%\MightyClaude.exe", @"C:\Mighty\MightyClaude.bat", @"\\server\share\MightyClaude.exe", @"C:\Mighty\..\MightyClaude.exe", @"C:\Mighty\x.exe:stream.exe", "C:/Mighty/MightyClaude.exe", @"C:\Mighty\\MightyClaude.exe",
            "C:\\Mighty\\MightyClaude.exe\n", "C:\\Mi\u007Fghty\\MightyClaude.exe", "C:\\Mighty\u202E\\MightyClaude.exe", "C:\\Mighty\\.\\MightyClaude.exe", "C:\\\uFEFFMighty\\MightyClaude.exe", "C:\\Mighty\u00A0Claude\\MightyClaude.exe", "C:\\Mighty\tClaude\\MightyClaude.exe", "C:\\Mighty\u0085\\MightyClaude.exe",
            @"C:\Program Files\dotnet\dotnet.exe", @"C:\Mighty\NotMightyClaude.exe", @"C:\Mighty\MightyClaude.exe " })
            Check(WindowsFirewallRule.AddArguments(bad, [43139]) is null && WindowsFirewallRule.ValidProgram(bad) is null, "a refused program path: " + Visible(bad));
        Check(WindowsFirewallRule.ValidProgram(@"d:\apps\mightyclaude.EXE") is not null, "the exe name is matched without case");
        foreach (var ports in new int[][] { [], [1023], [65536], [0], [43139, -1], Enumerable.Range(43100, 17).ToArray() })
            Check(WindowsFirewallRule.AddArguments(Program, ports) is null, "refused ports: " + string.Join(",", ports));
        Check(WindowsFirewallRule.AddArguments(Program, [1024, 65535]) is not null && WindowsFirewallRule.AddArguments(Program, Enumerable.Range(43100, 16).Concat([43100])) is not null, "1024 and 65535 are allowed, and 16 ports");
        Check(WindowsFirewallRule.ValidSystemDirectory(System32) && !WindowsFirewallRule.ValidSystemDirectory("") && !WindowsFirewallRule.ValidSystemDirectory(null) && !WindowsFirewallRule.ValidSystemDirectory("System32") && !WindowsFirewallRule.ValidSystemDirectory("C:\\Win\"dows")
            && !WindowsFirewallRule.ValidSystemDirectory(System32 + "\n") && !WindowsFirewallRule.ValidSystemDirectory("C:\\Win\u202Edows"), "the system folder is an absolute drive path");
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
        var launched = new List<ProcessStartInfo>(); var answers = new Queue<int>(); Exception? elevatedFailure = null, queryFailure = null;
        Task<int> Record(ProcessStartInfo info, CancellationToken _)
        {
            launched.Add(info);
            if ((info.UseShellExecute ? elevatedFailure : queryFailure) is { } failure) throw failure;
            return Task.FromResult(answers.Count > 0 ? answers.Dequeue() : 0);
        }
        void Answer(params int[] codes) { launched.Clear(); answers.Clear(); foreach (var code in codes) answers.Enqueue(code); }
        IEnumerable<ProcessStartInfo> Elevations() => launched.Where(info => info.UseShellExecute);
        var runner = new WindowsFirewallRuleRunner(Record, System32, Program);

        Answer(1); Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Absent, "exit 1: no such rule");
        Answer(0); Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Present, "exit 0: a rule by that name is there");
        Answer(2); Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "any other exit: unknown");
        Check(launched.All(info => !info.UseShellExecute && info.Verb.Length == 0), "reading the rule never elevates");

        Answer(1, 0);
        Check(await runner.AddAsync([43139], CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done, null, new("43139", Program))
            && launched.Count == 2 && !launched[0].UseShellExecute && Elevations().Single().Verb == WindowsFirewallRule.ElevationVerb
            && Elevations().Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.AddArguments(Program, [43139])!), "no rule yet: read, then one elevated add; the result says what was written");
        Answer(0, 0);
        Check(await runner.ApplyAsync([43139, 43137], CancellationToken.None) is { Kind: FirewallChange.Done, Applied: { Ports: "43137,43139", Program: Program } }
            && Elevations().Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.SetArguments(Program, [43137, 43139])!), "a rule by the name already there: one elevated set, so rules never pile up and a dropped port closes");
        Answer(1, 0);
        Check((await runner.ApplyAsync([43139], CancellationToken.None)).Kind == FirewallChange.Done && Elevations().Single().Arguments.StartsWith("advfirewall firewall add rule ", StringComparison.Ordinal), "apply adds when there is none");
        Answer(2);
        Check(await runner.AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Failed, Detail: "queryFailed", Applied: null } && !Elevations().Any(), "a rule that cannot be read is not added blindly");
        Answer(0);
        Check(await runner.RemoveAsync(CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()), "remove elevates the delete");
        Answer(0);
        Check(await runner.ApplyAsync([], CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()), "an empty port set removes the rule");

        elevatedFailure = new Win32Exception(WindowsFirewallRule.ElevationCancelled); Answer(1);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Cancelled, "a declined UAC prompt is a cancel, not an error");
        elevatedFailure = new Win32Exception(5); Answer(1);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Failed, "any other launch error is a failure");
        elevatedFailure = new TimeoutException(); Answer(1);
        Check(await runner.AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Failed, Detail: "timeout" } && await runner.RemoveAsync(CancellationToken.None) is { Kind: FirewallChange.Failed, Detail: "timeout" }, "an elevated netsh that does not finish is a timeout");
        elevatedFailure = new OperationCanceledException(); Answer(1);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Failed, "a cancel that is not the caller's is a failure");
        elevatedFailure = null;
        queryFailure = new Win32Exception(5);
        Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "a query that cannot run is unknown");
        queryFailure = new TimeoutException();
        Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "a query that does not finish is unknown");
        queryFailure = null;
        Answer(1, 1);
        Check(await runner.AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Failed, Detail: "1", Applied: null }, "netsh's exit code is kept");

        Answer();
        Check(await runner.AddAsync([], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "noPorts" } && launched.Count == 0, "no port, no command");
        Check(await runner.AddAsync([80], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badPorts" } && launched.Count == 0, "a port below 1024, no command");
        Check(await runner.ApplyAsync(Enumerable.Range(43100, 17).ToArray(), CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badPorts" } && launched.Count == 0, "more than 16 ports, no command");
        Check(await new WindowsFirewallRuleRunner(Record, System32, "MightyClaude.exe").AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badProgram" } && launched.Count == 0, "a relative program path, no command");
        Check(await new WindowsFirewallRuleRunner(Record, System32, @"C:\Program Files\dotnet\dotnet.exe").AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badProgram" } && launched.Count == 0, "a dotnet.exe development run, no command");
        Check(await new WindowsFirewallRuleRunner(Record, System32, null).ApplyAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badProgram" } && launched.Count == 0, "no program path, no command");
        var homeless = new WindowsFirewallRuleRunner(Record, null, Program);
        Check(await homeless.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown
            && await homeless.AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "noSystemDirectory" }
            && await homeless.RemoveAsync(CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "noSystemDirectory" }
            && await homeless.ApplyAsync([], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "noSystemDirectory" } && launched.Count == 0, "no system folder, nothing runs");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (var (name, call) in new (string, Func<Task>)[] { ("query", () => runner.QueryAsync(cancelled.Token)), ("add", () => runner.AddAsync([43139], cancelled.Token)), ("remove", () => runner.RemoveAsync(cancelled.Token)), ("apply", () => runner.ApplyAsync([], cancelled.Token)) })
            Check(await Throws(call) && launched.Count == 0, "a cancelled token throws and starts nothing: " + name);
        using var during = new CancellationTokenSource();
        var cancelling = new WindowsFirewallRuleRunner((info, token) => { launched.Add(info); during.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(0); }, System32, Program);
        Check(await Throws(() => cancelling.RemoveAsync(during.Token)), "the caller's cancel during netsh is rethrown, not a result");
        using var duringQuery = new CancellationTokenSource();
        cancelling = new WindowsFirewallRuleRunner((info, token) => { launched.Add(info); duringQuery.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(0); }, System32, Program);
        Check(await Throws(() => cancelling.QueryAsync(duringQuery.Token)), "the caller's cancel during the query is rethrown, not unknown");
    }

    /// <summary>Only WindowsFirewallRule.cs may name the elevation verb or set a ProcessStartInfo's Verb.</summary>
    internal static Task OnlyTheRuleFileElevates()
    {
        var verb = new Regex(@"\brunas\b|\bVerb\s*=(?!=)", RegexOptions.CultureInvariant);
        var windows = WindowsFolder();
        var sources = new[] { "MightyClaude.Core", "MightyClaude.WinUI" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(windows, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")).ToArray();
        Check(sources.Length > 50, "the scan found the sources");
        var hits = sources.Where(file => verb.IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).ToArray();
        Check(hits.SequenceEqual(["WindowsFirewallRule.cs"]), "the elevation verb appears only in WindowsFirewallRule.cs: " + string.Join(", ", hits));
        return Task.CompletedTask;
    }

    private static string WindowsFolder([CallerFilePath] string here = "") => Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;

    private static async Task<bool> Throws(Func<Task> call)
    {
        try { await call(); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private static string Visible(string text) => string.Concat(text.Select(c => c is >= ' ' and < '\u007F' ? c.ToString() : "\\u" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
}
