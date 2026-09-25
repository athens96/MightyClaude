using System.Collections.Concurrent;

namespace MightyClaude.Core;

public interface IRunManager : IAsyncDisposable
{
    Task StartAsync(StartRunRequest request);
    Task StopAsync(string sessionId);
}
/// <param name="permissionRequested">
/// Set only by a host that can show the approval bar for this run. When it is
/// null — remote workspaces and every headless path — Claude keeps launching
/// with --permission-prompts none exactly as before. A request is handed to
/// this callback alone: it is never an event, so it never reaches a snapshot
/// or a remote peer.
/// </param>
public sealed class RunManager(Func<string, Task<Workspace>> resolveWorkspace, ProviderCatalog providers, string pluginDirectory, Action<RunEvent> emit, Action<ToolPermissionRequest>? permissionRequested = null) : IRunManager
{
    private sealed class Run(StartRunRequest request)
    {
        internal readonly StartRunRequest Request = request;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource? Accepted = request.Attachments is { Count: > 0 } ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal ChildProcess? Child;
        internal ClaudePermissionChannel? Permissions;
        internal Task Task = Task.CompletedTask;
        internal int Bytes;
        internal bool Truncated;
        internal readonly string ActivityId = Wire.Id();
        internal int ActivityBytes;
        internal volatile bool Finalizing, Finished;
    }
    private readonly ConcurrentDictionary<string, Run> runs = [];
    private readonly object lifecycle = new();
    private readonly ModBridge bridge = new();
    private bool disposed;
    public bool IsRunning(string id) => runs.ContainsKey(id);
    public Task StartAsync(StartRunRequest value)
    {
        var request = value.Validate();
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runs.Count >= 16) throw new InvalidOperationException(Locale.Get("run.error.tooManyConcurrent"));
            var run = new Run(request);
            if (!runs.TryAdd(request.SessionId, run)) throw new InvalidOperationException(Locale.Get("run.error.alreadyRunning"));
            run.Task = ExecuteAsync(run); return run.Accepted?.Task ?? Task.CompletedTask;
        }
    }
    private void Log(Run run, string kind, string text)
    {
        if (run.Finished || string.IsNullOrEmpty(text)) return;
        if (kind is "output" or "assistant")
        {
            if (run.Truncated) return;
            if (Interlocked.Add(ref run.Bytes, System.Text.Encoding.UTF8.GetByteCount(text)) > 2 * 1024 * 1024) { run.Truncated = true; emit(RunEvent.Log(run.Request.SessionId, "system", Locale.Get("run.error.outputTruncated"), run.Request.Provider)); return; }
        }
        if (kind == "assistant") { emit(RunEvent.Log(run.Request.SessionId, kind, text, run.Request.Provider)); return; }
        for (var at = 0; at < text.Length; at += 8192) emit(RunEvent.Log(run.Request.SessionId, kind, text.Substring(at, Math.Min(8192, text.Length - at)), run.Request.Kind == "shell" ? null : run.Request.Provider));
    }
    private void Activity(Run run, AgentActivity activity)
    {
        if (run.Finished || ActivitySupport.Normalize(activity) is not { } value) return;
        if (value.Output is { } output && Interlocked.Add(ref run.ActivityBytes, System.Text.Encoding.UTF8.GetByteCount(output)) > 2 * 1024 * 1024) value = value with { Output = null };
        if (value.Kind != "turn") emit(new(run.Request.SessionId, "log", new(value.Id, "system", value.Summary, Wire.Now(), value.Provider, value)));
        emit(new(run.Request.SessionId, "activity", Activity: value));
    }
    private async Task ExecuteAsync(Run run)
    {
        var request = run.Request; var token = run.Cancel.Token; ModBridge.Connection? mod = null; StagedAttachments? attachments = null; var input = request.Input; Exception? startFailure = null;
        var parser = new OutputParser(request.Provider, (kind, text) => Log(run, kind, text), id => emit(new(request.SessionId, "resume", ResumeId: id)), value => Activity(run, value), value => { if (!run.Finalizing) emit(new(request.SessionId, "usage", Usage: value)); }, run.ActivityId);
        ExecutionGraphTracker? tracker = null;
        if (request.Kind != "shell" && MightyGraphSupport.Providers.Contains(request.Provider))
            tracker = new ExecutionGraphTracker(Wire.Id(), request.Input, request.Provider, request.Model, _ => { });
        void Finish(string state)
        {
            run.Finalizing = true;
            parser.Flush(); parser.FinishActivities(state == "stopped");
            if (request.Kind != "shell") Activity(run, new(run.ActivityId, request.Provider, "turn", state, state == "completed" ? Locale.Get("run.activity.completed") : state == "stopped" ? Locale.Get("run.activity.stopped") : Locale.Get("run.activity.error")));
            tracker?.Finish(state);
            if (tracker?.BuildRun() is { } graphRun) emit(new(request.SessionId, "graph_run", GraphRun: graphRun));
            run.Finished = true; emit(RunEvent.State(request.SessionId, state));
        }
        try
        {
            var workspace = await resolveWorkspace(request.WorkspaceId); token.ThrowIfCancellationRequested();
            var environment = ProviderCatalog.QuietEnvironment(); string binary; IEnumerable<string> arguments; var interactive = false;
            if (request.Kind == "shell") { binary = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "C:\\Windows\\System32\\cmd.exe" : "/bin/sh"; arguments = OperatingSystem.IsWindows() ? ["/d", "/s", "/c", request.Input] : ["-c", request.Input]; }
            else
            {
                var command = await providers.FindAsync(request.Provider, token) ?? throw new InvalidOperationException(Locale.Get("run.error.cliNotInstalled", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(request.Provider) }));
                token.ThrowIfCancellationRequested();
                if (request.Provider == "claude")
                {
                    if (!ProviderCatalog.SupportsMods(command.Version)) throw new InvalidOperationException(Locale.Get("run.error.modsVersionRequired"));
                    if (!File.Exists(Path.Combine(pluginDirectory, ".claude-plugin", "plugin.json"))) throw new FileNotFoundException(Locale.Get("run.error.modsResourceMissing"));
                    mod = await bridge.RegisterAsync(value => { if (run.Finalizing || token.IsCancellationRequested || !runs.TryGetValue(request.SessionId, out var current) || !ReferenceEquals(current, run)) return; parser.ReceiveMod(value); if (tracker is not null) FeedMod(tracker, value); }, token);
                    environment["CLAUDE_CODE_ENABLE_FUNCTION_HOOKS"] = "1"; environment["MIGHTY_CLAUDE_BRIDGE_URL"] = mod.Url; environment["MIGHTY_CLAUDE_BRIDGE_TOKEN"] = mod.Token; environment["MIGHTY_CLAUDE_RUN_ID"] = mod.Id;
                    environment["MIGHTY_CLAUDE_ACTIVITY"] = "1"; environment["MIGHTY_CLAUDE_USAGE"] = "1"; environment["MIGHTY_CLAUDE_GRAPH"] = "1";
                    if (request.Settings!.Effort != "default") environment["CLAUDE_CODE_EFFORT_LEVEL"] = request.Settings.Effort;
                }
                if (request.Settings!.Effort != "default") { var runtime = await providers.GetRuntimeAsync(); token.ThrowIfCancellationRequested(); if (!ProviderCatalog.Efforts(request.Provider, request.Model, runtime.Providers.Single(p => p.Id == request.Provider).ModelCatalog, request.RegisteredModels).Contains(request.Settings.Effort)) throw new ArgumentException(Locale.Get("run.error.effortNotSupported")); }
                if (request.Attachments is { Count: > 0 } files) { attachments = await StagedAttachments.CreateAsync(files, token); token.ThrowIfCancellationRequested(); input = attachments.InputFor(request); }
                binary = command.Binary;
                var providerArguments = attachments?.ArgumentsFor(request, pluginDirectory) ?? ProviderCatalog.Arguments(request, pluginDirectory);
                // Host prompts over stdio only where the approval bar exists.
                interactive = permissionRequested is not null && request.Provider == "claude";
                if (interactive) ProviderInput.HostPrompts(providerArguments);
                arguments = command.Prefix.Concat(providerArguments);
            }
            token.ThrowIfCancellationRequested();
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(binary, arguments, workspace.Path, environment), OperatingSystem.IsWindows() && request.Kind == "shell" ? request.Input : null);
            run.Child = child; token.ThrowIfCancellationRequested();
            using var stop = token.Register(child.Kill);
            if (interactive)
            {
                // Fail closed: the prompt itself is only written after a
                // successful handshake, and a failure stops the run.
                run.Permissions = new ClaudePermissionChannel(request.SessionId, ProviderInput.PromptFrame(input),
                    text => { if (run.Finished) return; try { child.Input.Write(text); child.Input.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } },
                    value => { if (!run.Finished) permissionRequested!(value); },
                    (value, state) => parser.PermissionActivity(value, state),
                    message => Log(run, "system", message),
                    message => { Log(run, "error", message); run.Cancel.Cancel(); });
            }
            emit(RunEvent.State(request.SessionId, "running"));
            if (request.Kind != "shell") Activity(run, new(run.ActivityId, request.Provider, "turn", "running", Locale.Get("run.activity.running", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(request.Provider) })));
            var output = PumpAsync(child.Output, line =>
            {
                if (run.Finalizing) return;
                if (request.Kind == "shell") { Log(run, "output", line); return; }
                run.Permissions?.Receive(line);
                if (tracker is not null) try { using var doc = System.Text.Json.JsonDocument.Parse(line); tracker.Consume(doc.RootElement); } catch { }
                parser.Parse(line);
                // One-shot shutdown: EOF only after the CLI's own turn result,
                // so an approval reply is still possible while the turn runs.
                if (run.Permissions is not null && ClaudeStream.IsTurnResult(line)) CloseChannel(run, child);
            }, token);
            var error = PumpAsync(child.Error, line => Log(run, "output", line), token);
            if (interactive)
            {
                run.Permissions!.Start();
                _ = Task.Delay(TimeSpan.FromSeconds(15), token).ContinueWith(_ => run.Permissions?.InitializationTimedOut(), TaskScheduler.Default);
            }
            else
            {
                if (request.Kind != "shell") await child.Input.WriteAsync(input.AsMemory(), token);
                child.Input.Close();
            }
            run.Accepted?.TrySetResult();
            var code = await child.Completion.WaitAsync(token);
            child.Kill(); // Close descendants that inherited stdout after their parent exited.
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3), token); parser.Flush();
            CloseChannel(run, child);
            if (mod is { Received: 0 }) Log(run, "system", Locale.Get("run.warning.noModEvents"));
            Finish(code == 0 && !parser.Failed ? "completed" : "error");
        }
        catch (OperationCanceledException ex) { startFailure = ex; Finish("stopped"); }
        catch (Exception ex) { startFailure = ex; Log(run, "error", ex.Message); Finish(run.Cancel.IsCancellationRequested ? "stopped" : "error"); }
        finally
        {
            run.Permissions?.CancelAll(); run.Permissions = null;
            mod?.Dispose(); run.Child = null;
            try { if (attachments is not null) await attachments.DisposeAsync(); }
            catch (IOException) { Log(run, "error", Locale.Get("run.error.attachmentCleanupFailed")); }
            catch (UnauthorizedAccessException) { Log(run, "error", Locale.Get("run.error.attachmentCleanupForbidden")); }
            finally { runs.TryRemove(new KeyValuePair<string, Run>(request.SessionId, run)); if (startFailure is not null) run.Accepted?.TrySetException(startFailure); }
        }
    }
    /// <summary>Settles every waiting request and sends the CLI end-of-input.</summary>
    private static void CloseChannel(Run run, ChildProcess child)
    {
        if (run.Permissions is null) return;
        run.Permissions.CancelAll(); run.Permissions = null;
        try { child.Input.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }
    /// <summary>이번만 허용 / 거부 for one waiting request of one run. Nothing else is ever returned.</summary>
    public void RespondToToolPermission(string sessionId, string requestId, bool allow)
    {
        if (!runs.TryGetValue(sessionId, out var run) || run.Permissions is not { } channel)
            throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
        channel.Respond(requestId, allow);
    }
    private static void FeedMod(ExecutionGraphTracker tracker, System.Text.Json.JsonElement root)
    {
        try
        {
            if (root.Text("claudeSessionId") is not { } sessionId || root.Text("event") is not { } ev) return;
            ModGraphMetadata? graph = null;
            if (MetadataJson.Property(root, "graph") is { ValueKind: System.Text.Json.JsonValueKind.Object } gEl && MetadataJson.Integer(gEl, "version") == 1)
                graph = new ModGraphMetadata(1, gEl.Text("phase") ?? "running", gEl.Text("agentId"), gEl.Text("parentAgentId"), gEl.Text("parentToolUseId"), gEl.Text("name") ?? gEl.Text("agentType"), gEl.Text("agentType"), gEl.Text("model"), gEl.Text("input"), gEl.Text("output"));
            tracker.ReceiveMod(new ModMetadata(sessionId, ev, root.Text("tool"), root.Text("toolUseId"), root.Text("agentId"), graph));
        }
        catch { }
    }
    private static async Task PumpAsync(StreamReader reader, Action<string> consume, CancellationToken token) { await foreach (var line in OutputParser.LinesAsync(reader, token)) consume(line); }
    public async Task StopAsync(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) throw new ArgumentException(Locale.Get("run.error.invalidId"));
        if (!runs.TryGetValue(sessionId, out var run)) return;
        run.Cancel.Cancel(); run.Child?.Kill();
        try { await run.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { run.Child?.Kill(); throw new TimeoutException(Locale.Get("run.error.stopTimeout")); }
    }
    public async ValueTask DisposeAsync() { string[] active; lock (lifecycle) { disposed = true; active = runs.Keys.ToArray(); } await Task.WhenAll(active.Select(StopAsync)); await bridge.DisposeAsync(); }
}
