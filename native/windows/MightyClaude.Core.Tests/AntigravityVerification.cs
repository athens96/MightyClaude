using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// The <c>gemini</c> provider runs Google's Antigravity CLI (<c>agy</c>), ported from the Mac's AntigravityTests and
/// AntigravityGraphTests: the stream, the arguments and stdin, sign-in, the model list, the one-time move off Gemini CLI,
/// and the Mighty views (the live graph with sub-agents read from their own transcripts, older requests, the conversations
/// to continue, skills). Every id, path and text is made up; the shapes follow what agy 1.3.5 printed and wrote.
/// </summary>
internal static class AntigravityVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static async Task English(Func<Task> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "en"; Locale.ResetCache();
        try { await body(); } finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    /// <summary>The kind an agy tool's row gets, read through the parser as a live run's row is.</summary>
    private static string ToolKind(string tool)
    {
        var rows = new List<AgentActivity>();
        new OutputParser("gemini", (_, _) => { }, _ => { }, rows.Add).Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["event"] = "step_update", ["step_update"] = new Dictionary<string, object> { ["step_index"] = 1, ["state"] = "ACTIVE", ["step_type"] = "tool", ["tool_info"] = new Dictionary<string, object> { ["name"] = tool } } }));
        return rows.Single(row => row.Kind != "turn").Kind;
    }

    /// <summary>A real <c>agy -p … --output-format stream-json --dangerously-skip-permissions</c> run (agy 1.3.5), trimmed.</summary>
    internal static readonly string[] Recorded =
    [
        """{"event":"init","conversation_id":"11111111-2222-4333-8444-555555555555","init":{"model":"gemini-3.8-flash-low","cwd":"/workspace/demo","tools":["view_file","run_command","write_to_file"],"permission_mode":"always-proceed"}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":0,"state":"DONE","step_type":"user_input"}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":2.916978,"usage":{"input_tokens":11835,"output_tokens":45,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":11880}}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/workspace/demo/note.txt"}}}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":2,"state":"DONE","step_type":"tool","tool_name":"view_file","duration_seconds":0.04126,"tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/workspace/demo/note.txt"},"output":"2 lines, 6 bytes"}}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":3,"state":"DONE","step_type":"agent_response","duration_seconds":2.9536,"usage":{"input_tokens":12065,"output_tokens":62,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12127}}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":4,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":4,"state":"DONE","step_type":"tool","tool_name":"run_command","duration_seconds":0.078959,"tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"output":"hi\r\n"}}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":5,"state":"ACTIVE","step_type":"agent_response","text_delta":"DONE"}}""",
        """{"event":"step_update","step_update":{"conversation_id":"11111111-2222-4333-8444-555555555555","step_index":5,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":2.01985,"usage":{"input_tokens":4094,"output_tokens":1,"thinking_tokens":0,"cache_read_tokens":8120,"total_tokens":4095}}}""",
        """{"event":"result","result":{"conversation_id":"11111111-2222-4333-8444-555555555555","status":"SUCCESS","response":"DONE\n","duration_seconds":8.079104,"num_turns":1,"usage":{"input_tokens":27994,"output_tokens":108,"thinking_tokens":0,"cache_read_tokens":8120,"total_tokens":28102}}}""",
    ];

    private sealed class Recorder
    {
        internal readonly List<(string Kind, string Text)> Logs = [];
        internal readonly List<string> Resumes = [];
        internal readonly List<AgentActivity> Activities = [];
        internal readonly List<SessionUsage> Usage = [];
        internal OutputParser Parser(bool activity = true, string? requestedResume = null)
        {
            double clock = 0;
            return new("gemini", (kind, text) => Logs.Add((kind, text)), Resumes.Add, activity ? Activities.Add : null, Usage.Add, "agy-run", () => clock += 10, requestedResume: requestedResume);
        }
    }

    internal static Task TheStreamIsReadForRepliesToolsResumeAndUsage() => English(() =>
    {
        var recorder = new Recorder(); var parser = recorder.Parser();
        foreach (var line in Recorded) parser.Parse(line);
        parser.Flush();
        // The deltas make one answer (its closing newline trimmed); the result's full response is not logged again.
        Check(recorder.Logs.Select(l => l.Kind).SequenceEqual(["assistant"]) && recorder.Logs[0].Text == "DONE", "one answer from the deltas: " + string.Join("|", recorder.Logs));
        Check(recorder.Resumes.SequenceEqual(["11111111-2222-4333-8444-555555555555"]) && !parser.Failed, "the conversation is the resume id");
        var tools = recorder.Activities.Where(a => a.Kind != "turn").ToList();
        var read = tools.Last(a => a.ToolName == "view_file");
        Check(read.Kind == "read" && read.State == "completed" && read.Summary == "/workspace/demo/note.txt" && read.Output == "2 lines, 6 bytes", "the read row");
        var command = tools.Last(a => a.ToolName == "run_command");
        Check(command.Kind == "command" && command.State == "completed" && command.Summary == "echo hi" && command.Output?.TrimEnd() == "hi", "the command row: " + command.Output);
        Check(tools.Any(a => a.ToolName == "run_command" && a.State == "running"), "a tool runs before it ends");
        Check(recorder.Activities.Where(a => a.Kind == "turn").Select(a => a.Summary).SequenceEqual(["Antigravity is writing a response", "Antigravity is finishing the response"]), "turn rows name Antigravity");
        var usage = recorder.Usage.Last();
        Check(usage.Provider == "gemini" && usage.Source == "antigravity.stream-json" && usage.TokenScope == "session" && usage.Model == "gemini-3.8-flash-low" && usage.ProviderSessionId == "11111111-2222-4333-8444-555555555555", "usage identity");
        // agy counts cached input apart (input 27994 + cache 8120); input here includes it.
        Check(usage.InputTokens == 36114 && usage.OutputTokens == 108 && usage.CacheReadTokens == 8120 && usage.ReasoningTokens == 0 && usage.TotalTokens == 36222, "usage counts");

        // Without activity rows the tools go to the log.
        var plain = new Recorder(); var logged = plain.Parser(activity: false);
        foreach (var line in Recorded) logged.Parse(line);
        logged.Flush();
        Check(plain.Logs.Select(l => l.Kind).SequenceEqual(["system", "output", "system", "output", "assistant"]) && plain.Logs[3].Text == "hi\r\n", "tools in the log: " + string.Join("|", plain.Logs.Select(l => l.Kind)));
        return Task.CompletedTask;
    });

    internal static Task ResultsDeniedActionsFailuresAndNotices() => English(() =>
    {
        var only = new Recorder(); var parser = only.Parser();
        parser.Parse("""{"event":"result","result":{"status":"SUCCESS","response":"Only in the result"}}"""); parser.Flush();
        Check(only.Logs.Select(l => l.Text).SequenceEqual(["Only in the result"]), "a result without deltas is the answer");

        var denied = new Recorder(); var declined = denied.Parser();
        declined.Parse("""{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}}}""");
        declined.Parse("""{"event":"step_update","step_update":{"step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"error":{"type":"TOOL_ERROR","message":"permission check failed for unsandboxed \"echo hi\": user denied permission to run command"}}}}""");
        declined.Parse("""{"event":"result","result":{"status":"SUCCESS","response":"","usage":{"input_tokens":11834,"output_tokens":57,"cache_read_tokens":0},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}"""); declined.Flush();
        // The turn went on and succeeded; the declined command shows as a failed tool and a notice.
        var command = denied.Activities.Last(a => a.ToolName == "run_command");
        Check(!declined.Failed && command.State == "error" && command.Output?.Contains("user denied permission") == true, "a declined command is a failed tool");
        Check(denied.Logs.Count == 1 && denied.Logs[0].Kind == "system" && denied.Logs[0].Text.Contains("RunCommand") && denied.Logs[0].Text.Contains("permissions.allow"), "the declined action is named");

        var failed = new Recorder(); var error = failed.Parser();
        error.Parse("""{"event":"result","result":{"status":"ERROR","error":{"message":"Quota exceeded"}}}""");
        Check(error.Failed && failed.Logs.Last() == ("error", "Quota exceeded"), "an error result fails the run with its message");
        var invalid = new Recorder().Parser(); invalid.Parse("""{"event":"result","result":{"status":"INVALID"}}"""); Check(invalid.Failed, "an invalid result fails");
        // agy ending the turn by itself is a failure; a stop the user asked for is the runner's.
        var ended = new Recorder(); var cancelled = ended.Parser();
        cancelled.Parse("""{"event":"result","result":{"status":"INTERRUPTED"}}""");
        Check(cancelled.Failed && ended.Logs.Last().Kind == "error" && ended.Logs.Last().Text.Contains("INTERRUPTED"), "agy ending the turn itself");
        var waiting = new Recorder(); var unknown = waiting.Parser();
        unknown.Parse("""{"event":"result","result":{"status":"WAITING"}}""");
        Check(!unknown.Failed && waiting.Logs.Last().Kind == "system" && waiting.Logs.Last().Text.Contains("WAITING"), "an unknown status is a notice");

        // A missing conversation is noted once, from stderr or from a new id at init.
        var notes = new Recorder(); var resumed = notes.Parser(requestedResume: "old-conversation");
        resumed.AntigravityStderr("warning: conversation \"old-conversation\" not found");
        resumed.Parse("""{"event":"init","conversation_id":"new-conversation","init":{}}""");
        Check(notes.Logs.Select(l => l.Kind).SequenceEqual(["system"]) && notes.Logs[0].Text == "The previous conversation was not found, so Antigravity started a new one." && notes.Resumes.SequenceEqual(["new-conversation"]), "a missing conversation is noted once");
        var same = new Recorder(); same.Parser(requestedResume: "c1").Parse("""{"event":"init","conversation_id":"c1","init":{}}""");
        Check(same.Logs.Count == 0, "the same conversation is not noted");
        // A read-only command's payload is left to its result; anything unknown is shown.
        var other = new Recorder(); var shown = other.Parser();
        shown.Parse("""{"event":"command_result","command":{"name":"help","data":{}}}""");
        shown.Parse("""{"event":"result","result":{"conversation_id":"","status":"SUCCESS","response":"/help\tShow available commands\n"}}""");
        shown.Parse("""{"event":"future_event","future_event":{"x":1}}""");
        Check(other.Logs.Select(l => l.Kind).SequenceEqual(["assistant", "output"]) && other.Logs[1].Text.Contains("future_event") && other.Resumes.Count == 0, "unknown events are shown");
        // A reply's start is kept as written; its closing whitespace is not.
        var reply = new Recorder(); var code = reply.Parser();
        code.Parse("""{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"    let x = 1\n\n"}}""");
        Check(reply.Logs.Select(l => l.Text).SequenceEqual(["    let x = 1"]), "an indented reply keeps its start");
        return Task.CompletedTask;
    });

    internal static async Task ArgumentsStdinAndAttachments()
    {
        var request = new StartRunRequest("run", "workspace", "claude", "--dangerously-skip-permissions /clear", [], "gemini-4-argon-medium", "gemini", new RunSettings(PermissionMode: "manual"), "11111111-2222-4333-8444-555555555555");
        var plain = ProviderInput.Prepare(request, "/plugin", false);
        Check(plain.Arguments.SequenceEqual(["--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--print-timeout", "0", "--model", "gemini-4-argon-medium", "--conversation", "11111111-2222-4333-8444-555555555555"]),
            "manual runs headless in agy's own review mode: " + string.Join(" ", plain.Arguments));
        var line = Encoding.UTF8.GetString(plain.StandardInput);
        Check(line.EndsWith('\n') && line.Count(c => c == '\n') == 1, "the prompt is one stdin line");
        using (var json = JsonDocument.Parse(line)) Check(json.RootElement.GetProperty("event").GetString() == "user" && json.RootElement.GetProperty("message").GetProperty("content").GetString() == request.Input, "the prompt is a user event, never an argument");
        Check(!plain.Arguments.Contains(request.Input), "the prompt is never an argument");
        foreach (var (mode, expected) in new[] { ("plan", new[] { "--mode", "plan" }), ("acceptEdits", ["--mode", "accept-edits"]), ("fullAccess", ["--dangerously-skip-permissions"]) })
        {
            var args = ProviderCatalog.Arguments(request with { ResumeId = null, Model = "default", Settings = new RunSettings(PermissionMode: mode) }, "/plugin");
            Check(args.Skip(7).SequenceEqual(expected), mode + " maps to " + string.Join(" ", expected) + ": " + string.Join(" ", args));
        }
        var file = AttachmentSupport.Make("notes.txt", "PURPLE-42"u8.ToArray());
        await using var staged = await StagedAttachments.CreateAsync([file]);
        var attached = request with { Input = "What does it say?", Attachments = [file] };
        var arguments = staged.ArgumentsFor(attached, "/plugin");
        Check(arguments.TakeLast(2).SequenceEqual(["--add-dir", staged.DirectoryPath]), "agy reads the staged folder");
        var prompt = staged.InputFor(attached);
        Check(prompt.StartsWith("What does it say?\n\n", StringComparison.Ordinal) && prompt.Contains(": \"" + staged.Files[0].Path + "\"", StringComparison.Ordinal), "the copy is named by its plain path");
        Check(ProviderInput.AntigravityUserMessage("a\nb") == "{\"event\":\"user\",\"message\":{\"content\":\"a\\nb\"}}\n", "one escaped line");
    }

    internal static async Task TheModelListLabelsAndInstall()
    {
        var catalog = ProviderCatalog.AntigravityCatalog("Fetching available models...\ngemini-4-argon-medium\tGemini 4 Argon (Medium)\nclaude-opus-5-5-low\tClaude Opus 5.5 (Low)\nnot a row\n--bad\tBad\ngemini-4-argon-medium\tDuplicate\n");
        Check(catalog.Source == "cli" && catalog.Models.Select(m => m.Value).SequenceEqual(["default", "gemini-4-argon-medium", "claude-opus-5-5-low"]), "agy models rows");
        Check(catalog.Models.Skip(1).Select(m => m.DisplayName).SequenceEqual(["Gemini 4 Argon (Medium)", "Claude Opus 5.5 (Low)"]), "agy's own names");
        Check(ProviderCatalog.AntigravityCatalog("Fetching available models...\n").Source == "fallback", "no rows keeps the fallback list");
        var fallback = ProviderCatalog.Fallback("gemini");
        Check(fallback.Models[0].Value == "default" && fallback.Models.Any(m => m.Value == "gemini-3.8-flash-low" && m.DisplayName == "Gemini 3.8 Flash (Low)"), "the fallback lists agy's models");
        Check(ProviderCatalog.Efforts("gemini", "gemini-4-argon-high", fallback).Length == 0 && !ProviderCatalog.Capabilities("gemini").Effort, "each slug carries its effort");
        Check(ProviderCatalog.Name("gemini") == "Antigravity CLI" && ProviderMark.Label("gemini") == "Antigravity" && ProviderCatalog.IsBeta("gemini"), "the name");
        Check(ProviderCatalog.Executable("gemini") == "agy" && ProviderCatalog.Executable("codex") == "codex" && ProviderCatalog.Executable("claude") == "claude", "the executable");
        Check(ComponentSection.InstallCommand("gemini") == "irm https://antigravity.google/cli/install.ps1 | iex", "Antigravity's own Windows installer");
        Check(CliUpdateService.ProviderLabel("gemini") == "Antigravity", "the update row's name");
        Check(ModelLabel.Format("gemini-4-argon-medium") == "Gemini 4 Argon (Medium)" && ModelLabel.Format("gemini-3.8-flash-low") == "Gemini 3.8 Flash (Low)" && ModelLabel.Format("gemini-2.5-flash-lite") == "Gemini 2.5 Flash Lite", "slugs carry their effort");
        Check(ToolKind("grep_search") == "search" && ToolKind("write_to_file") == "edit" && ToolKind("browser_click_element") == "web"
            && ToolKind("list_dir") == "read" && ToolKind("invoke_subagent") == "agent" && ToolKind("search_web") == "web" && ToolKind("run_command") == "command", "agy's tool kinds");
        var parameters = OutputParser.AntigravityParameters(Json("""{"TargetFile":"/a","SearchPath":"/b"}"""));
        Check(parameters.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["search_path", "target_file"]), "PascalCase parameters read as snake_case");
        Check(SlashCommandCatalog.Builtins("gemini").Select(c => c.Invocation).SequenceEqual(["model", "permissions", "clear", "usage", "config", "rename", "help"]), "agy's own command names");

        // A fake agy found where its installer puts it: status, update and the model list use the real commands.
        if (OperatingSystem.IsWindows()) return;
        var root = Verification.Temp();
        try
        {
            var local = Path.Combine(root, "local"); var bin = ProviderCatalog.AntigravityInstallFolder(new Dictionary<string, string> { ["LOCALAPPDATA"] = local });
            Directory.CreateDirectory(bin);
            var agy = Path.Combine(bin, "agy.exe");
            await File.WriteAllTextAsync(agy, """
                #!/bin/sh
                folder="$(/usr/bin/dirname "$0")"
                case "$1" in
                  --version) if [ -e "$folder/updated" ]; then printf '1.3.6\n'; else printf '1.3.5\n'; fi; exit 0;;
                  models) if [ -e "$folder/signed-out" ]; then printf "Error: authentication required. Run 'agy' to log in, then retry.\n" >&2; exit 1; fi
                          printf 'Fetching available models...\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n'; exit 0;;
                  update) : > "$folder/updated"; exit 0;;
                esac
                exit 2
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(agy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["LOCALAPPDATA"] = local };
            var updates = new CliUpdateService(new CliRunner(), environment, root);
            var installation = await updates.InspectAsync("gemini");
            Check(installation.Method == "native" && installation.CanUpdate && installation.Version == "1.3.5" && installation.ExecutablePath == agy, $"agy in its installer's folder updates itself: {installation.Method} {installation.Detail}");
            var result = await updates.UpdateAsync("gemini");
            Check(result.Status == "updated" && result.BeforeVersion == "1.3.5" && result.AfterVersion == "1.3.6", "agy update: " + result.Status + " " + result.Detail);
            File.Copy(agy, Path.Combine(root, "agy.exe"));
            var elsewhere = await new CliUpdateService(new CliRunner(), new Dictionary<string, string> { ["PATH"] = root + ":/usr/bin:/bin", ["LOCALAPPDATA"] = local }, root).InspectAsync("gemini");
            Check(!elsewhere.CanUpdate && elsewhere.Detail.Contains("irm https://antigravity.google/cli/install.ps1", StringComparison.Ordinal), "a copy elsewhere is left alone: " + elsewhere.Detail);
            // Off Windows the account check looks for agy by its bare name on PATH.
            File.Copy(agy, Path.Combine(bin, "agy"));
            var accounts = new CliAccountsCoordinator(new CliRunner(), new Dictionary<string, string> { ["PATH"] = bin + ":/usr/bin:/bin" }, root);
            Check((await accounts.StatusAsync("gemini")).LoggedIn == true, "a model list is a sign-in");
            await File.WriteAllTextAsync(Path.Combine(bin, "signed-out"), "");
            Check((await accounts.StatusAsync("gemini")).LoggedIn == false, "agy's own words are a lost sign-in");
            Check((await ProviderCatalog.ReadAntigravityModelsAsync(new(agy, []))).Source == "fallback", "signed out: the list fails");
            File.Delete(Path.Combine(bin, "signed-out"));
            Check((await ProviderCatalog.ReadAntigravityModelsAsync(new(agy, []))).Models.Select(m => m.Value).SequenceEqual(["default", "gemini-3.8-flash-low"]), "the live list");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task ASavedGeminiCliPaneIsMovedOnce()
    {
        await English(async () =>
        {
            var directory = Verification.Temp();
            try
            {
                var workspace = new Workspace { Id = "space", Name = "Fixture", Path = directory };
                var saved = new AppSnapshot
                {
                    Workspaces = [workspace],
                    Sessions =
                    [
                        new RunSession { Id = "legacy", WorkspaceId = workspace.Id, Title = "Gemini", Provider = "gemini", Model = "gemini-2.5-pro", ResumeId = "11111111-2222-4333-8444-555555555555" },
                        new RunSession { Id = "current", WorkspaceId = workspace.Id, Title = "Antigravity", Provider = "gemini", Model = "gemini-3.8-flash-low" },
                        new RunSession { Id = "claude", WorkspaceId = workspace.Id, Title = "Claude", Model = "opus", ResumeId = "claude-session" },
                    ],
                };
                await File.WriteAllBytesAsync(Path.Combine(directory, "workspace-state.json"), JsonSerializer.SerializeToUtf8Bytes(saved, Wire.Json));
                var store = new StateStore(directory);
                var migrated = await store.LoadAsync();
                Check(migrated.AntigravityMigrated == true, "the move is recorded");
                var pane = migrated.Sessions.Single(s => s.Id == "legacy");
                Check(pane.Model == "default" && pane.ResumeId is null && pane.Logs.Select(l => l.Kind).SequenceEqual(["system", "system"]) && pane.Logs[0].Text.Contains("gemini-2.5-pro"), "a Gemini CLI model and session are reset with a note");
                Check(migrated.Sessions.Single(s => s.Id == "current").Model == "gemini-3.8-flash-low", "an agy model stays");
                Check(migrated.Sessions.Single(s => s.Id == "claude") is { Model: "opus", ResumeId: "claude-session" }, "other panes are untouched");
                // Once: an agy conversation saved after the move is kept.
                var next = migrated with { Sessions = migrated.Sessions.Select(s => s.Id == "legacy" ? s with { ResumeId = "agy-conversation", Model = "gemini-4-argon-high" } : s).ToList() };
                await store.SaveAsync(next);
                var again = await new StateStore(directory).LoadAsync();
                Check(again.AntigravityMigrated == true && again.Sessions.Single(s => s.Id == "legacy") is { ResumeId: "agy-conversation", Model: "gemini-4-argon-high", Logs.Count: 2 }, "the move runs once");
                Check((await new StateStore(Path.Combine(directory, "fresh")).LoadAsync()).AntigravityMigrated == true, "a new profile never held Gemini CLI panes");
            }
            finally { Directory.Delete(directory, true); }
        });
        // agy's live list decides at run time; the fallback list never resets a model.
        var live = ProviderCatalog.AntigravityCatalog("gemini-4-argon-high\tGemini 4 Argon (High)\n");
        Check(AntigravityMigration.ModelUnavailable("gemini-2.5-pro", live) && !AntigravityMigration.ModelUnavailable("gemini-4-argon-high", live) && !AntigravityMigration.ModelUnavailable("default", live), "only the live list decides");
        Check(!AntigravityMigration.ModelUnavailable("gemini-9-new", ProviderCatalog.Fallback("gemini")), "the fallback list never resets a model");
    }

    // ── the Mighty views ─────────────────────────────────────────────────────

    internal const string Parent = "aaaaaaaa-0000-4000-8000-000000000001", Child = "aaaaaaaa-0000-4000-8000-000000000002", Grandchild = "aaaaaaaa-0000-4000-8000-000000000003";
    private static string Serialize(object value) => JsonSerializer.Serialize(value);
    private static string Step(Dictionary<string, object> fields) { fields["conversation_id"] = Parent; return Serialize(new Dictionary<string, object> { ["event"] = "step_update", ["step_update"] = fields }); }
    private static readonly Dictionary<string, object> Spec = new() { ["type_name"] = "research", ["role"] = "File Reader", ["initial_prompt"] = "Read a.txt", ["conversation_id"] = Child };
    /// <summary>The stream of a run that starts one sub-agent and answers after its message.</summary>
    internal static readonly string[] SubagentStream =
    [
        Serialize(new Dictionary<string, object> { ["event"] = "init", ["conversation_id"] = Parent, ["init"] = new Dictionary<string, object> { ["model"] = "gemini-3.8-flash-low", ["cwd"] = "/workspace/demo" } }),
        Step(new() { ["step_index"] = 0, ["state"] = "DONE", ["step_type"] = "user_input" }),
        Step(new() { ["step_index"] = 1, ["state"] = "DONE", ["step_type"] = "agent_response", ["usage"] = new Dictionary<string, int> { ["input_tokens"] = 100, ["output_tokens"] = 10, ["cache_read_tokens"] = 0 } }),
        Step(new() { ["step_index"] = 2, ["state"] = "ACTIVE", ["step_type"] = "subagent", ["tool_name"] = "invoke_subagent", ["subagent_info"] = new Dictionary<string, object> { ["subagents"] = new[] { Spec } } }),
        Step(new() { ["step_index"] = 2, ["state"] = "DONE", ["step_type"] = "subagent", ["tool_name"] = "invoke_subagent", ["subagent_info"] = new Dictionary<string, object> { ["subagents"] = new[] { Spec } } }),
        Step(new() { ["step_index"] = 3, ["state"] = "ACTIVE", ["step_type"] = "agent_response", ["text_delta"] = "Waiting for " }),
        Step(new() { ["step_index"] = 3, ["state"] = "DONE", ["step_type"] = "agent_response", ["text_delta"] = "the helper.\n", ["usage"] = new Dictionary<string, int> { ["input_tokens"] = 120, ["output_tokens"] = 5, ["cache_read_tokens"] = 0 } }),
        Step(new() { ["step_index"] = 4, ["state"] = "DONE", ["step_type"] = "system_message" }),
        Step(new() { ["step_index"] = 5, ["state"] = "DONE", ["step_type"] = "agent_response", ["text_delta"] = "The file says hi.\n", ["usage"] = new Dictionary<string, int> { ["input_tokens"] = 130, ["output_tokens"] = 6, ["cache_read_tokens"] = 40 } }),
        Serialize(new Dictionary<string, object> { ["event"] = "result", ["result"] = new Dictionary<string, object> { ["conversation_id"] = Parent, ["status"] = "SUCCESS", ["response"] = "Waiting for the helper.\nThe file says hi.\n" } }),
    ];

    private static string Line(int index, string source, string type, string status = "DONE", int minute = 0, Dictionary<string, object>? extra = null)
    {
        var fields = new Dictionary<string, object> { ["step_index"] = index, ["source"] = source, ["type"] = type, ["status"] = status, ["created_at"] = $"2026-01-02T03:{minute:00}:00Z" };
        foreach (var (key, value) in extra ?? []) fields[key] = value;
        return Serialize(fields);
    }
    private static string Request(string text) => "<USER_REQUEST>\n" + text + "\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: 2026-01-02T12:00:00+09:00.\n</ADDITIONAL_METADATA>";
    private static string Message(string sender, string text) =>
        "The following is a <SYSTEM_MESSAGE> not actually sent by the user.\n\n<SYSTEM_MESSAGE>\n[Message] timestamp=2026-01-02T03:00:00Z sender=" + sender + " priority=MESSAGE_PRIORITY_HIGH content=" + text + "\n</SYSTEM_MESSAGE>";
    private static Dictionary<string, object> Call(string name, Dictionary<string, object> args)
    {
        args["toolAction"] = "Doing it"; args["toolSummary"] = "Summary of " + name;
        return new() { ["name"] = name, ["args"] = args };
    }
    private static string Spawned(string id) =>
        "Created At: 2026-01-02T12:00:00+09:00\nCompleted At: 2026-01-02T12:00:00+09:00\nCreated the following subagents:\n{\n  \"conversationId\": \"" + id + "\",\n  \"workspaceUris\": [\n    \"file:///workspace/demo\"\n  ]\n}\nThe subagents will send you a message when they have completed their task.";

    /// <summary>The sub-agent: reads a file, starts its own helper, sends its answer back.</summary>
    internal static readonly string[] ChildLines =
    [
        Line(0, "SYSTEM", "SYSTEM_MESSAGE", minute: 1, extra: new() { ["content"] = Message(Parent, "Read a.txt") }),
        Line(1, "MODEL", "PLANNER_RESPONSE", minute: 1, extra: new() { ["tool_calls"] = new[] { Call("view_file", new() { ["AbsolutePath"] = "/workspace/demo/a.txt" }) }, ["input_tokens"] = 50, ["output_tokens"] = 5, ["cache_read_tokens"] = 0 }),
        Line(2, "MODEL", "GENERIC", minute: 1, extra: new() { ["content"] = "Created At: x\nCompleted At: x\nFile Path: `file:///workspace/demo/a.txt`\n1: secret file line" }),
        Line(3, "MODEL", "PLANNER_RESPONSE", minute: 2, extra: new() { ["tool_calls"] = new[] { Call("invoke_subagent", new() { ["Subagents"] = new[] { new Dictionary<string, object> { ["Role"] = "Checker", ["TypeName"] = "research", ["Prompt"] = "Check it", ["Model"] = "inherit" } } }) } }),
        Line(4, "MODEL", "GENERIC", minute: 2, extra: new() { ["content"] = Spawned(Grandchild) }),
        Line(5, "SYSTEM", "SYSTEM_MESSAGE", minute: 2, extra: new() { ["content"] = Message(Grandchild, "Checked.") }),
        Line(6, "MODEL", "PLANNER_RESPONSE", minute: 2, extra: new() { ["tool_calls"] = new[] { Call("send_message", new() { ["Recipient"] = Parent, ["Message"] = "It says hi" }) }, ["input_tokens"] = 60, ["output_tokens"] = 7, ["cache_read_tokens"] = 10 }),
        Line(7, "MODEL", "GENERIC", minute: 2, extra: new() { ["content"] = "Created At: x\nCompleted At: x\nMessage sent." }),
        Line(8, "MODEL", "PLANNER_RESPONSE", minute: 3, extra: new() { ["content"] = "Sent the content back." }),
    ];
    internal static readonly string[] GrandchildLines =
    [
        Line(0, "SYSTEM", "SYSTEM_MESSAGE", minute: 2, extra: new() { ["content"] = Message(Child, "Check it") }),
        Line(1, "MODEL", "PLANNER_RESPONSE", minute: 2, extra: new() { ["tool_calls"] = new[] { Call("send_message", new() { ["Recipient"] = Child, ["Message"] = "Checked." }) } }),
        Line(2, "MODEL", "GENERIC", minute: 2, extra: new() { ["content"] = "Message sent." }),
        Line(3, "MODEL", "PLANNER_RESPONSE", minute: 2, extra: new() { ["content"] = "Done checking." }),
    ];
    /// <summary>Three requests: a command, a sub-agent, and one cut short by a declined edit.</summary>
    internal static readonly string[] ParentLines =
    [
        Line(0, "USER_EXPLICIT", "USER_INPUT", minute: 0, extra: new() { ["content"] = Request("List the files") }),
        Line(1, "MODEL", "PLANNER_RESPONSE", minute: 0, extra: new() { ["tool_calls"] = new[] { Call("run_command", new() { ["CommandLine"] = "ls", ["Cwd"] = "/workspace/demo" }) }, ["input_tokens"] = 100, ["output_tokens"] = 10, ["cache_read_tokens"] = 0 }),
        Line(2, "MODEL", "GENERIC", minute: 0, extra: new() { ["content"] = "Created At: x\nCompleted At: x\n\nThe command exited with code 0.\nOutput:\na.txt\n" }),
        Line(3, "MODEL", "PLANNER_RESPONSE", minute: 0, extra: new() { ["content"] = "There is one file.", ["input_tokens"] = 120, ["output_tokens"] = 8, ["cache_read_tokens"] = 50 }),
        Line(4, "USER_EXPLICIT", "USER_INPUT", minute: 1, extra: new() { ["content"] = Request("Ask a helper to read a.txt") }),
        Line(5, "MODEL", "PLANNER_RESPONSE", minute: 1, extra: new() { ["tool_calls"] = new[] { Call("invoke_subagent", new() { ["Subagents"] = new[] { new Dictionary<string, object> { ["Role"] = "File Reader", ["TypeName"] = "research", ["Prompt"] = "Read a.txt", ["Model"] = "inherit" } } }) } }),
        Line(6, "MODEL", "GENERIC", minute: 1, extra: new() { ["content"] = Spawned(Child) }),
        Line(7, "MODEL", "PLANNER_RESPONSE", minute: 1, extra: new() { ["content"] = "Waiting for the helper." }),
        Line(8, "SYSTEM", "SYSTEM_MESSAGE", minute: 3, extra: new() { ["content"] = Message(Child, "It says hi") }),
        Line(9, "MODEL", "PLANNER_RESPONSE", minute: 3, extra: new() { ["content"] = "The file says hi." }),
        Line(10, "USER_EXPLICIT", "USER_INPUT", minute: 4, extra: new() { ["content"] = Request("Edit it") }),
        Line(11, "MODEL", "PLANNER_RESPONSE", minute: 4, extra: new() { ["tool_calls"] = new[] { Call("write_to_file", new() { ["TargetFile"] = "/workspace/demo/a.txt", ["CodeContent"] = "new" }) } }),
        Line(12, "MODEL", "GENERIC", status: "ERROR", minute: 4, extra: new() { ["content"] = "Created At: x\nCompleted At: x\nEncountered error", ["error"] = "permission check failed: user denied permission" }),
    ];
    private static string WriteTranscript(string home, string conversation, IEnumerable<string> lines, string trailing = "")
    {
        var path = AntigravityRecords.Transcript(AntigravityRecords.Brain(home), conversation)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n" + trailing);
        return path;
    }

    internal static Task ARunDrawsTheRequestItsSubagentsWorkAndTheLatestAnswer() => English(() =>
    {
        var tracker = new ExecutionGraphTracker("agy-run", "Ask a helper to read a.txt", "gemini", "default", _ => { });
        var logs = new List<(string Kind, string Text)>(); var activities = new List<AgentActivity>();
        var parser = new OutputParser("gemini", (kind, text) => logs.Add((kind, text)), _ => { }, value => { activities.Add(value); tracker.Activity(value); }, activityNamespace: "agy-run", answer: tracker.AntigravityAnswer);
        foreach (var line in SubagentStream) { tracker.Consume(Json(line)); parser.Parse(line); }
        Check(tracker.AntigravitySubagents.SequenceEqual([new AntigravitySubagent(Child, Parent, "File Reader (research)", "Read a.txt")]), "the stream names the sub-agent");
        Check(tracker.AntigravityConversation == Parent, "the run's own conversation");
        // The sub-agent's own transcript, as the runner's watcher reads it.
        var record = new AntigravityChildRecord(tracker.AntigravitySubagents[0], "agy-run");
        foreach (var line in ChildLines) record.Consume(Json(line));
        Check(record.TakeSpawned().Select(s => s.Conversation).SequenceEqual([Grandchild]), "a nested helper is handed out");
        tracker.AntigravitySession(record.Take()!);
        parser.Flush(); parser.FinishActivities(false); tracker.Finish("completed");
        var run = tracker.BuildRun()!;
        Check(run.Status == "completed" && run.Input == "Ask a helper to read a.txt" && run.Provider == "gemini", "the request");
        // The latest reply is the answer, exactly as the transcript shows it.
        Check(run.FinalOutput == "The file says hi.", "the answer: " + run.FinalOutput);
        Check(logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Waiting for the helper.", "The file says hi."]), "the replies");
        Check(run.Usage == new GraphTokenUsage(350, 21, 40), "the request's usage");
        Check(run.ResponseRecords?.Count == 3 && run.ResponseRecords.All(r => r.Model == "gemini-3.8-flash-low" && !r.MarkedAsConfigured), "per-response records name agy's model");
        var agent = run.Agents.Single();
        Check(agent.ParentID is null && agent.Title == "File Reader (research)" && agent.Input == "Read a.txt" && agent.Status == "completed", "the sub-agent block");
        Check(agent.Entries.Select(e => e.Activity?.ToolName).OfType<string>().SequenceEqual(["view_file", "invoke_subagent", "send_message"]) && agent.Entries.Select(e => e.Activity).OfType<AgentActivity>().All(a => a.State == "completed" && a.Provider == "gemini"), "its tool rows");
        // A file it read is named, never copied.
        Check(agent.Entries.Single(e => e.Activity?.ToolName == "view_file").Activity is { Summary: "/workspace/demo/a.txt", Output: null }, "a read file is never copied");
        Check(agent.Entries.Single(e => e.Activity?.ToolName == "send_message").Activity?.Summary == "Summary of send_message", "agy's own summary stands in");
        Check(agent.Entries.Any(e => e.Kind == "assistant" && e.Text == "It says hi") && agent.Usage == new GraphTokenUsage(110, 12, 10), "its answer and usage");
        // The start is one row in the request's own block.
        var row = activities.Last(a => a.ToolName == "invoke_subagent");
        Check(row.Kind == "agent" && row.State == "completed" && row.Summary == "File Reader (research)", "the start row");
        return Task.CompletedTask;
    });

    internal static Task ASubagentWaitingForItsHelperIsNotDoneUntilItAnswers() => English(() =>
    {
        var record = new AntigravityChildRecord(new(Child, Parent, "File Reader (research)", "Read a.txt"), "agy-run");
        foreach (var line in new[] { ChildLines[0],
            Line(1, "MODEL", "PLANNER_RESPONSE", minute: 1, extra: new() { ["tool_calls"] = new[] { Call("invoke_subagent", new() { ["Subagents"] = new[] { new Dictionary<string, object> { ["Role"] = "Checker", ["TypeName"] = "research", ["Prompt"] = "Check it" } } }) } }),
            Line(2, "MODEL", "GENERIC", minute: 1, extra: new() { ["content"] = Spawned(Grandchild) }),
            Line(3, "MODEL", "PLANNER_RESPONSE", minute: 1, extra: new() { ["content"] = "Waiting for my helper." }) }) record.Consume(Json(line));
        var waiting = record.Take()!;
        Check(waiting.State == "running" && waiting.Output is null, "waiting for its helper");
        foreach (var line in new[] { Line(4, "SYSTEM", "SYSTEM_MESSAGE", minute: 2, extra: new() { ["content"] = Message(Grandchild, "Checked.") }), Line(5, "MODEL", "PLANNER_RESPONSE", minute: 2, extra: new() { ["content"] = "All checked." }) }) record.Consume(Json(line));
        var done = record.Take()!;
        Check(done.State == "completed" && done.Output == "All checked." && done.Turns == 1, "done once the helper reported");
        // A helper that reports again after it answered: the same assignment goes on, in the graph too.
        var tracker = new ExecutionGraphTracker("agy-run", "Ask a helper", "gemini", null, _ => { });
        foreach (var line in SubagentStream) tracker.Consume(Json(line));
        tracker.AntigravitySession(waiting); tracker.AntigravitySession(done);
        Check(tracker.BuildRun(includeRunning: true)!.Agents.Single().Status == "completed", "completed first");
        record.Consume(Json(Line(6, "SYSTEM", "SYSTEM_MESSAGE", minute: 3, extra: new() { ["content"] = Message(Grandchild, "One more thing.") })));
        var continuing = record.Take()!;
        Check(continuing.State == "running" && continuing.Turns == 1, "the same assignment goes on");
        tracker.AntigravitySession(continuing);
        var agent = tracker.BuildRun(includeRunning: true)!.Agents.Single();
        Check(agent.Status == "running" && (agent.ActivityGeneration ?? 0) == 0, "running again without a new generation");
        record.Consume(Json(Line(7, "MODEL", "PLANNER_RESPONSE", minute: 3, extra: new() { ["content"] = "Updated." })));
        tracker.AntigravitySession(record.Take()!);
        var updated = tracker.BuildRun(includeRunning: true)!.Agents.Single();
        Check(updated.Status == "completed" && updated.Entries.Any(e => e.Kind == "assistant" && e.Text == "Updated."), "and done again");
        // A new instruction from the parent reopens a settled sub-agent and keeps its answer.
        record.Consume(Json(Line(8, "SYSTEM", "SYSTEM_MESSAGE", minute: 4, extra: new() { ["content"] = Message(Parent, "Now read b.txt") })));
        tracker.AntigravitySession(record.Take()!);
        var reopened = tracker.BuildRun(includeRunning: true)!.Agents.Single();
        Check(reopened.Status == "running" && reopened.ActivityGeneration == 1 && reopened.Entries.Any(e => e.Kind == "user" && e.Text == "Now read b.txt"), "a new instruction is new work");
        // Cancelled steps stop.
        var stopped = new AntigravityChildRecord(new(Child, Parent, null, null), "agy-run");
        foreach (var line in new[] { ChildLines[0], Line(1, "MODEL", "PLANNER_RESPONSE", status: "INTERRUPTED", extra: new() { ["content"] = "Stopping" }) }) stopped.Consume(Json(line));
        Check(stopped.Take()?.State == "stopped", "an interrupted step stops the sub-agent");
        return Task.CompletedTask;
    });

    internal static Task TheWatcherFollowsGrowingTranscriptsAndNestedSubagents()
    {
        var home = Verification.Temp();
        try
        {
            var brain = AntigravityRecords.Brain(home);
            var watcher = new AntigravitySubagentWatcher(brain, "agy-run");
            AntigravitySubagent[] started = [new(Child, Parent, "File Reader (research)", "Read a.txt")];
            // Not written yet: nothing to read, and no error.
            Check(watcher.Poll(started).Count == 0, "nothing to read yet");
            // A line still being written is not read.
            var path = WriteTranscript(home, Child, ChildLines.Take(2), ChildLines[2][..20]);
            var first = watcher.Poll(started).Single();
            Check(first.Thread == Child && first.State == "running" && first.Turns == 1 && first.Input == "Read a.txt" && first.Entries.Select(e => e.Activity?.State).SequenceEqual(["running"]), "the first two steps");
            // The rest arrives; the helper it starts is read in the same pass.
            File.AppendAllText(path, ChildLines[2][20..] + "\n" + string.Join("\n", ChildLines.Skip(3)) + "\n");
            WriteTranscript(home, Grandchild, GrandchildLines);
            var second = watcher.Poll(started);
            Check(second.Select(a => a.Thread).SequenceEqual([Child, Grandchild]), "the child and its helper");
            Check(second[0].State == "completed" && second[0].Output == "It says hi" && second[0].Entries.Any(e => e.Id == first.Entries[0].Id && e.Activity?.State == "completed"), "the child finished");
            Check(second[1].ParentThread == Child && second[1].Title == "Checker (research)" && second[1].Input == "Check it" && second[1].Output == "Checked.", "the helper under it");
            Check(watcher.Poll(started).Count == 0 && watcher.Finish(started).Count == 0 && watcher.Poll(started).Count == 0, "nothing new, and nothing after the end");
            // The run's own conversation is never followed as a sub-agent; only conversation ids name a transcript.
            Check(new AntigravitySubagentWatcher(brain, "agy-run", Child).Poll(started).Count == 0, "the root is never followed");
            Check(AntigravityRecords.Transcript(brain, "../escape") is null, "no path leaves the brain folder");
        }
        finally { Directory.Delete(home, true); }
        return Task.CompletedTask;
    }

    internal static Task OlderRequestsAreReadFromTheConversationTranscript() => English(() =>
    {
        var home = Verification.Temp();
        try
        {
            WriteTranscript(home, Parent, ParentLines); WriteTranscript(home, Child, ChildLines); WriteTranscript(home, Grandchild, GrandchildLines);
            Check(SessionHistory.Providers.Contains("gemini"), "Antigravity has history");
            Check(Path.GetFileName(SessionHistory.Locate("gemini", Parent, "/workspace/demo", new Dictionary<string, string>(), home)) == "transcript_full.jsonl", "the transcript is found");
            Check(SessionHistory.Locate("gemini", Grandchild.Replace('3', '9'), "/x", new Dictionary<string, string>(), home) is null, "a missing conversation has no transcript");
            var chunk = SessionHistory.Load(new SessionHistoryRequest("gemini", Parent, "/workspace/demo") { Home = home });
            Check(chunk.ReachedStart && chunk.Runs.Select(r => r.Input).SequenceEqual(["List the files", "Ask a helper to read a.txt", "Edit it"]), "three requests: " + string.Join("|", chunk.Runs.Select(r => r.Input)));
            Check(chunk.Runs.All(r => r.Provider == "gemini" && r.Id.StartsWith(HistoryReplay.RunPrefix, StringComparison.Ordinal)), "record runs");
            Check(chunk.Runs.Select(r => r.Status).SequenceEqual(["completed", "completed", "stopped"]), "statuses: " + string.Join("|", chunk.Runs.Select(r => r.Status)));
            var command = chunk.Runs[0];
            Check(command.FinalOutput == "There is one file." && command.Usage == new GraphTokenUsage(220, 18, 50), "the command request's answer and usage");
            var delegated = chunk.Runs[1];
            Check(delegated.FinalOutput == "The file says hi." && delegated.Agents.Select(a => a.Title).SequenceEqual(["File Reader (research)", "Checker (research)"]), "the sub-agents read back");
            var reader = delegated.Agents[0]; var checker = delegated.Agents[1];
            Check(reader.ParentID is null && reader.Input == "Read a.txt" && reader.Status == "completed" && reader.Entries.Select(e => e.Activity?.ToolName).OfType<string>().SequenceEqual(["view_file", "invoke_subagent", "send_message"]), "the reader");
            Check(!reader.Entries.Any(e => e.Text.Contains("secret file line") || e.Activity?.Output?.Contains("secret file line") == true), "file contents are never copied");
            Check(checker.ParentID == reader.Id && checker.Input == "Check it" && checker.Status == "completed", "the checker under the reader");
            Check(chunk.Runs[2].Agents.Count == 0, "the declined edit has no sub-agent");
            // Ten at a time from the end, as for Claude and Codex, and a later chunk above the newer requests.
            var newest = SessionHistory.Load(new SessionHistoryRequest("gemini", Parent, "/x") { Home = home, Turns = 1 });
            Check(newest.Runs.Select(r => r.Input).SequenceEqual(["Edit it"]) && !newest.ReachedStart, "the newest request alone");
            var older = SessionHistory.Load(new SessionHistoryRequest("gemini", Parent, "/x") { Home = home, File = newest.File, End = newest.End });
            Check(older.Runs.Select(r => r.Input).SequenceEqual(["List the files", "Ask a helper to read a.txt"]) && older.Runs[1].Agents.Count == 2, "the older chunk keeps its sub-agents");
            // Recorded steps become the events agy printed.
            var steps = ParentLines.Select(Json).ToList();
            var answered = AntigravityRecords.StreamEvents(steps.Skip(1).Take(3).ToList(), Parent);
            Check(answered.Status == "completed" && answered.Events.Select(e => e.Text("event") + ":" + MetadataJsonText(e)).SequenceEqual(["init:", "step_update:agent_response", "step_update:tool", "step_update:agent_response", "result:"]), "events");
            var open = AntigravityRecords.StreamEvents([steps[11]], Parent);
            Check(open.Status == "stopped" && open.Events[^1].GetProperty("step_update").GetProperty("state").GetString() == "ACTIVE", "a call left open ends stopped");
            var failed = AntigravityRecords.StreamEvents([Json(Line(1, "MODEL", "PLANNER_RESPONSE", status: "ERROR", extra: new() { ["error"] = "Quota exceeded" }))], Parent);
            Check(failed.Status == "error" && failed.Events[^1].GetProperty("result").GetProperty("error").GetProperty("message").GetString() == "Quota exceeded", "a failed answer");
            Check(AntigravityRecords.Request("<USER_REQUEST>\n  hello  \n</USER_REQUEST>\n<ADDITIONAL_METADATA>x</ADDITIONAL_METADATA>") == "hello" && AntigravityRecords.Message(Message(Parent, "Do it\nnow"), Parent) == "Do it\nnow"
                && AntigravityRecords.Message(Message("system", "[Notice]"), Parent) is null && AntigravityRecords.Title("", "research") == "research" && AntigravityRecords.Title(null, null) is null, "record helpers");
            Check(AntigravityRecords.Opening(Encoding.UTF8.GetBytes("""{"step_index": 0, "type": "USER_INPUT", "source": "USER_EXPLICIT", "content": "<USER_REQUEST>\nHi\n</USER_REQUEST>"}"""))?.Prompt == "Hi", "however agy spaces its JSON");
        }
        finally { Directory.Delete(home, true); }
        return Task.CompletedTask;
    });
    private static string MetadataJsonText(JsonElement value) => value.TryGetProperty("step_update", out var step) ? step.GetProperty("step_type").GetString() ?? "" : "";

    internal static Task TheConversationListAndSkills() => English(() =>
    {
        var root = Verification.Temp();
        try
        {
            var home = Path.Combine(root, "home"); var workspace = Path.Combine(root, "work", "demo project");
            Directory.CreateDirectory(workspace);
            const string other = "aaaaaaaa-0000-4000-8000-000000000004", automated = "aaaaaaaa-0000-4000-8000-000000000005", missing = "aaaaaaaa-0000-4000-8000-000000000006";
            var here = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri;
            if (!TestDatabase.Create(AntigravityRecords.Database(home), [(Parent, "Summary title", new[] { new Uri(Path.Combine(root, "attachments")).AbsoluteUri, here }, ""), (Child, "", [here], Parent),
                (other, "Elsewhere", [new Uri(Path.Combine(root, "elsewhere")).AbsoluteUri], ""), (automated, "", [here], ""), (missing, "No transcript", [here], "")]))
                throw new InvalidOperationException("the system's SQLite could not write the fixture database");
            WriteTranscript(home, Parent, ParentLines); WriteTranscript(home, Child, ChildLines);
            WriteTranscript(home, other, ParentLines.Take(4));
            WriteTranscript(home, automated, [Line(0, "USER_EXPLICIT", "USER_INPUT", extra: new() { ["content"] = Request("User: earlier text\nAssistant: more") })]);
            var query = new ResumableSessionQuery(workspace) { Home = home };
            var listing = ResumableSessions.Listing(query, "gemini");
            Check(listing.Items.Select(i => i.SessionID).SequenceEqual([Parent]) && listing.Hidden == 1, "this folder's conversation without sub-agents: " + string.Join("|", listing.Items.Select(i => i.SessionID)) + " hidden " + listing.Hidden);
            var item = listing.Items[0];
            Check(item.Provider == "gemini" && item.Title == "List the files" && item.Requests == 3 && item.Model is null && !item.Automated, "the row");
            Check(ResumableSessions.Listing(query with { IncludeAutomated = true }, "gemini").Items.Count == 2 && ResumableSessions.Listing(query).Items.Select(i => i.Provider).SequenceEqual(["gemini"]), "automated rows and the whole list");
            Check(ResumableSessions.Listing(query with { Excluding = new HashSet<string> { Parent.ToLowerInvariant() } }, "gemini").Items.Count == 0, "a conversation an open pane continues is left out");
            Check(ResumableSessions.OffersResume("claude", "gemini") && AddPaneMenu.MayAskResume("gemini", fromMenu: true), "창 추가 asks first");
            var pane = ResumableSessions.Apply(item, new RunSession { WorkspaceId = "space", Title = "Antigravity", Provider = "gemini" });
            Check(pane.ResumeId == Parent && pane.Title == "List the files", "continuing one resumes that conversation");
            Check(AntigravityRecords.Summaries(AntigravityRecords.Database(home), limit: 2)?.Select(s => s.Conversation).SequenceEqual([missing, automated]) == true, "the newest rows come first");
            Check(ResumableSessions.Listing(new ResumableSessionQuery(workspace) { Home = root }, "gemini").Items.Count == 0, "no database (agy never ran): nothing to list");

            // Skills: agy's own, the user's, and the workspace's `.agents/skills`.
            void Write(string text, string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
            var agy = AntigravityRecords.Folder(home);
            Write("---\nname: automation\ndescription: Schedule a task\n---\n", Path.Combine(agy, "builtin", "skills", "automation", "SKILL.md"));
            Write("---\nname: review\ndescription: Mine\n---\n", Path.Combine(agy, "skills", "review", "SKILL.md"));
            Write("---\nname: review\ndescription: The project's\n---\n", Path.Combine(workspace, ".agents", "skills", "review", "SKILL.md"));
            var commands = SlashCommandCatalog.Commands("gemini", workspace, home);
            Check(commands.Select(c => c.Invocation).SequenceEqual(["automation", "review"]) && commands[0].Source == SlashCommandStrings.AntigravitySkillSource && commands[1].Description == "The project's" && commands[1].Origin == SlashCommandOrigin.Project, "skills: " + string.Join("|", commands.Select(c => c.Invocation + " " + c.Source)));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    });

    /// <summary>Writes the fixture <c>conversation_summaries.db</c> with the system's own SQLite, as agy's own table is shaped.</summary>
    private static class TestDatabase
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Open(byte[] filename, out nint db, int flags, nint vfs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Exec(nint db, byte[] sql, nint callback, nint argument, nint error);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Close(nint db);
        internal static bool Create(string path, IEnumerable<(string Id, string Title, string[] Uris, string Parent)> rows)
        {
            var names = OperatingSystem.IsWindows() ? new[] { "winsqlite3.dll" } : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib"] : ["libsqlite3.so.0"];
            var library = names.Select(name => NativeLibrary.TryLoad(name, out var handle) ? handle : 0).FirstOrDefault(handle => handle != 0);
            if (library == 0) return false;
            var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "sqlite3_open_v2"));
            var exec = Marshal.GetDelegateForFunctionPointer<Exec>(NativeLibrary.GetExport(library, "sqlite3_exec"));
            var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "sqlite3_close"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
            if (open(Utf8(path), out var db, 0x2 | 0x4, 0) != 0) return false;
            try
            {
                bool Run(string sql) => exec(db, Utf8(sql), 0, 0, 0) == 0;
                if (!Run("CREATE TABLE `conversation_summaries` (`conversation_id` text,`title` text NOT NULL DEFAULT \"\",`step_count` integer NOT NULL DEFAULT 0,`workspace_uris` text NOT NULL,`agent_name` text NOT NULL DEFAULT \"\",`parent_conversation_id` text NOT NULL DEFAULT \"\",PRIMARY KEY (`conversation_id`))")) return false;
                foreach (var row in rows)
                    if (!Run($"INSERT INTO conversation_summaries (conversation_id, title, workspace_uris, parent_conversation_id) VALUES ('{row.Id}', '{row.Title}', '{JsonSerializer.Serialize(row.Uris).Replace("'", "''")}', '{row.Parent}')")) return false;
                return true;
            }
            finally { close(db); }
        }
    }
}
