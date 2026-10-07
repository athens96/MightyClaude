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

        // Both plan blocks take a dragged size (macOS planBlocksTakeTheirSavedSize…), the record whether folded or opened.
        var recordID = MightyGraphBlockSize.NodeId("g1", MightyGraphLayout.PlanRecordSuffix + "p1");
        MightyGraphLayout Sized(Dictionary<string, GraphBlockSize> sizes, HashSet<string> expanded) =>
            MightyGraphViewModel.CanvasLayout(runs, "", true, expanded, blockSizes: sizes, planRunID: "g2", planRecords: [("g1", "p1")]);
        var sizes = new Dictionary<string, GraphBlockSize> { [planID] = new(820, 610), [recordID] = new(470, 260) };
        var sizedPlan = Sized(sizes, []).Nodes.Single(n => n.Id == planID).Frame;
        Check(sizedPlan.W == 820 && sizedPlan.H == 610, "the plan card takes its saved size");
        foreach (var expanded in new[] { new HashSet<string>(), new HashSet<string> { recordID } })
        {
            var sizedRecord = Sized(sizes, expanded).Nodes.Single(n => n.Id == recordID).Frame;
            Check(sizedRecord.W == 470 && sizedRecord.H == 260, "an answered plan takes its saved size, folded or opened");
        }
        var opened = Sized([], [recordID]).Nodes.Single(n => n.Id == recordID).Frame;
        Check(opened.W == MightyGraphLayout.PlanRecordWidth && opened.H == MightyGraphLayout.PlanRecordHeight(true), "without one it opens to its own height");
        var least = Sized(new() { [planID] = new(300, 140) }, []).Nodes.Single(n => n.Id == planID).Frame;
        Check(least.W == MightyGraphLayout.PlanMinimumWidth && least.H == MightyGraphLayout.PlanMinimumHeight, "the plan card is never drawn below its answers");
        Check(MightyGraphLayout.MinimumBlockSize("plan") == (MightyGraphLayout.PlanMinimumWidth, MightyGraphLayout.PlanMinimumHeight)
              && MightyGraphLayout.MinimumBlockSize("planRecord") == (MightyGraphBlockSize.MinimumWidth, MightyGraphBlockSize.MinimumHeight), "the least a drag makes each plan block");
        var kept = GraphBlockPreferences.Normalize(sizes);
        Check(kept is not null && kept[planID] == sizes[planID] && kept[recordID] == sizes[recordID], "saved plan sizes survive the normalizer");
        return Task.CompletedTask;
    }
}
