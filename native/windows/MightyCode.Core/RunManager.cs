using System.Collections.Concurrent;

namespace MightyCode.Core;

public interface IRunManager : IAsyncDisposable
{
    Task StartAsync(StartRunRequest request);
    Task StopAsync(string sessionId);
}
/// <summary>
/// What a sharing host drives its clients' runs through (M/Remote/RemoteHostService.swift's ProcessRunner): start, stop and the answers
/// to a run's tool requests, questionnaires and plans, each by the host's own session id. A client's steer is refused without it.
/// </summary>
public interface IRemoteRunner : IAsyncDisposable
{
    Task StartAsync(StartRunRequest request);
    Task StopAsync(string sessionId);
    void RespondToToolPermission(string sessionId, string requestId, bool allow);
    void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers);
    PlanRecord AnswerPlan(string sessionId, string requestId, PlanDecision decision);
}
/// <summary>
/// Where a host's runner hands a run's events, tool requests and graph nodes (by session id), and where it looks its workspace up:
/// <paramref name="Workspace"/> finds only a workspace the sharing serves now, at its shared path, so the runner's own look-up at launch
/// never reaches another folder.
/// </summary>
public sealed record RemoteRunnerSinks(Action<RunEvent> Event, Action<ToolPermissionRequest> Permission, Action<string, ExecutionGraphNode> GraphNode, Func<string, Task<Workspace>> Workspace);
/// <param name="permissionRequested">
/// Set only by a host that can show the approval bar for this run. When it is
/// null — every headless path — Claude keeps launching with
/// --permission-prompts none exactly as before. A request is handed to this
/// callback alone: it is never an event, so it never reaches a snapshot.
/// </param>
/// <param name="images">The picture cache agent runs write what they show into (macOS ProcessRunner.imageCache); null leaves pictures out.</param>
public sealed class RunManager(Func<string, Task<Workspace>> resolveWorkspace, ProviderCatalog providers, string pluginDirectory, Action<RunEvent> emit, Action<ToolPermissionRequest>? permissionRequested = null, AgentImageCache? images = null) : IRunManager, IRemoteRunner
{
    private sealed class Run(StartRunRequest request, bool awaited)
    {
        internal readonly StartRunRequest Request = request;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource? Accepted = awaited || request.Attachments is { Count: > 0 } ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal ChildProcess? Child;
        internal AgentIOBinding? PaneBinding;
        internal ClaudePermissionChannel? Permissions;
        internal CodexApprovalChannel? CodexPermissions;
        /// <summary>An ACP agent's run (opencode, Hermes): the JSON-RPC channel its stdio carries.</summary>
        internal AcpChannel? Acp;
        /// <summary>A pi run's extension dialogs (its <c>ask_user</c> tool among them) as the pane's question card.</summary>
        internal PiDialogChannel? PiDialogs;
        /// <summary>The ACP prompt was answered and the runner ended the agent's server itself (not a failure).</summary>
        internal volatile bool AcpEnding;
        /// <summary>stdin of an ACP run was closed after its prompt's answer.</summary>
        internal int AcpInputClosed;
        internal bool ProtocolFailed;
        internal bool AuthenticationFailure;
        /// <summary>The latest Claude message the CLI marked as a lost sign-in, until a result ends its turn.</summary>
        internal string? AuthenticationFailureText;
        internal ExecutionGraphTracker? Tracker;
        /// <summary>A steer opened a new turn: background work no longer stands alone (<see cref="BackgroundTaskTracker.Reopen"/>).</summary>
        internal Action? TurnReopened;
        internal Task Task = Task.CompletedTask;
        internal int Bytes;
        internal bool Truncated;
        internal readonly string ActivityId = Wire.Id();
        internal int ActivityBytes;
        internal volatile bool Finalizing, Finished;
        // The user cancelled a plan: the run ends stopped however the CLI ends it.
        internal volatile bool PlanCancelled;
        // stdin and the approval channel close only once the request's own result is in and no background
        // task runs, so follow-up turns can still ask and new input can join (macOS ManagedProcess.settleInput).
        internal volatile bool TurnResultSeen;
        internal long LastOutputTicks = Environment.TickCount64;
        internal readonly object InputGate = new();
        internal OutputParser? Parser;
        /// <summary>pi's stdin was closed after its turn ended (or never opened): no steer follows.</summary>
        internal bool PiInputClosed;
        /// <summary>A leaf lock: a graph node is handed to the host and the last status is emitted under it, never both at once.</summary>
        internal readonly object NodeGate = new();
    }
    /// <summary>After the last background task ends, how long a silent CLI may keep stdin before the runner closes it.</summary>
    public TimeSpan BackgroundIdleClose { get; set; } = TimeSpan.FromSeconds(120);
    public Func<StartRunRequest, Workspace, AgentIOBinding?>? AgentIOBindingFactory { get; set; }
    /// <summary>The hidden switch, read at each run start (M/ProcessRunner.swift:564-565): while it is on, a Claude pane's run also gets the delegation server.</summary>
    public Func<bool> DelegationEnabled { get; set; } = DelegationSwitch.IsOn;
    /// <summary>The app's side of the delegation server, which each run's binding is connected to; null leaves every delegation call unanswered.</summary>
    public IDelegationRequestHandler? DelegationHandler { get; set; }
    /// <summary>Where a Claude run's file originals are kept (<see cref="FileOriginalStore"/>); null keeps none.</summary>
    public FileOriginalStore? FileOriginals { get; set; }
    /// <summary>Every execution-graph node of a run as it changes, by session id: a sharing host sends them as the Mac's "graph" events
    /// (M/ProcessRunner.swift's graph callback). A node is handed over only before the run's last status is emitted, never after it.
    /// The callback is called under a per-run lock: it must not throw, block, or call back into this runner. One that throws loses
    /// that node (traced by type) and the run goes on.</summary>
    public Action<string, ExecutionGraphNode>? GraphNodes { get; set; }
    /// <summary>A sharing host's runner: a shell request runs in Windows PowerShell (RemoteCommandShell) instead of cmd.exe. Read once per run.</summary>
    public bool PowerShellCommands { get; set; }
    private readonly ConcurrentDictionary<string, Run> runs = [];
    private readonly object lifecycle = new();
    private readonly ModBridge bridge = new();
    private bool disposed;
    public bool IsRunning(string id) => runs.ContainsKey(id);
    public Task StartAsync(StartRunRequest value) => Begin(value, false);
    /// <summary>A client's run works in the shared folder alone, and with no local grants: it never brings a worktree folder of its own
    /// or a local style's tool permissions.</summary>
    Task IRemoteRunner.StartAsync(StartRunRequest value) =>
        value.WorkingFolder is null && value.DelegationWorktreeRoot is null && value.StyleAutoAllow is null ? StartAsync(value) : throw new ArgumentException(Locale.Get("wire.startRun.invalidFormat"));
    /// <summary>
    /// Starts the run and returns once its process is up with its input open, or throws why it never got that far
    /// (M/AppStore.swift:1049 startReachedRunner). A one-shot run has its prompt written by then. A stream-json run has only begun
    /// its initialize handshake: its prompt is written after the CLI answers it, so a CLI that never answers never gets it.
    /// </summary>
    public Task StartReachedAsync(StartRunRequest value) => Begin(value, true);
    private Task Begin(StartRunRequest value, bool awaited)
    {
        var request = value.Validate();
        if (request.Provider == "codex" && request.Settings!.PermissionMode == "onRequest" && permissionRequested is null)
            throw new InvalidOperationException("Codex approval requests require a local approval surface.");
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runs.Count >= 16) throw new InvalidOperationException(Locale.Get("run.error.tooManyConcurrent"));
            var run = new Run(request, awaited);
            if (!runs.TryAdd(request.SessionId, run)) throw new InvalidOperationException(Locale.Get("run.error.alreadyRunning"));
            run.Task = Task.Run(() => ExecuteAsync(run)); return run.Accepted?.Task ?? Task.CompletedTask;
        }
    }
    private void Log(Run run, string kind, string text)
    {
        if (run.Finished || string.IsNullOrEmpty(text)) return;
        text = run.PaneBinding?.Redact(text) ?? text;
        // Antigravity's failures arrive as error lines (its last one decides, as on the Mac); its stderr and plain stdout lines only raise the signal.
        if (kind == "error" && run.Request.Provider == "gemini") run.AuthenticationFailure = CliAuthFailure.Antigravity(text);
        else if (kind == "error" && CliAuthFailure.Matches(run.Request.Provider, text)) run.AuthenticationFailure = true;
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
        var request = run.Request; var token = run.Cancel.Token; var codexApprovals = request.Provider == "codex" && request.Settings!.PermissionMode == "onRequest"; ModBridge.Connection? mod = null; StagedAttachments? attachments = null; var input = request.Input; Exception? startFailure = null;
        var startedAt = DateTimeOffset.UtcNow;
        ExecutionGraphTracker? tracker = null;
        // An ACP run's request as launched (a Hermes session that cannot be continued is started anew) and Hermes's clarify tool.
        StartRunRequest? acpLaunch = null; string? hermesNotice = null;
        (HermesQuestionTool.Outcome? First, IReadOnlyList<string>? List, bool Asks) hermesQuestions = (null, null, false);
        var graphDirty = 0;
        var graphGate = new object();
        CodexSessionWatcher? watcher = null;
        var watcherDisabled = false;
        string? codexHome = null;
        // Where this Antigravity run's sub-agents keep their transcripts, and the watcher that reads them once one starts.
        string? antigravityBrain = null;
        AntigravitySubagentWatcher? antigravityWatcher = null;
        using var graphCancel = new CancellationTokenSource();
        Task graphTask = Task.CompletedTask;
        var parser = new OutputParser(request.Provider, (kind, text) => Log(run, kind, text), id => emit(new(request.SessionId, "resume", ResumeId: id)), value => Activity(run, value), value => { if (!run.Finalizing) emit(new(request.SessionId, "usage", Usage: value)); }, run.ActivityId,
            images: request.Kind == "shell" ? null : images, imageEntry: entry => { if (!run.Finished) emit(new(request.SessionId, "log", entry)); },
            requestedResume: request.Provider == "gemini" ? request.ResumeId : null, piReply: data => { lock (run.InputGate) { if (run.Finished || run.PiInputClosed || run.Child is not { } pi) return; try { pi.Input.Write(data); pi.Input.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } } }, answer: text => tracker?.AntigravityAnswer(text),
            // The card goes out before the run's last status (M/ProcessRunner.swift antigravityPermission).
            antigravityPermission: card => { if (!run.Finished && request.Kind == "claude") emit(new(request.SessionId, "antigravityPermission", AntigravityPermission: card)); },
            graphActivity: (value, toolId) => tracker?.Activity(value, toolId) == true,
            graphImages: (entry, toolId, parentToolId) => tracker?.Images(entry, toolId, parentToolId) == true);
        if (request.Kind != "shell" && MightyGraphSupport.Providers.Contains(request.Provider))
            tracker = new ExecutionGraphTracker(Wire.Identifier(request.GraphRunId) ? request.GraphRunId! : Wire.Id(), request.Input, request.Provider, PhaseModelPreferences.EffectiveModel(request), node => { Interlocked.Exchange(ref graphDirty, 1); HandOver(run, node); });
        run.Tracker = tracker;
        // Claude's (or Codex's) execution checklist and Claude's background tasks (ClaudePlanMode.cs).
        var planGate = new object();
        var todoTracker = new TodoProgressTracker(request.TodoProgress);
        // The list names the request whose stream changed it: the diagram draws it only over that request's run.
        var todoRunId = tracker?.RunID ?? (Wire.Identifier(request.GraphRunId) ? request.GraphRunId : null);
        run.Parser = parser;
        // pi's dialogs reach the pane's question card where a pane can answer it; a headless run cancels them as before.
        if (request.Provider == "pi" && request.Kind != "shell" && permissionRequested is not null)
        {
            var dialogs = new PiDialogChannel(request.SessionId,
                text => { lock (run.InputGate) { if (run.Finished || run.PiInputClosed || run.Child is not { } pi) return; try { pi.Input.Write(text); pi.Input.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } } },
                value => { if (!run.Finished) permissionRequested(value); },
                (value, state) => parser.PermissionActivity(value, state),
                message => Log(run, "system", message),
                // pi answers a dialog with a deadline itself when it passes: the card goes then too.
                (id, seconds) => _ = Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 86_400))).ContinueWith(_ => run.PiDialogs?.TimedOut(id), TaskScheduler.Default));
            run.PiDialogs = dialogs; parser.PiDialogs = dialogs;
        }
        var backgroundTracker = new BackgroundTaskTracker();
        run.TurnReopened = () => { lock (planGate) if (!run.Finished && backgroundTracker.Reopen() is { } work) emit(new(request.SessionId, "background", Background: work)); };
        void ReadPlanMode(System.Text.Json.JsonElement frame)
        {
            lock (planGate)
            {
                if (run.Finished) return;
                if (todoTracker.Consume(frame) is { } progress) emit(new(request.SessionId, "todos", Todos: progress with { RunId = todoRunId }));
                if (request.Provider != "claude") return;
                if (backgroundTracker.Consume(frame) is { } work) emit(new(request.SessionId, "background", Background: work));
                if (frame.Text("type") == "result" && !ClaudeStream.IsNotificationResult(frame) && MetadataJson.Property(frame, "parent_tool_use_id").ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null
                    && backgroundTracker.TurnEnded() is { } ended) emit(new(request.SessionId, "background", Background: ended));
            }
        }
        void ReadChildren(bool closing)
        {
            // agy's stream names each sub-agent's conversation but not its work: once the run starts one, the sub-agents' own
            // transcripts are read about once a second until the run ends (M/ProcessRunner.swift watchAntigravitySubagents).
            if (!watcherDisabled && tracker is not null && request.Provider == "gemini" && antigravityBrain is not null)
            {
                try
                {
                    var known = tracker.AntigravitySubagents;
                    if (antigravityWatcher is null && known.Count > 0) antigravityWatcher = new(antigravityBrain, run.ActivityId, tracker.AntigravityConversation);
                    if (antigravityWatcher is not null) foreach (var child in closing ? antigravityWatcher.Finish(known) : antigravityWatcher.Poll(known)) tracker.AntigravitySession(child);
                }
                // Transcript observation is optional; a file changing under it must never fail the actual CLI turn.
                catch (Exception ex) when (ex is not OutOfMemoryException) { watcherDisabled = true; antigravityWatcher = null; }
                return;
            }
            if (watcherDisabled || tracker is null || request.Provider != "codex" || codexHome is null) return;
            try
            {
                if (watcher is null && tracker.CodexRootThread is { } root && Guid.TryParseExact(root, "D", out _))
                    watcher = new(codexHome, root, startedAt, tracker.RunID);
                if (watcher is not null) foreach (var child in closing ? watcher.Finish() : watcher.Poll()) tracker.CodexSession(child);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Session-file observation is optional; a concurrent file change
                // or malformed record must never fail the actual CLI turn.
                watcherDisabled = true;
                try { watcher?.Dispose(); } catch (IOException) { }
                watcher = null;
            }
        }
        void Finish(string state)
        {
            run.Finalizing = true; graphCancel.Cancel();
            // The process ended: a background task the CLI never reported ending is unknown.
            if (request.Provider == "claude" && request.Kind != "shell") lock (planGate) if (!run.Finished && backgroundTracker.Finish() is { } final) emit(new(request.SessionId, "background", Background: final));
            lock (graphGate)
            {
                parser.Flush(); parser.FinishActivities(state == "stopped");
                if (request.Kind != "shell") Activity(run, new(run.ActivityId, request.Provider, "turn", state, state == "completed" ? Locale.Get("run.activity.completed") : state == "stopped" ? Locale.Get("run.activity.stopped") : Locale.Get("run.activity.error")));
                ReadChildren(true);
                tracker?.Finish(state);
                if (tracker?.BuildRun() is { } graphRun) emit(new(request.SessionId, "graph_run", GraphRun: graphRun));
                // Each notice run its background work opened (BackgroundNoticeRuns) is a run of its own.
                foreach (var notice in tracker?.TakeNoticeRuns() ?? []) emit(new(request.SessionId, "graph_run", GraphRun: notice));
                // pi's sign-in failure is read from its RPC stream, where a later message or a retry clears it (OutputParser.AuthenticationFailure).
                lock (run.NodeGate) { run.Finished = true; emit(RunEvent.State(request.SessionId, state, state == "error" && (run.AuthenticationFailure || parser.AuthenticationFailure) ? "authentication" : null)); }
            }
        }
        // The live graph as it is now, when it changed since the last one sent; false once the run is ending (Finish sends the last).
        bool PublishGraph(bool readChildren)
        {
            lock (graphGate)
            {
                if (run.Finalizing || run.Finished) return false;
                if (readChildren) ReadChildren(false);
                if (Interlocked.Exchange(ref graphDirty, 0) != 0 && tracker?.BuildRun(includeRunning: true) is { } live)
                {
                    emit(new(request.SessionId, "graph_run", GraphRun: live));
                    foreach (var notice in tracker.TakeNoticeRuns(includeRunning: true)) emit(new(request.SessionId, "graph_run", GraphRun: notice));
                }
                return true;
            }
        }
        async Task PublishGraphs()
        {
            try
            {
                while (!graphCancel.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), graphCancel.Token);
                    if (!PublishGraph(true)) return;
                }
            }
            catch (OperationCanceledException) when (graphCancel.IsCancellationRequested) { }
        }
        try
        {
            // The request's diagram block appears the moment the run starts (natural submit A3), not a second later with the loop's
            // first pass: before the workspace, the CLI or the environment is looked up.
            if (tracker is not null) PublishGraph(false);
            var workspace = await resolveWorkspace(request.WorkspaceId); token.ThrowIfCancellationRequested();
            // A remote workspace's path is the host's: it never becomes a local working folder (M/ProcessRunner.start).
            if (workspace.Remote is not null) throw new InvalidOperationException(Locale.Get("run.error.approvedWorkspaceOnly"));
            // A delegated child's run, its terminal tool and its MCP binding work in its worktree folder, never in its parent's checkout (M/AppStore.swift:1001-1002).
            if (request.WorkingFolder is { } folder) workspace = Directory.Exists(folder) ? workspace with { Path = folder } : throw new DirectoryNotFoundException(Locale.Get("run.blocked.worktreeMissing"));
            parser.ImageRoot = workspace.Path;
            // A Claude run's Edit, Write and MultiEdit results name each file's content before the run changed it; the first of each
            // file is kept under the run's graph id for the source preview's 변경사항 (FileChanges.cs).
            if (request.Provider == "claude" && request.Kind == "claude" && tracker is not null && FileOriginals is { } originals)
            {
                var originalsRoot = workspace.Path; var graphRunId = tracker.RunID;
                parser.FileOriginal = (path, original) => originals.Capture(request.SessionId, graphRunId, originalsRoot, path, original);
            }
            await providers.RefreshEnvironmentAsync(true, token);
            var environment = ProviderCatalog.QuietEnvironment();
            codexHome = request.Provider == "codex" ? CliAccountSupport.CodexHome(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment) : null;
            antigravityBrain = request.Provider == "gemini" ? AntigravityRecords.Brain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) : null;
            if (tracker is not null) graphTask = PublishGraphs();
            string binary; IEnumerable<string> arguments; var interactive = false;
            // Read once: the launch below must quote the arguments PowerShell was given, never hand its text to cmd /s /c.
            var powerShell = request.Kind == "shell" && PowerShellCommands && OperatingSystem.IsWindows();
            if (powerShell)
            {
                (binary, var shellArguments) = RemoteCommandShell.Launch(request.Input, Environment.GetFolderPath(Environment.SpecialFolder.System));
                arguments = shellArguments;
                // The command runs in the shared folder: nothing there may stand in for a program it names.
                environment[WindowsCommand.NoCurrentDirectorySearch] = "1";
            }
            else if (request.Kind == "shell") { binary = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "C:\\Windows\\System32\\cmd.exe" : "/bin/sh"; arguments = OperatingSystem.IsWindows() ? ["/d", "/s", "/c", request.Input] : ["-c", request.Input]; }
            else
            {
                var command = await providers.FindAsync(request.Provider, token) ?? throw new InvalidOperationException(Locale.Get("run.error.cliNotInstalled", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(request.Provider) }));
                token.ThrowIfCancellationRequested();
                if (codexApprovals && !ProviderCatalog.SupportsCodexApprovals(command.Version)) throw new InvalidOperationException("Codex approval requests require stable Codex CLI 0.153.4 or later.");
                // OpenCode 1 (`opencode-ai`) shares the command but not the files, permissions or credentials: it is replaced, not run.
                if (request.Provider == "opencode" && !OpencodeAgent.IsSupported(command.Version)) throw new InvalidOperationException(Locale.Get("run.error.opencodeV1", new Dictionary<string, string> { ["version"] = command.Version ?? "" }));
                // Hermes asks questions only with its clarify tool on for editor sessions: looked at once per launch.
                if (request.Provider == "hermes") hermesQuestions = await HermesQuestionTool.EnsureAsync(command, environment, workspace.Path, token);
                if (request.Settings!.Effort != "default" || PhaseModelPreferences.MainEffort(request) is not null)
                {
                    var runtime = await providers.GetRuntimeAsync(workingDirectory: workspace.Path); token.ThrowIfCancellationRequested();
                    var catalog = runtime.Providers.Single(p => p.Id == request.Provider).ModelCatalog;
                    request = PhaseModelPreferences.ResolveEffort(request, catalog);
                    // A level the list does not name for the model is refused; a Haiku model that takes none, in Haiku's words.
                    if (ProviderCatalog.EffortRefusal(request.Provider, PhaseModelPreferences.EffectiveModel(request), request.Settings!.Effort, catalog, request.RegisteredModels) is { } refusal)
                        throw new ArgumentException(refusal);
                }
                if (request.Provider == "claude")
                {
                    if (!ProviderCatalog.SupportsMods(command.Version)) throw new InvalidOperationException(Locale.Get("run.error.modsVersionRequired"));
                    if (!File.Exists(Path.Combine(pluginDirectory, ".claude-plugin", "plugin.json"))) throw new FileNotFoundException(Locale.Get("run.error.modsResourceMissing"));
                    // Attribute each Mod's tool to its child before the parser emits the corresponding activity.
                    mod = await bridge.RegisterAsync(value => { if (run.Finalizing || token.IsCancellationRequested || !runs.TryGetValue(request.SessionId, out var current) || !ReferenceEquals(current, run)) return; if (tracker is not null) FeedMod(tracker, value); parser.ReceiveMod(value); }, token, graphEnabled: true);
                    environment["CLAUDE_CODE_ENABLE_FUNCTION_HOOKS"] = "1"; environment["MIGHTY_CLAUDE_BRIDGE_URL"] = mod.Url; environment["MIGHTY_CLAUDE_BRIDGE_TOKEN"] = mod.Token; environment["MIGHTY_CLAUDE_RUN_ID"] = mod.Id;
                    environment["MIGHTY_CLAUDE_ACTIVITY"] = "1"; environment["MIGHTY_CLAUDE_USAGE"] = "1"; environment["MIGHTY_CLAUDE_GRAPH"] = "1";
                    if (request.Settings!.Effort != "default") environment["CLAUDE_CODE_EFFORT_LEVEL"] = request.Settings.Effort;
                }
                if (request.Attachments is { Count: > 0 } files) { attachments = await StagedAttachments.CreateAsync(files, token); token.ThrowIfCancellationRequested(); input = attachments.InputFor(request); }
                binary = command.Binary;
                if (request.Provider == "pi" && attachments is null) input = ProviderInput.PiStartup(input);
                // pi exits on a session it cannot find (one that never got an answer is not written): such a pane starts a new session.
                var launch = request.Provider == "pi" && request.ResumeId is { } piResume && PiAgent.SessionFile(piResume, workspace.Path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment) is null ? request with { ResumeId = null } : request;
                // Hermes fixes `--yolo` when a session starts: a session that started with it is not continued by a pane that asks (it would not
                // ask), one that started without it keeps asking; a session whose first request never got an answer is not continued (it keeps
                // the provider it was made with and fails again). A record that cannot be read changes nothing (M/ProcessRunner.swift).
                if (request.Provider == "hermes" && launch.ResumeId is { } hermesResume)
                {
                    var database = HermesSessions.StateDatabase(environment);
                    var (resumeId, notice) = HermesAgent.Resume(hermesResume, request.Settings!.PermissionMode, HermesSessions.MessageCount(database, hermesResume), HermesSessions.StartedWithFullAccess(database, hermesResume));
                    launch = launch with { ResumeId = resumeId };
                    if (notice is not null) hermesNotice = Locale.Get(notice);
                }
                acpLaunch = launch;
                // OpenCode 2's rules for this run only, read after the user's own config; Hermes's `--yolo` for a session this run creates.
                if (request.Provider == "opencode") environment["OPENCODE_CONFIG_CONTENT"] = OpencodeAgent.ConfigContent(request.Settings!.PermissionMode, AgentFiles.Find(OpencodeAgent.QuestionInstructionsName));
                if (request.Provider == "hermes") HermesAgent.Apply(request.Settings!.PermissionMode, environment);
                var providerArguments = codexApprovals || ProviderCatalog.IsAcp(request.Provider) ? ProviderCatalog.Arguments(launch, pluginDirectory) : attachments?.ArgumentsFor(launch, pluginDirectory) ?? ProviderCatalog.Arguments(launch, pluginDirectory);
                if (request.Provider is "claude" or "codex" && AgentIOBindingFactory?.Invoke(request, workspace) is { } binding)
                {
                    run.PaneBinding = binding;
                    binding.ConnectDelegation(DelegationHandler, DelegationProviders.AttachesDelegation(request.Kind, request.Provider, request.Settings!.PermissionMode) && DelegationEnabled(), request.PermissionModeOverride ?? request.Settings!.PermissionMode);
                    foreach (var pair in binding.Environment) environment[pair.Key] = pair.Value;
                    binding.AppendArguments(providerArguments);
                }
                // Host prompts over stdio only where the approval bar exists.
                interactive = permissionRequested is not null && request.Provider == "claude";
                if (interactive) ProviderInput.HostPrompts(providerArguments);
                // The CLI's authoritative "turn over, no background agent left" signal.
                if (interactive) environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1";
                arguments = command.Prefix.Concat(providerArguments);
            }
            token.ThrowIfCancellationRequested();
            await using var child = request.Kind == "shell" ? ChildProcess.Start(ChildProcess.StartInfo(binary, arguments, workspace.Path, environment), OperatingSystem.IsWindows() && !powerShell ? request.Input : null) : ProviderCatalog.StartCli(binary, arguments, workspace.Path, environment);
            run.Child = child; token.ThrowIfCancellationRequested();
            // Hermes's turn runs in its gateway, which outlives this process: it is told to cancel the turn before the process ends.
            using var stop = token.Register(() => { if (request.Provider == "hermes") StopHermes(run, child); else child.Kill(); });
            if (interactive)
            {
                // Fail closed: the prompt itself is only written after a
                // successful handshake, and a failure stops the run.
                run.Permissions = new ClaudePermissionChannel(request.SessionId, ProviderInput.PromptFrame(input),
                    text => { if (run.Finished || run.Finalizing) throw new IOException("The Claude input channel is closed."); child.Input.Write(text); child.Input.Flush(); },
                    value => { if (!run.Finished) permissionRequested!(value); },
                    (value, state) => parser.PermissionActivity(value, state),
                    message => Log(run, "system", message),
                    message => { run.ProtocolFailed = true; Log(run, "error", message); run.Cancel.Cancel(); },
                    // The request's graph carries the tracker's id: the plan's history block attaches beside it.
                    record => { if (!run.Finished) emit(new(request.SessionId, "plan", Plan: (tracker is null ? record : record with { GraphRunId = tracker.RunID }) with { LaunchOverride = request.PermissionModeOverride })); },
                    paneMode: request.Settings!.PermissionMode);
            }
            bool BackgroundRunning() { lock (planGate) return backgroundTracker.Work.Running.Count > 0; }
            void SettleInput() { if (run.TurnResultSeen && !BackgroundRunning()) CloseChannel(run, child); }
            void Consume(string line)
            {
                line = run.PaneBinding?.Redact(line) ?? line;
                // A confirmed Bedrock policy denial reads as guidance in the graph and the transcript; the sign-in reading below keeps the CLI's own words.
                string? shown = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line); var frame = doc.RootElement;
                    shown = request.Provider == "claude" ? ClaudeStream.PresentFailure(frame) : null;
                    using var presented = shown is null ? null : System.Text.Json.JsonDocument.Parse(shown);
                    var notices = tracker?.NoticeRevision;
                    tracker?.Consume(presented?.RootElement ?? frame);
                    // A notice turn's run (BackgroundNoticeRuns) reaches the pane before the turn's lines, which it then takes.
                    if (tracker is not null && tracker.NoticeRevision != notices) PublishGraph(false);
                    if (request.Provider == "claude")
                    {
                        ReadPlanMode(frame);
                        // As on the Mac, only the run's own turn decides: never a sub-agent's frame or a background task's notification result.
                        if (!ClaudeStream.IsNotificationResult(frame) && MetadataJson.Property(frame, "parent_tool_use_id").ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
                        {
                            if (frame.Text("type") == "result") run.AuthenticationFailure = CliAuthFailure.Claude(frame) || CliAuthFailure.Claude(frame, run.AuthenticationFailureText);
                            else if (CliAuthFailure.Claude(frame)) run.AuthenticationFailure = true;
                            if (frame.Text("type") == "result" || frame.Text("type") == "assistant" && frame.Text("error") is { Length: > 0 })
                                run.AuthenticationFailureText = CliAuthFailure.ClaudeFailureText(frame);
                        }
                    }
                    else if (request.Provider == "codex")
                    {
                        ReadPlanMode(frame);
                        if (frame.Text("type") == "turn.completed") run.AuthenticationFailure = false;
                        else if (frame.Text("type") is "turn.failed" or "error")
                        {
                            var message = frame.Text("message") ?? MetadataJson.Property(frame, "error").Text("message") ?? "";
                            if (frame.Text("type") == "turn.failed") run.AuthenticationFailure = CliAuthFailure.Codex(message);
                            else if (CliAuthFailure.Codex(message)) run.AuthenticationFailure = true;
                        }
                    }
                }
                catch (System.Text.Json.JsonException) { }
                parser.Parse(shown ?? line);
            }
            if (codexApprovals)
            {
                run.CodexPermissions = new CodexApprovalChannel(request, workspace.Path, input,
                    attachments?.Files.Where(f => f.Attachment.MediaType.StartsWith("image/", StringComparison.Ordinal)).Select(f => f.Path).ToArray() ?? [],
                    text => { if (run.Finished) return; try { child.Input.Write(text); child.Input.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } }, Consume,
                    value => { if (!run.Finished) permissionRequested!(value); },
                    (value, state) => parser.PermissionActivity(value, state),
                    message => Log(run, "system", message),
                    message => { run.ProtocolFailed = true; Log(run, "error", message); run.Cancel.Cancel(); },
                    () => { try { child.Input.Close(); } catch (IOException) { } catch (ObjectDisposedException) { } });
            }
            var hermes = request.Provider == "hermes";
            var hermesStderr = hermes ? new HermesAgent.StderrFilter() : null;
            if (hermesNotice is not null) Log(run, "system", hermesNotice);
            if (ProviderCatalog.IsAcp(request.Provider) && acpLaunch is not null) run.Acp = StartAcp(run, child, acpLaunch, workspace.Path, attachments, hermesQuestions, Consume);
            emit(RunEvent.State(request.SessionId, "running"));
            if (request.Kind != "shell") Activity(run, new(run.ActivityId, request.Provider, "turn", "running", Locale.Get("run.activity.running", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(request.Provider) })));
            // The process is up: what the start changed in the diagram goes with it, not with the loop's next pass.
            if (tracker is not null) PublishGraph(false);
            // An agent's stream-json line may carry a whole picture in base64.
            var output = PumpAsync(child.Output, request.Kind == "shell" ? 1024 * 1024 : codexApprovals ? CodexApprovalChannel.MaximumFrameBytes : run.Acp is not null ? AcpChannel.MaximumFrameBytes : OutputParser.MaximumLineBytes(request.Provider), line =>
            {
                if (run.Finalizing) return;
                if (request.Kind == "shell") { Log(run, "output", line); return; }
                if (run.CodexPermissions is { } codex) { codex.Receive(line); return; }
                if (run.Acp is { } acp) { Interlocked.Exchange(ref run.LastOutputTicks, Environment.TickCount64); acp.Receive(line); return; }
                run.Permissions?.Receive(line);
                // Antigravity prints a lost sign-in as a plain line, never as JSON; a cut-off JSON line may quote anything, so it never counts.
                if (request.Provider == "gemini")
                {
                    if (!line.TrimStart().StartsWith('{')) { if (CliAuthFailure.Antigravity(line)) run.AuthenticationFailure = true; }
                    else if (CliAuthFailure.AntigravitySucceeded(line)) run.AuthenticationFailure = false;
                }
                Consume(line);
                // One-shot shutdown: EOF only after the CLI's own turn result, so an approval reply is still
                // possible while the turn runs; background tasks still running keep it open longer.
                Interlocked.Exchange(ref run.LastOutputTicks, Environment.TickCount64);
                if (run.Permissions is not null && ClaudeStream.IsSettlePoint(line))
                {
                    if (ClaudeStream.IsTurnResult(line)) run.TurnResultSeen = true;
                    SettleInput();
                }
            }, token, request.Kind == "shell" ? null : () => { if (run.Finalizing) return; if (run.Acp is { } acp) acp.FrameTooLarge(); else Log(run, "system", Locale.Get("run.notice.lineTooLong")); });
            var error = PumpAsync(child.Error, 1024 * 1024, line =>
            {
                // Only Antigravity reports a lost sign-in on stderr ("Error: authentication required. Run 'agy' to log in, then retry."),
                // and that the conversation it was asked to continue is gone.
                if (request.Kind != "shell" && request.Provider == "gemini")
                {
                    if (CliAuthFailure.Antigravity(line)) run.AuthenticationFailure = true;
                    parser.AntigravityStderr(line);
                }
                // Hermes logs to stderr (INFO lines, a traceback for every failed request): kept out of the pane.
                if (hermesStderr is not null && !hermesStderr.Keep(line)) return;
                Log(run, "output", line);
            }, token);
            if (run.Acp is { } channel)
            {
                channel.Start();
                // An ACP agent boots its own server (and may refresh its model list) before it answers; Hermes may have to start its gateway first.
                _ = Task.Delay(TimeSpan.FromSeconds(hermes ? 90 : 45), token).ContinueWith(delay => { if (!delay.IsCanceled && !run.Finished) channel.InitializationTimedOut(); }, TaskScheduler.Default);
            }
            else if (interactive || codexApprovals)
            {
                run.Permissions?.Start(); run.CodexPermissions?.Start();
                if (interactive) _ = Task.Run(async () =>
                {
                    // Closes input once the CLI is silent for BackgroundIdleClose after its last background task ended
                    // (a follow-up turn's own result or `session_state_changed: idle` normally closes it first).
                    var idle = BackgroundIdleClose.TotalMilliseconds;
                    try
                    {
                        while (run.Permissions is not null && !run.Finished && !token.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(idle / 4, 50, 1000)), token);
                            if (run.TurnResultSeen && !BackgroundRunning() && Environment.TickCount64 - Interlocked.Read(ref run.LastOutputTicks) >= idle) { CloseChannel(run, child); return; }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);
                _ = Task.Delay(TimeSpan.FromSeconds(15), token).ContinueWith(delay =>
                {
                    if (delay.IsCanceled || run.Finished) return;
                    run.Permissions?.InitializationTimedOut(); run.CodexPermissions?.InitializationTimedOut();
                }, TaskScheduler.Default);
            }
            else
            {
                // Antigravity reads its prompt as one stream-json line (ProviderInput.AntigravityUserMessage).
                if (request.Kind != "shell") await child.Input.WriteAsync((request.Provider == "gemini" ? ProviderInput.AntigravityUserMessage(input) : input).AsMemory(), token);
                // pi reads get_state and the prompt, then keeps reading for a steer: stdin stays open until its turn ended and went quiet.
                if (request.Provider == "pi") { await child.Input.FlushAsync(token); _ = ClosePiWhenIdle(run, child); }
                else child.Input.Close();
            }
            run.Accepted?.TrySetResult();
            // Hermes's turn runs in its gateway: a stop sends session/cancel first and ends the process a second later (StopAcp), so the run waits for the exit itself.
            var code = hermes ? await child.Completion : await child.Completion.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            child.Kill(); // Close descendants that inherited stdout after their parent exited.
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3), token); parser.Flush();
            run.CodexPermissions?.Flush();
            if (!run.AcpEnding) run.Acp?.Flush();
            CloseChannel(run, child);
            if (mod is { Received: 0 }) Log(run, "system", Locale.Get("run.warning.noModEvents"));
            // An ACP server the runner ended after the prompt's answer did not fail (M/ProcessRunner.swift acpEnding).
            var exitedCleanly = code == 0 || run.AcpEnding && run.Acp?.TurnCompleted == true;
            Finish(run.PlanCancelled ? "stopped" : exitedCleanly && !parser.Failed && !run.ProtocolFailed && (!codexApprovals || run.CodexPermissions?.TurnCompleted == true) && (run.Acp is null || run.Acp.TurnCompleted && !run.Acp.Failed) ? "completed" : "error");
        }
        catch (OperationCanceledException ex) { startFailure = ex; Finish(run.ProtocolFailed ? "error" : "stopped"); }
        catch (Exception ex) { startFailure = ex; Log(run, "error", ex.Message); Finish(run.Cancel.IsCancellationRequested && !run.ProtocolFailed ? "stopped" : "error"); }
        finally
        {
            graphCancel.Cancel();
            try { await graphTask; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            try { watcher?.Dispose(); } catch (IOException) { }
            run.Tracker = null;
            run.Permissions?.CancelAll(); run.Permissions = null;
            run.CodexPermissions?.CancelAll(); run.CodexPermissions = null;
            run.Acp?.CancelAll(); run.Acp = null;
            run.PiDialogs?.CancelAll(); run.PiDialogs = null;
            mod?.Dispose(); run.Child = null;
            try { if (attachments is not null) await attachments.DisposeAsync(); }
            catch (IOException) { Log(run, "error", Locale.Get("run.error.attachmentCleanupFailed")); }
            catch (UnauthorizedAccessException) { Log(run, "error", Locale.Get("run.error.attachmentCleanupForbidden")); }
            finally { runs.TryRemove(new KeyValuePair<string, Run>(request.SessionId, run)); if (startFailure is not null) run.Accepted?.TrySetException(startFailure); }
        }
    }
    /// <summary>One graph node to the host, unless the run's last status is already out; a throwing callback loses the node, never the run.</summary>
    private void HandOver(Run run, ExecutionGraphNode node)
    {
        lock (run.NodeGate)
        {
            if (run.Finished || GraphNodes is not { } nodes) return;
            try { nodes(run.Request.SessionId, node); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("run graph node: " + ex.GetType().Name); }
        }
    }
    /// <summary>
    /// The ACP channel of an opencode or Hermes run (M/MightyCore/ProcessRunner.swift): its prompt (the request, the attachments' copies and
    /// pictures, Hermes's clarify note), its settings, the notices the run starts with, Hermes's admission (no provider, no session) and picker,
    /// and where its frames, events, cards and end go.
    /// </summary>
    private AcpChannel StartAcp(Run run, ChildProcess child, StartRunRequest launch, string workspacePath, StagedAttachments? attachments,
        (HermesQuestionTool.Outcome? First, IReadOnlyList<string>? List, bool Asks) hermesQuestions, Action<string> consume)
    {
        var request = run.Request; var hermes = request.Provider == "hermes";
        var models = hermes ? providers.HermesModelCache : null;
        switch (hermesQuestions.First)
        {
            case HermesQuestionTool.Outcome.Enabled: Log(run, "system", Locale.Get("run.notice.hermesClarifyEnabled", new Dictionary<string, string> { ["key"] = HermesQuestionTool.Key, ["value"] = string.Join(", ", hermesQuestions.List ?? []) })); break;
            case HermesQuestionTool.Outcome.Missing: Log(run, "system", Locale.Get("run.notice.hermesClarifyMissing", new Dictionary<string, string> { ["key"] = HermesQuestionTool.Key })); break;
        }
        static bool Picture((RunAttachment Attachment, string Path) file) => file.Attachment.MediaType.StartsWith("image/", StringComparison.Ordinal);
        IReadOnlyList<(RunAttachment Attachment, string Path)> files = attachments?.Files ?? [];
        // Each copy by its plain path (a staged path never holds a quote), as Antigravity and pi read theirs.
        var references = files.Where(file => !Picture(file)).Select(file => "Attached file " + System.Text.Json.JsonSerializer.Serialize(file.Attachment.Name, Wire.Json) + ": \"" + file.Path + "\" (read this copy as needed).");
        var prompt = AcpChannel.PromptText(request.Input, references, hermes && hermesQuestions.Asks ? HermesQuestionTool.PromptNote : null);
        // Cards carry the pane as their run, as Claude's and Codex's do (the window finds the pane by it).
        return new AcpChannel(request.SessionId, launch.ResumeId, workspacePath, prompt, files.Where(Picture).Select(file => (file.Attachment.MediaType, file.Attachment.DataBase64)).ToList(),
            hermes ? HermesAgent.ConfigValues(launch) : OpencodeAgent.ConfigValues(launch),
            text => { lock (run.InputGate) { if (run.Finished || Volatile.Read(ref run.AcpInputClosed) != 0) return; try { child.Input.Write(text); child.Input.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } } },
            line =>
            {
                if (run.Finished) return;
                // The picker a Hermes session offers is the model menu's list from then on.
                if (hermes && models is not null)
                    try
                    {
                        var value = System.Text.Json.Nodes.JsonNode.Parse(line);
                        if (value?["type"]?.GetValue<string>() == "acp.session") HermesAgent.SaveModels(value["configOptions"], models);
                        else if (value?["type"]?.GetValue<string>() == "acp.update" && value["update"]?["sessionUpdate"]?.GetValue<string>() == "config_option_update") HermesAgent.SaveModels(value["update"]!["configOptions"], models);
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
                consume(line);
            },
            value => { if (!run.Finished) permissionRequested?.Invoke(value); },
            (value, state) => run.Parser?.PermissionActivity(value, state),
            message => Log(run, "system", message),
            message =>
            {
                run.ProtocolFailed = true; Log(run, "error", message);
                if (hermes) StopHermes(run, child); else child.Kill();
            },
            () => EndAcp(run, child),
            // Hermes without a provider would open a session that can never answer: the run stops as a missing sign-in.
            hermes ? result => { if (HermesAgent.Provider(result) is not null) return null; HermesAgent.ForgetModels(models); return Locale.Get("run.error.hermesNoProvider"); } : null,
            freeChoices: hermes);
    }
    /// <summary>The ACP prompt was answered (or failed): stdin closes, and an agent server still up two seconds later is ended by the runner, which does not fail the run.</summary>
    private static void EndAcp(Run run, ChildProcess child)
    {
        lock (run.InputGate)
        {
            if (run.Finished || Interlocked.Exchange(ref run.AcpInputClosed, 1) != 0) return;
            try { child.Input.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => { if (run.Finished || child.Completion.IsCompleted) return; run.AcpEnding = true; child.Kill(); }, TaskScheduler.Default);
    }
    /// <summary>Hermes's turn runs in its gateway: <c>session/cancel</c> goes first, and the process ends a second later (M/ProcessRunner.swift cancelGrace).</summary>
    private static void StopHermes(Run run, ChildProcess child)
    {
        if (run.Acp?.Cancellation.Take() is not { } frame) { child.Kill(); return; }
        lock (run.InputGate)
        {
            try { if (Volatile.Read(ref run.AcpInputClosed) == 0) { child.Input.Write(frame); child.Input.Flush(); } } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => child.Kill(), TaskScheduler.Default);
    }
    /// <summary>Settles every waiting request and sends the CLI end-of-input.</summary>
    private static void CloseChannel(Run run, ChildProcess child)
    {
        lock (run.InputGate)
        {
            if (run.Permissions is null) return;
            run.Permissions.CancelAll(); run.Permissions = null;
            try { child.Input.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }
    /// <summary>이번만 허용 / 거부 for one waiting request of one run. Nothing else is ever returned.</summary>
    public void RespondToToolPermission(string sessionId, string requestId, bool allow)
    {
        if (!runs.TryGetValue(sessionId, out var run)) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
        if (run.CodexPermissions is { } codex) codex.Respond(requestId, allow);
        else if (run.Acp is { } acp) acp.Respond(requestId, allow);
        else if (run.PiDialogs is { } dialogs) dialogs.Respond(requestId, allow);
        else if (run.Permissions is { } channel) channel.Respond(requestId, allow);
        else throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
    }
    /// <summary>
    /// Closes a pi run's stdin (pi then exits) once its turn is over and it has been silent for a second: compaction and an
    /// automatic retry come right after <c>agent_end</c> (M/MightyCore/ProcessRunner.swift armPiClose).
    /// </summary>
    private static async Task ClosePiWhenIdle(Run run, ChildProcess child)
    {
        try
        {
            while (!run.Finished && !run.Cancel.IsCancellationRequested)
            {
                await Task.Delay(250);
                if (run.Parser is not { PiTurnEnded: true, PiTurnOpen: false } || Environment.TickCount64 - Interlocked.Read(ref run.LastOutputTicks) < 1000) continue;
                lock (run.InputGate) { run.PiInputClosed = true; try { child.Input.Close(); } catch (IOException) { } catch (ObjectDisposedException) { } }
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public Task<bool> TrySteerAsync(string sessionId, string text)
    {
        // pi takes a steer as a `steer` command while its turn is open; it reads it after the current tool calls, before the next model call.
        if (runs.TryGetValue(sessionId, out var piRun) && piRun.Request.Provider == "pi")
        {
            lock (piRun.InputGate)
            {
                if (piRun.Finalizing || piRun.Finished || piRun.Cancel.IsCancellationRequested || piRun.PiInputClosed || piRun.Parser is not { PiTurnOpen: true } || piRun.Child is not { } pi) return Task.FromResult(false);
                try { pi.Input.Write(ProviderInput.PiSteer(text)); pi.Input.Flush(); Interlocked.Exchange(ref piRun.LastOutputTicks, Environment.TickCount64); return Task.FromResult(true); }
                catch (IOException) { return Task.FromResult(false); }
                catch (ObjectDisposedException) { return Task.FromResult(false); }
            }
        }
        if (!runs.TryGetValue(sessionId, out var run) || run.Finalizing || run.Finished || run.Cancel.IsCancellationRequested || run.Request.Provider != "claude" || run.Permissions is not { } channel) return Task.FromResult(false);
        // After the request's result this starts a new turn in the same process (it stays open while background tasks run).
        // The queue no longer reads as waiting only on background work until that turn's own result (M/ProcessRunner.steer).
        try { var accepted = channel.TrySteer(text); if (accepted) { Interlocked.Exchange(ref run.LastOutputTicks, Environment.TickCount64); run.TurnReopened?.Invoke(); run.Tracker?.Steer(Wire.Id(), text); } return Task.FromResult(accepted); } catch (IOException) { return Task.FromResult(false); } catch (ObjectDisposedException) { return Task.FromResult(false); }
    }
    /// <summary>Answers one pending ExitPlanMode request of one run (ClaudePlanMode.Response).</summary>
    public PlanRecord AnswerPlan(string sessionId, string requestId, PlanDecision decision)
    {
        if (!runs.TryGetValue(sessionId, out var run) || run.Finalizing || run.Finished || run.Cancel.IsCancellationRequested || run.Request.Provider != "claude" || run.Permissions is not { } channel)
            throw new InvalidOperationException(Locale.Get("plan.error.settled"));
        // Marked before the reply is written, so an exit racing it still ends stopped.
        var cancelling = decision.Kind == PlanDecision.Cancel.Kind;
        if (cancelling) { run.PlanCancelled = true; if (run.Parser is { } parser) parser.SuppressInterruptedResult = true; }
        try { return channel.AnswerPlan(requestId, decision); }
        catch { if (cancelling) { run.PlanCancelled = false; if (run.Parser is { } parser) parser.SuppressInterruptedResult = false; } throw; }
    }
    /// <summary>Claude's AskUserQuestion, an ACP agent's form or clarify (opencode, Hermes), or a pi extension's dialog.</summary>
    public void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        if (!runs.TryGetValue(sessionId, out var run) || run.Finalizing || run.Finished || run.Cancel.IsCancellationRequested) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
        if (run.Acp is { } acp) { acp.AnswerQuestions(requestId, answers); return; }
        if (run.PiDialogs is { } dialogs) { dialogs.AnswerQuestions(requestId, answers); return; }
        if (run.Request.Provider != "claude" || run.Permissions is not { } channel) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
        channel.AnswerQuestions(requestId, answers);
    }
    private static void FeedMod(ExecutionGraphTracker tracker, System.Text.Json.JsonElement root)
    {
        try
        {
            if (root.Text("claudeSessionId") is not { } sessionId || root.Text("event") is not { } ev) return;
            ModGraphMetadata? graph = null;
            if (MetadataJson.Property(root, "graph") is { ValueKind: System.Text.Json.JsonValueKind.Object } gEl && MetadataJson.SafeInteger(gEl, "version") == 1)
                graph = new ModGraphMetadata(1, gEl.Text("phase") ?? "running", gEl.Text("agentId"), gEl.Text("parentAgentId"), gEl.Text("parentToolUseId"), gEl.Text("name") ?? gEl.Text("agentType"), gEl.Text("agentType"), gEl.Text("model"), gEl.Text("input"), gEl.Text("output"));
            tracker.ReceiveMod(new ModMetadata(sessionId, ev, root.Text("tool"), root.Text("toolUseId"), root.Text("agentId"), graph));
        }
        catch { }
    }
    /// <param name="tooLong">Hears of each line that was skipped for being longer than the limit.</param>
    private static async Task PumpAsync(StreamReader reader, int maximumLineBytes, Action<string> consume, CancellationToken token, Action? tooLong = null) { await foreach (var line in OutputParser.LinesAsync(reader, token, maximumLineBytes, tooLong)) consume(line); }
    public async Task StopAsync(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) throw new ArgumentException(Locale.Get("run.error.invalidId"));
        if (!runs.TryGetValue(sessionId, out var run)) return;
        run.Cancel.Cancel(); run.Child?.Kill();
        try { await run.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { run.Child?.Kill(); throw new TimeoutException(Locale.Get("run.error.stopTimeout")); }
    }
    public async ValueTask DisposeAsync() { string[] active; lock (lifecycle) { disposed = true; active = runs.Keys.ToArray(); } await Task.WhenAll(active.Select(StopAsync)); await bridge.DisposeAsync(); }
}
