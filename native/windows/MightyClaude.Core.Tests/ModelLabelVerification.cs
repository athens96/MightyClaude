using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/model-labels.json is the label table the Mac
// (ModelLabelTests) and the phone (model-label.test.ts) are held to; these checks
// hold the Windows port to the same bytes, then repeat the Mac's catalogue,
// pane-selection, status-line and graph cases. Every case runs with the Korean
// locale, as the committed expectations are the ko copy.
internal static class ModelLabelVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T actual, T expected, string what) => Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected '{expected}', got '{actual}'");
    private static void Same(IEnumerable<string> actual, IEnumerable<string> expected, string what) => Check(actual.SequenceEqual(expected), $"{what}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.ModelLabels.json";
        using var stream = typeof(ModelLabelVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static JsonElement Rows(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// The catalogue the installed Claude Code reported on 2026-10-03 (macOS ModelLabelTests.claudeRows).
    private static ModelCatalog ClaudeRows() => ProviderCatalog.ClaudeCatalog(Rows("""
        [
          {"value": "default", "resolvedModel": "claude-opus-5-5", "displayName": "Default (recommended)", "description": "Opus 5.5 · Best for everyday, complex tasks"},
          {"value": "opus", "resolvedModel": "claude-opus-5-5", "displayName": "Opus"},
          {"value": "claude-fable-5-1[1m]", "resolvedModel": "claude-fable-5-1", "displayName": "Fable"},
          {"value": "sonnet", "resolvedModel": "claude-sonnet-5-5", "displayName": "Sonnet"},
          {"value": "haiku", "resolvedModel": "claude-haiku-4-5-20251001", "displayName": "Haiku"}
        ]
        """));

    private static SessionUsage Usage(string? model, string? selected) => new() { Provider = "claude", Source = "claude.stream-json", Model = model, SelectedModel = selected };

    internal static Task EveryFixtureCaseLabelsAsCommitted()
    {
        var cases = Fixture.Value.EnumerateArray().ToList();
        Check(cases.Count >= 40, "the shared model-label table must carry at least 40 cases; got " + cases.Count);
        foreach (var item in cases)
        {
            var model = item.GetProperty("model").GetString()!;
            string? Optional(string key) => item.TryGetProperty(key, out var value) ? value.GetString() : null;
            Equal(ModelLabel.Text(model, Optional("resolved"), Optional("fallback")), item.GetProperty("expected").GetString(), "label of '" + model + "'");
        }
        return Task.CompletedTask;
    }

    internal static Task CliCatalogueRowsCarryTheirVersion()
    {
        var catalog = ClaudeRows();
        Equal(catalog.Source, "cli", "catalogue source");
        Same(catalog.Models.Select(o => ModelLabel.Option(o)), ["Claude 설정 따름 · Opus 5.5", "Opus 5.5", "Fable 5.1 (1M)", "Sonnet 5.5", "Haiku 4.5"], "Claude picker rows");
        // The composer button reads the same row the picker drew.
        foreach (var option in catalog.Models) Equal(ModelLabel.InCatalog(option.Value, catalog), ModelLabel.Option(option), "button label of " + option.Value);
        // Only the label changes: the values sent to the CLI stay the aliases.
        Same(catalog.Models.Select(o => o.Value), ["default", "opus", "claude-fable-5-1[1m]", "sonnet", "haiku"], "Claude values");

        var codex = ProviderCatalog.Fallback("codex").Models.Take(1).ToList();
        ProviderCatalog.AddCodexModels(codex, Rows("""
            [
              {"model": "gpt-6.1-sol", "displayName": "GPT-6.1-Sol", "isDefault": true},
              {"model": "gpt-5.2-codex", "displayName": "GPT-5.2-Codex"}
            ]
            """));
        Same(codex.Select(o => ModelLabel.Option(o)), ["Codex 설정 따름 · GPT-6.1 Sol", "GPT-6.1 Sol", "GPT-5.2 Codex"], "Codex picker rows");
        Same(codex.Select(o => o.Value), ["default", "gpt-6.1-sol", "gpt-5.2-codex"], "Codex values");
        return Task.CompletedTask;
    }

    internal static Task FallbackCatalogueNeverInventsAVersion()
    {
        var fallback = ProviderCatalog.Fallback("claude");
        Equal(ModelLabel.InCatalog("opus", fallback), "Opus", "fallback opus");
        Equal(ModelLabel.InCatalog("default", fallback), "Claude 설정 따름", "fallback default");
        Equal(ModelLabel.InCatalog("opusplan", fallback), "opusplan", "fallback opusplan");
        // The pane's own reported model may give a family alias its version …
        Equal(ModelLabel.InCatalog("opus", fallback, "claude-opus-5-5"), "Opus 5.5", "opus with its reported id");
        // … but never another family's, and never default/best theirs.
        Equal(ModelLabel.InCatalog("sonnet", fallback, "claude-opus-5-5"), "Sonnet", "sonnet with an Opus report");
        Equal(ModelLabel.InCatalog("default", fallback, "claude-opus-5-5"), "Claude 설정 따름", "default with a report");
        Equal(ModelLabel.InCatalog("best", fallback, "claude-opus-5-5"), "best", "best with a report");
        Equal(ModelLabel.InCatalog("gemini-3-pro-preview", ProviderCatalog.Fallback("gemini")), "Gemini 3 Pro", "Gemini fallback row");
        Equal(ModelLabel.InCatalog("gpt-6-astra", null), "GPT-6 Astra", "an id with no catalogue");
        Equal(ModelLabel.InCatalog("claude-sonnet-5-5", fallback), "Sonnet 5.5", "a stored full id");
        return Task.CompletedTask;
    }

    internal static Task AReportedModelLabelsOnlyTheSelectionItWasReportedFor()
    {
        var fallback = ProviderCatalog.Fallback("claude");
        var session = new RunSession { WorkspaceId = "w", Title = "t", Model = "opus", SessionUsage = Usage("claude-opus-5-5", "opus") };
        var rows = ModelLabel.PickerOptions(session, fallback);
        // Values stay the catalogue's; only the selected row borrows the reported version.
        Same(rows.Select(r => r.Value), fallback.Models.Select(m => m.Value), "picker values");
        Equal(rows.First(r => r.Value == "opus").DisplayName, "Opus 5.5", "selected opus row");
        Equal(rows.First(r => r.Value == "sonnet").DisplayName, "Sonnet", "sonnet row");
        Equal(rows.First(r => r.Value == "best").DisplayName, "best", "best row");
        Equal(ModelLabel.Selection(session, fallback), "Opus 5.5", "selection");
        // Reported for another selection: stale, so no version for it.
        var switched = session with { Model = "sonnet" };
        Equal(ModelLabel.ReportedModel(switched), null, "reported model after a switch");
        Equal(ModelLabel.Selection(switched, fallback), "Sonnet", "selection after a switch");
        var stale = new RunSession { WorkspaceId = "w", Title = "t", Model = "opus", SessionUsage = Usage("claude-opus-4-5", "default") };
        Equal(ModelLabel.Selection(stale, fallback), "Opus", "a report for an earlier selection");
        // Usage saved before the selection was recorded gives no hint.
        var legacy = new RunSession { WorkspaceId = "w", Title = "t", Model = "opus", SessionUsage = Usage("claude-opus-5-5", null) };
        Equal(ModelLabel.ReportedModel(legacy), null, "legacy reported model");
        Equal(ModelLabel.Selection(legacy, fallback), "Opus", "legacy selection");

        // Recording usage remembers the selection it was reported for.
        var pane = new RunSession { Id = "s1", WorkspaceId = "w", Title = "t", Model = "opus" };
        var recorded = new AppSnapshot { Sessions = [pane] }.Apply(new RunEvent("s1", "usage", Usage: new SessionUsage { Provider = "claude", Source = "claude.stream-json", Model = "claude-opus-5-5" })).Sessions[0];
        Equal(recorded.SessionUsage?.SelectedModel, "opus", "the recorded selection");
        Equal(ModelLabel.ReportedModel(recorded), "claude-opus-5-5", "the recorded report");
        // The selection survives saving; a bad one and an older save carry none.
        var saved = JsonSerializer.Deserialize<SessionUsage>(JsonSerializer.Serialize(Usage("claude-opus-5-5", "opus"), Wire.Json), Wire.Json);
        Equal(saved?.SelectedModel, "opus", "a saved selection");
        Equal(SessionUsageSupport.Normalize(Usage("x", "bad model"))?.SelectedModel, null, "an invalid selection");
        var old = JsonSerializer.Deserialize<SessionUsage>("""{"provider":"claude","source":"s","tokenScope":"run","updatedAt":"2026-10-03T00:00:00Z"}""", Wire.Json);
        Equal(old?.SelectedModel, null, "an older save");
        return Task.CompletedTask;
    }

    internal static Task PicksAndDisplaysDifferOnlyByPickerMarks()
    {
        var catalog = ClaudeRows();
        var saved = new RunSession { WorkspaceId = "w", Title = "t", Model = "claude-opus-4-1" };
        Equal(ModelLabel.PickerOptions(saved, catalog).Last().DisplayName, "Opus 4.1 · 저장된 모델", "a saved model the catalogue lacks");
        Equal(ModelLabel.PickerOptions(saved, catalog).Last().Value, "claude-opus-4-1", "its value");
        Equal(ModelLabel.Selection(saved, catalog), "Opus 4.1", "its display");
        var bare = new RunSession { WorkspaceId = "w", Title = "t", Model = "default" };
        var empty = new ModelCatalog("cli", [], "");
        Equal(ModelLabel.Selection(bare, empty), "CLI 기본값", "default with no catalogue row");
        Same(ModelLabel.PickerOptions(bare, empty).Select(o => o.DisplayName), ["CLI 기본값"], "picker with no catalogue rows");
        return Task.CompletedTask;
    }

    internal static Task StatusLineNamesTheCurrentSelection()
    {
        var catalog = ClaudeRows();
        var current = new RunSession { WorkspaceId = "w", Title = "t", Model = "opus", SessionUsage = Usage("claude-opus-5-5", "opus") };
        Equal(ModelLabel.StatusLine(current, catalog), ("claude-opus-5-5", "Opus 5.5"), "status line for the reported selection");
        // Switched to Haiku after the run: the reported Opus gives way.
        Equal(ModelLabel.StatusLine(current with { Model = "haiku" }, catalog), ("claude-haiku-4-5-20251001", "Haiku 4.5"), "status line after a switch");
        // Older usage without a recorded selection keeps the reported model.
        var legacy = new RunSession { WorkspaceId = "w", Title = "t", Model = "sonnet", SessionUsage = Usage("claude-sonnet-4-5", null) };
        Equal(ModelLabel.StatusLine(legacy, catalog), ("claude-sonnet-4-5", "Sonnet 4.5"), "status line for legacy usage");
        Equal(ModelLabel.StatusLine(new RunSession { WorkspaceId = "w", Title = "t", Model = "default" }, ProviderCatalog.Fallback("claude")), ("default", "Claude 설정 따름"), "status line for the fallback default");
        return Task.CompletedTask;
    }

    internal static Task GraphCapsulesAndResumeRowsReadVersions()
    {
        var catalog = ClaudeRows().Models;
        var records = new List<GraphResponseRecord> { new("r1", "claude-opus-5-5", new GraphTokenUsage(1000, 50), ["a1"]) };
        var usage = new GraphTokenUsage(1500, 80);
        Equal(ModelUsageFormat.BlockCapsule(usage, records, null, catalog, versioned: true), "1.6K · Opus 5.5", "versioned capsule");
        Equal(ModelUsageFormat.ShortName("claude-sonnet-4-5", versioned: true), "Sonnet 4.5", "versioned short name");
        // The shared graph vectors keep the catalogue-only name.
        Equal(ModelUsageFormat.ShortName("claude-sonnet-4-5"), "claude-sonnet-4-5", "catalogue-only short name");
        var marker = " " + Locale.Get("graph.nodeModel.configuredSuffix");
        // Today's catalogue does not say which version an older run used.
        Equal(ModelUsageFormat.VersionedNodeLabel("opus" + marker), "Opus" + marker, "configured alias node label");
        Equal(ModelUsageFormat.BlockCapsule(usage, [], "opus" + marker, catalog, versioned: true), "Opus" + marker, "capsule before a response");
        Equal(ModelUsageFormat.VersionedNodeLabel("claude-sonnet-4-5" + marker), "Sonnet 4.5" + marker, "configured id node label");
        Equal(ModelUsageFormat.VersionedNodeLabel("claude-fable-5-1"), "Fable 5.1", "reported node label");
        Equal(ModelUsageFormat.VersionedNodeLabel(null), null, "no node label");
        // The canvas blocks WinUI draws use the versioned capsule.
        var run = new MightyGraphRun { Id = "run-1", Input = "go", Status = "completed", Provider = "claude", Usage = usage, ResponseRecords = records, FinalOutput = "done" };
        var layout = MightyGraphLayout.Make([run], "", false, new HashSet<string>());
        var request = MightyGraphBlockModel.Blocks(layout, [run], "", "Claude", true, catalog).Single(b => b.Kind == "request");
        Equal(request.Capsule, "1.6K · Opus 5.5", "request block capsule on the canvas");
        // The resume picker's second line names the model the same way (macOS ResumeSessionSheet).
        var item = new ResumableSession("claude", "5d9e2c1a-1111-4222-8333-444455556666", "t", DateTimeOffset.UtcNow.AddDays(-2), 1, "claude-opus-4-5", "/r");
        Check(ResumableSessions.Details(item, DateTimeOffset.UtcNow).Contains(" · Opus 4.5", StringComparison.Ordinal), "the resume row names Opus 4.5: " + ResumableSessions.Details(item, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}
