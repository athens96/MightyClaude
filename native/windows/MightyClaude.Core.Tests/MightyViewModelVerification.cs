using MightyClaude.Core;

internal static class MightyViewModelVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // ── 1: mighty switch shows only on claude and codex panes ─────────────────

    internal static Task ShowsOnlyOnClaudeAndCodexPanes()
    {
        // Claude + claude provider → shows
        var claude = new RunSession { Kind = "claude", Provider = "claude" };
        Check(MightyGraphViewModel.ShowsModeSwitch(claude), "claude pane must show the switch");

        // Claude + codex provider → shows
        var codex = new RunSession { Kind = "claude", Provider = "codex" };
        Check(MightyGraphViewModel.ShowsModeSwitch(codex), "codex pane must show the switch");

        // Gemini provider → does not show
        var gemini = new RunSession { Kind = "claude", Provider = "gemini" };
        Check(!MightyGraphViewModel.ShowsModeSwitch(gemini), "gemini pane must not show the switch");

        // Shell → does not show
        var shell = new RunSession { Kind = "shell", Provider = "claude" };
        Check(!MightyGraphViewModel.ShowsModeSwitch(shell), "shell pane must not show the switch");

        // Browser → does not show
        var browser = new RunSession { Kind = "browser", Provider = "claude" };
        Check(!MightyGraphViewModel.ShowsModeSwitch(browser), "browser pane must not show the switch");

        // Verify locale keys exist with the right Korean text
        Locale.LanguagePreference = "ko";
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyDefault) == "기본", "graph.view.default must be 기본");
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyMighty) == "마이티", "graph.view.mighty must be 마이티");

        return Task.CompletedTask;
    }

    // ── 2: mighty switch keeps run and draft ──────────────────────────────────

    internal static Task SwitchKeepsRunAndDraft()
    {
        var session = new RunSession
        {
            Kind = "claude", Provider = "claude", Status = "running",
            Draft = "my composer draft",
            AgentViewMode = "default",
            GraphRuns = [new MightyGraphRun { Id = "run-1", Input = "Hello", Status = "running" }],
        };

        // Switch to mighty
        var mighty = MightyGraphViewModel.ApplyViewMode(session, "mighty");
        Check(mighty.AgentViewMode == "mighty", "switching to mighty must save mighty");
        Check(mighty.Status == "running", "switching view mode must not change run status");
        Check(mighty.Draft == "my composer draft", "switching view mode must not clear draft");
        Check(mighty.GraphRuns is { Count: 1 } && mighty.GraphRuns[0].Id == "run-1",
            "switching view mode must not change GraphRuns");

        // Switch back to default
        var back = MightyGraphViewModel.ApplyViewMode(mighty, "default");
        Check(back.AgentViewMode == "default", "switching to default must save default");
        Check(back.Draft == "my composer draft", "switching back must not clear draft");

        // Unknown mode is a no-op
        var unchanged = MightyGraphViewModel.ApplyViewMode(session, "grid");
        Check(unchanged.AgentViewMode == "default", "unknown mode must leave AgentViewMode unchanged");

        return Task.CompletedTask;
    }

    // ── 3: mighty canvas blocks follow the layout ─────────────────────────────

    internal static Task CanvasBlocksFollowTheLayout()
    {
        var run = new MightyGraphRun
        {
            Id = "run-1", Input = "Build", Status = "running", Provider = "claude",
            Agents =
            [
                new() { Id = "agent-1", Title = "Reader", Input = "read", Status = "running" },
                new() { Id = "agent-2", ParentID = "agent-1", Title = "Writer", Input = "write", Status = "running" },
            ],
        };
        var graphRuns = new List<MightyGraphRun> { run };
        var layout = MightyGraphViewModel.CanvasLayout(graphRuns, draft: "", running: true, new HashSet<string>());

        // At minimum: request block + 2 agent blocks
        Check(layout.Nodes.Count >= 3, "running graph must produce at least request + 2 agent blocks");

        // Request block must be present
        var requestId = MightyGraphLayout.NodeID(run, "request");
        Check(layout.Nodes.Any(n => n.Id == requestId && n.Kind == "request"),
            "canvas must include a request block");

        // Agent blocks
        Check(layout.Nodes.Any(n => n.Kind == "agent"), "canvas must include agent blocks");

        // Draft block when there IS a draft
        var withDraft = MightyGraphViewModel.CanvasLayout(graphRuns, draft: "type here", running: false, new HashSet<string>());
        Check(withDraft.Nodes.Any(n => n.Kind == "draft"), "draft block must appear when draft is non-empty");

        // Edges exist
        Check(layout.Edges.Count > 0, "canvas must have edges connecting blocks");

        return Task.CompletedTask;
    }

    // ── 4: mighty capsule and tooltip match macOS ─────────────────────────────

    internal static Task CapsuleAndTooltipMatchMacOS()
    {
        // Single model record
        var records = new List<GraphResponseRecord>
        {
            new("resp-1", "claude-opus-5", new GraphTokenUsage(1000, 200), ["tool-1"]),
        };
        var usage = new GraphTokenUsage(1000, 200);
        var capsule = MightyGraphViewModel.BlockCapsule(usage, records, null);
        Check(capsule is not null, "capsule must not be null when records and usage are present");
        Check(capsule!.Contains("claude-opus-5"), "capsule must contain the model name");

        // Help text (tooltip) lists models
        var help = MightyGraphViewModel.BlockCapsuleHelp(records);
        Check(help.Contains("claude-opus-5"), "capsule help must list the model");

        // No records → nodeModelLabel fallback
        var noRecords = MightyGraphViewModel.BlockCapsule(null, new List<GraphResponseRecord>(), "claude-opus-5");
        Check(noRecords == "claude-opus-5", "with no records capsule must fall back to nodeModelLabel");

        // Locale keys are the macOS strings
        Locale.LanguagePreference = "ko";
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyZoomOut) == "축소", "graph.zoom.out must be 축소");
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset) == "실제 크기", "graph.zoom.reset must be 실제 크기");
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyZoomIn) == "확대", "graph.zoom.in must be 확대");
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesTitle) == "결과에 나온 파일",
            "graph.resultFiles.title must be 결과에 나온 파일");
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose) == "파일 목록 닫기",
            "graph.resultFiles.closeButton must be 파일 목록 닫기");
        Check(Locale.Get(MightyGraphViewModel.LocaleKeyBlockScrolling) == "블록 스크롤",
            "graph.block.scrolling must be 블록 스크롤");

        return Task.CompletedTask;
    }

    // ── 5: mighty zoom steps match macOS ─────────────────────────────────────

    internal static Task ZoomStepsMatchMacOS()
    {
        var levels = MightyGraphViewModel.ZoomLevels();

        // Must span 50% to 150%
        Check(levels[0] == 0.5, "zoom must start at 0.5 (50%)");
        Check(levels[^1] == 1.5, "zoom must end at 1.5 (150%)");

        // 10% steps → 11 levels (0.5, 0.6, …, 1.5)
        Check(levels.Length == 11, "zoom must have 11 levels from 50% to 150% in 10% steps");

        // Clamping at ends
        Check(MightyGraphViewModel.ZoomInDisabled(1.5), "zoom-in must be disabled at max");
        Check(!MightyGraphViewModel.ZoomInDisabled(1.4), "zoom-in must be enabled below max");
        Check(MightyGraphViewModel.ZoomOutDisabled(0.5), "zoom-out must be disabled at min");
        Check(!MightyGraphViewModel.ZoomOutDisabled(0.6), "zoom-out must be enabled above min");

        // Step up/down
        Check(MightyGraphViewModel.ZoomIn(1.0) == 1.1, "zoom-in from 100% must give 110%");
        Check(MightyGraphViewModel.ZoomOut(1.0) == 0.9, "zoom-out from 100% must give 90%");
        Check(MightyGraphViewModel.ZoomIn(1.5) == 1.5, "zoom-in at max must stay at max");
        Check(MightyGraphViewModel.ZoomOut(0.5) == 0.5, "zoom-out at min must stay at min");

        // Default
        Check(MightyGraphViewModel.ZoomDefault == 1.0, "default zoom must be 1.0 (100%)");

        // Label format
        Check(MightyGraphViewModel.ZoomLabel(1.0) == "100%", "100% zoom must format as '100%'");
        Check(MightyGraphViewModel.ZoomLabel(0.5) == "50%", "50% zoom must format as '50%'");
        Check(MightyGraphViewModel.ZoomLabel(1.5) == "150%", "150% zoom must format as '150%'");

        return Task.CompletedTask;
    }

    // ── 6: mighty selection routes the wheel ─────────────────────────────────

    internal static Task SelectionRoutesTheWheel()
    {
        // Clicking a block selects it
        var selected = MightyGraphViewModel.ApplySelection(null, "node-1");
        Check(selected == "node-1", "clicking a block must select it");

        // Clicking another block replaces selection
        var replaced = MightyGraphViewModel.ApplySelection("node-1", "node-2");
        Check(replaced == "node-2", "clicking another block must select it");

        // Clicking empty background (null) clears selection
        var cleared = MightyGraphViewModel.ApplySelection("node-1", null);
        Check(cleared is null, "clicking empty background must clear selection");

        // When a block is selected, wheel scrolls the block body
        Check(MightyGraphViewModel.WheelScrollsBlock("node-1"), "selected block must capture the wheel");
        Check(!MightyGraphViewModel.WheelScrollsBlock(null), "no selection must let wheel pan canvas");

        return Task.CompletedTask;
    }

    // ── 7: mighty result files panel rules match macOS ────────────────────────

    internal static Task ResultFilesPanelRulesMatchMacOS()
    {
        // Auto-opens for a new result with files
        var opened = MightyGraphViewModel.NextResultFilesRunID(
            currentRunID: null, manuallyClosed: false,
            latestCompletedRunID: "run-2", latestHasFiles: true);
        Check(opened == "run-2", "must auto-open for a new result with files");

        // Does not auto-open when there are no files
        var notOpened = MightyGraphViewModel.NextResultFilesRunID(
            currentRunID: null, manuallyClosed: false,
            latestCompletedRunID: "run-2", latestHasFiles: false);
        Check(notOpened is null, "must not auto-open when latest result has no files");

        // Manual close sticks until the next new result
        var stuckClosed = MightyGraphViewModel.NextResultFilesRunID(
            currentRunID: "run-1", manuallyClosed: true,
            latestCompletedRunID: "run-1", latestHasFiles: true);
        Check(stuckClosed is null, "manual close must stick for the same run");

        // A new result with files re-opens even after a manual close
        var reopened = MightyGraphViewModel.NextResultFilesRunID(
            currentRunID: "run-1", manuallyClosed: true,
            latestCompletedRunID: "run-2", latestHasFiles: true);
        Check(reopened == "run-2", "new result with files must re-open even after manual close");

        // Only one panel at a time: when already open for run-2, stay open
        var kept = MightyGraphViewModel.NextResultFilesRunID(
            currentRunID: "run-2", manuallyClosed: false,
            latestCompletedRunID: "run-2", latestHasFiles: true);
        Check(kept == "run-2", "panel for current run must stay open");

        // No completed run → no panel
        var none = MightyGraphViewModel.NextResultFilesRunID(
            currentRunID: null, manuallyClosed: false,
            latestCompletedRunID: null, latestHasFiles: false);
        Check(none is null, "no completed run means no panel");

        return Task.CompletedTask;
    }

    // ── 8: mighty indicators respect animations off ───────────────────────────

    internal static Task IndicatorsRespectAnimationsOff()
    {
        // Running with animations on → animating
        Check(MightyGraphViewModel.BlockIndicator("running", animationsEnabled: true) == "animating",
            "running + animations on must be animating");

        // Running with animations off → static (highlighted border, no motion)
        Check(MightyGraphViewModel.BlockIndicator("running", animationsEnabled: false) == "static",
            "running + animations off must be static");

        // Waiting → waiting (static pause mark, unaffected by animations flag)
        Check(MightyGraphViewModel.BlockIndicator("waiting", animationsEnabled: true) == "waiting",
            "waiting + animations on must be waiting");
        Check(MightyGraphViewModel.BlockIndicator("waiting", animationsEnabled: false) == "waiting",
            "waiting + animations off must still be waiting");

        // Finished blocks → none
        foreach (var status in new[] { "completed", "error", "stopped", "idle" })
        {
            Check(MightyGraphViewModel.BlockIndicator(status, animationsEnabled: true) == "none",
                status + " + animations on must be none");
            Check(MightyGraphViewModel.BlockIndicator(status, animationsEnabled: false) == "none",
                status + " + animations off must be none");
        }

        return Task.CompletedTask;
    }
}
