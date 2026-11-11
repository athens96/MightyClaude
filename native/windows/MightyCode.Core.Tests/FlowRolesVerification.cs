using System.Globalization;
using System.Text.Json;
using MightyCode.Core;

/// <summary>Builders and checks the role view's verifications share: the macOS tests' own fixtures (FlowRolesTests, FlowCardTests, FlowLogTests…)
/// in C#, a moment as an offset from 1 800 000 000 seconds as they count it.</summary>
internal static class FlowFixtures
{
    internal static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    internal static void Same<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{why}: expected <{expected}> but was <{actual}>"); }
    internal static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why)
    {
        var (was, wanted) = (actual.ToList(), expected.ToList());
        if (!was.SequenceEqual(wanted)) throw new InvalidOperationException($"{why}: expected [{string.Join(" | ", wanted)}] but was [{string.Join(" | ", was)}]");
    }
    internal static void Korean(Action action) => FlowTrackerVerification.InLanguage("ko", action);

    internal static string Stamp(int seconds) => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L + seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    internal static DateTimeOffset Date(int seconds) => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L + seconds);
    internal static LogEntry Tool(string id, string summary, int at = 0, string name = "Read", string kind = "tool", string state = "completed") =>
        new(id, "system", summary, Stamp(at), "claude", new AgentActivity(id, "claude", kind, state, summary, name));
    internal static LogEntry Said(string id, string text, int at = 0, string kind = "assistant") => new(id, kind, text, Stamp(at), "claude");
    internal static GraphResponseRecord Answered(string id, string model) => new(id, model, new GraphTokenUsage(10, 1), []);

    internal static MightyGraphAgent Agent(string id, string? parent = null, string status = "running", bool background = false, string? kind = null, string? source = null,
        string? title = null, string input = "확인", List<LogEntry>? entries = null, GraphAgentIdentity? identity = null, GraphAgentFigures? figures = null,
        GraphTokenUsage? usage = null, List<GraphResponseRecord>? records = null) => new()
    {
        Id = id, ParentID = parent, Title = title ?? id, Input = input, Status = status, Kind = kind, Background = background ? true : null, SourceBlockId = source,
        Entries = entries ?? [], Identity = identity is { IsEmpty: false } ? identity : null, Figures = figures is { IsEmpty: false } ? figures : null, Usage = usage, ResponseRecords = records,
    };

    internal static MightyGraphRun Run(string id, string status = "running", List<MightyGraphAgent>? agents = null, List<LogEntry>? root = null, string? output = null,
        bool notice = false, string? title = null, string? model = "claude-sonnet-5-5", string provider = "claude", GraphAdvisor? advisor = null, string? configuredAdvisor = null,
        string? effort = null)
    {
        var run = new MightyGraphRun
        {
            Id = id, Input = "요청 " + id, Status = status, Agents = agents ?? [], RootEntries = root ?? [], FinalOutput = output,
            ResponseRecords = model is null ? null : [new GraphResponseRecord(id + "-m", model, new GraphTokenUsage(1, 1), [])],
            Provider = provider, NoticeRun = notice ? true : null, NoticeTitle = title, Advisor = advisor, ConfiguredAdvisor = configuredAdvisor, ConfiguredEffort = effort,
        };
        MightyGraphSupport.RefreshResult(run);
        return run;
    }

    internal static MightyGraphLayout Layout(IReadOnlyList<MightyGraphRun> runs, bool running = true, IEnumerable<string>? opened = null, bool fanOut = true,
        IEnumerable<string>? expanded = null, Dictionary<string, GraphBlockSize>? sizes = null, string? plan = null) =>
        MightyGraphLayout.Make(MightyGraphSupport.ShownRuns(runs), "", running, (expanded ?? []).ToHashSet(StringComparer.Ordinal), blockSizes: sizes, planRunID: plan,
            splitsExecution: true, fanOut: fanOut ? new MightyGraphLayout.FanOut((opened ?? []).ToHashSet(StringComparer.Ordinal)) : null);

    internal static string Node(MightyGraphRun run, string suffix) => MightyGraphLayout.NodeID(run, suffix);
    internal static MightyGraphLayout.Node? Find(MightyGraphLayout layout, string id) => layout.Nodes.FirstOrDefault(n => n.Id == id);
    internal static MightyGraphLayout.Node Must(MightyGraphLayout layout, string id) => Find(layout, id) ?? throw new InvalidOperationException("no node " + id);
    /// <summary>Two frames share area (CGRect.intersects): touching edges do not.</summary>
    internal static bool Overlap(GraphRect a, GraphRect b) => a.X < b.MaxX && b.X < a.MaxX && a.Y < b.MaxY && b.Y < a.MaxY;
}

/// <summary>
/// Who a flow block is and what its card says, checked against the cases of the macOS suite: FlowRolesTests (one ink per role, a custom agent's
/// colour, the type chip, the marks, the model before a block answers, which chips a header has room for), AgentDefinitionsTests (custom agents' colour
/// and model from their definition files), FlowCardTests (when a sub-agent is a card and its four lines) and ClaudeAdvisorSettingsTests (the pane's
/// 조언자 setting on the argv and Claude's own settings files, with Windows paths).
/// </summary>
internal static class FlowRolesVerification
{
    private static void Check(bool value, string why) => FlowFixtures.Check(value, why);
    private static void Same<T>(T actual, T expected, string why) => FlowFixtures.Same(actual, expected, why);
    private static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why) => FlowFixtures.SameList(actual, expected, why);
    private static readonly string[] ChildKinds = ["task", "steer", "compact", "question"];

    private static MightyGraphAgent Agent(string title = "Map the repo", string? type = null, string? requested = null, string? resolved = null, string? isolation = null,
        bool background = false, string? kind = null) =>
        FlowFixtures.Agent("a", title: title, kind: kind, background: background, input: "", identity: new GraphAgentIdentity(type, requested, resolved, isolation));

    // ── FlowRolesTests ──

    internal static Task TheRolesTheirInksAndACustomAgentsColour()
    {
        var inks = Enum.GetValues<FlowRole>().Select(FlowRoles.Ink).ToList();
        Same(inks.Distinct().Count(), 4, "four roles, four inks");
        Same(FlowRoles.Ink(FlowRole.Subagent), FlowInk.Agent, "the sub-agent's violet");
        Check(FlowRoles.Ink(FlowRole.Task) == FlowInk.Neutral && FlowRoles.Ink(FlowRole.Task) != FlowRoles.Ink(FlowRole.Lead), "a command's ink is the secondary one, not the lead's");
        foreach (var palette in new[] { DesignTokens.Light, DesignTokens.Dark })
        {
            var colours = inks.Select(ink => palette[Enum.Parse<DesignToken>(FlowRoles.Token(ink), true)]).ToList();
            Same(colours.Distinct().Count(), 4, "four colours in " + (palette.IsDark ? "dark" : "light"));
            foreach (var colour in colours) Check(colour.Contrast(palette.Card) >= 4.5, "an AA ink on a card: " + colour.Hex);
        }
        // Every ink names a design token the page has.
        foreach (var ink in Enum.GetValues<FlowInk>()) Check(Enum.TryParse<DesignToken>(FlowRoles.Token(ink), true, out _), "a token for " + ink);

        Same(FlowRoles.AgentColors.Length, 8, "Claude Code's eight colours");
        foreach (var colour in FlowRoles.AgentColors) Check(FlowRoles.Ink(colour) is not null, colour + " is a palette ink");
        Same(FlowRoles.Ink("red"), FlowInk.Err, "red");
        Same(FlowRoles.Ink("blue"), FlowInk.Accent, "blue");
        Same(FlowRoles.Ink("green"), FlowInk.Done, "green");
        Same(FlowRoles.Ink("yellow"), FlowInk.Wait, "yellow");
        Same(FlowRoles.Ink("purple"), FlowInk.Agent, "purple");
        Same(FlowRoles.Ink("orange"), FlowInk.Question, "orange");
        Same(FlowRoles.Ink("pink"), FlowInk.Steer, "pink");
        Same(FlowRoles.Ink(" Cyan "), FlowInk.Task, "cyan, trimmed and in any case");
        Same(FlowRoles.Ink("teal"), null, "a colour Claude Code lacks");
        Same(FlowRoles.Ink((string?)null), null, "none");
        // Six of the eight are told apart; the two ambers are one value in today's palette.
        Same(FlowRoles.AgentColors.Select(FlowRoles.Ink).OfType<FlowInk>().Select(ink => DesignTokens.Light[Enum.Parse<DesignToken>(FlowRoles.Token(ink), true)]).Distinct().Count(), 7, "seven values");

        Same(FlowRoles.Ink(FlowRole.Subagent, "green"), FlowInk.Done, "a custom colour replaces the sub-agent ink");
        Same(FlowRoles.Ink(FlowRole.Subagent, "mauve"), FlowInk.Agent, "an unknown one does not");
        Same(FlowRoles.Ink(FlowRole.Lead, "green"), FlowRoles.Ink(FlowRole.Lead), "the lead keeps its ink");
        Same(FlowRoles.Ink(FlowRole.Advisor, "green"), FlowRoles.Ink(FlowRole.Advisor), "the advisor keeps its ink");
        return Task.CompletedTask;
    }

    internal static Task WhoTheLeadAndASubagentAre()
    {
        FlowFixtures.Korean(() =>
        {
            var run = FlowFixtures.Run("r", effort: "high");
            var lead = FlowRoles.Lead(run);
            Check(lead.Role == FlowRole.Lead && lead.Ink == FlowRoles.Ink(FlowRole.Lead), "the lead and its ink");
            Same(lead.Effort, "High", "the pane's effort");
            Same(lead.Summary, "메인 · 사고 강도 · High", "the lead in one line");
            Check(lead.Marks.Count == 0 && lead.TypeChip is null && lead.ModelLabel is null, "no marks, chip or model");
            run.ConfiguredEffort = "default";
            Same(FlowRoles.Lead(run).Effort, null, "the CLI decides");
            Same(FlowRoles.Lead(run).Summary, "메인", "the lead alone");
            run.ConfiguredEffort = null;
            Same(FlowRoles.Lead(run).Effort, null, "an older save");
            run.ConfiguredEffort = "minimal";
            Same(FlowRoles.Lead(run).Effort, "Minimal", "pi's levels are efforts too");

            var who = FlowRoles.Subagent(Agent(type: "Explore", requested: "haiku", background: true))!;
            Check(who.Role == FlowRole.Subagent && who.Ink == FlowInk.Agent, "a sub-agent and its ink");
            Check(who.Type == "Explore" && who.TypeChip == "Explore", "its type and chip");
            Same(who.ModelLabel, "haiku · 요청", "asked for, not yet said to run");
            SameList(who.Marks, [FlowMark.Background], "its mark");
            Same(who.Summary, "하위 에이전트 · Explore · 요청한 모델 Haiku · 백그라운드", "it in one line");
            var launched = FlowRoles.Subagent(Agent(type: "Explore", requested: "haiku", resolved: "claude-haiku-5-5"))!;
            Same(launched.ModelLabel, "claude-haiku-5-5", "the resolved model outranks the alias");
            Same(launched.Summary, "하위 에이전트 · Explore · Haiku 5.5", "the resolved model in words");
            Same(launched.Marks.Count, 0, "no mark");

            var fork = FlowRoles.Subagent(Agent("fork count a", type: "fork", resolved: "claude-haiku-5-5", background: true))!;
            SameList(fork.Marks, [FlowMark.Background, FlowMark.Fork], "a fork's marks");
            Check(fork.TypeChip is null && fork.Type == "fork", "a fork has no chip");
            Same(fork.Summary, "하위 에이전트 · Haiku 5.5 · 백그라운드 · 포크", "a fork in one line");
            var isolated = FlowRoles.Subagent(Agent(type: "general-purpose", isolation: "worktree"))!;
            SameList(isolated.Marks, [FlowMark.Worktree], "a worktree");
            Same(isolated.TypeChip, "general-purpose", "its chip");
            Same(FlowRoles.Subagent(Agent(type: "general-purpose", isolation: "remote"))!.Marks.Count, 0, "a remote isolation is no worktree");

            var definition = new AgentDefinition("reviewer", "green", "sonnet");
            var reviewer = FlowRoles.Subagent(Agent(type: "reviewer"), definition)!;
            Same(reviewer.Ink, FlowInk.Done, "the definition's colour");
            Same(reviewer.ModelLabel, "sonnet · 요청", "the definition's model stands in");
            Same(FlowRoles.Subagent(Agent(type: "reviewer", requested: "opus"), definition)!.ModelLabel, "opus · 요청", "the call's own model wins");
            var plain = FlowRoles.Subagent(Agent(type: "reviewer"), new AgentDefinition("reviewer", "plaid", "inherit"))!;
            Check(plain.ModelLabel is null && plain.Ink == FlowInk.Agent, "inherit asks for none; plaid is no colour");
            Same(plain.Summary, "하위 에이전트 · reviewer", "in one line");

            Same(GraphModelLabel.AgentModelLabel("claude-opus-5-5", "opus"), "claude-opus-5-5", "resolved first");
            Same(GraphModelLabel.AgentModelLabel(null, "opus"), "opus · 요청", "requested, marked");
            Same(GraphModelLabel.AgentModelLabel("", ""), null, "nothing");
            Same(GraphModelLabel.AgentModelLabel(null, "inherit"), null, "inherit");
            Same(ModelUsageFormat.VersionedNodeLabel("opus · 요청"), "Opus · 요청", "a requested label read through its model's name");
            Same(ModelUsageFormat.VersionedNodeLabel("opus · 설정"), "Opus · 설정", "a configured one");
            Same(ModelUsageFormat.VersionedNodeLabel("claude-haiku-5-5"), "Haiku 5.5", "a resolved one");
            Same(ModelUsageFormat.BlockCapsule(null, [], "haiku · 요청", versioned: true), "Haiku · 요청", "the capsule");
        });
        Same(FlowRoles.Subagent(Agent("Explore", type: "Explore"))!.TypeChip, null, "the chip does not repeat a title that is the type");
        var plugin = FlowRoles.Subagent(Agent("Fix the build", type: "oh-my-claudecode:executor"))!;
        Check(plugin.TypeChip == "executor" && plugin.Type == "oh-my-claudecode:executor", "a plugin's agent by its own name");
        Same(FlowRoles.Subagent(Agent("executor", type: "oh-my-claudecode:executor"))!.TypeChip, null, "nor a title that is the plugin agent's name");
        Same(FlowRoles.TypeName("a:b:c"), "c", "the last part");
        Same(FlowRoles.TypeName("plain"), "plain", "a plain type");
        Same(FlowRoles.TypeName("trailing:"), "trailing:", "a trailing colon");
        Check(FlowRoles.Subagent(Agent()) is not null && FlowRoles.Subagent(Agent())!.TypeChip is null, "a sub-agent that said nothing is still one");
        foreach (var kind in ChildKinds) Same(FlowRoles.Subagent(Agent(kind: kind)), null, kind + " is no sub-agent");
        var runs = new[] { FlowFixtures.Run("one", agents: [Agent(type: "Explore"), Agent(type: "reviewer"), Agent()]), FlowFixtures.Run("two", agents: [Agent(type: "Explore")]) };
        SameList(FlowRoles.Types(runs).Order(StringComparer.Ordinal), ["Explore", "reviewer"], "the types the blocks name");
        Same(FlowRoles.Types([]).Count, 0, "none");
        return Task.CompletedTask;
    }

    private static readonly FlowIdentity Who = new(FlowRole.Subagent, FlowInk.Agent, "Explore", "Explore", null, null, [FlowMark.Background, FlowMark.Worktree], "");

    internal static Task TheHeaderGivesWayFirstForTheChips()
    {
        Same(FlowRoles.FitHeader(MightyGraphBlockSize.MinimumWidth, "Map the repository layout", "실행 중", "12.3K · Haiku 5.5", 1, Who), FlowRoles.HeaderFit.None,
            "at the least width the title, status and tokens keep their room");
        var wide = FlowRoles.FitHeader(520, "Map the repository layout", "실행 중", "12.3K · Haiku 5.5", 1, Who);
        Check(wide.ShowsMarks && wide.TypeWidth >= FlowRoles.TypeFloor && wide.TypeWidth <= FlowRoles.TypeCeiling, "a wide header shows the marks and the chip");
        var seenMarksAlone = false;
        var previous = FlowRoles.HeaderFit.None;
        for (double width = 300; width <= 700; width += 4)
        {
            var fit = FlowRoles.FitHeader(width, "Map the repository layout", "실행 중", "12.3K · Haiku 5.5", 1, Who);
            if (fit.ShowsMarks && fit.TypeWidth is null) seenMarksAlone = true;
            if (previous.ShowsMarks) Check(fit.ShowsMarks, "the marks stay as it widens at " + width);
            if (previous.TypeWidth is { } before) Check((fit.TypeWidth ?? 0) >= before, "the chip only grows at " + width);
            if (fit.TypeWidth is not null) Check(fit.ShowsMarks, "no chip without the marks at " + width);
            previous = fit;
        }
        Check(seenMarksAlone && previous.TypeWidth is not null, "the marks come before the chip");
        var busy = FlowRoles.FitHeader(360, "Map", "실행 중", "12.3K · Haiku 5.5 +1", 1, Who);
        var quiet = FlowRoles.FitHeader(360, "Map", null, null, 1, Who);
        Check(quiet.TypeWidth is not null && (quiet.TypeWidth ?? 0) >= (busy.TypeWidth ?? 0), "a block without status or tokens has more room");
        var lead = new FlowIdentity(FlowRole.Lead, FlowInk.Task, null, null, null, "High", [], "");
        Check(!FlowRoles.FitHeader(300, "실행 · 요청 12", "턴 완료 · 백그라운드 2개 실행 중", "120.4K · Opus 5.5", 1, lead).ShowsEffort, "no room for the effort");
        Check(FlowRoles.FitHeader(500, "실행 · 요청 12", "실행 중", "120.4K · Opus 5.5", 1, lead).ShowsEffort, "room for it");
        Same(FlowRoles.FitHeader(900, "실행", null, null, 1, new FlowIdentity(FlowRole.Lead, FlowInk.Task, null, null, null, null, [], "")), FlowRoles.HeaderFit.None, "nothing to show");
        Check(FlowRoles.EstimatedWidth("실행 중", 10) > FlowRoles.EstimatedWidth("abc d", 10), "a wide character counts a full em");
        Same(FlowRoles.EstimatedWidth("", 10), 0d, "nothing");
        Check(FlowRoles.EstimatedWidth("aaaa", 10, true) > FlowRoles.EstimatedWidth("aaaa", 10), "monospaced is wider");
        return Task.CompletedTask;
    }

    // ── AgentDefinitionsTests ──

    private static void Write(string root, string path, string text)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    internal static Task CustomAgentsAreReadFromTheirDefinitionFiles()
    {
        Same(AgentDefinitions.Parse("---\nname: reviewer\ndescription: Reviews a diff\ncolor: green\nmodel: sonnet\ntools: Read, Grep\n---\n\nYou review code.\n", "code-reviewer.md"),
            new AgentDefinition("reviewer", "green", "sonnet"), "name, colour and model");
        Same(AgentDefinitions.Parse("---\ncolor: Blue\n---\n", "scout.md"), new AgentDefinition("scout", "blue"), "the file's own name");
        Same(AgentDefinitions.Parse("﻿---\nname: \"deep scout\"   # the long one\ncolor: 'purple'\nmodel: claude-opus-5-5 # pinned\n---\n", "x.md"),
            new AgentDefinition("deep scout", "purple", "claude-opus-5-5"), "quotes, comments and a BOM");
        Same(AgentDefinitions.Scalar("  'a # b'  "), "a # b", "a quoted comment mark");
        Same(AgentDefinitions.Scalar("plain # note"), "plain", "a trailing comment");
        Same(AgentDefinitions.Scalar("   "), null, "nothing");
        Same(AgentDefinitions.Scalar("[a, b]"), null, "a list");
        Same(AgentDefinitions.Scalar("|"), null, "a block");
        Same(AgentDefinitions.Scalar("\"unclosed"), null, "an unclosed quote");
        Same(AgentDefinitions.Parse("---\nname: fixer\ntools:\n\t- Read\n  - : broken\ndescription: >\n  folded\n  text: not a key\ncolor: orange\nmodel: [haiku\n---\nbody\n", "fixer.md"),
            new AgentDefinition("fixer", "orange"), "what YAML would reject still gives what it can");
        Same(AgentDefinitions.Parse("---\nname: odd\ncolor: teal\nmodel: two words\n---\n", "odd.md"), new AgentDefinition("odd"), "an unknown colour and a model no CLI names");
        Same(AgentDefinitions.Parse("---\ncolor: red\ncolor: blue\n---\n", "twice.md")?.Color, "red", "the first of a repeated key");
        Same(AgentDefinitions.Parse("# Just notes\n\ncolor: red\n", "notes.md"), null, "no frontmatter");
        Same(AgentDefinitions.Parse("---\nname: open\ncolor: red\n", "open.md"), null, "an unclosed one");
        Same(AgentDefinitions.Parse("", "empty.md"), null, "an empty file");
        Same(AgentDefinitions.Parse("---\n" + string.Concat(Enumerable.Repeat("x: y\n", AgentDefinitions.MaximumLines + 5)) + "color: red\n---\n", "long.md"), null, "a block past its first lines");
        Same(AgentDefinitions.Parse("---\r\nname: crlf\r\ncolor: cyan\r\n---\r\n", "crlf.md"), new AgentDefinition("crlf", "cyan"), "Windows line endings");

        var root = Verification.Temp();
        try
        {
            string config = Path.Combine(root, "config"), workspace = Path.Combine(root, "project");
            Write(config, Path.Combine("agents", "reviewer.md"), "---\nname: reviewer\ncolor: red\nmodel: opus\n---\n");
            Write(config, Path.Combine("agents", "scout.md"), "---\nname: scout\ncolor: cyan\n---\n");
            Write(workspace, Path.Combine(".claude", "agents", "reviewer.md"), "---\nname: reviewer\ncolor: green\n---\n");
            Write(workspace, Path.Combine(".claude", "agents", "README.txt"), "not an agent");
            var cache = new AgentDefinitions.Cache();
            var found = AgentDefinitions.Load(new HashSet<string> { "reviewer", "scout", "Explore" }, workspace, config, cache);
            SameList(found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value), ["reviewer=" + new AgentDefinition("reviewer", "green"), "scout=" + new AgentDefinition("scout", "cyan")],
                "the project's agent wins over the user's, only asked types");
            Same(AgentDefinitions.Load(new HashSet<string> { "reviewer" }, null, config, cache).GetValueOrDefault("reviewer")?.Color, "red", "without a project folder the user's");
            SameList(AgentDefinitions.Load(new HashSet<string> { "scout" }, workspace, config, cache).Keys, ["scout"], "only what was asked");
            Same(AgentDefinitions.Load(new HashSet<string>(), workspace, config, cache).Count, 0, "nothing asked");
            Same(AgentDefinitions.Load(new HashSet<string> { "reviewer" }, Path.Combine(root, "missing"), Path.Combine(root, "none"), cache).Count, 0, "folders that are not there");

            // A plugin's agent in its install folder.
            var install = Path.Combine(root, "cache", "tools", "1.2.0");
            Write(install, Path.Combine("agents", "executor.md"), "---\nname: executor\ncolor: pink\nmodel: sonnet\n---\n");
            var index = new Dictionary<string, object> { ["version"] = 2, ["plugins"] = new Dictionary<string, object> { ["tools@market"] = new[] { new Dictionary<string, string> { ["scope"] = "user", ["installPath"] = install } }, ["empty@market"] = Array.Empty<object>() } };
            Write(config, Path.Combine("plugins", "installed_plugins.json"), JsonSerializer.Serialize(index));
            var plugins = new AgentDefinitions.Cache();
            var scoped = AgentDefinitions.Load(new HashSet<string> { "tools:executor", "tools:missing", "other:executor", "tools:../escape", "tools:..\\escape" }, null, config, plugins);
            SameList(scoped.Select(p => p.Key + "=" + p.Value), ["tools:executor=" + new AgentDefinition("tools:executor", "pink", "sonnet")], "the plugin's agent");
            SameList(plugins.PluginFolders(config).Keys.Order(StringComparer.Ordinal), ["tools"], "the installed plugins");

            // A folder is read again only when its files change.
            var agents = Path.Combine(root, "solo", "agents");
            Write(Path.Combine(root, "solo"), Path.Combine("agents", "scout.md"), "---\nname: scout\ncolor: cyan\n---\n");
            var folders = new AgentDefinitions.Cache();
            SameList(folders.Definitions(agents).Select(d => d.Color), ["cyan"], "read");
            SameList(folders.Definitions(agents), [new AgentDefinition("scout", "cyan")], "the same");
            Write(Path.Combine(root, "solo"), Path.Combine("agents", "scout.md"), "---\nname: scout\ncolor: yellow\nmodel: haiku\n---\n");
            SameList(folders.Definitions(agents), [new AgentDefinition("scout", "yellow", "haiku")], "a file that grew is read again");
            Write(Path.Combine(root, "solo"), Path.Combine("agents", "second.md"), "---\nname: second\n---\n");
            SameList(folders.Definitions(agents).Select(d => d.Name), ["scout", "second"], "a new file");
            Directory.Delete(agents, true);
            Same(folders.Definitions(agents).Count, 0, "a folder that went");

            // Only a file's head is read.
            var heads = Path.Combine(root, "heads");
            var padding = string.Concat(Enumerable.Repeat("# " + new string('x', 98) + "\n", AgentDefinitions.MaximumBytes / 100 + 10));
            Write(heads, Path.Combine("agents", "huge.md"), "---\nname: huge\n" + padding + "color: red\n---\n");
            Write(heads, Path.Combine("agents", "small.md"), "---\nname: small\ncolor: red\n---\n" + string.Concat(Enumerable.Repeat("body\n", 50_000)));
            SameList(new AgentDefinitions.Cache().Definitions(Path.Combine(heads, "agents")), [new AgentDefinition("small", "red")], "the closing line past what is read");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }

        // Claude Code's own folder follows CLAUDE_CONFIG_DIR.
        var home = Path.Combine(Path.GetTempPath(), "someone");
        Same(ClaudeAdvisorSettings.ConfigDirectory(home, new Dictionary<string, string>()), Path.Combine(home, ".claude"), "the home's .claude");
        Same(ClaudeAdvisorSettings.ConfigDirectory(home, new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "" }), Path.Combine(home, ".claude"), "an empty variable");
        var configured = Path.Combine(Path.GetTempPath(), "claude-config");
        Same(ClaudeAdvisorSettings.ConfigDirectory(home, new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configured }), Path.GetFullPath(configured), "the variable's folder");
        return Task.CompletedTask;
    }

    // ── FlowCardTests ──

    internal static Task ASubagentOrACommandIsACardWhileItIsOne()
    {
        static MightyGraphAgent A(string status = "running", bool background = false, string? kind = null, string? source = null) =>
            FlowFixtures.Agent("a", status: status, background: background, kind: kind, source: source);
        Check(FlowCard.IsCompact(A(), false), "a running sub-agent is a card");
        Check(!FlowCard.IsCompact(A(), true), "until the user opens it");
        Check(FlowCard.IsCompact(A("waiting"), false), "a waiting one too");
        foreach (var status in new[] { "completed", "error", "stopped", "failed", "cancelled", "interrupted" })
            Check(!FlowCard.IsCompact(A(status), false) && !FlowCard.FoldsToCard(A(status)), "a foreground sub-agent that ended is its block: " + status);
        foreach (var status in new[] { "running", "completed", "error", "stopped" })
        {
            Check(FlowCard.IsCompact(A(status, true), false) && FlowCard.FoldsToCard(A(status, true)), "a background sub-agent stays a card: " + status);
            Check(!FlowCard.IsCompact(A(status, true), true), "opened in place: " + status);
        }
        Check(!FlowCard.IsCompact(A("completed", true, source: "original"), false) && !FlowCard.IsCompact(A("running", true, source: "original"), false), "a copy is never a card");
        foreach (var kind in ChildKinds.Where(k => k != "task"))
            Check(!FlowCard.IsCompact(A("running", true, kind), false) && !FlowCard.FoldsToCard(A("running", kind: kind)), kind + " is never a card");
        foreach (var status in new[] { "running", "completed", "error" })
            Check(FlowCard.IsCompact(A(status, true, "task"), false) && !FlowCard.IsCompact(A(status, true, "task"), true), "a command is a card until opened: " + status);
        Check(!FlowCard.IsCompact(A("completed", true, "task", "original"), false), "a command's copy is its block");
        return Task.CompletedTask;
    }

    internal static Task WhatACardSays()
    {
        FlowFixtures.Korean(() =>
        {
            var run = FlowFixtures.Run("r");
            var identity = new GraphAgentIdentity("Explore", "haiku");
            MightyGraphAgent A(GraphAgentIdentity? who = null, bool background = false, List<GraphResponseRecord>? records = null) =>
                FlowFixtures.Agent("a", title: "Map the repo", input: "", background: background, identity: who, records: records);
            var asked = FlowCard.Of(A(identity), run);
            Same(asked.Title, "Map the repo", "the name");
            Same(asked.Identity, "Explore · Haiku · 요청", "asked for, nothing answered yet");
            Check(asked.Status == "running" && asked.StateWord == "실행 중" && asked.Running, "running");
            Same(FlowCard.Of(A(identity with { ResolvedModel = "claude-haiku-5-5" }), run).Identity, "Explore · Haiku 5.5", "resolved at launch");
            Same(FlowCard.Of(A(identity, records: [new("m1", "claude-sonnet-5-5", new GraphTokenUsage(10, 5), [])]), run).Identity, "Explore · Sonnet 5.5", "the model that answered");
            Same(FlowCard.Of(A(new GraphAgentIdentity("tools:executor")), run).Identity, "executor", "a plugin's agent by its own name");
            var fork = FlowCard.Of(A(new GraphAgentIdentity("fork", null, "claude-opus-5-5"), true), run);
            Same(fork.Identity, "Opus 5.5", "a fork by its mark alone");
            SameList(fork.Marks, [FlowMark.Background, FlowMark.Fork], "its marks");
            Same(FlowCard.Of(A(), run).Identity, null, "nothing known, no line");
            Same(FlowCard.Of(A(), run).Ink, FlowInk.Agent, "the sub-agent ink");
            Same(FlowCard.Of(A(new GraphAgentIdentity("reviewer")), run, new AgentDefinition("reviewer", "green")).Ink, FlowInk.Done, "a custom agent's colour");
        });

        const string asked2 = "Look for call sites of the tracker.\nThen report.";
        MightyGraphAgent Doing(List<LogEntry>? entries = null, string input = "") => FlowFixtures.Agent("a", input: input, entries: entries);
        Same(FlowCard.Doing(Doing(input: asked2)), "Look for call sites of the tracker.", "the first line of its prompt");
        Same(FlowCard.Doing(Doing([FlowFixtures.Said("t1", "Starting.\n\nReading the tracker next.\n")], asked2)), "Reading the tracker next.", "its last line");
        Same(FlowCard.Doing(Doing([FlowFixtures.Tool("a", "Sources/A.swift 읽기"), FlowFixtures.Said("t1", "Found it."), FlowFixtures.Tool("b", "grep tracker", name: "Grep", state: "running"), FlowFixtures.Said("t2", "Now the tests.")], asked2)),
            "grep tracker", "the latest tool call outranks its words");
        Same(FlowCard.Doing(Doing([FlowFixtures.Tool("a", "", name: "WebFetch")])), "WebFetch", "a call without a summary is its tool");
        Same(FlowCard.Doing(Doing([FlowFixtures.Said("t1", "Thinking."), FlowFixtures.Tool("turn", "응답 생성", name: "", kind: "turn")])), "Thinking.", "the lifecycle row is no tool call");
        Same(FlowCard.Doing(Doing()), null, "nothing");
        var longest = FlowCard.Doing(Doing([FlowFixtures.Tool("a", new string('가', 400) + "\nsecond")]));
        Check(System.Text.Encoding.UTF8.GetByteCount(longest ?? "") <= FlowCard.LineBytes && longest?.Contains('\n') == false, "one bounded line");

        var plainRun = FlowFixtures.Run("r");
        var done = FlowFixtures.Agent("a", status: "completed", background: true, entries: [FlowFixtures.Tool("a", "a.txt 읽기"), FlowFixtures.Said("answer", "\nb.txt: 3\nc.txt: 5")]);
        var content = FlowCard.Of(done, plainRun);
        Same(content.Activity, "b.txt: 3", "an ended card's answer");
        Check(!content.Running, "ended");
        var stopped = FlowFixtures.Agent("a", status: "stopped", background: true, entries: [FlowFixtures.Tool("a", "a.txt 읽기"), FlowFixtures.Said("note", "응답 없이 끝났습니다.", kind: "system")]);
        Same(FlowCard.Outcome(stopped), "응답 없이 끝났습니다.", "the last thing it recorded");
        Same(FlowCard.Outcome(FlowFixtures.Agent("a", status: "completed", entries: [FlowFixtures.Tool("a", "a.txt 읽기")])), null, "nothing but a tool call");

        var agents = new List<MightyGraphAgent> { FlowFixtures.Agent("root"), FlowFixtures.Agent("child", "root"), FlowFixtures.Agent("grand", "child"), FlowFixtures.Agent("note", "child", kind: "compact"),
            FlowFixtures.Agent("other"), FlowFixtures.Agent("loop-a", "loop-b"), FlowFixtures.Agent("loop-b", "loop-a") };
        var nestedRun = FlowFixtures.Run("r", agents: agents);
        SameList(new[] { 0, 1, 4, 5 }.Select(i => FlowCard.Nested(agents[i], nestedRun)), [2, 1, 0, 1], "the sub-agents under each, a cycle ending");
        Same(FlowCard.Of(agents[0], nestedRun).Nested, 2, "the card's count");
        return Task.CompletedTask;
    }

    internal static Task ACardsFiguresAreWhatWasObserved()
    {
        FlowFixtures.Korean(() =>
        {
            var run = FlowFixtures.Run("r");
            var start = FlowFixtures.Date(0);
            var stamp = FlowFixtures.Stamp(0);
            var usage = new GraphTokenUsage(300, 45, 12_000);
            var running = FlowCard.Of(FlowFixtures.Agent("a", figures: new GraphAgentFigures(stamp, ToolCalls: 3), usage: usage), run);
            Same(FlowCard.Figures(running, start.AddSeconds(42)), "실행 중 · 42초 · 도구 3 · 토큰 12K", "running");
            Same(FlowCard.Figures(running, start.AddSeconds(125)), "실행 중 · 2분 5초 · 도구 3 · 토큰 12K", "minutes");
            Same(FlowCard.Figures(running, start.AddSeconds(-5)), "실행 중 · 0초 · 도구 3 · 토큰 12K", "a clock before the start");
            Same(FlowCard.Figures(FlowCard.Of(FlowFixtures.Agent("a"), run), start), "실행 중", "nothing live: the state alone");
            Same(FlowCard.Figures(FlowCard.Of(FlowFixtures.Agent("a", figures: new GraphAgentFigures(stamp, ToolCalls: 0)), run), start.AddSeconds(1)), "실행 중 · 1초", "no 도구 0");
            var done = FlowCard.Of(FlowFixtures.Agent("a", status: "completed", background: true, figures: new GraphAgentFigures(stamp, FlowFixtures.Stamp(65), 2), usage: usage), run);
            Same(FlowCard.Figures(done, start.AddSeconds(9_999)), "완료 · 1분 5초 · 도구 2 · 토큰 12K", "how long it ran");
            var restored = FlowCard.Of(FlowFixtures.Agent("a", status: "stopped", background: true, figures: new GraphAgentFigures(stamp, ToolCalls: 2)), run);
            Same(FlowCard.Figures(restored, start.AddSeconds(9_999)), "중단 · 도구 2", "an end the app did not see");
            Same(FlowCard.Figures(FlowCard.Of(FlowFixtures.Agent("a", status: "error", background: true), run), start), "실패", "failed");
            Check(!FlowCard.Figures(running, start.AddSeconds(42)).Contains('%') && !FlowCard.Figures(done, start).Contains('%'), "no percentage");
            // The page reads the time apart: the parts on either side of it.
            var (before, after) = FlowCard.FigureParts(running);
            SameList(before, ["실행 중"], "before the time");
            SameList(after, ["도구 3", "토큰 12K"], "after it");
        });
        Same(new GraphAgentFigures("yesterday", new string('9', 90), -1).Normalized, null, "nothing that reads back");
        Same(new GraphAgentFigures("2026-11-10T18:56:44.496Z", null, GraphAgentFigures.MaximumToolCalls + 1).Normalized, new GraphAgentFigures("2026-11-10T18:56:44.496Z"), "a count out of range");
        Check(new GraphAgentFigures("2026-11-10T18:56:44Z").Started is not null, "a time reads back");
        Check(new GraphAgentFigures().IsEmpty && !new GraphAgentFigures(ExitCode: 0).IsEmpty, "empty, and an exit code of 0 is something");
        Same(new GraphAgentFigures(ExitCode: 3).Normalized, new GraphAgentFigures(ExitCode: 3), "an exit code");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("n", "r", null, "task", "running", "t") { Figures = new(ExitCode: 2) })?.Figures, new GraphAgentFigures(ExitCode: 2), "a command's");
        Same(ExecutionGraphSupport.Normalized(new ExecutionGraphNode("s", "r", null, "steer", "running", "s") { Figures = new(ToolCalls: 2) })?.Figures, null, "no steer's");
        var budget = 100_000;
        var saved = MightyGraphSupport.Normalized([FlowFixtures.Run("r", "completed", [
            FlowFixtures.Agent("a", status: "completed", figures: new GraphAgentFigures("2026-11-10T18:56:44Z", "soon", 4)),
            FlowFixtures.Agent("t", status: "completed", kind: "task", figures: new GraphAgentFigures("2026-11-10T18:56:44Z", ExitCode: 0)),
            FlowFixtures.Agent("q", status: "completed", kind: "question", figures: new GraphAgentFigures(ToolCalls: 4))])], true, ref budget);
        Same(saved[0].Agents[0].Figures, new GraphAgentFigures("2026-11-10T18:56:44Z", ToolCalls: 4), "a sub-agent's, its unreadable end dropped");
        Same(saved[0].Agents[1].Figures, new GraphAgentFigures("2026-11-10T18:56:44Z", ExitCode: 0), "a command's");
        Same(saved[0].Agents[2].Figures, null, "a question's");
        var old = JsonSerializer.Deserialize<MightyGraphAgent>("""{"id":"a","title":"Old","input":"","status":"completed","entries":[]}""", Wire.Json)!;
        old.Figures = new GraphAgentFigures("2026-11-10T18:56:44Z", ToolCalls: 2);
        var text = JsonSerializer.Serialize(old, Wire.Json);
        Check(text.Contains("\"figures\":{") && text.Contains("\"toolCalls\":2") && !text.Contains("endedAt"), "only what is there: " + text);
        Same(JsonSerializer.Deserialize<MightyGraphAgent>(text, Wire.Json)!.Figures, old.Figures, "it reads back");

        // A finished background agent's card leads to its newest copy in a notice run.
        var original = FlowFixtures.Agent("block", status: "completed", background: true);
        var request = FlowFixtures.Run("r", "completed", [original]);
        var first = FlowFixtures.Run("r:notice:1", "completed", [FlowFixtures.Agent("r:notice:1:block", status: "completed", background: true, source: "block")], notice: true);
        var second = FlowFixtures.Run("r:notice:2", "completed", [FlowFixtures.Agent("r:notice:2:other", status: "completed", background: true, source: "other"),
            FlowFixtures.Agent("r:notice:2:block", status: "completed", background: true, source: "block")], notice: true);
        Same(FlowCard.CopyNodeID(original, [request]), null, "no copy yet");
        Same(FlowCard.CopyNodeID(original, [request, first]), FlowFixtures.Node(first, "agent:r:notice:1:block"), "its copy");
        Same(FlowCard.CopyNodeID(original, [request, first, second]), FlowFixtures.Node(second, "agent:r:notice:2:block"), "the newest copy");
        var impostor = FlowFixtures.Run("x", "completed", [FlowFixtures.Agent("x-copy", status: "completed", source: "block")]);
        Same(FlowCard.CopyNodeID(original, [request, impostor]), null, "only a notice run holds a copy");
        return Task.CompletedTask;
    }

    // ── ClaudeAdvisorSettingsTests ──

    private static StartRunRequest Request(string? advisor, string effort = "default", string provider = "claude", string mode = "plan") =>
        new("run", "workspace", "claude", "go", [], "sonnet", provider, new RunSettings(effort, mode, Advisor: advisor));
    private static Dictionary<string, string>? Environment(List<string> arguments)
    {
        var index = arguments.IndexOf("--settings");
        if (index < 0) return null;
        using var document = JsonDocument.Parse(arguments[index + 1]);
        return document.RootElement.GetProperty("env").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }
    private static string Words(Dictionary<string, string>? env) => env is null ? "none" : string.Join(",", env.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
    private static readonly string Plugin = Path.Combine(Path.GetTempPath(), "plugin");

    internal static Task ThePanesAdvisorSettingReachesTheCLIAsTheMacPassesIt()
    {
        var plain = ProviderCatalog.Arguments(Request(null), Plugin);
        Check(!plain.Contains("--advisor") && Environment(plain) is null, "following Claude's settings passes nothing");
        Same(Words(Environment(ProviderCatalog.Arguments(Request(null, "high"), Plugin))), "CLAUDE_CODE_EFFORT_LEVEL=high", "the effort alone");
        var model = ProviderCatalog.Arguments(Request("opus"), Plugin);
        Same(model[model.IndexOf("--advisor") + 1], "opus", "a model as --advisor");
        Same(model[model.IndexOf("--model") + 1], "sonnet", "beside the main model");
        Same(Environment(model), null, "no switch: the CLI checks the model itself");
        var full = ProviderCatalog.Arguments(Request("claude-opus-5-5[1m]"), Plugin);
        Same(full[full.IndexOf("--advisor") + 1], "claude-opus-5-5[1m]", "a full model id");
        var off = ProviderCatalog.Arguments(Request(RunSettings.AdvisorOff), Plugin);
        Check(!off.Contains("--advisor"), "off names no model");
        Same(Words(Environment(off)), "CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1", "off is the disable variable for that run");
        Same(Words(Environment(ProviderCatalog.Arguments(Request(RunSettings.AdvisorOff, "high"), Plugin))), "CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1,CLAUDE_CODE_EFFORT_LEVEL=high", "beside the effort");
        var codex = ProviderCatalog.Arguments(Request("opus", provider: "codex", mode: "acceptEdits"), Plugin);
        Check(!codex.Contains("--advisor") && !string.Join(" ", codex).Contains("ADVISOR", StringComparison.Ordinal), "another agent never reads it");
        var refused = 0;
        try { ProviderCatalog.Arguments(Request("two words"), Plugin); } catch (ArgumentException) { refused++; }
        try { Request("--dangerous").Validate(); } catch (ArgumentException) { refused++; }
        Same(refused, 2, "a value that is no model is refused");

        Check(!JsonSerializer.Serialize(new RunSettings(), Wire.Json).Contains("advisor"), "written only when set");
        var older = JsonSerializer.Deserialize<RunSettings>("""{"effort":"high","permissionMode":"plan","maxTurns":null,"maxBudgetUsd":null}""", Wire.Json)!;
        Check(older.Advisor is null && older.Effort == "high", "older state decodes");
        foreach (var value in new[] { "opus", RunSettings.AdvisorOff })
            Same(JsonSerializer.Deserialize<RunSettings>(JsonSerializer.Serialize(new RunSettings(Advisor: value), Wire.Json), Wire.Json)!.Advisor, value, "kept: " + value);
        Same(JsonSerializer.Deserialize<RunSettings>("""{"advisor":""}""", Wire.Json)!.Advisor, null, "an empty value is no choice");
        Same(JsonSerializer.Deserialize<RunSettings>("""{"advisor":7}""", Wire.Json)!.Advisor, null, "a malformed value is no choice");
        var pane = new RunSession { Id = "pane", WorkspaceId = "space", Title = "Claude", Provider = "claude", Settings = new RunSettings(Advisor: "opus") };
        Same(JsonSerializer.Deserialize<RunSession>(JsonSerializer.Serialize(pane, Wire.Json), Wire.Json)!.Settings.Advisor, "opus", "the pane keeps it");
        foreach (var value in new[] { "opus", RunSettings.AdvisorOff, "claude-opus-5-5[1m]" })
            Same(ProviderCatalog.NormalizeSettings("claude", new RunSettings(Advisor: value)).Advisor, value, "a Claude pane's choice: " + value);
        Same(ProviderCatalog.NormalizeSettings("claude", new RunSettings()).Advisor, null, "none");
        Same(ProviderCatalog.NormalizeSettings("claude", new RunSettings(Advisor: "two words")).Advisor, null, "no model");
        Same(ProviderCatalog.NormalizeSettings("codex", new RunSettings(Advisor: "opus")).Advisor, null, "no other agent's");
        // Never sent to another computer's host.
        Same(RemoteWireRequest.From(Request("opus"), "host-workspace").Settings.Advisor, null, "a remote request carries none");
        return Task.CompletedTask;
    }

    internal static Task ClaudesSettingsFilesSayWhetherTheAdvisorIsOn()
    {
        static ClaudeAdvisorSettings.Entry Entry(string text) => ClaudeAdvisorSettings.Read(System.Text.Encoding.UTF8.GetBytes(text));
        var opus = ClaudeAdvisorSettings.Choice.Of("opus");
        var off = ClaudeAdvisorSettings.Choice.Off;
        Same(Entry("""{"advisorModel":"opus"}"""), new ClaudeAdvisorSettings.Entry(opus), "a model");
        Same(Entry("""{"model":"sonnet","advisorModel":"claude-opus-5-5[1m]","permissions":{"allow":[]}}""").Model, ClaudeAdvisorSettings.Choice.Of("claude-opus-5-5[1m]"), "a full id");
        foreach (var text in new[] { """{"advisorModel":"off"}""", """{"advisorModel":"OFF"}""", """{"advisorModel":""}""", """{"advisorModel":null}""", """{"advisorModel":false}""", """{"advisorModel":"two words"}""" })
            Same(Entry(text).Model, off, "off: " + text);
        foreach (var text in new[] { """{"model":"sonnet"}""", "[1,2]", "{ not json", "" }) Same(Entry(text), new ClaudeAdvisorSettings.Entry(), "nothing: " + text);
        Same(Entry("""{"advisorModel":"opus","env":{"CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"1"}}"""), new ClaudeAdvisorSettings.Entry(opus, true), "the disable variable");
        Same(Entry("""{"env":{"CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"0"}}"""), new ClaudeAdvisorSettings.Entry(Disabled: false), "set to nothing that disables");
        Same(Entry("""{"env":{"OTHER":"1"}}"""), new ClaudeAdvisorSettings.Entry(), "another variable");
        foreach (var value in new[] { "1", "true", "TRUE", " yes ", "on" }) Check(ClaudeAdvisorSettings.Truthy(value), "truthy " + value);
        foreach (var value in new[] { "0", "false", "", "no", "off" }) Check(!ClaudeAdvisorSettings.Truthy(value), "not truthy " + value);
        Check(ClaudeAdvisorSettings.Truthy(JsonSerializer.SerializeToElement(true)) && ClaudeAdvisorSettings.Truthy(JsonSerializer.SerializeToElement(1))
            && !ClaudeAdvisorSettings.Truthy((string?)null) && !ClaudeAdvisorSettings.Truthy(JsonSerializer.SerializeToElement(0)), "switches of other types");

        Same(ClaudeAdvisorSettings.Resolve([]), null, "no file");
        Same(ClaudeAdvisorSettings.Resolve([new(opus)]), "opus", "one");
        Same(ClaudeAdvisorSettings.Resolve([new(opus), new(ClaudeAdvisorSettings.Choice.Of("sonnet")), new()]), "sonnet", "a later file wins; one without the key changes nothing");
        Same(ClaudeAdvisorSettings.Resolve([new(opus), new(), new(off)]), null, "a later off");
        Same(ClaudeAdvisorSettings.Resolve([new(off), new(opus)]), "opus", "a later model");
        Same(ClaudeAdvisorSettings.Resolve([new(opus, true)]), null, "disabled");
        Same(ClaudeAdvisorSettings.Resolve([new(Disabled: true), new(opus)]), null, "disabled whatever names a model");
        Same(ClaudeAdvisorSettings.Resolve([new(opus, true), new(Disabled: false)]), "opus", "until a later file sets it back");

        string home = Verification.Temp(), workspace = Verification.Temp();
        try
        {
            var files = ClaudeAdvisorSettings.Files(workspace, home);
            SameList(files, [Path.Combine(home, "settings.json"), Path.Combine(workspace, ".claude", "settings.json"), Path.Combine(workspace, ".claude", "settings.local.json")], "the user's, then the folder's two");
            Same(ClaudeAdvisorSettings.Files(null, home).Count, 1, "without a folder the user's alone");
            var cache = new ClaudeAdvisorSettings.Cache();
            string? Model() => ClaudeAdvisorSettings.Model(workspace, home, cache);
            void Put(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
            Same(Model(), null, "no file at all");
            Put(files[0], """{"advisorModel":"opus"}""");
            Same(Model(), "opus", "the user's");
            Put(files[1], """{"advisorModel":"sonnet"}""");
            Same(Model(), "sonnet", "the project's");
            Put(files[2], """{"advisorModel":"off"}""");
            Same(Model(), null, "the local file's off");
            Same(ClaudeAdvisorSettings.Model(null, home, cache), "opus", "without a folder");
            Put(files[2], """{"advisorModel":"claude-opus-5-5","note":"changed"}""");
            Same(Model(), "claude-opus-5-5", "a changed file is read again");
            File.Delete(files[2]);
            Same(Model(), "sonnet", "a file that went says nothing");
            Put(files[1], "{\"advisorModel\":\"opus\",\"pad\":\"" + new string('x', ClaudeAdvisorSettings.MaximumBytes) + "\"}");
            Same(Model(), "opus", "a file too large says nothing");
            File.Delete(files[1]);
            Directory.CreateDirectory(files[1]);
            Same(Model(), "opus", "a folder in a file's place says nothing");
            Same(File.ReadAllText(files[0]), """{"advisorModel":"opus"}""", "nothing was written");
        }
        finally { foreach (var folder in new[] { home, workspace }) try { Directory.Delete(folder, true); } catch (IOException) { } }

        var read = 0;
        Func<string?> Settings(string? value) => () => { read++; return value; };
        Same(ClaudeAdvisorSettings.Configured("opus", false, Settings("sonnet")), "opus", "the pane's own model");
        Same(ClaudeAdvisorSettings.Configured(RunSettings.AdvisorOff, false, Settings("sonnet")), null, "the pane turned it off");
        Same(read, 0, "Claude's settings not even read");
        Same(ClaudeAdvisorSettings.Configured(null, false, Settings("sonnet")), "sonnet", "a pane that follows them");
        Same(ClaudeAdvisorSettings.Configured(null, false, Settings(null)), null, "nothing named");
        Same(read, 2, "read for a pane that follows them");
        Same(ClaudeAdvisorSettings.Configured("opus", true, Settings("sonnet")), null, "the run's environment turns it off");
        Same(ClaudeAdvisorSettings.Configured(null, true, Settings("sonnet")), null, "whatever is named");
        Same(ClaudeAdvisorSettings.Configured("two words", false, Settings("sonnet")), null, "a pane value that is no model");
        return Task.CompletedTask;
    }
}
