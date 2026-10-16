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
    private string? cachedDirectory;
    private DateTimeOffset refreshed;
    private Task? shutdown;
    /// <summary>
    /// The <c>gemini</c> id is kept for saved panes and the wire; it runs Google's Antigravity CLI (<c>agy</c>), which also runs
    /// Claude and GPT-OSS models (M/MightyCore/Models.swift ProviderOptions.label/executable).
    /// </summary>
    public static string Name(string provider) => provider == "claude" ? "Claude Code" : provider == "codex" ? "Codex CLI" : "Antigravity CLI";
    /// <summary>The executable the app runs, finds, version-checks, updates and signs in with.</summary>
    public static string Executable(string provider) => provider == "gemini" ? "agy" : provider;
    /// <summary>
    /// Where Antigravity's own installer (<c>irm https://antigravity.google/cli/install.ps1 | iex</c>) puts <c>agy.exe</c>:
    /// <c>%LOCALAPPDATA%\agy\bin</c>. It is looked for there too, since the installer's PATH change reaches only new sessions.
    /// </summary>
    public static string AntigravityInstallFolder(IReadOnlyDictionary<string, string>? environment = null) =>
        Path.Combine(environment?.GetValueOrDefault("LOCALAPPDATA") is { Length: > 0 } local ? local : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin");
    // Claude is official; Codex and Antigravity are beta. The one Windows answer every beta
    // badge asks — the badge sits beside Name and never becomes part of it.
    public static bool IsBeta(string provider) => provider is "codex" or "gemini";
    // Menu items and plain text rows cannot host the badge, so it rides in their text.
    public static string BetaLabel(string provider, string text) => IsBeta(provider) ? text + " · " + Locale.Get("badge.beta") : text;
    // A sidebar row or tab carries the capsule after its title only for a beta agent's pane
    // (macOS paneRow / PaneDockView: kind "claude" and isBeta); shells, browsers and files never do.
    public static bool ShowsBetaBadge(RunSession session) => session.Kind == "claude" && IsBeta(session.Provider);
    public static string[] PermissionModes(string provider, bool includeAuto = true) => provider == "codex" ? ["manual", "onRequest", "acceptEdits", "fullAccess"] : provider == "claude" && includeAuto ? ["plan", "manual", "acceptEdits", "auto", "fullAccess"] : ["manual", "plan", "acceptEdits", "fullAccess"];
    public static ProviderCapabilities Capabilities(string provider, string? version = null) => new(provider != "gemini", PermissionModes(provider, provider == "claude" && SupportsMods(version)).Where(mode => mode != "onRequest" || SupportsCodexApprovals(version)).ToArray(), provider == "claude", provider == "claude", true, provider == "codex", provider == "codex", provider == "codex", true);
    public static RunSettings NormalizeSettings(string provider, RunSettings? value)
    {
        var v = value ?? new(); var caps = Capabilities(provider);
        var permission = PermissionModes(provider).Contains(v.PermissionMode) ? v.PermissionMode : "manual";
        return new(caps.Effort && Wire.Efforts.Contains(v.Effort) ? v.Effort : "default", permission, caps.MaxTurns && v.MaxTurns is >= 1 and <= 1000 ? v.MaxTurns : null, caps.MaxBudgetUsd && v.MaxBudgetUsd is > 0 and <= 10000 ? v.MaxBudgetUsd : null, caps.FastMode && v.FastMode, caps.WebSearch && v.WebSearch is "disabled" or "cached" or "live" ? v.WebSearch : "default", caps.NetworkAccess && permission is ("acceptEdits" or "onRequest") && v.NetworkAccess);
    }
    public static ModelCatalog Fallback(string provider)
    {
        var models = new List<ModelOption> { new("default", Locale.Get("provider.fallback.defaultLabel", new Dictionary<string, string> { ["name"] = ProviderMark.Label(provider) }), Locale.Get("provider.fallback.defaultDescription")) };
        if (provider == "gemini") foreach (var (slug, name) in AntigravityModels) models.Add(new(slug, name, Locale.Get("provider.fallback.exampleDescription")));
        else foreach (var value in provider switch { "claude" => new[] { "best", "fable", "opus", "sonnet", "haiku", "opusplan" }, _ => new[] { "gpt-5.6-sol", "gpt-6-astra" } }) models.Add(new(value, value, Locale.Get("provider.fallback.exampleDescription"), SupportsEffort: value.Contains("haiku") ? false : null));
        return new("fallback", models, Locale.Get("provider.fallback.source"));
    }
    /// <summary>
    /// What <c>agy models</c> listed when this was written (M/MightyCore/Models.swift antigravityModels). Each slug carries its
    /// effort, so Antigravity has no separate effort setting.
    /// </summary>
    public static readonly IReadOnlyList<(string Slug, string Name)> AntigravityModels =
    [
        ("gemini-4-argon-medium", "Gemini 4 Argon (Medium)"), ("gemini-4-argon-high", "Gemini 4 Argon (High)"), ("gemini-4-argon-low", "Gemini 4 Argon (Low)"),
        ("gemini-3.8-flash-high", "Gemini 3.8 Flash (High)"), ("gemini-3.8-flash-medium", "Gemini 3.8 Flash (Medium)"), ("gemini-3.8-flash-low", "Gemini 3.8 Flash (Low)"),
        ("claude-opus-5-5-low", "Claude Opus 5.5 (Low)"), ("claude-opus-5-5-medium", "Claude Opus 5.5 (Medium)"), ("claude-opus-5-5-high", "Claude Opus 5.5 (High)"),
        ("claude-sonnet-5-5-low", "Claude Sonnet 5.5 (Low)"), ("claude-sonnet-5-5-medium", "Claude Sonnet 5.5 (Medium)"), ("claude-sonnet-5-5-high", "Claude Sonnet 5.5 (High)"),
        ("gpt-oss-120b-medium", "GPT-OSS 120B (Medium)"),
    ];
    /// <summary>
    /// <c>agy models</c> (M/MightyCore/ProviderService.swift normalizeAntigravityCatalog): a "Fetching available models..." line,
    /// then one <c>slug&lt;TAB&gt;Display Name</c> line per model. It needs the network and a sign-in; anything else keeps the
    /// fallback list.
    /// </summary>
    public static ModelCatalog AntigravityCatalog(string text)
    {
        var fallback = Fallback("gemini");
        var models = new List<ModelOption> { fallback.Models[0] }; var seen = new HashSet<string> { "default" };
        foreach (var line in text.Split('\n').Take(256))
        {
            var fields = line.Split('\t', 2).Select(field => field.Trim(' ', '\t', '\r')).ToArray();
            if (fields.Length != 2 || !Wire.Model(fields[0]) || fields[1].Length == 0 || models.Count > 128 || !seen.Add(fields[0])) continue;
            models.Add(new(fields[0], Wire.Clean(fields[1], 160), ""));
        }
        return models.Count > 1 ? new("cli", models, Locale.Get("provider.catalog.antigravitySource")) : fallback;
    }
    /// <summary>Asks <c>agy models</c> for its list (15 s), or keeps the fallback list.</summary>
    public static async Task<ModelCatalog> ReadAntigravityModelsAsync(CliCommand command, CancellationToken cancellation = default, string? workingDirectory = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary, command.Prefix.Concat(["models"]), workingDirectory ?? Path.GetTempPath(), QuietEnvironment()));
            child.Input.Close();
            var stderr = DrainAsync(child.Error, timeout.Token); _ = stderr.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var text = await ReadBoundedAsync(child.Output, 262_144, timeout.Token);
            if (await child.Completion.WaitAsync(timeout.Token) == 0) return AntigravityCatalog(text);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return Fallback("gemini");
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

    public static bool SupportsCodexApprovals(string? version) => Regex.Match(version ?? "", @"(?<!\d)(\d+)\.(\d+)\.(\d+)(?![\d-])") is { Success: true } m && Version.Parse($"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}") >= new Version(0, 153, 4);

    /// <summary>
    /// Has <paramref name="provider"/> found as <paramref name="command"/> until it is set back with null, when it is looked for
    /// again: the GUI smoke's scripted stand-in for one step, never a real launch (M/AppStore+DelegationSmoke.swift:7-12).
    /// </summary>
    public void Override(string provider, CliCommand? command)
    {
        lock (commands) { if (command is null) overrides.Remove(provider); else overrides[provider] = command; commands.Remove(provider); }
    }
    private readonly Dictionary<string, CliCommand> overrides = [];
    public async Task<CliCommand?> FindAsync(string provider, CancellationToken token = default)
    {
        lock (commands) if (overrides.TryGetValue(provider, out var forced)) return forced;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token);
        await RefreshEnvironmentAsync(false, linked.Token);
        lock (commands) if (commands.TryGetValue(provider, out var saved)) return saved;
        var result = finder is null ? await DiscoverAsync(provider, linked.Token) : await finder(provider, linked.Token);
        lock (commands) commands[provider] = result;
        return result;
    }
    // Injected finders used by isolated tests do not inspect the user's profiles.
    public Task<Dictionary<string, string>> RefreshEnvironmentAsync(bool force = false, CancellationToken token = default) =>
        finder is null ? CliEnvironment.RefreshAsync(force, token) : Task.FromResult(CliEnvironment.Current());
    public async Task<RuntimeInfo> GetRuntimeAsync(bool force = false, string? workingDirectory = null)
    {
        await gate.WaitAsync(closing.Token);
        try
        {
            await RefreshEnvironmentAsync(force, closing.Token);
            var directory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!force && cachedDirectory == directory && cached is not null && DateTimeOffset.UtcNow - refreshed < TimeSpan.FromMinutes(1)) return cached;
            if (force) lock (commands) commands.Clear();
            var providers = new List<ProviderRuntime>();
            foreach (var id in Wire.Providers) providers.Add(await ReadProviderAsync(id, directory));
            cached = Info(providers);
            cachedDirectory = directory; refreshed = DateTimeOffset.UtcNow; return cached;
        }
        finally { gate.Release(); }
    }
    private async Task<ProviderRuntime> ReadProviderAsync(string id, string directory)
    {
        var command = await FindAsync(id, closing.Token);
        var catalog = command is null ? Fallback(id) : id == "gemini" ? await ReadAntigravityModelsAsync(command, closing.Token, directory) : await ReadModelsAsync(id, command, closing.Token, directory);
        return new(id, Name(id), command is not null && (id != "claude" || SupportsMods(command.Version)), command?.Version, command is null ? Locale.Get("provider.notInstalled") : id == "claude" && !SupportsMods(command.Version) ? Locale.Get("provider.unsupportedVersion") : Locale.Get("provider.available"), catalog, Capabilities(id, command?.Version));
    }
    private static RuntimeInfo Info(List<ProviderRuntime> providers)
    {
        var claude = providers[0];
        return new(OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux", "0.1.0", claude.Version is not null, claude.Version, claude.ModelCatalog, providers, new(claude.Available ? "available" : claude.Version is null ? "unavailable" : "unsupported", "2.1.271", claude.Detail));
    }
    /// <summary>The runtime with one provider's reading replaced by a newer one.</summary>
    public static RuntimeInfo With(RuntimeInfo runtime, ProviderRuntime provider) => Info(runtime.Providers.Select(p => p.Id == provider.Id ? provider : p).ToList());
    /// <summary>
    /// One provider read again for a folder: its model list is asked of its CLI anew (macOS
    /// ProviderService.providerRuntime(forceRefresh:)), and a runtime kept for that folder takes the new reading. The CLI
    /// is the one already found, unless <paramref name="rediscover"/> asks for it to be looked for again (a reading that would drop
    /// waiting requests must not rest on one held from before an update); a forced <see cref="GetRuntimeAsync"/> looks for every CLI again.
    /// </summary>
    public async Task<ProviderRuntime> RefreshProviderAsync(string provider, string? workingDirectory = null, bool rediscover = false)
    {
        await gate.WaitAsync(closing.Token);
        try
        {
            if (rediscover) lock (commands) commands.Remove(provider);
            var directory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var value = await ReadProviderAsync(provider, directory);
            if (cached is not null && cachedDirectory == directory) cached = With(cached, value);
            return value;
        }
        finally { gate.Release(); }
    }
    private static async Task<CliCommand?> DiscoverAsync(string provider, CancellationToken token)
    {
        var directories = (CliEnvironment.Current().GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator).Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        // Antigravity's installer adds its folder to PATH for new sessions only, so the folder it installs to is looked in as well.
        if (provider == "gemini" && OperatingSystem.IsWindows()) directories = [.. directories, AntigravityInstallFolder(CliEnvironment.Current())];
        var candidates = directories.Select(path => new CliCommand(Path.Combine(path, Executable(provider) + (OperatingSystem.IsWindows() ? ".exe" : "")), [])).ToList();
        // npm's global install leaves only claude.cmd on PATH; the executable it forwards to sits inside the package.
        if (OperatingSystem.IsWindows() && provider == "claude") candidates.AddRange(directories.Select(path => new CliCommand(Path.Combine(path, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"), [])));
        if (OperatingSystem.IsWindows() && provider == "codex")
        {
            var node = directories.Select(path => Path.Combine(path, "node.exe")).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")).FirstOrDefault(File.Exists);
            if (node is not null) foreach (var directory in directories)
                { var script = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js"); if (File.Exists(script)) candidates.Add(new(node, [script])); }
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
    public static Dictionary<string, string> QuietEnvironment()
    {
        var environment = CliEnvironment.Current();
        foreach (var key in new[] { "DISABLE_AUTOUPDATER", "DISABLE_TELEMETRY", "DISABLE_ERROR_REPORTING", "NO_UPDATE_NOTIFIER" }) environment[key] = "1";
        return environment;
    }
    public static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) { result.Append(buffer, 0, count); if (result.Length > limit) throw new InvalidDataException(Locale.Get("provider.cliOutputLimit")); }
        return result.ToString();
    }
    public static async Task DrainAsync(StreamReader reader, CancellationToken token) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { } }
    /// <summary>A provider's model list from its CLI, or the fallback list; <paramref name="limit"/> (7 s) bounds the read unless the caller's token does (Timeout.InfiniteTimeSpan).</summary>
    public static async Task<ModelCatalog> ReadModelsAsync(string provider, CliCommand command, CancellationToken cancellation = default, string? workingDirectory = null, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(7));
        var token = timeout.Token;
        var args = provider == "codex" ? new List<string> { "app-server", "--listen", "stdio://" } : ["--print", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json", "--no-session-persistence", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--tools", "", "--safe-mode"];
        var env = QuietEnvironment();
        if (provider == "claude") { env["CLAUDE_CODE_SAFE_MODE"] = "1"; env["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] = "1"; env["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1"; env["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1"; }
        try
        {
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary, command.Prefix.Concat(args), workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env));
            var drain = DrainAsync(child.Error, token); _ = drain.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var requestId = Wire.Id(); var expected = 1; var pages = 0; var models = Fallback(provider).Models.Take(1).ToList();
            await child.Input.WriteLineAsync(provider == "codex" ? JsonSerializer.Serialize(new { id = expected, method = "initialize", @params = new { clientInfo = new { name = "mighty_claude", title = "Mighty Claude", version = "0.1.0" } } }, Wire.Json) : JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "initialize" } }, Wire.Json));
            var total = 0;
            await foreach (var line in OutputParser.LinesAsync(child.Output, token))
            {
                total += line.Length; if (total > 2 * 1024 * 1024) break;
                using var document = JsonDocument.Parse(line); var root = document.RootElement;
                if (provider == "claude")
                {
                    if (root.Text("type") != "control_response" || !root.TryGetProperty("response", out var response) || response.Text("request_id") != requestId || !response.TryGetProperty("response", out var payload) || !payload.TryGetProperty("models", out var rows)) continue;
                    return ClaudeCatalog(rows);
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
                    AddCodexModels(models, data);
                    if (++pages < 4 && models.Count < 128 && result.Text("nextCursor") is { Length: > 0 and < 4096 } cursor) await child.Input.WriteLineAsync(JsonSerializer.Serialize(new { id = ++expected, method = "model/list", @params = new { limit = 64, includeHidden = false, cursor } }, Wire.Json));
                    else return new("cli", models.DistinctBy(m => m.Value).ToList(), Locale.Get("provider.codexCliSource"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return Fallback(provider);
    }
    // The CLI's own "default" row only lends the fallback "<name> 설정 따름" row the model it
    // stands for (macOS ProviderService.normalizeClaudeCatalog), so the label reads
    // "Claude 설정 따름 · Opus 5.5" while the value sent stays "default".
    public static ModelCatalog ClaudeCatalog(JsonElement rows)
    {
        var models = Fallback("claude").Models.Take(1).ToList();
        foreach (var row in rows.EnumerateArray().Take(128))
        {
            if (ReadModel(row, "claude") is not { } model) continue;
            if (model.Value == "default") models[0] = models[0] with { ResolvedModel = model.ResolvedModel, SupportsEffort = model.SupportsEffort, SupportedEffortLevels = model.SupportedEffortLevels };
            else models.Add(model);
        }
        return new("cli", models.DistinctBy(m => m.Value).ToList(), Locale.Get("provider.claudeCliSource"));
    }
    // Codex marks its default with isDefault; the fallback default row resolves to it
    // (macOS ProviderService.normalizeCodexCatalog).
    public static void AddCodexModels(List<ModelOption> models, JsonElement data)
    {
        foreach (var row in data.EnumerateArray().Take(128 - models.Count))
        {
            if (ReadModel(row, "codex") is not { } model) continue;
            models.Add(model);
            if (models.Count > 0 && models[0].Value == "default" && row.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)
                models[0] = models[0] with { ResolvedModel = model.Value, SupportsEffort = model.SupportsEffort, SupportedEffortLevels = model.SupportedEffortLevels };
        }
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
        phaseModels = PhaseModelPreferences.Normalize(phaseModels ?? request.PhaseModels);
        if (request.Provider == "claude")
        {
            args.AddRange(["--print", "--verbose", "--output-format", "stream-json", "--permission-prompts", "none", "--permission-mode", (request.PermissionModeOverride ?? settings.PermissionMode) is "fullAccess" ? "bypassPermissions" : request.PermissionModeOverride ?? settings.PermissionMode, "--plugin-dir", pluginDirectory]);
            if (request.StyleAutoAllow is { Count: > 0 } styleTools)
                args.AddRange(["--allowedTools", string.Join(",", styleTools)]);
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
            if (request.Model == "default" && (phaseModels?.ClaudeMain is { } claudeMain && claudeMain != "default" || request.ResumeId is not null))
                args.AddRange(["--model", phaseModels?.ClaudeMain ?? "default"]);
            if (settings.MaxTurns is int turns) args.AddRange(["--max-turns", turns.ToString(CultureInfo.InvariantCulture)]);
            if (settings.MaxBudgetUsd is double budget) args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (request.Provider == "codex")
        {
            var approvals = settings.PermissionMode == "onRequest";
            var sandbox = settings.PermissionMode == "fullAccess" ? "danger-full-access" : settings.PermissionMode is ("acceptEdits" or "onRequest") ? "workspace-write" : "read-only";
            args.AddRange(["-c", $"approval_policy=\"{(approvals ? "on-request" : "never")}\"", "-c", $"sandbox_mode=\"{sandbox}\"", "-c", $"sandbox_workspace_write.network_access={(settings.NetworkAccess ? "true" : "false")}", "-c", $"features.fast_mode={(settings.FastMode ? "true" : "false")}", "-c", $"service_tier=\"{(settings.FastMode ? "fast" : "default")}\""]);
            if (settings.WebSearch != "default") args.AddRange(["-c", $"web_search=\"{settings.WebSearch}\""]);
            if (settings.Effort != "default") args.AddRange(["-c", $"model_reasoning_effort=\"{settings.Effort}\""]);
            if (phaseModels is not null)
            {
                if (phaseModels.CodexReviewModel != "default") args.AddRange(["-c", $"review_model=\"{phaseModels.CodexReviewModel}\""]);
                if (phaseModels.CodexSubagentDefault != "default") args.AddRange(["-c", $"agents.default_subagent_model=\"{phaseModels.CodexSubagentDefault}\""]);
                if (phaseModels.CodexSubagentEffort is { } subagentEffort) args.AddRange(["-c", $"agents.default_subagent_reasoning_effort=\"{subagentEffort}\""]);
                if (phaseModels.CodexPlanModeReasoningEffort != "default") args.AddRange(["-c", $"plan_mode_reasoning_effort=\"{phaseModels.CodexPlanModeReasoningEffort}\""]);
            }
            if (approvals)
            {
                args.AddRange(["-c", "approvals_reviewer=\"user\""]);
                if (request.Model != "default") args.AddRange(["-c", $"model=\"{request.Model}\""]);
                args.AddRange(["app-server", "--listen", "stdio://"]);
            }
            else
            {
                args.Add("exec");
                if (request.ResumeId is not null) args.AddRange(["resume", request.ResumeId]);
                args.AddRange(["--json", "--skip-git-repo-check"]);
            }
        }
        else
        {
            // Antigravity CLI (M/MightyCore/ProviderService.swift arguments): `--print=` with no text takes the prompt from the
            // stream-json line on stdin (ProviderInput.AntigravityUserMessage), one turn per line; closing stdin ends the session.
            // `--print-timeout 0` waits for the turn. Manual keeps agy's own review mode: headless, what it would ask about (shell
            // commands, file edits) is declined unless its settings allow it.
            args.AddRange(["--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--print-timeout", "0"]);
            if (settings.PermissionMode == "plan") args.AddRange(["--mode", "plan"]);
            else if (settings.PermissionMode == "acceptEdits") args.AddRange(["--mode", "accept-edits"]);
            else if (settings.PermissionMode == "fullAccess") args.Add("--dangerously-skip-permissions");
        }
        if (request.Model != "default" && !(request.Provider == "codex" && settings.PermissionMode == "onRequest")) args.AddRange(["--model", request.Model]);
        if (request.Provider != "codex" && request.ResumeId is not null) args.AddRange([request.Provider == "gemini" ? "--conversation" : "--resume", request.ResumeId]);
        if (request.Provider == "codex" && settings.PermissionMode != "onRequest") args.Add("-");
        return args;
    }
    public ValueTask DisposeAsync() { lock (commands) return new(shutdown ??= ShutdownAsync()); }
    private async Task ShutdownAsync() { closing.Cancel(); await gate.WaitAsync(); gate.Release(); }
}

public static class JsonExtensions
{
    public static string? Text(this JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
