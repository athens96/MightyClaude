using MightyClaude.Core;

/// <summary>
/// Agent marks on the Windows sidebar: an agent's row names its provider after the
/// Claude, Codex or Gemini mark; other rows carry none. Mirrors
/// native/macos/Tests/MightyCoreTests/WorkDashboardTests.swift
/// (onlyAnAgentRowNamesItsProviderSoOnlyItCarriesTheMark) and the macOS ProviderMark data.
/// </summary>
internal static class ProviderMarkVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task OnlyAnAgentRowNamesItsProviderSoOnlyItCarriesTheMark()
    {
        foreach (var provider in Wire.Providers)
        {
            var agent = new RunSession { Kind = "claude", Provider = provider, Status = "running" };
            Check(ProviderMark.SidebarProvider(agent) == provider, provider + " row must carry its own mark");
            Check(ProviderMark.MarkedProvider(agent.Provider) == provider, provider + " must be a marked provider");
        }
        foreach (var kind in new[] { "shell", "browser", FilePaneKind.Kind })
        {
            var pane = new RunSession { Kind = kind, Provider = "codex", Status = "running" };
            Check(ProviderMark.SidebarProvider(pane) is null, kind + " row must carry no mark");
        }
        Check(ProviderMark.SidebarProvider(new RunSession { Kind = "claude", Provider = "outline" }) is null, "an unknown provider must not borrow Claude's mark");
        Check(ProviderMark.MarkedProvider(null) is null, "no provider, no mark");
        return Task.CompletedTask;
    }

    internal static Task MarksAreTheMacOutlinesInTheirBrandColours()
    {
        Check(ProviderMark.Colors("claude").SequenceEqual(new uint[] { 0xD97757 }), "Claude orange");
        Check(ProviderMark.Colors("codex").SequenceEqual(new uint[] { 0x10A37F }), "Codex green");
        Check(ProviderMark.Colors("gemini").SequenceEqual(new uint[] { 0x4285F4, 0x9B72CB, 0xD96570 }), "Gemini gradient");
        Check(ProviderMark.Label("claude") == "Claude" && ProviderMark.Label("codex") == "Codex" && ProviderMark.Label("gemini") == "Gemini", "labels match macOS ProviderOptions.label");
        // The outlines are the macOS data, byte for byte.
        var swift = MacSource();
        if (swift is not null)
            foreach (var provider in Wire.Providers)
                Check(swift.Contains("static let " + provider + " = \"" + ProviderMark.Outline(provider) + "\"", StringComparison.Ordinal), provider + " outline must match macOS ProviderMark.swift");
        foreach (var provider in Wire.Providers)
        {
            var commands = ProviderMark.Commands(ProviderMark.Outline(provider));
            Check(commands.Count > 0 && commands[0].Kind == 'M', provider + " outline must start with a move");
            Check(commands.All(c => c.Kind switch { 'M' or 'L' => c.Values.Length == 2, 'C' => c.Values.Length == 6, 'Z' => c.Values.Length == 0, _ => false }), provider + " outline must use only M, L, C and Z with their arity");
            var figures = ProviderMark.Figures(provider);
            Check(figures.Count == commands.Count(c => c.Kind == 'M'), provider + " has one figure per move");
            var points = figures.SelectMany(f => f.Segments.Select(s => (s.X, s.Y)).Prepend((f.X, f.Y))).ToList();
            Check(points.All(p => p.Item1 >= -1 && p.Item1 <= ProviderMark.Box + 1 && p.Item2 >= -1 && p.Item2 <= ProviderMark.Box + 1), provider + " stays in the 24 box");
        }
        Check(ProviderMark.Figures("gemini").Count == 1 && ProviderMark.Figures("gemini")[0].Closed, "Gemini is one closed star");
        Check(ProviderMark.Figures("gemini")[0].Segments.All(s => s is GlyphCurve), "Gemini is all curves");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors macOS ProviderMarkTests.theMarkGoesBeforeTheTrailingProviderName and
    /// onlyKnownAgentsGetAMarkBesideTheirName (a65a65b), plus the diagram's request block
    /// header: only it names the pane's agent, so only it carries the mark.
    /// </summary>
    internal static Task RightSideCardsMarkTheAgentNameTheirTitleEndsWith()
    {
        foreach (var provider in Wire.Providers) Check(ProviderMark.MarkedProvider(provider) == provider, provider + " is a known agent");
        foreach (var other in new[] { "", "shell", "terminal", "browser", "unknown", "Claude" }) Check(ProviderMark.MarkedProvider(other) is null, "'" + other + "' must carry no mark");

        var request = ProviderMark.SplitTrailingLabel("요청 3 · Claude", "claude");
        Check(request is { Head: "요청 3 · ", Label: "Claude" }, "the mark goes between '요청 3 · ' and 'Claude'");
        Check(ProviderMark.SplitTrailingLabel("Claude", "claude") is { Head: "", Label: "Claude" }, "a bare name splits with an empty head");
        Check(ProviderMark.SplitTrailingLabel("Request 2 · Gemini", "gemini") is { Head: "Request 2 · ", Label: "Gemini" }, "English request titles split the same way");
        Check(ProviderMark.SplitTrailingLabel("Codex", "claude") is null, "another provider's name is left alone");
        Check(ProviderMark.SplitTrailingLabel("요청 3 · Claude", "unknown") is null, "a provider with no mark is left alone");
        Check(ProviderMark.SplitTrailingLabel("요청 3 · Claude Code", "claude") is null, "a title that does not end with the short name is left alone");

        // macOS: the mark's side is the line's font size rounded, kept to 11–15.
        Check(ProviderMark.InlineSize(12) == 12 && ProviderMark.InlineSize(16) == 15 && ProviderMark.InlineSize(9) == 11 && ProviderMark.InlineSize(12.5) == 13, "mark side follows the line's font, 11–15");

        // The diagram: a request block's title ends with the short name, so its header carries
        // the pane's mark; sub-agent, result and draft headers carry none.
        var saved = Locale.LanguagePreference;
        Locale.LanguagePreference = "ko";
        try
        {
            foreach (var provider in Wire.Providers)
            {
                var run = new MightyGraphRun
                {
                    Id = "run-" + provider, Input = "요청", Status = "completed", Provider = provider, FinalOutput = "끝",
                    Agents = [new MightyGraphAgent { Id = "agent-1", Title = "explore", Input = "찾기", Status = "completed" }],
                };
                var layout = MightyGraphViewModel.CanvasLayout([run], draft: "다음", running: false, new HashSet<string>());
                var blocks = MightyGraphBlockModel.Blocks(layout, [run], "다음", ProviderMark.Label(provider), animationsEnabled: true);
                Check(blocks.Any(b => b.Kind == "agent") && blocks.Any(b => b.Kind == "result"), "the fixture draws sub-agent and result blocks");
                var requestBlock = blocks.Single(b => b.Kind == "request");
                Check(requestBlock.Title.EndsWith(" · " + ProviderMark.Label(provider), StringComparison.Ordinal), "request title ends with the short name: " + requestBlock.Title);
                Check(MightyGraphBlockModel.TitleProvider(requestBlock, provider) == provider, provider + " request header must carry its mark");
                foreach (var other in blocks.Where(b => b.Kind != "request"))
                    Check(MightyGraphBlockModel.TitleProvider(other, provider) is null, other.Kind + " header must carry no mark");
                Check(MightyGraphBlockModel.TitleProvider(requestBlock, "unknown") is null, "an unknown pane provider must not borrow a mark");
            }
        }
        finally { Locale.LanguagePreference = saved; }
        return Task.CompletedTask;
    }

    private static string? MacSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "native", "macos", "Sources", "MightyCore", "ProviderMark.swift");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
