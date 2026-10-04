using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class AgentIOVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid input was accepted."); }
    internal static Task BindingSecretsAndArgumentBoundaries()
    {
        var registry = new AgentIOBindings(); var workspace = new Workspace { Id = "workspace", Path = Path.GetTempPath() }; var executable = Path.Combine(Path.GetTempPath(), "Mighty Claude.exe");
        var first = registry.Bind("pane", workspace, "claude", "pipe", executable); var secret = first.Environment[AgentIOBinding.TokenKey];
        Check(registry.Resolve(secret) == first && registry.Resolve(new string('0', 64)) is null, "unknown tokens cannot resolve a pane");
        Check(!first.ToString().Contains(secret, StringComparison.Ordinal) && first.Redact("x" + secret + "y") == "x<pane-token-redacted>y", "binding and output redact the secret");
        var args = new List<string>(); first.AppendArguments(args);
        Check(args[0] == "--mcp-config" && !string.Join(" ", args).Contains(secret, StringComparison.Ordinal), "Claude token never appears in argv/config");
        using (var config = JsonDocument.Parse(args[1])) Check(config.RootElement.GetProperty("mcpServers").GetProperty("mighty-terminal").GetProperty("command").GetString() == executable, "space-containing path is a structured argument");
        var second = registry.Bind("pane", workspace, "codex", "pipe", executable);
        Check(registry.Resolve(secret) is null, "new run revokes prior binding");
        args.Clear(); second.AppendArguments(args);
        Check(args.Any(s => s.Contains("env_vars", StringComparison.Ordinal)) && args.All(s => !s.Contains("developer_instructions", StringComparison.Ordinal) && !s.Contains(second.Environment[AgentIOBinding.TokenKey], StringComparison.Ordinal)), "Codex forwards secret env names and preserves user instructions");
        registry.Revoke("pane"); Check(registry.Resolve(second.Environment[AgentIOBinding.TokenKey]) is null, "pane close revokes its token");
        foreach (var url in new[] { "file:///tmp/private", "javascript:alert(1)", "https://user:password@example.com", "data:text/html,x", "https://example.com\n" })
            Reject(() => AgentIOTools.Validate("open_url", JsonSerializer.SerializeToElement(new { url })));
        Check(AgentIOTools.Validate("open_url", JsonSerializer.SerializeToElement(new { url = "http://localhost:3000/path" })) == "http://localhost:3000/path", "developer localhost previews supported");
        Reject(() => AgentIOTools.Validate("run_in_terminal", JsonSerializer.SerializeToElement(new { command = "echo safe", workspace = "other" })));
        Reject(() => AgentIOTools.Validate("run_in_terminal", JsonSerializer.SerializeToElement(new { command = new string('가', 30_000) })));
        return Task.CompletedTask;
    }
    internal static async Task StdioMcpContract()
    {
        var calls = 0;
        var server = new AgentIOMcp((name, args, _) => { calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new { handle = "handle", status = "done", output = "한글\nline2", exitCode = 0 } })); });
        using var hello = JsonDocument.Parse((await server.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}"""))!);
        Check(hello.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "protocol negotiates a supported revision");
        using var tools = JsonDocument.Parse((await server.Handle("""{"jsonrpc":"2.0","id":"list","method":"tools/list"}"""))!);
        Check(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 4, "exactly the Mac terminal/web tool set");
        Check(await server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""") is null, "notifications receive no protocol reply");
        using var invalid = JsonDocument.Parse((await server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"open_url","arguments":{"url":"file:///secret"}}}"""))!);
        Check(invalid.RootElement.GetProperty("result").GetProperty("isError").GetBoolean() && calls == 0, "invalid tools never reach the host");
        using var valid = JsonDocument.Parse((await server.Handle("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_in_terminal","arguments":{"command":"echo hello"}}}"""))!);
        Check(!valid.RootElement.GetProperty("result").GetProperty("isError").GetBoolean() && calls == 1 && valid.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("output").GetString() == "한글\nline2", "UTF-8 structured output remains intact");
        var output = new StringWriter(); await server.Serve(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n{\"jsonrpc\":\"2.0\",\"method\":\"notification\"}\n"), output, CancellationToken.None);
        Check(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1, "stdio contains only one JSON-RPC reply per request");
    }
    internal static async Task AuthenticatedPipeRejectsRevocation()
    {
        var received = new List<string>();
        await using var host = new AgentIOPipe((binding, tool, value, _) => { lock (received) received.Add(binding.PaneId); return Task.FromResult<object>(new { owner = binding.PaneId }); });
        var workspace = new Workspace { Id = "workspace", Path = Path.GetTempPath() };
        var binding = host.Bindings.Bind("mine", workspace, "codex", host.Name, Path.Combine(Path.GetTempPath(), "app.exe"));
        var arguments = JsonSerializer.SerializeToElement(new { command = "echo fixture" });
        var first = await AgentIOPipe.Call(host.Name, binding.Environment[AgentIOBinding.TokenKey], "run_in_terminal", arguments, CancellationToken.None);
        Check(first.GetProperty("ok").GetBoolean() && first.GetProperty("result").GetProperty("owner").GetString() == "mine", "authenticated pipe routes only to bound owner");
        host.Bindings.Revoke("mine");
        var next = await AgentIOPipe.Call(host.Name, binding.Environment[AgentIOBinding.TokenKey], "run_in_terminal", arguments, CancellationToken.None);
        Check(!next.GetProperty("ok").GetBoolean() && received.SequenceEqual(new[] { "mine" }), "revoked credential cannot execute another tool");
        using var oversized = new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff });
        try { await AgentIOPipe.ReadPacket(oversized, CancellationToken.None); throw new InvalidOperationException("oversized packet allocated"); } catch (IOException) { }
    }
    internal static Task TerminalCleanerAndEphemeralOwnership()
    {
        var cleaner = new TerminalTextCleaner();
        Check(cleaner.Clean("before\u001b[3") == "before" && cleaner.Clean("1m한글\u001b[0m\r\n") == "한글\n", "CSI state survives chunk boundaries");
        Check(cleaner.Clean("\u001b]52;clipboard-secret") == "" && cleaner.Clean("\u001b\\after") == "after", "OSC clipboard/control payload never reaches agent output");
        var workspace = new Workspace { Id = "w", Path = Path.GetTempPath() };
        var parent = new RunSession { Id = "agent", WorkspaceId = "w" };
        var terminal = new RunSession { Id = "agent-terminal:agent", Kind = "agent-terminal", OwnerSessionId = "agent", WorkspaceId = "w" };
        var snapshot = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [parent, terminal] }, false);
        Check(snapshot.Sessions.Count == 2 && FilePaneKind.Stored(snapshot).Sessions.Select(s => (s.Id, s.Kind)).SequenceEqual(new[] { (parent.Id, parent.Kind) }), "agent IO is shown live and excluded from saved state");
        Check(StateStore.Normalize(snapshot with { Sessions = [terminal] }, false).Sessions.Count == 0, "orphan IO panes disappear with parent");
        Check(StateStore.Normalize(snapshot, true).Sessions.Count == 1, "agent IO never restores without native processes");
        return Task.CompletedTask;
    }
    internal static async Task RealWindowsInteractiveProcess()
    {
        var directory = Verification.Temp();
        try
        {
            await using var first = new AgentTerminal(directory); await using var other = new AgentTerminal(directory);
            var result = await first.Run("[Console]::WriteLine(('mighty-'+'한글-marker')); exit 7", CancellationToken.None);
            for (var i = 0; result.Status == "running" && i < 100; i++) { await Task.Delay(50); result = first.Read(result.Handle); }
            Check(result.Status == "done", "visible ConPTY command completes");
            Check(result.ExitCode == 7, "visible ConPTY preserves exit code: " + result.ExitCode);
            Check(first.Replay.Contains("mighty-한글-marker", StringComparison.Ordinal), "visible ConPTY captures Unicode output even when the host stdout is redirected");
            Reject(() => other.Read(result.Handle));
            var running = await first.Run("Start-Sleep -Seconds 300", CancellationToken.None);
            Check(running.Status == "running", "long commands return a pollable handle");
            var timer = System.Diagnostics.Stopwatch.StartNew(); var stopped = await first.Stop(running.Handle);
            Check(stopped.Status == "done" && timer.Elapsed < TimeSpan.FromSeconds(10), "stop interrupts then kills only owned job within deadline");
        }
        finally { Directory.Delete(directory, true); }
    }
}
