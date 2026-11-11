using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

public sealed record CliCommand(string Binary, string[] Prefix, string? Version = null);

/// <summary>The agents' CLIs: where each is, its model list and the arguments a run gets (M/MightyCore/ProviderService.swift).</summary>
/// <param name="finder">Finds a provider's CLI; null looks along the user's PATH and the installers' folders.</param>
/// <param name="storageDirectory">
/// Where the last list each CLI gave is kept (<see cref="KeptModelLists"/>, the profile folder); null (the checks, helpers) keeps the
/// lists in memory only.
/// </param>
public sealed class ProviderCatalog(Func<string, CancellationToken, Task<CliCommand?>>? finder = null, string? storageDirectory = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource closing = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, CliCommand?> commands = [];
    private RuntimeInfo? cached;
    private string? cachedDirectory;
    private DateTimeOffset refreshed;
    private Task? shutdown;
    /// <summary>The last list each CLI gave, loaded from <c>storageDirectory</c> on first use; guarded by <see cref="keptGate"/>.</summary>
    private KeptModelLists? keptLists;
    /// <summary>When the kept lists' file was last written.</summary>
    private DateTimeOffset? keptWrittenAt;
    private readonly object keptGate = new();
    /// <summary>
    /// The <c>gemini</c> id is kept for saved panes and the wire; it runs Google's Antigravity CLI (<c>agy</c>), which also runs
    /// Claude and GPT-OSS models (M/MightyCore/Models.swift ProviderOptions.label/executable).
    /// </summary>
    public static string Name(string provider) => provider switch { "claude" => "Claude Code", "codex" => "Codex CLI", "pi" => "pi", "opencode" => "opencode CLI", "hermes" => "Hermes CLI", _ => "Antigravity CLI" };
    /// <summary>The executable the app runs, finds, version-checks, updates and signs in with.</summary>
    public static string Executable(string provider) => provider == "gemini" ? "agy" : provider;
    /// <summary>
    /// Where Antigravity's own installer (<c>irm https://antigravity.google/cli/install.ps1 | iex</c>) puts <c>agy.exe</c>:
    /// <c>%LOCALAPPDATA%\agy\bin</c>. It is looked for there too, since the installer's PATH change reaches only new sessions.
    /// </summary>
    public static string AntigravityInstallFolder(IReadOnlyDictionary<string, string>? environment = null) =>
        Path.Combine(environment?.GetValueOrDefault("LOCALAPPDATA") is { Length: > 0 } local ? local : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin");
    // Claude is official; Codex, Antigravity, pi, opencode and Hermes are beta. The one Windows answer every beta
    // badge asks — the badge sits beside Name and never becomes part of it.
    public static bool IsBeta(string provider) => provider is "codex" or "gemini" or "pi" or "opencode" or "hermes";
    /// <summary>
    /// pi, opencode and Hermes run on this computer only (M/MightyCore/Models.swift isLocalOnly): a remote workspace, whose host may be a Mac
    /// or an older Windows, offers none of them, and a pane of one in a remote workspace is refused (<c>run.blocked.*Remote</c>).
    /// </summary>
    public static bool IsLocalOnly(string provider) => provider is "pi" or "opencode" or "hermes";
    /// <summary>The agents that speak the Agent Client Protocol (<c>opencode acp</c>, <c>hermes acp</c>) through <see cref="AcpChannel"/>.</summary>
    public static bool IsAcp(string provider) => provider is "opencode" or "hermes";
    // Menu items and plain text rows cannot host the badge, so it rides in their text.
    public static string BetaLabel(string provider, string text) => IsBeta(provider) ? text + " · " + Locale.Get("badge.beta") : text;
    // A sidebar row or tab carries the capsule after its title only for a beta agent's pane
    // (macOS paneRow / PaneDockView: kind "claude" and isBeta); shells, browsers and files never do.
    public static bool ShowsBetaBadge(RunSession session) => session.Kind == "claude" && IsBeta(session.Provider);
    /// <summary>
    /// The permission modes a pane of <paramref name="provider"/> offers: pi's read-only or all on; Hermes asks before edits (or not) and
    /// before dangerous commands and has no read-only or plan mode (M/MightyCore/Models.swift permissionModes).
    /// </summary>
    public static string[] PermissionModes(string provider, bool includeAuto = true) => provider == "pi" ? ["manual", "fullAccess"] : provider == "hermes" ? ["manual", "acceptEdits", "fullAccess"] : provider == "codex" ? ["manual", "onRequest", "acceptEdits", "fullAccess"] : provider == "claude" && includeAuto ? ["plan", "manual", "acceptEdits", "auto", "fullAccess"] : ["manual", "plan", "acceptEdits", "fullAccess"];
    public static ProviderCapabilities Capabilities(string provider, string? version = null) => new(provider != "gemini", PermissionModes(provider, provider == "claude" && SupportsMods(version)).Where(mode => mode != "onRequest" || SupportsCodexApprovals(version)).ToArray(), provider == "claude", provider == "claude", true, provider == "codex", provider == "codex", provider == "codex", true);
    public static RunSettings NormalizeSettings(string provider, RunSettings? value)
    {
        var v = value ?? new(); var caps = Capabilities(provider);
        var permission = PermissionModes(provider).Contains(v.PermissionMode) ? v.PermissionMode : "manual";
        return new(caps.Effort && Wire.RunEfforts(provider).Contains(v.Effort) ? v.Effort : "default", permission, caps.MaxTurns && v.MaxTurns is >= 1 and <= 1000 ? v.MaxTurns : null, caps.MaxBudgetUsd && v.MaxBudgetUsd is > 0 and <= 10000 ? v.MaxBudgetUsd : null, caps.FastMode && v.FastMode, caps.WebSearch && v.WebSearch is "disabled" or "cached" or "live" ? v.WebSearch : "default", caps.NetworkAccess && permission is ("acceptEdits" or "onRequest") && v.NetworkAccess,
            // Only Claude has an advisor: a model's name, or off (macOS normalizedSettings).
            provider == "claude" && Wire.Model(v.Advisor) ? v.Advisor : null);
    }
    public static ModelCatalog Fallback(string provider)
    {
        var models = new List<ModelOption> { new("default", Locale.Get("provider.fallback.defaultLabel", new Dictionary<string, string> { ["name"] = ProviderMark.Label(provider) }), Locale.Get("provider.fallback.defaultDescription")) };
        // pi and opencode list their models (`pi --list-models`, `opencode models`) once signed in, Hermes the picker its last session
        // offered (HermesAgent.CachedCatalog); until then only their own default.
        if (provider == "gemini") foreach (var (slug, name) in AntigravityModels) models.Add(new(slug, name, Locale.Get("provider.fallback.exampleDescription")));
        else if (provider is not ("pi" or "opencode" or "hermes")) foreach (var value in provider switch { "claude" => new[] { "best", "fable", "opus", "sonnet", "haiku", "opusplan" }, _ => new[] { "gpt-5.6-sol", "gpt-6-astra" } }) models.Add(new(value, value, Locale.Get("provider.fallback.exampleDescription"), SupportsEffort: value.Contains("haiku") ? false : null));
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
    /// <c>agy models</c> (M/MightyCore/ProviderService.swift normalizeAntigravityCatalog): one <c>slug&lt;TAB&gt;Display Name</c> line per
    /// model on standard output (agy 1.5.5 writes its "Fetching available models..." line to standard error; an older agy wrote it first
    /// on standard output). It needs the network and a sign-in; anything else keeps the fallback list.
    /// </summary>
    public static ModelCatalog AntigravityCatalog(string text)
    {
        var fallback = Fallback("gemini");
        var models = new List<ModelOption> { fallback.Models[0] }; var seen = new HashSet<string> { "default" };
        foreach (var line in text.Split('\n').Take(256))
        {
            var fields = line.Split('\t', 2).Select(field => field.Trim(' ', '\t', '\r')).ToArray();
            if (fields.Length != 2 || !Wire.Model(fields[0]) || fields[1].Length == 0 || models.Count > Wire.ListedModelLimit("gemini") || !seen.Add(fields[0])) continue;
            models.Add(new(fields[0], Wire.Clean(fields[1], 160), ""));
        }
        return models.Count > 1 ? new("cli", models, Locale.Get("provider.catalog.antigravitySource")) : fallback;
    }
    /// <summary>Asks <c>agy models</c> for its list (15 s): the list, or the fallback list marked with why (<see cref="ReadListCommandAsync"/>).</summary>
    public static Task<ModelCatalog> ReadAntigravityModelsAsync(CliCommand command, CancellationToken cancellation = default, string? workingDirectory = null) =>
        ReadListCommandAsync("gemini", command, cancellation, workingDirectory);
    /// <summary>
    /// Starts a CLI with its pipes. pi is an npm shim (<c>pi.cmd</c>), which CreateProcess does not run: a batch file goes through
    /// cmd.exe with every argument quoted (<see cref="WindowsCommand.Launch"/>), as the other tools' shims do.
    /// </summary>
    public static ChildProcess StartCli(string binary, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string>? environment)
    {
        if (OperatingSystem.IsWindows() && WindowsCommand.IsBatch(binary))
        {
            var (info, shell) = WindowsCommand.Launch(binary, arguments.ToList(), workingDirectory, environment);
            info.Environment["NO_COLOR"] = "1";
            return ChildProcess.Start(info, shell);
        }
        return ChildProcess.Start(ChildProcess.StartInfo(binary, arguments, workingDirectory, environment));
    }
    /// <summary>
    /// <c>pi --list-models</c> (M/MightyCore/ProviderService.swift normalizePiCatalog): a header row (<c>provider model context
    /// max-out thinking images</c>) and one row per model, columns padded with spaces. Without a sign-in it prints "No models
    /// available…", which keeps the fallback (just pi's own default). A model is <c>provider/id</c>, the form <c>--model</c> takes;
    /// <c>thinking: no</c> means it has no thinking levels.
    /// </summary>
    public static ModelCatalog PiCatalog(string text)
    {
        var fallback = Fallback("pi");
        var models = new List<ModelOption> { fallback.Models[0] }; var seen = new HashSet<string> { "default" };
        foreach (var line in text.Split('\n').Take(1_024))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || fields[0] == "provider" || fields[4] is not ("yes" or "no")) continue;
            var value = fields[0] + "/" + fields[1];
            if (!Wire.Model(value) || models.Count > Wire.ListedModelLimit("pi") || !seen.Add(value)) continue;
            models.Add(new(value, value, Locale.Get("provider.catalog.piModelRow", new Dictionary<string, string> { ["context"] = fields[2], ["output"] = fields[3] }), SupportsEffort: fields[4] == "yes"));
        }
        return models.Count > 1 ? new("cli", models, Locale.Get("provider.catalog.piSource")) : fallback;
    }
    /// <summary>
    /// <c>opencode models --standalone</c> (M/MightyCore/ProviderService.swift normalizeOpencodeCatalog), without OpenCode 2's background
    /// service: one <c>provider/model</c> per line. Without a sign-in it lists Zen's free models, which still run. Anything that is not a
    /// <c>provider/model</c> (a notice, a refresh line) is left out; an empty list keeps the fallback.
    /// </summary>
    public static ModelCatalog OpencodeCatalog(string text)
    {
        var fallback = Fallback("opencode");
        var models = new List<ModelOption> { fallback.Models[0] }; var seen = new HashSet<string> { "default" };
        foreach (var line in text.Split('\n').Take(4_096))
        {
            var value = line.Trim(' ', '\t', '\r');
            var slash = value.IndexOf('/');
            if (slash <= 0 || slash == value.Length - 1 || value.Contains(' ') || !Wire.Model(value) || models.Count > Wire.ListedModelLimit("opencode") || !seen.Add(value)) continue;
            models.Add(new(value, value, Locale.Get("provider.catalog.opencodeModelRow", new Dictionary<string, string> { ["provider"] = value[..slash] })));
        }
        return models.Count > 1 ? new("cli", models, Locale.Get("provider.catalog.opencodeSource")) : fallback;
    }
    /// <summary>Asks <c>pi --list-models</c> for its list (15 s): the list, or the fallback list marked with why (<see cref="ReadListCommandAsync"/>).</summary>
    public static Task<ModelCatalog> ReadPiModelsAsync(CliCommand command, CancellationToken cancellation = default, string? workingDirectory = null) =>
        ReadListCommandAsync("pi", command, cancellation, workingDirectory);

    /// <summary>
    /// The arguments (after the executable and its prefix) of each CLI's model-list read (M/MightyCore/ProviderService.swift
    /// modelListArguments). Each was run against the CLI named here, and ModelListProbeVerification pins them: a flag one CLI takes is
    /// not another's (agy 1.5.5 refuses <c>models --standalone</c>, "flags provided but not defined").
    /// <list type="bullet">
    /// <item>claude 2.1.322: the headless stream-json launch the work map's summary shares (<see cref="ClaudeHeadlessArguments"/>), sent only
    /// <c>initialize</c> (its answer lists the models).</item>
    /// <item>codex 0.167.0: <c>app-server</c> over stdio, <c>initialize</c> then <c>model/list</c>.</item>
    /// <item>agy 1.5.5: <c>models</c>. pi 1.5.2: <c>--list-models</c>. opencode 2.2.7: <c>models --standalone</c>, without starting OpenCode 2's
    /// background service.</item>
    /// <item>Hermes 0.23.1 has no list command (null): the picker its last session offered is read instead (<c>HermesAgent.CachedCatalog</c>).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string>? ModelListArguments(string provider) => provider switch
    {
        "claude" => ClaudeHeadlessArguments,
        "codex" => ["app-server", "--listen", "stdio://"],
        "gemini" => ["models"],
        "pi" => ["--list-models"],
        "opencode" => ["models", "--standalone"],
        _ => null,
    };

    /// <summary>The command a model-list read runs, as its failure names it (Claude's long headless launch is just <c>claude</c>).</summary>
    public static string ModelListCommand(string provider)
    {
        var executable = Executable(provider);
        return provider == "claude" || ModelListArguments(provider) is not { } arguments ? executable : string.Join(' ', [executable, .. arguments]);
    }

    /// <summary>
    /// The one line the model menu's help shows for a read that failed (M/MightyCore/ProviderService.swift readFailure): the command, what
    /// went wrong, and the line of the CLI's output that says why. The home folder reads <c>~</c>.
    /// </summary>
    public static string ReadFailure(string provider, string problem, string output = "")
    {
        var text = ModelListCommand(provider) + ": " + problem;
        if (TellingLine(output) is { } line) text += " · " + line;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && home != "/") text = text.Replace(home, "~", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return ActivitySupport.Clean(text, 400, singleLine: true);
    }

    /// <summary>The line of a CLI's output that says why it failed: the last that names an error, else the first that says anything.</summary>
    public static string? TellingLine(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(200).Select(line => ActivitySupport.Clean(line, 240, singleLine: true)).Where(line => line.Length > 0).ToList();
        return lines.LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault();
    }

    /// <summary>
    /// <c>agy models</c> and <c>pi --list-models</c> (M/MightyCore/ProviderService.swift readListCommand): one command whose standard output is
    /// the list. A non-zero exit, a timeout, too much output or no models in it is a failed read: the fallback list marked with why
    /// (<see cref="ModelCatalog.ReadFailure"/>), which the catalog replaces with the last list the CLI gave when it has one.
    /// </summary>
    public static async Task<ModelCatalog> ReadListCommandAsync(string provider, CliCommand command, CancellationToken cancellation = default, string? workingDirectory = null)
    {
        string problem, output = "";
        if (ModelListArguments(provider) is not { } arguments) return Fallback(provider) with { ReadFailure = ReadFailure(provider, Locale.Get("provider.catalog.failure.noModels")) };
        // opencode's first read may build its model cache: 20 s, and its list is the longest (M/ProviderService.swift readListCommand).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(provider == "opencode" ? 20 : 15));
        try
        {
            await using var child = StartCli(command.Binary, command.Prefix.Concat(arguments), workingDirectory ?? Path.GetTempPath(), QuietEnvironment());
            child.Input.Close();
            var errors = new ErrorTail(child.Error, timeout.Token);
            var text = await ReadBoundedAsync(child.Output, provider == "opencode" ? 2_097_152 : provider == "pi" ? 1_048_576 : 262_144, timeout.Token);
            var code = await child.Completion.WaitAsync(timeout.Token);
            if (code == 0)
            {
                var catalog = provider == "pi" ? PiCatalog(text) : provider == "opencode" ? OpencodeCatalog(text) : AntigravityCatalog(text);
                if (catalog.IsFresh) return catalog;
                problem = Locale.Get("provider.catalog.failure.noModels"); output = text + "\n" + await errors.TextAsync();
            }
            else
            {
                problem = Locale.Get("provider.catalog.failure.exit", new Dictionary<string, string> { ["code"] = code.ToString(CultureInfo.InvariantCulture) });
                var stderr = await errors.TextAsync(); output = stderr.Trim().Length > 0 ? stderr : text;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { problem = Locale.Get("process.error.timeout"); }
        catch (InvalidDataException) { problem = Locale.Get("process.error.outputLimit"); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException) { problem = ex.Message; }
        return Fallback(provider) with { ReadFailure = ReadFailure(provider, problem, output) };
    }

    /// <summary>A CLI's standard error read to its end, its last 8 KiB kept for a failure's reason.</summary>
    private sealed class ErrorTail
    {
        private const int Keep = 8_192;
        private readonly System.Text.StringBuilder text = new();
        private readonly Task pump;
        internal ErrorTail(StreamReader reader, CancellationToken token)
        {
            pump = PumpAsync(reader, token);
            _ = pump.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        private async Task PumpAsync(StreamReader reader, CancellationToken token)
        {
            var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                lock (text) { text.Append(buffer, 0, count); if (text.Length > Keep) text.Remove(0, text.Length - Keep); }
        }
        /// <summary>What the CLI wrote there, once it closed it or a second passed (a child of its own may keep it open).</summary>
        internal async Task<string> TextAsync()
        {
            try { await pump.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            lock (text) return text.ToString();
        }
    }
    public static string[] Efforts(string provider, string model, ModelCatalog catalog) =>
        Efforts(provider, model, catalog, null);

    public static string[] Efforts(string provider, string model, ModelCatalog catalog, IReadOnlyList<RegisteredModelEntry>? registeredModels)
    {
        if (provider == "gemini") return [];
        if (provider == "pi") return catalog.Models.FirstOrDefault(m => m.Value == model)?.SupportsEffort == false ? [] : Wire.PiThinkingLevels;
        // opencode's effort is the model's variant (ACP's `effort` option); one a model lacks is named and skipped at the run.
        if (provider == "opencode") return model == "default" || catalog.Models.FirstOrDefault(m => m.Value == model)?.SupportsEffort == false ? [] : Wire.OpencodeEfforts;
        // Hermes takes a reasoning effort for the session (`reasoning_effort`), whatever the model.
        if (provider == "hermes") return Wire.Efforts;
        var row = catalog.Models.FirstOrDefault(m => m.Value == model || m.ResolvedModel == model);
        if (row?.SupportsEffort == false) return [];
        // A Codex model's list may name `ultra`; Claude's never offers it, whatever its list says.
        if (row?.SupportedEffortLevels is { } levels) return levels.Where((provider == "codex" ? Wire.CodexEfforts : Wire.Efforts).Contains).Distinct().ToArray();
        if (row is null && registeredModels is not null)
        {
            var reg = registeredModels.FirstOrDefault(r => r.Name == model);
            if (reg is not null)
            {
                if (!reg.SupportsEffort) return [];
                return (reg.SupportedEffortLevels ?? []).Where(Wire.Efforts.Contains).ToArray();
            }
        }
        if (provider == "codex" || SilentHaiku(model, row?.SupportsEffort, row?.SupportedEffortLevels)) return [];
        if (Regex.IsMatch(model, @"(opus|sonnet)[-.]4[-.]6")) return ["low", "medium", "high", "max"];
        return Regex.IsMatch(model, @"^(default|best|fable|opus|sonnet|opusplan)(\[1m\])?$|fable[-.]5|opus[-.](5|4[-.][78])|sonnet[-.]5") ? Wire.Efforts : row?.SupportsEffort == true ? ["low", "medium", "high"] : [];
    }
    /// <summary>
    /// Why a run's effort cannot go with its model, by the list of its workspace (M/MightyCore/Models.swift validateSelection), or null: a level the
    /// list does not name for that model, or, for a Haiku model that takes no effort at all (one the CLI says nothing about), Haiku's own words.
    /// </summary>
    public static string? EffortRefusal(string provider, string model, string effort, ModelCatalog catalog, IReadOnlyList<RegisteredModelEntry>? registeredModels)
    {
        if (effort == "default") return null;
        var levels = Efforts(provider, model, catalog, registeredModels);
        if (levels.Contains(effort)) return null;
        return Locale.Get(provider == "claude" && levels.Length == 0 && model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? "validation.run.haikuEffort" : "run.error.effortNotSupported");
    }
    /// <summary>
    /// A Haiku model the CLI says nothing about (M/MightyCore/Models.swift silentHaiku): no <c>supportsEffort</c> and no levels in its list, or no
    /// list that names it. Haiku took no effort before Claude Code named levels for it, so such a model still takes none; what the CLI says wins
    /// (2.1.322 gives Haiku 5.5 low to max, and nothing for Haiku 4.5).
    /// </summary>
    public static bool SilentHaiku(string model, bool? supportsEffort, string[]? levels) =>
        supportsEffort is null && levels is null && model.Contains("haiku", StringComparison.OrdinalIgnoreCase);
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
            foreach (var id in Wire.Providers) providers.Add(await ReadProviderAsync(id, directory, workingDirectory));
            cached = Info(providers);
            cachedDirectory = directory; refreshed = DateTimeOffset.UtcNow; return cached;
        }
        finally { gate.Release(); }
    }
    /// <param name="directory">The folder the CLI runs in.</param>
    /// <param name="workspace">The workspace folder whose list it is, for the kept lists; null for a read outside any.</param>
    private async Task<ProviderRuntime> ReadProviderAsync(string id, string directory, string? workspace)
    {
        var command = await FindAsync(id, closing.Token);
        var read = command is null ? Fallback(id) with { ReadFailure = Locale.Get("run.error.cliExecutableMissing", new Dictionary<string, string> { ["name"] = ProviderMark.Label(id) }) }
            // Hermes lists models only inside a session: the picker its last session offered is kept and read here.
            : id == "hermes" ? HermesModels()
            : id is "gemini" or "pi" or "opencode" ? await ReadListCommandAsync(id, command, closing.Token, directory) : await ReadModelsAsync(id, command, closing.Token, directory);
        var catalog = await SettleAsync(id, read, workspace);
        return new(id, Name(id), command is not null && (id != "claude" || SupportsMods(command.Version)), command?.Version, command is null ? Locale.Get("provider.notInstalled") : id == "claude" && !SupportsMods(command.Version) ? Locale.Get("provider.unsupportedVersion") : Locale.Get("provider.available"), catalog, Capabilities(id, command?.Version));
    }

    /// <summary>
    /// A list the CLI gave is kept as its newest; a read that failed answers with the list kept for this workspace (else the provider's
    /// newest anywhere), or the built-in list, either marked with why the read failed (M/MightyCore/ProviderService.swift settle).
    /// </summary>
    private async Task<ModelCatalog> SettleAsync(string provider, ModelCatalog read, string? workspace)
    {
        // Hermes's picker is its own kept list: one forgotten for a Hermes with no provider is not brought back as an earlier list.
        if (provider == "hermes") return read;
        if (read.IsFresh) { await KeepAsync(read, provider, workspace); return read; }
        var reason = read.ReadFailure ?? ReadFailure(provider, Locale.Get("provider.catalog.failure.noModels"));
        KeptModelLists.Entry? entry;
        lock (keptGate) entry = LoadedKeptLists().List(provider, workspace);
        return entry is not null ? KeptModelLists.Kept(entry, reason) : Fallback(provider) with { ReadFailure = reason };
    }

    /// <summary>Where the picker the last Hermes session offered is kept: the storage folder (none without one, so a checks' fake Hermes never writes over a user's).</summary>
    public string? HermesModelCache => storageDirectory is null ? null : Path.Combine(storageDirectory, HermesAgent.ModelCacheFileName);
    /// <summary>The picker the last Hermes session offered (M/MightyCore/HermesSupport.swift cachedCatalog).</summary>
    private ModelCatalog HermesModels() => HermesAgent.CachedCatalog(HermesModelCache);

    /// <summary>Keeps a list the CLI just gave, writing the file (in the storage folder only) when it changed or is five minutes old.</summary>
    private async Task KeepAsync(ModelCatalog catalog, string provider, string? workspace)
    {
        var now = DateTimeOffset.UtcNow; byte[]? data = null;
        lock (keptGate) { var lists = LoadedKeptLists(); if (lists.Remember(catalog, provider, workspace, now, keptWrittenAt) && storageDirectory is not null) data = lists.Encoded(); }
        if (data is null || KeptModelLists.FilePath(storageDirectory) is not { } path) return;
        try { await StateStore.AtomicWriteAsync(path, data); lock (keptGate) keptWrittenAt = now; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The list is still kept in memory; the next change writes it. */ }
    }

    /// <summary>The kept lists, read from the storage folder the first time (called under <see cref="keptGate"/>).</summary>
    private KeptModelLists LoadedKeptLists() => keptLists ??= KeptModelLists.Load(KeptModelLists.FilePath(storageDirectory));
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
            var value = await ReadProviderAsync(provider, directory, workingDirectory);
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
        // pi's npm shim, opencode's binary or shim and Hermes's launchers in its installer's folder: CliDiscovery.Files names each CLI's files.
        var candidates = CliDiscovery.Files(provider, directories, OperatingSystem.IsWindows(), CliEnvironment.Current()).Select(path => new CliCommand(path, [])).ToList();
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
                await using var child = StartCli(candidate.Binary, candidate.Prefix.Concat(["--version"]), Path.GetTempPath(), QuietEnvironment());
                var stderr = DrainAsync(child.Error, timeout.Token);
                var text = await ReadBoundedAsync(child.Output, 16384, timeout.Token);
                var code = await child.Completion.WaitAsync(timeout.Token); await stderr;
                // Hermes prints its install folder, Python and update status after its version line: only that line is kept.
                var version = CliDiscovery.VersionText(Executable(provider), text);
                if (code == 0 && version.Length > 0) return candidate with { Version = Wire.Clean(version, 160) };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }
    /// <summary>The headless Claude launch the model list and the work map's topic summary share: one stream-json message in, no tools, no MCP, no saved session.</summary>
    public static readonly IReadOnlyList<string> ClaudeHeadlessArguments = ["--print", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json", "--no-session-persistence", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--tools", "", "--safe-mode"];
    public static Dictionary<string, string> ClaudeHeadlessEnvironment()
    {
        var env = QuietEnvironment();
        env["CLAUDE_CODE_SAFE_MODE"] = "1"; env["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] = "1"; env["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1"; env["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1";
        return env;
    }
    /// <summary>
    /// The environment of a CLI the app runs (the model lists, the version checks, the runs). <c>DISABLE_TELEMETRY</c> is left out on
    /// purpose (M/MightyCore/ProviderService.swift quietClaudeEnvironment): with it Claude Code skips fetching the account's current
    /// model configuration and answers with its built-in list, so a model released after the CLI was built (Fable 5.2 on 2.1.322)
    /// never reaches the model menu, and the <c>fable</c> alias reads as the older model.
    /// </summary>
    public static Dictionary<string, string> QuietEnvironment()
    {
        var environment = CliEnvironment.Current();
        foreach (var key in new[] { "DISABLE_AUTOUPDATER", "DISABLE_ERROR_REPORTING", "NO_UPDATE_NOTIFIER" }) environment[key] = "1";
        return environment;
    }
    public static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) { result.Append(buffer, 0, count); if (result.Length > limit) throw new InvalidDataException(Locale.Get("provider.cliOutputLimit")); }
        return result.ToString();
    }
    public static async Task DrainAsync(StreamReader reader, CancellationToken token) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { } }
    /// <summary>
    /// Claude's and Codex's model lists come from a short session with the CLI (<see cref="ModelListArguments"/>): Claude answers
    /// <c>initialize</c> with its models; Codex's app-server answers <c>initialize</c>, then <c>model/list</c>. <paramref name="limit"/> (7 s)
    /// bounds the read unless the caller's token does (Timeout.InfiniteTimeSpan). A CLI that ends first, refuses, times out, says too
    /// much or lists no model is a failed read: the fallback list marked with why (M/MightyCore/ProviderService.swift CLIModelProbe).
    /// </summary>
    public static async Task<ModelCatalog> ReadModelsAsync(string provider, CliCommand command, CancellationToken cancellation = default, string? workingDirectory = null, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(7));
        var token = timeout.Token;
        var env = provider == "claude" ? ClaudeHeadlessEnvironment() : QuietEnvironment();
        var noModels = Locale.Get("provider.catalog.failure.noModels");
        string problem, output = "";
        try
        {
            if (ModelListArguments(provider) is not { } args) return Fallback(provider) with { ReadFailure = ReadFailure(provider, noModels) };
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary, command.Prefix.Concat(args), workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env));
            var errors = new ErrorTail(child.Error, token);
            // A CLI that already ended takes no more input: what it wrote and its exit code say why.
            async Task Send(string line) { try { await child.Input.WriteLineAsync(line); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
            var requestId = Wire.Id(); var expected = 1; var pages = 0; var models = Fallback(provider).Models.Take(1).ToList();
            await Send(provider == "codex" ? JsonSerializer.Serialize(new { id = expected, method = "initialize", @params = new { clientInfo = new { name = "mighty_claude", title = "Mighty Code", version = "0.1.0" } } }, Wire.Json) : JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "initialize" } }, Wire.Json));
            var total = 0; string? answer = null;
            await foreach (var line in OutputParser.LinesAsync(child.Output, token))
            {
                total += line.Length; if (total > 2 * 1024 * 1024) { answer = Locale.Get("process.error.outputLimit"); break; }
                JsonElement root;
                try { using var document = JsonDocument.Parse(line); root = document.RootElement.Clone(); } catch (JsonException) { continue; }
                if (provider == "claude")
                {
                    if (root.Text("type") != "control_response" || MetadataJson.Property(root, "response") is not { ValueKind: JsonValueKind.Object } response || response.Text("request_id") != requestId) continue;
                    if (response.Text("subtype") != "success") { answer = response.Text("error") is { Length: > 0 } refusal ? refusal : noModels; break; }
                    if (MetadataJson.Property(response, "response") is not { ValueKind: JsonValueKind.Object } payload || MetadataJson.Property(payload, "models") is not { ValueKind: JsonValueKind.Array } rows) { answer = noModels; break; }
                    var catalog = ClaudeCatalog(rows);
                    if (catalog.IsFresh) return catalog;
                    answer = noModels; break;
                }
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number != expected) continue;
                if (root.TryGetProperty("error", out var error)) { answer = error.Text("message") is { Length: > 0 } message ? message : noModels; break; }
                if (expected == 1)
                {
                    await Send("{\"method\":\"initialized\"}");
                    await Send(JsonSerializer.Serialize(new { id = ++expected, method = "model/list", @params = new { limit = 64, includeHidden = false } }, Wire.Json));
                }
                else if (MetadataJson.Property(root, "result") is { ValueKind: JsonValueKind.Object } result && MetadataJson.Property(result, "data") is { ValueKind: JsonValueKind.Array } data)
                {
                    AddCodexModels(models, data);
                    if (++pages < 4 && models.Count < 1 + Wire.ListedModelLimit("codex") && result.Text("nextCursor") is { Length: > 0 and < 4096 } cursor) await Send(JsonSerializer.Serialize(new { id = ++expected, method = "model/list", @params = new { limit = 64, includeHidden = false, cursor } }, Wire.Json));
                    else
                    {
                        var catalog = CodexCatalog(models);
                        if (catalog.IsFresh) return catalog;
                        answer = noModels; break;
                    }
                }
                else { answer = noModels; break; }
            }
            if (answer is not null) problem = answer;
            else
            {
                // The CLI closed its output before it answered: it ended, and its exit code and its last error line say why.
                var code = await child.Completion.WaitAsync(token);
                problem = Locale.Get("provider.catalog.failure.exit", new Dictionary<string, string> { ["code"] = code.ToString(CultureInfo.InvariantCulture) });
                output = await errors.TextAsync();
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { problem = Locale.Get("process.error.timeout"); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException) { problem = ex.Message; }
        return Fallback(provider) with { ReadFailure = ReadFailure(provider, problem, output) };
    }
    // The CLI's own "default" row only lends the fallback "<name> 설정 따름" row the model it
    // stands for (macOS ProviderService.normalizeClaudeCatalog), so the label reads
    // "Claude 설정 따름 · Opus 5.5" while the value sent stays "default". An answer with no
    // model at all is the fallback list (a failed read).
    public static ModelCatalog ClaudeCatalog(JsonElement rows)
    {
        var models = Fallback("claude").Models.Take(1).ToList(); var listed = false;
        foreach (var row in rows.EnumerateArray().Take(Wire.ListedModelLimit("claude")))
        {
            if (ReadModel(row, "claude") is not { } model) continue;
            listed = true;
            if (model.Value == "default") models[0] = models[0] with { ResolvedModel = model.ResolvedModel, SupportsEffort = model.SupportsEffort, SupportedEffortLevels = model.SupportedEffortLevels };
            else models.Add(model);
        }
        return listed ? new("cli", models.DistinctBy(m => m.Value).ToList(), Locale.Get("provider.claudeCliSource")) : Fallback("claude");
    }
    /// <summary>Codex's <c>model/list</c> rows as the menu's list (M/MightyCore/ProviderService.swift normalizeCodexCatalog).</summary>
    public static ModelCatalog CodexCatalog(JsonElement data)
    {
        var models = Fallback("codex").Models.Take(1).ToList();
        AddCodexModels(models, data);
        return CodexCatalog(models);
    }
    /// <summary>Its default row and the models Codex listed; none listed is the fallback list (a failed read).</summary>
    private static ModelCatalog CodexCatalog(List<ModelOption> models) =>
        models.Count > 1 ? new("cli", models.DistinctBy(m => m.Value).ToList(), Locale.Get("provider.codexCliSource")) : Fallback("codex");
    // Codex marks its default with isDefault; the fallback default row resolves to it
    // (macOS ProviderService.normalizeCodexCatalog).
    public static void AddCodexModels(List<ModelOption> models, JsonElement data)
    {
        foreach (var row in data.EnumerateArray().Take(Math.Max(0, 1 + Wire.ListedModelLimit("codex") - models.Count)))
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
        // The efforts the CLI names for the model: Codex's `ultra` among them (Wire.CodexEfforts), never for Claude.
        string[]? efforts = null; var known = provider == "codex" ? Wire.CodexEfforts : Wire.Efforts;
        if (row.TryGetProperty(provider == "codex" ? "supportedReasoningEfforts" : "supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array) efforts = levels.EnumerateArray().Select(l => provider == "codex" ? l.Text("reasoningEffort") : l.ValueKind == JsonValueKind.String ? l.GetString() : null).Where(s => s is not null && known.Contains(s)).Cast<string>().Distinct().ToArray();
        bool? said = row.TryGetProperty("supportsEffort", out var supported) && supported.ValueKind is JsonValueKind.True or JsonValueKind.False ? supported.GetBoolean() : null;
        var support = said ?? (efforts is not null ? efforts.Length > 0 : null);
        // What the CLI says about a model's effort wins: Claude Code 2.1.322 gives Haiku 5.5 low to max. A Haiku model it says nothing about
        // (Haiku 4.5 there) keeps the old rule, no effort.
        if (provider == "claude" && SilentHaiku(value!, said, efforts)) { support = false; efforts = []; }
        return new(value!, Wire.Clean(row.Text("displayName") ?? value, 160), Wire.Clean(row.Text("description"), 2400), Wire.Model(row.Text("resolvedModel")) ? row.Text("resolvedModel") : null, support, efforts);
    }
    public static List<string> Arguments(StartRunRequest value, string pluginDirectory, PhaseModelsSnapshot? phaseModels = null)
    {
        var request = value.Validate(); var settings = request.Settings!; var args = new List<string>();
        phaseModels = PhaseModelPreferences.Normalize(phaseModels ?? request.PhaseModels);
        if (request.Provider == "claude")
        {
            args.AddRange(["--print", "--verbose", "--output-format", "stream-json", "--permission-prompts", "none", "--permission-mode", (request.PermissionModeOverride ?? settings.PermissionMode) is "fullAccess" ? "bypassPermissions" : request.PermissionModeOverride ?? settings.PermissionMode, "--plugin-dir", pluginDirectory]);
            // With the app's stream-json input Claude Code loads its task tools only when they are allowed by name (the request's plan,
            // FlowSteps): one --allowedTools with a style's own tools.
            args.AddRange(["--allowedTools", string.Join(",", (request.StyleAutoAllow ?? []).Concat(FlowSteps.TaskTools).Distinct(StringComparer.Ordinal))]);
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
            // The request's plan in the role view (FlowSteps) is Claude's own task list: ask for one whenever there is work.
            args.AddRange(["--append-system-prompt", FlowSteps.PlanInstruction]);
            if (settings.MaxTurns is int turns) args.AddRange(["--max-turns", turns.ToString(CultureInfo.InvariantCulture)]);
            if (settings.MaxBudgetUsd is double budget) args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (request.Provider == "codex")
        {
            var approvals = settings.PermissionMode == "onRequest";
            var sandbox = settings.PermissionMode == "fullAccess" ? "danger-full-access" : settings.PermissionMode is ("acceptEdits" or "onRequest") ? "workspace-write" : "read-only";
            args.AddRange(["-c", $"approval_policy=\"{(approvals ? "on-request" : "never")}\"", "-c", $"sandbox_mode=\"{sandbox}\"", "-c", $"sandbox_workspace_write.network_access={(settings.NetworkAccess ? "true" : "false")}", "-c", $"features.fast_mode={(settings.FastMode ? "true" : "false")}", "-c", $"service_tier=\"{(settings.FastMode ? "fast" : "default")}\""]);
            if (settings.WebSearch != "default") args.AddRange(["-c", $"web_search=\"{settings.WebSearch}\""]);
            if (sandbox == "workspace-write" && request.DelegationWorktreeRoot is { } root) args.AddRange(["-c", "sandbox_workspace_write.writable_roots=" + JsonSerializer.Serialize(new[] { root }, Wire.Json)]);
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
        else if (request.Provider == "pi")
        {
            // One RPC-mode process per request (M/MightyCore/ProviderService.swift arguments): the prompt, a steer and the stop travel
            // as JSON lines on stdin (ProviderInput.PiStartup). pi has no approval window, so manual keeps only the read tools and
            // full access leaves every tool on. The model, thinking level and session follow the same order as the Mac's.
            args.AddRange(["--mode", "rpc"]);
            if (request.Model != "default") args.AddRange(["--model", request.Model]);
            if (settings.Effort != "default") args.AddRange(["--thinking", settings.Effort]);
            // The app's question tool (`ask_user`) rides along as an extension of this run only; the read-only allowlist names it, or `--tools`
            // would leave it off. Without the shipped file pi runs without the tool.
            var questions = AgentFiles.Find(PiAgent.QuestionExtensionName);
            if (settings.PermissionMode != "fullAccess") args.AddRange(["--tools", Wire.PiReadOnlyTools + (questions is null ? "" : "," + PiAgent.QuestionTool)]);
            if (questions is not null) args.AddRange(["--extension", questions]);
            if (request.ResumeId is not null) args.AddRange(["--session", request.ResumeId]);
            return args;
        }
        else if (IsAcp(request.Provider))
        {
            // One ACP server per request (`opencode acp`, OpenCode 2; `hermes acp`, a viewer of Hermes's gateway, which it starts when needed):
            // the session, the settings and the prompt travel as JSON-RPC on stdio (AcpChannel); the permission mode as opencode's run config
            // (OPENCODE_CONFIG_CONTENT) or Hermes's edit policy and HERMES_YOLO_MODE.
            args.Add("acp");
            return args;
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
        if (request.Provider == "gemini" && request.DelegationWorktreeRoot is { } added) args.AddRange(["--add-dir", added]);
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
