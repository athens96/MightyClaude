using System.Collections.Concurrent;

namespace MightyClaude.Core;

public interface IRunManager : IAsyncDisposable
{
    Task StartAsync(StartRunRequest request);
    Task StopAsync(string sessionId);
}
public sealed class RunManager(Func<string, Task<Workspace>> resolveWorkspace, ProviderCatalog providers, string pluginDirectory, Action<RunEvent> emit) : IRunManager
{
    private sealed class Run(StartRunRequest request)
    {
        internal readonly StartRunRequest Request = request;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource? Accepted = request.Attachments is { Count: > 0 } ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal ChildProcess? Child;
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
            if (runs.Count >= 16) throw new InvalidOperationException("동시 실행은 16개까지 가능합니다.");
            var run = new Run(request);
            if (!runs.TryAdd(request.SessionId, run)) throw new InvalidOperationException("이미 실행 중인 창입니다.");
            run.Task = ExecuteAsync(run); return run.Accepted?.Task ?? Task.CompletedTask;
        }
    }
    private void Log(Run run, string kind, string text)
    {
        if (run.Finished || string.IsNullOrEmpty(text)) return;
        if (kind is "output" or "assistant")
        {
            if (run.Truncated) return;
            if (Interlocked.Add(ref run.Bytes, System.Text.Encoding.UTF8.GetByteCount(text)) > 2 * 1024 * 1024) { run.Truncated = true; emit(RunEvent.Log(run.Request.SessionId, "system", "출력이 2MB를 넘어서 이후 표시를 생략합니다.", run.Request.Provider)); return; }
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
        void Finish(string state)
        {
            run.Finalizing = true;
            parser.Flush(); parser.FinishActivities(state == "stopped");
            if (request.Kind != "shell") Activity(run, new(run.ActivityId, request.Provider, "turn", state, state == "completed" ? "응답 완료" : state == "stopped" ? "실행 중지" : "실행 오류"));
            run.Finished = true; emit(RunEvent.State(request.SessionId, state));
        }
        try
        {
            var workspace = await resolveWorkspace(request.WorkspaceId); token.ThrowIfCancellationRequested();
            var environment = ProviderCatalog.QuietEnvironment(); string binary; IEnumerable<string> arguments;
            if (request.Kind == "shell") { binary = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "C:\\Windows\\System32\\cmd.exe" : "/bin/sh"; arguments = OperatingSystem.IsWindows() ? ["/d", "/s", "/c", request.Input] : ["-c", request.Input]; }
            else
            {
                var command = await providers.FindAsync(request.Provider, token) ?? throw new InvalidOperationException($"{ProviderCatalog.Name(request.Provider)} CLI를 설치하고 로그인해 주세요.");
                token.ThrowIfCancellationRequested();
                if (request.Provider == "claude")
                {
                    if (!ProviderCatalog.SupportsMods(command.Version)) throw new InvalidOperationException("Claude Mods 연결은 2.1.271 이상의 공개 API를 기준으로 합니다.");
                    if (!File.Exists(Path.Combine(pluginDirectory, ".claude-plugin", "plugin.json"))) throw new FileNotFoundException("Mighty bridge Mod 리소스를 찾을 수 없습니다.");
                    mod = await bridge.RegisterAsync(value => { if (run.Finalizing || token.IsCancellationRequested || !runs.TryGetValue(request.SessionId, out var current) || !ReferenceEquals(current, run)) return; parser.ReceiveMod(value); }, token);
                    environment["CLAUDE_CODE_ENABLE_FUNCTION_HOOKS"] = "1"; environment["MIGHTY_CLAUDE_BRIDGE_URL"] = mod.Url; environment["MIGHTY_CLAUDE_BRIDGE_TOKEN"] = mod.Token; environment["MIGHTY_CLAUDE_RUN_ID"] = mod.Id;
                    environment["MIGHTY_CLAUDE_ACTIVITY"] = "1"; environment["MIGHTY_CLAUDE_USAGE"] = "1"; environment["MIGHTY_CLAUDE_GRAPH"] = "0";
                    if (request.Settings!.Effort != "default") environment["CLAUDE_CODE_EFFORT_LEVEL"] = request.Settings.Effort;
                }
                if (request.Settings!.Effort != "default") { var runtime = await providers.GetRuntimeAsync(); token.ThrowIfCancellationRequested(); if (!ProviderCatalog.Efforts(request.Provider, request.Model, runtime.Providers.Single(p => p.Id == request.Provider).ModelCatalog).Contains(request.Settings.Effort)) throw new ArgumentException("선택한 모델의 지원 강도를 확인할 수 없습니다. Auto를 선택하세요."); }
                if (request.Attachments is { Count: > 0 } files) { attachments = await StagedAttachments.CreateAsync(files, token); token.ThrowIfCancellationRequested(); input = attachments.InputFor(request); }
                binary = command.Binary; arguments = command.Prefix.Concat(attachments?.ArgumentsFor(request, pluginDirectory) ?? ProviderCatalog.Arguments(request, pluginDirectory));
            }
            token.ThrowIfCancellationRequested();
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(binary, arguments, workspace.Path, environment), OperatingSystem.IsWindows() && request.Kind == "shell" ? request.Input : null);
            run.Child = child; token.ThrowIfCancellationRequested();
            using var stop = token.Register(child.Kill);
            emit(RunEvent.State(request.SessionId, "running"));
            if (request.Kind != "shell") Activity(run, new(run.ActivityId, request.Provider, "turn", "running", ProviderCatalog.Name(request.Provider) + " 실행 중"));
            var output = PumpAsync(child.Output, line => { if (run.Finalizing) return; if (request.Kind == "shell") Log(run, "output", line); else parser.Parse(line); }, token);
            var error = PumpAsync(child.Error, line => Log(run, "output", line), token);
            if (request.Kind != "shell") await child.Input.WriteAsync(input.AsMemory(), token);
            child.Input.Close();
            run.Accepted?.TrySetResult();
            var code = await child.Completion.WaitAsync(token);
            child.Kill(); // Close descendants that inherited stdout after their parent exited.
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3), token); parser.Flush();
            if (mod is { Received: 0 }) Log(run, "system", "Mod 연결 이벤트가 없습니다. 표시된 응답은 CLI 출력입니다.");
            Finish(code == 0 && !parser.Failed ? "completed" : "error");
        }
        catch (OperationCanceledException ex) { startFailure = ex; Finish("stopped"); }
        catch (Exception ex) { startFailure = ex; Log(run, "error", ex.Message); Finish(run.Cancel.IsCancellationRequested ? "stopped" : "error"); }
        finally
        {
            mod?.Dispose(); run.Child = null;
            try { if (attachments is not null) await attachments.DisposeAsync(); }
            catch (IOException) { Log(run, "error", "실행은 종료했지만 첨부 임시 사본을 삭제하지 못했습니다."); }
            catch (UnauthorizedAccessException) { Log(run, "error", "실행은 종료했지만 첨부 임시 사본을 삭제할 권한이 없습니다."); }
            finally { runs.TryRemove(new KeyValuePair<string, Run>(request.SessionId, run)); if (startFailure is not null) run.Accepted?.TrySetException(startFailure); }
        }
    }
    private static async Task PumpAsync(StreamReader reader, Action<string> consume, CancellationToken token) { await foreach (var line in OutputParser.LinesAsync(reader, token)) consume(line); }
    public async Task StopAsync(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) throw new ArgumentException("실행 ID가 올바르지 않습니다.");
        if (!runs.TryGetValue(sessionId, out var run)) return;
        run.Cancel.Cancel(); run.Child?.Kill();
        try { await run.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { run.Child?.Kill(); throw new TimeoutException("프로세스 종료가 지연되고 있습니다."); }
    }
    public async ValueTask DisposeAsync() { string[] active; lock (lifecycle) { disposed = true; active = runs.Keys.ToArray(); } await Task.WhenAll(active.Select(StopAsync)); await bridge.DisposeAsync(); }
}
