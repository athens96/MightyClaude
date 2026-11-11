using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// opencode and Hermes runs through the real RunManager against a stand-in agent (the test binary, <c>--fake-acp</c>) that answers the
/// channel's requests with a recorded fixture (M/Tests/OpencodeTests.swift fakeOpencode, HermesTests.swift): argv, the run's environment,
/// stdin, the end of a run whose server exits or lingers, a lost sign-in, OpenCode 1, Hermes's yolo, its clarify switch and its stop.
/// </summary>
internal static class AcpRunVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Ended(RunEvent value) => value.Type == "status" && value.Status is "completed" or "error" or "stopped";

    /// <summary>The stand-in agent (Program.cs <c>--fake-acp state fixture [--fake-…] args</c>).</summary>
    internal static async Task FakeAcpAsync(string state, string fixture, string[] rest)
    {
        var linger = rest.Contains("--fake-linger"); var stall = rest.Contains("--fake-stall");
        var args = rest.Where(a => !a.StartsWith("--fake-", StringComparison.Ordinal)).ToArray();
        await using var stdout = Console.OpenStandardOutput();
        async Task Say(string text) { await stdout.WriteAsync(Encoding.UTF8.GetBytes(text)); await stdout.FlushAsync(); }
        if (args.SequenceEqual(["--version"])) { await Say((File.Exists(Path.Combine(state, "version")) ? File.ReadAllText(Path.Combine(state, "version")) : "opencode v2.2.7") + "\n"); return; }
        var config = Path.Combine(state, "hermes-config.json");
        if (args is ["config", "get", "--json", var key])
        {
            var values = File.Exists(config) ? JsonNode.Parse(File.ReadAllText(config))!.AsObject() : [];
            File.AppendAllText(Path.Combine(state, "config.log"), "get " + key + "\n");
            if (values[key] is { } value) { await Say(value.ToJsonString() + "\n"); return; }
            Console.Error.WriteLine("Config key not set: " + key); Environment.ExitCode = 1; return;
        }
        if (args is ["config", "set", var setKey, var setValue])
        {
            var values = File.Exists(config) ? JsonNode.Parse(File.ReadAllText(config))!.AsObject() : [];
            values[setKey] = JsonNode.Parse(setValue); File.WriteAllText(config, values.ToJsonString());
            File.AppendAllText(Path.Combine(state, "config.log"), "set " + setKey + " " + setValue + "\n");
            return;
        }
        File.WriteAllText(Path.Combine(state, "argv.json"), JsonSerializer.Serialize(args));
        File.WriteAllText(Path.Combine(state, "env.json"), JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["OPENCODE_CONFIG_CONTENT"] = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT"),
            ["HERMES_YOLO_MODE"] = Environment.GetEnvironmentVariable("HERMES_YOLO_MODE"),
        }));
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdin = Path.Combine(state, "stdin.txt");
        foreach (var line in File.ReadLines(fixture).Where(l => l.Length > 0))
        {
            if (stall && line.Contains("\"stopReason\"", StringComparison.Ordinal)) break;
            if (line.Contains("\"id\":\"mighty-", StringComparison.Ordinal))
            {
                if (await input.ReadLineAsync() is not { } request) return;
                File.AppendAllText(stdin, request + "\n");
            }
            await Say(line + "\n");
        }
        while (await input.ReadLineAsync() is { } extra) File.AppendAllText(stdin, extra + "\n");
        if (linger || stall) await Task.Delay(60_000);
    }

    private static ProviderCatalog Fake(string provider, string state, string fixture, string version, params string[] options) =>
        new((asked, _) => Task.FromResult<CliCommand?>(asked == provider ? Verification.Self(["--fake-acp", state, AcpVerification.FixturePath(provider, fixture), .. options]) with { Version = version } : null));
    private static string[] Lines(string path) => File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToArray() : [];

    internal static async Task AnOpencodeRunSpeaksAcpAndCompletesWhetherTheServerExitsOrIsEnded()
    {
        foreach (var linger in new[] { false, true })
        {
            var directory = Verification.Temp();
            try
            {
                var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
                await using var catalog = Fake("opencode", directory, "answer", "opencode v2.2.7", linger ? ["--fake-linger"] : []);
                await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue, _ => { }))
                {
                    await manager.StartAsync(new("pane", workspace.Id, "claude", "hi there", [], Provider: "opencode"));
                    await Verification.Until(() => events.Any(Ended), 30_000);
                }
                var all = events.ToList();
                Check(all.Last(e => e.Type == "status").Status == "completed", (linger ? "an ended" : "an exited") + " server completes: " + string.Join(" | ", all.Where(e => e.Type is "status" or "log").Select(e => e.Status ?? e.Entry?.Kind + ": " + e.Entry?.Text)));
                Check(all.Any(e => e.Type == "log" && e.Entry is { Kind: "assistant", Text: "Hello! How can I help with this project?" }), "the answer");
                Check(all.Any(e => e.Type == "resume" && e.ResumeId == AcpVerification.OpencodeSession), "the session");
                Check(all.Any(e => e.Type == "usage" && e.Usage is { Provider: "opencode", CostUSD: 0.0123 }), "usage");
                Check(JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, "argv.json")))!.SequenceEqual(["acp"]), "argv is acp");
                var env = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Path.Combine(directory, "env.json")))!;
                Check(env["OPENCODE_CONFIG_CONTENT"] == OpencodeAgent.ConfigContent("manual", AgentFiles.Find(OpencodeAgent.QuestionInstructionsName)), "the run's rules: " + env["OPENCODE_CONFIG_CONTENT"]);
                var stdin = Lines(Path.Combine(directory, "stdin.txt"));
                Check(stdin.Length == 3 && stdin[0].Contains("\"initialize\"") && stdin[1].Contains("\"session/new\"") && stdin[2].Contains("\"session/prompt\""), "stdin: " + string.Join(" | ", stdin));
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
        }
    }

    internal static async Task ASignedOutOpencodeRunEndsInErrorWithTheSignInReason()
    {
        var directory = Verification.Temp();
        try
        {
            var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
            await using var catalog = Fake("opencode", directory, "auth", "opencode v2.2.7", "--fake-linger");
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue, _ => { }))
            {
                await manager.StartAsync(new("pane", workspace.Id, "claude", "hi", [], Provider: "opencode"));
                await Verification.Until(() => events.Any(Ended), 30_000);
            }
            var status = events.Last(e => e.Type == "status");
            Check(status.Status == "error" && status.Reason == "authentication", "a lost sign-in: " + status.Status + " " + status.Reason);
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    internal static async Task AnOpenCode1IsNotRunAndAsksForOpenCode2()
    {
        var directory = Verification.Temp();
        try
        {
            var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
            await using var catalog = Fake("opencode", directory, "answer", "1.19.5");
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue, _ => { }))
            {
                try { await manager.StartAsync(new("pane", workspace.Id, "claude", "hi", [], Provider: "opencode")); } catch (InvalidOperationException) { }
                await Verification.Until(() => events.Any(Ended), 30_000);
            }
            Check(events.Any(e => e.Type == "log" && e.Entry?.Kind == "error" && e.Entry.Text == Locale.Get("run.error.opencodeV1", new Dictionary<string, string> { ["version"] = "1.19.5" })), "OpenCode 1 is named");
            Check(events.Last(e => e.Type == "status").Status == "error" && !File.Exists(Path.Combine(directory, "argv.json")), "and not run");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    internal static async Task AHermesRunGivesFullAccessAsYoloAndTurnsClarifyOnOnce()
    {
        HermesQuestionTool.Reset();
        foreach (var mode in new[] { "manual", "fullAccess", "manual" })
        {
            var directory = Verification.Temp();
            try
            {
                var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
                // Full access also lifts the edit policy, which Hermes answers before the prompt (the recorded full-access session).
                await using var catalog = Fake("hermes", directory, mode == "fullAccess" ? "full-access" : "answer", "Hermes Agent v0.23.1 (2026.9.24) · stable");
                await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue, _ => { }))
                {
                    await manager.StartAsync(new("pane", workspace.Id, "claude", "hi there", [], Provider: "hermes", Settings: new RunSettings(PermissionMode: mode)));
                    await Verification.Until(() => events.Any(Ended), 30_000);
                }
                var all = events.ToList();
                Check(all.Last(e => e.Type == "status").Status == "completed", mode + ": completed: " + string.Join(" | ", all.Where(e => e.Type is "status" or "log").Select(e => e.Status ?? e.Entry?.Kind + ": " + e.Entry?.Text)));
                var env = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Path.Combine(directory, "env.json")))!;
                Check(env["HERMES_YOLO_MODE"] == (mode == "fullAccess" ? "1" : null), mode + ": yolo " + env["HERMES_YOLO_MODE"]);
                var stdin = Lines(Path.Combine(directory, "stdin.txt"));
                // The policy goes unless the session already has it (a new session asks before edits).
                Check(stdin.Any(l => l.Contains("\"edit_approval_policy\"") && l.Contains(HermesAgent.EditPolicy(mode))) == (mode != "manual"), mode + ": the edit policy");
                var prompt = stdin.First(l => l.Contains("\"session/prompt\""));
                // A fresh fake Hermes each time (its own state folder): the first launch sets the key, the app remembers it per executable.
                var notice = all.Where(e => e.Type == "log" && e.Entry?.Kind == "system" && e.Entry.Text.Contains(HermesQuestionTool.Key)).Select(e => e.Entry!.Text).ToList();
                if (mode == "manual" && notice.Count > 0)
                {
                    Check(notice.SequenceEqual([Locale.Get("run.notice.hermesClarifyEnabled", new Dictionary<string, string> { ["key"] = HermesQuestionTool.Key, ["value"] = "hermes-acp, clarify, x_search" })]), "the pane says it turned clarify on");
                    Check(Lines(Path.Combine(directory, "config.log")).Contains("set " + HermesQuestionTool.Key + " [\"hermes-acp\",\"clarify\",\"x_search\"]"), "the value Hermes stores as a list");
                }
                Check(prompt.Contains("Note from the Mighty Code app"), mode + ": the note follows the request while clarify is on");
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
        }
        HermesQuestionTool.Reset();
    }

    internal static async Task AStopTellsHermesToCancelTheTurnBeforeTheProcessEnds()
    {
        HermesQuestionTool.Reset();
        var directory = Verification.Temp();
        try
        {
            var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
            // clarify already on: no config set during the run.
            File.WriteAllText(Path.Combine(directory, "hermes-config.json"), "{\"platform_toolsets.acp\":[\"hermes-acp\",\"clarify\"]}");
            await using var catalog = Fake("hermes", directory, "answer", "Hermes Agent v0.23.1", "--fake-stall");
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue, _ => { }))
            {
                await manager.StartAsync(new("pane", workspace.Id, "claude", "stop me", [], Provider: "hermes"));
                await Verification.Until(() => Lines(Path.Combine(directory, "stdin.txt")).Any(l => l.Contains("\"session/prompt\"")), 30_000);
                await manager.StopAsync("pane");
                await Verification.Until(() => events.Any(Ended), 30_000);
            }
            Check(events.Last(e => e.Type == "status").Status == "stopped", "stopped");
            var stdin = Lines(Path.Combine(directory, "stdin.txt"));
            Check(stdin.Any(l => l.Contains("\"session/cancel\"") && l.Contains(AcpVerification.HermesSession)), "session/cancel went before the end: " + string.Join(" | ", stdin));
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } HermesQuestionTool.Reset(); }
    }
}
