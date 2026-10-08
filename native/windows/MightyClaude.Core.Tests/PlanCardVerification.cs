using MightyClaude.Core;

/// <summary>What the plan card, its place in the Mighty diagram and the pane header read (macOS PlanCardTests).</summary>
internal static class PlanCardVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static ToolPermissionRequest Plan(string id = "plan-1", string state = "pending", bool answerable = true) =>
        new(id, "pane", "toolu_" + id, ClaudePlanMode.ToolName, "{\"plan\":\"# 계획\\n\\n1. 테스트\"}", "plan", State: state, CanAllow: false, CanAnswerPlan: answerable, ReceivedAt: "2027-01-15T08:05:00.000Z");
    private static readonly ToolPermissionRequest Bash = new("perm-1", "pane", "toolu_b", "Bash", "{\"command\":\"ls\"}", "ls");
    private static MightyGraphRun Run(string id, string status = "running") => new() { Id = id, Input = "request " + id, Status = status };

    internal static Task Helpers()
    {
        Check(PlanCardSupport.PendingPlan(null) is null && PlanCardSupport.PendingPlan([Bash]) is null, "no plan, no card");
        Check(PlanCardSupport.PendingPlan([Plan(state: "allowed")]) is null && PlanCardSupport.PendingPlan([Plan(answerable: false)]) is null, "only an answerable pending plan");
        Check(PlanCardSupport.PendingPlan([Bash, Plan("plan-2"), Plan("plan-3")])?.Id == "plan-2", "the first plan asked");

        IReadOnlyList<MightyGraphRun> runs = [Run("g1", "completed"), Run("g2")];
        Check(PlanCardSupport.DiagramPlanRunID(Plan(), true, runs) == "g2", "the plan goes under the running request");
        Check(PlanCardSupport.DiagramPlanRunID(Plan(), false, runs) is null, "docked when the diagram is not shown");
        Check(PlanCardSupport.DiagramPlanRunID(Plan(), true, [Run("g1", "completed")]) is null, "docked when the request already ended");
        Check(PlanCardSupport.DiagramPlanRunID(Plan(), true, []) is null && PlanCardSupport.DiagramPlanRunID(Bash, true, runs) is null, "only a plan, only with a request");

        Check(!PlanCardSupport.CanSendRevise(null) && !PlanCardSupport.CanSendRevise(" \n\t ") && PlanCardSupport.CanSendRevise(" 테스트 먼저 "), "revise needs text");
        Check(!PlanCardSupport.CanSendRevise(new string('a', ClaudePlanMode.MaximumFeedbackBytes + 1)), "revise stays within the bound");

        Check(PlanCardSupport.OutcomeTitle(PlanOutcome.ApprovedAuto) == Locale.Get("plan.outcome.approvedAuto")
            && PlanCardSupport.OutcomeTitle(PlanOutcome.ApprovedConfirm) == Locale.Get("plan.outcome.approvedConfirm")
            && PlanCardSupport.OutcomeTitle(PlanOutcome.Revised) == Locale.Get("plan.outcome.revised")
            && PlanCardSupport.OutcomeTitle(PlanOutcome.Cancelled) == Locale.Get("plan.outcome.cancelled"), "each outcome reads its own key");
        Check(PlanOutcome.All.Select(PlanCardSupport.OutcomeTitle).Distinct().Count() == 4, "four distinct outcome words");
        Check(PlanCardSupport.TimeText("2027-01-15T08:05:00.000Z", TimeZoneInfo.Utc) == "08:05" && PlanCardSupport.TimeText("not a time") == "not a time", "times read as HH:mm");
        Check(PlanCardSupport.ReceivedText("2027-01-15T08:05:00.000Z", TimeZoneInfo.Utc) == Locale.Get("plan.card.received", new Dictionary<string, string> { ["time"] = "08:05" }), "received line");
        Check(PlanCardSupport.Headline("\n\n## 로그인 고치기\n\n1. 원인") == "로그인 고치기" && PlanCardSupport.Headline("") == "", "headline is the first line");

        Check(PlanCardSupport.Headline("# " + new string('가', 200)).Length == 80, "the headline is bounded in bytes, as on the Mac");
        Check(!PlanCardSupport.FeedbackTooLong("") && !PlanCardSupport.FeedbackTooLong(" \n ") && !PlanCardSupport.FeedbackTooLong("짧은 요청")
            && PlanCardSupport.FeedbackTooLong(new string('가', ClaudePlanMode.MaximumFeedbackBytes / 3 + 1)), "too long only once there are too many words");
        Check(PlanCardSupport.ShowsHistoryStrip(false) && !PlanCardSupport.ShowsHistoryStrip(true), "blocks in the diagram, the strip elsewhere");

        IReadOnlyList<MightyGraphRun> graph = [new MightyGraphRun { Id = "g1", SourceRunID = "g1", Status = "completed" }];
        var record = new PlanRecord("p1", "pane", "# Plan", "2027-01-15T08:00:00.000Z", "2027-01-15T08:01:00.000Z", PlanOutcome.ApprovedAuto);
        Check(PlanCardSupport.DiagramRecords([record], graph).Count == 0, "a record without its graph run has no block");
        Check(PlanCardSupport.DiagramRecords([record with { GraphRunId = "g1" }], graph).SequenceEqual([("g1", "p1")]), "a record attaches beside its own request");
        Check(ClaudePlanMode.NormalizedHistory([record with { GraphRunId = "bad id" }])![0].GraphRunId is null, "a damaged graph id is dropped");

        var running = new BackgroundTask("a", "agent", "조사", "2027-01-15T08:00:00.000Z");
        var done = new BackgroundTask("b", "shell", "빌드", "2027-01-15T08:00:00.000Z", "completed");
        Check(PlanCardSupport.BackgroundStatus(null) is null && PlanCardSupport.BackgroundStatus(new([running], false)) is null && PlanCardSupport.BackgroundStatus(new([done], true)) is null, "plain status otherwise");
        Check(PlanCardSupport.BackgroundStatus(new([running, done, running with { Id = "c" }], true)) == Locale.Get("plan.background.status", new Dictionary<string, string> { ["count"] = "2" }), "turn done with two running");
        return Task.CompletedTask;
    }

    internal static Task LayoutPlacesThePlanCard()
    {
        var running = Run("g2"); running.Agents.Add(new MightyGraphAgent { Id = "a1", Title = "조사", Status = "running" });
        IReadOnlyList<MightyGraphRun> runs = [Run("g1", "completed"), running];
        var plain = MightyGraphLayout.Make(runs, "", true, new HashSet<string>());
        var layout = MightyGraphViewModel.CanvasLayout(runs, "", true, new HashSet<string>(), planRunID: "g2");
        var planID = MightyGraphBlockSize.NodeId("g2", MightyGraphLayout.PlanSuffix);
        var plan = layout.Nodes.Single(n => n.Id == planID);
        Check(plan.Kind == "plan" && plan.Frame.W == MightyGraphLayout.PlanWidth && plan.Frame.H == MightyGraphLayout.PlanHeight, "the plan card's size");
        var agent = layout.Nodes.Single(n => n.Kind == "agent");
        Check(Math.Abs(plan.Frame.Y - (agent.Frame.MaxY + 32)) < 0.01 && layout.Edges.Any(e => e.Source == agent.Id && e.Target == planID && e.Joins), "below the last block, joined to it");
        Check(Math.Abs(plan.Frame.X + plan.Frame.W / 2 - MightyGraphCamera.CentreX) < 0.5, "on the diagram's centreline");
        foreach (var node in plain.Nodes) Check(layout.Nodes.Single(n => n.Id == node.Id).Frame == node.Frame, "nothing above it moves: " + node.Id);
        var blocks = MightyGraphBlockModel.Blocks(layout, runs, "", "Claude", false);
        Check(blocks.Single(b => b.Id == planID) is { Kind: "plan", Status: "waiting" } block && block.Title == Locale.Get("plan.card.title"), "the plan block");
        Check(!MightyGraphLayout.Make([Run("g1", "completed")], "", false, new HashSet<string>(), planRunID: "g1").Nodes.Any(n => n.Kind == "plan"), "a finished run shows its result instead");
        var withRecords = MightyGraphViewModel.CanvasLayout(runs, "", true, new HashSet<string>(), planRunID: "g2", planRecords: [("g1", "p1"), ("missing", "p2")]);
        var recordNode = withRecords.Nodes.Single(n => n.Kind == "planRecord");
        var firstRequest = withRecords.Nodes.Single(n => n.Id == MightyGraphBlockSize.NodeId("g1", "request"));
        Check(recordNode.Frame.Y == firstRequest.Frame.Y && recordNode.Frame.X > firstRequest.Frame.MaxX && MightyGraphCamera.IsAuxiliary(recordNode.Id), "an answered plan hangs beside its request");
        Check(MightyGraphBlockModel.Blocks(withRecords, runs, "", "Claude", false).Single(b => b.Kind == "planRecord").RecordId == "p1", "the block knows its record");

        // An answered plan takes a dragged size, folded or opened; the pending plan the pane's one plan size
        // (macOS answeredPlanBlocksTakeTheirSavedSizeAndThePendingPlanItsOwn).
        var recordID = MightyGraphBlockSize.NodeId("g1", MightyGraphLayout.PlanRecordSuffix + "p1");
        MightyGraphLayout Sized(Dictionary<string, GraphBlockSize> sizes, HashSet<string> expanded, GraphBlockSize? planSize = null) =>
            MightyGraphViewModel.CanvasLayout(runs, "", true, expanded, blockSizes: sizes, planRunID: "g2", planRecords: [("g1", "p1")], planSize: planSize);
        var sizes = new Dictionary<string, GraphBlockSize> { [planID] = new(820, 610), [recordID] = new(470, 260) };
        var unread = Sized(sizes, []).Nodes.Single(n => n.Id == planID).Frame;
        Check(unread.W == MightyGraphLayout.PlanWidth && unread.H == MightyGraphLayout.PlanHeight, "a per-block size under the plan's key is not read");
        var sizedPlan = Sized(sizes, [], new(820, 610)).Nodes.Single(n => n.Id == planID).Frame;
        Check(sizedPlan.W == 820 && sizedPlan.H == 610, "the plan block takes the pane's plan size");
        foreach (var expanded in new[] { new HashSet<string>(), new HashSet<string> { recordID } })
        {
            var sizedRecord = Sized(sizes, expanded).Nodes.Single(n => n.Id == recordID).Frame;
            Check(sizedRecord.W == 470 && sizedRecord.H == 260, "an answered plan takes its saved size, folded or opened");
        }
        var opened = Sized([], [recordID]).Nodes.Single(n => n.Id == recordID).Frame;
        Check(opened.W == MightyGraphLayout.PlanRecordWidth && opened.H == MightyGraphLayout.PlanRecordHeight(true), "without one it opens to its own height");
        // Its answers are in the composer, so the plan block may be as small as any block.
        Check(MightyGraphLayout.PlanMinimumWidth == MightyGraphBlockSize.MinimumWidth && MightyGraphLayout.PlanMinimumHeight == MightyGraphBlockSize.MinimumHeight, "the plan's least is any block's");
        var least = Sized([], [], new(10, 10)).Nodes.Single(n => n.Id == planID).Frame;
        Check(least.W == MightyGraphLayout.PlanMinimumWidth && least.H == MightyGraphLayout.PlanMinimumHeight, "a tiny plan size is drawn at the blocks' least");
        var folded = MightyGraphLayout.PlanRecordHeight(false);
        Check(MightyGraphLayout.MinimumBlockSize("plan") == (MightyGraphBlockSize.MinimumWidth, MightyGraphBlockSize.MinimumHeight)
              && MightyGraphLayout.MinimumBlockSize("planRecord") == (MightyGraphBlockSize.MinimumWidth, folded), "the least a drag makes each plan block");
        // An answered plan keeps its folded height (macOS anAnsweredPlanKeepsItsFoldedHeight): its first drag does not jump to 140.
        var requestID = MightyGraphBlockSize.NodeId("g1", "request");
        Check(MightyGraphBlockSize.MinimumHeightFor(recordID) == folded && MightyGraphBlockSize.MinimumHeightFor(requestID) == MightyGraphBlockSize.MinimumHeight
              && MightyGraphBlockSize.MinimumHeightFor(MightyGraphBlockSize.NodeId("x:plan-record:y", "request")) == MightyGraphBlockSize.MinimumHeight, "only an answered plan's key takes its folded height");
        Check(new GraphBlockSize(320, 40).NormalizedFor(recordID) == new GraphBlockSize(320, folded) && new GraphBlockSize(320, folded).NormalizedFor(requestID) == new GraphBlockSize(320, MightyGraphBlockSize.MinimumHeight),
            "a size is clamped to its own block's least");
        var shortRecord = Sized(new() { [recordID] = new(360, folded) }, []).Nodes.Single(n => n.Id == recordID).Frame;
        Check(shortRecord.W == 360 && shortRecord.H == folded, "an answered plan dragged to its folded height is drawn there");
        // The Windows normalizer keeps every well-formed key; what it must not do is lift a folded answered plan to 140.
        var kept = GraphBlockPreferences.Normalize(new Dictionary<string, GraphBlockSize> { [recordID] = new(360, folded), [requestID] = new(360, folded) });
        Check(kept is not null && kept[recordID] == new GraphBlockSize(360, folded) && kept[requestID] == new GraphBlockSize(360, MightyGraphBlockSize.MinimumHeight),
            "the saved-size normalizer keeps an answered plan at its folded height and other blocks at their least");
        Check(GraphBlockPreferences.Set(new RunSession(), recordID, new(360, folded)).GraphBlockSizes?[recordID] == new GraphBlockSize(360, folded), "a dragged answered plan is saved at its folded height");
        return Task.CompletedTask;
    }

    /// <summary>The plan block fits the pane as the newest result does (macOS PlanCardTests / MightyGraphResultFitTests).</summary>
    internal static Task PlanFitsThePaneLikeTheResult()
    {
        IReadOnlyList<MightyGraphRun> runs = [Run("g1", "completed"), Run("g2")];
        var planID = MightyGraphBlockSize.NodeId("g2", MightyGraphLayout.PlanSuffix);
        var resultID = MightyGraphBlockSize.NodeId("g1", "result");
        MightyGraphLayout Make((double W, double H)? viewport, double? zoom = 1, GraphBlockSize? plan = null, GraphBlockSize? result = null) =>
            MightyGraphViewModel.CanvasLayout(runs, "", true, new HashSet<string>(), viewport: viewport, zoom: zoom, sharedResultSize: result, resultContentHeight: 5_000, planRunID: "g2", planSize: plan);
        (double W, double H) Frame(MightyGraphLayout layout, string node) => layout.Nodes.Single(n => n.Id == node).Frame is var f ? (f.W, f.H) : default;

        var fitted = Make((1_200, 800));
        Check(Frame(fitted, planID) == (1_152, 752) && Frame(fitted, planID) == MightyGraphLayout.ResultFitSize((1_200, 800), false), "nothing saved: the window fit");
        Check(fitted.PlanWindowFit == (1_152, 752) && fitted.PlanLimit == (1_152, 752), "the layout carries the plan's limit and window fit for a drag");
        var none = MightyGraphViewModel.CanvasLayout(runs, "", true, new HashSet<string>(), viewport: (1_200, 800), zoom: 1);
        Check(none.PlanWindowFit is null && none.PlanLimit is null, "no plan block, no plan limits");

        var saved = new GraphBlockSize(900, 700);
        Check(Frame(Make((1_400, 1_000), plan: saved), planID) == (900, 700), "the saved size in a large pane");
        Check(Frame(Make((700, 500), plan: saved), planID) == (652, 452), "a smaller pane draws it within the pane");
        Check(Frame(Make((1_000, 800), 1.5, saved), planID) == MightyGraphLayout.ResultViewportLimit((1_000, 800), 1.5, false), "zoomed in, within what the pane shows");
        Check(Frame(Make((1_400, 1_000), plan: saved), planID) == (900, 700), "it grows back to the saved size");
        Check(Frame(Make(null, null), planID) == (MightyGraphLayout.PlanWidth, MightyGraphLayout.PlanHeight), "no viewport: the document size");
        Check(Frame(Make(null, null, new(700, 400)), planID) == (700, 400), "no viewport: the saved size");

        // The plan and the result keep their own sizes.
        Check(Frame(Make((1_200, 800), result: new(700, 400)), planID) == (1_152, 752), "a result size leaves the plan at the window fit");
        Check(Frame(Make((1_200, 800), plan: new(700, 400)), resultID) == (1_152, 752), "a plan size leaves the result at the window fit");

        // A drag by the result's rules.
        var fit = MightyGraphLayout.PlanSize(null, (1_000, 700), 1);
        Check(MightyGraphLayout.ResultDrag((600, 420), true, true, MightyGraphLayout.ResizePhase.Finished, null, fit.Limit, fit.WindowFit).Save == new GraphBlockSize(600, 420), "released inside the pane: saved as released");
        Check(MightyGraphLayout.ResultDrag((2_000, 420), true, true, MightyGraphLayout.ResizePhase.Live, null, fit.Limit, fit.WindowFit).Live == new GraphBlockSize(952, 420), "live: kept within the pane");
        return Task.CompletedTask;
    }

    /// <summary>
    /// While the diagram draws the plan the composer area shows only its answers, else the whole card docks
    /// (macOS theComposerShowsOnlyTheAnswersWhileTheDiagramDrawsThePlan); the plan size round-trips under the Mac field.
    /// </summary>
    internal static async Task ComposerShowsTheAnswersAndThePlanSizeIsKept()
    {
        IReadOnlyList<MightyGraphRun> runs = [Run("g1", "completed"), Run("g2")];
        Check(PlanCardSupport.ComposerShowsPlanActions(Plan(), true, runs), "the diagram draws it: answers only");
        Check(!PlanCardSupport.ComposerShowsPlanActions(Plan(), false, runs), "outside the diagram: the whole card");
        Check(!PlanCardSupport.ComposerShowsPlanActions(Plan(), true, [Run("g1", "completed")]) && !PlanCardSupport.ComposerShowsPlanActions(Bash, true, runs)
            && !PlanCardSupport.ComposerShowsPlanActions(null, true, runs) && !PlanCardSupport.ComposerShowsPlanActions(Plan(state: "allowed"), true, runs), "only a pending plan the diagram draws");

        var workspace = new Workspace { Path = Path.GetTempPath() };
        var session = new RunSession { WorkspaceId = workspace.Id, Provider = "claude", AgentViewMode = "mighty", GraphPlanSize = new(5_000, 610) };
        var shell = new RunSession { WorkspaceId = workspace.Id, Kind = "shell", GraphPlanSize = new(900, 700) };
        var plain = new RunSession { WorkspaceId = workspace.Id, Provider = "claude" };
        var encoded = System.Text.Json.JsonSerializer.Serialize(new AppSnapshot { Version = 1, Workspaces = [workspace], Sessions = [session, shell, plain] }, Wire.Json);
        using (var document = System.Text.Json.JsonDocument.Parse(encoded))
        {
            var saved = document.RootElement.GetProperty("sessions")[0].GetProperty("graphPlanSize");
            Check(saved.GetProperty("width").GetDouble() == 5_000 && saved.GetProperty("height").GetDouble() == 610, "graphPlanSize is {width,height}");
            Check(!document.RootElement.GetProperty("sessions")[2].TryGetProperty("graphPlanSize", out _), "no saved plan size writes nothing");
        }
        var directory = Verification.Temp();
        try
        {
            await StateStore.AtomicWriteAsync(Path.Combine(directory, "workspace-state.json"), System.Text.Encoding.UTF8.GetBytes(encoded));
            var loaded = await new StateStore(directory).LoadAsync();
            Check(loaded.Sessions.Single(s => s.Id == session.Id).GraphPlanSize == new GraphBlockSize(1_400, 610), "the saved plan size survives clamped");
            Check(loaded.Sessions.Single(s => s.Id == shell.Id).GraphPlanSize is null, "a shell pane keeps no plan size");
            Check(loaded.Sessions.Single(s => s.Id == plain.Id).GraphPlanSize is null, "no saved plan size loads as none");
        }
        finally { Directory.Delete(directory, true); }
    }
}
