using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// Where pi (<c>@mariozechner/pi-coding-agent</c>) keeps its files, and what the app reads of them: whether any sign-in exists
/// (<c>~/.pi/agent/auth.json</c>, or an API key in the environment) and the session records for "resume an earlier session". The
/// app never writes any of it and never shows a credential (M/MightyCore/PiSupport.swift).
/// </summary>
public static class PiAgent
{
    /// <summary>The app's question tool for pi (the shipped Agents/pi-ask-user.js, the Mac bundle's own file): loaded with <c>--extension</c> for each run.</summary>
    public const string QuestionExtensionName = "pi-ask-user.js";
    /// <summary>The tool that extension registers; the read-only allowlist names it, or <c>--tools</c> would leave it off.</summary>
    public const string QuestionTool = "ask_user";
    /// <summary>The API key variables pi reads. Any one non-empty counts as a sign-in; the Cloudflare and Azure companions alone do not.</summary>
    public static readonly string[] ApiKeyVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_OAUTH_TOKEN", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY", "DEEPSEEK_API_KEY", "GEMINI_API_KEY",
        "GROQ_API_KEY", "CEREBRAS_API_KEY", "XAI_API_KEY", "FIREWORKS_API_KEY", "OPENROUTER_API_KEY", "AI_GATEWAY_API_KEY", "ZAI_API_KEY",
        "MISTRAL_API_KEY", "MINIMAX_API_KEY", "MINIMAX_CN_API_KEY", "MOONSHOT_API_KEY", "OPENCODE_API_KEY", "KIMI_API_KEY", "CLOUDFLARE_API_KEY",
        "XIAOMI_API_KEY", "XIAOMI_TOKEN_PLAN_CN_API_KEY", "XIAOMI_TOKEN_PLAN_AMS_API_KEY", "XIAOMI_TOKEN_PLAN_SGP_API_KEY", "HF_TOKEN",
        "AWS_BEARER_TOKEN_BEDROCK",
    ];

    private static bool Absolute(string path) => path.StartsWith('/') || Path.IsPathFullyQualified(path);

    /// <summary><c>$PI_CODING_AGENT_DIR</c> (when absolute), else <c>~/.pi/agent</c>.</summary>
    public static string Directory(string home, IReadOnlyDictionary<string, string> environment) =>
        environment.GetValueOrDefault("PI_CODING_AGENT_DIR") is { Length: > 0 } given && Absolute(given) ? given : Path.Combine(home, ".pi", "agent");
    public static string AuthFile(string home, IReadOnlyDictionary<string, string> environment) => Path.Combine(Directory(home, environment), "auth.json");

    /// <summary>What <c>auth.json</c> holds: the providers with a credential, and whether one of them is a subscription (OAuth) sign-in rather than an API key.</summary>
    public static (List<string> Providers, bool Subscription) Credentials(string? authJson)
    {
        if (authJson is null) return ([], false);
        try
        {
            using var document = JsonDocument.Parse(authJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ([], false);
            var entries = document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).ToList();
            return (entries.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Take(16).Select(n => Wire.Clean(n, 64)).ToList(), entries.Any(p => p.Value.Text("type") == "oauth"));
        }
        catch (JsonException) { return ([], false); }
    }
    /// <summary>The API key variables set to something in <paramref name="environment"/>.</summary>
    public static List<string> EnvironmentKeys(IReadOnlyDictionary<string, string> environment) =>
        ApiKeyVariables.Where(name => environment.GetValueOrDefault(name) is { Length: > 0 }).ToList();

    /// <summary>The folder of <c>sessions/</c> a working directory's sessions are kept in: <c>--&lt;path with / \ : turned into -&gt;--</c>.</summary>
    public static string SessionFolder(string path) =>
        "--" + new string(path.TrimStart('/', '\\').Select(c => c is '/' or '\\' or ':' ? '-' : c).ToArray()) + "--";
    /// <summary>
    /// Where pi keeps the sessions of <paramref name="workspacePaths"/>: <c>$PI_CODING_AGENT_SESSION_DIR</c> as it is, else one
    /// folder per path under <c>&lt;agent dir&gt;/sessions</c>.
    /// </summary>
    public static List<string> SessionFolders(IEnumerable<string> workspacePaths, string home, IReadOnlyDictionary<string, string> environment)
    {
        if (environment.GetValueOrDefault("PI_CODING_AGENT_SESSION_DIR") is { Length: > 0 } given && Absolute(given)) return [given];
        var sessions = Path.Combine(Directory(home, environment), "sessions");
        var seen = new HashSet<string>();
        return workspacePaths.OrderBy(p => p, StringComparer.Ordinal).Select(SessionFolder).Where(seen.Add).Select(name => Path.Combine(sessions, name)).ToList();
    }
    /// <summary>The record of session <paramref name="id"/>: <c>&lt;timestamp&gt;_&lt;id&gt;.jsonl</c> in one of the workspace's folders.</summary>
    public static string? SessionFile(string id, string workspacePath, string home, IReadOnlyDictionary<string, string> environment)
    {
        if (!Wire.Identifier(id)) return null;
        foreach (var folder in SessionFolders(ResumableSessions.WorkspacePaths(workspacePath), home, environment))
            foreach (var (path, _) in RecordFiles.Files(folder, ".jsonl"))
                if (Path.GetFileName(path).EndsWith("_" + id + ".jsonl", StringComparison.Ordinal)) return path;
        return null;
    }
    /// <summary>A session's id from its file name (<c>2024-12-03T14-00-00-000Z_&lt;uuid&gt;.jsonl</c>).</summary>
    internal static string? SessionID(string fileName)
    {
        if (!fileName.EndsWith(".jsonl", StringComparison.Ordinal) || fileName.IndexOf('_') is not (>= 0 and var underscore)) return null;
        var id = fileName[(underscore + 1)..^".jsonl".Length];
        return Wire.Identifier(id) ? id : null;
    }
}

public static class PiAccounts
{
    /// <summary>The <c>pi --list-models</c> text of a pi with no model to offer.</summary>
    public static bool HasNoModels(string output) => output.Contains("no models available", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// pi has no status command. It is signed in when it can list a model; what it is signed in with comes from <c>auth.json</c>
    /// (a subscription or API keys) or from the environment's API keys, and a list from <c>models.json</c> alone (a local server)
    /// still counts. "No models available" reads signed out; a failed or timed-out call stays unknown.
    /// </summary>
    public static CliAccountStatus Status(int exitCode, string output, string? authJson, IReadOnlyDictionary<string, string> environment, bool timedOut = false)
    {
        if (HasNoModels(output)) return new CliAccountStatus { Provider = "pi", LoggedIn = false, CanSignOut = false };
        if (exitCode != 0 || timedOut)
            return new CliAccountStatus { Provider = "pi", Detail = Locale.Get(timedOut ? "settings.cliAccounts.detailPiTimeout" : "settings.cliAccounts.detailPiUnknown"), CanSignOut = false };
        var stored = PiAgent.Credentials(authJson);
        if (stored.Providers.Count > 0)
            return new CliAccountStatus
            {
                Provider = "pi", LoggedIn = true, Method = Locale.Get(stored.Subscription ? "cliAccounts.method.piSubscription" : "windows.cli.method.apiKey"),
                MethodId = stored.Subscription ? CliAccountMethod.PiSubscription : CliAccountMethod.PiApiKey, Account = string.Join(", ", stored.Providers),
                Detail = Locale.Get("settings.cliAccounts.detailPiSignOut"), CanSignOut = false,
            };
        if (PiAgent.EnvironmentKeys(environment).Count > 0)
            return new CliAccountStatus { Provider = "pi", LoggedIn = true, Method = Locale.Get("cliAccounts.method.piEnvironmentKey"), MethodId = CliAccountMethod.PiApiKey, CanSignOut = false };
        return new CliAccountStatus { Provider = "pi", LoggedIn = true, Method = Locale.Get("cliAccounts.method.piModelsFile"), MethodId = CliAccountMethod.PiModels, CanSignOut = false };
    }
}

/// <summary>
/// pi's RPC events (M/MightyCore/ExecutionGraphTracker.swift consumePi). pi has no sub-agents: the graph is the request's own block.
/// Each assistant message adds its usage; the latest message with text is the answer.
/// </summary>
public sealed partial class ExecutionGraphTracker
{
    private void ConsumePi(JsonElement value)
    {
        if (value.Text("type") != "message_end" || MetadataJson.Property(value, "message") is not { ValueKind: JsonValueKind.Object } message || message.Text("role") != "assistant") return;
        var key = "pi:" + (message.TryGetProperty("responseId", out var response) && response.ValueKind != JsonValueKind.Null ? response.ToString() : message.TryGetProperty("timestamp", out var stamp) ? stamp.ToString() : "message");
        if (MetadataJson.Property(message, "usage") is { ValueKind: JsonValueKind.Object } usage)
        {
            var mapped = new System.Text.Json.Nodes.JsonObject();
            foreach (var (source, target) in new[] { ("input", "input_tokens"), ("output", "output_tokens"), ("cacheRead", "cache_read_input_tokens"), ("cacheWrite", "cache_creation_input_tokens") })
                if (MetadataJson.Property(usage, source) is { ValueKind: JsonValueKind.Number } number) mapped[target] = System.Text.Json.Nodes.JsonNode.Parse(number.GetRawText());
            if (GraphTokenUsage.Parse(JsonSerializer.SerializeToElement(mapped)) is { } tokens) RecordUsage(tokens, key, mainID, Wire.Model(message.Text("model")) ? message.Text("model") : null);
        }
        var text = string.Join("\n", (MetadataJson.Property(message, "content") is { ValueKind: JsonValueKind.Array } blocks ? blocks.EnumerateArray() : []).Where(b => b.Text("type") == "text").Select(b => b.Text("text")).OfType<string>());
        if (text.Length > 0) Update(mainID, node => node with { Output = text });
    }
}
