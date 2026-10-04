using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class CliEnvironmentVerification
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Frame(string nonce, string json) => "profile chatter\n\0MIGHTY_ENV_" + nonce + "\0" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\0MIGHTY_END_" + nonce + "\0after profile output";
    internal static Task StrictFramingAndSecrets()
    {
        var valid = Frame(Nonce, JsonSerializer.Serialize(new Dictionary<string, string> { ["Path"] = "C:\\trusted bin", ["AWS_BEARER_TOKEN_BEDROCK"] = "fixture-secret=\nsecond-line" }));
        var values = WindowsShellEnvironment.Parse(valid, Nonce);
        Check(values?.GetValueOrDefault("PATH") == "C:\\trusted bin" && values["AWS_BEARER_TOKEN_BEDROCK"] == "fixture-secret=\nsecond-line", "Framing must preserve Unicode/newlines and Windows case semantics.");
        Check(WindowsShellEnvironment.Parse(valid, new string('a', 32)) is null, "A different invocation's frame is rejected.");
        Check(WindowsShellEnvironment.Parse(valid + valid, Nonce) is null, "Repeated frames are ambiguous and rejected.");
        foreach (var json in new[] { "{}", "[]", "{\"PATH\":\"x\",\"path\":\"y\"}", "{\"PATH\":\"x\",\"bad=name\":\"x\"}", "{\"PATH\":\"x\",\"AWS_KEY\":true}", "{\"PATH\":\"x\",\"AWS_KEY\":\"bad\\u0000value\"}" })
            Check(WindowsShellEnvironment.Parse(Frame(Nonce, json), Nonce) is null, "Malformed/partial environment cannot replace the current environment.");
        Check(WindowsShellEnvironment.Parse(valid + new string('x', WindowsShellEnvironment.MaximumOutput), Nonce) is null, "Profile chatter is bounded too.");
        var command = WindowsShellEnvironment.Command(Nonce);
        Check(!command.Contains("fixture-secret") && !command.Contains("ExecutionPolicy") && !command.Contains("-NoProfile") && !command.Contains("Set-Location"), "Capture never embeds credentials or changes execution policy/project location.");
        Check(!new CliEnvironmentSnapshot(values!, true).ToString().Contains("fixture-secret"), "Diagnostics never print the environment dictionary.");
        return Task.CompletedTask;
    }

    internal static async Task RefreshRotationAndUnset()
    {
        var baseline = new Dictionary<string, string> { ["PATH"] = "base-path", ["AWS_BEARER_TOKEN_BEDROCK"] = "old-inherited", ["AWS_ACCESS_KEY_ID"] = "stale-access-key" };
        var count = 0; var value = "rotated-first"; var malformed = false;
        var resolver = new WindowsShellEnvironment((_, nonce, _) =>
        {
            count++;
            return Task.FromResult<string?>(malformed ? "broken profile output" : Frame(nonce, JsonSerializer.Serialize(new Dictionary<string, string> { ["PATH"] = "profile-path", ["AWS_BEARER_TOKEN_BEDROCK"] = value })));
        });
        var first = await resolver.ResolveAsync(baseline);
        Check(first.FromProfile && first.Values["AWS_BEARER_TOKEN_BEDROCK"] == value && first.Values["AWS_ACCESS_KEY_ID"] == "", "Fresh profile wins and explicit unset does not revive inherited AWS credentials.");
        first.Values["AWS_BEARER_TOKEN_BEDROCK"] = "consumer-mutation";
        Check((await resolver.ResolveAsync(baseline)).Values["AWS_BEARER_TOKEN_BEDROCK"] == value && count == 1, "Cached credentials are returned as independent dictionaries.");
        value = "rotated-second";
        Check((await resolver.ResolveAsync(baseline, true)).Values["AWS_BEARER_TOKEN_BEDROCK"] == value && count == 2, "Forced login/run refresh reloads the rotated token.");
        malformed = true;
        var failed = await resolver.ResolveAsync(baseline, true);
        Check(!failed.FromProfile && failed.Values["AWS_BEARER_TOKEN_BEDROCK"] == "old-inherited" && resolver.Current(baseline)["PATH"] == "base-path", "Failed refresh explicitly returns baseline and discards prior captured credentials.");
    }

    internal static async Task CancellationTimeoutAndFreshness()
    {
        var baseline = new Dictionary<string, string> { ["PATH"] = "base-path" };
        var hanging = new WindowsShellEnvironment(async (_, _, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }, timeout: TimeSpan.FromMilliseconds(30));
        var timeout = await hanging.ResolveAsync(baseline);
        Check(!timeout.FromProfile && timeout.Values["PATH"] == "base-path", "Slow interactive profiles fall back without leaking output.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await hanging.ResolveAsync(baseline, true, cancel.Token); throw new InvalidOperationException("Cancellation must propagate."); }
        catch (OperationCanceledException) { }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        var resolver = new WindowsShellEnvironment(async (_, nonce, token) =>
        {
            var generation = Interlocked.Increment(ref count);
            if (generation == 1) { started.SetResult(); await release.Task.WaitAsync(token); }
            return Frame(nonce, JsonSerializer.Serialize(new Dictionary<string, string> { ["PATH"] = "generation-" + generation }));
        });
        var prior = resolver.ResolveAsync(baseline); await started.Task;
        var fresh = resolver.ResolveAsync(baseline, true); release.SetResult();
        await prior; Check((await fresh).Values["PATH"] == "generation-2" && resolver.Current(baseline)["PATH"] == "generation-2", "Forced refresh cannot join an already-running capture or be overwritten by the older result.");
        var expiring = new WindowsShellEnvironment((_, nonce, _) => Task.FromResult<string?>(Frame(nonce, "{\"PATH\":\"profile-path\"}")), lifetime: TimeSpan.Zero);
        await expiring.ResolveAsync(baseline);
        Check(expiring.Current(baseline)["PATH"] == "profile-path", "Cache TTL never removes credentials from synchronous terminal launches; async entrypoints refresh them.");
    }

    internal static async Task WindowsCaptureWire()
    {
        Check(OperatingSystem.IsWindows(), "This fixture requires Windows PowerShell.");
        var shell = PseudoTerminal.DefaultShell;
        Check(Path.GetFileName(shell).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase), "The fixture exercises the app's selected ConsoleHost shell.");
        var arguments = WindowsShellEnvironment.Arguments(Nonce).ToList();
        Check(!arguments.Contains("-NoProfile") && !arguments.Contains("-ExecutionPolicy"), "Production keeps normal profile loading and execution policy.");
        // Only this isolated fixture suppresses personal profiles. The payload
        // serializer/framing and selected shell are identical to production.
        arguments.Insert(0, "-NoProfile");
        var command = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));
        command = "$env:MIGHTY_ENV_FIXTURE='token-\uD55C\uAE00=fixture';$env:MIGHTY_ENV_REMOVED=$null;" + command;
        arguments[^1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var child = ChildProcess.Start(ChildProcess.StartInfo(shell, arguments, Path.GetTempPath(), new Dictionary<string, string> { ["MIGHTY_ENV_REMOVED"] = "inherited-fixture" }));
        using var kill = cancel.Token.Register(child.Kill); child.Input.Close();
        var output = ProviderCatalog.ReadBoundedAsync(child.Output, WindowsShellEnvironment.MaximumOutput, cancel.Token);
        var error = ProviderCatalog.DrainAsync(child.Error, cancel.Token);
        await Task.WhenAll(output, error, child.Completion.WaitAsync(cancel.Token));
        Check(child.Completion.Result == 0, "Synthetic PowerShell capture must finish successfully.");
        var values = WindowsShellEnvironment.Parse(await output, Nonce);
        Check(values?.GetValueOrDefault("MIGHTY_ENV_FIXTURE") == "token-\uD55C\uAE00=fixture" && !values.ContainsKey("MIGHTY_ENV_REMOVED"), "Real PowerShell capture preserves Unicode and explicit credential deletion.");
    }
}
