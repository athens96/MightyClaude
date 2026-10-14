using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyClaude.Core;

/// <summary>
/// A scripted stand-in for the <c>claude</c> CLI, ported from the Mac's FakeClaude (M/Tests/FakeClaude/main.swift). There is no
/// model: what the "agent" does is read from a script. The Core checks and the app's delegation smoke start it through the real
/// run path as <c>--fake-claude &lt;root&gt; &lt;CLI arguments…&gt;</c>, the check binary's or the app's own. It speaks the CLI's stream-json protocol, answers the host's
/// <c>initialize</c> control request, asks for permissions the CLI's way (<c>can_use_tool</c>), reaches the delegation MCP server
/// named in the app-written <c>--mcp-config</c> with the environment it inherited (so the pane token never leaves the
/// environment), and writes its transcript where Claude Code does: <c>&lt;root&gt;\claude-config\projects\&lt;folder&gt;\&lt;session&gt;.jsonl</c>.
///
/// Under <c>&lt;root&gt;</c>, keyed by the Claude project folder name of the folder it runs in:
/// <c>scripts\&lt;key&gt;.json</c> <c>{"turns": [[step]]}</c> is what a run in that folder plays, the n-th turn for the n-th
/// prompt; without one, <c>child-scripts.json</c> <c>[{"phrase", "steps"}]</c> gives the first turn of a run whose first prompt
/// holds the phrase (the last such entry wins). <c>logs\&lt;key&gt;.jsonl</c> is what it saw and did.
///
/// Steps: <c>{"say"}</c>; <c>{"call", "arguments", "server"?, "ask"?}</c> a tools/call, asked for first when <c>ask</c>;
/// <c>{"permission", "input"}</c> a built-in tool use, asked for; <c>{"run": [[argv]], "ask"?}</c> a Bash tool use running each
/// command in the working folder (<c>git</c> is the git delegation uses, with fixture settings); <c>{"write", "text"}</c> a file
/// under the working folder; <c>{"sleep"}</c> seconds; <c>{"exit"}</c> leave at once with this code.
/// </summary>
public sealed class DelegationFakeClaude
{
    /// <summary>The argument that starts it, before its root folder and the CLI's arguments.</summary>
    public const string Argument = "--fake-claude";

    private readonly string root, cwd = Environment.CurrentDirectory, key, sessionId;
    private readonly string[] arguments;
    private readonly StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    private readonly BlockingCollection<JsonObject> incoming = [];
    private readonly List<JsonObject> waiting = [];
    private readonly Dictionary<string, Connection> servers = [];
    private List<string> configured = [];
    private string? lastUuid;
    private int counter;
    private bool started;

    private DelegationFakeClaude(string root, string[] arguments)
    {
        this.root = root; this.arguments = arguments; key = SessionHistory.ClaudeProjectFolder(cwd);
        sessionId = Value("--resume") ?? Guid.NewGuid().ToString();
    }

    public static async Task<int> RunAsync(string root, string[] arguments)
    {
        if (arguments.Contains("--version")) { Console.WriteLine("2.1.271 (Claude Code)"); return 0; }
        // The app's model-list probe: leaving at once makes it use its built-in list.
        if (arguments.Contains("--no-session-persistence")) return 0;
        return await Task.Run(() => new DelegationFakeClaude(root, arguments).Run());
    }

    private string? Value(string option) => Array.IndexOf(arguments, option) is var index and >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;

    private int Run()
    {
        Log(new() { ["event"] = "argv", ["arguments"] = new JsonArray([.. arguments.Select(argument => (JsonNode)argument)]) });
        ConnectServers();
        new Thread(() =>
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while (input.ReadLine() is { } line) if (JsonNode.Parse(line) is JsonObject message) incoming.Add(message);
            incoming.CompleteAdding();
        }) { IsBackground = true }.Start();
        var turn = 0; List<List<JsonObject>>? turns = null;
        while (Next(_ => true) is { } message)
        {
            switch ((string?)message["type"])
            {
                case "control_request" when (string?)message["request_id"] is { } id:
                    if ((string?)message["request"]?["subtype"] == "initialize") { Emit(new() { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = id, ["response"] = new JsonObject { ["commands"] = new JsonArray(), ["models"] = new JsonArray() } } }); Log(new() { ["event"] = "initialize" }); }
                    else Emit(new() { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = id, ["error"] = "Unsupported control request." } });
                    break;
                case "user":
                    var prompt = message["message"]?["content"]?.DeepClone() ?? "";
                    turns ??= Script(TextOf(prompt));
                    var exit = Play(turn < turns.Count ? turns[turn] : [], prompt); turn++;
                    if (exit is { } code) return Finish(code);
                    break;
            }
        }
        return Finish(0);
    }

    /// <summary>The first message <paramref name="matching"/> takes, waiting for one; null once stdin closed with none left.</summary>
    private JsonObject? Next(Func<JsonObject, bool> matching)
    {
        if (waiting.FirstOrDefault(matching) is { } queued) { waiting.Remove(queued); return queued; }
        foreach (var message in incoming.GetConsumingEnumerable())
        {
            if (matching(message)) return message;
            waiting.Add(message);
        }
        return null;
    }

    private List<List<JsonObject>> Script(string prompt)
    {
        static List<List<JsonObject>> Turns(JsonNode? turns) => turns is JsonArray list ? [.. list.Select(turn => turn is JsonArray steps ? steps.OfType<JsonObject>().Select(step => (JsonObject)step.DeepClone()).ToList() : [])] : [];
        var own = Path.Combine(root, "scripts", key + ".json");
        if (File.Exists(own)) return Turns(JsonNode.Parse(File.ReadAllText(own))?["turns"]);
        var children = Path.Combine(root, "child-scripts.json");
        if (!File.Exists(children) || JsonNode.Parse(File.ReadAllText(children)) is not JsonArray entries) return [];
        return entries.OfType<JsonObject>().LastOrDefault(entry => prompt.Contains((string?)entry["phrase"] ?? "\0", StringComparison.Ordinal)) is { } found ? [Turns(new JsonArray(found["steps"]!.DeepClone()))[0]] : [];
    }

    private void ConnectServers()
    {
        if (Value("--mcp-config") is not { } value || JsonNode.Parse(value.StartsWith('{') ? value : File.ReadAllText(value))?["mcpServers"] is not JsonObject entries) return;
        configured = [.. entries.Select(entry => entry.Key).Order(StringComparer.Ordinal)];
        // The delegation server is the one these scripts call.
        if (entries[DelegationMcp.ServerName] is not JsonObject entry || (string?)entry["command"] is not { } command) return;
        try
        {
            var connection = new Connection(command, entry["args"] is JsonArray list ? [.. list.Select(item => (string)item!)] : []);
            servers[DelegationMcp.ServerName] = connection;
            Log(new() { ["event"] = "mcp", ["server"] = DelegationMcp.ServerName, ["tools"] = new JsonArray([.. connection.Tools.Select(tool => (JsonNode)tool)]) });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Log(new() { ["event"] = "mcp", ["server"] = DelegationMcp.ServerName, ["error"] = ex.Message }); }
    }

    /// <summary>Plays one turn; the code to leave with when a step said so.</summary>
    private int? Play(List<JsonObject> steps, JsonNode prompt)
    {
        if (!started)
        {
            started = true;
            Emit(new() { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["cwd"] = cwd, ["model"] = "claude-fake-1", ["permissionMode"] = Value("--permission-mode") ?? "default",
                ["tools"] = new JsonArray([.. new[] { "Bash", "Edit", "Read", "Write" }.Concat(servers.SelectMany(server => server.Value.Tools.Select(tool => $"mcp__{server.Key}__{tool}"))).Select(name => (JsonNode)name)]),
                ["mcp_servers"] = new JsonArray([.. configured.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["status"] = servers.ContainsKey(name) ? "connected" : "pending" })]) });
        }
        StateChanged("running");
        Record(new() { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt.DeepClone() } });
        var said = "";
        foreach (var step in steps)
        {
            if ((string?)step["say"] is { } text) { said = text; Assistant(new JsonObject { ["type"] = "text", ["text"] = text }); }
            else if ((string?)step["call"] is { } tool)
            {
                var server = (string?)step["server"] ?? DelegationMcp.ServerName; var input = step["arguments"] as JsonObject ?? []; var name = $"mcp__{server}__{tool}"; var id = ToolUseId();
                Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input.DeepClone() });
                if ((bool?)step["ask"] == true && Ask(name, input, id) is { } denial) { ToolResult(id, denial, true); continue; }
                if (!servers.TryGetValue(server, out var connection)) { ToolResult(id, $"No MCP server named {server}.", true); continue; }
                var result = connection.Call(tool, input);
                Log(new() { ["event"] = "call", ["server"] = server, ["tool"] = tool, ["arguments"] = input.DeepClone(), ["result"] = result.DeepClone() });
                ToolResult(id, result["content"]?.DeepClone() ?? new JsonArray(), (bool?)result["isError"] == true);
            }
            else if ((string?)step["permission"] is { } builtIn)
            {
                var input = step["input"] as JsonObject ?? []; var id = ToolUseId();
                Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = builtIn, ["input"] = input.DeepClone() });
                if (Ask(builtIn, input, id) is { } denial) ToolResult(id, denial, true); else ToolResult(id, $"{builtIn} ran.", false);
            }
            else if (step["run"] is JsonArray commands)
            {
                var argv = commands.OfType<JsonArray>().Select(command => command.Select(part => (string)part!).ToArray()).ToList();
                var input = new JsonObject { ["command"] = string.Join(" && ", argv.Select(command => string.Join(' ', command))) }; var id = ToolUseId();
                Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = input });
                if ((bool?)step["ask"] == true && Ask("Bash", input, id) is { } denial) { ToolResult(id, denial, true); continue; }
                var (code, shown) = (0, "");
                foreach (var command in argv) { (code, shown) = Shell(command); if (code != 0) break; }
                Log(new() { ["event"] = "bash", ["command"] = (string)input["command"]!, ["code"] = code });
                ToolResult(id, shown, code != 0);
            }
            else if ((string?)step["write"] is { } path)
            {
                var file = Path.GetFullPath(Path.Combine(cwd, path)); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, Encoding.UTF8.GetBytes((string?)step["text"] ?? ""));
                Log(new() { ["event"] = "write", ["path"] = path });
            }
            else if ((double?)step["sleep"] is { } seconds) Thread.Sleep(TimeSpan.FromSeconds(seconds));
            else if ((int?)step["exit"] is { } code) return code;
        }
        Emit(new() { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["duration_ms"] = 1, ["num_turns"] = 1, ["result"] = said, ["session_id"] = sessionId, ["total_cost_usd"] = 0, ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } });
        StateChanged("idle");
        return null;
    }

    /// <summary>Asks the host whether <paramref name="tool"/> may run, the way the CLI does under <c>--permission-prompt-tool stdio</c>: null when allowed, else why not.</summary>
    private string? Ask(string tool, JsonObject input, string toolUseId)
    {
        if (Value("--input-format") != "stream-json" || Value("--permission-prompt-tool") != "stdio")
        {
            Log(new() { ["event"] = "permission", ["tool"] = tool, ["behavior"] = "deny" });
            return $"Permission to use {tool} was not granted.";
        }
        var requestId = "perm-" + Number();
        Emit(new() { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = tool, ["input"] = input.DeepClone(), ["tool_use_id"] = toolUseId, ["permission_suggestions"] = new JsonArray() } });
        if (Next(message => (string?)message["type"] == "control_response" && (string?)message["response"]?["request_id"] == requestId) is not { } answer)
        {
            Log(new() { ["event"] = "permission", ["tool"] = tool, ["behavior"] = "unanswered" });
            return "The permission request was not answered.";
        }
        var decision = answer["response"]?["response"] as JsonObject ?? [];
        var behavior = (string?)answer["response"]?["subtype"] == "success" ? (string?)decision["behavior"] ?? "deny" : "error";
        Log(new() { ["event"] = "permission", ["tool"] = tool, ["behavior"] = behavior, ["toolUseID"] = decision["toolUseID"]?.DeepClone() });
        return behavior == "allow" ? null : (string?)decision["message"] ?? $"Permission to use {tool} was denied.";
    }

    /// <summary>Runs one command in the working folder, as the CLI's Bash tool does; git with fixture settings, away from the user's and the system's.</summary>
    private (int Code, string Output) Shell(string[] command)
    {
        var info = new ProcessStartInfo(command[0] == "git" ? DelegationGit.Executable ?? "git" : command[0]) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var part in command.Skip(1)) info.ArgumentList.Add(part);
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        var settings = Path.Combine(root, "fixture.gitconfig"); if (!File.Exists(settings)) File.WriteAllText(settings, "");
        info.Environment["GIT_CONFIG_GLOBAL"] = settings; info.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var who in new[] { "GIT_AUTHOR", "GIT_COMMITTER" }) { info.Environment[who + "_NAME"] = "Fake Claude"; info.Environment[who + "_EMAIL"] = "fake-claude@example.invalid"; }
        try
        {
            using var process = Process.Start(info)!; process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync(); var text = process.StandardOutput.ReadToEnd() + error.Result;
            process.WaitForExit(); return (process.ExitCode, text);
        }
        catch (System.ComponentModel.Win32Exception ex) { return (127, ex.Message); }
    }

    private void Assistant(JsonObject content)
    {
        var message = new JsonObject { ["id"] = "msg_fake_" + Number(), ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-fake-1", ["content"] = new JsonArray(content), ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } };
        var uuid = Record(new() { ["type"] = "assistant", ["message"] = message.DeepClone() });
        Emit(new() { ["type"] = "assistant", ["message"] = message, ["parent_tool_use_id"] = null, ["session_id"] = sessionId, ["uuid"] = uuid });
    }

    private void ToolResult(string id, JsonNode content, bool isError)
    {
        var message = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content, ["is_error"] = isError }) };
        var uuid = Record(new() { ["type"] = "user", ["message"] = message.DeepClone() });
        Emit(new() { ["type"] = "user", ["message"] = message, ["parent_tool_use_id"] = null, ["session_id"] = sessionId, ["uuid"] = uuid });
    }

    private void StateChanged(string state)
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS") == "1") Emit(new() { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = state, ["session_id"] = sessionId });
    }

    private void Emit(JsonObject line) { lock (output) output.WriteLine(line.ToJsonString()); }

    /// <summary>Appends one line to the session's transcript, linked to the line before it as Claude Code links them; its uuid.</summary>
    private string Record(JsonObject fields)
    {
        var uuid = Guid.NewGuid().ToString();
        var line = new JsonObject { ["parentUuid"] = lastUuid, ["isSidechain"] = false, ["userType"] = "external", ["cwd"] = cwd, ["sessionId"] = sessionId, ["version"] = "2.1.271", ["uuid"] = uuid, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
        foreach (var (name, value) in fields) line[name] = value?.DeepClone();
        lastUuid = uuid;
        Append(Path.Combine(root, "claude-config", "projects", key, sessionId + ".jsonl"), line);
        return uuid;
    }

    private void Log(JsonObject entry) => Append(Path.Combine(root, "logs", key + ".jsonl"), entry);

    private static void Append(string path, JsonObject line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line.ToJsonString() + "\n", new UTF8Encoding(false));
    }

    private static string TextOf(JsonNode prompt) => prompt is JsonValue value && value.TryGetValue<string>(out var text) ? text
        : string.Join("\n", (prompt as JsonArray ?? []).OfType<JsonObject>().Where(block => (string?)block["type"] == "text").Select(block => (string?)block["text"] ?? ""));

    private int Number() => ++counter;
    private string ToolUseId() => "toolu_fake_" + Number();

    private int Finish(int code)
    {
        foreach (var server in servers.Values) server.Close();
        Log(new() { ["event"] = "exit", ["code"] = code });
        return code;
    }

    /// <summary>One stdio MCP server from the config, spoken to over JSON-RPC lines with the environment this process inherited.</summary>
    private sealed class Connection
    {
        private readonly Process process;
        private int nextId = 1;
        internal readonly List<string> Tools;

        internal Connection(string command, string[] arguments)
        {
            var info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
            // A binary run by dotnet names its assembly first.
            if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new InvalidOperationException("The MCP server did not start.");
            _ = process.StandardError.ReadToEndAsync();
            if (Request("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.1.271" } })?["result"] is null)
                throw new InvalidOperationException("The MCP server did not initialize.");
            Send(new() { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            Tools = (Request("tools/list", new JsonObject())?["result"]?["tools"] as JsonArray ?? []).Select(tool => (string?)tool?["name"] ?? "").ToList();
        }

        /// <summary>The <c>tools/call</c> result, or an error result when none came.</summary>
        internal JsonObject Call(string tool, JsonObject arguments)
        {
            if (Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }) is not { } answer) return Error("mighty-delegation closed without answering.");
            return answer["result"] as JsonObject is { } result ? (JsonObject)result.DeepClone() : Error((string?)answer["error"]?["message"] ?? "mighty-delegation answered with an error.");
        }

        private static JsonObject Error(string text) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = true };

        internal void Close() { try { process.StandardInput.Close(); process.WaitForExit(10_000); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { } }

        private void Send(JsonObject message) { process.StandardInput.WriteLine(message.ToJsonString()); process.StandardInput.Flush(); }

        private JsonObject? Request(string method, JsonObject parameters)
        {
            var id = nextId++;
            Send(new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            while (process.StandardOutput.ReadLine() is { } line)
                if (JsonNode.Parse(line) is JsonObject message && message["id"] is JsonValue value && value.TryGetValue<int>(out var number) && number == id) return message;
            return null;
        }
    }
}
