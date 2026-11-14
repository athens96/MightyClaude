using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

/// <summary>
/// The files the app ships for the agents' runs (the Mac bundle's Resources/Agents, shared, not copied): pi's <c>ask_user</c> extension and
/// opencode's question instructions. The app finds them beside itself (<c>Agents\</c>); null when one is missing, and the run goes without it.
/// </summary>
public static class AgentFiles
{
    /// <summary>Overrides where the files are looked for: when set, the only folder looked in.</summary>
    public static string? Directory { get; set; }
    public static string? Find(string name)
    {
        var path = Path.Combine(Directory ?? Path.Combine(AppContext.BaseDirectory, "Agents"), name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// OpenCode 2 (<c>@opencode/cli</c>, <c>opencode acp</c>) as the app runs it (M/MightyCore/OpencodeSupport.swift): which version it takes, the
/// pane's permission mode as OpenCode 2's own rules for the run, and the settings sent as ACP config options.
/// </summary>
public static class OpencodeAgent
{
    /// <summary>The first OpenCode 2 release: an older <c>opencode</c> is OpenCode 1 (<c>opencode-ai</c>), whose files, permissions and credentials differ, and is replaced rather than run.</summary>
    public const int MinimumMajor = 2;
    /// <summary>The app's instruction file for a run: opencode's model asks through its <c>question</c> tool (the pane's card) when it offers choices.</summary>
    public const string QuestionInstructionsName = "opencode-questions.md";

    /// <summary>Whether <c>opencode --version</c> (<c>opencode v2.2.7</c>; OpenCode 1 printed <c>1.19.5</c>) names OpenCode 2.</summary>
    public static bool IsSupported(string? version) => version is not null && SetupProber.VersionNumbers(version) is [var major, ..] && major >= MinimumMajor;

    /// <summary>
    /// The pane's permission mode as OpenCode 2's ordered rules (<c>permissions</c>, the last match wins), given to the run alone in
    /// <c>OPENCODE_CONFIG_CONTENT</c> after the user's own config. OpenCode 2 allows everything by default, the folders outside the project
    /// too; what these leave at "ask" comes to the pane as the approval card. Plan sets no edit rule, so the plan agent's own edit denial stays.
    /// </summary>
    public static IReadOnlyList<(string Action, string Effect)> PermissionRules(string mode)
    {
        string[] outside = ["shell", "webfetch", "websearch", "subagent", "external_directory"];
        return mode switch
        {
            "fullAccess" => [("*", "allow")],
            "acceptEdits" => [("edit", "allow"), .. outside.Select(action => (action, "ask"))],
            "plan" => [.. outside.Select(action => (action, "ask"))],
            _ => [("edit", "ask"), .. outside.Select(action => (action, "ask"))],
        };
    }

    /// <summary>The run's <c>OPENCODE_CONFIG_CONTENT</c>: the rules, and the question instructions (opencode joins every source's <c>instructions</c>, so the user's own stay).</summary>
    public static string ConfigContent(string mode, string? instructions)
    {
        var content = new JsonObject { ["permissions"] = new JsonArray([.. PermissionRules(mode).Select(rule => new JsonObject { ["action"] = rule.Action, ["effect"] = rule.Effect, ["resource"] = "*" })]) };
        if (instructions is not null) content["instructions"] = new JsonArray(JsonValue.Create(instructions));
        return content.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>The settings a run sends as <c>session/set_config_option</c> before its prompt.</summary>
    public static IReadOnlyList<(string Id, string Value)> ConfigValues(StartRunRequest request)
    {
        var values = new List<(string, string)>();
        if (request.Model != "default") values.Add(("model", request.Model));
        if (request.Settings!.Effort != "default") values.Add(("effort", request.Settings.Effort));
        // opencode's two primary agents: build (the default, left as the session has it) and plan, which cannot edit.
        if (request.Settings.PermissionMode == "plan") values.Add(("mode", "plan"));
        return values;
    }
}

/// <summary>
/// Hermes Agent (<c>hermes acp</c>) as the app runs it (M/MightyCore/HermesSupport.swift HermesAgent): its version line, the pane's permission mode
/// as the session's edit policy and <c>HERMES_YOLO_MODE</c>, the settings sent as ACP config options, the provider its <c>initialize</c>
/// names, its stderr kept out of the pane, and the picker its sessions offer, kept for the model menu.
/// </summary>
public static class HermesAgent
{
    /// <summary>The ACP method id Hermes always advertises: its terminal setup, not a sign-in.</summary>
    public const string SetupMethod = "hermes-setup";
    public const string ModelOption = "model", EffortOption = "reasoning_effort", EditPolicyOption = "edit_approval_policy";
    /// <summary>The picker the last Hermes session offered, in the app's profile folder (never in Hermes's own).</summary>
    public const string ModelCacheFileName = "hermes-models.json";

    /// <summary><c>hermes --version</c> prints its version first ("Hermes Agent v0.23.1 (2026.9.24) · stable"), then its install folder, Python and update status.</summary>
    public static string VersionLine(string output) => output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";

    /// <summary>Hermes's edit approval policy for the pane's mode (ACP modes Default / Accept Edits / Don't Ask).</summary>
    public static string EditPolicy(string mode) => mode switch { "acceptEdits" => "workspace_session", "fullAccess" => "session", _ => "ask" };

    /// <summary>
    /// The run's environment: full access is Hermes's <c>--yolo</c> for a session the run creates, given as its documented
    /// <c>HERMES_YOLO_MODE</c>; any other mode removes one the user's environment set, so the pane's choice holds.
    /// </summary>
    public static void Apply(string mode, IDictionary<string, string> environment)
    {
        if (mode == "fullAccess") environment["HERMES_YOLO_MODE"] = "1";
        else foreach (var key in environment.Keys.Where(key => key.Equals("HERMES_YOLO_MODE", StringComparison.OrdinalIgnoreCase)).ToList()) environment.Remove(key);
    }

    /// <summary>The settings a run sends before its prompt: the model (Hermes's picker id, <c>provider:model</c>), the reasoning effort, and the edit policy, always, so a resumed session follows the pane.</summary>
    public static IReadOnlyList<(string Id, string Value)> ConfigValues(StartRunRequest request)
    {
        var values = new List<(string, string)>();
        if (request.Model != "default") values.Add((ModelOption, request.Model));
        if (request.Settings!.Effort != "default") values.Add((EffortOption, request.Settings.Effort));
        values.Add((EditPolicyOption, EditPolicy(request.Settings.PermissionMode)));
        return values;
    }

    /// <summary>
    /// Whether a run continues the pane's Hermes session, and what the pane is told (M/ProcessRunner.swift): Hermes fixes <c>--yolo</c> when a
    /// session starts, so one that started with it is not continued by a pane that asks (a new session, run.notice.hermesFullAccessSession),
    /// and one that started without it keeps asking in a full-access pane (continued, run.notice.hermesAskingSession); a session whose first
    /// request never got an answer (<c>message_count</c> 0) keeps the provider it was made with and is not continued. A session whose policy
    /// receipt cannot be read may have started with <c>--yolo</c>: a pane that asks starts a new one (run.notice.hermesUnknownSession), a
    /// full-access pane continues it.
    /// </summary>
    public static (string? ResumeId, string? NoticeKey) Resume(string resumeId, string mode, int? messageCount, bool? startedWithFullAccess)
    {
        var full = mode == "fullAccess";
        if (messageCount == 0) return (null, null);
        if (startedWithFullAccess == true && !full) return (null, "run.notice.hermesFullAccessSession");
        if (startedWithFullAccess is null && !full) return (null, "run.notice.hermesUnknownSession");
        return (resumeId, startedWithFullAccess == false && full ? "run.notice.hermesAskingSession" : null);
    }

    /// <summary>The provider an <c>initialize</c> result's <c>authMethods</c> name: the entry that is not the terminal setup.</summary>
    public static string? Provider(JsonObject result)
    {
        foreach (var method in (result["authMethods"] as JsonArray ?? []).OfType<JsonObject>())
            if (method["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.String && id.GetValue<string>() is { Length: > 0 } name && name != SetupMethod
                && (method["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String || type.GetValue<string>() == "agent")) return name;
        return null;
    }

    /// <summary>
    /// Keeps Hermes's own log lines (<c>2026-11-11 01:18:54 [INFO] …</c>) and the Python traceback it writes for every request it answers with an
    /// error out of the pane, which shows the ACP error itself. Anything else (a start-up failure's own words) passes.
    /// </summary>
    public sealed class StderrFilter
    {
        private bool inTraceback;
        private static readonly Regex LogLine = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(,[0-9]+)? \[[A-Z]+\]", RegexOptions.CultureInvariant);
        /// <summary>Whether one complete stderr line reaches the pane.</summary>
        public bool Keep(string line)
        {
            if (LogLine.IsMatch(line)) { inTraceback = false; return false; }
            if (line.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal)) { inTraceback = true; return false; }
            if (line.StartsWith("During handling of the above exception", StringComparison.Ordinal) || line.StartsWith("The above exception was the direct cause", StringComparison.Ordinal)) return false;
            if (!inTraceback) return line.Trim().Length > 0;
            // The frames are indented; the first line that is not is the exception itself, the traceback's last.
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) inTraceback = false;
            return false;
        }
    }

    /// <summary>The <c>model</c> option of a session's <c>configOptions</c> as picker rows: <c>provider:model</c> ids with Hermes's own names.</summary>
    public static List<ModelOption> Models(JsonNode? configOptions)
    {
        var models = new List<ModelOption>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        if ((configOptions as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => Text(o["id"]) == ModelOption) is not { } option) return models;
        void Add(JsonObject entry)
        {
            if (Text(entry["value"]) is not { } value || value == "default" || !Wire.Model(value) || models.Count >= Wire.ListedModelLimit("hermes") || !seen.Add(value)) return;
            var name = ActivitySupport.Clean(Text(entry["name"]) ?? value, 160, singleLine: true);
            var provider = value.IndexOf(':') is var colon and > 0 ? value[..colon] : "";
            models.Add(new(value, name.Length == 0 ? value : name, provider.Length == 0 ? "" : Locale.Get("provider.catalog.hermesModelRow", new Dictionary<string, string> { ["provider"] = provider })));
        }
        foreach (var entry in (option["options"] as JsonArray ?? []).OfType<JsonObject>().Take(4_096))
        {
            Add(entry);
            foreach (var inner in (entry["options"] as JsonArray ?? []).OfType<JsonObject>().Take(4_096)) Add(inner);
        }
        return models;
    }

    /// <summary>Keeps a session's picker for the model menu; an empty one changes nothing. Without a file (the checks) nothing is kept.</summary>
    public static void SaveModels(JsonNode? configOptions, string? path)
    {
        var models = Models(configOptions);
        if (path is null || models.Count == 0) return;
        var data = JsonSerializer.SerializeToUtf8Bytes(models, Wire.Json);
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, data); File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>Without a provider the kept picker is a past sign-in's: it goes.</summary>
    public static void ForgetModels(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>"Follow Hermes's settings" and, once a session ran, the models its picker offered.</summary>
    public static ModelCatalog CachedCatalog(string? path)
    {
        var fallback = ProviderCatalog.Fallback("hermes");
        var empty = new ModelCatalog("fallback", [fallback.Models[0]], Locale.Get("provider.catalog.hermesEmpty"));
        List<ModelOption>? stored = null;
        try { if (path is not null && new FileInfo(path) is { Exists: true, Length: <= 2_097_152 }) stored = JsonSerializer.Deserialize<List<ModelOption>>(File.ReadAllBytes(path), Wire.Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var seen = new HashSet<string>(StringComparer.Ordinal) { "default" };
        var models = (stored ?? []).Take(Wire.ListedModelLimit("hermes")).Where(m => m is not null && Wire.Model(m.Value) && seen.Add(m.Value))
            .Select(m => new ModelOption(m.Value, ActivitySupport.Clean(m.DisplayName, 160, singleLine: true) is { Length: > 0 } name ? name : m.Value, ActivitySupport.Clean(m.Description, 2_400))).ToList();
        return models.Count == 0 ? empty : new ModelCatalog("cli", [fallback.Models[0], .. models], Locale.Get("provider.catalog.hermesSource"));
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

/// <summary>
/// Hermes's clarify tool for the app's ACP sessions (M/MightyCore/HermesSupport.swift HermesQuestionTool): ACP's toolset leaves clarify out, and
/// the one way to turn it on is Hermes's config key <c>platform_toolsets.acp</c>. Once per app launch for each Hermes (its executable and data
/// folder), before its first run, an unset key is set to <c>["hermes-acp","clarify","x_search"]</c> through Hermes's own <c>config</c> command;
/// a list the user saved is left alone (the pane says how to add clarify when it lacks it). Nothing else of Hermes's config is read or written.
/// </summary>
public static class HermesQuestionTool
{
    public const string Key = "platform_toolsets.acp";
    public const string Tool = "clarify";
    public enum Outcome { Enabled, Missing, Ready }
    /// <summary>
    /// What follows the user's request in a Hermes prompt while clarify is on: Hermes has no per-run system prompt, and without these words its
    /// model lists choices as text. After the request, not before it: Hermes titles a session by the start of its first message.
    /// </summary>
    public const string PromptNote = "[Note from the Mighty Code app, not from the user: this pane shows your clarify tool as a card the user answers with a click. Whenever you present the user with options to choose from (a quiz or multiple-choice problem, a preference, a decision), call clarify with the question and the choices instead of listing them in your reply.]";
    /// <summary>A stored request without the note, as the session list shows it.</summary>
    public static string WithoutNote(string text) => text.IndexOf("[Note from the Mighty Code app, not from the user:", StringComparison.Ordinal) is var at and >= 0 ? text[..at].Trim() : text;

    /// <summary>
    /// What the unset key becomes. Saving a list makes Hermes stop adding two things it adds to the unsaved default, so they are named:
    /// <c>x_search</c> (added when xAI credentials exist; without them the tool's own check still leaves it out) and <c>kanban</c> when the
    /// config's legacy top-level <c>toolsets</c> opts into it.
    /// </summary>
    public static string[] Value(IReadOnlyCollection<string> legacyToolsets) => ["hermes-acp", Tool, "x_search", .. legacyToolsets.Contains("kanban") ? new[] { "kanban" } : []];

    /// <summary>A <c>config get --json</c> answer as toolset names: a list, or the list a string spells (older builds saved <c>'["a", "b"]'</c> as text).</summary>
    public static List<string>? Names(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonArray list) return list.Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : item?.ToJsonString() ?? "null").ToList();
            if (node is JsonValue text && text.GetValueKind() == JsonValueKind.String && text.GetValue<string>().Trim().StartsWith('[') && JsonNode.Parse(text.GetValue<string>().Replace('\'', '"')) is JsonArray spelled)
                return spelled.Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : item?.ToJsonString() ?? "null").ToList();
        }
        catch (JsonException) { }
        return null;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Outcome> Looked = [];
    /// <summary>
    /// Once per app launch for each Hermes. <c>First</c> is what this launch found the first time (the pane says it once, with the list set when
    /// it was <see cref="Outcome.Enabled"/>); <c>Asks</c> whether Hermes's model has the tool as far as the app knows.
    /// </summary>
    public static async Task<(Outcome? First, IReadOnlyList<string>? List, bool Asks)> EnsureAsync(CliCommand command, IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken token)
    {
        var identity = command.Binary + "\n" + (environment.GetValueOrDefault("HERMES_HOME") ?? "");
        lock (Gate) if (Looked.TryGetValue(identity, out var known)) return (null, null, known != Outcome.Missing);
        // A Hermes that could not answer is asked again by the next run.
        if (await ApplyAsync(command, environment, workingDirectory, token) is not { } outcome) return (null, null, false);
        bool first;
        lock (Gate) first = Looked.TryAdd(identity, outcome.Outcome);
        return (first ? outcome.Outcome : null, first ? outcome.List : null, outcome.Outcome != Outcome.Missing);
    }

    /// <summary>Forgets what this launch found (the checks run more than one fake Hermes).</summary>
    public static void Reset() { lock (Gate) Looked.Clear(); }

    /// <summary>Null when Hermes could not be asked (no answer, a managed or unreadable config).</summary>
    public static async Task<(Outcome Outcome, IReadOnlyList<string>? List)?> ApplyAsync(CliCommand command, IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken token)
    {
        if (await RunAsync(command, ["config", "get", "--json", Key], environment, workingDirectory, token) is not { } current) return null;
        if (current.Code == 0) return Names(current.Output) is { } names ? (names.Contains(Tool) ? Outcome.Ready : Outcome.Missing, null) : null;
        // Only Hermes's own "not set" answer is an unset key; any other failure changes nothing.
        if (current.Code != 1 || current.Output.Trim().Length > 0 || !current.Error.Contains("not set: " + Key, StringComparison.Ordinal)) return null;
        var legacy = await RunAsync(command, ["config", "get", "--json", "toolsets"], environment, workingDirectory, token);
        var list = Value(legacy is { Code: 0 } found ? Names(found.Output) ?? [] : []);
        var json = JsonSerializer.Serialize(list);
        if (await RunAsync(command, ["config", "set", Key, json], environment, workingDirectory, token) is not { Code: 0 }
            || await RunAsync(command, ["config", "get", "--json", Key], environment, workingDirectory, token) is not { Code: 0 } check || Names(check.Output)?.SequenceEqual(list) != true) return null;
        return (Outcome.Enabled, list);
    }

    private static async Task<(int Code, string Output, string Error)?> RunAsync(CliCommand command, string[] arguments, IReadOnlyDictionary<string, string> environment, string workingDirectory, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var env = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase) { ["NO_COLOR"] = "1" };
            await using var child = ProviderCatalog.StartCli(command.Binary, command.Prefix.Concat(arguments), workingDirectory, env);
            child.Input.Close();
            var error = child.Error.ReadToEndAsync(timeout.Token);
            var output = await child.Output.ReadToEndAsync(timeout.Token);
            var code = await child.Completion.WaitAsync(timeout.Token);
            return (code, output.Length > 1_048_576 ? output[..1_048_576] : output, await error);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
