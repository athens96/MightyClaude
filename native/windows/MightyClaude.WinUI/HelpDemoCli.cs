using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyClaude.WinUI;

/// <summary>
/// The help capture's stand-in for the <c>claude</c> and <c>codex</c> CLIs (MainWindow.HelpCapture.cs), started through the
/// real run path as <c>--help-demo-cli &lt;script.json&gt; &lt;CLI arguments…&gt;</c>. Nothing runs and nothing goes on the network
/// but the run's own loopback mod bridge: a run prints the demo profile's canned stream lines for the request it was given, the
/// way the Mac's capture replays them through its parser, so the panes show what a real run would. The script is
/// <c>{"runs": [{"provider", "input", "frames", "marker"}]}</c>; the run whose input the prompt holds plays, and its
/// <c>marker</c> file is written once every line is out. A Claude run then waits for its input to close, as the CLI does while
/// the host may still answer it; a Codex run (<c>exec --json</c>) leaves at once.
/// </summary>
internal static class HelpDemoCli
{
    internal const string Argument = "--help-demo-cli";

    internal static int Run(string scriptPath, string[] arguments)
    {
        if (arguments.Contains("--version")) { Console.WriteLine("2.1.271 (Claude Code)"); return 0; }
        // The app's model-list probe: leaving at once makes it use its built-in list.
        if (arguments.Contains("--no-session-persistence")) return 0;
        var runs = JsonNode.Parse(File.ReadAllText(scriptPath))?["runs"]?.AsArray() ?? [];
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        JsonNode? Find(string provider, string prompt) => runs.LastOrDefault(run => (string?)run?["provider"] == provider && (string?)run?["input"] is { Length: > 0 } text && prompt.Contains(text, StringComparison.Ordinal));
        void Play(JsonNode run)
        {
            foreach (var frame in run["frames"]?.AsArray() ?? []) { output.WriteLine(frame?.ToJsonString() ?? "{}"); Thread.Sleep(15); }
            if ((string?)run["marker"] is { Length: > 0 } marker) File.WriteAllText(marker, "");
        }
        if (arguments.Contains("exec"))
        {
            var prompt = input.ReadToEnd();
            if (Find("codex", prompt) is { } run) Play(run);
            return 0;
        }
        var played = false;
        while (input.ReadLine() is { } line)
        {
            JsonNode? message;
            try { message = JsonNode.Parse(line); } catch (JsonException) { continue; }
            if ((string?)message?["type"] == "control_request" && (string?)message["request_id"] is { } id && (string?)message["request"]?["subtype"] == "initialize")
            {
                output.WriteLine(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = id, ["response"] = new JsonObject { ["commands"] = new JsonArray(), ["models"] = new JsonArray() } } }.ToJsonString());
                continue;
            }
            if ((string?)message?["type"] != "user" || played) continue;
            var content = message["message"]?["content"];
            var prompt = content is JsonValue ? (string?)content ?? "" : string.Join("\n", (content?.AsArray() ?? []).Select(part => (string?)part?["text"] ?? ""));
            if (Find("claude", prompt) is not { } run) continue;
            played = true;
            SayStarted(run);
            Play(run);
        }
        return 0;
    }

    /// <summary>One <c>session.start</c> to the run's mod bridge, as the real CLI's mod sends, so the run does not report its mod silent.</summary>
    private static void SayStarted(JsonNode run)
    {
        if (Environment.GetEnvironmentVariable("MIGHTY_CLAUDE_BRIDGE_URL") is not { Length: > 0 } url || Environment.GetEnvironmentVariable("MIGHTY_CLAUDE_BRIDGE_TOKEN") is not { Length: > 0 } token
            || Environment.GetEnvironmentVariable("MIGHTY_CLAUDE_RUN_ID") is not { Length: > 0 } id) return;
        var session = run["frames"]?.AsArray().Select(frame => (string?)frame?["session_id"]).FirstOrDefault(value => value is { Length: > 0 }) ?? Guid.NewGuid().ToString();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(new JsonObject { ["version"] = 1, ["runId"] = id, ["claudeSessionId"] = session, ["event"] = "session.start" }.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new("Bearer", token);
            client.Send(request).Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }
}
