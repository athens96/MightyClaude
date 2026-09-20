using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MightyClaude.Core;

internal static class Verification
{
    private static int passed, skipped;
    internal static string Temp() { var path = Path.Combine(Path.GetTempPath(), "mighty-core-test-" + Wire.Id()); Directory.CreateDirectory(path); return path; }
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception) { return; } throw new InvalidOperationException("Expected rejection"); }
    internal static async Task Until(Func<bool> condition, int milliseconds = 7000) { var end = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds); while (!condition()) { if (DateTimeOffset.UtcNow >= end) throw new TimeoutException("Expected condition was not reached"); await Task.Delay(20); } }
    private static async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    internal static CliCommand Self(params string[] args)
    {
        var binary = Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable"); return new(binary, Path.GetFileNameWithoutExtension(binary).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? new[] { Assembly.GetExecutingAssembly().Location }.Concat(args).ToArray() : args, "2.1.271");
    }
    private static ProviderCatalog Absent() => new((_, _) => Task.FromResult<CliCommand?>(null));
    private static StartRunRequest Shell(string id, Workspace workspace, string text) => new(id, workspace.Id, "shell", text);
    private static string LongCommand(string pidFile) { var self = Self("--long-child", pidFile); return OperatingSystem.IsWindows() ? string.Join(" ", new[] { self.Binary }.Concat(self.Prefix).Select(ChildProcess.QuoteWindows)) : string.Join(" ", new[] { self.Binary }.Concat(self.Prefix).Select(v => "'" + v.Replace("'", "'\"'\"'") + "'")); }
    private static string AttachmentReferencePath(string prompt, string name)
    {
        var prefix = JsonSerializer.Serialize(name, Wire.Json) + ": ";
        var line = prompt.Split('\n').Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return JsonSerializer.Deserialize<string>(line[prefix.Length..], Wire.Json) ?? throw new InvalidOperationException("Missing attachment path.");
    }
    private sealed class Protector : ISecretProtector
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(string value) { var nonce = RandomNumberGenerator.GetBytes(12); var bytes = Encoding.UTF8.GetBytes(value); var cipher = new byte[bytes.Length]; var tag = new byte[16]; using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, bytes, cipher, tag); return nonce.Concat(tag).Concat(cipher).ToArray(); }
        public string Unprotect(byte[] value) { var output = new byte[value.Length - 28]; using var aes = new AesGcm(key, 16); aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), output); return Encoding.UTF8.GetString(output); }
    }
    private sealed class FakeManager(Action<RunEvent> emit) : IRunManager
    {
        internal readonly ConcurrentBag<StartRunRequest> Started = [];
        internal readonly ConcurrentBag<string> Stopped = [];
        internal void Emit(RunEvent value) => emit(value);
        public Task StartAsync(StartRunRequest request) { Started.Add(request); emit(RunEvent.State(request.SessionId, "running")); return Task.CompletedTask; }
        public Task StopAsync(string id) { Stopped.Add(id); emit(RunEvent.State(id, "stopped")); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { foreach (var request in Started.Where(r => !Stopped.Contains(r.SessionId))) Stopped.Add(request.SessionId); return ValueTask.CompletedTask; }
    }
    internal static async Task FakeCliAsync(string provider, string record, string[] args)
    {
        await File.WriteAllTextAsync(record + ".args", JsonSerializer.Serialize(args, Wire.Json));
        if (args.Contains("--version")) { Console.WriteLine("2.1.271"); return; }
        // A Claude launched with host permission prompts: answer the handshake,
        // ask to use one tool once the prompt frame arrives, record the answer.
        if (args.Contains("--permission-prompt-tool"))
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                await File.AppendAllTextAsync(record + ".input", line + "\n");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                switch (root.Text("type"))
                {
                    case "control_request":
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = root.Text("request_id"), response = new { } } }, Wire.Json));
                        break;
                    case "user":
                        Console.WriteLine("{\"type\":\"control_request\",\"request_id\":\"ask-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Read\",\"tool_use_id\":\"tool-ask-1\",\"input\":{\"file_path\":\"~/.claude/CLAUDE.md\"},\"blocked_path\":\"~/.claude/CLAUDE.md\"}}");
                        break;
                    case "control_response":
                        await File.WriteAllTextAsync(record + ".decision", line);
                        Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"fixture\",\"session_id\":\"fixture\"}");
                        return;
                }
            }
            return;
        }
        if (args.Contains("app-server") || args.Contains("--safe-mode"))
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                await File.AppendAllTextAsync(record + ".input", line + "\n"); using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (provider == "claude")
                {
                    if (root.GetProperty("type").GetString() != "control_request") throw new InvalidOperationException("Prompt forbidden");
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = root.GetProperty("request_id").GetString(), response = new { models = new[] { new { value = "sonnet", displayName = "Sonnet", description = "Fixture", supportsEffort = true, supportedEffortLevels = new[] { "low", "high" } } } } } }, Wire.Json));
                }
                else if (root.GetProperty("method").GetString() == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
                else if (root.GetProperty("method").GetString() == "model/list") Console.WriteLine("{\"id\":2,\"result\":{\"data\":[{\"id\":\"gpt-6-astra\",\"model\":\"gpt-6-astra\",\"displayName\":\"Astra\",\"supportedReasoningEfforts\":[{\"reasoningEffort\":\"high\"}]}],\"nextCursor\":null}}");
                else if (root.GetProperty("method").GetString() != "initialized") throw new InvalidOperationException("Turn creation forbidden");
            }
        }
        else
        {
            var input = await Console.In.ReadToEndAsync(); await File.WriteAllTextAsync(record + ".prompt", input);
            if (args.Contains("--verify-attachments"))
            {
                var flag = provider == "claude" ? "--add-dir" : provider == "codex" ? "--image" : "--include-directories";
                var index = Array.IndexOf(args, flag); Check(index >= 0 && index + 1 < args.Length, provider + " staged attachment argument missing");
                var directory = provider == "codex" ? Path.GetDirectoryName(args[index + 1])! : args[index + 1];
                var captured = new Dictionary<string, string>();
                foreach (var file in Directory.EnumerateFiles(directory)) captured.Add(file, Convert.ToBase64String(await File.ReadAllBytesAsync(file)));
                await File.WriteAllTextAsync(record + ".attachments", JsonSerializer.Serialize(captured, Wire.Json));
            }
            Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"fixture-thread\"}"); Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"FAKE_CLI_OK\"}}");
            if (args.Contains("--hold-run")) await Task.Delay(60000);
            if (args.Contains("--fail-run")) Environment.ExitCode = 2;
        }
    }
    internal static async Task RunAsync()
    {
        await Test("structured tool identity, duration, Mods dedup and cancellation", ActivityUsageVerification.Activities);
        await Test("Codex/Gemini direct usage snapshots and intact long Markdown", ActivityUsageVerification.ProviderUsage);
        await Test("Claude current context, cumulative totals and quota observation age", ActivityUsageVerification.ClaudeContext);
        await Test("a leftover background task's report does not end the real request", ActivityUsageVerification.LeftoverTaskResult);
        await Test("optional metadata recovery, elapsed checkpoints and workspace state", ActivityUsageVerification.Persistence);
        await Test("remote activity/usage opt-in, identity mapping and legacy cursor fallback", ActivityUsageVerification.Remote);
        await Test("authenticated structured Mods output and usage boundaries", ActivityUsageVerification.Mods);
        await Test("pane docking, split geometry, normalization and layout persistence", PaneLayoutVerification.Run);
        await Test("execution settings wire, permissions, fast/search/network overrides", SettingsVerification.Run);
        await Test("Claude Auto permissions, supported runtime gate and legacy-safe persistence", AutoPermissionVerification.Run);
        await Test("attachment validation, Unicode wire, safe staging and multimodal CLI payloads", AttachmentVerification.Run);
        await Test("wire settings include explicit nullable limits; provider validation", async () =>
        {
            var json = JsonSerializer.Serialize(new RunSettings(), Wire.Json); Check(json.Contains("\"maxTurns\":null") && json.Contains("\"maxBudgetUsd\":null"));
            await Reject(() => Task.FromResult(new StartRunRequest("p", "w", "claude", "x", Provider: "gemini", Settings: new("high")).Validate()));
            await Reject(() => Task.FromResult(new StartRunRequest("p", "w", "claude", "x", Provider: "codex", Settings: new(PermissionMode: "plan")).Validate()));
            Check(ProviderCatalog.Arguments(new("p", "w", "claude", "secret", "sonnet", "claude", new("high", "manual", 3, 1.5)), "/plugin").Contains("--effort"));
            Check(ProviderCatalog.Efforts("codex", "default", ProviderCatalog.Fallback("codex")).Length == 0); Check(!ProviderCatalog.SupportsMods("2.1.263")); Check(ProviderCatalog.SupportsMods("2.1.271 (Claude Code)"));
        });
        await Test("native profile imports a copy, preserves settings, remote identity and drafts", async () =>
        {
            var legacy = Temp(); var native = Temp();
            try
            {
                var workspace = new Workspace { Path = legacy }; var pane = new RunSession { WorkspaceId = workspace.Id, Provider = "codex", Model = "gpt-6-astra", Settings = new("high", "acceptEdits", FastMode: true, WebSearch: "cached", NetworkAccess: true), Status = "running", Draft = "한글 draft" };
                var original = JsonSerializer.Serialize(new AppSnapshot { Workspaces = [workspace], Sessions = [pane], Theme = "light", Layout = "columns" }, Wire.Json); await File.WriteAllTextAsync(Path.Combine(legacy, "workspace-state.json"), original);
                var store = new StateStore(native, legacy); var state = await store.LoadAsync(); Check(state.Sessions[0].Status == "stopped" && state.Sessions[0].Draft == pane.Draft && state.Sessions[0].Settings.Effort == "high");
                var peer = store.ApproveRemote("connection", workspace with { Id = "peer", Path = "C:\\remote\\project" }, "Peer"); await store.SaveAsync(state with { Workspaces = state.Workspaces.Append(peer).ToList() });
                await Reject(() => store.ResolveLocalAsync(peer.Id)); Check(await File.ReadAllTextAsync(Path.Combine(legacy, "workspace-state.json")) == original);
                var restored = await new StateStore(native).LoadAsync(); Check(restored.Workspaces.Last().Remote?.WorkspaceId == "peer"); Check(restored.Sessions[0].Settings == pane.Settings, "Selected composer settings must survive profile restart.");
                await Reject(() => store.SaveAsync(state with { Workspaces = [workspace with { Path = Path.GetTempPath() }] }));
            }
            finally { Directory.Delete(legacy, true); Directory.Delete(native, true); }
        });
        await Test("corrupt/unknown/oversize state cannot be overwritten by shutdown", async () =>
        {
            foreach (var text in new[] { "null", "{", "{\"version\":2,\"workspaces\":[],\"sessions\":[]}", "{\"version\":1,\"workspaces\":null,\"sessions\":[]}" })
            {
                var directory = Temp(); try { var path = Path.Combine(directory, "workspace-state.json"); await File.WriteAllTextAsync(path, text); var store = new StateStore(directory); await Reject(() => store.LoadAsync()); await Reject(() => store.SaveAsync(new())); Check(await File.ReadAllTextAsync(path) == text); } finally { Directory.Delete(directory, true); }
            }
            var source = Temp(); var target = Temp(); try { await File.WriteAllBytesAsync(Path.Combine(source, "workspace-state.json"), new byte[8 * 1024 * 1024 + 1]); await Reject(() => new StateStore(target, source).LoadAsync()); Check(!File.Exists(Path.Combine(target, "workspace-state.json"))); } finally { Directory.Delete(source, true); Directory.Delete(target, true); }
        });
        await Test("stream parser keeps text after thinking, errors latch and dedupe is bounded", () =>
        {
            var output = new List<string>(); var parser = new OutputParser("claude", (_, value) => output.Add(value), _ => { });
            parser.Parse("{\"type\":\"assistant\",\"message\":{\"id\":\"a\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"private\"}]}}"); parser.Parse("{\"type\":\"assistant\",\"message\":{\"id\":\"a\",\"content\":[{\"type\":\"text\",\"text\":\"visible\"}]}}"); Check(output.SequenceEqual(["visible"]));
            var gemini = new OutputParser("gemini", (_, _) => { }, _ => { }); gemini.Parse("{\"type\":\"error\",\"severity\":\"error\",\"message\":\"fatal\"}"); gemini.Parse("{\"type\":\"error\",\"severity\":\"warning\",\"message\":\"warning\"}"); Check(gemini.Failed);
            var codex = new OutputParser("codex", (_, _) => { }, _ => { }); codex.Parse(JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "a", type = "agent_message", text = new string('x', 100000) } })); var seen = (HashSet<string>)typeof(OutputParser).GetField("seen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(codex)!; Check(seen.Single().Length < 200); return Task.CompletedTask;
        });
        await Test("Claude/Codex metadata protocol sends no user prompt or model turn", async () =>
        {
            var directory = Temp(); try
            {
                foreach (var provider in new[] { "claude", "codex" })
                {
                    var record = Path.Combine(directory, provider); var catalog = await ProviderCatalog.ReadModelsAsync(provider, Self("--fake-cli", provider, record)); Check(catalog.Source == "cli" && catalog.Models.Count == 2, provider + " metadata");
                    var input = await File.ReadAllTextAsync(record + ".input"); Check(!input.Contains("turn/start") && !input.Contains("\"type\":\"user\""));
                    var arguments = await File.ReadAllTextAsync(record + ".args"); if (provider == "claude") Check(arguments.Contains("--no-session-persistence") && arguments.Contains("--safe-mode") && input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1);
                }
            } finally { Directory.Delete(directory, true); }
        });
        await Test("provider prompt goes to stdin; shell output and cancellation execute real children", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var record = Path.Combine(directory, "fake"); var events = new ConcurrentQueue<RunEvent>();
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Self("--fake-cli", "codex", record)));
            await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue);
            try
            {
                var prompt = "stdin only ` & $() 한글"; await manager.StartAsync(new("provider", workspace.Id, "claude", prompt, Provider: "codex")); await Until(() => events.Any(e => e.SessionId == "provider" && e.Status == "completed")); Check(await File.ReadAllTextAsync(record + ".prompt") == prompt); Check(!(await File.ReadAllTextAsync(record + ".args")).Contains(prompt));
                await manager.StartAsync(Shell("echo", workspace, "echo MIGHTY_NATIVE_OK")); await Until(() => events.Any(e => e.SessionId == "echo" && e.Status == "completed")); Check(events.Any(e => e.SessionId == "echo" && e.Entry?.Text.Contains("MIGHTY_NATIVE_OK") == true));
                var pid = Path.Combine(directory, "pid"); await manager.StartAsync(Shell("long", workspace, LongCommand(pid))); await Until(() => File.Exists(pid)); var processId = int.Parse(await File.ReadAllTextAsync(pid)); await manager.StopAsync("long"); Check(events.Any(e => e.SessionId == "long" && e.Status == "stopped")); await Until(() => !Alive(processId));
            }
            finally { await manager.DisposeAsync(); Directory.Delete(directory, true); }
        });
        await Test("fake CLI receives attachment bytes and staging is removed on completion, error and stop", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var events = new ConcurrentQueue<RunEvent>(); var files = new[] { AttachmentSupport.Make("image.png", AttachmentVerification.Png), AttachmentSupport.Make("source.cs", "attachment-fixture-contents"u8.ToArray()) };
            try
            {
                var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin")); await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
                foreach (var provider in Wire.Providers)
                {
                    var record = Path.Combine(directory, provider); await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Self("--fake-cli", provider, record, "--verify-attachments"))); await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue);
                    await manager.StartAsync(new(provider, workspace.Id, "claude", "", Provider: provider, Attachments: files)); await Until(() => !manager.IsRunning(provider));
                    Check(events.Any(e => e.SessionId == provider && e.Status == "completed"), provider + " attachment run did not complete");
                    var args = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record + ".args"), Wire.Json)!;
                    var flag = provider == "claude" ? "--add-dir" : provider == "codex" ? "--image" : "--include-directories";
                    var path = args[Array.IndexOf(args, flag) + 1]; if (provider == "codex") path = Path.GetDirectoryName(path)!;
                    Check(!Directory.Exists(path), provider + " staging cleanup");
                    var prompt = await File.ReadAllTextAsync(record + ".prompt");
                    var captured = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(record + ".attachments"), Wire.Json)!;
                    Check(captured.Count == files.Length, provider + " child did not read every staged attachment");
                    if (provider == "claude")
                    {
                        using var json = JsonDocument.Parse(prompt); var content = json.RootElement.GetProperty("message").GetProperty("content").EnumerateArray().ToArray();
                        Check(content.Single(c => c.GetProperty("type").GetString() == "image").GetProperty("source").GetProperty("data").GetString() == files[0].DataBase64, "Claude inline image bytes changed");
                        prompt = content.Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                    }
                    // References are JSON strings: Windows separators must be decoded before comparing paths.
                    foreach (var attachment in provider == "claude" ? files.Skip(1) : files)
                    {
                        var referencedPath = AttachmentReferencePath(prompt, attachment.Name);
                        Check(Path.GetDirectoryName(referencedPath) == path, provider + " reference escaped its staged directory");
                        Check(captured.TryGetValue(referencedPath, out var bytes) && bytes == attachment.DataBase64, provider + " referenced bytes changed before CLI consumption");
                        if (provider == "codex" && attachment.MediaType.StartsWith("image/", StringComparison.Ordinal)) Check(args[Array.IndexOf(args, "--image") + 1] == referencedPath, "Codex image argument and reference differ");
                    }
                    Check(!args.Contains(files[0].DataBase64), provider + " attachment bytes leaked into argv");
                }
                foreach (var behavior in new[] { "--hold-run", "--fail-run" })
                {
                    var record = Path.Combine(directory, behavior); await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Self("--fake-cli", "codex", record, behavior))); await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue); var id = Wire.Id();
                    await manager.StartAsync(new(id, workspace.Id, "claude", "", Provider: "codex", Attachments: files)); await Until(() => File.Exists(record + ".prompt")); var args = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record + ".args"), Wire.Json)!; var stage = Path.GetDirectoryName(args[Array.IndexOf(args, "--image") + 1])!;
                    if (behavior == "--hold-run") { Check(Directory.Exists(stage)); await manager.StopAsync(id); } else await Until(() => !manager.IsRunning(id));
                    Check(!Directory.Exists(stage)); Check(events.Any(e => e.SessionId == id && e.Status == (behavior == "--hold-run" ? "stopped" : "error")));
                }
                await using var absent = Absent(); await using var unavailable = new RunManager(_ => Task.FromResult(workspace), absent, plugin, events.Enqueue);
                await Reject(() => unavailable.StartAsync(new("no-cli", workspace.Id, "claude", "", Provider: "codex", Attachments: files))); Check(!unavailable.IsRunning("no-cli"));
                var pending = new TaskCompletionSource<Workspace>(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = new RunManager(_ => pending.Task, absent, plugin, events.Enqueue); var start = cancelled.StartAsync(new("cancel-attach", workspace.Id, "claude", "", Provider: "codex", Attachments: files)); var closing = cancelled.DisposeAsync().AsTask(); pending.SetResult(workspace); await Reject(() => start); await closing;
            }
            finally { Directory.Delete(directory, true); }
        });
        await Test("pending stop and disposal prevent a late child launch", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var pending = new TaskCompletionSource<Workspace>(TaskCreationOptions.RunContinuationsAsynchronously); var events = new ConcurrentQueue<RunEvent>(); await using var catalog = Absent();
            var manager = new RunManager(_ => pending.Task, catalog, "", events.Enqueue); await manager.StartAsync(Shell("pending", workspace, "echo NEVER")); var shutdown = manager.DisposeAsync().AsTask(); pending.SetResult(workspace); await shutdown; Check(events.All(e => e.Entry?.Text != "NEVER")); await Reject(() => manager.StartAsync(Shell("late", workspace, "echo NEVER")));
            Directory.Delete(directory, true);
        });
        await Test("desktop routing retains cancellation before manager admission", async () =>
        {
            var directory = Temp(); var workspacePath = Temp(); await using var catalog = Absent(); var service = new DesktopService(directory, null, "", catalog, testLoopback: true);
            try
            {
                await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath); var pane = new RunSession { WorkspaceId = workspace.Id, Kind = "shell" }; await service.UpdateAsync(s => s with { Sessions = [pane] });
                service.RunEventReceived += value => { if (value.Status == "running") _ = service.StopAsync(value.SessionId); };
                await service.StartAsync(Shell(pane.Id, workspace, "echo SHOULD_NOT_RUN")); Check(service.Snapshot.Sessions[0].Status == "stopped"); Check(service.Snapshot.Sessions[0].Logs.All(l => l.Kind != "output"));
            }
            finally { await service.DisposeAsync(); Directory.Delete(directory, true); Directory.Delete(workspacePath, true); }
        });
        await Test("tailnet policy rejects LAN/public/service/mixed DNS and requires Tailscale", async () =>
        {
            Check(RemoteNetwork.TailAddress(IPAddress.Parse("100.64.0.1"))); Check(RemoteNetwork.TailAddress(IPAddress.Parse("fd7a:115c:a1e0::1"))); Check(!RemoteNetwork.TailAddress(IPAddress.Parse("100.100.100.100")));
            var tailscale = new TailscaleInfo(true, ["100.64.0.1"], "test", "test");
            foreach (var address in new[] { "http://127.0.0.1:1", "http://192.168.1.1:1", "http://8.8.8.8", "http://user@100.64.0.1", "http://100.64.0.1/path", "http://100.64.0.1?x=1", "https://100.64.0.1" }) await Reject(async () => { _ = await RemoteNetwork.PinAsync(address, tailscale); });
            await Reject(async () => { _ = await RemoteNetwork.PinAsync("http://peer:1", tailscale, lookup: (_, _) => Task.FromResult(new[] { IPAddress.Parse("100.64.0.2"), IPAddress.Loopback })); });
            await Reject(async () => { _ = await RemoteNetwork.PinAsync("http://100.64.0.1", tailscale with { Available = false }); });
        });
        await Test("remote authentication, allowlist, random job IDs, bounded cursors and orphan lease", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; await using var catalog = Absent(); FakeManager? manager = null;
            await using var server = new RemoteServer("test", [workspace.Id], () => [workspace], _ => Task.FromResult(workspace), () => catalog.GetRuntimeAsync(), emit => manager = new(emit), true, TimeSpan.FromMilliseconds(300));
            try
            {
                await server.StartAsync(IPAddress.Loopback, 0); var target = new PinnedRemote(new(server.Address), IPAddress.Loopback);
                await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, RemoteNetwork.Token(), HttpMethod.Get, "/v1/info"); });
                using var client = new HttpClient(); using var browser = new HttpRequestMessage(HttpMethod.Get, server.Address + "/v1/info"); browser.Headers.Add("Origin", "http://localhost"); browser.Headers.Add("Authorization", "Bearer " + server.Token); browser.Headers.Add(RemoteNetwork.VersionHeader, "1"); using var denied = await client.SendAsync(browser); Check(denied.StatusCode == HttpStatusCode.Forbidden);
                await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, server.Token, HttpMethod.Post, "/v1/runs", new { request = Shell("local-pane", workspace with { Id = "unshared" }, "echo bad") }); });
                using var started = await RemoteNetwork.RequestAsync(target, server.Token, HttpMethod.Post, "/v1/runs", new { request = Shell("local-pane", workspace, "echo good") }); var id = started.RootElement.GetProperty("jobId").GetString()!; Check(id != "local-pane"); await Until(() => manager!.Started.Count == 1);
                for (var i = 0; i < 400; i++) manager!.Emit(RunEvent.Log(id, "output", "row " + i));
                using var poll = await RemoteNetwork.RequestAsync(target, server.Token, HttpMethod.Get, $"/v1/runs/{id}/events?cursor=0"); Check(poll.RootElement.GetProperty("gap").GetBoolean()); Check(poll.RootElement.GetProperty("events").GetArrayLength() == 100);
                await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, server.Token, HttpMethod.Get, $"/v1/runs/{id}/events?cursor=99999"); });
                await Until(() => manager!.Stopped.Contains(id), 3000);
            }
            finally { await server.DisposeAsync(); Directory.Delete(directory, true); }
        });
        await Test("remote settings reject legacy hosts before POST and round trip with advertised capabilities", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var modern = false; var posted = new ConcurrentQueue<StartRunRequest>(); var bodies = new ConcurrentQueue<JsonElement>(); var events = new ConcurrentQueue<RunEvent>(); await using var catalog = Absent();
            var legacyCaps = new { effort = true, permissionModes = new[] { "manual", "acceptEdits" }, maxTurns = false, maxBudgetUsd = false, resume = true };
            await using var server = await HttpHost.StartAsync(IPAddress.Loopback, 0, async context =>
            {
                if (context.Request.Path == "/v1/info")
                {
                    object caps = modern ? ProviderCatalog.Capabilities("codex") : legacyCaps;
                    await HttpHost.ReplyAsync(context, 200, new { protocol = 1, hostId = "settings-host", hostName = "Settings host", workspaces = new[] { workspace }, runtime = new { platform = "win32", appVersion = "fixture", claudeAvailable = false, providers = new[] { new { id = "codex", name = "Codex", available = true, detail = "Metadata fixture only", modelCatalog = ProviderCatalog.Fallback("codex"), capabilities = caps } } } }); return;
                }
                if (context.Request.Method == "POST" && context.Request.Path == "/v1/runs")
                {
                    using var json = await HttpHost.ReadJsonAsync(context); var request = json.RootElement.GetProperty("request").Deserialize<StartRunRequest>(Wire.Json)!; posted.Enqueue(request); bodies.Enqueue(json.RootElement.GetProperty("request").GetProperty("settings").Clone()); await HttpHost.ReplyAsync(context, 202, new { protocol = 1, jobId = request.SessionId }); return;
                }
                var parts = context.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (context.Request.Method == "GET" && parts.Length == 4) { await HttpHost.ReplyAsync(context, 200, new WirePoll(1, 1, 1, false, true, [new(1, RunEvent.State(parts[2], "completed"))])); return; }
                await HttpHost.ReplyAsync(context, 200, new { protocol = 1, stopped = true });
            });
            await using var client = new RemoteController(directory, () => [], _ => throw new ArgumentException(), () => catalog.GetRuntimeAsync(), emit => new FakeManager(emit), events.Enqueue, testLoopback: true);
            try
            {
                var connected = await client.ConnectAsync(new("Settings host", server.Address.GetLeftPart(UriPartial.Authority), RemoteNetwork.Token())); var connection = connected.Connections.Single(); var imported = workspace with { Id = "imported-settings", Remote = new(connection.Id, workspace.Id, "Settings host") };
                var selected = new[] { new RunSettings(FastMode: true), new RunSettings(WebSearch: "live"), new RunSettings(PermissionMode: "acceptEdits", NetworkAccess: true), new RunSettings(PermissionMode: "fullAccess") };
                foreach (var settings in selected) await Reject(() => client.StartRunAsync(new(Wire.Id(), imported.Id, "claude", "fixture metadata only", Provider: "codex", Settings: settings), imported));
                Check(posted.Count == 0, "Unsupported settings reached a legacy host."); Check((await client.GetStateAsync()).Connections.Single().Status == "connected");
                await client.StartRunAsync(new("legacy-default", imported.Id, "claude", "fixture metadata only", Provider: "codex"), imported); await Until(() => events.Any(e => e.SessionId == "legacy-default" && e.Status == "completed")); Check(bodies.Single().EnumerateObject().Count() == 4);
                modern = true; await client.RefreshAsync(connection.Id);
                foreach (var settings in selected) { var id = Wire.Id(); await client.StartRunAsync(new(id, imported.Id, "claude", "fixture metadata only", Provider: "codex", Settings: settings), imported); await Until(() => events.Any(e => e.SessionId == id && e.Status == "completed")); }
                Check(posted.Skip(1).Select(r => r.Settings).SequenceEqual(selected)); Check(posted.All(r => r.WorkspaceId == workspace.Id));
                foreach (var json in new[] { "{\"fastMode\":\"true\"}", "{\"networkAccess\":1}", "{\"webSearch\":null}" }) await Reject(() => Task.FromResult(JsonSerializer.Deserialize<RunSettings>(json, Wire.Json)));
            }
            finally { await client.DisposeAsync(); await server.DisposeAsync(); Directory.Delete(directory, true); }
        });
        await Test("remote attachment capability, large-byte transfer, authenticated limits and failed acceptance", async () =>
        {
            var directory = Temp(); var clientDirectory = Temp(); var workspace = new Workspace { Path = directory }; var modern = false; var shared = true; var events = new ConcurrentQueue<RunEvent>(); await using var catalog = Absent(); var baseline = await catalog.GetRuntimeAsync(); FakeManager? manager = null;
            Task<RuntimeInfo> Runtime() => Task.FromResult(baseline with { Providers = baseline.Providers.Select(p => p with { Available = true, Capabilities = p.Capabilities with { Attachments = modern } }).ToList() });
            await using var host = new RemoteServer("Attachments", [workspace.Id], () => shared ? [workspace] : [], _ => Task.FromResult(workspace), Runtime, emit => manager = new(emit), true);
            await using var client = new RemoteController(clientDirectory, () => [], _ => throw new ArgumentException(), Runtime, emit => new FakeManager(emit), events.Enqueue, testLoopback: true);
            try
            {
                await host.StartAsync(IPAddress.Loopback, 0); var state = await client.ConnectAsync(new("Attachments", host.Address, host.Token)); var connection = state.Connections.Single(); var imported = workspace with { Id = "attachment-workspace", Remote = new(connection.Id, workspace.Id, "Attachments") };
                var file = AttachmentSupport.Make("large.txt", Enumerable.Repeat((byte)'Z', 600000).ToArray()); var request = new StartRunRequest("large-attachment", imported.Id, "claude", "", Provider: "codex", Attachments: [file]);
                await Reject(() => client.StartRunAsync(request, imported)); Check(manager!.Started.IsEmpty, "Unsupported attachment reached host.");
                modern = true; await client.RefreshAsync(connection.Id); await client.StartRunAsync(request, imported); await Until(() => manager.Started.Count == 1); Check(manager.Started.Single().Attachments!.Single() == file); Check(manager.Started.Single().WorkspaceId == workspace.Id); await client.StopRunAsync(request.SessionId);
                var target = new PinnedRemote(new(host.Address), IPAddress.Loopback);
                await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, RemoteNetwork.Token(), HttpMethod.Post, "/v1/runs", new { request = request with { WorkspaceId = workspace.Id } }); });
                await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, host.Token, HttpMethod.Post, "/v1/runs", new { request = request with { WorkspaceId = workspace.Id, Attachments = [file with { MediaType = "image/png" }] } }); });
                var job = manager.Started.Single().SessionId;
                await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, host.Token, HttpMethod.Post, $"/v1/runs/{job}/stop", new { padding = new string('x', 600000) }); });
                Check(manager.Started.Count == 1);
                shared = false;
                await Reject(() => client.StartRunAsync(request with { SessionId = "rejected-upload" }, imported)); Check(manager.Started.Count == 1, "Rejected startup must not run.");
                await Until(() => !client.IsRemoteRun("rejected-upload"));
            }
            finally { await client.DisposeAsync(); await host.DisposeAsync(); Directory.Delete(directory, true); Directory.Delete(clientDirectory, true); }
        });
        await Test("two controllers run/stop real shell remotely and persist encrypted keys", async () =>
        {
            var directory = Temp(); var clientDirectory = Temp(); var workspace = new Workspace { Path = directory }; var events = new ConcurrentQueue<RunEvent>(); await using var catalog = Absent(); var protector = new Protector();
            await using var host = new RemoteController(directory, () => [workspace], _ => Task.FromResult(workspace), () => catalog.GetRuntimeAsync(), emit => new RunManager(_ => Task.FromResult(workspace), catalog, "", emit), _ => { }, testLoopback: true);
            var client = new RemoteController(clientDirectory, () => [], _ => throw new ArgumentException(), () => catalog.GetRuntimeAsync(), emit => new FakeManager(emit), events.Enqueue, protector, true);
            try
            {
                var shared = await host.StartSharingAsync(new([workspace.Id], 0)); var state = await client.ConnectAsync(new("Host", shared.Host.Address!, shared.Host.Token!)); var id = state.Connections.Single().Id;
                var imported = workspace with { Id = "remote-pane-workspace", Remote = new(id, workspace.Id, "Host") };
                await client.StartRunAsync(Shell("remote-echo", imported, "echo MIGHTY_REMOTE_OK"), imported); await Until(() => events.Any(e => e.SessionId == "remote-echo" && e.Status == "completed")); Check(events.Any(e => e.Entry?.Text.Contains("MIGHTY_REMOTE_OK") == true));
                var pid = Path.Combine(directory, "remote-pid"); await client.StartRunAsync(Shell("remote-long", imported, LongCommand(pid)), imported); await Until(() => File.Exists(pid)); var processId = int.Parse(await File.ReadAllTextAsync(pid)); await client.DisconnectAsync(id); await Until(() => !Alive(processId)); Check((await client.GetStateAsync()).Connections[0].Status == "disconnected"); await Task.Delay(600); Check((await client.GetStateAsync()).Connections[0].Status == "disconnected");
                var file = await File.ReadAllTextAsync(Path.Combine(clientDirectory, "remote-connections.json")); Check(!file.Contains(shared.Host.Token!)); await client.DisposeAsync();
                client = new(clientDirectory, () => [], _ => throw new ArgumentException(), () => catalog.GetRuntimeAsync(), emit => new FakeManager(emit), _ => { }, protector, true); Check((await client.GetStateAsync()).Host.Enabled == false); Check((await client.RefreshAsync(id)).Connections[0].Status == "connected");
                await host.StopSharingAsync(); var newShared = await host.StartSharingAsync(new([workspace.Id], shared.Host.Port)); var updated = await client.ConnectAsync(new("Host renamed", newShared.Host.Address!, newShared.Host.Token!)); Check(updated.Connections.Single().Id == id, "Rotating a host key must preserve imported workspace references");
            }
            finally { await client.DisposeAsync(); await host.DisposeAsync(); Directory.Delete(directory, true); Directory.Delete(clientDirectory, true); }
        });
        await Test("remote transport pins DNS, refuses redirects and response version mismatches", async () =>
        {
            var hits = 0; await using var server = await HttpHost.StartAsync(IPAddress.Loopback, 0, context => { hits++; if (context.Request.Path == "/redirect") { context.Response.Headers.Location = "/destination"; return HttpHost.ReplyAsync(context, 302, new { protocol = 1 }); } context.Response.StatusCode = 200; return context.Response.WriteAsync("{\"protocol\":1}"); });
            var target = new PinnedRemote(new($"http://pinned.invalid:{server.Address.Port}"), IPAddress.Loopback); await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, RemoteNetwork.Token(), HttpMethod.Get, "/redirect"); }); Check(hits == 1); await Reject(async () => { using var _ = await RemoteNetwork.RequestAsync(target, RemoteNetwork.Token(), HttpMethod.Get, "/version-missing"); }); Check(hits == 2);
        });
        await Test("legacy connection names keep imported IDs without migrating credentials", async () =>
        {
            var legacy = Temp(); var native = Temp(); await using var catalog = Absent();
            try
            {
                var original = JsonSerializer.Serialize(new { version = 1, connections = new[] { new { id = "imported-connection", name = "Legacy peer", address = "http://100.64.0.2:43137", encryptedToken = "old-electron-ciphertext" } } }, Wire.Json); var path = Path.Combine(legacy, "remote-connections.json"); await File.WriteAllTextAsync(path, original);
                await using var client = new RemoteController(native, () => [], _ => throw new ArgumentException(), () => catalog.GetRuntimeAsync(), emit => new FakeManager(emit), _ => { }, testLoopback: true, legacyDirectory: legacy);
                var state = await client.GetStateAsync(); Check(state.Connections.Single().Id == "imported-connection" && state.Connections.Single().Status == "disconnected"); await Reject(() => client.RefreshAsync("imported-connection")); Check(await File.ReadAllTextAsync(path) == original); Check(!(await File.ReadAllTextAsync(Path.Combine(native, "remote-connections.json"))).Contains("old-electron-ciphertext"));
            }
            finally { Directory.Delete(legacy, true); Directory.Delete(native, true); }
        });
        await Test("slash query parsing", SlashCommandVerification.QueryParsing);
        await Test("slash argument query", SlashCommandVerification.ArgumentQuery);
        await Test("slash builtins per provider", SlashCommandVerification.BuiltinsPerProvider);
        await Test("slash filter", SlashCommandVerification.Filter);
        await Test("slash frontmatter", SlashCommandVerification.Frontmatter);
        await Test("slash discovery from temp home and workspace", SlashCommandVerification.Discovery);
        await Test("slash 400 cap", SlashCommandVerification.CapAt400);
        await Test("slash palette opens on a query with matches", SlashPaletteVerification.Opens);
        await Test("slash palette closes with no match or no query", SlashPaletteVerification.Closes);
        await Test("slash palette highlight wraps and clamps", SlashPaletteVerification.HighlightMoves);
        await Test("slash palette chooses a plain command", SlashPaletteVerification.ChoosesPlainCommand);
        await Test("slash palette chooses an app action", SlashPaletteVerification.ChoosesAppAction);
        await Test("slash palette chooses an argument command", SlashPaletteVerification.ChoosesArgumentCommand);
        await Test("slash palette Esc closes without changing the draft", SlashPaletteVerification.EscapeClosesWithoutChangingTheDraft);
        await Test("slash palette rows and footer use the macOS copy", SlashPaletteVerification.RowsAndFooterUseMacCopy);
        await Test("slash palette leaves out app actions Windows cannot do", SlashPaletteVerification.LeavesOutActionsWindowsCannotDo);
        await Test("slash palette caps the list at 60 rows", SlashPaletteVerification.CapsRowsAt60);
        await Test("status line config follows Claude settings precedence and command-only entries", StatusLineVerification.ConfigFollowsPrecedenceAndOnlyCommandEntries);
        await Test("status line fingerprint matches macOS formula", StatusLineVerification.FingerprintMatchesMacOS);
        await Test("status line payload uses the CLI field names and transcript layout", StatusLineVerification.StatusLinePayloadUsesTheCLIsFieldNamesAndTranscriptLayout);
        await Test("status line payload nests cost and fast mode like the CLI", StatusLineVerification.StatusLinePayloadNestsCostAndFastModeLikeTheCLI);
        await Test("status line payload nests the context window like the CLI", StatusLineVerification.StatusLinePayloadNestsTheContextWindowLikeTheCLI);
        await Test("status line payload omits unknown sections like the CLI", StatusLineVerification.StatusLinePayloadOmitsUnknownSectionsLikeTheCLI);
        await Test("status line payload keys rate limits by canonical kind", StatusLineVerification.StatusLinePayloadKeysRateLimitsByCanonicalKind);
        await Test("status line payload has exactly the macOS top-level keys", StatusLineVerification.StatusLinePayloadHasExactlyTheMacOSTopLevelKeys);
        await Test("status line ANSI parsing keeps colours weight and strips other escapes", StatusLineVerification.AnsiParsingKeepsColoursWeightAndStripsEscapes);
        await Test("status line parses 256-colour and RGB like macOS", StatusLineVerification.AnsiParsingSupports256ColourAndRgbLikeMacOS);
        await Test("status line palette index maps to the xterm colours", StatusLineVerification.PaletteIndexMapsToXtermColours);
        await Test("status line trust rule requires fingerprint for workspace commands", StatusLineVerification.TrustRuleRequiresFingerprintForWorkspaceCommands);
        await Test("status line falls back to the user command while the workspace command is gated", StatusLineVerification.FallsBackToUserCommandWhileWorkspaceCommandIsGated);
        await Test("status line runner feeds stdin captures output and colour", StatusLineVerification.RunnerFeedsStdinCapturesOutputAndColour);
        await Test("status line runner enforces timeout", StatusLineVerification.RunnerEnforcesTimeout);
        await Test("status line shell follows Claude Code on Windows", StatusLineVerification.ShellFollowsClaudeCodeOnWindows);
        await Test("status line refresher debounces single run from rapid triggers", StatusLineVerification.StatusLineRefresherDebouncesSingleRunFromRapidTriggers);
        await Test("status line refresher generation counter discards stale results", StatusLineVerification.StatusLineRefresherGenerationCounterDiscardsStaleResult);
        await Test("status line refresher reruns when pending during a run", StatusLineVerification.StatusLineRefresherRerunsWhenPendingDuringARun);
        await Test("status line refresher ignores requests after close", StatusLineVerification.StatusLineRefresherIgnoresRequestsAfterClose);
        await Test("status line refresher never starts two commands at once", StatusLineVerification.StatusLineRefresherNeverStartsTwoCommandsAtOnce);
        await Test("status line refresher falls back to user command while workspace command is gated", StatusLineVerification.StatusLineRefresherFallsBackToUserCommandWhileWorkspaceIsGated);
        await Test("status line refresher trust unblocks the workspace command", StatusLineVerification.StatusLineRefresherTrustUnblocksWorkspaceCommand);
        await Test("status line refresher shows nothing for a disabled or missing entry", StatusLineVerification.StatusLineRefresherShowsNothingForDisabledOrMissingEntry);
        await Test("cli runner runs a real short command", CliRunnerVerification.RealShortCommand);
        await Test("cli runner kills a process that exceeds the timeout", CliRunnerVerification.KillsCommandThatExceedsTimeout);
        await Test("cli runner reports the exit code and error output of a failing command", CliRunnerVerification.ReportsExitCodeAndErrorOutput);
        await Test("cli runner caps the output it captures", CliRunnerVerification.CapsTheCapturedOutput);
        await Test("cli runner keeps arguments and output out of the log unredacted", CliRunnerVerification.KeepsSecretsOutOfTheLog);
        await Test("cli runner kills the whole process group when a command times out", CliRunnerVerification.TimeoutKillsTheWholeProcessGroup);
        await Test("cli runner cancellation stops the command and its children", CliRunnerVerification.CancellationStopsTheCommandAndItsChildren);
        await Test("strings match macOS", StringsVerification.MatchMacOS);
        await Test("tool permission handshake sends initialize before the prompt", ToolPermissionVerification.HandshakeRunsBeforeThePrompt);
        await Test("tool permission handshake failure and timeout fail closed", ToolPermissionVerification.HandshakeFailureAndTimeoutFailClosed);
        await Test("tool permission 이번만 허용 returns the original input once", ToolPermissionVerification.AllowOnceReturnsTheOriginalInput);
        await Test("tool permission 거부 never returns allow rules or settings", ToolPermissionVerification.DenyNeverReturnsAllow);
        await Test("tool permission channel accepts only can_use_tool and validates identifiers", ToolPermissionVerification.OnlyCanUseToolIsAcceptedAndIdentifiersAreValidated);
        await Test("tool permission channel caps waiting requests at 16", ToolPermissionVerification.PendingRequestsAreCappedAtSixteen);
        await Test("tool permission input too large to show completely is denied", ToolPermissionVerification.InputTooLargeToShowIsDenied);
        await Test("tool permission questionnaires and extra screens can only be denied", ToolPermissionVerification.QuestionnairesAndExtraScreensCanOnlyBeDenied);
        await Test("tool permission stopping the run settles every waiting request", ToolPermissionVerification.StoppingTheRunSettlesEveryWaitingRequest);
        await Test("tool permission bar shows the title summary reason path and count", ToolPermissionVerification.BarShowsTheTitleSummaryReasonPathAndCount);
        await Test("tool permission host prompts are added only for a Claude launch that can show the bar", ToolPermissionVerification.HostPromptsOnlyWhereTheBarExists);
        await Test("tool permission run launches with host prompts over stdio and answers one request", ToolPermissionVerification.RunLaunchesWithHostPromptsAndAnswersOneRequest);
        await Test("tool permission strings match macOS", StringsVerification.ToolPermissionsMatchMacOS);
        await Test("completion notification fires once for running then completed", CompletionNotificationVerification.FiresOnceAfterRunning);
        await Test("completion notification title includes workspace name", CompletionNotificationVerification.TitleIncludesWorkspaceName);
        await Test("completion notification title is session alone without workspace", CompletionNotificationVerification.TitleIsSessionAloneWithoutWorkspace);
        await Test("completion notification does not fire for error or stopped", CompletionNotificationVerification.DoesNotFireForErrorOrStopped);
        await Test("completion notification does not fire for completed without running", CompletionNotificationVerification.DoesNotFireForCompletedWithoutRunning);
        await Test("completion notification does not fire twice for the same run", CompletionNotificationVerification.DoesNotFireTwiceForSameRun);
        await Test("completion notification does not fire when preference is off", CompletionNotificationVerification.DoesNotFireWhenPreferenceOff);
        await Test("completion notification strings match macOS", StringsVerification.CompletionNotificationStringsMatchMacOS);
        await Test("completion notification smoke sends one fixture call and records sent", CompletionNotificationVerification.SmokeSendsOneFixtureCallAndRecordsSent);
        await Test("completion notification smoke records skipped with a reason", CompletionNotificationVerification.SmokeRecordsSkippedWithAReason);
        await Test("completion notification smoke failure after support is a failure", CompletionNotificationVerification.SmokeFailureAfterSupportIsAFailure);
        await Test("completion notification smoke keeps the saved state version at 1", CompletionNotificationVerification.SmokeKeepsTheSavedStateVersion);
        await Test("cli update reports a CLI that is not installed as skipped and installs nothing", CliUpdateVerification.MissingCliIsSkipped);
        await Test("cli update uses Claude Code's own update command for a native install", CliUpdateVerification.NativeClaudeUsesItsOwnUpdateCommand);
        await Test("cli update upgrades exactly the one winget package it found", CliUpdateVerification.WingetUpgradesExactlyThePackageItFound);
        await Test("cli update updates only the official npm package in its own prefix", CliUpdateVerification.NpmUpdatesOnlyTheOfficialPackageInItsPrefix);
        await Test("cli update skips a prerelease npm channel with the macOS sentence", CliUpdateVerification.PrereleaseNpmChannelIsSkipped);
        await Test("cli update skips an install method it does not recognise", CliUpdateVerification.UnknownInstallMethodIsSkipped);
        await Test("cli update reports a failing installer with a bounded diagnostic output", CliUpdateVerification.FailingInstallerIsReportedWithBoundedOutput);
        await Test("cli update runs one at a time, reports busy and cancels cleanly", CliUpdateVerification.SecondRequestIsBusyAndCancelStopsTheRun);
        await Test("cli update start-up pass covers every provider only when the switch is on", CliUpdateVerification.StartupPassCoversEveryProviderOnlyWhenTheSwitchIsOn);
        await Test("cli update coordinator tracks state through a complete run", CliUpdateVerification.CoordinatorTracksStateAndRunsInOrder);
        await Test("cli update coordinator refuses a second start while running", CliUpdateVerification.CoordinatorRefusesSecondStartWhileRunning);
        await Test("cli update coordinator begins automatic only once and only when the switch is on", CliUpdateVerification.CoordinatorBeginsAutomaticOnlyOnceAndOnlyWhenSwitchIsOn);
        await Test("cli update coordinator cancel stops the run and shutdown refuses new work", CliUpdateVerification.CoordinatorCancelStopsRunAndShutdownRefusesNew);
        await Test("cli update coordinator shows each result while the button reads 업데이트 중…", CliUpdateVerification.CoordinatorShowsEachResultWhileTheButtonReadsUpdating);
        await Test("cli update coordinator reports a failed provider and still updates the rest", CliUpdateVerification.CoordinatorReportsAFailedProviderAndStillUpdatesTheRest);
        await Test("cli update coordinator keeps the last run's results and finish time", CliUpdateVerification.CoordinatorKeepsTheLastRunResultsAndFinishTime);
        await Test("cli update strings match macOS", StringsVerification.CliUpdateStringsMatchMacOS);
        await Test("settings preferences missing key keeps default off and version stays 1", SettingsPreferencesVerification.MissingKeyKeepsDefaultOff);
        await Test("settings preferences explicit on and off persist across state store reloads", SettingsPreferencesVerification.ExplicitOnAndOffPersistAcrossReloads);
        await Test("settings preferences only JSON booleans enable the setting and malformed values keep sessions", SettingsPreferencesVerification.OnlyJsonBooleansEnableSettingAndMalformedValuesKeepSessions);
        await Test("settings sections appear in the macOS order with their titles", SettingsSectionsVerification.SectionsAppearInMacOrderWithTheirTitles);
        await Test("settings sections leave out features that are not on Windows yet", SettingsSectionsVerification.SectionsLeaveOutFeaturesNotOnWindowsYet);
        await Test("settings sections registering a section does not touch the others", SettingsSectionsVerification.RegisteringASectionDoesNotTouchTheOthers);
        await Test("settings sections smoke flips the CLI auto-update switch and restores it", SettingsSectionsVerification.SmokeFlipsTheAutoUpdateSwitchAndRestoresIt);
        await Test("settings sections smoke shows every fixture status and rejects a wrong screen", SettingsSectionsVerification.SmokeShowsEveryFixtureStatusAndRejectsAWrongScreen);
        await Test("cli account strings match macOS", StringsVerification.CliAccountStringsMatchMacOS);
        await Test("cli account claude and codex statuses expose account labels only", CliAccountVerification.ClaudeAndCodexStatusesExposeAccountLabelsOnly);
        await Test("cli account token never survives the read", CliAccountVerification.TokenNeverSurvivesTheRead);
        await Test("cli account gemini status and logout work on its account files", CliAccountVerification.GeminiStatusAndLogoutWorkOnItsAccountFiles);
        await Test("cli account commands are the CLIs own", CliAccountVerification.CommandsAreTheCLIsOwn);
        await Test("cli account coordinator reads statuses and calls logout", CliAccountVerification.CoordinatorReadsStatusesAndCallsLogout);
        await Test("cli account smoke shows fixture statuses and confirmation flow works", CliAccountVerification.SmokeShowsFixtureStatusesAndConfirmationFlowWorks);
        await Test("account usage strings match macOS", StringsVerification.AccountUsageStringsMatchMacOS);
        await Test("account usage secret never reaches a snapshot, a log line or an error", AccountUsageVerification.Secret);
        await Test("account usage refuses another host or a redirect", AccountUsageVerification.RefusesAnotherHostOrARedirect);
        await Test("account usage direct claude lookup is off by default", AccountUsageVerification.DirectLookupIsOffByDefault);
        await Test("account usage claude reads quota and profile from the credentials file", AccountUsageVerification.ClaudeReadsQuotaAndProfileFromTheCredentialsFile);
        await Test("account usage failure keeps the last known value and backs off", AccountUsageVerification.FailureKeepsTheLastKnownValueAndBacksOff);
        await Test("account usage codex is asked through its own app-server", AccountUsageVerification.CodexIsAskedThroughItsOwnAppServer);
        await Test("account usage closing the app cancels pending reads", AccountUsageVerification.ClosingTheAppCancelsPendingReads);
        await Test("app update ed25519 matches the RFC 8032 vectors", AppUpdateVerification.Ed25519MatchesTheRfc8032Vectors);
        await Test("app update strings match macOS", StringsVerification.AppUpdateStringsMatchMacOS);
        await Test("app update manifest accepts a fixture signature and refuses everything else", AppUpdateVerification.ManifestAcceptsAFixtureSignatureAndRefusesEverythingElse);
        await Test("app update manifest refuses an asset without sha256 or size", AppUpdateVerification.ManifestRefusesAnAssetWithoutSha256OrSize);
        await Test("app update version comparison orders releases and pre-releases", AppUpdateVerification.VersionComparisonOrdersReleasesAndPreReleases);
        await Test("app update without a public key there is no check at all", AppUpdateVerification.NoPublicKeyMeansNoCheckAtAll);
        await Test("app update a built-in address ignores a user address", AppUpdateVerification.ABuiltInAddressIgnoresAUserAddress);
        await Test("app update transport refuses a non-https hop", AppUpdateVerification.TransportRefusesANonHttpsHop);
        await Test("app update download verifies the bytes on disk and cleans up", AppUpdateVerification.DownloadVerifiesTheBytesOnDiskAndCleansUp);
        await Test("app update staging refuses an escaping entry or the wrong package", AppUpdateVerification.StagingRefusesAnEscapingEntryOrTheWrongPackage);
        await Test("app update replacement verifies again and replaces the install", AppUpdateVerification.ReplacementVerifiesAgainAndReplacesTheInstall);
        await Test("app update replacement leaves the install untouched when it cannot proceed", AppUpdateVerification.ReplacementLeavesTheInstallUntouchedWhenItCannotProceed);
        await Test("app update helper executable is inside the staged folder", AppUpdateVerification.AppUpdateHelperExecutableIsInsideTheStagedFolder);
        await Test("app update helper copies the staged folder to the install path", AppUpdateVerification.AppUpdateHelperCopiesTheStagedFolderToTheInstallPath);
        await Test("app update helper rolls back after a copy that fails half way", AppUpdateVerification.AppUpdateHelperRollsBackAfterACopyThatFailsHalfWay);
        await Test("app update helper waits for the app to quit before it moves anything", AppUpdateVerification.AppUpdateHelperWaitsForTheAppToQuitBeforeItMovesAnything);
        await Test("app update helper verifies the package again before the install is moved aside", AppUpdateVerification.AppUpdateHelperVerifiesThePackageAgainBeforeTheInstallIsMovedAside);
        await Test("app update helper keeps the backup until the new app has started", AppUpdateVerification.AppUpdateHelperKeepsTheBackupUntilTheNewAppHasStarted);
        await Test("app update automatic check happens at most once a day", AppUpdateVerification.AutomaticCheckHappensAtMostOnceADay);
        await Test("app update section shows the macOS copy for every phase", AppUpdateVerification.SectionShowsTheMacOSCopyForEveryPhase);
        await Test("app update pipeline runs from a fixture-signed manifest to a ready install plan", AppUpdateVerification.PipelineRunsFromAFixtureSignedManifestToAReadyInstallPlan);
        await Test("rename validation accepts valid names", RenameVerification.renameValidationAcceptsValidNames);
        await Test("rename validation refuses empty", RenameVerification.renameValidationRefusesEmpty);
        await Test("rename validation refuses over 120 characters", RenameVerification.renameValidationRefusesOver120);
        await Test("rename validation refuses control characters", RenameVerification.renameValidationRefusesControlCharacters);
        await Test("rename workspace stores name in snapshot", RenameVerification.renameWorkspaceStoresNameInSnapshot);
        await Test("rename session stores title in snapshot", RenameVerification.renameSessionStoresTitleInSnapshot);
        await Test("rename session title survives new output", RenameVerification.renameSessionTitleSurvivesNewOutput);
        await Test("rename name survives restart", RenameVerification.renameNameSurvivesRestart);
        await Test("rename rejects invalid name", RenameVerification.renameRejectsInvalidName);
        await Test("rename rejects unknown target", RenameVerification.renameRejectsUnknownTarget);
        await Test("rename via DesktopService persists name", RenameVerification.renameViaDesktopServicePersistsName);
        await Test("rename strings match macOS", RenameVerification.renameStringsMatchMacOS);
        await Test("Mod bridge authenticates metadata and rejects browser/secret/stale events", async () =>
        {
            var received = 0; await using var bridge = new ModBridge(); using var connection = await bridge.RegisterAsync(_ => Interlocked.Increment(ref received), CancellationToken.None); using var client = new HttpClient();
            async Task<HttpStatusCode> Post(string token, object body, bool browser = false)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, connection.Url); request.Headers.Add("Authorization", "Bearer " + token); if (browser) request.Headers.Add("Origin", "http://localhost"); request.Content = new StringContent(JsonSerializer.Serialize(body, Wire.Json), Encoding.UTF8, "application/json"); using var response = await client.SendAsync(request); return response.StatusCode;
            }
            var body = new { version = 1, runId = connection.Id, claudeSessionId = "claude-session", @event = "session.start" };
            Check(await Post(connection.Token, body) == HttpStatusCode.NoContent); Check(received == 1);
            Check(await Post(new string('0', 64), body) == HttpStatusCode.Unauthorized); Check(await Post(connection.Token, body, true) == HttpStatusCode.Forbidden);
            Check(await Post(connection.Token, new { version = 1, runId = connection.Id, claudeSessionId = "claude-session", @event = "session.start", prompt = "must never enter metadata" }) == HttpStatusCode.BadRequest);
            connection.Dispose(); Check(await Post(connection.Token, body) == HttpStatusCode.Unauthorized); Check(received == 1);
        });
        if (OperatingSystem.IsWindows())
        {
            await Test("Windows Job Object handles Unicode and kills descendants after parent exits", async () =>
            {
                var directory = Temp(); var pid = Path.Combine(directory, "descendant.pid");
                ChildProcess? tree = null; var failures = new List<Exception>(); var phase = "Unicode output"; var jobExitVerified = false;
                try
                {
                    var command = "echo 안녕하세요";
                    await using (var child = ChildProcess.Start(ChildProcess.StartInfo(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/s", "/c", command], directory), command))
                    {
                        child.Input.Close(); var output = await child.Output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        Check(await child.Completion.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "Unicode shell failed");
                        Check(output.Contains("안녕하세요"), "Unicode shell output changed: " + JsonSerializer.Serialize(output));
                    }
                    phase = "descendant startup";
                    var start = "start \"\" /b " + LongCommand(pid);
                    tree = ChildProcess.Start(ChildProcess.StartInfo(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/s", "/c", start], directory), start);
                    tree.Input.Close(); await Until(() => File.Exists(pid)); var processId = int.Parse(await File.ReadAllTextAsync(pid));
                    phase = "parent exit";
                    Check(await tree.Completion.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "Parent shell failed");
                    Check(Alive(processId), "The fixture descendant must outlive its parent before job disposal");
                    phase = "job disposal";
                    await tree.DisposeAsync(); tree = null;
                    Check(!Alive(processId), "Job disposal returned while its descendant was alive");
                    jobExitVerified = true;
                }
                catch (Exception error) { failures.Add(new InvalidOperationException("Windows child fixture failed during " + phase, error)); }
                finally
                {
                    // Always terminate the job before deleting its cwd, including assertion failures.
                    if (tree is not null) try { await tree.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
                    try
                    {
                        var cleanup = Stopwatch.StartNew();
                        while (true)
                        {
                            try { Directory.Delete(directory, true); break; }
                            // Only after the empty job and dead descendant assertions passed:
                            // Windows may briefly retain directory handles outside that job.
                            catch (IOException error) when (jobExitVerified && ((error.HResult & 0xffff) is 32 or 145) && cleanup.Elapsed < TimeSpan.FromSeconds(3))
                            { await Task.Delay(40); }
                        }
                    }
                    catch (Exception error) { failures.Add(new IOException("Windows fixture directory cleanup failed", error)); }
                }
                if (failures.Count > 0) throw new AggregateException("Windows child verification failed", failures);
            });
        }
        else { skipped++; Console.WriteLine("SKIP Windows Job Object / UTF-8 console (requires Windows)"); }
        Console.WriteLine($"Native Core: {passed} passed, {skipped} platform-specific skipped.");
    }
    private static bool Alive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
