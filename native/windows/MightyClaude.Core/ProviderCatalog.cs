using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record CliCommand(string Binary, string[] Prefix, string? Version = null);

public sealed class ProviderCatalog(Func<string, CancellationToken, Task<CliCommand?>>? finder = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource closing = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, CliCommand?> commands = [];
    private RuntimeInfo? cached;
    private DateTimeOffset refreshed;
    private Task? shutdown;
    public static string Name(string provider) => provider == "claude" ? "Claude Code" : provider == "codex" ? "Codex CLI" : "Gemini CLI";
    public static string[] PermissionModes(string provider, bool includeAuto = true) => provider == "codex" ? ["manual", "acceptEdits", "fullAccess"] : provider == "claude" && includeAuto ? ["plan", "manual", "acceptEdits", "auto", "fullAccess"] : ["manual", "plan", "acceptEdits", "fullAccess"];
    public static ProviderCapabilities Capabilities(string provider, string? version = null) => new(provider != "gemini", PermissionModes(provider, provider == "claude" && SupportsMods(version)), provider == "claude", provider == "claude", true, provider == "codex", provider == "codex", provider == "codex", true);
    public static RunSettings NormalizeSettings(string provider, RunSettings? value)
    {
        var v = value ?? new(); var caps = Capabilities(provider);
        var permission = PermissionModes(provider).Contains(v.PermissionMode) ? v.PermissionMode : "manual";
        return new(caps.Effort && Wire.Efforts.Contains(v.Effort) ? v.Effort : "default", permission, caps.MaxTurns && v.MaxTurns is >= 1 and <= 1000 ? v.MaxTurns : null, caps.MaxBudgetUsd && v.MaxBudgetUsd is > 0 and <= 10000 ? v.MaxBudgetUsd : null, caps.FastMode && v.FastMode, caps.WebSearch && v.WebSearch is "disabled" or "cached" or "live" ? v.WebSearch : "default", caps.NetworkAccess && permission == "acceptEdits" && v.NetworkAccess);
    }
    public static string? RemoteSettingsProblem(RunSettings settings, ProviderCapabilities? capabilities)
    {
        if (settings.PermissionMode == "auto" && capabilities?.PermissionModes?.Contains("auto") != true) return Locale.Get("provider.remote.autoModeUnsupported");
        if (settings.PermissionMode == "fullAccess" && capabilities?.PermissionModes?.Contains("fullAccess") != true) return Locale.Get("provider.remote.fullAccessUnsupported");
        if (settings.FastMode && capabilities?.FastMode != true) return Locale.Get("provider.remote.fastModeUnsupported");
        if (settings.WebSearch != "default" && capabilities?.WebSearch != true) return Locale.Get("provider.remote.webSearchUnsupported");
        if (settings.NetworkAccess && capabilities?.NetworkAccess != true) return Locale.Get("provider.remote.networkAccessUnsupported");
        return null;
    }
    public static ModelCatalog Fallback(string provider)
    {
        var models = new List<ModelOption> { new("default", Locale.Get("provider.fallback.defaultLabel", new Dictionary<string, string> { ["name"] = Name(provider) }), Locale.Get("provider.fallback.defaultDescription")) };
        foreach (var value in provider switch { "claude" => new[] { "best", "fable", "opus", "sonnet", "haiku", "opusplan" }, "codex" => new[] { "gpt-5.6-sol", "gpt-6-astra" }, _ => new[] { "auto", "gemini-3-pro-preview", "gemini-3-flash-preview", "gemini-2.5-pro", "gemini-2.5-flash" } }) models.Add(new(value, value, Locale.Get("provider.fallback.exampleDescription"), SupportsEffort: value.Contains("haiku") ? false : null));
        return new("fallback", models, Locale.Get("provider.fallback.source"));
    }
    public static string[] Efforts(string provider, string model, ModelCatalog catalog) =>
        Efforts(provider, model, catalog, null);

    public static string[] Efforts(string provider, string model, ModelCatalog catalog, IReadOnlyList<RegisteredModelEntry>? registeredModels)
    {
        if (provider == "gemini") return [];
        var row = catalog.Models.FirstOrDefault(m => m.Value == model || m.ResolvedModel == model);
        if (row?.SupportsEffort == false) return [];
        if (row?.SupportedEffortLevels is { } levels) return levels.Where(Wire.Efforts.Contains).Distinct().ToArray();
        if (row is null && registeredModels is not null)
        {
            var reg = registeredModels.FirstOrDefault(r => r.Name == model);
            if (reg is not null)
            {
                if (!reg.SupportsEffort) return [];
                return (reg.SupportedEffortLevels ?? []).Where(Wire.Efforts.Contains).ToArray();
            }
        }
        if (provider == "codex" || model.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return [];
        if (Regex.IsMatch(model, @"(opus|sonnet)[-.]4[-.]6")) return ["low", "medium", "high", "max"];
        return Regex.IsMatch(model, @"^(default|best|fable|opus|sonnet|opusplan)(\[1m\])?$|fable[-.]5|opus[-.](5|4[-.][78])|sonnet[-.]5") ? Wire.Efforts : row?.SupportsEffort == true ? ["low", "medium", "high"] : [];
    }
    public static bool SupportsMods(string? version) => Regex.Match(version ?? "", @"(?<!\d)(\d+)\.(\d+)\.(\d+)(?![\d-])") is { Success: true } m && Version.Parse($"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}") >= new Version(2, 1, 271);

    public async Task<CliCommand?> FindAsync(string provider, CancellationToken token = default)
    {
        lock (commands) if (commands.TryGetValue(provider, out var saved)) return saved;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token);
        var result = finder is null ? await DiscoverAsync(provider, linked.Token) : await finder(provider, linked.Token);
        lock (commands) commands[provider] = result;
        return result;
    }
    public async Task<RuntimeInfo> GetRuntimeAsync(bool force = false)
    {
        await gate.WaitAsync(closing.Token);
        try
        {
            if (!force && cached is not null && DateTimeOffset.UtcNow - refreshed < TimeSpan.FromMinutes(1)) return cached;
            if (force) lock (commands) commands.Clear();
            var providers = new List<ProviderRuntime>();
            foreach (var id in Wire.Providers)
            {
                var command = await FindAsync(id, closing.Token);
                var catalog = command is not null && id != "gemini" ? await ReadModelsAsync(id, command, closing.Token) : Fallback(id);
                providers.Add(new(id, Name(id), command is not null && (id != "claude" || SupportsMods(command.Version)), command?.Version, command is null ? Locale.Get("provider.notInstalled") : id == "claude" && !SupportsMods(command.Version) ? Locale.Get("provider.unsupportedVersion") : Locale.Get("provider.available"), catalog, Capabilities(id, command?.Version)));
            }
            var claude = providers[0];
            cached = new(OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux", "0.1.0", claude.Version is not null, claude.Version, claude.ModelCatalog, providers, new(claude.Available ? "available" : claude.Version is null ? "unavailable" : "unsupported", "2.1.271", claude.Detail));
            refreshed = DateTimeOffset.UtcNow; return cached;
        }
        finally { gate.Release(); }
    }
    private static async Task<CliCommand?> DiscoverAsync(string provider, CancellationToken token)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        var candidates = directories.Select(path => new CliCommand(Path.Combine(path, provider + (OperatingSystem.IsWindows() ? ".exe" : "")), [])).ToList();
        if (OperatingSystem.IsWindows() && provider != "claude")
        {
            var node = directories.Select(path => Path.Combine(path, "node.exe")).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")).FirstOrDefault(File.Exists);
            if (node is not null) foreach (var directory in directories)
                foreach (var entry in provider == "codex" ? new[] { "@openai/codex/bin/codex.js" } : ["@google/gemini-cli/bundle/gemini.js", "@google/gemini-cli/dist/index.js"])
                { var script = Path.Combine(directory, "node_modules", entry.Replace('/', Path.DirectorySeparatorChar)); if (File.Exists(script)) candidates.Add(new(node, [script])); }
        }
        foreach (var candidate in candidates.Where(c => File.Exists(c.Binary)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await using var child = ChildProcess.Start(ChildProcess.StartInfo(candidate.Binary, candidate.Prefix.Concat(["--version"]), Path.GetTempPath(), QuietEnvironment()));
                var stderr = DrainAsync(child.Error, timeout.Token);
                var text = await ReadBoundedAsync(child.Output, 16384, timeout.Token);
                var code = await child.Completion.WaitAsync(timeout.Token); await stderr;
                if (code == 0 && text.Trim().Length > 0) return candidate with { Version = Wire.Clean(text.Trim(), 160) };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }
    public static Dictionary<string, string> QuietEnvironment() => new() { ["DISABLE_AUTOUPDATER"] = "1", ["DISABLE_TELEMETRY"] = "1", ["DISABLE_ERROR_REPORTING"] = "1", ["NO_UPDATE_NOTIFIER"] = "1" };
    public static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) { result.Append(buffer, 0, count); if (result.Length > limit) throw new InvalidDataException(Locale.Get("provider.cliOutputLimit")); }
        return result.ToString();
    }
    public static async Task DrainAsync(StreamReader reader, CancellationToken token) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { } }
    public static async Task<ModelCatalog> ReadModelsAsync(string provider, CliCommand command, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(7));
        var token = timeout.Token;
        var args = provider == "codex" ? new List<string> { "app-server", "--listen", "stdio://" } : ["--print", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json", "--no-session-persistence", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--tools", "", "--safe-mode"];
        var env = QuietEnvironment();
        if (provider == "claude") { env["CLAUDE_CODE_SAFE_MODE"] = "1"; env["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] = "1"; env["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1"; env["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1"; }
        try
        {
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary, command.Prefix.Concat(args), Path.GetTempPath(), env));
            var drain = DrainAsync(child.Error, token); _ = drain.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var requestId = Wire.Id(); var expected = 1; var pages = 0; var models = Fallback(provider).Models.Take(1).ToList();
            await child.Input.WriteLineAsync(provider == "codex" ? JsonSerializer.Serialize(new { id = expected, method = "initialize", @params = new { clientInfo = new { name = "mighty_claude", title = "MightyClaude", version = "0.1.0" } } }, Wire.Json) : JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "initialize" } }, Wire.Json));
            var total = 0;
            await foreach (var line in OutputParser.LinesAsync(child.Output, token))
            {
                total += line.Length; if (total > 2 * 1024 * 1024) break;
                using var document = JsonDocument.Parse(line); var root = document.RootElement;
                if (provider == "claude")
                {
                    if (root.Text("type") != "control_response" || !root.TryGetProperty("response", out var response) || response.Text("request_id") != requestId || !response.TryGetProperty("response", out var payload) || !payload.TryGetProperty("models", out var rows)) continue;
                    foreach (var row in rows.EnumerateArray().Take(128)) if (ReadModel(row, provider) is { } model && model.Value != "default") models.Add(model);
                    return new("cli", models.DistinctBy(m => m.Value).ToList(), Locale.Get("provider.claudeCliSource"));
                }
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number != expected) continue;
                if (root.TryGetProperty("error", out _)) break;
                if (expected == 1)
                {
                    await child.Input.WriteLineAsync("{\"method\":\"initialized\"}");
                    await child.Input.WriteLineAsync(JsonSerializer.Serialize(new { id = ++expected, method = "model/list", @params = new { limit = 64, includeHidden = false } }, Wire.Json));
                }
                else if (root.TryGetProperty("result", out var result) && result.TryGetProperty("data", out var data))
                {
                    foreach (var row in data.EnumerateArray().Take(128 - models.Count)) if (ReadModel(row, provider) is { } model) models.Add(model);
                    if (++pages < 4 && models.Count < 128 && result.Text("nextCursor") is { Length: > 0 and < 4096 } cursor) await child.Input.WriteLineAsync(JsonSerializer.Serialize(new { id = ++expected, method = "model/list", @params = new { limit = 64, includeHidden = false, cursor } }, Wire.Json));
                    else return new("cli", models.DistinctBy(m => m.Value).ToList(), Locale.Get("provider.codexCliSource"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return Fallback(provider);
    }
    private static ModelOption? ReadModel(JsonElement row, string provider)
    {
        var value = row.Text(provider == "codex" ? "model" : "value");
        if (!Wire.Model(value) || row.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) return null;
        string[]? efforts = null;
        if (row.TryGetProperty(provider == "codex" ? "supportedReasoningEfforts" : "supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array) efforts = levels.EnumerateArray().Select(l => provider == "codex" ? l.Text("reasoningEffort") : l.ValueKind == JsonValueKind.String ? l.GetString() : null).Where(s => s is not null && Wire.Efforts.Contains(s)).Cast<string>().Distinct().ToArray();
        var support = row.TryGetProperty("supportsEffort", out var supported) && supported.ValueKind is JsonValueKind.True or JsonValueKind.False ? supported.GetBoolean() : efforts is not null ? efforts.Length > 0 : (bool?)null;
        if (value!.Contains("haiku")) { support = false; efforts = []; }
        return new(value, Wire.Clean(row.Text("displayName") ?? value, 160), Wire.Clean(row.Text("description"), 2400), Wire.Model(row.Text("resolvedModel")) ? row.Text("resolvedModel") : null, support, efforts);
    }
    public static List<string> Arguments(StartRunRequest value, string pluginDirectory, PhaseModelsSnapshot? phaseModels = null)
    {
        var request = value.Validate(); var settings = request.Settings!; var args = new List<string>();
        if (request.Provider == "claude")
        {
            args.AddRange(["--print", "--verbose", "--output-format", "stream-json", "--permission-prompts", "none", "--permission-mode", settings.PermissionMode == "fullAccess" ? "bypassPermissions" : settings.PermissionMode, "--plugin-dir", pluginDirectory]);
            // Build a single --settings env JSON merging effort + phase model aliases.
            var env = new Dictionary<string, string?>();
            if (settings.Effort != "default") { args.AddRange(["--effort", settings.Effort]); env["CLAUDE_CODE_EFFORT_LEVEL"] = settings.Effort; }
            if (phaseModels is not null)
            {
                if (phaseModels.ClaudeOpusAlias != "default") env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = phaseModels.ClaudeOpusAlias;
                if (phaseModels.ClaudeSonnetAlias != "default") env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = phaseModels.ClaudeSonnetAlias;
                if (phaseModels.ClaudeHaikuAlias != "default") env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = phaseModels.ClaudeHaikuAlias;
                if (phaseModels.ClaudeSubagentDefault != "default") env["CLAUDE_CODE_SUBAGENT_MODEL"] = phaseModels.ClaudeSubagentDefault;
            }
            if (env.Count > 0) args.AddRange(["--settings", JsonSerializer.Serialize(new { env }, Wire.Json)]);
            // When the session model is default and claudeMain is set, override with --model.
            if (request.Model == "default" && phaseModels?.ClaudeMain is { } claudeMain && claudeMain != "default")
                args.AddRange(["--model", claudeMain]);
            if (settings.MaxTurns is int turns) args.AddRange(["--max-turns", turns.ToString(CultureInfo.InvariantCulture)]);
            if (settings.MaxBudgetUsd is double budget) args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (request.Provider == "codex")
        {
            var sandbox = settings.PermissionMode == "fullAccess" ? "danger-full-access" : settings.PermissionMode == "acceptEdits" ? "workspace-write" : "read-only";
            args.AddRange(["-c", "approval_policy=\"never\"", "-c", $"sandbox_mode=\"{sandbox}\"", "-c", $"sandbox_workspace_write.network_access={(settings.NetworkAccess ? "true" : "false")}", "-c", $"features.fast_mode={(settings.FastMode ? "true" : "false")}", "-c", $"service_tier=\"{(settings.FastMode ? "fast" : "default")}\""]);
            if (settings.WebSearch != "default") args.AddRange(["-c", $"web_search=\"{settings.WebSearch}\""]);
            args.Add("exec");
            if (request.ResumeId is not null) args.AddRange(["resume", request.ResumeId]);
            args.AddRange(["--json", "--skip-git-repo-check"]);
            if (settings.Effort != "default") args.AddRange(["-c", $"model_reasoning_effort=\"{settings.Effort}\""]);
            if (phaseModels is not null)
            {
                if (phaseModels.CodexReviewModel != "default") args.AddRange(["-c", $"review_model=\"{phaseModels.CodexReviewModel}\""]);
                if (phaseModels.CodexSubagentDefault != "default") args.AddRange(["-c", $"agents.default_subagent_model=\"{phaseModels.CodexSubagentDefault}\""]);
                if (phaseModels.CodexPlanModeReasoningEffort != "default") args.AddRange(["-c", $"plan_mode_reasoning_effort=\"{phaseModels.CodexPlanModeReasoningEffort}\""]);
            }
        }
        else args.AddRange(["--output-format", "stream-json", "--approval-mode", settings.PermissionMode == "fullAccess" ? "yolo" : settings.PermissionMode == "acceptEdits" ? "auto_edit" : settings.PermissionMode == "plan" ? "plan" : "default"]);
        if (request.Model != "default") args.AddRange(["--model", request.Model]);
        if (request.Provider != "codex" && request.ResumeId is not null) args.AddRange(["--resume", request.ResumeId]);
        if (request.Provider == "codex") args.Add("-");
        return args;
    }
    public ValueTask DisposeAsync() { lock (commands) return new(shutdown ??= ShutdownAsync()); }
    private async Task ShutdownAsync() { closing.Cancel(); await gate.WaitAsync(); gate.Release(); }
}

public static class JsonExtensions
{
    public static string? Text(this JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
