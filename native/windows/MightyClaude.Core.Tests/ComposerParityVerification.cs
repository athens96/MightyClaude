using System.Text;
using System.Text.Json;
using MightyClaude.Core;
using ToolkitFileEntry = MightyClaude.Core.ToolkitFileReader.ToolkitFileEntry;

/// <summary>
/// The rules the Windows composer, transcript and run pipeline took from the Mac app: model lists that keep themselves right,
/// a built-in slash command sent in full, transcript retention, the stream events the parser used to drop, the queue's notices,
/// the agent switch, the status line's tick, the wait on a terminal sign-in, text put into a terminal, the run log copy, the
/// resume look-up and the toolkit's words.
/// </summary>
internal static class ComposerParityVerification
{
    private const string Stamp = "2026-10-01T10:00:00Z";
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
    private static string? Text(string language, string key) => Locale.Catalogue(language).GetValueOrDefault(key);
    private static ModelOption Model(string value, string? resolved = null, bool? supportsEffort = null, string[]? levels = null) => new(value, value, "", resolved, supportsEffort, levels);
    private static ModelCatalog Cli(params ModelOption[] models) => new("cli", models.ToList(), "");
    private static ModelSelectionResolution Kept(string model, string effort) => new(model, effort);
    private static ProviderRuntime Runtime(string provider, ModelCatalog catalog, bool available = true, string detail = "", params string[] modes) =>
        new(provider, ProviderCatalog.Name(provider), available, "2.1.271", detail, catalog, new ProviderCapabilities(true, modes, true, true, true));

    // M/Tests/ModelRefreshTests.swift:173-216, M/AppStore+ModelCatalogs.swift:47-63.
    internal static Task ModelSelectionFollowsTheNewList()
    {
        var old = Cli(Model("old-alias", "provider/model"), Model("retired"));
        var fresh = Cli(Model("new-alias", "provider/model", levels: ["high"]), Model("no-effort", supportsEffort: false));
        Check(ModelSelectionSupport.Reconcile("old-alias", "high", old, fresh) == Kept("new-alias", "high"), "an alias renamed for the same model is followed");
        Check(ModelSelectionSupport.Reconcile("provider/model", "low", null, fresh) == Kept("new-alias", "default"), "a concrete id takes the alias that stands for it, and an effort the alias lacks goes");
        Check(ModelSelectionSupport.Reconcile("retired", "high", old, fresh) == Kept("default", "default"), "a model the CLI offered before and no longer does gives way to the default");
        Check(ModelSelectionSupport.Reconcile("custom/private", "high", old, fresh) == Kept("custom/private", "high"), "an id typed by hand, which no list ever offered, survives");
        Check(ModelSelectionSupport.Reconcile("default", "high", old, fresh) == Kept("default", "high"), "the default stays the default");
        Check(ModelSelectionSupport.Reconcile("retired", "high", old, new ModelCatalog("fallback", [], "")) == Kept("retired", "high"), "only a list the CLI itself gave retires a model");
        Check(ModelSelectionSupport.Reconcile("no-effort", "high", null, fresh) == Kept("no-effort", "default"), "an effort a model does not take goes back to the default");

        using var rows = JsonDocument.Parse("""[{"value":"default","resolvedModel":"apac.anthropic.claude-opus-5","supportedEffortLevels":["high"]},{"value":"apac.anthropic.claude-fable-5-1","displayName":"Fable"}]""");
        var bedrock = ProviderCatalog.ClaudeCatalog(rows.RootElement);
        Check(bedrock.Source == "cli" && bedrock.Models[0] is { Value: "default", ResolvedModel: "apac.anthropic.claude-opus-5" }, "the fixture's default row must name the model it stands for");
        foreach (var previous in new ModelCatalog?[] { null, ProviderCatalog.Fallback("claude") })
            Check(ModelSelectionSupport.Reconcile("claude-fable-5-1[1m]", "high", previous, bedrock, "claude") == Kept("default", "default"), "a retired official id gives way even with no list from before");
        Check(ModelSelectionSupport.Reconcile("apac.anthropic.claude-fable-5-1", "high", null, bedrock, "claude").Model == "apac.anthropic.claude-fable-5-1", "a regional id the list has is kept");
        Check(ModelSelectionSupport.Reconcile("company/claude-fable-5-1", "high", null, bedrock, "claude").Model == "company/claude-fable-5-1", "a company's own id is no official id and survives");
        Check(ModelSelectionSupport.Reconcile("default", "max", null, bedrock, "claude").Effort == "default", "the default row's own effort levels decide the default's effort");

        using var concrete = JsonDocument.Parse("""[{"value":"default","resolvedModel":"apac.anthropic.claude-opus-5"},{"value":"apac.anthropic.claude-sonnet-5","supportsEffort":true,"supportedEffortLevels":["high"]}]""");
        var regional = ProviderCatalog.ClaudeCatalog(concrete.RootElement);
        foreach (var alias in new[] { "sonnet", "opus", "fable", "haiku", "opusplan", "best", "sonnet[1m]", "opus[1m]", "fable[1m]" })
        {
            Check(ModelSelectionSupport.IsOfficialClaudeModel(alias), alias + " is a name Claude Code itself knows");
            foreach (var previous in new ModelCatalog?[] { null, ProviderCatalog.Fallback("claude"), Cli(Model(alias, "apac.anthropic.claude-sonnet-5")) })
                Check(ModelSelectionSupport.Reconcile(alias, "high", previous, regional, "claude") == Kept(alias, "high"), "a stable alias survives a list of concrete Bedrock ids: " + alias);
        }
        Check(ModelSelectionSupport.Reconcile("sonnet", "max", null, Cli(Model("sonnet", levels: ["high"])), "claude") == Kept("sonnet", "default"), "an alias the list does have follows its row's effort levels");
        Check(!ModelSelectionSupport.IsOfficialClaudeModel("company/claude-fable-5-1") && ModelSelectionSupport.IsOfficialClaudeModel("claude-3-5-sonnet-20241022") && !ModelSelectionSupport.IsOfficialClaudeModel("gpt-6-astra"), "official names are Claude Code's own shapes only");

        var pane = new RunSession { Id = "pane", WorkspaceId = "w", Model = "retired", Settings = new(Effort: "high", PermissionMode: "plan"), Logs = [new("first", "user", "kept", Stamp)] };
        var adjusted = ModelSelectionSupport.Adjusted(pane, old, fresh);
        Check(adjusted is { Model: "default", Settings: { Effort: "default", PermissionMode: "plan" }, Logs.Count: 2 }, "the pane takes the default model and effort and keeps the rest");
        Check(adjusted!.Logs[1] is { Kind: "system", Provider: "claude" } line && line.Text == Locale.Get("composer.model.catalogAdjusted", Values(("fromModel", "retired"), ("toModel", "default"), ("fromEffort", "high"), ("toEffort", "default"))), "one transcript line says what was replaced by what");
        Check(ModelSelectionSupport.Adjusted(pane with { Model = "custom/private" }, old, fresh) is null, "a choice the list leaves as it is changes nothing and says nothing");
        var named = Cli(Model("default", "provider/current"), Model("new-alias"));
        Check(ModelSelectionSupport.Adjusted(pane, old, named)?.Model == "default", "a new conversation falls back to the plain default");
        Check(ModelSelectionSupport.Adjusted(pane with { ResumeId = "conversation" }, old, named)?.Model == "provider/current", "a continued conversation names the model the default stands for, so the CLI does not restore the retired one");
        foreach (var language in Locale.Languages)
            Check(Text(language, "composer.model.catalogAdjusted") is { } sentence && new[] { "{fromModel}", "{toModel}", "{fromEffort}", "{toEffort}" }.All(sentence.Contains) && Text(language, "composer.placeholder.checkingModels") is { Length: > 0 }, language + ": the adjusted line names all four values and the checking notice exists");
        return Task.CompletedTask;
    }

    // M/LocalModelCatalogRefresh.swift, M/AppStore+ModelCatalogs.swift:10-19, 66-76.
    internal static async Task ModelListIsReadAgainWhenItMustBe()
    {
        var key = new ModelContext("w", Path.GetTempPath(), "claude");
        var now = DateTimeOffset.UnixEpoch.AddDays(20000);
        var loads = 0; var held = new Queue<TaskCompletionSource>(); var changes = new List<(string? Before, string After)>(); var fails = false; var fallback = false;
        async Task<ProviderRuntime> Load(ModelContext context)
        {
            var number = Interlocked.Increment(ref loads);
            TaskCompletionSource? hold; lock (held) hold = held.Count > 0 ? held.Dequeue() : null;
            if (hold is not null) await hold.Task;
            if (fails) throw new IOException("the CLI did not answer");
            return Runtime(context.Provider, fallback ? ProviderCatalog.Fallback(context.Provider) : Cli(Model("default"), Model("m" + number)));
        }
        var refresh = new ModelCatalogRefresh(Load, (_, before, after) => { lock (changes) changes.Add((before?.ModelCatalog.Models[^1].Value, after.ModelCatalog.Models[^1].Value)); }, () => now);
        Check(ModelCatalogRefresh.ForcedLifetime == TimeSpan.FromSeconds(2) && ModelCatalogRefresh.Lifetime == TimeSpan.FromSeconds(60), "a selected pane's list is used again for 2 s, a run's for 60 s");
        Check(refresh.Value(key) is null && !refresh.IsCurrent(key), "nothing is known before the first reading");

        var first = refresh.Request(key, force: true); var joined = refresh.Request(key);
        Check(ReferenceEquals(first, joined) && !refresh.IsCurrent(key), "requests made while a probe runs share it");
        Check((await first)?.ModelCatalog.Models[^1].Value == "m1" && loads == 1 && changes.SequenceEqual([(null, "m1")]), "one reading answers both, and is reported once with nothing before it");
        Check(refresh.IsCurrent(key) && refresh.Value(key)?.ModelCatalog.Models[^1].Value == "m1", "the list in hand is the newest asked for");

        Check((await refresh.Request(key, force: true))?.ModelCatalog.Models[^1].Value == "m1" && loads == 1, "a list read a moment ago answers a selected pane without another probe");
        now = now.AddSeconds(3);
        Check((await refresh.Request(key))?.ModelCatalog.Models[^1].Value == "m1" && loads == 1, "a run about to start uses a list up to a minute old");
        Check((await refresh.Request(key, force: true))?.ModelCatalog.Models[^1].Value == "m2" && loads == 2 && changes[^1] == ("m1", "m2"), "a selected pane reads again after 2 s, and the list before goes with the new one");
        now = now.AddSeconds(61);
        Check((await refresh.Request(key))?.ModelCatalog.Models[^1].Value == "m3" && loads == 3, "a run about to start reads again after a minute");

        // Another agent or account: the list in hand is no longer the right one, however new.
        Check((await refresh.Request(key, force: true, invalidate: true))?.ModelCatalog.Models[^1].Value == "m4" && loads == 4 && refresh.IsCurrent(key), "an invalidating request reads again at once");

        // Invalidated while the probe runs: it reads again before it answers, and the list from before is never reported.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (held) held.Enqueue(hold);
        var probing = refresh.Request(key, invalidate: true);
        await Verification.Until(() => Volatile.Read(ref loads) == 5);
        Check(ReferenceEquals(refresh.Request(key, invalidate: true), probing) && !refresh.IsCurrent(key), "an invalidating request joins the running probe");
        hold.SetResult();
        Check((await probing)?.ModelCatalog.Models[^1].Value == "m6" && loads == 6 && refresh.IsCurrent(key), "an invalidation during a probe makes it read again before it answers");
        Check(changes.Select(change => change.After).SequenceEqual(["m1", "m2", "m3", "m4", "m6"]), "the reading that was overtaken is never reported");

        // The whole runtime read at once feeds the same book.
        refresh.Store(key, Runtime("claude", Cli(Model("default"), Model("stored"))));
        Check(changes[^1] == ("m6", "stored") && refresh.IsCurrent(key) && (await refresh.Request(key, force: true))?.ModelCatalog.Models[^1].Value == "stored" && loads == 6, "a list read another way is the newest, is reported, and is fresh");

        // A list that is not the CLI's own (offline, signed out) is not asked again by each run for a while; a selected pane asks again.
        fallback = true; now = now.AddSeconds(3);
        Check(ModelCatalogRefresh.FailedLifetime == TimeSpan.FromSeconds(15), "a failed or fallback reading holds for 15 s");
        Check((await refresh.Request(key, force: true))?.ModelCatalog.Source == "fallback" && loads == 7, "the fixture must read a fallback list");
        Check((await refresh.Request(key))?.ModelCatalog.Source == "fallback" && loads == 7 && refresh.IsCurrent(key), "a run about to start does not wait for the CLI to fail again within 15 s");
        Check((await refresh.Request(key, force: true))?.ModelCatalog.Source == "fallback" && loads == 8, "a selected pane asks the CLI again");
        now = now.AddSeconds(16);
        Check((await refresh.Request(key))?.ModelCatalog.Source == "fallback" && loads == 9, "after 15 s a run asks again");

        fails = true; now = now.AddSeconds(16);
        Check(await refresh.Request(key) is null && loads == 10, "a reading that failed answers nothing");
        Check(await refresh.Request(key) is null && loads == 10, "and is not asked again by a run within 15 s");
        fails = false; fallback = false;
        Check((await refresh.Request(key, force: true))?.ModelCatalog.Models[^1].Value == "m11" && loads == 11, "and leaves no probe behind: a selected pane reads again");
        refresh.Shutdown();
        Check(await refresh.Request(key, force: true) is null && loads == 11 && refresh.Value(key) is null, "nothing is read once the app is closing");

        // A run waits for its list no longer than PreRunWait, then goes on with what is in hand.
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new ModelCatalogRefresh(async context => { await slow.Task; return Runtime(context.Provider, Cli(Model("default"))); }, (_, _, _) => { });
        Check(ModelCatalogRefresh.PreRunWait == TimeSpan.FromSeconds(3) && !await waiting.WaitCurrent(key, TimeSpan.FromMilliseconds(150)), "a list that takes too long holds the run up no longer than its limit");
        slow.SetResult();
        Check(await waiting.WaitCurrent(key) && waiting.IsCurrent(key), "a list that comes in time is the run's");
        // A probe that the report of another starts is kept, not forgotten by the first one's end.
        Task<ProviderRuntime?>? second = null; ModelCatalogRefresh? chained = null; var chainLoads = 0;
        chained = new ModelCatalogRefresh(context => { Interlocked.Increment(ref chainLoads); return Task.FromResult(Runtime(context.Provider, Cli(Model("default")))); },
            (context, _, _) => { if (second is null) second = chained!.Request(context, force: true, invalidate: true); });
        await chained.Request(key);
        Check(second is not null && ReferenceEquals(chained.Request(key), second) && !second.IsCompleted, "a probe started while the last one reported is the one a request joins");
        await second!; Check(chainLoads == 2, "and only two readings were made");
        // Every reading is a new object: two that hold the same are the same reading.
        Check(ModelCatalogRefresh.Same(Runtime("claude", Cli(Model("default"), Model("a"))), Runtime("claude", Cli(Model("default"), Model("a")))) && !ModelCatalogRefresh.Same(Runtime("claude", Cli(Model("default"), Model("a"))), Runtime("claude", Cli(Model("default"), Model("b")))) && !ModelCatalogRefresh.Same(null, Runtime("claude", Cli(Model("default")))), "readings are compared by what they hold");

        // One provider read again for a folder: the kept runtime takes the reading, and no CLI is looked for again.
        var found = new Dictionary<string, int>();
        await using var catalog = new ProviderCatalog((provider, _) => { lock (found) found[provider] = found.GetValueOrDefault(provider) + 1; return Task.FromResult<CliCommand?>(null); });
        var folder = Path.GetTempPath();
        var whole = await catalog.GetRuntimeAsync(false, folder);
        var codex = await catalog.RefreshProviderAsync("codex", folder);
        Check(codex is { Id: "codex", Available: false } && codex.ModelCatalog.Source == "fallback", "a provider with no CLI reads as not installed with the fallback list");
        Check(found["codex"] == 1 && found["claude"] == 1 && found["gemini"] == 1 && !ReferenceEquals(codex, whole.Providers.Single(p => p.Id == "codex")), "the list is read anew with the CLI already found");
        var after = await catalog.GetRuntimeAsync(false, folder);
        Check(ReferenceEquals(after.Providers.Single(p => p.Id == "codex"), codex) && after.Providers.Select(p => p.Id).SequenceEqual(whole.Providers.Select(p => p.Id)) && ReferenceEquals(after.Providers[0], whole.Providers[0]), "the kept runtime of that folder takes the new reading and keeps the others");
    }

    // M/SessionPaneView.swift:931-952, M/AppStore+SlashCommands.swift:111-113.
    internal static Task SentBuiltinNeverReachesTheCli()
    {
        static SlashCommand[] Choices(SlashArgument argument, string command) => argument == SlashArgument.Model
            ? SlashPalette.ModelChoices(command, [("default", "Default"), ("opus", "Opus")], "default")
            : SlashPalette.PermissionChoices(command, ["manual", "plan"], "manual", mode => mode);
        static SlashSubmission? Sent(string draft, string provider = "claude", string kind = "claude", bool attachments = false) => SlashPalette.Submitted(provider, kind, draft, attachments, Choices);

        Check(Sent("/clear") is { Mismatch: null, Command: { Invocation: "clear", Action: SlashCommandAction.NewConversation } }, "/clear typed in full runs in the app");
        Check(Sent("  /clear \n")?.Command?.Invocation == "clear", "the draft is read trimmed");
        Check(Sent("/clear now")?.Command?.Invocation == "clear", "words after a built-in that takes none do not make it prompt text");
        Check(Sent("/model opus") is { Mismatch: null, Command: { Action: SlashCommandAction.SetModel, ActionArg: "opus" } }, "/model opus applies the model");
        Check(Sent("/permissions plan")?.Command is { Action: SlashCommandAction.SetPermission, ActionArg: "plan" }, "/permissions plan applies the mode");
        Check(Sent("/model") is { Command: { Invocation: "model", Argument: SlashArgument.Model, Action: null } } && Sent("/model ")?.Command?.Invocation == "model", "/model alone is the built-in itself");
        Check(SlashPaletteState.Choose(Sent("/model")!.Command) is { Effect: SlashChoiceEffect.ArgumentCompletion, Draft: "/model " }, "which opens its choices instead of being sent");
        Check(Sent("/model zzz") is { Command: null } miss && miss.Mismatch == Locale.Get("slash.note.noModelMatch", Values(("query", "zzz"), ("command", "model"))), "a model that matches no choice is explained");
        Check(Sent("/permissions nope")?.Mismatch == Locale.Get("slash.note.noPermissionMatch", Values(("query", "nope"), ("command", "permissions"))), "so is a permission mode that matches none");
        Check(Sent("/model " + new string('z', 120))?.Mismatch == Locale.Get("slash.note.noModelMatch", Values(("query", new string('z', 80)), ("command", "model"))), "the notice quotes at most 80 characters of what was typed");
        Check(Sent("/review") is null && Sent("/mod") is null && Sent("hello /clear") is null && Sent("/model a b") is null && Sent("") is null, "anything else is the CLI's");
        Check(Sent("/clear", kind: "shell") is null && Sent("/clear", attachments: true) is null, "a shell has no built-ins, and a draft with files is a request");
        Check(Sent("/new", "codex")?.Command?.Action == SlashCommandAction.NewConversation && Sent("/clear", "codex") is null && Sent("/approval-mode", "gemini")?.Command?.Argument == SlashArgument.Permission, "each agent's own names are read");
        Check(SlashPalette.TypedBuiltin("claude", "/usage")?.Action == SlashCommandAction.ShowUsage && SlashPalette.TypedBuiltin("claude", "/usag") is null, "only a name typed out in full counts");
        foreach (var language in Locale.Languages)
            foreach (var note in new[] { "slash.note.noModelMatch", "slash.note.noPermissionMatch" })
                Check(Text(language, note) is { } sentence && sentence.Contains("{query}") && sentence.Contains("{command}"), language + ": the notice quotes the argument and names the command: " + note);
        return Task.CompletedTask;
    }

    // M/Tests/TranscriptRetentionTests.swift:13-34.
    internal static Task TranscriptKeepsTheNewestFourHundred()
    {
        static List<LogEntry> Entries(int count) => Enumerable.Range(0, count).Select(i => new LogEntry("log-" + i, "assistant", "line " + i, Stamp)).ToList();
        Check(TranscriptRetention.MaximumEntries == 400, "the cap is the Mac's 400");
        var kept = TranscriptRetention.Trimmed(Entries(500));
        Check(kept.Count == 400 && kept[0].Id == "log-100" && kept[^1].Id == "log-499", "a pane keeps exactly the newest 400 entries");
        Check(TranscriptRetention.Trimmed(Entries(400)).Count == 400 && TranscriptRetention.Trimmed(Entries(399)).Count == 399 && TranscriptRetention.Trimmed([]).Count == 0, "fewer are all kept");
        Check(TranscriptRetention.Trimmed(Entries(7)).Select(entry => entry.Id).SequenceEqual(Entries(7).Select(entry => entry.Id)), "and kept in order");

        var workspace = new Workspace { Id = "ws", Name = "Repo", Path = Path.GetTempPath() };
        var session = new RunSession { Id = "pane", WorkspaceId = "ws", Title = "Claude", Status = "completed", Logs = Entries(500) };
        var restored = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [session] }, true).Sessions[0];
        Check(restored.Logs.Select(entry => entry.Id).SequenceEqual(kept.Select(entry => entry.Id)), "saving and restoring keeps the same entries the running app kept; got " + restored.Logs.Count);

        var state = new AppSnapshot { Workspaces = [workspace], Sessions = [session with { Logs = Entries(400) }] };
        state = state.Apply(new RunEvent("pane", "log", new LogEntry("log-400", "assistant", "line 400", Stamp)));
        Check(state.Sessions[0].Logs is { Count: 400 } logs && logs[0].Id == "log-1" && logs[^1].Id == "log-400", "an entry appended past the cap pushes out the oldest one");
        return Task.CompletedTask;
    }

    // M/MightyCore/CLIStreamParser.swift:383-384, 432-437, M/Tests/ContextCompactionTests.swift, M/Tests/SessionUsageTests.swift:187-203.
    internal static async Task StreamEventsReachTheTranscript()
    {
        var logs = new List<(string Kind, string Text)>(); var activities = new List<AgentActivity>(); var usage = new List<SessionUsage>();
        var claude = new OutputParser("claude", (kind, text) => logs.Add((kind, text)), _ => { }, activities.Add, usage.Add, "claude-run");
        using var metadata = JsonDocument.Parse("""{"trigger":"auto","pre_tokens":84120,"post_tokens":21300,"duration_ms":12300}""");
        claude.Parse("""{"type":"system","subtype":"compact_boundary","session_id":"claude-one","compact_metadata":{"trigger":"auto","pre_tokens":84120,"post_tokens":21300,"duration_ms":12300}}""");
        Check(logs.SequenceEqual([("system", ContextCompaction.Title + " · " + ContextCompaction.ClaudeSummary(metadata.RootElement))]), "Claude summarizing its context is one transcript line with the figures");
        Check(logs[0].Text.Contains("84,120") && logs[0].Text.Contains("21,300"), "the line carries the token counts: " + logs[0].Text);
        claude.Parse("""{"type":"system","subtype":"compact_boundary","parent_tool_use_id":"child","compact_metadata":{"trigger":"auto"}}""");
        Check(logs.Count == 1, "a sub-agent's own compaction stays out of the request's transcript");

        claude.Parse("""{"type":"rate_limit_event","session_id":"claude-one","rate_limit_info":{"status":"allowed_warning","rateLimitType":"seven_day","utilization":0.86,"resetsAt":1789668000,"unifiedWindows":{"five_hour":{"utilization":0.22,"resetsAt":1789641600},"seven_day":{"utilization":0.86,"resetsAt":1789668000}}}}""");
        Check(usage.Count > 0 && usage[^1].RateLimits is { Count: 2 } limits && limits.Select(limit => limit.Kind).SequenceEqual(["five_hour", "seven_day"])
            && limits.Select(limit => (int)Math.Round(limit.PercentUsed ?? -1)).SequenceEqual([22, 86]) && limits[0].ResetsAt?.StartsWith("2026-09-", StringComparison.Ordinal) == true && usage[^1].RateLimitsUpdatedAt is not null,
            "a rate_limit_event fills the usage windows, in percent, with their reset times");
        claude.Parse("""{"type":"rate_limit_event","session_id":"claude-one","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.5,"unifiedWindows":{"x":{"utilization":7}}}}""");
        Check(usage[^1].RateLimits is [{ Kind: "five_hour", PercentUsed: 50, ResetsAt: null }], "a malformed window is dropped and the event's own window still counts");
        var readings = usage.Count;
        claude.Parse("""{"type":"rate_limit_event","session_id":"claude-one","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":true}}}}""");
        claude.Parse("""{"type":"rate_limit_event","session_id":"claude-one","parent_tool_use_id":"child","rate_limit_info":{"rateLimitType":"weekly","utilization":0.9}}""");
        Check(usage.Count == readings, "an event with no usable window, or a sub-agent's, changes nothing");

        logs.Clear(); activities.Clear();
        var codex = new OutputParser("codex", (kind, text) => logs.Add((kind, text)), _ => { }, activities.Add, null, "codex-run");
        codex.Parse("""{"type":"item.started","item":{"id":"compact-1","type":"context_compaction"}}""");
        Check(logs.Count == 0, "only a finished compaction is said");
        codex.Parse("""{"type":"item.completed","item":{"id":"compact-1","type":"context_compaction"}}""");
        Check(logs.SequenceEqual([("system", ContextCompaction.Title + " · " + ContextCompaction.CodexSummary)]), "Codex summarizing its context is one transcript line");

        codex.Parse("""{"type":"item.started","item":{"id":"call-1","type":"collab_tool_call","tool":"spawn_agent","prompt":"Review the diff","receiver_thread_ids":["thread-aaaaaaaaaaaa-long"],"status":"in_progress"}}""");
        Check(activities is [{ State: "running", ToolName: "spawn_agent", Output: null } spawn] && spawn.Summary == Locale.Get("run.agent.spawn") + " · Review the diff · thread-aaaaa", "starting a sub-agent is a tool row: what it does, its prompt and the agent's id cut short; got " + activities.LastOrDefault()?.Summary);
        codex.Parse("""{"type":"item.completed","item":{"id":"call-2","type":"collab_agent_tool_call","tool":"wait","receiver_thread_ids":["thread-b"],"agents_states":{"thread-b":{"status":"completed","message":"All good"}},"status":"completed"}}""");
        Check(activities.Count == 2 && activities[1] is { State: "completed", ToolName: "wait", Output: "thread-b · completed\nAll good" } wait && wait.Summary == Locale.Get("run.agent.wait") + " · thread-b", "waiting for a sub-agent is a row that ends with what the agent said; got " + activities[^1].Summary + " / " + activities[^1].Output);
        codex.Parse("""{"type":"item.completed","item":{"id":"call-3","type":"collab_tool_call","tool":"send_input","prompt":"Go on","receiver_thread_ids":["thread-b"],"status":"failed"}}""");
        Check(activities.Count == 3 && activities[2] is { State: "error", ToolName: "send_input" } && activities[2].Summary.StartsWith(Locale.Get("run.agent.sendInput"), StringComparison.Ordinal), "a failed instruction is a row in error");
        codex.Parse("""{"type":"item.completed","item":{"id":"call-4","type":"collab_tool_call","tool":"teleport"}}""");
        Check(activities.Count == 3, "a collaboration tool the app does not know makes no row");
        foreach (var language in Locale.Languages)
            Check(new[] { "run.agent.spawn", "run.agent.sendInput", "run.agent.wait", "run.agent.close", "run.notice.lineTooLong", "bedrock.scpDenied" }.All(key => Text(language, key) is { Length: > 0 }), language + ": the rows' words and the notices exist");

        // A line longer than the limit is skipped up to its line break and reported once (M/MightyCore/AgentOutputLines.swift:3-48).
        Check(OutputParser.MaximumLineBytes("claude") == 64 * 1_048_576 && OutputParser.MaximumLineBytes("codex") == 64 * 1_048_576 && OutputParser.MaximumLineBytes("gemini") == 8 * 1_048_576, "an agent's line may be 64 MiB, Gemini's 8 MiB");
        var skipped = 0; var read = new List<string>();
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("short\n" + new string('a', 40) + "\nafter\n" + new string('b', 99) + "\nlast")));
        await foreach (var line in OutputParser.LinesAsync(reader, default, 16, () => skipped++)) read.Add(line);
        Check(read.SequenceEqual(["short", "after", "last"]) && skipped == 2, $"each over-long line is skipped and reported once; read {string.Join(",", read)}, reported {skipped}");
    }

    // M/Tests/BedrockAuthDiagnosticsTests.swift:21-93.
    internal static Task BedrockPolicyDenialReadsAsGuidance()
    {
        const string denied = "User: arn:aws:sts::123456789012:assumed-role/private-role/private-session is not authorized to perform: bedrock:InvokeModel on resource: arn:aws:bedrock:ap-northeast-2::foundation-model/private-model with an explicit deny in a service control policy";
        static string ApiError(string message, int status = 403, string? code = null)
        {
            var body = new Dictionary<string, string> { ["Message"] = message }; if (code is not null) body["__type"] = code;
            return "AWS authentication failed · refresh your AWS credentials and retry · API Error: " + status + " " + JsonSerializer.Serialize(body);
        }
        foreach (var action in new[] { "InvokeModel", "InvokeModelWithResponseStream" })
            foreach (var suffix in new[] { "", ": arn:aws:organizations::123456789012:policy/o-0123456789/service_control_policy/p-01234567" })
                foreach (var connector in new[] { "with", "because of" })
                    foreach (var code in new string?[] { null, "AccessDeniedException", "private.namespace#AccessDeniedException" })
                    {
                        var message = denied.Replace("InvokeModel", action).Replace("with an explicit", connector + " an explicit") + suffix;
                        var guidance = BedrockSettings.RuntimeFailureGuidance(ApiError(message, code: code));
                        Check(guidance == BedrockSettings.ScpInvokeModelDeniedMessage, $"a service control policy's denial of {action} ({connector}, {code ?? "no code"}) is recognised under the credential advice");
                        Check(!guidance!.Contains("private-") && !guidance.Contains("123456789012") && !guidance.Contains("refresh your AWS") && !guidance.Contains("arn:"), "the guidance names no account, role or policy");
                    }
        var others = new[]
        {
            ApiError(denied, status: 400),
            ApiError(denied, code: "ExpiredTokenException"),
            ApiError("Authentication failed: Please make sure your API Key is valid. " + denied),
            ApiError("The security token included in the request is expired. See service control policy documentation."),
            ApiError("Not authorized; see service control policy documentation for explicit deny examples."),
            ApiError("For example: " + denied),
            ApiError(denied.Replace("service control policy", "identity-based policy") + ". See service control policy documentation."),
            ApiError(denied.Replace("InvokeModel", "ListFoundationModels")),
            ApiError(denied.Replace("InvokeModel", "InvokeModelWithResponseStreamExtra")),
            ApiError(denied + ": arn:aws:iam::123456789012:policy/private"),
            ApiError(denied + ". See service control policy documentation."),
            ApiError(denied + ": https://private.invalid/policy"),
            ApiError(denied.Replace("with an explicit deny in", "because no permission is granted by")),
            "API Error: 403 invalid-json " + denied,
            denied,
        };
        for (var index = 0; index < others.Length; index++) Check(BedrockSettings.RuntimeFailureGuidance(others[index]) is null, "any other failure keeps its own words: case " + index);

        // A confirmed failure reads as guidance wherever the frame is shown; the transcript parser reads the frame as presented.
        var raw = ApiError(denied);
        var marked = JsonSerializer.Serialize(new { type = "assistant", error = "authentication_failed", message = new { id = "aws-error", content = new[] { new { type = "text", text = raw } } } });
        var failed = JsonSerializer.Serialize(new { type = "result", subtype = "error_during_execution", is_error = true, errors = new[] { raw }, result = raw });
        var logs = new List<(string Kind, string Text)>();
        var parser = new OutputParser("claude", (kind, text) => logs.Add((kind, text)), _ => { });
        foreach (var line in new[] { marked, failed })
        {
            using var frame = JsonDocument.Parse(line);
            var shown = ClaudeStream.PresentFailure(frame.RootElement);
            Check(shown is not null && !shown.Contains("private-") && !shown.Contains("123456789012"), "the presented frame carries none of the AWS message");
            parser.Parse(shown!);
        }
        Check(parser.Failed && logs.Select(log => log.Kind).SequenceEqual(["assistant", "error"]) && logs.All(log => log.Text == BedrockSettings.ScpInvokeModelDeniedMessage), "the marked message and the failed result both read as the guidance");

        var ordinary = new[]
        {
            JsonSerializer.Serialize(new { type = "assistant", message = new { id = "quoted-example", content = new[] { new { type = "text", text = raw } } } }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "tool-fixture", is_error = true, content = raw } } } }),
            JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = raw }),
            JsonSerializer.Serialize(new { type = "turn.failed", error = new { message = raw } }),
        };
        foreach (var line in ordinary)
        {
            using var frame = JsonDocument.Parse(line);
            Check(ClaudeStream.PresentFailure(frame.RootElement) is null, "an ordinary answer, a tool's result, a successful result and another agent's failure are not rewritten");
        }
        var mixed = JsonSerializer.Serialize(new { type = "result", subtype = "error_during_execution", is_error = true, errors = new[] { "another failure", raw } });
        using var both = JsonDocument.Parse(mixed); using var presented = JsonDocument.Parse(ClaudeStream.PresentFailure(both.RootElement)!);
        Check(presented.RootElement.GetProperty("errors").EnumerateArray().Select(error => error.GetString()).SequenceEqual(["another failure", BedrockSettings.ScpInvokeModelDeniedMessage]), "only the denial among a result's errors is replaced");
        return Task.CompletedTask;
    }

    // M/AppStore.swift:488-506, 895-931.
    internal static Task QueueSaysWhatBecameOfIt()
    {
        Check(QueuedInputBuffer.SettleNotice("stopped", 3) == Locale.Get("queue.log.cancelledOnStop", Values(("count", "3"))), "a stop says how many queued requests it cancelled");
        Check(QueuedInputBuffer.SettleNotice("stopped", 3, keepOnStop: true) is null, "a stop that keeps the queue cancels nothing");
        Check(QueuedInputBuffer.SettleNotice("error", 2) == Locale.Get("queue.log.heldAfterError", Values(("count", "2"))), "an error says how many requests it holds");
        Check(new[] { "completed", "idle", "running", "" }.All(status => QueuedInputBuffer.SettleNotice(status, 2) is null) && QueuedInputBuffer.SettleNotice("stopped", 0) is null && QueuedInputBuffer.SettleNotice("error", 0) is null, "nothing is said where nothing waited or the run simply ended");
        Check(QueuedInputBuffer.UnrunnableNotice(2, "why") == Locale.Get("queue.log.cancelledUnrunnable", Values(("count", "2"), ("reason", "why"))), "a queue the pane cannot run is cancelled with its reason");
        Check(QueuedInputBuffer.KeptNotice("refused") == Locale.Get("queue.log.keptAfterFailure", Values(("error", "refused"))), "a refused start keeps its request and says why");
        foreach (var language in Locale.Languages)
        {
            Check(new[] { "queue.log.cancelledOnStop", "queue.log.heldAfterError", "queue.log.cancelledUnrunnable" }.All(key => Text(language, key)?.Contains("{count}") == true), language + ": each notice counts the requests");
            Check(Text(language, "queue.log.cancelledUnrunnable")?.Contains("{reason}") == true && Text(language, "queue.log.keptAfterFailure")?.Contains("{error}") == true, language + ": the reason is quoted");
        }

        var pane = new RunSession { Id = "pane", WorkspaceId = "w" };
        var ready = Runtime("claude", ProviderCatalog.Fallback("claude"), true, "ready", "manual", "plan");
        Check(RunBlock.Reason(pane, ready) is null, "a pane whose CLI is ready may run");
        Check(RunBlock.Reason(pane, null) is null, "a CLI that has not been read yet is no verdict");
        Check(RunBlock.Reason(pane, Runtime("claude", ProviderCatalog.Fallback("claude"), false, "not installed")) == "not installed", "a missing CLI blocks with what the runtime says of it");
        Check(RunBlock.Reason(pane, Runtime("claude", ProviderCatalog.Fallback("claude"), false)) == Locale.Get("run.blocked.installCLI", Values(("name", "Claude Code"))), "or with the install sentence when it says nothing");
        Check(RunBlock.Reason(pane with { Settings = new(PermissionMode: "auto") }, ready) == Locale.Get("composer.hint.autoModeUnverified"), "Auto on a CLI that does not take it blocks");
        Check(RunBlock.Reason(pane with { Settings = new(PermissionMode: "auto") }, Runtime("claude", ProviderCatalog.Fallback("claude"), true, "ready", "manual", "auto")) is null, "Auto on a CLI that takes it runs");
        Check(RunBlock.Reason(pane with { Provider = "codex", Settings = new(PermissionMode: "onRequest") }, Runtime("codex", ProviderCatalog.Fallback("codex"), true, "ready", "manual")) == Locale.Get("run.blocked.onRequestNeedsUpdate"), "ask-for-approval on a Codex too old for it blocks");
        Check(RunBlock.Reason(pane with { Kind = "shell" }, Runtime("claude", ProviderCatalog.Fallback("claude"), false)) is null, "a shell needs no CLI");
        return Task.CompletedTask;
    }

    // M/AppStore.swift:705-717.
    internal static Task SwitchingTheAgentSaysSo()
    {
        var pane = new RunSession
        {
            Id = "pane", WorkspaceId = "w", Title = ProviderCatalog.Name("claude"), Model = "opus", Settings = new(Effort: "high", PermissionMode: "plan"), ResumeId = "conversation",
            SessionUsage = new() { Provider = "claude" }, Draft = "typed", Logs = [new("first", "user", "kept", Stamp, "claude")],
        };
        var switched = SessionTemplate.SwitchProvider(pane, "codex");
        Check(switched is { Provider: "codex", Model: "default", ResumeId: null, SessionUsage: null, Draft: "typed", Logs.Count: 2 } && switched.Settings == new RunSettings(), "the new agent starts with its defaults, no conversation and none of the old agent's usage");
        Check(switched.Title == ProviderCatalog.Name("codex") && SessionTemplate.SwitchProvider(pane with { Title = "My pane" }, "gemini").Title == "My pane", "a title that was the old agent's name follows; a title of the user's stays");
        Check(switched.Logs[0].Text == "kept" && switched.Logs[1] is { Kind: "system", Provider: "codex" } line && line.Text == Locale.Get("pane.log.switchedProvider", Values(("provider", "Codex"))), "the transcript keeps what was said and adds one line naming the new agent");
        Check(ReferenceEquals(SessionTemplate.SwitchProvider(pane, "claude"), pane) && ReferenceEquals(SessionTemplate.SwitchProvider(pane, "nope"), pane), "the same agent, or none the app knows, changes nothing");
        var full = pane with { Logs = Enumerable.Range(0, 400).Select(i => new LogEntry("log-" + i, "assistant", "line " + i, Stamp)).ToList() };
        Check(SessionTemplate.SwitchProvider(full, "gemini").Logs is { Count: 400 } logs && logs[0].Id == "log-1" && logs[^1].Kind == "system", "the line is trimmed in like any other entry");
        foreach (var language in Locale.Languages) Check(Text(language, "pane.log.switchedProvider")?.Contains("{provider}") == true, language + ": the line names the agent");
        return Task.CompletedTask;
    }

    // M/SessionPaneView.swift:719-726.
    internal static Task StatusLineAsksAgainOnATick()
    {
        Check(StatusLineRefresher.TickInterval(true) == TimeSpan.FromSeconds(10), "every 10 s while the pane's run goes on");
        Check(StatusLineRefresher.TickInterval(false) == TimeSpan.FromSeconds(60), "every 60 s at rest");
        Check(StatusLineRefresher.MinimumInterval < StatusLineRefresher.TickInterval(true), "a tick is never swallowed by the least interval between runs");
        return Task.CompletedTask;
    }

    // M/AppStore+CLIAccounts.swift:9-10, 95-124.
    internal static async Task TerminalSignInIsPolledUntilItTakes()
    {
        static CliAccountStatus Status(bool? loggedIn, bool? verified = null) => new() { Provider = "claude", LoggedIn = loggedIn, AccessVerified = verified };
        var quick = TimeSpan.FromMilliseconds(10);
        Check(CliTerminalLogin.PollInterval == TimeSpan.FromSeconds(5) && CliTerminalLogin.PollLimit == TimeSpan.FromMinutes(10), "the account is read every 5 s for up to 10 minutes");
        Check(CliTerminalLogin.SignedIn(Status(true)) && CliTerminalLogin.SignedIn(Status(true, true)) && !CliTerminalLogin.SignedIn(Status(true, false)) && !CliTerminalLogin.SignedIn(Status(false)) && !CliTerminalLogin.SignedIn(Status(null)), "signed in means the CLI says so and its access is not known to be unverified");

        var reads = 0;
        var result = await CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(++reads >= 4)), interval: quick);
        Check(result is { Outcome: CliLoginOutcome.LoggedIn, StartedSignedOut: true } && reads == 4 && CliTerminalLogin.Resends(result), "signed out, then signed in on a later reading: the wait ends and the waiting requests go again");

        reads = 0;
        result = await CliTerminalLogin.RunAsync(_ => { reads++; return Task.FromResult(Status(true)); }, interval: quick);
        Check(result is { Outcome: CliLoginOutcome.LoggedIn, StartedSignedOut: false } && reads == 1 && !CliTerminalLogin.Resends(result), "an account signed in all along ends the wait at once and sends nothing again");

        reads = 0;
        result = await CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(++reads == 1 ? null : true)), interval: quick);
        Check(result.Outcome == CliLoginOutcome.LoggedIn && !result.StartedSignedOut && !CliTerminalLogin.Resends(result), "a status that could not be read at first is no proof of a new sign-in");
        // M/AppStore+CLIAccounts.swift:98: the status held before the terminal opened decides, whatever the first reading says.
        reads = 0;
        result = await CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(++reads == 1 ? null : true)), interval: quick, before: Status(false));
        Check(result.Outcome == CliLoginOutcome.LoggedIn && CliTerminalLogin.Resends(result), "signed out before the terminal opened, an unreadable first reading, then signed in: the waiting requests go again");
        result = await CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(true)), interval: quick, before: Status(false));
        Check(result.Outcome == CliLoginOutcome.LoggedIn && CliTerminalLogin.Resends(result), "a sign-in that took before the first reading still sends the waiting requests again");
        result = await CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(true)), interval: quick, before: Status(true));
        Check(!CliTerminalLogin.Resends(result), "signed in all along sends nothing again");

        result = await CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(false)), interval: quick, limit: TimeSpan.FromMilliseconds(60));
        Check(result is { Outcome: CliLoginOutcome.TimedOut, StartedSignedOut: true } && !CliTerminalLogin.Resends(result), "the wait gives up at its limit and sends nothing again");

        // A terminal whose window is known to have closed ends the wait with one last reading.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); reads = 0;
        var waiting = CliTerminalLogin.RunAsync(_ => { Interlocked.Increment(ref reads); return Task.FromResult(Status(false)); }, closed: closed.Task, interval: TimeSpan.FromSeconds(30));
        await Verification.Until(() => Volatile.Read(ref reads) == 1);
        closed.SetResult();
        result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Outcome == CliLoginOutcome.Exited && reads == 2 && !CliTerminalLogin.Resends(result), "a closed window ends the wait after one more reading");
        var signed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); reads = 0;
        waiting = CliTerminalLogin.RunAsync(_ => Task.FromResult(Status(Interlocked.Increment(ref reads) > 1)), closed: signed.Task, interval: TimeSpan.FromSeconds(30));
        await Verification.Until(() => Volatile.Read(ref reads) == 1);
        signed.SetResult();
        result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Outcome == CliLoginOutcome.LoggedIn && CliTerminalLogin.Resends(result), "a sign-in that took as the window closed is still a sign-in");

        // "Cancel waiting" ends the wait at once.
        using var cancel = new CancellationTokenSource(); reads = 0;
        var cancelled = CliTerminalLogin.RunAsync(_ => { Interlocked.Increment(ref reads); return Task.FromResult(Status(false)); }, cancel.Token, interval: TimeSpan.FromSeconds(30));
        await Verification.Until(() => Volatile.Read(ref reads) == 1);
        cancel.Cancel();
        var threw = false;
        try { await cancelled.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { threw = true; }
        Check(threw && reads == 1, "a cancelled wait reads nothing more");
        foreach (var language in Locale.Languages) Check(Text(language, "loginRecovery.terminalPending") is { Length: > 0 } pending && pending != Text(language, "loginRecovery.body"), language + ": the card has its own words while the sign-in goes on in a terminal");
    }

    // M/Tests/TerminalInputPolicyTests.swift:15-37.
    internal static Task TerminalInputRefusesALineBreak()
    {
        var written = new List<string>(); bool Take(string text) { written.Add(text); return true; }
        Check(TerminalInputPolicy.Apply("npx example-installer", false, Take) == TerminalInputPolicy.Outcome.Pasted && written.SequenceEqual(["npx example-installer"]), "a command from elsewhere is put in and left for the user to run");
        written.Clear();
        Check(TerminalInputPolicy.Apply("claude login", true, Take) == TerminalInputPolicy.Outcome.PastedAndRan && written.SequenceEqual(["claude login", "\r"]), "only a command the app wrote itself is followed by Enter");
        foreach (var separator in new[] { "\n", "\r", ((char)0x2028).ToString(), ((char)0x2029).ToString() })
        {
            written.Clear();
            Check(TerminalInputPolicy.HasLineBreak("rm -rf /" + separator + "echo done"), "a line break is seen: U+" + ((int)separator[0]).ToString("X4"));
            Check(TerminalInputPolicy.Apply("rm -rf /" + separator + "echo done", true, Take) == TerminalInputPolicy.Outcome.Refused && written.Count == 0, "a line break stops the whole text: U+" + ((int)separator[0]).ToString("X4"));
        }
        Check(!TerminalInputPolicy.HasLineBreak("echo one && echo two") && !TerminalInputPolicy.HasLineBreak(""), "one line is one line");
        var attempts = 0;
        Check(TerminalInputPolicy.Apply("ls", true, _ => { attempts++; return false; }) == TerminalInputPolicy.Outcome.Failed && attempts == 1, "text the terminal did not take is never followed by Enter");
        Check(TerminalInputPolicy.Apply("ls", true, text => text != "\r") == TerminalInputPolicy.Outcome.Failed, "an Enter the terminal did not take is a failure too");
        Check(TerminalInputPolicy.NewlineRefusal == Locale.Get("terminal.input.newlineRefused"), "the refusal has its own sentence");
        foreach (var language in Locale.Languages) Check(Text(language, "terminal.input.newlineRefused") is { Length: > 0 }, language + ": the refusal is worded");
        return Task.CompletedTask;
    }

    // M/SessionPaneView.swift:322-325, 962-964.
    internal static Task RunLogIsCopiedBehindItsRoles()
    {
        var pane = new RunSession
        {
            Id = "pane", WorkspaceId = "w", Provider = "codex",
            Logs =
            [
                new("1", "user", "hello", Stamp), new("2", "assistant", "hi", Stamp, "claude"), new("3", "assistant", "hey\nthere", Stamp), new("4", "output", "out", Stamp),
                new("5", "error", "bad", Stamp), new("6", "system", "note", Stamp), new("7", "image", "picture", Stamp),
            ],
        };
        var system = Locale.Get("pane.log.roleSystem");
        var expected = string.Join("\n\n", "[" + Locale.Get("pane.log.roleUser") + "] hello", "[Claude] hi", "[Codex] hey\nthere", "[" + Locale.Get("composer.sessionInfo.output") + "] out",
            "[" + Locale.Get("session.state.error") + "] bad", "[" + system + "] note", "[" + system + "] picture");
        Check(RunLogText.Copy(pane) == expected, "each entry is copied behind its role, a blank line between entries; got:\n" + RunLogText.Copy(pane));
        Check(RunLogText.Copy(pane with { Logs = [] }) == "", "an empty log copies nothing");
        Check(RunLogText.Role(new("8", "assistant", "x", Stamp, "gemini"), "claude") == "Gemini" && RunLogText.Role(new("9", "assistant", "x", Stamp), "claude") == "Claude", "an answer is its own agent's, or the pane's when the entry names none");
        foreach (var language in Locale.Languages) Check(Text(language, "pane.log.roleUser") is { Length: > 0 } && Text(language, "pane.log.roleSystem") is { Length: > 0 }, language + ": the roles are worded");
        return Task.CompletedTask;
    }

    // M/Tests/AddAgentPaneTests.swift:21-51.
    internal static Task ResumeLookUpAsksOnlyForASessionToContinue()
    {
        static ResumableSession Session(string provider, string id) => new(provider, id, null, DateTimeOffset.UnixEpoch, null, null, "");
        Check(AddPaneMenu.QuietLookUp == TimeSpan.FromMilliseconds(300), "a slow look-up shows the sheet after 300 ms");
        Check(!AddPaneMenu.AsksResume("gemini", [Session("gemini", "g1"), Session("claude", "c1")], []), "an agent without resume starts at once even with sessions listed");
        Check(!AddPaneMenu.AsksResume("claude", [], []) && !AddPaneMenu.AsksResume("claude", [Session("codex", "x1")], []), "nothing of this agent's to continue: no question");
        Check(!AddPaneMenu.AsksResume("codex", [Session("codex", "AbC-1")], ["abc-1"]) && !AddPaneMenu.AsksResume("codex", [Session("codex", "abc-1")], ["ABC-1"]), "a session a pane took while the look-up ran does not count, in any case");
        Check(AddPaneMenu.AsksResume("claude", [Session("claude", "c1")], []) && AddPaneMenu.AsksResume("codex", [Session("codex", "x1"), Session("codex", "x2")], ["x1"]), "one free session of this agent is enough to ask");
        foreach (var language in Locale.Languages) Check(Text(language, "resume.loading") is { Length: > 0 }, language + ": the checking sheet has its words");
        return Task.CompletedTask;
    }

    // M/ComponentsSettingsView.swift:110-115, 199-252.
    internal static Task ToolkitNamesItsToolsAndSaysWhy()
    {
        static ToolkitFileEntry Entry(string id, string name) => new(id, name, "plugin", new ToolkitFileReader.PluginSpec("owner/" + id, id + "@market"), Approval: null, Source: ToolkitFileReader.ToolkitEntrySource.User);
        var plan = new[]
        {
            new ToolkitPlanItem { Entry = Entry("a", "Tool A"), Action = ToolkitPlanItem.PlanAction.Run, Commands = [["claude", "plugin", "install", "a@market"], ["claude", "plugin", "enable", "a@market"]] },
            new ToolkitPlanItem { Entry = Entry("b", "Tool B"), Action = ToolkitPlanItem.PlanAction.Skip, Commands = [] },
        };
        var rows = ToolkitPresentation.PlanRows(plan);
        Check(rows.Count == 2 && rows[0] is { Name: "Tool A", Skipped: false } && rows[0].Lines.SequenceEqual(["claude plugin install a@market", "claude plugin enable a@market"]), "a tool that will run is named over every command it runs");
        Check(rows[1] is { Name: "Tool B", Skipped: true } && rows[1].Lines.SequenceEqual(["→ " + Locale.Get("settings.toolkit.verdictSkipped")]), "a tool that will not run is named too, with the one line that says so");
        var run = new ToolkitRunItem { EntryId = "a", RunVerdict = ToolkitRunItem.Verdict.Installed };
        Check(ToolkitPresentation.ResultName(run, [Entry("a", "Tool A")]) == "Tool A" && ToolkitPresentation.ResultName(run, []) == "a", "a result is named as the tool is, or by its id once the tool is gone");
        Check(ToolkitPresentation.ErrorBanner("why it failed") == Locale.Get("settings.toolkit.errorBanner") + " why it failed", "the banner says that the file failed, then why");

        var directory = Verification.Temp();
        try
        {
            var path = Path.Combine(directory, "toolkit.json");
            File.WriteAllText(path, "not-json{{{");
            var broken = new ToolkitStore(directory);
            var reason = broken.List().Error;
            Check(reason == Locale.Get("toolkit.error.unreadable"), "a file that cannot be parsed is reported in the app's words, as on the Mac, not the runtime's own text");
            var refused = "";
            try { broken.Add(Entry("c", "Tool C")); } catch (InvalidOperationException ex) { refused = ex.Message; }
            Check(refused == reason && File.ReadAllText(path) == "not-json{{{", "a change to an unreadable file is refused with the same reason and the file is left as it was");
            File.WriteAllText(path, """{"version":2,"entries":[]}""");
            Check(new ToolkitStore(directory).List().Error == Locale.Get("toolkit.error.unreadable"), "a file of another version is reported as unreadable in the app's words");
            File.WriteAllText(path, """{"version":1,"entries":[]}""");
            Check(new ToolkitStore(directory).List().Error is null, "a readable file reports nothing");
        }
        finally { Directory.Delete(directory, true); }
        foreach (var language in Locale.Languages) Check(Text(language, "toolkit.error.unreadable") is { Length: > 0 } && Text(language, "settings.toolkit.verdictSkipped") is { Length: > 0 }, language + ": the reason and the skipped word exist");
        return Task.CompletedTask;
    }
}
