using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MightyClaude.Core;

if (args is ["--long-child", var pidFile]) { var ready = pidFile + ".ready"; await File.WriteAllTextAsync(ready, Environment.ProcessId.ToString()); File.Move(ready, pidFile); Console.WriteLine("READY"); await Task.Delay(60000); return; }
// Fixtures for the cli runner checks: a failing command, a talkative command and a
// command whose own child must die with it. None of them is a real CLI.
if (args is ["--fail-child", var failText]) { await Console.Error.WriteLineAsync(failText); Environment.Exit(3); }
if (args is ["--noisy-child", var byteCount]) { var line = new string('x', 255); for (var written = 0; written < int.Parse(byteCount); written += 256) Console.WriteLine(line); return; }
if (args is ["--group-child", var groupPid])
{
    var self = Verification.Self("--long-child", groupPid);
    var info = new ProcessStartInfo(self.Binary) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var value in self.Prefix) info.ArgumentList.Add(value);
    using var grandchild = Process.Start(info) ?? throw new IOException("fixture grandchild did not start");
    Console.WriteLine("READY");
    await Task.Delay(60000);
    return;
}
if (args.Length >= 3 && args[0] == "--fake-cli") { await Verification.FakeCliAsync(args[1], args[2], args.Skip(3).ToArray()); return; }
await Verification.RunAsync();
